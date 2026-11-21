using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

// Supplied by trusted bootstrap, outside candidate configuration and the launch journal.
internal sealed record CommissionBootstrap(HostReservationScope Scope, string CommissionedSlot, string SocketPath,
    string ObserverLeafSha256);

/// <summary>Read-only preparation of the closed pair. This entry has no launch or serving dependencies.</summary>
internal sealed class CommissionIntake(CommissionBootstrap bootstrap, ICommissioningHeadReader head, TimeProvider clock)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal Action<int, CancellationToken>? PayloadReadPendingForTests { private get; init; }
    internal string? ChallengeForTests { private get; init; }
    internal Action? BeforePairParseForTests { private get; init; }

    internal async ValueTask<CheckedEnrolledPair?> ReadAsync(string toml, EnrolledPairClaim claim, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || bootstrap.Scope.Enrollment?.IsValid != true || bootstrap.Scope.Identity == Guid.Empty ||
            !Path.IsPathFullyQualified(bootstrap.SocketPath) || bootstrap.SocketPath.Contains('\0', StringComparison.Ordinal) ||
            !HostMutation.IsPlanDigest(bootstrap.ObserverLeafSha256))
            return null;

        var budget = new CommissionReadBudget(clock, clock.GetTimestamp(), Timeout);
        using var timer = new CancellationTokenSource(budget.Remaining, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var token = linked.Token;
        try
        {
            var request = new CommissionRequest(CommissionCodec.Version, bootstrap.CommissionedSlot,
                ChallengeForTests ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(CommissionCodec.ChallengeBytes)));
            if (!CommissionCodec.IsRequestFor(request, bootstrap.CommissionedSlot)) return null;

            var reply = await ReadReplyAsync(request, budget, token).ConfigureAwait(false);
            CheckBudget(budget, token);
            if (!CommissionCodec.MatchesReply(reply, request) ||
                !reply.Commission.AuthorizesScope(bootstrap.Scope, bootstrap.CommissionedSlot))
                return null;

            CheckBudget(budget, token);
            var current = await head.ReadCurrentAsync(bootstrap.CommissionedSlot, budget, token).ConfigureAwait(false);
            CheckBudget(budget, token);
            if (current is null || !current.AuthorizesScope(bootstrap.Scope, bootstrap.CommissionedSlot) ||
                current.Topology.Head != claim.Head || !current.Matches(reply.Commission))
                return null;

            CheckBudget(budget, token);
            BeforePairParseForTests?.Invoke();
            var validated = RouterConfigLoader.ValidateEnrolledPair(toml, bootstrap.CommissionedSlot, claim, current.Topology);
            CheckBudget(budget, token);
            var pair = validated.Project();
            var approved = current.ApprovesPair(pair);
            CheckBudget(budget, token);
            return approved ? pair : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || budget.Remaining == TimeSpan.Zero)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or SocketException or AuthenticationException or JsonException
            or UnauthorizedAccessException or RouterConfigException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private async ValueTask<CommissionReply> ReadReplyAsync(CommissionRequest request, CommissionReadBudget budget,
        CancellationToken ct)
    {
        CheckBudget(budget, ct);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(bootstrap.SocketPath), ct).ConfigureAwait(false);
        CheckBudget(budget, ct);
        using var network = new NetworkStream(socket, ownsSocket: false);
        using var tls = new SslStream(network, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "observer",
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null &&
                string.Equals(Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)),
                    bootstrap.ObserverLeafSha256, StringComparison.OrdinalIgnoreCase),
        }, ct).ConfigureAwait(false);
        CheckBudget(budget, ct);
        await CommissionCodec.WriteRequestAsync(tls, request, ct).ConfigureAwait(false);
        CheckBudget(budget, ct);
        return await CommissionCodec.ReadReplyAsync(tls, PayloadReadPendingForTests, ct).ConfigureAwait(false);
    }

    private static void CheckBudget(CommissionReadBudget budget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (budget.Remaining == TimeSpan.Zero) throw new OperationCanceledException(ct);
    }
}
