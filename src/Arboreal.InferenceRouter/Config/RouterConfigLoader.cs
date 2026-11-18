using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Tomlyn;
using Tomlyn.Serialization;

namespace Arboreal.InferenceRouter;

/// <summary>Config loader. TOML via Tomlyn's source-generated typed context, JSON via STJ's
/// source-generated context over the same DTOs (<see cref="ConfigDto"/>). Schema errors fail
/// fast with path-precise messages; unknown engine kinds are rejected at boot.
/// Deliberately reflection-free: both parsers are Roslyn-generated (Native AOT clean).</summary>
public static class RouterConfigLoader
{
    public static RouterConfig LoadFile(string path)
    {
        var text = File.ReadAllText(path);
        return Load(text, source: path, isJson: Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase));
    }

    public static RouterConfig Load(string text, string? source = null, bool isJson = false)
    {
        try
        {
            ConfigDto dto;
            if (isJson)
            {
                // JSON permits no comments per spec, but be forgiving (trailing commas, //…)
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                DockerLaunchResources.CheckJson(doc.RootElement);
                dto = JsonSerializer.Deserialize<ConfigDto>(doc, ConfigJsonContext.Default.ConfigDto)
                      ?? throw new RouterConfigException("config: empty document");
            }
            else
            {
                dto = TomlSerializer.Deserialize(text, ConfigTomlContext.Default.ConfigDto)
                      ?? throw new RouterConfigException("config: empty document");
            }
            return FromDto(dto, source ?? (isJson ? "config.json" : "config.toml"));
        }
        catch (TomlException ex)
        {
            throw new RouterConfigException($"TOML parse error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            throw new RouterConfigException($"JSON parse error: {ex.Message}");
        }
    }

    // ── DTO → domain mapping (fail-fast, path-precise) ──────────────────────

    public static RouterConfig FromDto(ConfigDto dto, string source)
    {
        var cfg = new RouterConfig();
        if (dto.Listen is { } listen) cfg.Listen = listen;
        if (dto.DefaultModel is { } dm) cfg.DefaultModel = dm;
        if (dto.AutoSwitch is { } autoSwitch) cfg.AutoSwitch = autoSwitch;
        if (dto.MaxRequestBodyBytes is { } maxBody)
        {
            if (maxBody < 1024) throw new RouterConfigException("max_request_body_bytes must be at least 1024");
            cfg.MaxRequestBodyBytes = maxBody;
        }
        if (dto.MaxEventSubscribers is { } maxSubs)
        {
            if (maxSubs < 1) throw new RouterConfigException("max_event_subscribers must be at least 1");
            cfg.MaxEventSubscribers = maxSubs;
        }
        if (dto.SwitchCooldownMs is { } cooldown) cfg.SwitchCooldownMs = checked((int)cooldown);
        if (dto.DefaultPriority is { } dp)
            cfg.DefaultPriority = ParsePriority(dp, "default_priority");

        if (dto.Scheduler is { } sched)
        {
            var sc = new SchedulerConfig();
            if (sched.PrefixAffinityThresholdChars is { } v1) sc.PrefixAffinityThresholdChars = v1;
            if (sched.AffinityWeight is { } v2) sc.AffinityWeight = v2;
            if (sched.PrefillCostAlpha is { } v3) sc.PrefillCostAlpha = v3;
            if (sched.MaxQueuePerModel is { } v4) sc.MaxQueuePerModel = v4;
            if (sched.MaxInflightPerEngine is { } v5) sc.MaxInflightPerEngine = v5;
            if (sched.PrefixTrieMaxNodes is { } v6) sc.PrefixTrieMaxNodes = v6;
            if (sched.CharsPerTokenEstimate is { } v7) sc.CharsPerTokenEstimate = v7;
            if (sched.SwitchWaitTimeoutMs is { } v8)
            {
                if (v8 < 0)
                    throw new RouterConfigException(
                        "scheduler.switch_wait_timeout_ms must be >= 0 (0 lets a request wait through the whole model load)");
                sc.SwitchWaitTimeoutMs = v8;
            }
            cfg.Scheduler = sc;
        }

        if (dto.Threads is { } threads)
        {
            var dict = new Dictionary<string, ThreadDefinition>(StringComparer.Ordinal);
            foreach (var (key, t) in threads)
            {
                if (t.Priority is null)
                    throw new RouterConfigException($"threads.{key}.priority is required");
                var td = new ThreadDefinition
                {
                    Priority = ParsePriority(t.Priority, $"threads.{key}.priority"),
                    ModelAffinity = t.ModelAffinity,
                };
                if (t.Weight is { } w) td = td with { Weight = w };
                dict[key] = td;
            }
            cfg.Threads = dict;
        }

        if (dto.Engines is { } engines)
        {
            var dict = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal);
            foreach (var (name, e) in engines)
                dict[name] = ParseEngine(name, e);
            cfg.Engines = dict;
        }

        if (cfg.Engines.Count == 0)
            throw new RouterConfigException($"{source}: at least one [engine.<name>] is required");
        cfg.ValidateLifecycleTopology();
        return cfg;
    }

    private static EngineDefinition ParseEngine(string name, EngineDto e)
    {
        var kindStr = e.Kind ?? throw new RouterConfigException($"engine.{name}.kind is required");
        var kind = kindStr switch
        {
            "openai-compatible" => EngineKind.OpenAiCompatible,
            _ => throw new RouterConfigException($"engine.{name}: unknown engine kind '{kindStr}'"),
        };
        var endpointStr = e.Endpoint ?? throw new RouterConfigException($"engine.{name}.endpoint is required");
        if (!Uri.TryCreate(endpointStr, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != "http" && endpoint.Scheme != "https"))
            throw new RouterConfigException($"engine.{name}.endpoint: '{endpointStr}' is not an absolute http(s) URI");

        var capabilities = new EngineCapabilities();
        if (e.Capabilities is { } caps)
        {
            if (caps.Vision is { } vision) capabilities.Vision = vision;
            if (caps.SuffixCache is { } sc) capabilities.SuffixCache = sc;
            if (caps.MaxContext is { } mc) capabilities.MaxContext = mc;
            if (caps.MaxSlots is { } ms) capabilities.MaxSlots = ms;
        }

        HealthProbeDefinition? health = null;
        if (e.HealthProbe is { } hp)
        {
            health = new HealthProbeDefinition
            {
                Path = hp.Path ?? "/props",
                Expect = hp.Expect,
                IntervalMs = hp.IntervalMs ?? 2_000,
                TimeoutMs = hp.TimeoutMs ?? 5_000,
                ContextPath = hp.ContextPath ?? "n_ctx",
                SlotsPath = hp.SlotsPath ?? "total_slots",
            };
        }

        var catalog = new Dictionary<string, EngineModelDefinition>(StringComparer.Ordinal);
        if (e.Catalog is { } cat)
        {
            foreach (var (modelName, m) in cat)
            {
                var def = new EngineModelDefinition { Name = modelName };
                if (m.Aliases is { } aliases)
                    def = def with { Aliases = aliases.ToArray() };
                if (m.Profiles is { } profiles)
                    def = def with { Profiles = profiles.ToArray() };
                if (m.Profile is { } profile) def = def with { Profile = profile };
                if (m.DefaultProfile is { } dp) def = def with { Profile = dp };
                if (m.Serving is { } serving)
                {
                    var ladder = new ServingProfile[serving.Count];
                    for (var i = 0; i < serving.Count; i++)
                    {
                        var s = serving[i];
                        if (s.Name is not { Length: > 0 } sname)
                            throw new RouterConfigException($"engine.{name}.catalog.{modelName}.serving[{i}].name is required");
                        if (s.MaxContext is not { } mc || mc <= 0)
                            throw new RouterConfigException($"engine.{name}.catalog.{modelName}.serving[{i}].max_context must be a positive token count");
                        ladder[i] = new ServingProfile
                        {
                            Name = sname,
                            MaxContext = mc,
                            MaxSlots = s.MaxSlots ?? 1,
                        };
                        if (ladder[i].MaxSlots < 1)
                            throw new RouterConfigException($"engine.{name}.catalog.{modelName}.serving[{i}].max_slots must be >= 1");
                    }
                    def = def with { ServingProfiles = ladder };
                    if (def.Profile is { } p && Array.Find(ladder, x => x.Name == p) is null)
                        throw new RouterConfigException(
                            $"engine.{name}.catalog.{modelName}.profile '{p}' is not one of the declared serving profiles");
                }
                if (m.Select is { } sel)
                {
                    def = def with
                    {
                        Select = new LifecycleCommand
                        {
                            Exec = sel.Exec ?? throw new RouterConfigException(
                                $"engine.{name}.catalog.{modelName}.select.exec is required"),
                            WaitHealth = sel.WaitHealth ?? true,
                            WaitTimeoutMs = sel.WaitTimeoutMs ?? 7_200_000,
                            Args = sel.Args is { } args ? args.ToArray() : [],
                        },
                    };
                }
                catalog[modelName] = def;
            }
        }

        var lifecycle = EngineLifecycleKind.Delegated;
        DockerLifecycleConfig? docker = null;
        if (e.Lifecycle is { Length: > 0 } lcText)
        {
            var lc = lcText.Trim();
            if (lc.Equals("docker", StringComparison.OrdinalIgnoreCase)) lifecycle = EngineLifecycleKind.Docker;
            else if (lc.Equals("exec", StringComparison.OrdinalIgnoreCase) ||
                     lc.Equals("delegated", StringComparison.OrdinalIgnoreCase)) lifecycle = EngineLifecycleKind.Delegated;
            else throw new RouterConfigException($"engine.{name}.lifecycle must be \"docker\" or \"exec\" (got '{lc}')");
        }
        if (e.Docker is { } d)
        {
            lifecycle = EngineLifecycleKind.Docker;
            if (d.CallTimeoutMs is { } callMs && callMs < 100)
                throw new RouterConfigException(
                    $"{name}.docker.call_timeout_ms must be at least 100: below that a healthy daemon is treated as wedged");
            if (string.IsNullOrWhiteSpace(d.Image))
                throw new RouterConfigException(
                    $"engine.{name}.docker.image is required: the router starts an image that is already on the host and never pulls one itself");
            docker = new DockerLifecycleConfig
            {
                Image = d.Image!,
                ImageIdentity = ParseDockerImageIdentity(d, name),
                Resources = d.Resources is { } resources ? DockerLaunchResources.Parse(resources, name) : null,
                Socket = d.Socket ?? "unix:///var/run/docker.sock",
                ApiVersion = d.ApiVersion ?? "v1.44",
                RouterId = d.RouterId ?? throw new RouterConfigException(
                    $"engine.{name}.docker.router_id is required: it is the label that says which router owns a container, and a shared default would let two routers stop each other's work"),
                Command = d.Command ?? [],
                Env = d.Env ?? [],
                StopTimeoutMs = d.StopTimeoutMs ?? 30_000,
                HostPort = d.HostPort,
                HostIp = d.HostIp ?? "127.0.0.1",
                RemoveStopped = d.RemoveStopped ?? true,
                ContainerPort = d.ContainerPort ?? 8080,
                NetworkMode = d.NetworkMode,
                WaitHealth = d.WaitHealth ?? true,
                WaitTimeoutMs = d.WaitTimeoutMs ?? 600_000,
                CallTimeoutMs = d.CallTimeoutMs ?? 15_000,
            };
        }
        else if (lifecycle == EngineLifecycleKind.Docker)
        {
            throw new RouterConfigException($"engine.{name}.lifecycle = \"docker\" needs an [engine.{name}.docker] block with image = …");
        }
        if (docker is not null)
        {
            foreach (var (modelName, model) in catalog)
            {
                if (model.Select is not null)
                    throw new RouterConfigException(
                        $"engine.{name} has native docker lifecycle, and model \"{modelName}\" still carries a select command: " +
                        "pick one owner for this model's lifecycle (a delegated selector or the router) — they cannot both prove ownership");
            }
        }

        var responseTimeoutMs = e.ResponseTimeoutMs ?? 600_000;
        if (responseTimeoutMs < 0)
            throw new RouterConfigException(
                $"engine.{name}.response_timeout_ms must be >= 0 (0 disables the cap on waiting for the engine's first byte)");

        if (e.ModelsContextWindowPath is { } modelsContextPath &&
            modelsContextPath.Split('.').Any(segment => segment.Length == 0 || segment != segment.Trim()))
            throw new RouterConfigException(
                $"engine.{name}.models_context_window_path must contain nonempty dotted object field names");

        if (e.Remote.HasValue && e.LifecycleOwner is not null)
            throw new RouterConfigException($"engine.{name}: remove remote when setting lifecycle_owner");
        var owner = e.LifecycleOwner switch
        {
            "external" when e.Lifecycle is not null => throw new RouterConfigException(
                $"engine.{name}.lifecycle_owner = external conflicts with lifecycle"),
            "external" => EngineLifecycleOwner.External,
            "router" when e.Lifecycle is { Length: > 0 } mode && !mode.Trim().Equals("docker", StringComparison.OrdinalIgnoreCase) =>
                throw new RouterConfigException($"engine.{name}.lifecycle_owner = router conflicts with delegated lifecycle"),
            "router" => EngineLifecycleOwner.Router,
            "delegated" => EngineLifecycleOwner.Delegated,
            null when e.Remote == true => EngineLifecycleOwner.External,
            null when docker is not null => EngineLifecycleOwner.Router,
            null when e.Lifecycle is { } controller && !string.IsNullOrWhiteSpace(controller) ||
                catalog.Values.Any(DelegatedExecProvider.HasUsableSelector) => EngineLifecycleOwner.Delegated,
            null => EngineLifecycleOwner.External,
            _ => throw new RouterConfigException($"engine.{name}.lifecycle_owner must be external, router, or delegated"),
        };

        var definition = new EngineDefinition
        {
            Name = name,
            Kind = kind,
            Endpoint = endpoint,
            ApiKey = e.ApiKey,
            ApiKeyFile = e.ApiKeyFile,
            Health = health,
            ModelsEndpoint = e.ModelsEndpoint ?? "/models",
            ModelsContextWindowPath = e.ModelsContextWindowPath,
            LoadedRequiresStatusLoaded = e.LoadedRequiresStatusLoaded ?? false,
            Capabilities = capabilities,
            Catalog = catalog,
            Remote = e.Remote ?? false,
            LifecycleOwner = owner,
            LegacyLifecycleConfiguration = e.LifecycleOwner is null,
            ResponseTimeoutMs = responseTimeoutMs,
            Lifecycle = lifecycle,
            Docker = docker,
        };
        _ = definition.ResolveLifecycleOwner();
        return definition;
    }

    private static DockerImageIdentity? ParseDockerImageIdentity(DockerDto docker, string engineName)
    {
        if (!docker.HasImageIdentity) return null;
        try
        {
            return new DockerImageIdentity(docker.ImageId ?? "", docker.ImageRepoDigest ?? "", docker.ImagePlatform ?? "");
        }
        catch (ArgumentException ex)
        {
            var field = ex.ParamName switch
            {
                "id" => "image_id",
                "repoDigest" => "image_repo_digest",
                _ => "image_platform",
            };
            throw new RouterConfigException($"engine.{engineName}.docker.{field} is missing or malformed. {ex.Message}");
        }
    }

    private static ThreadPriority ParsePriority(PriorityDto t, string path)
    {
        var classStr = t.Class ?? throw new RouterConfigException($"{path}.class is required");
        if (!ThreadPriorityClass.TryParseClass(classStr.Trim(), out var cls))
            throw new RouterConfigException($"{path}: unknown priority class '{classStr}' (background|low|normal|high|interactive)");
        var sub = t.Sub ?? 0;
        if (sub is < 0 or > ThreadPriority.MaxSubPriority)
            throw new RouterConfigException($"{path}.sub must be 0..{ThreadPriority.MaxSubPriority}");
        return new ThreadPriority((byte)cls, (byte)sub);
    }
}
