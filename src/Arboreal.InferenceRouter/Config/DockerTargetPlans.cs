using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed record DockerTargetLaunch(string Model, string Profile, ImmutableArray<string> Command,
    ImmutableArray<string> Env, ImmutableArray<DockerBind> Binds, long? MemoryBytes, long? MemorySwapBytes,
    DockerTargetHealthcheck? Healthcheck, DockerGpu? Gpu, long? ShmSizeBytes);

internal sealed record DockerTargetHealthcheck(string Command, long? IntervalNs, long? StartPeriodNs,
    long? TimeoutNs, int? Retries);

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

    internal DockerTargetLaunch? Resolve(LifecycleTarget target) =>
        Resolve(target.EngineName, target.Model, target.Profile?.Name);

    internal DockerTargetLaunch? Resolve(string engine, string requestedModel, string? requestedTier)
    {
        if (!string.Equals(_engine, engine, StringComparison.Ordinal) ||
            !_models.TryGetValue(requestedModel, out var model))
            throw new RouterConfigException("Docker target model is not in the captured catalog");
        if (model.DefaultProfile is null) return null;
        var profile = requestedTier ?? model.DefaultProfile;
        return model.Profiles.TryGetValue(profile, out var launch) ? launch
            : throw new RouterConfigException("Docker target profile is not in the captured catalog");
    }

    internal static void ValidateProfileAliases(string[] aliases, DockerTargetDto? target, string path)
    {
        if (target is null || !HasLaunchSettings(target))
            throw new RouterConfigException($"{path}.aliases requires a nonempty Docker target on this serving tier");
        if (aliases.Length == 0)
            throw new RouterConfigException($"{path}.aliases must contain at least one profile alias");
        for (var i = 0; i < aliases.Length; i++)
        {
            var alias = aliases[i];
            if (string.IsNullOrWhiteSpace(alias) || alias.AsSpan().Trim().Length != alias.Length || alias.Any(char.IsControl))
                throw new RouterConfigException($"{path}.aliases[{i}] must be a nonblank control-free profile name without edge whitespace");
        }
    }

    internal static DockerTargetPlans Parse(string engine, Dictionary<string, ModelDto> catalog, DockerDto docker,
        DockerLaunchResources? common)
    {
        if (docker.Command is { } command)
            ValidateCommandArguments(command, $"engine.{engine}.docker.command");
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
        if (!plans.ContainsKey(defaultProfile))
            throw new RouterConfigException($"{path}.default_profile must name a canonical serving tier");
        for (var i = 0; i < tiers.Count; i++)
        {
            var tier = tiers[i];
            if (tier.Aliases is not { } aliases) continue;
            var launch = plans[tier.Name!];
            for (var aliasIndex = 0; aliasIndex < aliases.Length; aliasIndex++)
                if (!plans.TryAdd(aliases[aliasIndex], launch))
                    throw new RouterConfigException($"{path}.serving[{i}].aliases[{aliasIndex}] duplicates a canonical tier or alias");
        }
        return new ModelPlans(canonical, defaultProfile, plans.ToImmutable());
    }

    private static bool HasLaunchSettings(DockerTargetDto target) =>
        target.Gpu is not null || target.ShmSizeBytes is not null || target.Command is not null ||
        target.MemoryBytes is not null || target.MemorySwapBytes is not null ||
        target.Env is { Length: > 0 } || target.BindMounts is { Length: > 0 } || target.Healthcheck is not null;

    private static DockerTargetLaunch Compose(string engine, string model, string profile, DockerDto docker,
        DockerLaunchResources? common, DockerTargetDto? tier, string path)
    {
        if (tier?.Unknown is { Count: > 0 } unknown)
            throw new RouterConfigException($"{path}.docker.{unknown.Keys.First()} is not a target setting");
        if (tier is not null && !HasLaunchSettings(tier))
            throw new RouterConfigException($"{path}.docker must contain a command, environment assignments, bind mounts, memory settings, a health check, a GPU request or shared memory");

        ImmutableArray<string> command = tier?.Command is { } literal
            ? FreezeTierCommand(literal, $"{path}.docker.command")
            : [.. (docker.Command ?? []).Select(value => ExpandTargetTemplate(value, model, profile, engine))];
        var env = ImmutableArray.CreateBuilder<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in docker.Env ?? [])
            AddEnv(value is null ? null : ExpandTargetTemplate(value, model, profile, engine));
        foreach (var value in tier?.Env ?? [])
            AddEnv(value);

        var bindPath = $"{path}.docker.bind_mounts";
        var additions = DockerLaunchResources.ParseBinds(tier?.BindMounts, bindPath, isTier: true);
        var binds = DockerLaunchResources.CombineTargetBinds(common?.BindMounts ?? [], additions, bindPath);
        var (memory, memorySwap) = ResolveMemory(tier, common, $"{path}.docker");
        var healthcheck = tier?.Healthcheck is { } probe ? ParseHealthcheck(probe, $"{path}.docker.healthcheck") : null;
        var gpu = tier?.Gpu is { } device ? ParseTierGpu(device, common, $"{path}.docker.gpu") : common?.Gpu;
        var shm = tier?.ShmSizeBytes ?? common?.ShmSizeBytes;
        DockerLaunchResources.ValidateSharedMemory(shm, $"{path}.docker.shm_size_bytes");
        return new DockerTargetLaunch(model, profile, command, env.ToImmutable(), binds, memory, memorySwap,
            healthcheck, gpu, shm);

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

    private static ImmutableArray<string> FreezeTierCommand(string[] command, string path)
    {
        if (command.Length == 0)
            throw new RouterConfigException($"{path} must contain at least one argument");
        ValidateCommandArguments(command, path);
        return [.. command];
    }

    private static void ValidateCommandArguments(string[] command, string path)
    {
        for (var i = 0; i < command.Length; i++)
            if (command[i] is not { } argument || argument.Contains('\0', StringComparison.Ordinal))
                throw new RouterConfigException($"{path}[{i}] must be a string without NUL");
    }

    private static DockerTargetHealthcheck ParseHealthcheck(DockerHealthcheckDto probe, string path)
    {
        if (probe.Unknown is { Count: > 0 } unknown)
            throw new RouterConfigException($"{path}.{unknown.Keys.First()} is not a health check setting");
        var command = probe.Command;
        if (string.IsNullOrWhiteSpace(command) || command.Any(char.IsControl))
            throw new RouterConfigException($"{path}.command must be a nonblank control-free string");
        if (probe.Retries is <= 0)
            throw new RouterConfigException($"{path}.retries must be a positive Int32");
        return new DockerTargetHealthcheck(command,
            Nanoseconds(probe.IntervalMs, $"{path}.interval_ms"),
            Nanoseconds(probe.StartPeriodMs, $"{path}.start_period_ms"),
            Nanoseconds(probe.TimeoutMs, $"{path}.timeout_ms"), probe.Retries);
    }

    private static long? Nanoseconds(long? milliseconds, string path)
    {
        const long nanosecondsPerMillisecond = 1_000_000;
        if (milliseconds is not { } value) return null;
        if (value <= 0 || value > long.MaxValue / nanosecondsPerMillisecond)
            throw new RouterConfigException($"{path} must be a positive millisecond duration that fits in Int64 nanoseconds");
        return checked(value * nanosecondsPerMillisecond);
    }

    private static (long? Memory, long? MemorySwap) ResolveMemory(DockerTargetDto? tier,
        DockerLaunchResources? common, string path)
    {
        var memory = tier?.MemoryBytes;
        var memorySwap = tier?.MemorySwapBytes;
        if (memory is null && memorySwap is null)
            return (common?.MemoryBytes, common?.MemorySwapBytes);
        if (memorySwap is null && common?.MemorySwapBytes is not null)
            throw new RouterConfigException($"{path}.memory_swap_bytes is required when overriding an engine memory and swap pair");
        DockerLaunchResources.ValidateMemory(memory, memorySwap, path);
        return (memory, memorySwap);
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

    [GeneratedRegex(@"\{\{(?:model|profile|endpoint)\}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1_000)]
    private static partial Regex TargetPlaceholders();

    // Walk each occurrence before STJ can discard a shadowed opt-in. JSON structural names
    // use schema casing for engines that declare a tier or unqualified launch options.
    internal static void CheckJson(JsonElement root)
    {
        CheckLaunchOptionsJson(root);
        foreach (var engines in Members(root, "engine"))
        foreach (var engine in Properties(engines.Value))
        foreach (var catalog in Members(engine.Value, "catalog"))
        foreach (var model in Properties(catalog.Value))
        foreach (var serving in Members(model.Value, "serving"))
        {
            var enginePath = $"engine.{engine.Name}";
            var modelPath = $"{enginePath}.catalog.{model.Name}";
            RejectMisplacedContainerPolicyJson(serving.Value, $"{modelPath}.serving");
            RejectMisplacedTierResourceJson(serving.Value, $"{modelPath}.serving");
            if (serving.Value.ValueKind == JsonValueKind.Object &&
                (HasTierDeclaration(serving.Value) || Members(serving.Value, "command").Any()))
                throw new RouterConfigException($"{modelPath}.serving must be an array of serving tiers");
            if (serving.Value.ValueKind != JsonValueKind.Array) continue;
            var index = 0;
            foreach (var tier in serving.Value.EnumerateArray())
            {
                var tierPath = $"{modelPath}.serving[{index}]";
                RejectMisplacedContainerPolicyJson(tier, tierPath);
                RejectMisplacedTierResourceJson(tier, tierPath);
                foreach (var tierDocker in Members(tier, "docker"))
                    RejectMisplacedContainerPolicyJson(tierDocker.Value, $"{tierPath}.docker");
                if (Members(tier, "command").Any())
                    throw new RouterConfigException($"{tierPath}.command belongs inside serving-tier docker");
                if (Members(tier, "nested_under").Any())
                    throw new RouterConfigException($"{tierPath}.nested_under belongs on a serving-tier bind mount");
                if (HasTierDeclaration(tier))
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
                    CheckMember(tier, "docker", $"{tierPath}.docker");
                    CheckMember(tier, "name", $"{tierPath}.name");
                    CheckMember(tier, "max_context", $"{tierPath}.max_context");
                    CheckMember(tier, "max_slots", $"{tierPath}.max_slots");
                    CheckMember(tier, "aliases", $"{tierPath}.aliases");
                    if (tier.TryGetProperty("aliases", out var aliases) && aliases.ValueKind != JsonValueKind.Array)
                        throw new RouterConfigException($"{tierPath}.aliases must be an array of profile names");
                    if (!tier.TryGetProperty("docker", out var target) || target.ValueKind != JsonValueKind.Object)
                        throw new RouterConfigException($"{tierPath}.docker must be an object");
                    CheckCommandJson(target, $"{tierPath}.docker.command", requireArguments: true);
                    CheckHealthcheckJson(target, $"{tierPath}.docker.healthcheck");
                    CheckTierBindsJson(target, $"{tierPath}.docker.bind_mounts");
                    CheckTierGpuJson(target, $"{tierPath}.docker.gpu");
                    CheckSharedMemoryJson(target, $"{tierPath}.docker.shm_size_bytes");
                    DockerLaunchResources.CheckJsonTree(tier, tierPath);
                    CheckCommonDocker(engine.Value, enginePath);
                    if (engine.Value.TryGetProperty("docker", out var common))
                    {
                        CheckCommandJson(common, $"{enginePath}.docker.command", requireArguments: false);
                        if (target.TryGetProperty("gpu", out _) && common.TryGetProperty("resources", out var resources))
                            CheckCommonGpuJson(resources, $"{enginePath}.docker.resources");
                    }
                }
                index++;
            }
        }
    }

    private static void CheckCommandJson(JsonElement table, string path, bool requireArguments)
    {
        CheckMember(table, "command", path);
        if (!table.TryGetProperty("command", out var command)) return;
        if (command.ValueKind != JsonValueKind.Array || (requireArguments && command.GetArrayLength() == 0))
            throw new RouterConfigException($"{path} must be {(requireArguments ? "a nonempty array" : "an array")} of strings");
        var index = 0;
        foreach (var value in command.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } argument || argument.Contains('\0', StringComparison.Ordinal))
                throw new RouterConfigException($"{path}[{index}] must be a string without NUL");
            index++;
        }
    }

    private static bool HasTierDeclaration(JsonElement tier) =>
        Members(tier, "docker").Any() || Members(tier, "aliases").Any();

    private static void CheckLaunchOptionsJson(JsonElement root)
    {
        foreach (var engines in Members(root, "engine"))
        foreach (var engine in Properties(engines.Value))
        {
            var path = $"engine.{engine.Name}";
            RejectMisplacedContainerPolicyJson(engine.Value, path);
            RejectMisplacedTierResourceJson(engine.Value, path);
            foreach (var catalogOccurrence in Members(engine.Value, "catalog"))
            foreach (var model in Properties(catalogOccurrence.Value))
            {
                var modelPath = $"{path}.catalog.{model.Name}";
                RejectMisplacedContainerPolicyJson(model.Value, modelPath);
                RejectMisplacedTierResourceJson(model.Value, modelPath);
                foreach (var modelDocker in Members(model.Value, "docker"))
                {
                    RejectMisplacedContainerPolicyJson(modelDocker.Value, $"{modelPath}.docker");
                    RejectMisplacedTierResourceJson(modelDocker.Value, $"{modelPath}.docker");
                }
            }
            foreach (var docker in Members(engine.Value, "docker"))
            {
                var dockerPath = $"{path}.docker";
                RejectMisplacedTargetOptionsJson(docker.Value, dockerPath);
                RejectMisplacedTierResourceJson(docker.Value, dockerPath);
                foreach (var resources in Members(docker.Value, "resources"))
                {
                    var resourcePath = $"{dockerPath}.resources";
                    RejectMisplacedTargetOptionsJson(resources.Value, resourcePath);
                    RejectMisplacedContainerPolicyJson(resources.Value, resourcePath);
                    if (!Members(resources.Value, "shm_size_bytes").Any()) continue;
                    CheckMember(root, "engine", "engine");
                    CheckMember(engines.Value, engine.Name, path, exactKey: true);
                    CheckMember(engine.Value, "docker", dockerPath);
                    CheckMember(docker.Value, "resources", resourcePath);
                    CheckSharedMemoryJson(resources.Value, $"{resourcePath}.shm_size_bytes");
                }
                var hasPolicy = ContainerPolicyFields.Any(field => Members(docker.Value, field).Any());
                if (!hasPolicy && !Members(docker.Value, "entrypoint").Any() && !Members(docker.Value, "ipc_mode").Any()) continue;
                CheckMember(root, "engine", "engine");
                CheckMember(engines.Value, engine.Name, path, exactKey: true);
                CheckCommonDocker(engine.Value, path);
                CheckMember(engine.Value, "catalog", $"{path}.catalog");
                _ = engine.Value.TryGetProperty("catalog", out var catalog);
                CheckAuthority(engine.Value, catalog, path);
                if (hasPolicy) CheckContainerPolicyJson(docker.Value, dockerPath);
            }
        }
    }

    private static void RejectMisplacedTargetOptionsJson(JsonElement table, string path)
    {
        if (Members(table, "healthcheck").Any())
            throw new RouterConfigException($"{path}.healthcheck is only supported inside a serving-tier docker block");
        if (Members(table, "nested_under").Any())
            throw new RouterConfigException($"{path}.nested_under is only supported on serving-tier bind mounts");
        foreach (var list in Members(table, "bind_mounts"))
        {
            if (Members(list.Value, "nested_under").Any())
                throw new RouterConfigException($"{path}.bind_mounts.nested_under is only supported on serving-tier bind mounts");
            if (list.Value.ValueKind != JsonValueKind.Array) continue;
            var index = 0;
            foreach (var bind in list.Value.EnumerateArray())
            {
                if (Members(bind, "nested_under").Any())
                    throw new RouterConfigException($"{path}.bind_mounts[{index}].nested_under is only supported on serving-tier bind mounts");
                index++;
            }
        }
    }

    private static void CheckTierBindsJson(JsonElement target, string path)
    {
        CheckMember(target, "bind_mounts", path);
        if (!target.TryGetProperty("bind_mounts", out var binds) || binds.ValueKind != JsonValueKind.Array) return;
        var index = 0;
        foreach (var bind in binds.EnumerateArray())
        {
            var bindPath = $"{path}[{index}]";
            CheckMember(bind, "source", $"{bindPath}.source");
            CheckMember(bind, "target", $"{bindPath}.target");
            CheckMember(bind, "read_only", $"{bindPath}.read_only");
            CheckMember(bind, "nested_under", $"{bindPath}.nested_under");
            if (bind.ValueKind == JsonValueKind.Object && bind.TryGetProperty("nested_under", out var parent) &&
                parent.ValueKind != JsonValueKind.String)
                throw new RouterConfigException($"{bindPath}.nested_under must be a string");
            index++;
        }
    }

    private static void CheckHealthcheckJson(JsonElement target, string path)
    {
        CheckMember(target, "healthcheck", path);
        if (!target.TryGetProperty("healthcheck", out var healthcheck)) return;
        if (healthcheck.ValueKind != JsonValueKind.Object)
            throw new RouterConfigException($"{path} must be an object");

        CheckMember(healthcheck, "command", $"{path}.command");
        CheckMember(healthcheck, "interval_ms", $"{path}.interval_ms");
        CheckMember(healthcheck, "start_period_ms", $"{path}.start_period_ms");
        CheckMember(healthcheck, "timeout_ms", $"{path}.timeout_ms");
        CheckMember(healthcheck, "retries", $"{path}.retries");
        foreach (var field in healthcheck.EnumerateObject())
        {
            switch (field.Name)
            {
                case "command":
                    if (field.Value.ValueKind != JsonValueKind.String)
                        throw new RouterConfigException($"{path}.command must be a string");
                    break;
                case "interval_ms":
                case "start_period_ms":
                case "timeout_ms":
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt64(out _))
                        throw new RouterConfigException($"{path}.{field.Name} must be an Int64 integer");
                    break;
                case "retries":
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out _))
                        throw new RouterConfigException($"{path}.retries must be an Int32 integer");
                    break;
                default:
                    throw new RouterConfigException($"{path}.{field.Name} is not a health check setting");
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
