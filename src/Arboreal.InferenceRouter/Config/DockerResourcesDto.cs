using System.Text.Json.Serialization;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Arboreal.InferenceRouter;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DockerTargetDto
{
    private string[]? _command;
    private string[]? _env;
    private DockerBindDto[]? _bindMounts;
    private long? _memoryBytes;
    private long? _memorySwapBytes;
    private long? _shmSizeBytes;
    private DockerHealthcheckDto? _healthcheck;
    private DockerTargetGpuDto? _gpu;

    [JsonPropertyName("command")][TomlPropertyName("command")]
    public string[]? Command
    {
        get => _command;
        set
        {
            if (_command is not null) throw new RouterConfigException("duplicate serving-tier docker.command");
            _command = value ?? throw new RouterConfigException("serving-tier docker.command cannot be null");
        }
    }

    [JsonPropertyName("env")][TomlPropertyName("env")]
    public string[]? Env
    {
        get => _env;
        set
        {
            if (_env is not null) throw new RouterConfigException("duplicate serving-tier docker.env");
            _env = value ?? throw new RouterConfigException("serving-tier docker.env cannot be null");
        }
    }

    [JsonPropertyName("bind_mounts")][TomlPropertyName("bind_mounts")]
    public DockerBindDto[]? BindMounts
    {
        get => _bindMounts;
        set
        {
            if (_bindMounts is not null) throw new RouterConfigException("serving-tier docker.bind_mounts appears more than once");
            _bindMounts = value ?? throw new RouterConfigException("serving-tier docker.bind_mounts cannot be null");
        }
    }

    [JsonPropertyName("memory_bytes")][TomlPropertyName("memory_bytes")]
    public long? MemoryBytes
    {
        get => _memoryBytes;
        set
        {
            if (_memoryBytes.HasValue) throw new RouterConfigException("duplicate serving-tier docker.memory_bytes");
            _memoryBytes = value ?? throw new RouterConfigException("serving-tier docker.memory_bytes cannot be null");
        }
    }

    [JsonPropertyName("memory_swap_bytes")][TomlPropertyName("memory_swap_bytes")]
    public long? MemorySwapBytes
    {
        get => _memorySwapBytes;
        set
        {
            if (_memorySwapBytes.HasValue) throw new RouterConfigException("duplicate serving-tier docker.memory_swap_bytes");
            _memorySwapBytes = value ?? throw new RouterConfigException("serving-tier docker.memory_swap_bytes cannot be null");
        }
    }

    [JsonPropertyName("shm_size_bytes")][TomlPropertyName("shm_size_bytes")]
    public long? ShmSizeBytes
    {
        get => _shmSizeBytes;
        set
        {
            if (_shmSizeBytes.HasValue) throw new RouterConfigException("duplicate serving-tier docker.shm_size_bytes");
            _shmSizeBytes = value ?? throw new RouterConfigException("serving-tier docker.shm_size_bytes cannot be null");
        }
    }

    [JsonPropertyName("gpu")][TomlPropertyName("gpu")]
    public DockerTargetGpuDto? Gpu
    {
        get => _gpu;
        set
        {
            if (_gpu is not null) throw new RouterConfigException("duplicate serving-tier docker.gpu");
            _gpu = value ?? throw new RouterConfigException("serving-tier docker.gpu cannot be null");
        }
    }

    [JsonPropertyName("healthcheck")][TomlPropertyName("healthcheck")]
    public DockerHealthcheckDto? Healthcheck
    {
        get => _healthcheck;
        set
        {
            if (_healthcheck is not null) throw new RouterConfigException("duplicate serving-tier docker.healthcheck");
            _healthcheck = value ?? throw new RouterConfigException("serving-tier docker.healthcheck cannot be null");
        }
    }

    [JsonIgnore, TomlIgnore(Condition = TomlIgnoreCondition.Never), TomlExtensionData]
    public TomlTable? Unknown { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DockerTargetGpuDto
{
    [JsonPropertyName("device_ids")][TomlPropertyName("device_ids")] public string[]? DeviceIds { get; set; }

    [JsonIgnore, TomlIgnore(Condition = TomlIgnoreCondition.Never), TomlExtensionData]
    public TomlTable? Unknown { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DockerHealthcheckDto
{
    private string? _command;
    private long? _intervalMs;
    private long? _startPeriodMs;
    private long? _timeoutMs;
    private int? _retries;

    [JsonPropertyName("command")][TomlPropertyName("command")]
    public string? Command
    {
        get => _command;
        set
        {
            if (_command is not null) throw new RouterConfigException("duplicate docker.healthcheck.command");
            _command = value ?? throw new RouterConfigException("docker.healthcheck.command cannot be null");
        }
    }

    [JsonPropertyName("interval_ms")][TomlPropertyName("interval_ms")]
    public long? IntervalMs
    {
        get => _intervalMs;
        set
        {
            if (_intervalMs.HasValue) throw new RouterConfigException("duplicate docker.healthcheck.interval_ms");
            _intervalMs = value ?? throw new RouterConfigException("docker.healthcheck.interval_ms cannot be null");
        }
    }

    [JsonPropertyName("start_period_ms")][TomlPropertyName("start_period_ms")]
    public long? StartPeriodMs
    {
        get => _startPeriodMs;
        set
        {
            if (_startPeriodMs.HasValue) throw new RouterConfigException("duplicate docker.healthcheck.start_period_ms");
            _startPeriodMs = value ?? throw new RouterConfigException("docker.healthcheck.start_period_ms cannot be null");
        }
    }

    [JsonPropertyName("timeout_ms")][TomlPropertyName("timeout_ms")]
    public long? TimeoutMs
    {
        get => _timeoutMs;
        set
        {
            if (_timeoutMs.HasValue) throw new RouterConfigException("duplicate docker.healthcheck.timeout_ms");
            _timeoutMs = value ?? throw new RouterConfigException("docker.healthcheck.timeout_ms cannot be null");
        }
    }

    [JsonPropertyName("retries")][TomlPropertyName("retries")]
    public int? Retries
    {
        get => _retries;
        set
        {
            if (_retries.HasValue) throw new RouterConfigException("duplicate docker.healthcheck.retries");
            _retries = value ?? throw new RouterConfigException("docker.healthcheck.retries cannot be null");
        }
    }

    [JsonIgnore, TomlIgnore(Condition = TomlIgnoreCondition.Never), TomlExtensionData]
    public TomlTable? Unknown { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DockerResourcesDto
{
    [JsonPropertyName("gpu")][TomlPropertyName("gpu")] public DockerGpuDto? Gpu { get; set; }
    [JsonPropertyName("device_paths")][TomlPropertyName("device_paths")] public string[]? DevicePaths { get; set; }
    [JsonPropertyName("bind_mounts")][TomlPropertyName("bind_mounts")] public DockerBindDto[]? BindMounts { get; set; }
    [JsonPropertyName("nano_cpus")][TomlPropertyName("nano_cpus")] public long? NanoCpus { get; set; }
    [JsonPropertyName("memory_bytes")][TomlPropertyName("memory_bytes")] public long? MemoryBytes { get; set; }
    [JsonPropertyName("memory_swap_bytes")][TomlPropertyName("memory_swap_bytes")] public long? MemorySwapBytes { get; set; }
    [JsonPropertyName("shm_size_bytes")][TomlPropertyName("shm_size_bytes")] public long? ShmSizeBytes { get; set; }
    [JsonPropertyName("ulimits")][TomlPropertyName("ulimits")] public DockerUlimitDto[]? Ulimits { get; set; }

    [JsonIgnore, TomlIgnore(Condition = TomlIgnoreCondition.Never), TomlExtensionData]
    public TomlTable? Unknown { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DockerGpuDto
{
    [JsonPropertyName("all")][TomlPropertyName("all")] public bool? All { get; set; }
    [JsonPropertyName("device_ids")][TomlPropertyName("device_ids")] public string[]? DeviceIds { get; set; }

    [JsonIgnore, TomlIgnore(Condition = TomlIgnoreCondition.Never), TomlExtensionData]
    public TomlTable? Unknown { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DockerBindDto
{
    private string? _nestedUnder;

    [JsonPropertyName("source")][TomlPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("target")][TomlPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("read_only")][TomlPropertyName("read_only")] public bool? ReadOnly { get; set; }
    [JsonPropertyName("nested_under")][TomlPropertyName("nested_under")]
    public string? NestedUnder
    {
        get => _nestedUnder;
        set
        {
            if (_nestedUnder is not null) throw new RouterConfigException("duplicate bind_mounts nested_under");
            _nestedUnder = value ?? throw new RouterConfigException("bind_mounts nested_under cannot be null");
        }
    }

    [JsonIgnore, TomlIgnore(Condition = TomlIgnoreCondition.Never), TomlExtensionData]
    public TomlTable? Unknown { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DockerUlimitDto
{
    [JsonPropertyName("name")][TomlPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("soft")][TomlPropertyName("soft")] public long? Soft { get; set; }
    [JsonPropertyName("hard")][TomlPropertyName("hard")] public long? Hard { get; set; }

    [JsonIgnore, TomlIgnore(Condition = TomlIgnoreCondition.Never), TomlExtensionData]
    public TomlTable? Unknown { get; set; }
}
