using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Arboreal.InferenceRouter.Lifecycle;

// One bounded HTTP/1.1 request from the commissioned Docker writer. The writer uses
// Content-Length for Create, and no content for Stop/Start. There is no reusable connection.
internal static class SuccessorIngressEndpoint
{
    private const int HeaderLimit = 8 * 1024;
    private const int BodyLimit = 512 * 1024;

    internal static async Task ServeAsync(Socket accepted, uint writerUid,
        DockerLifecycleProvider.SuccessorIngress ingress, TimeSpan allowance, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(allowance);
        var token = timeout.Token;
        try
        {
            if (!HeadReadV1Endpoint.IsAuthorizedRequester(accepted, writerUid)) return;
            using var stream = new NetworkStream(accepted, ownsSocket: false);
            var request = await ReadRequestAsync(stream, token).ConfigureAwait(false);
            if (request is not { } received) return;

            var reply = await ingress.ForwardAuthenticatedAsync(accepted, writerUid, HttpMethod.Post,
                received.Target, received.Body, token).ConfigureAwait(false);
            await WriteReplyAsync(stream, reply, token).ConfigureAwait(false);
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error) || error is HttpRequestException)
        {
            // An interrupted exchange has no usable acknowledgment.
        }
    }

    private static async ValueTask<(string Target, byte[] Body)?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new byte[HeaderLimit];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), ct).ConfigureAwait(false);
            if (read == 0) return null;
            count += read;
            var end = bytes.AsSpan(0, count).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;

            var header = ParseHeader(bytes.AsSpan(0, end));
            var bodyAt = end + 4;
            if (header is not { } received || count - bodyAt > received.Length) return null;
            var body = new byte[received.Length];
            bytes.AsSpan(bodyAt, count - bodyAt).CopyTo(body);
            await stream.ReadExactlyAsync(body.AsMemory(count - bodyAt), ct).ConfigureAwait(false);
            return (received.Target, body);
        }

        return null;
    }

    private static (string Target, int Length)? ParseHeader(ReadOnlySpan<byte> header)
    {
        foreach (var value in header)
            if (value is < 32 and not (9 or 10 or 13) or >= 127)
                return null;

        var lines = Encoding.ASCII.GetString(header).Split("\r\n", StringSplitOptions.None);
        var request = lines[0].Split(' ', StringSplitOptions.None);
        if (request is not ["POST", var target, "HTTP/1.1"] || target is not ['/', ..])
            return null;

        var host = false;
        int? length = null;
        foreach (var line in lines.AsSpan(1))
        {
            var boundary = line.IndexOf(':', StringComparison.Ordinal);
            if (boundary <= 0 || line.Any(static ch => ch is '\r' or '\n')) return null;
            var name = line.AsSpan(0, boundary);
            foreach (var character in name)
                if (!char.IsAsciiLetterOrDigit(character) && character != '-') return null;
            var value = line.AsSpan(boundary + 1).Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (length is not null || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
                    size is < 0 or > BodyLimit) return null;
                length = size;
            }
            else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (host || !value.Equals("docker.engine", StringComparison.OrdinalIgnoreCase)) return null;
                host = true;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Expect", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return host ? (target, length ?? 0) : null;
    }

    private static async Task WriteReplyAsync(Stream stream, (HttpStatusCode Status, byte[] Body)? reply, CancellationToken ct)
    {
        var (status, body) = reply ?? (HttpStatusCode.ServiceUnavailable, []);
        var code = (int)status;
        if (code is < 200 or > 599) return;

        var header = status is HttpStatusCode.NoContent or HttpStatusCode.NotModified
            ? FormattableString.Invariant($"HTTP/1.1 {code} \r\nConnection: close\r\n\r\n")
            : FormattableString.Invariant($"HTTP/1.1 {code} \r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
        if (status is not (HttpStatusCode.NoContent or HttpStatusCode.NotModified) && body.Length != 0)
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }
}
