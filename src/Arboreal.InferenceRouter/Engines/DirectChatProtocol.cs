using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Arboreal.InferenceRouter;

internal static class DirectChatProtocol
{
    private static readonly JsonDocumentOptions s_json = new() { MaxDepth = 32, AllowDuplicateProperties = false };

    internal static byte[] Project(InferenceRequest request, DirectChatProvider provider, DirectChatModel model, out ulong choices)
    {
        choices = 1;
        if (request.IsLegacyCompletionHttp || request.Stream && !provider.AllowsStreaming || request.Options?.Profile is not null)
            throw Unsupported();
        try
        {
            using var document = JsonDocument.Parse(request.Body, s_json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() is 0 or > 512 ||
                !messages.EnumerateArray().All(message => ValidMessage(message, provider.AllowsTools))) throw Unsupported();
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("model", model.RequestId);
                writer.WriteBoolean("stream", request.Stream);
                if (provider.BackupParameter is { } backup) writer.WriteBoolean(backup, false);
                foreach (var property in root.EnumerateObject())
                {
                    var value = property.Value;
                    if (property.Name == provider.BackupParameter)
                    {
                        if (value.ValueKind != JsonValueKind.False) throw Unsupported();
                        continue;
                    }
                    switch (property.Name)
                    {
                        case "model" when value.ValueKind == JsonValueKind.String:
                        case "stream" when value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean() == request.Stream:
                            continue;
                        case "messages":
                            break;
                        case "reasoning_effort" when provider.AllowsReasoning && value.ValueKind == JsonValueKind.String &&
                            model.ReasoningEfforts.Contains(value.GetString()!, StringComparer.Ordinal):
                            break;
                        case "tools" when provider.AllowsTools && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 64:
                        case "tool_choice" when provider.AllowsTools && value.ValueKind is JsonValueKind.String or JsonValueKind.Object:
                        case "parallel_tool_calls" when provider.AllowsTools && value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                            break;
                        case "stream_options" when request.Stream && value.ValueKind == JsonValueKind.Object &&
                            value.EnumerateObject().All(static field => field.Name == "include_usage" &&
                                field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False):
                            break;
                        case "max_tokens" or "max_completion_tokens" when value.ValueKind == JsonValueKind.Number &&
                            value.TryGetInt32(out var tokens) && tokens > 0:
                            if (root.TryGetProperty(property.Name == "max_tokens" ? "max_completion_tokens" : "max_tokens", out _))
                                throw Unsupported();
                            break;
                        case "n" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count is >= 1 and <= 64:
                            choices = count == 64 ? ulong.MaxValue : (1UL << count) - 1;
                            break;
                        case "temperature" or "top_p" when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
                            double.IsFinite(number) && number >= 0 && number <= (property.Name == "top_p" ? 1 : 2):
                            break;
                        case "seed" when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _):
                        case "stop" when value.ValueKind == JsonValueKind.String || value.ValueKind == JsonValueKind.Array &&
                            value.GetArrayLength() <= 8 && value.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String):
                            break;
                        default:
                            throw Unsupported();
                    }
                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return buffer.ToArray();
        }
        catch (JsonException) { throw Unsupported(); }
    }

    private static bool ValidMessage(JsonElement message, bool tools)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String ||
            role.GetString() is not ("system" or "developer" or "user" or "assistant" or "tool") ||
            (!tools && role.ValueEquals("tool"u8)) || !message.TryGetProperty("content", out var content) ||
            (content.ValueKind != JsonValueKind.String && (!tools || content.ValueKind != JsonValueKind.Null || !message.TryGetProperty("tool_calls", out _))))
            return false;
        return message.EnumerateObject().All(field => field.Name is "role" or "content" || tools && (field.Name switch
        {
            "tool_calls" => role.ValueEquals("assistant"u8) && field.Value.ValueKind == JsonValueKind.Array,
            "tool_call_id" => role.ValueEquals("tool"u8) && field.Value.ValueKind == JsonValueKind.String,
            _ => false,
        }));
    }

    internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes) => JsonDocument.Parse(bytes, s_json);

    internal static RouterRequestException Unsupported() => new("commercial_request_unsupported", "the configured chat semantics cannot authorize this request");
    internal static RouterRequestException Identity() => new("commercial_identity_invalid", "the configured served model could not be verified");
    internal static RouterRequestException Deadline() => new("commercial_identity_timeout", "verified semantic output did not arrive before the deadline");
    internal static RouterRequestException BadResponse() => new("commercial_exchange_failed", "the configured chat response was invalid or incomplete");
}

public sealed record DirectChatMetric(string State, long? Value = null);
public sealed record DirectChatEvidence(DirectChatMetric Prompt, DirectChatMetric Completion, DirectChatMetric RetryAfterMilliseconds)
{
    public string ModelIdentity { get; init; } = "unknown";
    public string? VerifiedModel { get; init; }
    public double? FirstRawMilliseconds { get; init; }
    public double? FirstSemanticMilliseconds { get; init; }
}

internal sealed class DirectChatMetrics(DirectChatResponseMapping mapping, HttpResponseHeaders headers, TimeProvider clock)
{
    private static readonly DirectChatMetric s_missing = new("missing");
    private static readonly DirectChatMetric s_invalid = new("invalid");
    private readonly object _gate = new();
    private readonly long _observedAtUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
    private DirectChatMetric _prompt = s_missing;
    private DirectChatMetric _completion = s_missing;
    private DirectChatMetric _retryAfter = s_missing;

    internal void Observe(JsonElement root)
    {
        lock (_gate) ObserveUnderGate(root);
    }

    private void ObserveUnderGate(JsonElement root)
    {
        if (mapping.Prompt is { } prompt && mapping.Completion is { } completion)
        {
            _prompt = Combine(_prompt, Read(prompt, root));
            _completion = Combine(_completion, Read(completion, root));
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
        {
            _prompt = Combine(_prompt, ReadToken(usage, "prompt_tokens"));
            _completion = Combine(_completion, ReadToken(usage, "completion_tokens"));
        }
        if (mapping.RetryAfter is { } retry) _retryAfter = Combine(_retryAfter, Read(retry, root));
    }

    internal DirectChatEvidence Evidence()
    {
        lock (_gate) return EvidenceUnderGate();
    }

    private DirectChatEvidence EvidenceUnderGate()
    {
        ObserveUnderGate(default);
        if (mapping.RetryAfter is null && headers.TryGetValues("Retry-After", out var values))
        {
            var value = Single(values);
            if (TryNatural(value, out var seconds)) _retryAfter = ConvertValue(seconds, DirectChatScalarUnit.Seconds);
            else if (DateTimeOffset.TryParseExact(value, "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                _retryAfter = ConvertValue(date.ToUnixTimeMilliseconds(), DirectChatScalarUnit.UnixMilliseconds);
            else _retryAfter = s_invalid;
        }
        return new(_prompt, _completion, _retryAfter);
    }

    internal RouterRequestException Refusal(JsonElement root, int status)
    {
        Observe(root);
        var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("code", out var raw) && raw.ValueKind == JsonValueKind.String && raw.GetString() is { Length: <= 64 } name
            ? mapping.Codes.GetValueOrDefault(name) : null;
        return code == "rate_limited" || code is null && status == 429
            ? new RouterRequestException("commercial_rate_limited", "the configured chat provider requested backoff")
            : DirectChatProtocol.BadResponse();
    }

    private DirectChatMetric Read(DirectChatScalar scalar, JsonElement root)
    {
        if (scalar.Source == DirectChatScalarSource.Header)
        {
            if (!headers.TryGetValues(scalar.Name, out var values)) return s_missing;
            return TryNatural(Single(values), out var value) ? ConvertValue(value, scalar.Unit) : s_invalid;
        }
        if (root.ValueKind == JsonValueKind.Undefined) return s_missing;
        foreach (var segment in scalar.Name.Split('.'))
        {
            if (root.ValueKind != JsonValueKind.Object) return s_invalid;
            if (!root.TryGetProperty(segment, out var child)) return s_missing;
            root = child;
        }
        return root.ValueKind == JsonValueKind.Number && root.TryGetInt64(out var scalarValue) && scalarValue >= 0
            ? ConvertValue(scalarValue, scalar.Unit) : s_invalid;
    }

    private DirectChatMetric ConvertValue(long value, DirectChatScalarUnit unit)
    {
        try
        {
            var result = unit switch
            {
                DirectChatScalarUnit.Seconds => checked(value * 1_000),
                DirectChatScalarUnit.UnixSeconds => checked(value * 1_000 - _observedAtUnixMilliseconds),
                DirectChatScalarUnit.UnixMilliseconds => checked(value - _observedAtUnixMilliseconds),
                _ => value,
            };
            if (unit == DirectChatScalarUnit.Tokens) return new("available", result);
            return result <= 86_400_000 ? new("available", Math.Max(0, result)) : s_invalid;
        }
        catch (OverflowException) { return s_invalid; }
    }

    private static DirectChatMetric ReadToken(JsonElement usage, string name)
    {
        if (usage.ValueKind != JsonValueKind.Object) return s_invalid;
        if (!usage.TryGetProperty(name, out var value)) return s_missing;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var tokens) && tokens >= 0
            ? new("available", tokens) : s_invalid;
    }

    internal static string? Single(IEnumerable<string> values)
    {
        using var iterator = values.GetEnumerator();
        if (!iterator.MoveNext()) return null;
        var result = iterator.Current;
        return !iterator.MoveNext() && result.Length is > 0 and <= 512 ? result : null;
    }

    private static bool TryNatural(string? value, out long result) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;

    private static DirectChatMetric Combine(DirectChatMetric old, DirectChatMetric next) => next.State == "missing" ? old :
        old.State == "missing" || old == next ? next : s_invalid;
}
