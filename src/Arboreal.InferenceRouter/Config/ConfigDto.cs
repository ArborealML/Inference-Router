using System.Text.Json.Serialization;
using Tomlyn.Serialization;

namespace Arboreal.InferenceRouter;

/// <summary>Config DTOs mirroring the config schema 1:1. Explicit wire names via
/// JsonPropertyName (STJ JSON context) and TomlPropertyName (Tomlyn source-generated context);
/// dictionary keys (thread ids, engine names, model ids) stay as-is under both.</summary>
public sealed class ConfigDto
{
    [JsonPropertyName("listen")][TomlPropertyName("listen")] public string? Listen { get; set; }
    [JsonPropertyName("default_model")][TomlPropertyName("default_model")] public string? DefaultModel { get; set; }
    [JsonPropertyName("auto_switch")][TomlPropertyName("auto_switch")] public bool? AutoSwitch { get; set; }
    [JsonPropertyName("switch_cooldown_ms")][TomlPropertyName("switch_cooldown_ms")] public int? SwitchCooldownMs { get; set; }
    [JsonPropertyName("default_priority")][TomlPropertyName("default_priority")] public PriorityDto? DefaultPriority { get; set; }
    [JsonPropertyName("scheduler")][TomlPropertyName("scheduler")] public SchedulerDto? Scheduler { get; set; }
    [JsonPropertyName("threads")][TomlPropertyName("threads")] public Dictionary<string, ThreadDto>? Threads { get; set; }
    [JsonPropertyName("engine")][TomlPropertyName("engine")] public Dictionary<string, EngineDto>? Engines { get; set; }
}

public sealed class PriorityDto
{
    [JsonPropertyName("class")][TomlPropertyName("class")] public string? Class { get; set; }
    [JsonPropertyName("sub")][TomlPropertyName("sub")] public int? Sub { get; set; }
}

public sealed class SchedulerDto
{
    [JsonPropertyName("prefix_affinity_threshold_chars")][TomlPropertyName("prefix_affinity_threshold_chars")] public int? PrefixAffinityThresholdChars { get; set; }
    [JsonPropertyName("affinity_weight")][TomlPropertyName("affinity_weight")] public double? AffinityWeight { get; set; }
    [JsonPropertyName("prefill_cost_alpha")][TomlPropertyName("prefill_cost_alpha")] public double? PrefillCostAlpha { get; set; }
    [JsonPropertyName("max_queue_per_model")][TomlPropertyName("max_queue_per_model")] public int? MaxQueuePerModel { get; set; }
    [JsonPropertyName("max_inflight_per_engine")][TomlPropertyName("max_inflight_per_engine")] public int? MaxInflightPerEngine { get; set; }
    [JsonPropertyName("prefix_trie_max_nodes")][TomlPropertyName("prefix_trie_max_nodes")] public int? PrefixTrieMaxNodes { get; set; }
    [JsonPropertyName("chars_per_token_estimate")][TomlPropertyName("chars_per_token_estimate")] public double? CharsPerTokenEstimate { get; set; }
}

public sealed class ThreadDto
{
    [JsonPropertyName("priority")][TomlPropertyName("priority")] public PriorityDto? Priority { get; set; }
    [JsonPropertyName("weight")][TomlPropertyName("weight")] public double? Weight { get; set; }
    [JsonPropertyName("model_affinity")][TomlPropertyName("model_affinity")] public string? ModelAffinity { get; set; }
}

public sealed class EngineDto
{
    [JsonPropertyName("kind")][TomlPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("endpoint")][TomlPropertyName("endpoint")] public string? Endpoint { get; set; }
    [JsonPropertyName("api_key")][TomlPropertyName("api_key")] public string? ApiKey { get; set; }
    [JsonPropertyName("api_key_file")][TomlPropertyName("api_key_file")] public string? ApiKeyFile { get; set; }
    [JsonPropertyName("models_endpoint")][TomlPropertyName("models_endpoint")] public string? ModelsEndpoint { get; set; }
    [JsonPropertyName("loaded_requires_status_loaded")][TomlPropertyName("loaded_requires_status_loaded")] public bool? LoadedRequiresStatusLoaded { get; set; }
    [JsonPropertyName("remote")][TomlPropertyName("remote")] public bool? Remote { get; set; }
    [JsonPropertyName("health_probe")][TomlPropertyName("health_probe")] public HealthProbeDto? HealthProbe { get; set; }
    [JsonPropertyName("capabilities")][TomlPropertyName("capabilities")] public CapabilitiesDto? Capabilities { get; set; }
    [JsonPropertyName("catalog")][TomlPropertyName("catalog")] public Dictionary<string, ModelDto>? Catalog { get; set; }
}

public sealed class HealthProbeDto
{
    [JsonPropertyName("path")][TomlPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("expect")][TomlPropertyName("expect")] public string? Expect { get; set; }
    [JsonPropertyName("interval_ms")][TomlPropertyName("interval_ms")] public int? IntervalMs { get; set; }
    [JsonPropertyName("timeout_ms")][TomlPropertyName("timeout_ms")] public int? TimeoutMs { get; set; }
    [JsonPropertyName("context_path")][TomlPropertyName("context_path")] public string? ContextPath { get; set; }
    [JsonPropertyName("slots_path")][TomlPropertyName("slots_path")] public string? SlotsPath { get; set; }
}

public sealed class CapabilitiesDto
{
    [JsonPropertyName("vision")][TomlPropertyName("vision")] public bool? Vision { get; set; }
    [JsonPropertyName("suffix_cache")][TomlPropertyName("suffix_cache")] public bool? SuffixCache { get; set; }
    [JsonPropertyName("max_context")][TomlPropertyName("max_context")] public long? MaxContext { get; set; }
    [JsonPropertyName("max_slots")][TomlPropertyName("max_slots")] public int? MaxSlots { get; set; }
}

public sealed class ModelDto
{
    [JsonPropertyName("aliases")][TomlPropertyName("aliases")] public List<string>? Aliases { get; set; }
    [JsonPropertyName("profiles")][TomlPropertyName("profiles")] public List<string>? Profiles { get; set; }
    [JsonPropertyName("profile")][TomlPropertyName("profile")] public string? Profile { get; set; }
    [JsonPropertyName("default_profile")][TomlPropertyName("default_profile")] public string? DefaultProfile { get; set; }
    [JsonPropertyName("serving")][TomlPropertyName("serving")] public List<ServingDto>? Serving { get; set; }
    [JsonPropertyName("select")][TomlPropertyName("select")] public SelectDto? Select { get; set; }
}

/// <summary>One context-window tier a model can be served under, with the concurrency it costs.
/// Small windows buy more slots; a request needing more context queues behind a profile switch.</summary>
public sealed class ServingDto
{
    [JsonPropertyName("name")][TomlPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("max_context")][TomlPropertyName("max_context")] public long? MaxContext { get; set; }
    [JsonPropertyName("max_slots")][TomlPropertyName("max_slots")] public int? MaxSlots { get; set; }
}

public sealed class SelectDto
{
    [JsonPropertyName("exec")][TomlPropertyName("exec")] public string? Exec { get; set; }
    [JsonPropertyName("args")][TomlPropertyName("args")] public List<string>? Args { get; set; }
    [JsonPropertyName("wait_health")][TomlPropertyName("wait_health")] public bool? WaitHealth { get; set; }
    [JsonPropertyName("wait_timeout_ms")][TomlPropertyName("wait_timeout_ms")] public int? WaitTimeoutMs { get; set; }
}

/// <summary>TOML parser: Tomlyn's Roslyn source generator emits the typed (de)serializer —
/// no reflection, no dynamic code (Native AOT clean by construction).</summary>
[TomlSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[TomlSerializable(typeof(ConfigDto))]
[TomlSerializable(typeof(ThreadDto))]
[TomlSerializable(typeof(EngineDto))]
[TomlSerializable(typeof(ServingDto))]
internal sealed partial class ConfigTomlContext : TomlSerializerContext;

/// <summary>JSON twin parser (same DTOs, STJ source generation).</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ConfigDto))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
