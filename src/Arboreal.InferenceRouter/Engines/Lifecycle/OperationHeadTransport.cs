using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

internal readonly record struct OperationHeadBudget(long Started, TimeSpan Duration, CancellationToken Token)
{
    internal void Check()
    {
        Token.ThrowIfCancellationRequested();
        if (TimeProvider.System.GetElapsedTime(Started) >= Duration)
            throw new OperationCanceledException("The operation-head exchange expired.", Token);
    }
}

internal sealed class OperationHeadClient(UnixDomainSocketEndPoint endpoint, string pinnedLeaf, HostReservationScope scope)
    : ISupervisedLaunchHead
{
    public SupervisedLaunchJournalHead? Read(HostReservationScope requested) =>
        Exchange(OperationHeadProtocol.Read, requested, null, null).Head;

    public bool TryAdvance(SupervisedLaunchJournalHead? predecessor, SupervisedLaunchJournalHead next) =>
        Exchange(OperationHeadProtocol.CompareAdvance, next.Scope, predecessor, next).Outcome == OperationHeadProtocol.Applied;

    // The journal port is synchronous. The private exchange has no context capture and one absolute
    // budget, and runs only in the supervisor's journal path.
    private OperationHeadReply Exchange(string operation, HostReservationScope requested,
        SupervisedLaunchJournalHead? predecessor, SupervisedLaunchJournalHead? next)
    {
        try
        {
            var request = new OperationHeadRequest(OperationHeadProtocol.Version, operation, requested,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(OperationHeadProtocol.NonceBytes)), predecessor, next);
            if (OperatingSystem.IsLinux() && OperationHeadProtocol.IsScopeValid(scope) &&
                OperationHeadProtocol.IsCanonicalDigest(pinnedLeaf) && request.IsValidFor(scope))
                return ExchangeAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            // A lost or invalid acknowledgment gives no evidence of either absence or no write.
        }
        throw new IOException("Operation-head authority is unavailable.");
    }

    private async Task<OperationHeadReply> ExchangeAsync(OperationHeadRequest request)
    {
        var started = TimeProvider.System.GetTimestamp();
        using var timeout = new CancellationTokenSource(OperationHeadProtocol.Timeout);
        var budget = new OperationHeadBudget(started, OperationHeadProtocol.Timeout, timeout.Token);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(endpoint, budget.Token).ConfigureAwait(false);
        budget.Check();
        using var network = new NetworkStream(socket, ownsSocket: false);
        using var tls = new SslStream(network, leaveInnerStreamOpen: true,
            (_, remote, _, _) => remote is not null && string.Equals(
                remote.GetCertHashString(HashAlgorithmName.SHA256), pinnedLeaf, StringComparison.Ordinal));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "operation-head" },
            budget.Token).ConfigureAwait(false);
        budget.Check();
        await BoundedJsonFrame.WriteAsync(tls, request, OperationHeadWireJsonContext.Default.OperationHeadRequest,
            OperationHeadProtocol.MaxPayload, budget.Token).ConfigureAwait(false);
        budget.Check();
        var reply = await BoundedJsonFrame.ReadAsync(tls, OperationHeadWireJsonContext.Default.OperationHeadReply,
            OperationHeadProtocol.MaxPayload, budget.Token).ConfigureAwait(false);
        if (!reply.Matches(request)) throw new IOException("The operation-head reply is invalid.");
        budget.Check();
        return reply;
    }
}

internal static class OperationHeadTransport
{
    internal static async Task ServeAsync(Socket accepted, uint requesterUid, X509Certificate2 certificate,
        HostReservationScope scope, TimeSpan duration,
        Func<OperationHeadRequest, OperationHeadBudget, OperationHeadReply> decide, CancellationToken ct)
    {
        var started = TimeProvider.System.GetTimestamp();
        using var timeout = new CancellationTokenSource(duration);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var budget = new OperationHeadBudget(started, duration, lifetime.Token);
        try
        {
            if (!HeadReadV1Endpoint.IsAuthorizedRequester(accepted, requesterUid)) return;
            budget.Check();
            using var network = new NetworkStream(accepted, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate },
                budget.Token).ConfigureAwait(false);
            budget.Check();
            var request = await BoundedJsonFrame.ReadAsync(tls, OperationHeadWireJsonContext.Default.OperationHeadRequest,
                OperationHeadProtocol.MaxPayload, budget.Token).ConfigureAwait(false);
            if (!request.IsValidFor(scope)) return;
            budget.Check();
            var reply = decide(request, budget);
            if (!reply.Matches(request)) return;
            budget.Check();
            await BoundedJsonFrame.WriteAsync(tls, reply, OperationHeadWireJsonContext.Default.OperationHeadReply,
                OperationHeadProtocol.MaxPayload, budget.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            // The connection ends without conferring a result.
        }
    }

    internal static bool IsUnavailable(Exception error) =>
        error is IOException or SocketException or AuthenticationException or JsonException or ArgumentException or
                 OperationCanceledException or UnauthorizedAccessException or CryptographicException;
}
