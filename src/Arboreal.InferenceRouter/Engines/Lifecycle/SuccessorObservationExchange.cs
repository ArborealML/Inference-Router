using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record SuccessorAuthorityPeer(string SocketPath, string LeafSha256)
{
    internal bool IsValid() => OperationHeadProtocol.IsCanonicalDigest(LeafSha256) &&
        Path.IsPathFullyQualified(SocketPath) && !SocketPath.Contains('\0', StringComparison.Ordinal) &&
        SocketPath == Path.GetFullPath(SocketPath);
}

internal enum SuccessorObservationKind { Occupancy = 1, Preflight, Worker, StopEffect, LaunchEffect }

// The observer gets the installed identity and commitments, never a proposed Create body or effect.
internal sealed record SuccessorObservationRequest(int Version, string Nonce, SuccessorObservationKind Kind,
    HostReservationScope Scope, EnrolledPairHead CatalogHead, Guid QueryId, long CheckpointRevision,
    string CheckpointDigest, HostCheckpointPhase Phase, EnrolledPlanKey Target, string PlanDigest, string CreateDigest,
    HostCheckpointLaunchEffect? Incumbent, VerifiedInstanceRoute? ExpectedIncumbentRoute,
    HostMutation? Operation, SwitchAttemptId? Attempt, Guid? CloseId)
{
    internal bool IsValidFor(EnrolledSuccessorScope scope)
    {
        if (Version != SuccessorObservationExchange.Version || Scope != scope.Reservation || CatalogHead != scope.Commission.Head ||
            !OperationHeadProtocol.IsCanonicalDigest(Nonce) || QueryId == Guid.Empty || CheckpointRevision <= 0 ||
            !HostMutation.IsPlanDigest(CheckpointDigest) || !HostMutation.IsPlanDigest(PlanDigest) ||
            !HostMutation.IsPlanDigest(CreateDigest) || string.IsNullOrWhiteSpace(Target.Engine) ||
            string.IsNullOrWhiteSpace(Target.Model) || string.IsNullOrWhiteSpace(Target.Profile) ||
            scope.Commission.Approvals.IsDefault || !scope.Commission.Approvals.Any(approval =>
                approval is { Plan: { } plan, Launch.Intent: { } intent } && plan.Key == Target &&
                plan.PlanDigest == PlanDigest && intent.CreateDigest == CreateDigest)) return false;

        if (Kind == SuccessorObservationKind.Preflight)
            return Operation is null && Attempt is null && CloseId is null;
        if (Kind == SuccessorObservationKind.Occupancy)
            return Operation is null && Attempt is null && (CloseId is null ||
                CloseId != Guid.Empty && Phase is (HostCheckpointPhase.StopPrepared or HostCheckpointPhase.StopApplied or
                    HostCheckpointPhase.LaunchDispatching) && ExpectedIncumbentRoute is { } sealedRoute &&
                Incumbent is { } retiredResident && EnrolledSuccessorScope.BindsResidentRoute(Scope, sealedRoute, retiredResident));

        if (Operation is not { } operation || operation.Id == Guid.Empty || operation.Epoch <= 0 ||
            Attempt is not { Sequence: > 0 } attempt || attempt.RuntimeEpoch == Guid.Empty ||
            CloseId is not { } close || close == Guid.Empty || ExpectedIncumbentRoute is not { } route ||
            Incumbent is not { } resident || !EnrolledSuccessorScope.BindsResidentRoute(Scope, route, resident) ||
            operation.Epoch <= resident.Operation.Epoch) return false;

        var exactStop = operation.Kind == HostMutationKind.Stop && operation.Engine == resident.Plan.Engine &&
            operation.Target == resident.InstanceId && operation.Profile is null && operation.PlanDigest is null;
        var exactLaunch = operation.Kind == HostMutationKind.Launch && operation.Engine == Target.Engine &&
            operation.Target == Target.Model && operation.Profile == Target.Profile && operation.PlanDigest == PlanDigest;

        return Kind switch
        {
            SuccessorObservationKind.Worker => Phase == HostCheckpointPhase.StopDispatching && exactStop ||
                Phase == HostCheckpointPhase.LaunchDispatching && exactLaunch,
            SuccessorObservationKind.StopEffect => Phase == HostCheckpointPhase.StopDispatching && exactStop,
            SuccessorObservationKind.LaunchEffect => Phase == HostCheckpointPhase.LaunchDispatching && exactLaunch,
            _ => false,
        };
    }
}

internal sealed record SuccessorObservationReply(SuccessorObservationRequest Request, EnrolledSuccessorObservation Current,
    bool? PreflightApproved = null, EnrolledSuccessorRetirement? Retirement = null, SupervisedObservedCreate? Create = null,
    SupervisedObservedStart? Running = null, HostCheckpointStopEffect? Stopped = null, HostCheckpointLaunchEffect? Launched = null)
{
    internal bool Matches(SuccessorObservationRequest request, EnrolledSuccessorScope scope)
    {
        if (Request != request || Current.QueryId != request.QueryId || !scope.Matches(Current.Scope) ||
            Current.OwnerGeneration <= 0 || Current.IngressGeneration <= 0 || Current.CoverageGeneration <= 0 ||
            !Enum.IsDefined(Current.Occupancy) ||
            (Current.ResidentRoute is { } route && (Current.Resident is not { } resident ||
                !EnrolledSuccessorScope.BindsResidentRoute(request.Scope, route, resident))) ||
            (Retirement is { } retirement && (retirement.CloseId != request.CloseId ||
                retirement.SealedRoute != request.ExpectedIncumbentRoute || retirement.Incumbent != request.Incumbent))) return false;

        return request.Kind switch
        {
            SuccessorObservationKind.Occupancy => this is { PreflightApproved: null,
                Create: null, Running: null, Stopped: null, Launched: null } &&
                (request.CloseId is null ? Retirement is null :
                    Current.IngressClosed && Retirement is { } closed && closed.OwnerGeneration == Current.OwnerGeneration &&
                    closed.IngressGeneration == Current.IngressGeneration),
            SuccessorObservationKind.Preflight => this is { PreflightApproved: not null, Retirement: null,
                Create: null, Running: null, Stopped: null, Launched: null },
            SuccessorObservationKind.Worker => this is { PreflightApproved: null, Retirement: not null, Stopped: null, Launched: null } &&
                (request.Operation?.Kind == HostMutationKind.Launch || this is { Create: null, Running: null }),
            SuccessorObservationKind.StopEffect => this is { PreflightApproved: null, Retirement: not null,
                Create: null, Running: null, Stopped: not null, Launched: null },
            SuccessorObservationKind.LaunchEffect => this is { PreflightApproved: null, Retirement: not null,
                Create: null, Running: null, Stopped: null, Launched: not null },
            _ => false,
        };
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SuccessorObservationRequest))]
[JsonSerializable(typeof(SuccessorObservationReply))]
internal sealed partial class SuccessorObservationJsonContext : JsonSerializerContext;

// The listener and independent fact reader belong to the commissioned observer. This exchange
// authenticates one bounded read and cannot issue mutation or grant ingress.
internal static class SuccessorObservationExchange
{
    internal const int Version = 1;
    internal const int RequestLimit = 16 * 1024;
    internal const int ReplyLimit = 2 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    internal static async ValueTask<SuccessorObservationReply?> ReadAsync(SuccessorAuthorityPeer peer,
        EnrolledSuccessorScope scope, SuccessorObservationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !request.IsValidFor(scope)) return null;

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var budget = Start(lifetime);
        try
        {
            if (!peer.IsValid()) return null;
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(peer.SocketPath), budget.Token).ConfigureAwait(false);
            using var wire = new NetworkStream(socket, ownsSocket: false);
            using var tls = new SslStream(wire, leaveInnerStreamOpen: true, (_, certificate, _, _) =>
                string.Equals(certificate?.GetCertHashString(HashAlgorithmName.SHA256), peer.LeafSha256, StringComparison.Ordinal));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "successor-observer" },
                budget.Token).ConfigureAwait(false);
            budget.Check();
            await BoundedJsonFrame.WriteAsync(tls, request, SuccessorObservationJsonContext.Default.SuccessorObservationRequest,
                RequestLimit, budget.Token).ConfigureAwait(false);
            var reply = await BoundedJsonFrame.ReadAsync(tls, SuccessorObservationJsonContext.Default.SuccessorObservationReply,
                ReplyLimit, budget.Token).ConfigureAwait(false);
            var matches = reply.Matches(request, scope);
            budget.Check();
            return matches ? reply : null;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    internal static async Task ServeAsync(Socket accepted, uint requesterUid, X509Certificate2 certificate,
        EnrolledSuccessorScope scope, Func<SuccessorObservationRequest, CancellationToken, ValueTask<SuccessorObservationReply?>> read,
        CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var budget = Start(lifetime);
        try
        {
            if (!HeadReadV1Endpoint.IsAuthorizedRequester(accepted, requesterUid)) return;
            using var wire = new NetworkStream(accepted, ownsSocket: false);
            using var tls = new SslStream(wire, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate },
                budget.Token).ConfigureAwait(false);
            var request = await BoundedJsonFrame.ReadAsync(tls, SuccessorObservationJsonContext.Default.SuccessorObservationRequest,
                RequestLimit, budget.Token).ConfigureAwait(false);
            budget.Check();
            if (!request.IsValidFor(scope)) return;
            var reply = await read(request, budget.Token).ConfigureAwait(false);
            if (reply is null || !reply.Matches(request, scope)) return;
            budget.Check();
            await BoundedJsonFrame.WriteAsync(tls, reply, SuccessorObservationJsonContext.Default.SuccessorObservationReply,
                ReplyLimit, budget.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            // The peer receives no authoritative observation.
        }
    }

    private static OperationHeadBudget Start(CancellationTokenSource lifetime)
    {
        var budget = new OperationHeadBudget(TimeProvider.System.GetTimestamp(), Lifetime, lifetime.Token);
        lifetime.CancelAfter(Lifetime);
        return budget;
    }
}
