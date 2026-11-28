using System.Collections.Immutable;

namespace Arboreal.InferenceRouter.Lifecycle;

// The checked projection is the candidate; the independent commission must still agree at issue.
internal sealed record EnrolledSuccessorScope(HostReservationScope Reservation, EnrolledCatalogAuthority Commission)
{
    internal static bool BindsResidentRoute(HostReservationScope scope, VerifiedInstanceRoute route,
        HostCheckpointLaunchEffect resident) => scope.Enrollment is { } enrollment && route.HostGroup == enrollment.Group &&
        route.EnrollmentGeneration == enrollment.Generation && route.Engine == resident.Plan.Engine &&
        route.EffectiveTarget == resident.Operation.PlanDigest && route.ContainerId == resident.InstanceId &&
        route.LaunchEpoch == resident.Operation.Epoch && route.RouteIncarnation > 0 &&
        !string.IsNullOrWhiteSpace(route.ServingIncarnation);

    internal bool Approves(CheckedEnrolledTarget target) => SupervisedLaunchApproval.IsPinnedApiVersion(target.ApiVersion) &&
        !Commission.Engines.IsDefault && !Commission.Approvals.IsDefault &&
        Commission.Engines.Contains(target.EngineGrant) && Commission.Approvals.Any(approval =>
            approval is { Plan: { } plan, Launch: { } launch } && plan.Key == target.Key &&
            plan.Configuration == target.Plan.Configuration && plan.BackendRoute == target.Plan.BackendRoute &&
            plan.PlanDigest == target.PlanDigest && VerifiedDispatchEnvelope.SameEffectiveUri(target.Endpoint, plan.Endpoint) &&
            launch == new SupervisedLaunchApproval(target.Intent, target.Image, target.ApiVersion));

    internal bool Matches(EnrolledSuccessorScope other) => Reservation == other.Reservation &&
        Commission.Head == other.Commission.Head && SameSet(Commission.Devices, other.Commission.Devices) &&
        SameSet(Commission.SequentialRoutes, other.Commission.SequentialRoutes) && SameSet(Commission.Engines, other.Commission.Engines) &&
        ComparableApprovals(Commission.Approvals) && ComparableApprovals(other.Commission.Approvals) &&
        SameSet(ToExactApprovals(Commission.Approvals), ToExactApprovals(other.Commission.Approvals));

    private static bool ComparableApprovals(ImmutableArray<EnrolledCatalogApproval> approvals) => !approvals.IsDefault &&
        approvals.All(approval => approval is { Plan: { } plan, Launch.Intent.Plan: not null } &&
            VerifiedDispatchEnvelope.IsHttpEndpoint(plan.Endpoint));

    private static ImmutableArray<(EnrolledCatalogPlan Plan, string Endpoint, SupervisedLaunchApproval Launch)> ToExactApprovals(
        ImmutableArray<EnrolledCatalogApproval> approvals) =>
        [.. approvals.Select(approval => (approval.Plan, approval.Plan.Endpoint.AbsoluteUri, approval.Launch))];

    private static bool SameSet<T>(ImmutableArray<T> left, ImmutableArray<T> right)
    {
        if (left.IsDefault || right.IsDefault || left.Length != right.Length) return false;
        var set = left.ToHashSet();
        return set.Count == left.Length && right.All(set.Remove) && set.Count == 0;
    }
}

internal sealed record EnrolledSuccessorOffer(EnrolledSuccessorController Controller,
    HostCheckpointLaunchEffect Incumbent, VerifiedInstanceRoute IncumbentRoute, CheckedEnrolledTarget Successor);

internal sealed record InstalledEnrolledSuccessor(EnrolledSuccessorOffer Offer,
    SwitchAttemptId Attempt, SwitchSampleKey Target, long RequiredWindow);

internal sealed record EnrolledSuccessorApplied(InstalledEnrolledSuccessor Installed, EnrolledSuccessorScope Scope,
    HostCheckpointLaunchEffect Launch, HostCheckpointStoppedResident Stopped, EnrolledSuccessorRetirement Retirement)
{
    internal bool MatchesStage(HostClosedRouteStage stage) => stage.ClaimId != Guid.Empty && stage.Launch == Launch &&
        Launch.Plan == Installed.Offer.Successor.Plan && Stopped.Incumbent == Installed.Offer.Incumbent &&
        Stopped.Effect.Matches(Stopped.Effect.Operation, Stopped.Incumbent) &&
        Stopped.Effect.Operation.Kind == HostMutationKind.Stop && Stopped.Effect.Operation.Epoch == Launch.Operation.Epoch &&
        Retirement.Incumbent == Stopped.Incumbent && Retirement.Stop == Stopped.Effect.Operation &&
        Retirement.SealedRoute == Installed.Offer.IncumbentRoute &&
        Scope.Reservation.Enrollment is { } enrollment && stage.Route.HostGroup == enrollment.Group &&
        stage.Route.EnrollmentGeneration == enrollment.Generation && stage.Route.Engine == Launch.Plan.Engine &&
        stage.Route.ContainerId == Launch.InstanceId && stage.Route.LaunchEpoch == Launch.Operation.Epoch &&
        stage.Route.EffectiveTarget == Launch.Operation.PlanDigest &&
        stage.Route.RouteIncarnation > Retirement.SealedRoute.RouteIncarnation &&
        !string.IsNullOrWhiteSpace(stage.Route.ServingIncarnation);
}

internal sealed record EnrolledSuccessorRuntime(EnrolledSuccessorController Controller,
    Func<HostStagedDeadlinePublication, EnrolledSuccessorApplied, CancellationToken, ValueTask<HostStagedSwitchResult>> StageAsync);

internal sealed record EnrolledInitialReadiness(VerifiedInstanceRoute Route, string CanonicalModel, ServingCapacity Serving);

internal sealed record EnrolledSuccessorQuery(Guid Id, EnrolledSuccessorScope Scope,
    HostEffectCheckpointState Checkpoint, CheckedEnrolledTarget Successor, VerifiedInstanceRoute? ExpectedIncumbentRoute,
    EnrolledSuccessorRetirement? ExpectedRetirement = null, HostMutation? ProposedStop = null);

internal sealed record EnrolledSuccessorObservation(Guid QueryId, EnrolledSuccessorScope Scope,
    long OwnerGeneration, long CoverageGeneration, long IngressGeneration, bool CompleteCoverage, bool IngressClosed,
    HostOccupancyKind Occupancy, HostCheckpointLaunchEffect? Resident, VerifiedInstanceRoute? ResidentRoute)
{
    // The exchange carries the broker witness separately from current physical occupancy.
    [System.Text.Json.Serialization.JsonIgnore]
    internal EnrolledSuccessorRetirement? Retirement { get; init; }
}

internal sealed record EnrolledSuccessorRetirement(Guid QueryId, Guid CloseId, HostCheckpointLaunchEffect Incumbent,
    HostMutation Stop, long OwnerGeneration, long IngressGeneration, VerifiedInstanceRoute SealedRoute,
    EnrolledPlanKey Successor, string PlanDigest, string CreateDigest)
{
    internal bool Approves(CheckedEnrolledTarget target) => Approves(target.Intent);

    internal bool Approves(HostInitialLaunchIntent intent) =>
        Successor == new EnrolledPlanKey(intent.Plan.Engine, intent.Plan.Model, intent.Plan.Tier ?? "") &&
        PlanDigest == intent.Plan.Digest() && CreateDigest == intent.CreateDigest;
}

internal sealed record EnrolledSuccessorIssue(EnrolledSuccessorQuery Query, EnrolledSuccessorObservation Observation,
    EnrolledSuccessorRetirement Retirement, HostEffectCheckpointState Dispatching, SwitchAttemptId Attempt)
{
    // The worker calls this against independently read current state under its own exclusion,
    // and holds that exclusion through possible-issue recording and daemon entry.
    internal bool CanIssue(HostEffectCheckpointState installed, EnrolledSuccessorObservation? current,
        EnrolledSuccessorRetirement? frontier)
    {
        if (installed != Dispatching || frontier != Retirement || !Retirement.Approves(Query.Successor) || current is not { IngressClosed: true } ||
            !Query.Scope.Matches(current.Scope) || current.QueryId != Query.Id ||
            Observation.QueryId != Query.Id || current.OwnerGeneration != Observation.OwnerGeneration ||
            current.IngressGeneration != Observation.IngressGeneration ||
            current.OwnerGeneration != Retirement.OwnerGeneration || current.IngressGeneration != Retirement.IngressGeneration ||
            current.CoverageGeneration != Observation.CoverageGeneration || !current.CompleteCoverage ||
            Query.ExpectedIncumbentRoute != Retirement.SealedRoute ||
            !EnrolledSuccessorScope.BindsResidentRoute(Query.Scope.Reservation, Retirement.SealedRoute, Retirement.Incumbent) ||
            installed.Transition is not { } transition || transition.Stop != Retirement.Stop ||
            installed.AppliedLaunch != Retirement.Incumbent || transition.Plan != Query.Successor.Plan)
            return false;

        var prior = Query.Checkpoint;
        return installed.Phase switch
        {
            HostCheckpointPhase.StopDispatching => prior.Phase == HostCheckpointPhase.StopPrepared &&
                installed == (prior with { Phase = HostCheckpointPhase.StopDispatching, Revision = prior.Revision + 1 }) &&
                current.Occupancy == HostOccupancyKind.Owned && current.Resident == Retirement.Incumbent &&
                current.ResidentRoute == Retirement.SealedRoute && Observation.ResidentRoute == Retirement.SealedRoute,
            HostCheckpointPhase.LaunchDispatching => prior.Phase == HostCheckpointPhase.StopApplied &&
                prior.Transition is { StopEffect: { } stopEffect } beforeLaunch &&
                stopEffect.Matches(Retirement.Stop, Retirement.Incumbent) &&
                transition.Launch is { } launch && Query.Successor.Plan.Matches(launch, installed.Epoch) &&
                installed == (prior with
                {
                    Phase = HostCheckpointPhase.LaunchDispatching, Revision = prior.Revision + 2,
                    Transition = beforeLaunch with { Launch = launch },
                }) && current.Occupancy == HostOccupancyKind.Vacant && current.Resident is null && current.ResidentRoute is null,
            _ => false,
        };
    }
}

// Read authority and effects independently of the worker's reply. CloseAndDrain returns only
// after conclusive retirement of every physical use at the exact closed frontier, or refuses.
internal interface IEnrolledSuccessorObserver
{
    ValueTask<EnrolledSuccessorObservation?> ObserveAsync(EnrolledSuccessorQuery query, CancellationToken ct);
    ValueTask<bool> PreflightAsync(EnrolledSuccessorQuery query, CancellationToken ct);
    ValueTask<EnrolledSuccessorRetirement?> CloseAndDrainAsync(EnrolledSuccessorQuery query, CancellationToken ct);
    ValueTask<HostCheckpointStopEffect?> ObserveStopAsync(EnrolledSuccessorIssue issue, CancellationToken ct);
    ValueTask<HostCheckpointLaunchEffect?> ObserveLaunchAsync(EnrolledSuccessorIssue issue, CancellationToken ct);
}

internal interface IEnrolledSuccessorHost : IEnrolledSuccessorObserver
{
    ValueTask<HostMutationOutcome> StopAsync(EnrolledSuccessorIssue issue, CancellationToken ct);
    ValueTask<HostMutationOutcome> LaunchAsync(EnrolledSuccessorIssue issue, CancellationToken ct);
}

internal sealed class EnrolledSuccessorController(string checkpointLock, EnrolledSuccessorScope scope,
    CheckedEnrolledCatalog catalog, IEnrolledSuccessorHost host)
{
    internal EnrolledSuccessorScope Scope => scope;

    internal bool Owns(EngineDefinition engine) => catalog.OwnsConfiguredEngine(engine) &&
        catalog.Targets.Any(member => member.Key.Engine == engine.Name && Approved(member));

    internal async ValueTask<EnrolledSuccessorOffer?> ObserveOfferAsync(string engine, string model, string tier,
        CancellationToken ct)
    {
        var key = new EnrolledPlanKey(engine, model, tier);
        var member = catalog.Targets.SingleOrDefault(member => member.Key == key);
        if (member is null || !Approved(member)) return null;

        try
        {
            using var checkpoint = HostEffectCheckpoint.Open(checkpointLock);
            var state = checkpoint.State;
            if (!CanSupersede(state, member, out var incumbent)) return null;
            var query = Query(state, member);
            var observed = await host.ObserveAsync(query, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return Matches(query, observed, HostOccupancyKind.Owned, incumbent) &&
                observed is { IngressClosed: false, ResidentRoute: { } route } &&
                EnrolledSuccessorScope.BindsResidentRoute(scope.Reservation, route, incumbent)
                ? new EnrolledSuccessorOffer(this, incumbent, route, member) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // Keep the checkpoint reservation through the short capability CAS, and obtain both observations
    // before entering transition. This seed cannot create a receipt or adopt a later successor.
    internal async ValueTask<bool> VerifyInitialResidentAsync(VerifiedDispatchResident resident,
        Func<VerifiedInstanceRoute, CancellationToken, Task<EnrolledInitialReadiness?>> observeReadiness,
        Func<EnrolledInitialReadiness, bool> publish, Func<HostCheckpointLaunchEffect, bool>? verifyAnchor, CancellationToken ct)
    {
        var target = resident.Target;
        var route = resident.Route;
        if (scope.Reservation.Enrollment is not { } enrollment ||
            route.HostGroup != enrollment.Group || route.EnrollmentGeneration != enrollment.Generation ||
            !target.Matches(route) || catalog.Targets.SingleOrDefault(member => member.Key ==
                new EnrolledPlanKey(target.Engine, target.Model, target.Tier)) is not { } member ||
            !Approved(member) || target.Model != member.Plan.Model || target.Tier != member.Plan.Tier ||
            target.PlanDigest != member.PlanDigest || !VerifiedDispatchEnvelope.SameEffectiveUri(target.Endpoint, member.Endpoint) ||
            !VerifiedDispatchEnvelope.SameEffectiveUri(resident.Endpoint, member.Endpoint)) return false;

        using var checkpoint = HostEffectCheckpoint.Open(checkpointLock);
        var current = checkpoint.State;
        if (current is not { Phase: HostCheckpointPhase.Resident, Epoch: 1, AppliedLaunch: { } launch } ||
            launch.Plan != member.Plan || launch.Operation.Epoch != route.LaunchEpoch ||
            launch.Operation.PlanDigest != route.EffectiveTarget || launch.InstanceId != route.ContainerId ||
            verifyAnchor?.Invoke(launch) == false) return false;

        ct.ThrowIfCancellationRequested();
        if (await observeReadiness(route, ct).ConfigureAwait(false) is not { } evidence || evidence.Route != route ||
            evidence.CanonicalModel != member.Plan.Model || evidence.Serving.Model != evidence.CanonicalModel ||
            evidence.Serving.Profile != member.Plan.Tier || evidence.Serving.ContextWindow != resident.MeasuredWindow ||
            evidence.Serving.TotalSlots != resident.TotalSlots || !resident.Fits(resident.TierWindow)) return false;

        ct.ThrowIfCancellationRequested();
        var query = Query(current, member);
        var observed = await host.ObserveAsync(query, ct).ConfigureAwait(false);
        return !ct.IsCancellationRequested && observed is { IngressClosed: false } && observed.ResidentRoute == route &&
            Matches(query, observed, HostOccupancyKind.Owned, launch) &&
            HostEffectCheckpoint.ReadInstalledState(checkpointLock) == current && verifyAnchor?.Invoke(launch) != false &&
            !ct.IsCancellationRequested && publish(evidence);
    }

    // The stable checkpoint lock remains held through closed readiness, cleanup and the Open decision.
    // This first path only starts from a retained resident. Dispatching state is never resent.
    internal async ValueTask<HostStagedSwitchResult> ActivateAsync(InstalledEnrolledSuccessor installed,
        Func<EnrolledSuccessorApplied, CancellationToken, ValueTask<HostStagedSwitchResult>> stageAsync,
        CancellationToken ct)
    {
        var offer = installed.Offer;
        var member = offer.Successor;
        if (!ReferenceEquals(offer.Controller, this) || installed.Attempt.RuntimeEpoch != installed.Target.RuntimeEpoch ||
            installed.Attempt.Sequence <= 0 || installed.Target.CanonicalModel != member.Plan.Model ||
            installed.Target.EffectiveProfile != member.Plan.Tier || !Approved(member)) return Refused();

        using var checkpoint = HostEffectCheckpoint.Open(checkpointLock);
        var current = checkpoint.State;
        if (!CanSupersede(current, member, out var incumbent) || incumbent != offer.Incumbent) return Refused();
        var incumbentRoute = offer.IncumbentRoute;
        var preflight = Query(current, member, incumbentRoute);
        var beforeClose = await host.ObserveAsync(preflight, ct).ConfigureAwait(false);
        if (!Matches(preflight, beforeClose, HostOccupancyKind.Owned, incumbent) ||
            beforeClose is not { IngressClosed: false } || beforeClose.ResidentRoute != incumbentRoute ||
            !EnrolledSuccessorScope.BindsResidentRoute(scope.Reservation, incumbentRoute, incumbent) ||
            !await host.PreflightAsync(preflight, ct).ConfigureAwait(false)) return Refused();
        ct.ThrowIfCancellationRequested();

        var stop = checkpoint.ProposeSuccessorStop(member.Plan);
        var stopQuery = Query(current, member, incumbentRoute) with { ProposedStop = stop };
        var retirement = await host.CloseAndDrainAsync(stopQuery, ct).ConfigureAwait(false);
        if (retirement is null || retirement.QueryId != stopQuery.Id || retirement.CloseId == Guid.Empty || retirement.Stop != stop ||
            retirement.Incumbent != incumbent || retirement.SealedRoute != incumbentRoute || !retirement.Approves(member)) return Refused();

        ct.ThrowIfCancellationRequested();
        checkpoint.PrepareSuccessor(member.Plan, stop);
        stopQuery = stopQuery with { Checkpoint = checkpoint.State, ExpectedRetirement = retirement };
        var owned = await host.ObserveAsync(stopQuery, ct).ConfigureAwait(false);
        if (owned is not { IngressClosed: true } || owned.ResidentRoute != incumbentRoute ||
            !Matches(stopQuery, owned, HostOccupancyKind.Owned, incumbent) ||
            !MatchesFrontier(owned, retirement)) return Refused();
        ct.ThrowIfCancellationRequested();
        checkpoint.MarkStopDispatching(stop);
        var stopIssue = new EnrolledSuccessorIssue(stopQuery, owned, retirement, checkpoint.State, installed.Attempt);
        var stopped = await host.StopAsync(stopIssue, ct).ConfigureAwait(false);
        if (stopped.Status != HostMutationStatus.Applied || stopped.Operation != stop || stopped.ContainerId != incumbent.InstanceId ||
            await host.ObserveStopAsync(stopIssue, ct).ConfigureAwait(false) is not { } stopEffect ||
            !stopEffect.Matches(stop, incumbent)) return Refused();
        checkpoint.RecordAppliedStop(stopped, stopEffect);

        var vacancyQuery = Query(checkpoint.State, member, incumbentRoute) with { ExpectedRetirement = retirement };
        var vacant = await host.ObserveAsync(vacancyQuery, ct).ConfigureAwait(false);
        if (vacant is not { IngressClosed: true, ResidentRoute: null } || !Matches(vacancyQuery, vacant, HostOccupancyKind.Vacant, null) ||
            !MatchesFrontier(vacant, retirement)) return Refused();
        ct.ThrowIfCancellationRequested();
        var launch = checkpoint.PrepareSuccessorLaunch(member.Plan);
        checkpoint.MarkLaunchDispatching(launch);
        var launchIssue = new EnrolledSuccessorIssue(vacancyQuery, vacant, retirement, checkpoint.State, installed.Attempt);
        var launched = await host.LaunchAsync(launchIssue, ct).ConfigureAwait(false);
        if (launched.Status != HostMutationStatus.Applied || launched.Operation != launch ||
            await host.ObserveLaunchAsync(launchIssue, ct).ConfigureAwait(false) is not { } effect ||
            effect.Operation != launch || effect.Plan != member.Plan || !effect.IsValid() ||
            effect.InstanceId != launched.ContainerId) return Refused();

        var appliedQuery = Query(checkpoint.State, member, incumbentRoute) with { ExpectedRetirement = retirement };
        var applied = await host.ObserveAsync(appliedQuery, ct).ConfigureAwait(false);
        if (applied is not { IngressClosed: true } || !Matches(appliedQuery, applied, HostOccupancyKind.Owned, effect) ||
            !MatchesFrontier(applied, retirement)) return Refused();
        checkpoint.RecordAppliedLaunch(launched, effect);
        var appliedState = checkpoint.State;
        return await stageAsync(new(installed, scope, appliedState.AppliedLaunch!, appliedState.LastStoppedResident!, retirement), ct)
            .ConfigureAwait(false);

    }

    private EnrolledSuccessorQuery Query(HostEffectCheckpointState state, CheckedEnrolledTarget member,
        VerifiedInstanceRoute? expectedIncumbentRoute = null) => new(Guid.NewGuid(), scope, state, member, expectedIncumbentRoute);

    private bool Approved(CheckedEnrolledTarget member) => catalog.Head == scope.Commission.Head &&
        catalog.Targets.Contains(member) && scope.Reservation.Enrollment is { IsValid: true } enrollment &&
        enrollment.Generation == catalog.Head.Generation && enrollment.Group == catalog.Head.Partition &&
        SupervisedLaunchCommission.SameDevices(catalog.Devices, scope.Commission.Devices) && scope.Approves(member);

    private bool CanSupersede(HostEffectCheckpointState state, CheckedEnrolledTarget member,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HostCheckpointLaunchEffect? incumbent)
    {
        incumbent = state.AppliedLaunch;
        if (state.Phase is not (HostCheckpointPhase.Resident or HostCheckpointPhase.RetainedBeforeDispatch or
            HostCheckpointPhase.RetainedAfterRejection) || incumbent is null || incumbent.Plan == member.Plan)
            return false;

        var plan = incumbent.Plan;
        return catalog.Targets.Any(candidate => candidate.Plan == plan && Approved(candidate));
    }

    private static bool Matches(EnrolledSuccessorQuery query, EnrolledSuccessorObservation? observed,
        HostOccupancyKind occupancy, HostCheckpointLaunchEffect? resident) => observed is not null &&
        observed.QueryId == query.Id && query.Scope.Matches(observed.Scope) && observed.CompleteCoverage &&
        observed.OwnerGeneration > 0 && observed.CoverageGeneration > 0 && observed.IngressGeneration > 0 &&
        observed.Occupancy == occupancy && observed.Resident == resident;

    private static bool MatchesFrontier(EnrolledSuccessorObservation observed, EnrolledSuccessorRetirement retirement) =>
        observed.Retirement == retirement && observed.OwnerGeneration == retirement.OwnerGeneration &&
        observed.IngressGeneration == retirement.IngressGeneration;

    private static HostStagedSwitchResult Refused() => new(
        LifecycleResult.Fail("host_successor_refused", "the enrolled successor could not prove its exact transition"), default);
}
