using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

/// <summary>The subset of the Docker Engine HTTP API this provider uses. Property names are pinned
/// explicitly because the daemon's casing is its own (and mixed), and a naming policy that happens to
/// match today would silently break on a daemon update. Serialized only through the source-generated
/// context below: no reflection, so the Native AOT publish stays trim-clean.</summary>
public sealed class DockerVersionResponse
{
    [JsonPropertyName("ApiVersion")] public string? ApiVersion { get; set; }
    [JsonPropertyName("Os")] public string? Os { get; set; }
}

public sealed class DockerImageInspectResponse
{
    [JsonPropertyName("Id")] public string? Id { get; set; }
    [JsonPropertyName("RepoDigests")] public string[]? RepoDigests { get; set; }
    [JsonPropertyName("Os")] public string? Os { get; set; }
    [JsonPropertyName("Architecture")] public string? Architecture { get; set; }
    [JsonPropertyName("Variant")] public string? Variant { get; set; }
    [JsonPropertyName("Config")] public DockerImageLaunchDefaults? Config { get; set; }
}

public sealed class DockerImageLaunchDefaults
{
    [JsonPropertyName("Entrypoint")] public string[]? Entrypoint { get; set; }
    [JsonPropertyName("Cmd")] public string[]? Cmd { get; set; }
}

public sealed class DockerContainerSummary
{
    [JsonPropertyName("Id")] public string? Id { get; set; }
    [JsonPropertyName("Image")] public string? Image { get; set; }
    [JsonPropertyName("ImageID")] public string? ImageId { get; set; }
    [JsonPropertyName("State")] public string? State { get; set; }
    [JsonPropertyName("Status")] public string? Status { get; set; }
    [JsonPropertyName("Labels")] public Dictionary<string, string>? Labels { get; set; }
}

internal sealed class DockerContainerInspectResponse
{
    [JsonPropertyName("Id")] public string? Id { get; set; }
    [JsonPropertyName("Config")] public DockerContainerInspectConfig? Config { get; set; }
    [JsonPropertyName("State")] public DockerContainerInspectState? State { get; set; }
}

internal sealed class DockerContainerInspectConfig
{
    [JsonPropertyName("Labels")] public Dictionary<string, string>? Labels { get; set; }
}

internal sealed class DockerContainerInspectState
{
    [JsonPropertyName("Status")] public string? Status { get; set; }
    [JsonPropertyName("Running")] public bool? Running { get; set; }
    [JsonPropertyName("Restarting")] public bool? Restarting { get; set; }
    [JsonPropertyName("Paused")] public bool? Paused { get; set; }
    [JsonPropertyName("Dead")] public bool? Dead { get; set; }
}

internal sealed class DockerObservationResponse
{
    [JsonPropertyName("Name")] public string? Name { get; set; }
    [JsonPropertyName("Id")] public string? Id { get; set; }
    [JsonPropertyName("Image")] public string? Image { get; set; }
    [JsonPropertyName("RestartCount")] public long? RestartCount { get; set; }
    [JsonPropertyName("Config")] public DockerContainerInspectConfig? Config { get; set; }
    [JsonPropertyName("HostConfig")] public DockerProcessHostConfig? HostConfig { get; set; }
    [JsonPropertyName("State")] public DockerObservationState? State { get; set; }
}

internal sealed class DockerProcessHostConfig
{
    [JsonPropertyName("PidMode")] public string? PidMode { get; set; }
    [JsonPropertyName("Init")] public bool? Init { get; set; }
}

internal sealed class DockerObservationState
{
    [JsonPropertyName("Status")] public string? Status { get; set; }
    [JsonPropertyName("Running")] public bool? Running { get; set; }
    [JsonPropertyName("Paused")] public bool? Paused { get; set; }
    [JsonPropertyName("Restarting")] public bool? Restarting { get; set; }
    [JsonPropertyName("Dead")] public bool? Dead { get; set; }
    [JsonPropertyName("StartedAt")] public string? StartedAt { get; set; }
    [JsonPropertyName("Pid")] public int? Pid { get; set; }
}

public sealed class DockerCreateRequest
{
    [JsonPropertyName("Image")] public required string Image { get; set; }
    [JsonPropertyName("Entrypoint")] public string[]? Entrypoint { get; set; }
    [JsonPropertyName("Cmd")] public string[]? Cmd { get; set; }
    [JsonPropertyName("Env")] public string[]? Env { get; set; }
    [JsonPropertyName("Healthcheck")] public DockerCreateHealthcheck? Healthcheck { get; set; }
    [JsonPropertyName("Labels")] public required Dictionary<string, string> Labels { get; set; }
    [JsonPropertyName("StopTimeout")] public int? StopTimeoutSeconds { get; set; }
    [JsonPropertyName("ExposedPorts")] public Dictionary<string, DockerExposedPort>? ExposedPorts { get; set; }
    [JsonPropertyName("HostConfig")] public DockerHostConfig? HostConfig { get; set; }
}

// Docker represents each exposed port as an empty object.
public sealed class DockerExposedPort
{
}

public sealed class DockerCreateHealthcheck
{
    [JsonPropertyName("Test")] public required string[] Test { get; init; }
    [JsonPropertyName("Interval")] public long? Interval { get; init; }
    [JsonPropertyName("StartPeriod")] public long? StartPeriod { get; init; }
    [JsonPropertyName("Timeout")] public long? Timeout { get; init; }
    [JsonPropertyName("Retries")] public int? Retries { get; init; }
}

public sealed class DockerHostConfig
{
    [JsonPropertyName("PortBindings")] public Dictionary<string, DockerPortBinding[]>? PortBindings { get; set; }
    [JsonPropertyName("NetworkMode")] public string? NetworkMode { get; set; }
    [JsonPropertyName("IpcMode")] public string? IpcMode { get; set; }
    [JsonPropertyName("Init")] public bool? Init { get; set; }
    [JsonPropertyName("RestartPolicy")] public DockerRestartPolicy? RestartPolicy { get; set; }
    [JsonPropertyName("LogConfig")] public DockerLogConfig? LogConfig { get; set; }
    [JsonPropertyName("Devices")] public DockerDeviceMapping[]? Devices { get; set; }
    [JsonPropertyName("DeviceRequests")] public DockerDeviceRequest[]? DeviceRequests { get; set; }
    [JsonPropertyName("Mounts")] public DockerMount[]? Mounts { get; set; }
    [JsonPropertyName("NanoCpus")] public long? NanoCpus { get; set; }
    [JsonPropertyName("Memory")] public long? Memory { get; set; }
    [JsonPropertyName("MemorySwap")] public long? MemorySwap { get; set; }
    [JsonPropertyName("ShmSize")] public long? ShmSize { get; set; }
    [JsonPropertyName("Ulimits")] public DockerLimit[]? Ulimits { get; set; }
}

public sealed record DockerRestartPolicy(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("MaximumRetryCount")] int MaximumRetryCount);

public sealed record DockerLogConfig(
    [property: JsonPropertyName("Type")] string Type,
    [property: JsonPropertyName("Config")] Dictionary<string, string> Config);

public sealed record DockerDeviceMapping(
    [property: JsonPropertyName("PathOnHost")] string HostPath,
    [property: JsonPropertyName("PathInContainer")] string ContainerPath,
    [property: JsonPropertyName("CgroupPermissions")] string Permissions);

public sealed record DockerDeviceRequest(
    [property: JsonPropertyName("Driver")] string Driver,
    [property: JsonPropertyName("Count")] int Count,
    [property: JsonPropertyName("DeviceIDs")] string[]? DeviceIds,
    [property: JsonPropertyName("Capabilities")] string[][] Capabilities);

public sealed record DockerMount(
    [property: JsonPropertyName("Type")] string Type,
    [property: JsonPropertyName("Source")] string Source,
    [property: JsonPropertyName("Target")] string Target,
    [property: JsonPropertyName("ReadOnly")] bool ReadOnly);

public sealed record DockerLimit(
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Soft")] long Soft,
    [property: JsonPropertyName("Hard")] long Hard);

public sealed class DockerPortBinding
{
    /// <summary>Empty HostIp is Docker's shorthand for "every interface on the host", which for an
    /// unauthenticated inference backend is a public port. The provider always names one.</summary>
    [JsonPropertyName("HostIp")] public required string HostIp { get; set; }
    [JsonPropertyName("HostPort")] public required string HostPort { get; set; }
}

/// <summary>The container-list filter. Built through the source-generated context rather than by hand,
/// because a config-supplied router id or engine name inside pasted JSON is an injection point.</summary>
public sealed class DockerListFilter
{
    [JsonPropertyName("label")] public required string[] Label { get; set; }
}

public sealed class DockerCreateResponse
{
    [JsonPropertyName("Id")] public string? Id { get; set; }
    [JsonPropertyName("Warnings")] public string[]? Warnings { get; set; }
}

internal sealed class DockerDiagnosticContainerResponse
{
    [JsonPropertyName("Id")] public string? Id { get; set; }
    [JsonPropertyName("Config")] public DockerDiagnosticContainerConfig? Config { get; set; }
}

internal sealed class DockerDiagnosticContainerConfig
{
    [JsonPropertyName("Labels")] public Dictionary<string, string>? Labels { get; set; }
    [JsonPropertyName("Tty")] public bool? Tty { get; set; }
}

internal sealed record DockerExecCreateRequest(
    [property: JsonPropertyName("Cmd")] string[] Command)
{
    [JsonPropertyName("AttachStdout")] public bool AttachStdout { get; } = true;
    [JsonPropertyName("AttachStderr")] public bool AttachStderr { get; } = true;
    [JsonPropertyName("AttachStdin")] public bool AttachStdin { get; }
    [JsonPropertyName("Tty")] public bool Tty { get; }
    [JsonPropertyName("Privileged")] public bool Privileged { get; }
}

internal sealed class DockerExecStartRequest
{
    [JsonPropertyName("Detach")] public bool Detach { get; }
    [JsonPropertyName("Tty")] public bool Tty { get; }
}

internal sealed class DockerExecInspectResponse
{
    [JsonPropertyName("ID")] public string? Id { get; set; }
    [JsonPropertyName("ContainerID")] public string? ContainerId { get; set; }
    [JsonPropertyName("Running")] public bool? Running { get; set; }
    [JsonPropertyName("ExitCode")] public int? ExitCode { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DockerVersionResponse))]
[JsonSerializable(typeof(DockerImageInspectResponse))]
[JsonSerializable(typeof(List<DockerContainerSummary>))]
[JsonSerializable(typeof(DockerContainerInspectResponse))]
[JsonSerializable(typeof(DockerObservationResponse))]
[JsonSerializable(typeof(DockerCreateRequest))]
[JsonSerializable(typeof(DockerCreateResponse))]
[JsonSerializable(typeof(DockerDiagnosticContainerResponse))]
[JsonSerializable(typeof(DockerExecCreateRequest))]
[JsonSerializable(typeof(DockerExecStartRequest))]
[JsonSerializable(typeof(DockerExecInspectResponse))]
[JsonSerializable(typeof(DockerListFilter))]
internal sealed partial class DockerJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(AllowDuplicateProperties = false)]
[JsonSerializable(typeof(DockerObservationResponse))]
internal sealed partial class DockerProcessObservationJsonContext : JsonSerializerContext
{
}
