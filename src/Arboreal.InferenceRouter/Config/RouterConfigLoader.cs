using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace Arboreal.InferenceRouter;

/// <summary>Config loader. Reads TOML (Tomlyn untyped DOM — reflection-free, AOT-safe by
/// construction) or JSON (JsonDocument) into a neutral Dictionary tree, then maps fields by
/// hand to the typed RouterConfig. Schema errors fail fast with path-precise messages;
/// unknown engine kinds are rejected at boot.</summary>
public static class RouterConfigLoader
{
    public static RouterConfig LoadFile(string path)
    {
        var text = File.ReadAllText(path);
        return Load(text, source: path, isJson: Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase));
    }

    public static RouterConfig Load(string text, string? source = null, bool isJson = false)
    {
        var dom = isJson ? ParseJson(text) : ParseToml(text);
        return FromDom(dom, source ?? (isJson ? "config.json" : "config.toml"));
    }

    // ── neutral DOM ─────────────────────────────────────────────────────────
    // Dictionary<string, object?> where values are string | long | double | bool |
    // Dictionary<string, object?> | List<object?>. Deliberately plain types.

    internal static Dictionary<string, object?> ParseToml(string text)
    {
        try
        {
            var table = (TomlTable)TomlSerializer.Deserialize(text, typeof(TomlTable))!;
            return ConvertToml(table);
        }
        catch (TomlException ex)
        {
            throw new RouterConfigException($"TOML parse error: {ex.Message}");
        }
    }

    private static Dictionary<string, object?> ConvertToml(TomlTable table)
    {
        var dict = new Dictionary<string, object?>(table.Keys.Count);
        foreach (var key in table.Keys)
            dict[key] = ConvertTomlValue(table[key]);
        return dict;
    }

    private static object? ConvertTomlValue(object? value) => value switch
    {
        null => null,
        TomlTable t => ConvertToml(t),
        TomlTableArray tta => (object)new List<object?>(tta.Cast<object?>().Select(ConvertTomlValue)),
        TomlArray ta => new List<object?>(ta.Cast<object?>().Select(ConvertTomlValue)),
        TomlDateTime dt => dt.ToString(),
        _ => value, // string | long | double | bool
    };

    internal static Dictionary<string, object?> ParseJson(string text)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(text, options);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new RouterConfigException("JSON config: top-level must be an object");
        return (Dictionary<string, object?>)ConvertJson(root)!;
    }

    private static object? ConvertJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : (object)e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object => ToDict(e),
        JsonValueKind.Array => ToList(e),
        _ => null,
    };

    private static Dictionary<string, object?> ToDict(JsonElement e)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in e.EnumerateObject())
            d[p.Name] = ConvertJson(p.Value);
        return d;
    }

    private static List<object?> ToList(JsonElement e)
    {
        var l = new List<object?>();
        foreach (var item in e.EnumerateArray())
            l.Add(ConvertJson(item));
        return l;
    }

    // ── typed mapping ───────────────────────────────────────────────────────

    public static RouterConfig FromDom(Dictionary<string, object?> root, string source)
    {
        var cfg = new RouterConfig();
        if (TryGet(root, "listen", out string? listen)) cfg.Listen = listen!;
        if (TryGet(root, "default_model", out string? dm)) cfg.DefaultModel = dm!;
        if (TryGet(root, "auto_switch", out bool autoSwitch)) cfg.AutoSwitch = autoSwitch;
        if (TryGet(root, "switch_cooldown_ms", out long cooldown)) cfg.SwitchCooldownMs = checked((int)cooldown);
        if (GetTable(root, "default_priority") is { } dp)
            cfg.DefaultPriority = ParsePriority(dp, "default_priority");

        if (GetTable(root, "scheduler") is { } sched)
        {
            var sc = new SchedulerConfig();
            if (TryGet(sched, "prefix_affinity_threshold_chars", out long v1)) sc.PrefixAffinityThresholdChars = checked((int)v1);
            if (TryGet(sched, "affinity_weight", out double v2)) sc.AffinityWeight = v2;
            if (TryGet(sched, "prefill_cost_alpha", out double v3)) sc.PrefillCostAlpha = v3;
            if (TryGet(sched, "max_queue_per_model", out long v4)) sc.MaxQueuePerModel = checked((int)v4);
            if (TryGet(sched, "max_inflight_per_engine", out long v5)) sc.MaxInflightPerEngine = checked((int)v5);
            if (TryGet(sched, "prefix_trie_max_nodes", out long v6)) sc.PrefixTrieMaxNodes = checked((int)v6);
            cfg.Scheduler = sc;
        }

        if (GetTable(root, "threads") is { } threads)
        {
            var dict = new Dictionary<string, ThreadDefinition>();
            foreach (var key in TableKeys(threads))
            {
                if (threads[key] is not Dictionary<string, object?> t)
                    throw new RouterConfigException($"threads.{key} must be a table");
                var td = new ThreadDefinition
                {
                    Priority = ParsePriority(RequireTable(t, "priority", $"threads.{key}"), $"threads.{key}.priority"),
                    ModelAffinity = Get<string?>(t, "model_affinity"),
                };
                if (TryGet(t, "weight", out double w)) td = td with { Weight = w };
                dict[key] = td;
            }
            cfg.Threads = dict;
        }

        if (GetTable(root, "engine") is { } engines)
        {
            var dict = new Dictionary<string, EngineDefinition>();
            foreach (var name in TableKeys(engines))
            {
                if (engines[name] is not Dictionary<string, object?> e)
                    throw new RouterConfigException($"engine.{name} must be a table");
                dict[name] = ParseEngine(name, e);
            }
            cfg.Engines = dict;
        }

        if (cfg.Engines.Count == 0)
            throw new RouterConfigException($"{source}: at least one [engine.<name>] is required");
        return cfg;
    }

    private static EngineDefinition ParseEngine(string name, Dictionary<string, object?> e)
    {
        var kindStr = Get<string>(e, "kind") ?? throw new RouterConfigException($"engine.{name}.kind is required");
        var kind = kindStr switch
        {
            "openai-compatible" => EngineKind.OpenAiCompatible,
            _ => throw new RouterConfigException($"engine.{name}: unknown engine kind '{kindStr}'"),
        };
        var endpointStr = Get<string>(e, "endpoint") ?? throw new RouterConfigException($"engine.{name}.endpoint is required");
        if (!Uri.TryCreate(endpointStr, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != "http" && endpoint.Scheme != "https"))
            throw new RouterConfigException($"engine.{name}.endpoint: '{endpointStr}' is not an absolute http(s) URI");

        var capabilities = new EngineCapabilities();
        if (GetTable(e, "capabilities") is { } caps)
        {
            if (TryGet(caps, "vision", out bool vision)) capabilities.Vision = vision;
            if (TryGet(caps, "suffix_cache", out bool sc)) capabilities.SuffixCache = sc;
            if (TryGet(caps, "max_context", out long mc)) capabilities.MaxContext = mc;
            if (TryGet(caps, "max_slots", out long ms)) capabilities.MaxSlots = checked((int)ms);
        }

        HealthProbeDefinition? health = null;
        if (GetTable(e, "health_probe") is { } hp)
        {
            health = new HealthProbeDefinition
            {
                Path = Get<string>(hp, "path") ?? "/props",
                Expect = Get<string>(hp, "expect"),
                IntervalMs = GetInt(hp, "interval_ms", 2_000),
                TimeoutMs = GetInt(hp, "timeout_ms", 5_000),
            };
        }

        var catalog = new Dictionary<string, EngineModelDefinition>();
        if (GetTable(e, "catalog") is { } cat)
        {
            foreach (var modelName in TableKeys(cat))
            {
                if (cat[modelName] is not Dictionary<string, object?> m)
                    throw new RouterConfigException($"engine.{name}.catalog.{modelName} must be a table");
                var def = new EngineModelDefinition { Name = modelName };
                if (GetArray(m, "aliases") is { } aliases)
                    def = def with { Aliases = aliases.Select(a => a?.ToString() ?? "").ToArray() };
                if (GetArray(m, "profiles") is { } profiles)
                    def = def with { Profiles = profiles.Select(a => a?.ToString() ?? "").ToArray() };
                if (TryGet(m, "profile", out string? profile)) def = def with { Profile = profile };
                if (GetTable(m, "select") is { } sel)
                {
                    def = def with
                    {
                        Select = new LifecycleCommand
                        {
                            Exec = Get<string>(sel, "exec") ?? throw new RouterConfigException($"engine.{name}.catalog.{modelName}.select.exec is required"),
                            WaitHealth = TryGet(sel, "wait_health", out bool wh) ? wh : true,
                            WaitTimeoutMs = GetInt(sel, "wait_timeout_ms", 7_200_000),
                            Args = GetArray(sel, "args") is { } args
                                ? args.Select(a => a?.ToString() ?? "").ToArray()
                                : [],
                        },
                    };
                }
                catalog[modelName] = def;
            }
        }

        return new EngineDefinition
        {
            Name = name,
            Kind = kind,
            Endpoint = endpoint,
            ApiKey = Get<string>(e, "api_key"),
            ApiKeyFile = Get<string>(e, "api_key_file"),
            Health = health,
            ModelsEndpoint = Get<string>(e, "models_endpoint") ?? "/models",
            LoadedRequiresStatusLoaded = TryGet(e, "loaded_requires_status_loaded", out bool l) && l,
            Capabilities = capabilities,
            Catalog = catalog,
            Remote = TryGet(e, "remote", out bool r) && r,
        };
    }

    private static ThreadPriority ParsePriority(Dictionary<string, object?> t, string path)
    {
        var classStr = Get<string>(t, "class") ?? throw new RouterConfigException($"{path}.class is required");
        if (!ThreadPriorityClass.TryParseClass(classStr.Trim(), out var cls))
            throw new RouterConfigException($"{path}: unknown priority class '{classStr}' (background|low|normal|high|interactive)");
        var sub = GetInt(t, "sub", 0);
        if (sub is < 0 or > ThreadPriority.MaxSubPriority)
            throw new RouterConfigException($"{path}.sub must be 0..{ThreadPriority.MaxSubPriority}");
        return new ThreadPriority((byte)cls, (byte)sub);
    }

    // ── neutral DOM helpers ─────────────────────────────────────────────────

    private static string[] TableKeys(Dictionary<string, object?> table)
    {
        var list = new List<string>();
        foreach (var key in table.Keys) list.Add(key);
        return list.ToArray();
    }

    private static Dictionary<string, object?> RequireTable(Dictionary<string, object?> table, string key, string path)
        => table.TryGetValue(key, out var v) && v is Dictionary<string, object?> t
            ? t
            : throw new RouterConfigException($"{path}.{key} must be a table");

    private static int GetInt(Dictionary<string, object?> table, string key, int fallback)
        => TryGet(table, key, out long v) ? checked((int)v) : fallback;

    private static T? Get<T>(Dictionary<string, object?> table, string key)
        => table.TryGetValue(key, out var v) && v is T t ? t : default;

    private static bool TryGet<T>(Dictionary<string, object?> table, string key, out T value)
    {
        value = default!;
        if (!table.TryGetValue(key, out var v) || v is not T t) return false;
        value = t;
        return true;
    }

    private static Dictionary<string, object?>? GetTable(Dictionary<string, object?> table, string key)
        => table.TryGetValue(key, out var v) && v is Dictionary<string, object?> t ? t : null;

    private static List<object?>? GetArray(Dictionary<string, object?> table, string key)
        => table.TryGetValue(key, out var v) && v is List<object?> a ? a : null;
}
