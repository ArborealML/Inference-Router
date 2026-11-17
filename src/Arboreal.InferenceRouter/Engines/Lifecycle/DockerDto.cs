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

public sealed class DockerContainerSummary
{
    [JsonPropertyName("Id")] public string? Id { get; set; }
    [JsonPropertyName("Image")] public string? Image { get; set; }
    [JsonPropertyName("State")] public string? State { get; set; }
    [JsonPropertyName("Status")] public string? Status { get; set; }
    [JsonPropertyName("Labels")] public Dictionary<string, string>? Labels { get; set; }
}

public sealed class DockerCreateRequest
{
    [JsonPropertyName("Image")] public required string Image { get; set; }
    [JsonPropertyName("Cmd")] public string[]? Cmd { get; set; }
    [JsonPropertyName("Env")] public string[]? Env { get; set; }
    [JsonPropertyName("Labels")] public required Dictionary<string, string> Labels { get; set; }
    [JsonPropertyName("StopTimeout")] public int? StopTimeoutSeconds { get; set; }
    [JsonPropertyName("HostConfig")] public DockerHostConfig? HostConfig { get; set; }
}

public sealed class DockerHostConfig
{
    [JsonPropertyName("PortBindings")] public Dictionary<string, DockerPortBinding[]>? PortBindings { get; set; }
    [JsonPropertyName("NetworkMode")] public string? NetworkMode { get; set; }
}

public sealed class DockerPortBinding
{
    [JsonPropertyName("HostPort")] public required string HostPort { get; set; }
}

public sealed class DockerCreateResponse
{
    [JsonPropertyName("Id")] public string? Id { get; set; }
    [JsonPropertyName("Warnings")] public string[]? Warnings { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DockerVersionResponse))]
[JsonSerializable(typeof(List<DockerContainerSummary>))]
[JsonSerializable(typeof(DockerCreateRequest))]
[JsonSerializable(typeof(DockerCreateResponse))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal sealed partial class DockerJsonContext : JsonSerializerContext
{
}
