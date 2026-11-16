using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text;
using System.IO.Pipelines;

namespace Arboreal.InferenceRouter.Server;

/// <summary>Server-side JSON source generation (Native AOT): events, stats, admin acks, errors.
/// Wire DTOs (OpenAI bodies/responses) are passed through as raw bytes — see ServerApp.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
internal static partial class AirServerJson
{
    [JsonSerializable(typeof(RouterEvent))]
    [JsonSerializable(typeof(RouterStats))]
    [JsonSerializable(typeof(TicketSnapshot))]
    [JsonSerializable(typeof(SwitchAck))]
    [JsonSerializable(typeof(ErrorBody))]
    internal sealed partial class AirServerContext : JsonSerializerContext;
}

/// <summary>AOT-safe JSON write helpers over the source-generated context.</summary>
internal static class AirJson
{
    public static byte[] ToBytes<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var buffer = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        return buffer;
    }

    public static async Task WriteAsync<T>(HttpResponse response, T value, JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default)
    {
        response.Headers.ContentType = "application/json";
        await response.Body.WriteAsync(ToBytes(value, typeInfo), ct).ConfigureAwait(false);
    }

    public static Task WriteSseFrameAsync<T>(PipeWriter writer, T value, JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default)
        => WriteFrameAsync(writer, Frame(JsonSerializer.Serialize(value, typeInfo), null), ct);

    public static async Task WriteFrameAsync(PipeWriter writer, ReadOnlyMemory<byte> frame,
        CancellationToken ct = default)
    {
        await writer.WriteAsync(frame, ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>SSE data frame: "data: {payload}\n\n" plus optional trailing "data: [DONE]" frame.</summary>
    public static ReadOnlyMemory<byte> Frame(string payload, string? donePayload = null)
        => Encoding.UTF8.GetBytes(donePayload is null
            ? $"data: {payload}\n\n"
            : $"data: {payload}\n\ndata: {donePayload}\n\n");
}


/// <summary>Admin + error DTOs. Top-level (not nested) so source-gen metadata resolves cleanly.</summary>
internal sealed record ErrorBody
{
    public required ErrorDetail Error { get; init; }
}

internal sealed record ErrorDetail
{
    public required string Message { get; init; }
    public string Code { get; init; } = "internal";
    public string Type { get; init; } = "inference_router_error";
}

internal sealed record SwitchAck
{
    public required string Engine { get; init; }
    public required string Model { get; init; }
    public bool Switching { get; init; }
}
