using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tomlyn.Model;

namespace Arboreal.InferenceRouter;

/// <summary>The declaration tree preserves settings the generated domain mapper intentionally ignores.</summary>
internal sealed record ConfigDeclaration(
    Dictionary<string, ConfigDeclaration>? Fields = null,
    ConfigDeclaration[]? Items = null, object? Scalar = null)
{
    internal static ConfigDeclaration Read(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new Dictionary<string, ConfigDeclaration>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
                if (!fields.TryAdd(field.Name, Read(field.Value)))
                    throw new RouterConfigException("duplicate JSON configuration property");
            return new(Fields: fields);
        }
        if (value.ValueKind == JsonValueKind.Array) return new(Items: [.. value.EnumerateArray().Select(Read)]);
        return new(Scalar: value.ValueKind == JsonValueKind.String ? value.GetString() : value.Clone());
    }

    internal static ConfigDeclaration Read(object? value) => value switch
    {
        TomlTable table => new(Fields: table.ToDictionary(pair => pair.Key, pair => Read(pair.Value), StringComparer.Ordinal)),
        TomlArray array => new(Items: [.. array.Select(Read)]),
        TomlTableArray array => new(Items: [.. array.Select(Read)]),
        _ => new(Scalar: value),
    };

    internal bool Matches(ConfigDeclaration other, bool ordered = false)
    {
        if (Fields is { } fields && other.Fields is { } otherFields)
        {
            return fields.Count == otherFields.Count &&
                (!ordered || fields.Keys.SequenceEqual(otherFields.Keys, StringComparer.Ordinal)) &&
                fields.All(pair => otherFields.TryGetValue(pair.Key, out var field) &&
                    pair.Value.Matches(field, ordered: pair.Key == "catalog"));
        }
        if (Fields is not null || other.Fields is not null) return false;
        if (Items is { } items && other.Items is { } otherItems)
            return items.Length == otherItems.Length && items.Where((item, index) => !item.Matches(otherItems[index])).Any() == false;
        if (Items is not null || other.Items is not null) return false;
        if (Scalar is JsonElement left && other.Scalar is JsonElement right) return JsonElement.DeepEquals(left, right);
        return Equals(Scalar, other.Scalar);
    }
}

internal sealed class ReloadConfiguration(RouterConfig config, ConfigDeclaration declaration, string text, bool isJson)
{
    internal RouterConfig Config { get; } = config;
    private ConfigDeclaration Declaration { get; } = declaration;

    internal RouterConfig Observe() => RouterConfigLoader.Load(text, isJson: isJson);

    internal void ValidateNames()
    {
        if (Config.Engines.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Config.Engines.Count)
            throw new RouterConfigException("engine names must be distinct ignoring case");
    }

    internal ConfigReloadStatus Compare(ReloadConfiguration candidate, out string[] added, out string[] removed) =>
        Compare(candidate, out added, out removed, out _);

    internal ConfigReloadStatus Compare(ReloadConfiguration candidate, out string[] added, out string[] removed, out bool aliasesChanged)
    {
        aliasesChanged = false;
        candidate.ValidateNames();
        var before = Config.Engines;
        var after = candidate.Config.Engines;
        added = [.. after.Keys.Where(key => !before.ContainsKey(key)).Order(StringComparer.Ordinal)];
        removed = [.. before.Keys.Where(key => !after.ContainsKey(key))];
        var oldRoot = Declaration.Fields!;
        var newRoot = candidate.Declaration.Fields!;
        if (!new ConfigDeclaration(Fields: WithoutLiveSettings(oldRoot))
            .Matches(new ConfigDeclaration(Fields: WithoutLiveSettings(newRoot))))
            return ConfigReloadStatus.RestartRequired;

        var oldEngines = oldRoot["engine"].Fields!;
        var newEngines = newRoot["engine"].Fields!;
        if (!before.Keys.Where(after.ContainsKey).SequenceEqual(after.Keys.Where(before.ContainsKey), StringComparer.Ordinal) ||
            removed.Any(key => before[key].ResolveLifecycleOwner() != EngineLifecycleOwner.External) ||
            added.Any(key => (after[key].ResolveLifecycleOwner() != EngineLifecycleOwner.External &&
                !(after[key].LifecycleGroup is { } group && before.Values.Any(e => e.LifecycleGroup == group))) ||
                before.Keys.Contains(key, StringComparer.OrdinalIgnoreCase)))
            return ConfigReloadStatus.RestartRequired;

        foreach (var name in before.Keys.Where(after.ContainsKey))
        {
            if (oldEngines[name].Matches(newEngines[name])) continue;
            if (before[name].LifecycleGroup is null || !OnlyAddsAliases(oldEngines[name], newEngines[name]))
                return ConfigReloadStatus.RestartRequired;
            aliasesChanged = true;
        }
        candidate.Config.ValidateLifecycleTopology();
        foreach (var name in added)
        {
            ValidateAddedEngine(newEngines[name]);
            if (after[name].Health is { } probe && (probe.IntervalMs <= 0 || probe.TimeoutMs < -1))
                throw new RouterConfigException("invalid external health probe timing");
        }
        if (added.Length != 0 || aliasesChanged) ValidateAliases(before, after, aliasesChanged ? [.. after.Keys] : added);

        return !aliasesChanged && added.Length == 0 && removed.Length == 0 &&
            string.Equals(Config.DefaultModel, candidate.Config.DefaultModel, StringComparison.Ordinal)
            ? ConfigReloadStatus.Unchanged : ConfigReloadStatus.Applied;
    }

    private static bool OnlyAddsAliases(ConfigDeclaration oldEngine, ConfigDeclaration newEngine)
    {
        var oldFields = oldEngine.Fields!;
        var newFields = newEngine.Fields!;
        if (!ExceptField(oldFields, "catalog").Matches(ExceptField(newFields, "catalog")) ||
            !oldFields.TryGetValue("catalog", out var oldCatalog) || !newFields.TryGetValue("catalog", out var newCatalog) ||
            oldCatalog.Fields is not { } models || newCatalog.Fields is not { } next ||
            !models.Keys.SequenceEqual(next.Keys, StringComparer.Ordinal)) return false;

        foreach (var (name, model) in models)
        {
            if (model.Fields is not { } fields || next[name].Fields is not { } updated ||
                !ExceptField(fields, "aliases").Matches(ExceptField(updated, "aliases"))) return false;
            if (!fields.TryGetValue("aliases", out var aliases)) continue;
            if (!updated.TryGetValue("aliases", out var replacement) || aliases.Items is not { } oldItems ||
                replacement.Items is not { } newItems || !oldItems.All(old => newItems.Any(item => item.Matches(old))))
                return false;
        }
        return true;
    }

    private static ConfigDeclaration ExceptField(Dictionary<string, ConfigDeclaration> fields, string name) =>
        new(Fields: fields.Where(pair => pair.Key != name).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static Dictionary<string, ConfigDeclaration> WithoutLiveSettings(Dictionary<string, ConfigDeclaration> fields) =>
        fields.Where(pair => pair.Key is not ("engine" or "default_model"))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static void ValidateAddedEngine(ConfigDeclaration declaration)
    {
        var context = ConfigJsonContext.Default;
        RequireKnownFields(declaration, context.GetTypeInfo(typeof(EngineDto))!);
        var fields = declaration.Fields!;
        if (fields.TryGetValue("health_probe", out var probe))
            RequireKnownFields(probe, context.GetTypeInfo(typeof(HealthProbeDto))!);
        if (fields.TryGetValue("capabilities", out var capabilities))
            RequireKnownFields(capabilities, context.GetTypeInfo(typeof(CapabilitiesDto))!);
        if (!fields.TryGetValue("catalog", out var catalog) || catalog.Fields is null) return;
        foreach (var model in catalog.Fields.Values)
        {
            RequireKnownFields(model, context.GetTypeInfo(typeof(ModelDto))!);
            if (model.Fields?.TryGetValue("serving", out var serving) == true && serving.Items is { } tiers)
                foreach (var tier in tiers)
                    RequireKnownFields(tier, context.GetTypeInfo(typeof(ServingDto))!);
        }
    }

    private static void RequireKnownFields(ConfigDeclaration declaration, JsonTypeInfo shape)
    {
        if (declaration.Fields is { } fields && fields.Keys.Any(key => shape.Properties.All(property => property.Name != key)))
            throw new RouterConfigException("unknown field in added engine");
    }

    private static void ValidateAliases(IReadOnlyDictionary<string, EngineDefinition> before,
        IReadOnlyDictionary<string, EngineDefinition> after, string[] added)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var engine in before.Values)
            foreach (var (model, definition) in engine.Catalog)
            {
                known.TryAdd(model, model);
                foreach (var alias in definition.Aliases) known.TryAdd(alias, model);
            }
        foreach (var name in added)
            foreach (var (model, definition) in after[name].Catalog)
            {
                Check(model, model);
                foreach (var alias in definition.Aliases) Check(alias, model);
            }

        void Check(string token, string model)
        {
            if (!known.TryAdd(token, model) && !known[token].Equals(model, StringComparison.OrdinalIgnoreCase))
                throw new RouterConfigException("ambiguous model alias in added engine");
        }
    }
}

internal enum ConfigReloadStatus
{
    Applied, Unchanged, Stale, Busy, Invalid, RestartRequired, Closing, Cancelled,
}
