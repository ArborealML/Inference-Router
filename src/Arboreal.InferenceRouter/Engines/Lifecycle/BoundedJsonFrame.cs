using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Arboreal.InferenceRouter.Lifecycle;

// One UTF-8 JSON payload with a four-byte big-endian length. The caller owns the stream and deadline.
internal static class BoundedJsonFrame
{
    private const int HeaderBytes = sizeof(int);

    internal static async ValueTask WriteAsync<T>(Stream stream, T message, JsonTypeInfo<T> type,
        int payloadLimit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadLimit);
        ct.ThrowIfCancellationRequested();
        var frame = new byte[HeaderBytes + payloadLimit];
        using var buffer = new MemoryStream(frame) { Position = HeaderBytes };
        try
        {
            JsonSerializer.Serialize(buffer, message, type);
        }
        catch (NotSupportedException error)
        {
            throw new IOException("The message could not be serialized within its frame.", error);
        }

        var end = checked((int)buffer.Position);
        BinaryPrimitives.WriteInt32BigEndian(frame, end - HeaderBytes);
        ct.ThrowIfCancellationRequested();
        await stream.WriteAsync(frame.AsMemory(0, end), ct).ConfigureAwait(false);
    }

    internal static ValueTask<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, int payloadLimit,
        CancellationToken ct) where T : class => ReadAsync(stream, type, payloadLimit, null, ct);

    internal static async ValueTask<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, int payloadLimit,
        Action<int, CancellationToken>? payloadReadPendingForTests, CancellationToken ct) where T : class
    {
        var payload = await ReadPayloadAsync(stream, payloadLimit, payloadReadPendingForTests, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(payload, type) ?? throw new JsonException("The frame payload is empty.");
    }

    // Persisted frame digests cover the exact payload bytes, excluding the length prefix.
    internal static ValueTask<byte[]> ReadPayloadAsync(Stream stream, int payloadLimit, CancellationToken ct) =>
        ReadPayloadAsync(stream, payloadLimit, null, ct);

    private static async ValueTask<byte[]> ReadPayloadAsync(Stream stream, int payloadLimit,
        Action<int, CancellationToken>? payloadReadPendingForTests, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadLimit);
        var header = new byte[HeaderBytes];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size <= 0 || size > payloadLimit) throw new IOException("The frame has an invalid payload length.");

        var payload = new byte[size];
        for (var offset = 0; offset < size;)
        {
            var read = stream.ReadAsync(payload.AsMemory(offset), ct);
            if (!read.IsCompleted) payloadReadPendingForTests?.Invoke(offset, ct);
            var count = await read.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (count == 0) throw new EndOfStreamException("The payload ended before its declared length.");
            offset += count;
        }

        return payload;
    }
}
