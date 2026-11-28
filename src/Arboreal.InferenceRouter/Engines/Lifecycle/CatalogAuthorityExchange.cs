using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record CatalogAuthorityPeer(string SocketPath, string LeafSha256)
{
    internal bool IsValid() => SocketPath is { Length: > 0 } &&
        Path.IsPathFullyQualified(SocketPath) && !SocketPath.Contains('\0', StringComparison.Ordinal) &&
        HostMutation.IsPlanDigest(LeafSha256);
}

internal sealed record CatalogAuthorityBootstrap(CatalogAuthorityScope Scope, CatalogAuthorityPeer Observer,
    CatalogAuthorityPeer Head);

internal static class CatalogAuthorityEndpoint
{
    internal static Task ServeTopologyAsync(Socket accepted, uint requesterUid, X509Certificate2 certificate,
        CatalogAuthorityScope scope, CatalogAuthorityStore store, CancellationToken ct) =>
        ServeAsync(accepted, requesterUid, certificate, scope, CatalogAuthorityV2.ReadTopology,
            CatalogAuthorityV2.TopologyVersion, store.ReadAsync, ct);

    internal static Task ServeCommissionAsync(Socket accepted, uint requesterUid, X509Certificate2 certificate,
        CatalogAuthorityScope scope, Func<CancellationToken, ValueTask<CatalogAuthoritySnapshot?>> read, CancellationToken ct) =>
        ServeAsync(accepted, requesterUid, certificate, scope, CatalogAuthorityV2.ReadCommission,
            CatalogAuthorityV2.CommissionVersion, read, ct);

    private static async Task ServeAsync(Socket accepted, uint requesterUid, X509Certificate2 certificate,
        CatalogAuthorityScope scope, string operation, int version, Func<CancellationToken, ValueTask<CatalogAuthoritySnapshot?>> read,
        CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(CatalogAuthorityV2.Timeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var token = lifetime.Token;
        try
        {
            if (!scope.IsValid() || !HeadReadV1Endpoint.IsAuthorizedRequester(accepted, requesterUid)) return;

            using var network = new NetworkStream(accepted, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate },
                token).ConfigureAwait(false);
            var request = await BoundedJsonFrame.ReadAsync(tls, CatalogAuthorityJsonContext.Default.CatalogAuthorityRequest,
                CatalogAuthorityV2.MaxRequest, token).ConfigureAwait(false);
            if (!request.IsFor(operation, version, scope)) return;

            token.ThrowIfCancellationRequested();
            var snapshot = await read(token).ConfigureAwait(false);
            if (snapshot is null || !snapshot.IsEvidenceFor(scope)) return;

            await CatalogAuthorityV2.WriteReplyAsync(tls, new(operation, version, scope,
                request.Nonce, snapshot), token).ConfigureAwait(false);
        }
        catch (Exception error) when (CatalogAuthorityExchange.IsUnavailable(error))
        {
            // An incomplete or refused exchange supplies no topology or commission.
        }
    }
}

internal static class CatalogAuthorityExchange
{
    internal static async Task<CatalogAuthoritySnapshot?> ReadAsync(CatalogAuthorityPeer peer, CatalogAuthorityScope scope,
        string operation, int version, CommissionReadBudget budget, CancellationToken ct)
    {
        CheckBudget(budget, ct);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(peer.SocketPath), ct).ConfigureAwait(false);
        CheckBudget(budget, ct);

        using var network = new NetworkStream(socket, ownsSocket: false);
        using var tls = new SslStream(network, leaveInnerStreamOpen: true, (_, remote, _, _) =>
            remote is not null && string.Equals(remote.GetCertHashString(HashAlgorithmName.SHA256), peer.LeafSha256,
                StringComparison.OrdinalIgnoreCase));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = operation }, ct)
            .ConfigureAwait(false);
        CheckBudget(budget, ct);

        var request = new CatalogAuthorityRequest(operation, version, scope,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(CatalogAuthorityV2.NonceBytes)));
        await BoundedJsonFrame.WriteAsync(tls, request, CatalogAuthorityJsonContext.Default.CatalogAuthorityRequest,
            CatalogAuthorityV2.MaxRequest, ct).ConfigureAwait(false);
        CheckBudget(budget, ct);

        var reply = await BoundedJsonFrame.ReadAsync(tls, CatalogAuthorityJsonContext.Default.CatalogAuthorityReply,
            CatalogAuthorityV2.MaxReply, ct).ConfigureAwait(false);
        CheckBudget(budget, ct);
        return reply.Matches(request) ? reply.Snapshot : null;
    }

    internal static void CheckBudget(CommissionReadBudget budget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (budget.Remaining == TimeSpan.Zero) throw new OperationCanceledException(ct);
    }

    internal static bool IsUnavailable(Exception error) =>
        error is IOException or SocketException or AuthenticationException or JsonException or UnauthorizedAccessException
            or RouterConfigException or FormatException or ArgumentException or OperationCanceledException;
}

internal sealed record CheckedEnrolledStartup(CheckedEnrolledConfiguration Configured, CatalogAuthoritySnapshot Authority);

internal sealed class CatalogAuthorityIntake(CatalogAuthorityBootstrap bootstrap, TimeProvider clock)
{
    internal Action? BeforeProjectionForTests { private get; init; }
    internal Action? AfterProjectionForTests { private get; init; }

    internal async ValueTask<CheckedEnrolledCatalog?> ReadAsync(string toml, EnrolledCatalogClaim claim, CancellationToken ct) =>
        (await ReadStartupAsync(toml, claim, ct).ConfigureAwait(false))?.Configured.Catalog;

    internal async Task<bool> IsCurrentAsync(CatalogAuthoritySnapshot expected, CancellationToken ct)
    {
        var budget = new CommissionReadBudget(clock, clock.GetTimestamp(), CatalogAuthorityV2.Timeout);
        using var timer = new CancellationTokenSource(budget.Remaining, clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var token = lifetime.Token;
        try
        {
            var observer = await CatalogAuthorityExchange.ReadAsync(bootstrap.Observer, bootstrap.Scope,
                CatalogAuthorityV2.ReadCommission, CatalogAuthorityV2.CommissionVersion, budget, token).ConfigureAwait(false);
            if (observer is null || !observer.Matches(expected)) return false;
            var current = await CatalogAuthorityExchange.ReadAsync(bootstrap.Head, bootstrap.Scope,
                CatalogAuthorityV2.ReadTopology, CatalogAuthorityV2.TopologyVersion, budget, token).ConfigureAwait(false);
            return current is not null && current.Matches(expected);
        }
        catch (Exception error) when (CatalogAuthorityExchange.IsUnavailable(error))
        {
            return false;
        }
    }

    internal async ValueTask<CheckedEnrolledStartup?> ReadStartupAsync(string toml, EnrolledCatalogClaim claim, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !bootstrap.Scope.IsValid() ||
            !bootstrap.Observer.IsValid() || !bootstrap.Head.IsValid() ||
            bootstrap.Observer.SocketPath == bootstrap.Head.SocketPath ||
            string.Equals(bootstrap.Observer.LeafSha256, bootstrap.Head.LeafSha256, StringComparison.OrdinalIgnoreCase))
            return null;

        var budget = new CommissionReadBudget(clock, clock.GetTimestamp(), CatalogAuthorityV2.Timeout);
        using var timer = new CancellationTokenSource(budget.Remaining, clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var token = lifetime.Token;
        try
        {
            var observer = await CatalogAuthorityExchange.ReadAsync(bootstrap.Observer, bootstrap.Scope,
                CatalogAuthorityV2.ReadCommission, CatalogAuthorityV2.CommissionVersion, budget, token).ConfigureAwait(false);
            if (observer is null) return null;

            var current = await CatalogAuthorityExchange.ReadAsync(bootstrap.Head, bootstrap.Scope,
                CatalogAuthorityV2.ReadTopology, CatalogAuthorityV2.TopologyVersion, budget, token).ConfigureAwait(false);
            if (current is null || current.Catalog.Head != claim.Head || !current.Matches(observer)) return null;

            CatalogAuthorityExchange.CheckBudget(budget, token);
            BeforeProjectionForTests?.Invoke();
            CatalogAuthorityExchange.CheckBudget(budget, token);
            // Parsing is synchronous. A projection that overruns the cutoff supplies no result.
            var configured = RouterConfigLoader.PrepareEnrolledConfiguration(toml, bootstrap.Scope.Slot, claim, current.Catalog);
            AfterProjectionForTests?.Invoke();
            CatalogAuthorityExchange.CheckBudget(budget, token);
            return new(configured, current);
        }
        catch (Exception error) when (CatalogAuthorityExchange.IsUnavailable(error))
        {
            return null;
        }
    }
}
