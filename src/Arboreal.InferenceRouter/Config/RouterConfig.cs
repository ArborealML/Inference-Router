namespace Arboreal.InferenceRouter;

/// <summary>Engine kinds the library ships adapters for. New kinds = new adapter class,
/// still no caller code changes; unknown kinds fail fast at config load.</summary>
public enum EngineKind : byte { OpenAiCompatible }

/// <summary>Declared lifecycle command: exec template with {{model}}/{{profile}}/{{gpu}}/{{suffixCache}} expansion.</summary>
public sealed class LifecycleCommand
{
    public required string Exec { get; init; }
    public string[] Args { get; init; } = [];
    public bool WaitHealth { get; init; } = true;
    public int WaitTimeoutMs { get; init; } = 7_200_000;   // select-model strata waits up to 2h
}

/// <summary>How an engine's model loads are managed. Delegated means a selector program does it (the
/// historical <c>select = { exec = … }</c>); Docker means the router drives the Engine API itself and can
/// therefore prove which containers it owns.</summary>
public enum EngineLifecycleKind : byte { Delegated, Docker }

/// <summary>Native Docker lifecycle for one engine: the image, how it is launched, and how long the
/// router waits for it to prove it is serving. What readiness must prove stays in config, because the
/// router can only verify what it can name.</summary>
public sealed class DockerLifecycleConfig
{
    public required string Image { get; init; }
    /// <summary><c>unix:///path/to/sock</c> or <c>http(s)://host:port</c>.</summary>
    public string Socket { get; init; } = "unix:///var/run/docker.sock";
    /// <summary>Pinned so a daemon update cannot change the API under a running router.</summary>
    public string ApiVersion { get; init; } = "v1.44";
    /// <summary>Written into the container labels, and what makes a container ours on a shared host.</summary>
    public string RouterId { get; init; } = "default";
    public string[] Command { get; init; } = [];
    public string[] Env { get; init; } = [];
    public int StopTimeoutMs { get; init; } = 30_000;
    public int? HostPort { get; init; }
    public int ContainerPort { get; init; } = 8080;
    public string? NetworkMode { get; init; }
    public bool WaitHealth { get; init; } = true;
    public int WaitTimeoutMs { get; init; } = 600_000;
    /// <summary>Deadline for one Engine API call. A wedged daemon must not be able to hold a switch
    /// open forever: the wait above is how long a model may take to load, not how long one HTTP call
    /// may take.</summary>
    public int CallTimeoutMs { get; init; } = 15_000;
}

/// <summary>Health probe definition. Expect is a tiny comparison DSL: "path OP value" with
/// path = dotted JSON path, OP in { >, >=, ==, !=, <, <= }, value = number or string literal.</summary>
public sealed class HealthProbeDefinition
{
    public required string Path { get; init; } = "/props";
    public string? Expect { get; init; }
    public int IntervalMs { get; init; } = 2_000;
    public int TimeoutMs { get; init; } = 5_000;
    /// <summary>Dotted path in the probe JSON reporting the active context window (tokens),
    /// e.g. "n_ctx". Empty when the engine does not expose it.</summary>
    public string ContextPath { get; init; } = "n_ctx";
    /// <summary>Dotted path reporting active concurrent slots, e.g. "total_slots".</summary>
    public string SlotsPath { get; init; } = "total_slots";
}

public sealed class EngineCapabilities
{
    public bool Vision { get; set; }
    public bool SuffixCache { get; set; }
    public long MaxContext { get; set; }
    public int MaxSlots { get; set; } = 1;
}

/// <summary>One model the engine can serve (catalog), with switch command + aliases.</summary>
/// <summary>A serving profile: a context-window tier with its slot capacity. Engines load a
/// profile via their lifecycle command; requests whose required context exceeds the active
/// profile's window are queued behind an escalation (profile switch), not compacted.</summary>
public sealed record ServingProfile
{
    public required string Name { get; init; }
    /// <summary>Maximum context window this profile provides, in tokens.</summary>
    public required long MaxContext { get; init; }
    /// <summary>Concurrent slots this profile allows (engine capacity while active).</summary>
    public int MaxSlots { get; init; } = 1;
}

public sealed record EngineModelDefinition
{
    public required string Name { get; init; }
    public string[] Aliases { get; init; } = [];
    public string[] Profiles { get; init; } = [];
    public LifecycleCommand? Select { get; init; }
    public string? Profile { get; init; }             // default profile for switch
    /// <summary>Serving profiles this model can be served under (context tier ladder).</summary>
    public IReadOnlyList<ServingProfile> ServingProfiles { get; init; } =
        Array.Empty<ServingProfile>();
}

public sealed class EngineDefinition
{
    public required string Name { get; init; }
    public EngineKind Kind { get; init; }
    public required Uri Endpoint { get; init; }       // base incl. /v1
    public string? ApiKey { get; init; }
    public string? ApiKeyFile { get; init; }
    public HealthProbeDefinition? Health { get; init; }
    public string ModelsEndpoint { get; init; } = "/models";
    /// <summary>When true, an entry counts as loaded only when its status says loaded
    /// (llama.cpp-style servers with alias entries).</summary>
    public bool LoadedRequiresStatusLoaded { get; init; }
    public EngineCapabilities Capabilities { get; init; } = new();
    public IReadOnlyDictionary<string, EngineModelDefinition> Catalog { get; init; } =
        new Dictionary<string, EngineModelDefinition>(StringComparer.Ordinal);
    public bool Remote { get; init; }                 // remote peer: no lifecycle commands
    public EngineLifecycleKind Lifecycle { get; init; } = EngineLifecycleKind.Delegated;
    public DockerLifecycleConfig? Docker { get; init; }
}

/// <summary>Thread pre-configuration from TOML.</summary>
public sealed record ThreadDefinition
{
    public required ThreadPriority Priority { get; init; }
    public double Weight { get; init; } = 1.0;
    public string? ModelAffinity { get; init; }
}

public sealed class SchedulerConfig
{
    public int PrefixAffinityThresholdChars { get; set; } = PrefixAwareScheduler.AffinityThresholdChars;
    public double AffinityWeight { get; set; } = PrefixAwareScheduler.AffinityWeight;
    public double PrefillCostAlpha { get; set; } = 1.0;
    public int MaxQueuePerModel { get; set; } = 64;
    public int MaxInflightPerEngine { get; set; } = 8;
    public int PrefixTrieMaxNodes { get; set; } = 8192;
    /// <summary>Chars-per-token estimate used to convert prompt chars into context demand.
    /// Conservative default; engines reporting n_ctx are compared in tokens.</summary>
    public double CharsPerTokenEstimate { get; set; } = 4.0;
}

public sealed class RouterConfig
{
    public string Listen { get; set; } = "http://127.0.0.1:8321";
    public string DefaultModel { get; set; } = "default";
    public ThreadPriority DefaultPriority { get; set; } = ThreadPriority.Normal();
    public bool AutoSwitch { get; set; } = true;
    public int SwitchCooldownMs { get; set; } = 15_000;
    public SchedulerConfig Scheduler { get; set; } = new();
    public IReadOnlyDictionary<string, ThreadDefinition> Threads { get; set; } =
        new Dictionary<string, ThreadDefinition>(StringComparer.Ordinal);
    /// <summary>How many live event-feed subscriptions the router will hold. Each one is a stream a
    /// client keeps open; the cap is what stops the admin feed from being the thing that runs the
    /// process out of handles. Feeds beyond it are refused with 503.</summary>
    public int MaxEventSubscribers { get; set; } = 256;

    public IReadOnlyDictionary<string, EngineDefinition> Engines { get; set; } =
        new Dictionary<string, EngineDefinition>(StringComparer.Ordinal);
    /// <summary>Optional per-engine HTTP handler factory (test harnesses, custom transports).
    /// Production config leaves it null; engines then use a default HttpClient.</summary>
    public Func<EngineDefinition, HttpMessageHandler>? HttpHandlerFactory { get; set; }
}

public sealed class RouterConfigException(string message) : Exception(message);
