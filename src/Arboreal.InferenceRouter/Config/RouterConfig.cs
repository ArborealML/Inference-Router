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

/// <summary>Health probe definition. Expect is a tiny comparison DSL: "path OP value" with
/// path = dotted JSON path, OP in { >, >=, ==, !=, <, <= }, value = number or string literal.</summary>
public sealed class HealthProbeDefinition
{
    public required string Path { get; init; } = "/props";
    public string? Expect { get; init; }
    public int IntervalMs { get; init; } = 2_000;
    public int TimeoutMs { get; init; } = 5_000;
}

public sealed class EngineCapabilities
{
    public bool Vision { get; set; }
    public bool SuffixCache { get; set; }
    public long MaxContext { get; set; }
    public int MaxSlots { get; set; } = 1;
}

/// <summary>One model the engine can serve (catalog), with switch command + aliases.</summary>
public sealed record EngineModelDefinition
{
    public required string Name { get; init; }
    public string[] Aliases { get; init; } = [];
    public string[] Profiles { get; init; } = [];
    public LifecycleCommand? Select { get; init; }
    public string? Profile { get; init; }             // default profile for switch
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
        new Dictionary<string, EngineModelDefinition>();
    public bool Remote { get; init; }                 // remote peer: no lifecycle commands
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
        new Dictionary<string, ThreadDefinition>();
    public IReadOnlyDictionary<string, EngineDefinition> Engines { get; set; } =
        new Dictionary<string, EngineDefinition>();
}

public sealed class RouterConfigException(string message) : Exception(message);
