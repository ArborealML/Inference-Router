using System.Collections.Immutable;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Arboreal.InferenceRouter.Lifecycle;

// An independently maintained snapshot of the live exclusion boundary and accepted daemon
// operations. Revision and digest commit the entire snapshot. A Docker census cannot mint it.
internal sealed record SuccessorPhysicalSnapshot(
    HostReservationScope Scope, EnrolledPairHead Catalog, long Revision, string Digest,
    long OwnerGeneration, long CoverageGeneration, long IngressGeneration, bool Exclusive, bool IngressClosed,
    ImmutableArray<string> CoveredDevices, HostCheckpointLaunchEffect? Resident, VerifiedInstanceRoute? ResidentRoute,
    DockerImageIdentity? ResidentImage, bool ResidentRunning, EnrolledPlanKey? PreflightTarget,
    SupervisedObservedCreate? Create, SupervisedObservedStart? Start, HostCheckpointStopEffect? Stop,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<EnrolledPlanKey> PreflightTargets = default);

// Composes independent installed evidence, a fresh unfiltered Docker census and the broker's
// read-only terminal frontier. The caller opens the listener and provides only scoped read ports.
internal sealed class CommissionedPhysicalSuccessorObserver(
    EnrolledSuccessorScope scope, string checkpointLock, DockerLifecycleProvider.SuccessorCensus daemon,
    Func<CancellationToken, ValueTask<SuccessorPhysicalSnapshot?>> readPartition,
    Func<Guid, CancellationToken, ValueTask<EnrolledSuccessorRetirement?>> readRetirement)
{
    internal async Task RunAsync(Socket listener, uint requesterUid, X509Certificate2 certificate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var accepted = await listener.AcceptAsync(ct).ConfigureAwait(false);
            await SuccessorObservationExchange.ServeAsync(accepted, requesterUid, certificate, scope, ObserveAsync, ct)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<SuccessorObservationReply?> ObserveAsync(SuccessorObservationRequest request, CancellationToken ct)
    {
        try
        {
            var installed = HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock);
            var transition = installed.State.Transition;
            if (installed.State.Revision != request.CheckpointRevision || installed.Digest != request.CheckpointDigest ||
                installed.State.Phase != request.Phase || installed.State.AppliedLaunch != request.Incumbent ||
                request.Operation is { } operation && operation != (operation.Kind == HostMutationKind.Stop
                    ? transition?.Stop : transition?.Launch)) return null;

            var frontier = request.CloseId is { } closeId
                ? await readRetirement(closeId, ct).ConfigureAwait(false) : null;
            var sample = await readPartition(ct).ConfigureAwait(false);
            if (sample is null || !Matches(request, sample, transition, frontier) ||
                !await daemon.CorroborateAsync(scope.Commission.Head.RouterId, sample.Resident, sample.ResidentRunning,
                    sample.ResidentImage, sample.Stop, ct).ConfigureAwait(false)) return null;

            var approval = scope.Commission.Approvals.Single(item => item.Plan.Key == request.Target);
            var imageAvailable = request.Kind == SuccessorObservationKind.Preflight &&
                await daemon.HasImageAsync(approval.Launch.Image, ct).ConfigureAwait(false);
            if (installed.State.Phase == HostCheckpointPhase.LaunchDispatching &&
                sample.Resident is { } candidate && candidate.Operation == transition?.Launch)
            {
                if (sample.Create is not { } accepted || accepted.InstanceId != candidate.InstanceId ||
                    await daemon.ReadEffectiveCreateAsync(candidate.InstanceId, approval.Launch.Image, ct)
                        .ConfigureAwait(false) is not { } effective) return null;
                sample = sample with { Create = accepted with { EffectiveSettings = effective } };
            }
            var after = await readPartition(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (after is null || !Matches(request, after, transition, frontier) ||
                after.Revision != sample.Revision || after.Digest != sample.Digest ||
                (frontier is not null && await readRetirement(frontier.CloseId, ct).ConfigureAwait(false) != frontier) ||
                HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock) != installed) return null;

            var current = new EnrolledSuccessorObservation(request.QueryId, scope, sample.OwnerGeneration,
                sample.CoverageGeneration, sample.IngressGeneration, CompleteCoverage: true, sample.IngressClosed,
                sample.Resident is null ? HostOccupancyKind.Vacant : HostOccupancyKind.Owned,
                sample.Resident, sample.ResidentRoute);
            return MakeReply(request, installed.State, sample, current, frontier, approval, imageAvailable);
        }
        catch (HttpRequestException) { return null; }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    private bool Matches(SuccessorObservationRequest request, SuccessorPhysicalSnapshot sample,
        HostCheckpointTransition? transition, EnrolledSuccessorRetirement? frontier)
    {
        if (sample.Scope != scope.Reservation || sample.Catalog != scope.Commission.Head || sample.Revision <= 0 ||
            !OperationHeadProtocol.IsCanonicalDigest(sample.Digest) || !sample.Exclusive || sample.OwnerGeneration <= 0 ||
            sample.CoverageGeneration <= 0 || sample.IngressGeneration <= 0 ||
            !SupervisedLaunchCommission.SameDevices(scope.Commission.Devices, sample.CoveredDevices))
            return false;

        if (sample.Resident is { } resident)
        {
            if (!resident.IsValid() || !SupervisedLaunchEvidence.IsFullContainerId(resident.InstanceId) ||
                sample.ResidentImage is null || !scope.Commission.Approvals.Any(approval =>
                    approval.Launch.Intent.Plan == resident.Plan && approval.Launch.Image == sample.ResidentImage) ||
                (sample.ResidentRoute is { } route && (!sample.ResidentRunning ||
                    !EnrolledSuccessorScope.BindsResidentRoute(scope.Reservation, route, resident))) ||
                (!sample.IngressClosed && sample.ResidentRoute is null)) return false;
        }
        else if (sample.ResidentRoute is not null || sample.ResidentImage is not null || sample.ResidentRunning)
            return false;

        return request.CloseId is null ? frontier is null : sample.IngressClosed && frontier is not null &&
            frontier.CloseId == request.CloseId && frontier.QueryId != Guid.Empty && frontier.Stop == transition?.Stop &&
            frontier.Incumbent == request.Incumbent && frontier.SealedRoute == request.ExpectedIncumbentRoute &&
            frontier.OwnerGeneration == sample.OwnerGeneration && frontier.IngressGeneration == sample.IngressGeneration;
    }

    private SuccessorObservationReply? MakeReply(SuccessorObservationRequest request, HostEffectCheckpointState installed,
        SuccessorPhysicalSnapshot sample, EnrolledSuccessorObservation current, EnrolledSuccessorRetirement? frontier,
        EnrolledCatalogApproval approval, bool imageAvailable)
    {
        if (request.Kind == SuccessorObservationKind.Occupancy)
        {
            if (frontier is not null && !MatchesClosedOccupancy(installed, sample, frontier)) return null;
            if (installed.Phase == HostCheckpointPhase.LaunchDispatching && frontier is not null &&
                (installed.Transition?.Launch is not { } launchOperation ||
                 MatchLaunch(sample, approval, launchOperation) is null)) return null;
            return new(request, current, Retirement: frontier);
        }
        if (request.Kind == SuccessorObservationKind.Preflight)
            return new(request, current, PreflightApproved: imageAvailable && !sample.IngressClosed && sample.ResidentRunning &&
                (sample.PreflightTarget == request.Target ||
                    !sample.PreflightTargets.IsDefault && sample.PreflightTargets.Contains(request.Target)) &&
                current.Resident == installed.AppliedLaunch &&
                current.ResidentRoute == request.ExpectedIncumbentRoute);
        if (frontier is null) return null;

        if (request.Operation == frontier.Stop)
        {
            if (request.Kind == SuccessorObservationKind.Worker)
                return new(request, current, Retirement: frontier);
            return request.Kind == SuccessorObservationKind.StopEffect && sample.Stop is { } stop &&
                stop.Matches(frontier.Stop, frontier.Incumbent)
                ? new(request, current, Retirement: frontier, Stopped: stop) : null;
        }

        if (request.Operation is not { Kind: HostMutationKind.Launch } launch ||
            installed.Transition?.Launch != launch) return null;
        if (sample.Resident is null)
            return request.Kind == SuccessorObservationKind.Worker && sample.Create is null && sample.Start is null
                ? new(request, current, Retirement: frontier) : null;

        if (MatchLaunch(sample, approval, launch) is null) return null;

        return request.Kind switch
        {
            SuccessorObservationKind.Worker => new(request, current, Retirement: frontier,
                Create: sample.Create, Running: sample.Start),
            SuccessorObservationKind.LaunchEffect when sample.ResidentRunning =>
                new(request, current, Retirement: frontier, Launched: sample.Resident),
            _ => null,
        };
    }

    private HostInitialCreate? MatchLaunch(SuccessorPhysicalSnapshot sample, EnrolledCatalogApproval approval,
        HostMutation launch)
    {
        var grant = scope.Commission.Engines.Single(item => item.Engine == launch.Engine);
        var command = new SupervisedLaunchCommand(scope.Reservation, scope.Commission.Head, grant.SocketPath, launch,
            approval.Launch.ApiVersion!);
        var created = SupervisedLaunchEvidence.VerifyCreate(sample.Create, command, approval.Launch,
            approval.Plan.BackendRoute, approval.Plan.Endpoint, scope.Commission.Devices);
        if (created is null || sample.Resident != new HostCheckpointLaunchEffect(launch, approval.Launch.Intent.Plan,
                created.InstanceId)) return null;

        var started = new SupervisedObservedStart(command, created.InstanceId, Running: true);
        return (sample.ResidentRunning ? sample.Start == started : sample.Start is null) ? created : null;
    }

    private static bool MatchesClosedOccupancy(HostEffectCheckpointState installed, SuccessorPhysicalSnapshot sample,
        EnrolledSuccessorRetirement frontier) => installed.Phase switch
    {
        HostCheckpointPhase.StopPrepared => sample.ResidentRunning && sample.Resident == frontier.Incumbent &&
            sample.ResidentRoute == frontier.SealedRoute,
        HostCheckpointPhase.StopApplied => sample.Resident is null && sample.Stop == installed.Transition?.StopEffect &&
            sample.Stop?.Matches(frontier.Stop, frontier.Incumbent) == true,
        HostCheckpointPhase.LaunchDispatching => sample.ResidentRunning && sample.Resident is { } started &&
            installed.Transition is { Launch: { } launch } transition && started.Operation == launch &&
            started.Plan == transition.Plan && sample.Stop == transition.StopEffect &&
            sample.Stop?.Matches(frontier.Stop, frontier.Incumbent) == true,
        _ => false,
    };
}
