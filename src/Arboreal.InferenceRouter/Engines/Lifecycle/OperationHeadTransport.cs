using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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

internal sealed class OperationHeadClient(UnixDomainSocketEndPoint endpoint, string pinnedLeaf, HostReservationScope scope,
    int version = OperationHeadProtocol.Version)
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
            var request = new OperationHeadRequest(version, operation, requested,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(OperationHeadProtocol.NonceBytes)), predecessor, next);
            if (OperatingSystem.IsLinux() && OperationHeadProtocol.IsScopeValid(scope) &&
                OperationHeadProtocol.IsCanonicalDigest(pinnedLeaf) && request.IsValidFor(scope))
                return OperationHeadTransport.ExchangeOperationAsync(endpoint, pinnedLeaf, request).GetAwaiter().GetResult();
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            // A lost or invalid acknowledgment gives no evidence of either absence or no write.
        }
        throw new IOException("Operation-head authority is unavailable.");
    }
}

internal static class OperationHeadTransport
{
    internal static Task<OperationHeadReply> ExchangeOperationAsync(UnixDomainSocketEndPoint endpoint, string pinnedLeaf,
        OperationHeadRequest request) => ExchangeAsync(endpoint, pinnedLeaf, request,
            OperationHeadWireJsonContext.Default.OperationHeadRequest, OperationHeadWireJsonContext.Default.OperationHeadReply,
            reply => reply.Matches(request));

    internal static async Task<BrokerAnchorReply> ExchangeBrokerAsync(UnixDomainSocketEndPoint endpoint, string pinnedLeaf,
        BrokerAnchorRequest request)
    {
        try
        {
            if (OperatingSystem.IsLinux() && OperationHeadProtocol.IsCanonicalDigest(pinnedLeaf) &&
                OperationHeadProtocol.IsScopeValid(request.Scope) && request.IsValidFor(request.Scope))
                return await ExchangeAsync(endpoint, pinnedLeaf, request,
                    BrokerAnchorJsonContext.Default.BrokerAnchorRequest, BrokerAnchorJsonContext.Default.BrokerAnchorReply,
                    reply => reply.Matches(request)).ConfigureAwait(false);
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            // Only a validated reply may report that the protected comparison made no write.
        }
        throw new IOException("The protected broker anchor is unavailable.");
    }

    private static async Task<TReply> ExchangeAsync<TRequest, TReply>(UnixDomainSocketEndPoint endpoint, string pinnedLeaf,
        TRequest request, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TReply> replyType, Func<TReply, bool> matches)
        where TReply : class
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
        await BoundedJsonFrame.WriteAsync(tls, request, requestType, OperationHeadProtocol.MaxPayload, budget.Token)
            .ConfigureAwait(false);
        budget.Check();
        var reply = await BoundedJsonFrame.ReadAsync(tls, replyType, OperationHeadProtocol.MaxPayload, budget.Token)
            .ConfigureAwait(false);
        if (!matches(reply)) throw new IOException("The operation-head reply is invalid.");
        budget.Check();
        return reply;
    }

    internal static async Task ServeAsync(Socket accepted, uint requesterUid, uint? successorReaderUid, uint? registrarReaderUid,
        uint? brokerWriterUid, uint? brokerReaderUid,
        X509Certificate2 certificate,
        HostReservationScope scope, int serviceVersion, TimeSpan duration,
        Func<OperationHeadRequest, OperationHeadBudget, OperationHeadReply> decide,
        Func<BrokerAnchorRequest, OperationHeadBudget, BrokerAnchorReply> decideBroker, CancellationToken ct)
    {
        var started = TimeProvider.System.GetTimestamp();
        using var timeout = new CancellationTokenSource(duration);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var budget = new OperationHeadBudget(started, duration, lifetime.Token);
        try
        {
            var operationPeer = HeadReadV1Endpoint.IsAuthorizedRequester(accepted, requesterUid);
            var successorReader = (successorReaderUid is { } ingress && HeadReadV1Endpoint.IsAuthorizedRequester(accepted, ingress)) ||
                (registrarReaderUid is { } registrar && HeadReadV1Endpoint.IsAuthorizedRequester(accepted, registrar));
            var brokerWriter = brokerWriterUid is { } writer && HeadReadV1Endpoint.IsAuthorizedRequester(accepted, writer);
            var brokerReader = brokerReaderUid is { } reader && HeadReadV1Endpoint.IsAuthorizedRequester(accepted, reader);
            if (!operationPeer && !successorReader && !brokerWriter && !brokerReader) return;
            budget.Check();
            using var network = new NetworkStream(accepted, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate },
                budget.Token).ConfigureAwait(false);
            budget.Check();
            var payload = await BoundedJsonFrame.ReadPayloadAsync(tls, OperationHeadProtocol.MaxPayload, budget.Token)
                .ConfigureAwait(false);
            using var message = JsonDocument.Parse(payload);
            if (message.RootElement.ValueKind != JsonValueKind.Object) return;
            if (message.RootElement.TryGetProperty("Purpose", out _))
            {
                if (brokerWriter || brokerReader)
                    await ServeBrokerAsync(tls, payload, scope, brokerWriter, decideBroker, budget).ConfigureAwait(false);
                return;
            }

            if (!operationPeer && !successorReader) return;
            var request = JsonSerializer.Deserialize(payload, OperationHeadWireJsonContext.Default.OperationHeadRequest);
            if (request is null) return;
            if (request.Version > serviceVersion || !request.IsValidFor(scope)) return;
            if (!operationPeer &&
                (request.Version != OperationHeadProtocol.SuccessorVersion || request.Operation != OperationHeadProtocol.Read)) return;
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

    private static async ValueTask ServeBrokerAsync(Stream authenticated, byte[] payload, HostReservationScope scope, bool canAdvance,
        Func<BrokerAnchorRequest, OperationHeadBudget, BrokerAnchorReply> decide, OperationHeadBudget budget)
    {
        var request = JsonSerializer.Deserialize(payload, BrokerAnchorJsonContext.Default.BrokerAnchorRequest);
        if (request is null || !request.IsValidFor(scope) ||
            (!canAdvance && request.Operation == OperationHeadProtocol.CompareAdvance)) return;
        budget.Check();
        var reply = decide(request, budget);
        if (!reply.Matches(request)) return;
        budget.Check();
        await BoundedJsonFrame.WriteAsync(authenticated, reply, BrokerAnchorJsonContext.Default.BrokerAnchorReply,
            OperationHeadProtocol.MaxPayload, budget.Token).ConfigureAwait(false);
    }

    internal static bool IsUnavailable(Exception error) =>
        error is IOException or SocketException or AuthenticationException or JsonException or ArgumentException or
                 OperationCanceledException or UnauthorizedAccessException or CryptographicException;
}
