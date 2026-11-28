using System.Text.Json.Serialization;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Arboreal.InferenceRouter;

/// <summary>Config DTOs mirroring the config schema 1:1. Explicit wire names via
/// JsonPropertyName (STJ JSON context) and TomlPropertyName (Tomlyn source-generated context);
/// dictionary keys (thread ids, engine names, model ids) stay as-is under both.</summary>
public sealed class ConfigDto
{
    [JsonPropertyName("owned_private_policy")][TomlPropertyName("owned_private_policy")] public bool? OwnedPrivatePolicy { get; set; }
    [JsonPropertyName("listen")][TomlPropertyName("listen")] public string? Listen { get; set; }
    [JsonPropertyName("default_model")][TomlPropertyName("default_model")] public string? DefaultModel { get; set; }
    [JsonPropertyName("auto_switch")][TomlPropertyName("auto_switch")] public bool? AutoSwitch { get; set; }
    [JsonPropertyName("switch_cooldown_ms")][TomlPropertyName("switch_cooldown_ms")] public int? SwitchCooldownMs { get; set; }
    [JsonPropertyName("max_event_subscribers")][TomlPropertyName("max_event_subscribers")] public int? MaxEventSubscribers { get; set; }
    [JsonPropertyName("max_request_body_bytes")][TomlPropertyName("max_request_body_bytes")] public int? MaxRequestBodyBytes { get; set; }
    [JsonPropertyName("stream_buffer_limit_bytes")][TomlPropertyName("stream_buffer_limit_bytes")] public int? StreamBufferLimitBytes { get; set; }
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
    [JsonPropertyName("switch_wait_timeout_ms")][TomlPropertyName("switch_wait_timeout_ms")] public int? SwitchWaitTimeoutMs { get; set; }
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
    [JsonPropertyName("models_context_window_path")][TomlPropertyName("models_context_window_path")]
    public string? ModelsContextWindowPath { get; set; }
    [JsonPropertyName("loaded_requires_status_loaded")][TomlPropertyName("loaded_requires_status_loaded")] public bool? LoadedRequiresStatusLoaded { get; set; }
    [JsonPropertyName("response_timeout_ms")][TomlPropertyName("response_timeout_ms")] public int? ResponseTimeoutMs { get; set; }
    [JsonPropertyName("stream_idle_timeout_ms")][TomlPropertyName("stream_idle_timeout_ms")] public int? StreamIdleTimeoutMs { get; set; }
    [JsonPropertyName("remote")][TomlPropertyName("remote")] public bool? Remote { get; set; }
    [JsonPropertyName("lifecycle_owner")][TomlPropertyName("lifecycle_owner")] public string? LifecycleOwner { get; set; }
    [JsonPropertyName("lifecycle_group")][TomlPropertyName("lifecycle_group")] public string? LifecycleGroup { get; set; }
    [JsonPropertyName("health_probe")][TomlPropertyName("health_probe")] public HealthProbeDto? HealthProbe { get; set; }
    [JsonPropertyName("capabilities")][TomlPropertyName("capabilities")] public CapabilitiesDto? Capabilities { get; set; }
    [JsonPropertyName("catalog")][TomlPropertyName("catalog")] public Dictionary<string, ModelDto>? Catalog { get; set; }
    [JsonPropertyName("lifecycle")][TomlPropertyName("lifecycle")] public string? Lifecycle { get; set; }
    [JsonPropertyName("docker")][TomlPropertyName("docker")] public DockerDto? Docker { get; set; }
}

public sealed class DockerDto : ITomlOnDeserialized
{
    private string? _imageId;
    private string? _imageRepoDigest;
    private string? _imagePlatform;
    private string? _entrypoint;
    private string? _ipcMode;
    private bool? _init;
    private string? _restart;
    private DockerLogRotationDto? _logRotation;
    internal bool HasContainerPolicy => Init is not null || Restart is not null || LogRotation is not null;
    // JSON null is an explicit value, so it must not take the legacy all-omitted path.
    internal bool HasImageIdentity { get; private set; }

    [JsonPropertyName("image")][TomlPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("image_id")][TomlPropertyName("image_id")]
    public string? ImageId
    {
        get => _imageId;
        set { _imageId = value; HasImageIdentity = true; }
    }
    [JsonPropertyName("image_repo_digest")][TomlPropertyName("image_repo_digest")]
    public string? ImageRepoDigest
    {
        get => _imageRepoDigest;
        set { _imageRepoDigest = value; HasImageIdentity = true; }
    }
    [JsonPropertyName("image_platform")][TomlPropertyName("image_platform")]
    public string? ImagePlatform
    {
        get => _imagePlatform;
        set { _imagePlatform = value; HasImageIdentity = true; }
    }
    [JsonPropertyName("socket")][TomlPropertyName("socket")] public string? Socket { get; set; }
    [JsonPropertyName("api_version")][TomlPropertyName("api_version")] public string? ApiVersion { get; set; }
    [JsonPropertyName("router_id")][TomlPropertyName("router_id")] public string? RouterId { get; set; }
    [JsonPropertyName("resources")][TomlPropertyName("resources")] public DockerResourcesDto? Resources { get; set; }
    [JsonPropertyName("entrypoint")][TomlPropertyName("entrypoint")]
    public string? Entrypoint
    {
        get => _entrypoint;
        set
        {
            if (_entrypoint is not null) throw new RouterConfigException("duplicate docker.entrypoint");
            _entrypoint = value ?? throw new RouterConfigException("docker.entrypoint cannot be null");
        }
    }
    [JsonPropertyName("ipc_mode")][TomlPropertyName("ipc_mode")]
    public string? IpcMode
    {
        get => _ipcMode;
        set
        {
            if (_ipcMode is not null) throw new RouterConfigException("duplicate docker.ipc_mode");
            _ipcMode = value ?? throw new RouterConfigException("docker.ipc_mode cannot be null");
        }
    }
    [JsonPropertyName("init")][TomlPropertyName("init")]
    public bool? Init
    {
        get => _init;
        set
        {
            if (_init is not null) throw new RouterConfigException("duplicate docker.init");
            _init = value ?? throw new RouterConfigException("docker.init cannot be null");
        }
    }
    [JsonPropertyName("restart")][TomlPropertyName("restart")]
    public string? Restart
    {
        get => _restart;
        set
        {
            if (_restart is not null) throw new RouterConfigException("duplicate docker.restart");
            _restart = value ?? throw new RouterConfigException("docker.restart cannot be null");
        }
    }
    [JsonPropertyName("log_rotation")][TomlPropertyName("log_rotation")]
    public DockerLogRotationDto? LogRotation
    {
        get => _logRotation;
        set
        {
            if (_logRotation is not null) throw new RouterConfigException("duplicate docker.log_rotation");
            _logRotation = value ?? throw new RouterConfigException("docker.log_rotation cannot be null");
        }
    }
    [JsonPropertyName("command")][TomlPropertyName("command")] public string[]? Command { get; set; }
    [JsonPropertyName("env")][TomlPropertyName("env")] public string[]? Env { get; set; }
    [JsonPropertyName("stop_timeout_ms")][TomlPropertyName("stop_timeout_ms")] public int? StopTimeoutMs { get; set; }
    [JsonPropertyName("host_port")][TomlPropertyName("host_port")] public int? HostPort { get; set; }
    [JsonPropertyName("host_ip")][TomlPropertyName("host_ip")] public string? HostIp { get; set; }
    [JsonPropertyName("remove_stopped")][TomlPropertyName("remove_stopped")] public bool? RemoveStopped { get; set; }
    [JsonPropertyName("container_port")][TomlPropertyName("container_port")] public int? ContainerPort { get; set; }
    [JsonPropertyName("network_mode")][TomlPropertyName("network_mode")] public string? NetworkMode { get; set; }
    [JsonPropertyName("wait_health")][TomlPropertyName("wait_health")] public bool? WaitHealth { get; set; }
    [JsonPropertyName("wait_timeout_ms")][TomlPropertyName("wait_timeout_ms")] public int? WaitTimeoutMs { get; set; }
    [JsonPropertyName("call_timeout_ms")][TomlPropertyName("call_timeout_ms")] public int? CallTimeoutMs { get; set; }

    [JsonIgnore, TomlIgnore(Condition = TomlIgnoreCondition.Never), TomlExtensionData]
    public TomlTable? UnmappedToml { get; set; }

    // Inspect each decoded Docker table before a case variant could replace it on the engine DTO.
    void ITomlOnDeserialized.OnTomlDeserialized() => RejectMisplacedHealthcheck("docker");

    internal void RejectMisplacedHealthcheck(string path)
    {
        if (UnmappedToml?.Keys.Any(key => key.Equals("healthcheck", StringComparison.OrdinalIgnoreCase)) == true)
            throw new RouterConfigException($"{path}.healthcheck is only supported inside a serving-tier docker block");
    }
}

public sealed class DockerLogRotationDto
{
    [JsonPropertyName("max_size")][TomlPropertyName("max_size")] public string? MaxSize { get; set; }
    [JsonPropertyName("max_files")][TomlPropertyName("max_files")] public int? MaxFiles { get; set; }
}

public sealed class HealthProbeDto
{
    [JsonPropertyName("path")][TomlPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("expect")][TomlPropertyName("expect")] public string? Expect { get; set; }
    [JsonPropertyName("interval_ms")][TomlPropertyName("interval_ms")] public int? IntervalMs { get; set; }
    [JsonPropertyName("timeout_ms")][TomlPropertyName("timeout_ms")] public int? TimeoutMs { get; set; }
    [JsonPropertyName("context_path")][TomlPropertyName("context_path")] public string? ContextPath { get; set; }
    [JsonPropertyName("slots_path")][TomlPropertyName("slots_path")] public string? SlotsPath { get; set; }
    [JsonPropertyName("slots_source")][TomlPropertyName("slots_source")] public string? SlotsSource { get; set; }
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
    private DockerTargetDto? _docker;
    private string[]? _aliases;

    [JsonPropertyName("name")][TomlPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("max_context")][TomlPropertyName("max_context")] public long? MaxContext { get; set; }
    [JsonPropertyName("max_slots")][TomlPropertyName("max_slots")] public int? MaxSlots { get; set; }
    [JsonPropertyName("aliases")][TomlPropertyName("aliases")]
    public string[]? Aliases
    {
        get => _aliases;
        set
        {
            if (_aliases is not null) throw new RouterConfigException("duplicate serving-tier aliases");
            _aliases = value ?? throw new RouterConfigException("serving-tier aliases cannot be null");
        }
    }

    [JsonPropertyName("docker")][TomlPropertyName("docker")]
    public DockerTargetDto? Docker
    {
        get => _docker;
        set
        {
            if (_docker is not null) throw new RouterConfigException("duplicate serving-tier docker");
            _docker = value ?? throw new RouterConfigException("serving-tier docker cannot be null");
        }
    }
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

[TomlSerializable(typeof(TomlTable))]
internal sealed partial class ConfigTomlDocumentContext : TomlSerializerContext;

/// <summary>JSON twin parser (same DTOs, STJ source generation).</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ConfigDto))]
[JsonSerializable(typeof(DockerDto))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
