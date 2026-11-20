using System.Buffers.Binary;

namespace Arboreal.InferenceRouter.Lifecycle;

internal static class DockerOutputReader
{
    private const int WireCeiling = 1024 * 1024;
    private const int RetainedBytes = 64 * 1024;
    private const int RetainedFrames = 256;

    internal static async ValueTask<DockerOutputCapture> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[8];
        var scratch = new byte[8192];
        List<DockerOutputFrame> frames = [];
        var wireLeft = WireCeiling;
        var retainLeft = RetainedBytes;
        var truncated = false;

        while (wireLeft >= header.Length)
        {
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken: ct)
                .ConfigureAwait(false);
            if (read == 0) return new([.. frames], truncated);
            if (read != header.Length)
                throw new InvalidDataException("Docker output ended inside a frame header.");
            if (header[0] is not (1 or 2) || header[1] != 0 || header[2] != 0 || header[3] != 0)
                throw new InvalidDataException("Docker output had an unsupported frame header.");

            wireLeft -= header.Length;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (length > wireLeft)
                throw new InvalidDataException("Docker output exceeded its framed wire limit.");

            var retained = frames.Count < RetainedFrames ? Math.Min((int)length, retainLeft) : 0;
            if (retained != 0)
            {
                var payload = new byte[retained];
                await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
                frames.Add(new(header[0], payload));
                retainLeft -= retained;
            }

            var discard = (int)length - retained;
            truncated |= discard != 0;
            wireLeft -= (int)length;
            while (discard != 0)
            {
                var count = Math.Min(discard, scratch.Length);
                await stream.ReadExactlyAsync(scratch.AsMemory(0, count), ct).ConfigureAwait(false);
                discard -= count;
            }
        }

        // Fewer than eight bytes remain. Any suffix is either a partial or an over-budget frame.
        if (await stream.ReadAsync(scratch.AsMemory(0, 1), ct).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Docker output had a suffix beyond its last complete frame.");
        return new([.. frames], truncated);
    }
}
