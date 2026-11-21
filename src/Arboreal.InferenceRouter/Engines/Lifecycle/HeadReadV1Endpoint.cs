using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

internal static class HeadReadV1Endpoint
{
    // Linux ucred contains pid, uid, gid in native byte order.
    private const int LinuxSocketLevel = 1;
    private const int LinuxPeerCredentials = 17;

    internal static async Task ServeAsync(Socket accepted, uint requesterUid, X509Certificate2 certificate,
        HeadReadScope scope, Func<HeadReadScope, CancellationToken, ValueTask<HeadReadValue>> read,
        CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(HeadReadV1.Timeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var token = lifetime.Token;
        try
        {
            if (!scope.IsValid() || !IsAuthorizedRequester(accepted, requesterUid)) return;
            using var network = new NetworkStream(accepted, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate },
                token).ConfigureAwait(false);
            var request = await BoundedJsonFrame.ReadAsync(tls, HeadReadJsonContext.Default.HeadReadV1Request,
                HeadReadV1.MaxRequest, token).ConfigureAwait(false);
            if (!request.IsValid() || request.Scope != scope) return;

            token.ThrowIfCancellationRequested();
            var value = await read(scope, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!value.IsEvidenceFor(scope)) return;

            var reply = new HeadReadV1Reply(request.Operation, request.Version, scope, request.Nonce, value);
            await HeadReadV1.WriteReplyAsync(tls, reply, token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            // A refused exchange ends without granting absence or exposing authority contents.
        }
    }

    // The caller owns the original deadline timer. The same origin is used for every synchronous
    // recheck here, and the linked token bounds the socket, handshake and exact frame operations.
    internal static async Task<HeadReadValue> ReadAsync(UnixDomainSocketEndPoint endpoint, string pinnedLeaf,
        HeadReadScope scope, TimeProvider clock, long started, TimeSpan duration, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !scope.IsValid() || !HostMutation.IsPlanDigest(pinnedLeaf) ||
            duration > HeadReadV1.Timeout || !InBudget(clock, started, duration, ct)) return HeadReadValue.Unavailable;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            if (!InBudget(clock, started, duration, ct)) return HeadReadValue.Unavailable;
            using var network = new NetworkStream(socket, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true,
                (_, remote, _, _) => remote is not null && string.Equals(
                    remote.GetCertHashString(HashAlgorithmName.SHA256), pinnedLeaf, StringComparison.OrdinalIgnoreCase));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "head" }, ct)
                .ConfigureAwait(false);
            if (!InBudget(clock, started, duration, ct)) return HeadReadValue.Unavailable;

            var request = new HeadReadV1Request(HeadReadV1.Operation, HeadReadV1.Version, scope,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(HeadReadV1.NonceBytes)));
            await BoundedJsonFrame.WriteAsync(tls, request, HeadReadJsonContext.Default.HeadReadV1Request,
                HeadReadV1.MaxRequest, ct).ConfigureAwait(false);
            if (!InBudget(clock, started, duration, ct)) return HeadReadValue.Unavailable;

            var reply = await BoundedJsonFrame.ReadAsync(tls, HeadReadJsonContext.Default.HeadReadV1Reply,
                HeadReadV1.MaxReply, ct).ConfigureAwait(false);
            return reply.Matches(request) && InBudget(clock, started, duration, ct)
                ? reply.Value : HeadReadValue.Unavailable;
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return HeadReadValue.Unavailable;
        }
    }

    internal static bool IsAuthorizedRequester(Socket accepted, uint requesterUid)
    {
        if (!OperatingSystem.IsLinux() || accepted.AddressFamily != AddressFamily.Unix) return false;
        Span<byte> credentials = stackalloc byte[3 * sizeof(int)];
        var size = accepted.GetRawSocketOption(LinuxSocketLevel, LinuxPeerCredentials, credentials);
        return size == credentials.Length && MemoryMarshal.Read<uint>(credentials[sizeof(int)..]) == requesterUid;
    }

    private static bool InBudget(TimeProvider clock, long started, TimeSpan duration, CancellationToken ct) =>
        !ct.IsCancellationRequested && clock.GetElapsedTime(started) < duration;

    private static bool IsUnavailable(Exception error) =>
        error is IOException or SocketException or AuthenticationException or JsonException or ArgumentException or OperationCanceledException;
}
