using System.Text.Json.Serialization;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Arboreal.InferenceRouter;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DockerTargetDto
{
    private string[]? _env;
    private DockerBindDto[]? _bindMounts;

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
    [JsonPropertyName("source")][TomlPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("target")][TomlPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("read_only")][TomlPropertyName("read_only")] public bool? ReadOnly { get; set; }

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
