using System.Net.Sockets;
using System.Security.Cryptography;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed class CommissionedSuccessorObserver(string checkpointLock, EnrolledSuccessorScope scope,
    SuccessorAuthorityPeer peer,
    Func<EnrolledSuccessorQuery, CancellationToken, ValueTask<EnrolledSuccessorRetirement?>> sealAndRetire)
    : IEnrolledSuccessorWorkerObserver
{
    public async ValueTask<EnrolledSuccessorObservation?> ObserveAsync(EnrolledSuccessorQuery query, CancellationToken ct)
    {
        var reply = await ReadAsync(query, SuccessorObservationKind.Occupancy, issue: null, ct).ConfigureAwait(false);
        return reply is not null && reply.Retirement == query.ExpectedRetirement
            ? reply.Current with { Retirement = reply.Retirement } : null;
    }

    public async ValueTask<bool> PreflightAsync(EnrolledSuccessorQuery query, CancellationToken ct)
    {
        if (query.ExpectedIncumbentRoute is null) return false;
        var reply = await ReadAsync(query, SuccessorObservationKind.Preflight, issue: null, ct).ConfigureAwait(false);
        return reply is { PreflightApproved: true, Current: { CompleteCoverage: true, IngressClosed: false,
            Occupancy: HostOccupancyKind.Owned } current } && current.Resident == query.Checkpoint.AppliedLaunch &&
            current.ResidentRoute == query.ExpectedIncumbentRoute;
    }

    public async ValueTask<EnrolledSuccessorRetirement?> CloseAndDrainAsync(EnrolledSuccessorQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (query.Id == Guid.Empty || !scope.Matches(query.Scope) || !scope.Approves(query.Successor) ||
            query.Checkpoint.AppliedLaunch is not { } resident || query.ProposedStop is not { } stop ||
            !HostEffectCheckpoint.BindsProposedStop(query.Checkpoint, stop) || query.ExpectedIncumbentRoute is not { } route ||
            !EnrolledSuccessorScope.BindsResidentRoute(scope.Reservation, route, resident)) return null;

        try
        {
            var before = HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock);
            if (before.State != query.Checkpoint) return null;
            // Only the commissioned broker can close the exact certified route and attest retirement.
            var retired = await sealAndRetire(query, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return retired is { OwnerGeneration: > 0, IngressGeneration: > 0 } && retired.QueryId == query.Id &&
                retired.CloseId != Guid.Empty && retired.Stop == stop && retired.Incumbent == resident && retired.Approves(query.Successor) &&
                retired.SealedRoute == route && HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock) == before ? retired : null;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    public async ValueTask<EnrolledSuccessorWorkerObservation?> ObserveWorkerAsync(EnrolledSuccessorIssue issue, CancellationToken ct)
    {
        var reply = await ReadAsync(issue.Query, SuccessorObservationKind.Worker, issue, ct).ConfigureAwait(false);
        return reply is { Retirement: { } retirement } && retirement == issue.Retirement
            ? new(reply.Current, retirement, reply.Create, reply.Running) : null;
    }

    public async ValueTask<HostCheckpointStopEffect?> ObserveStopAsync(EnrolledSuccessorIssue issue, CancellationToken ct)
    {
        var reply = await ReadAsync(issue.Query, SuccessorObservationKind.StopEffect, issue, ct).ConfigureAwait(false);
        return reply is { Stopped: { } effect } && reply.Retirement == issue.Retirement &&
            effect.Matches(issue.Retirement.Stop, issue.Retirement.Incumbent) ? effect : null;
    }

    public async ValueTask<HostCheckpointLaunchEffect?> ObserveLaunchAsync(EnrolledSuccessorIssue issue, CancellationToken ct)
    {
        var reply = await ReadAsync(issue.Query, SuccessorObservationKind.LaunchEffect, issue, ct).ConfigureAwait(false);
        return reply is { Launched: { } effect } && reply.Retirement == issue.Retirement &&
            effect.Operation == issue.Dispatching.Transition?.Launch && effect.Plan == issue.Query.Successor.Plan &&
            effect.IsValid() && SupervisedLaunchEvidence.IsFullContainerId(effect.InstanceId) ? effect : null;
    }

    private async ValueTask<SuccessorObservationReply?> ReadAsync(EnrolledSuccessorQuery query, SuccessorObservationKind kind,
        EnrolledSuccessorIssue? issue, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!scope.Matches(query.Scope) || !scope.Approves(query.Successor)) return null;
        try
        {
            var installed = HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock);
            if (installed.State != (issue?.Dispatching ?? query.Checkpoint)) return null;
            var operation = issue is null ? null : installed.State.Phase == HostCheckpointPhase.StopDispatching
                ? installed.State.Transition?.Stop : installed.State.Transition?.Launch;
            var request = new SuccessorObservationRequest(SuccessorObservationExchange.Version,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(OperationHeadProtocol.NonceBytes)), kind,
                scope.Reservation, scope.Commission.Head, query.Id, installed.State.Revision, installed.Digest,
                installed.State.Phase, query.Successor.Key, query.Successor.PlanDigest, query.Successor.Intent.CreateDigest,
                installed.State.AppliedLaunch, query.ExpectedIncumbentRoute, operation, issue?.Attempt,
                (issue?.Retirement ?? query.ExpectedRetirement)?.CloseId);

            var reply = await SuccessorObservationExchange.ReadAsync(peer, scope, request, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return reply is not null && HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock) == installed ? reply : null;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }
}

internal static class CommissionedSuccessorWorker
{
    // The scope, both peer pins, and the broker call come from protected supervisor configuration.
    // This constructs the production-shaped worker and does not start a router or open ingress.
    internal static EnrolledDockerSuccessorWorker Connect(string checkpointLock, EnrolledSuccessorScope scope,
        RouterConfig configuration, CheckedEnrolledCatalog catalog, SuccessorAuthorityPeer observerPeer,
        SuccessorAuthorityPeer operationHeadPeer,
        Func<EnrolledSuccessorQuery, CancellationToken, ValueTask<EnrolledSuccessorRetirement?>> sealAndRetire)
    {
        if (!OperatingSystem.IsLinux() || !observerPeer.IsValid() || !operationHeadPeer.IsValid() ||
            observerPeer.SocketPath == operationHeadPeer.SocketPath || observerPeer.LeafSha256 == operationHeadPeer.LeafSha256 ||
            !Path.IsPathFullyQualified(checkpointLock) || checkpointLock.Contains('\0', StringComparison.Ordinal) ||
            checkpointLock != Path.GetFullPath(checkpointLock) ||
            !OperationHeadProtocol.IsScopeValid(scope.Reservation) || scope.Commission.Head != catalog.Head ||
            !SupervisedLaunchCommission.SameDevices(scope.Commission.Devices, catalog.Devices) || catalog.Targets.IsDefaultOrEmpty ||
            !catalog.Targets.All(target => scope.Approves(target) && configuration.Engines.TryGetValue(target.Key.Engine, out var engine) &&
                catalog.OwnsConfiguredEngine(engine)))
            throw new InvalidOperationException("The commissioned successor worker bindings do not match the checked catalog.");

        var head = new OperationHeadClient(new UnixDomainSocketEndPoint(operationHeadPeer.SocketPath), operationHeadPeer.LeafSha256,
            scope.Reservation, OperationHeadProtocol.SuccessorVersion);
        var journal = new EnrolledSuccessorFileJournal(scope.Reservation, catalog.Head, head);
        var observer = new CommissionedSuccessorObserver(checkpointLock, scope, observerPeer, sealAndRetire);
        return new EnrolledDockerSuccessorWorker(checkpointLock, scope.Reservation, configuration, catalog, journal, observer);
    }
}
