using System.Text.Json.Serialization;
using Tomlyn.Serialization;

namespace Arboreal.InferenceRouter;

public sealed class DirectChatProviderDto
{
    [JsonPropertyName("enabled")][TomlPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("protocol")][TomlPropertyName("protocol")] public string? Protocol { get; set; }
    [JsonPropertyName("origin")][TomlPropertyName("origin")] public string? Origin { get; set; }
    [JsonPropertyName("chat_path")][TomlPropertyName("chat_path")] public string? ChatPath { get; set; }
    [JsonPropertyName("account")][TomlPropertyName("account")] public string? Account { get; set; }
    [JsonPropertyName("credential_ref")][TomlPropertyName("credential_ref")] public string? CredentialRef { get; set; }
    [JsonPropertyName("allows_streaming")][TomlPropertyName("allows_streaming")] public bool? AllowsStreaming { get; set; }
    [JsonPropertyName("allows_tools")][TomlPropertyName("allows_tools")] public bool? AllowsTools { get; set; }
    [JsonPropertyName("allows_reasoning")][TomlPropertyName("allows_reasoning")] public bool? AllowsReasoning { get; set; }
    [JsonPropertyName("backup_parameter")][TomlPropertyName("backup_parameter")] public string? BackupParameter { get; set; }
    [JsonPropertyName("input_price_microusd_per_million")][TomlPropertyName("input_price_microusd_per_million")]
    public long? InputPrice { get; set; }
    [JsonPropertyName("output_price_microusd_per_million")][TomlPropertyName("output_price_microusd_per_million")]
    public long? OutputPrice { get; set; }
    [JsonPropertyName("maximum_attempt_microusd")][TomlPropertyName("maximum_attempt_microusd")] public long? AttemptCap { get; set; }
    [JsonPropertyName("first_output_ms")][TomlPropertyName("first_output_ms")] public int? FirstOutputMs { get; set; }
    [JsonPropertyName("semantic_idle_ms")][TomlPropertyName("semantic_idle_ms")] public int? SemanticIdleMs { get; set; }
    [JsonPropertyName("maximum_stream_ms")][TomlPropertyName("maximum_stream_ms")] public int? MaximumStreamMs { get; set; }
    [JsonPropertyName("identity_buffer_bytes")][TomlPropertyName("identity_buffer_bytes")] public int? IdentityBufferBytes { get; set; }
    [JsonPropertyName("response_body_bytes")][TomlPropertyName("response_body_bytes")] public int? ResponseBodyBytes { get; set; }
    [JsonPropertyName("model")][TomlPropertyName("model")] public Dictionary<string, DirectChatModelDto>? Models { get; set; }
    [JsonPropertyName("response")][TomlPropertyName("response")] public DirectChatResponseDto? Response { get; set; }
}

public sealed class DirectChatModelDto
{
    [JsonPropertyName("request_id")][TomlPropertyName("request_id")] public string? RequestId { get; set; }
    [JsonPropertyName("served_id")][TomlPropertyName("served_id")] public string? ServedId { get; set; }
    [JsonPropertyName("equivalence")][TomlPropertyName("equivalence")] public string? Equivalence { get; set; }
    [JsonPropertyName("reasoning_efforts")][TomlPropertyName("reasoning_efforts")] public string[]? ReasoningEfforts { get; set; }
}

public sealed class DirectChatResponseDto
{
    [JsonPropertyName("served_model_header")][TomlPropertyName("served_model_header")] public string? ServedModelHeader { get; set; }
    [JsonPropertyName("retry_after")][TomlPropertyName("retry_after")] public DirectChatScalarDto? RetryAfter { get; set; }
    [JsonPropertyName("usage_prompt")][TomlPropertyName("usage_prompt")] public DirectChatScalarDto? Prompt { get; set; }
    [JsonPropertyName("usage_completion")][TomlPropertyName("usage_completion")] public DirectChatScalarDto? Completion { get; set; }
    [JsonPropertyName("codes")][TomlPropertyName("codes")] public Dictionary<string, string>? Codes { get; set; }
}

public sealed class DirectChatScalarDto
{
    [JsonPropertyName("source")][TomlPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("name")][TomlPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("unit")][TomlPropertyName("unit")] public string? Unit { get; set; }
}
