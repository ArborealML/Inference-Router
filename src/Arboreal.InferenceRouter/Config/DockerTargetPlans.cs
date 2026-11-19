using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed record DockerTargetLaunch(string Model, string Profile, ImmutableArray<string> Command,
    ImmutableArray<string> Env, ImmutableArray<DockerBind> Binds);

/// <summary>Immutable Docker create inputs. They describe launch intent, not a verified serving instance.</summary>
internal sealed partial class DockerTargetPlans
{
    private sealed record ModelPlans(string Canonical, string? DefaultProfile,
        ImmutableDictionary<string, DockerTargetLaunch> Profiles);

    private readonly string _engine;
    private readonly ImmutableDictionary<string, ModelPlans> _models;

    private DockerTargetPlans(string engine, ImmutableDictionary<string, ModelPlans> models)
    {
        _engine = engine;
        _models = models;
    }

    internal static bool IsDeclared(EngineDto engine) =>
        engine.Catalog?.Values.Any(model => model.Serving?.Any(tier => tier?.Docker is not null) == true) == true;

    internal DockerTargetLaunch? Resolve(LifecycleTarget target)
    {
        if (!string.Equals(_engine, target.EngineName, StringComparison.Ordinal) ||
            !_models.TryGetValue(target.Model, out var model))
            throw new RouterConfigException("Docker target model is not in the captured catalog");
        if (model.DefaultProfile is null) return null;
        var profile = target.Profile?.Name ?? model.DefaultProfile;
        return model.Profiles.TryGetValue(profile, out var launch) ? launch
            : throw new RouterConfigException("Docker target profile is not in the captured catalog");
    }

    internal static DockerTargetPlans Parse(string engine, Dictionary<string, ModelDto> catalog, DockerDto docker,
        DockerLaunchResources? common)
    {
        var models = ImmutableDictionary.CreateBuilder<string, ModelPlans>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, model) in catalog)
        {
            var path = $"engine.{engine}.catalog.{canonical}";
            var tiers = model.Serving;
            var plans = tiers is not null && tiers.Any(tier => tier.Docker is not null)
                ? ParseModel(engine, canonical, model, tiers, docker, common, path)
                : new ModelPlans(canonical, null, ImmutableDictionary<string, DockerTargetLaunch>.Empty);
            AddModelSpelling(canonical);
            foreach (var alias in model.Aliases ?? [])
                AddModelSpelling(alias);

            void AddModelSpelling(string spelling)
            {
                if (string.IsNullOrWhiteSpace(spelling))
                    throw new RouterConfigException($"{path} contains an empty model name or alias");
                if (models.TryGetValue(spelling, out var owner) &&
                    !string.Equals(owner.Canonical, canonical, StringComparison.Ordinal))
                    throw new RouterConfigException($"{path} has a model name or alias shared with another model");
                models[spelling] = plans;
            }
        }
        return new DockerTargetPlans(engine, models.ToImmutable());
    }

    private static ModelPlans ParseModel(string engine, string canonical, ModelDto model, List<ServingDto> tiers,
        DockerDto docker, DockerLaunchResources? common, string path)
    {
        if (model.Profile is { } legacy && model.DefaultProfile is { } configured &&
            !string.Equals(legacy, configured, StringComparison.Ordinal))
            throw new RouterConfigException($"{path}.profile conflicts with default_profile");
        var defaultProfile = model.DefaultProfile ?? model.Profile ?? (tiers.Count == 1 ? tiers[0].Name : null)
            ?? throw new RouterConfigException($"{path}.default_profile is required for multiple Docker target tiers");
        var plans = ImmutableDictionary.CreateBuilder<string, DockerTargetLaunch>(StringComparer.Ordinal);
        for (var i = 0; i < tiers.Count; i++)
        {
            var tier = tiers[i];
            var tierPath = $"{path}.serving[{i}]";
            if (string.IsNullOrWhiteSpace(tier.Name))
                throw new RouterConfigException($"{tierPath}.name must contain a serving profile name");
            if (plans.ContainsKey(tier.Name))
                throw new RouterConfigException($"{tierPath}.name duplicates a serving profile");
            plans.Add(tier.Name, Compose(engine, canonical, tier.Name, docker, common, tier.Docker, tierPath));
        }
        return new ModelPlans(canonical, defaultProfile, plans.ToImmutable());
    }

    private static DockerTargetLaunch Compose(string engine, string model, string profile, DockerDto docker,
        DockerLaunchResources? common, DockerTargetDto? tier, string path)
    {
        if (tier?.Unknown is { Count: > 0 } unknown)
            throw new RouterConfigException($"{path}.docker.{unknown.Keys.First()} is not a target setting");
        if (tier is not null && tier.Env is not { Length: > 0 } && tier.BindMounts is not { Length: > 0 })
            throw new RouterConfigException($"{path}.docker must contain environment assignments or bind mounts");

        var command = ImmutableArray.CreateBuilder<string>();
        foreach (var value in docker.Command ?? [])
            command.Add(ExpandTargetTemplate(value ?? throw new RouterConfigException("Docker command entries must be strings"),
                model, profile, engine));
        var env = ImmutableArray.CreateBuilder<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in docker.Env ?? [])
            AddEnv(value is null ? null : ExpandTargetTemplate(value, model, profile, engine));
        foreach (var value in tier?.Env ?? [])
            AddEnv(value);

        var bindPath = $"{path}.docker.bind_mounts";
        var additions = DockerLaunchResources.ParseBinds(tier?.BindMounts, bindPath);
        var binds = DockerLaunchResources.CombineTargetBinds(common?.BindMounts ?? [], additions, bindPath);
        return new DockerTargetLaunch(model, profile, command.ToImmutable(), env.ToImmutable(), binds);

        void AddEnv(string? value)
        {
            var separator = value?.IndexOf('=', StringComparison.Ordinal) ?? -1;
            if (value is null || separator <= 0 || value.Any(char.IsControl) ||
                !IsEnvName(value.AsSpan(0, separator)))
                throw new RouterConfigException($"{path}.docker.env requires control-free KEY=value assignments");
            if (!names.Add(value[..separator]))
                throw new RouterConfigException($"{path}.docker.env repeats an effective environment name");
            env.Add(value);
        }
    }

    private static bool IsEnvName(ReadOnlySpan<char> name)
    {
        if (name[0] != '_' && !char.IsAsciiLetter(name[0])) return false;
        foreach (var character in name[1..])
            if (character != '_' && !char.IsAsciiLetterOrDigit(character)) return false;
        return true;
    }

    internal static string Expand(string value, string model, string profile, string engine) =>
        value.Replace("{{model}}", model, StringComparison.Ordinal)
             .Replace("{{profile}}", profile, StringComparison.Ordinal)
             .Replace("{{endpoint}}", engine, StringComparison.Ordinal);

    // Expand only placeholders in the original common template. An identifier may itself contain
    // placeholder syntax, and its exact spelling is also used in the resolved create labels.
    private static string ExpandTargetTemplate(string value, string model, string profile, string engine) =>
        TargetPlaceholders().Replace(value, match => match.Value switch
        {
            "{{model}}" => model,
            "{{profile}}" => profile,
            _ => engine,
        });

    [GeneratedRegex(@"\{\{(?:model|profile|endpoint)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TargetPlaceholders();

    // Walk each occurrence before STJ can discard a shadowed opt-in. Only ancestors of an
    // actual tier block receive the new strictness. JSON structural names use schema casing.
    internal static void CheckJson(JsonElement root)
    {
        foreach (var engines in Members(root, "engine"))
        foreach (var engine in Properties(engines.Value))
        foreach (var catalog in Members(engine.Value, "catalog"))
        foreach (var model in Properties(catalog.Value))
        foreach (var serving in Members(model.Value, "serving"))
        {
            var enginePath = $"engine.{engine.Name}";
            var modelPath = $"{enginePath}.catalog.{model.Name}";
            if (serving.Value.ValueKind == JsonValueKind.Object && Members(serving.Value, "docker").Any())
                throw new RouterConfigException($"{modelPath}.serving must be an array of serving tiers");
            if (serving.Value.ValueKind != JsonValueKind.Array) continue;
            var index = 0;
            foreach (var tier in serving.Value.EnumerateArray())
            {
                if (Members(tier, "docker").Any())
                {
                    CheckMember(root, "engine", "engine");
                    CheckMember(engines.Value, engine.Name, enginePath, exactKey: true);
                    CheckMember(engine.Value, "catalog", $"{enginePath}.catalog");
                    CheckAuthority(engine.Value, catalog.Value, enginePath);
                    CheckMember(catalog.Value, model.Name, modelPath, exactKey: true);
                    CheckMember(model.Value, "serving", $"{modelPath}.serving");
                    CheckMember(model.Value, "aliases", $"{modelPath}.aliases");
                    CheckMember(model.Value, "profile", $"{modelPath}.profile");
                    CheckMember(model.Value, "default_profile", $"{modelPath}.default_profile");
                    var tierPath = $"{modelPath}.serving[{index}]";
                    CheckMember(tier, "docker", $"{tierPath}.docker");
                    CheckMember(tier, "name", $"{tierPath}.name");
                    CheckMember(tier, "max_context", $"{tierPath}.max_context");
                    CheckMember(tier, "max_slots", $"{tierPath}.max_slots");
                    DockerLaunchResources.CheckJsonTree(tier, tierPath);
                    var target = tier.GetProperty("docker");
                    if (target.ValueKind != JsonValueKind.Object)
                        throw new RouterConfigException($"{tierPath}.docker must be an object");
                    CheckCommonDocker(engine.Value, enginePath);
                }
                index++;
            }
        }
    }

    private static void CheckAuthority(JsonElement engine, JsonElement catalog, string enginePath)
    {
        CheckMember(engine, "lifecycle_owner", $"{enginePath}.lifecycle_owner");
        CheckMember(engine, "remote", $"{enginePath}.remote");
        CheckMember(engine, "lifecycle", $"{enginePath}.lifecycle");
        // A delegated select on any model conflicts with native ownership of this engine.
        foreach (var model in Properties(catalog))
        {
            if (!Members(model.Value, "select").Any()) continue;
            var path = $"{enginePath}.catalog.{model.Name}";
            CheckMember(catalog, model.Name, path, exactKey: true);
            CheckMember(model.Value, "select", $"{path}.select");
        }
    }

    private static void CheckCommonDocker(JsonElement engine, string path)
    {
        CheckMember(engine, "docker", $"{path}.docker");
        if (engine.TryGetProperty("docker", out var docker))
        {
            if (docker.ValueKind != JsonValueKind.Object)
                throw new RouterConfigException($"{path}.docker must be an object");
            foreach (var field in ConfigJsonContext.Default.DockerDto.Properties)
                CheckMember(docker, field.Name, $"{path}.docker.{field.Name}");
            DockerLaunchResources.CheckJsonTree(docker, $"{path}.docker");
        }
    }

    private static IEnumerable<JsonProperty> Properties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in value.EnumerateObject())
            yield return property;
    }

    private static IEnumerable<JsonProperty> Members(JsonElement value, string name, bool exactKey = false)
    {
        foreach (var property in Properties(value))
            if (exactKey ? property.NameEquals(name) : string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                yield return property;
    }

    private static void CheckMember(JsonElement value, string name, string path, bool exactKey = false)
    {
        var seen = false;
        foreach (var property in Members(value, name, exactKey))
        {
            if (seen || !property.NameEquals(name))
                throw new RouterConfigException($"{path} is duplicated or uses noncanonical JSON casing");
            seen = true;
        }
    }
}
