namespace Arboreal.InferenceRouter.Lifecycle;

internal enum HostOccupancyKind { Unknown, Vacant, Owned, Foreign }

internal sealed record HostResident(string Engine, string ContainerId);

internal readonly record struct HostOccupancy(Guid QueryId, HostOccupancyKind Kind, HostResident? Resident = null);

internal enum HostMutationStatus { UnknownAfterIssue, Applied, RejectedBeforeIssue }

internal readonly record struct HostMutationOutcome(HostMutation? Operation, HostMutationStatus Status, string? ContainerId = null);

internal sealed record HostServingProof(LifecycleTarget Target, long Epoch, string ContainerId);

internal sealed record HostSwitchPublication(
    Func<HostServingProof, bool> TryPublish, Func<Action, bool> TryAccept, Action Complete);

internal interface IHostDrain : IDisposable
{
    LifecycleResult Result { get; }

    // Withdraw serving authority after the stop intent is durable and before dispatching it.
    // Disposing the admission guard must never restore this authority without fresh evidence.
    void WithdrawIncumbent();

    // Publish provisionally under host admission, then pass the exact route store to tryAccept.
    // Return the decision from tryAccept. Its synchronous store is infallible and must perform
    // no I/O, take no locks and send no notifications.
    bool CommitVerifiedRoute(HostServingProof proof, Func<bool> publish, Func<Action, bool> tryAccept);
}

/// <summary>The host authority for cooperating launchers. Observations cover the physical host,
/// including foreign residents and any occupancy the provider cannot classify.</summary>
internal interface IHostLifecycleProvider
{
    // Bind the query ID to a new host observation. Return unknown if the observation cannot be made.
    ValueTask<HostOccupancy> ObserveAsync(Guid queryId, CancellationToken ct);
    // Validate the requested target without mutation. Rechecked after draining an incumbent.
    ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct);

    // The returned guard keeps admission closed until disposed. A failed or cancelled drain
    // must release admission, either through its guard or before throwing.
    ValueTask<IHostDrain> CloseAndDrainAsync(HostResident? incumbent, CancellationToken ct);

    // Digest the full immutable launch plan understood by this provider, without issuing I/O.
    string GetLaunchPlanDigest(LifecycleTarget target);

    // Dispatch must already be durable. The worker binds the request to the exact operation and
    // durably accepts it before I/O. RejectedBeforeIssue is authoritative only before mutation I/O.
    ValueTask<HostMutationOutcome> StopAsync(HostMutation operation, CancellationToken ct);
    ValueTask<HostMutationOutcome> LaunchAsync(HostMutation operation, LifecycleTarget target, CancellationToken ct);

    // Read-only. A vacancy snapshot or an elapsed timeout cannot settle an outstanding operation.
    ValueTask<HostMutationOutcome> ReconcileAsync(HostMutation operation, CancellationToken ct);
    // Verify the exact launched ID against its recorded plan, including after loss of the original claim.
    ValueTask<LifecycleResult> VerifyReadyAsync(HostMutation launch, string containerId, CancellationToken ct);
}

/// <summary>Serializes one host transaction through readiness and leaves uncertain effects recorded
/// for the next holder. Every cooperating launcher uses the same local reservation file.</summary>
internal sealed partial class HostLifecycleCoordinator(string reservationPath, IHostLifecycleProvider provider,
    HostClosedRouteStaging? stagingForTests = null, IHostInitialLaunchAuthority? initialLaunch = null)
{
    private readonly string _reservationPath = reservationPath;
    private readonly IHostInitialLaunchAuthority? _initialLaunch = initialLaunch;

    internal bool OwnsStaging(HostClosedRouteStaging staging) => ReferenceEquals(stagingForTests, staging);

    internal bool MatchesScope(HostReservationScope scope) => Path.GetFullPath(_reservationPath) == scope.Slot;

    internal bool MatchesRecipe(LifecycleTarget target, string digest) => provider.GetLaunchPlanDigest(target) == digest;

    public async ValueTask<LifecycleResult> SwitchAsync(
        LifecycleTarget target, CancellationToken ct, HostSwitchPublication? publication = null,
        HostStagedPublication? stagedPublicationForTests = null)
    {
        if ((stagingForTests is null) != (stagedPublicationForTests is null) ||
            (stagedPublicationForTests is not null && (publication is not null || stagedPublicationForTests.ClaimId == Guid.Empty)))
            return PublicationRefusal();

        using var staged = stagedPublicationForTests is null ? null : stagingForTests!.Begin(stagedPublicationForTests);
        try
        {
            var result = await SwitchUnderReservationAsync(target, publication, staged, ct).ConfigureAwait(false);
            // Drain and reservation cleanup can fail. Finish both with minting still closed, then
            // open only if the exact committed claim remains current in the fixture authority.
            if (result.Succeeded && staged is not null && !staged.Open(ct)) return PublicationRefusal();
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReservationRefusal();
        }
    }

    internal async ValueTask<HostStagedSwitchResult> SwitchStagedAsync(
        LifecycleTarget target, HostStagedDeadlinePublication publication, CancellationToken ct)
    {
        if (stagingForTests is null || publication.ClaimId == Guid.Empty ||
            publication.ExpectedPlanDigest is { } expected && !MatchesRecipe(target, expected))
            return new(PublicationRefusal(), default);

        using var staged = stagingForTests.Begin(publication);
        try
        {
            var result = await SwitchUnderReservationAsync(target, null, staged, ct).ConfigureAwait(false);
            if (result.Succeeded)
            {
                if (!staged.Open(ct)) return new(RouteNotOpened(), staged.Decision);
                publication.Opened();
            }
            return new(result, staged.Decision, staged.Opened);
        }
        catch (Exception error) when (staged.Decision.Verdict != HostStagedVerdict.Refused)
        {
            // A selected runtime outcome survives post-guard notification and cleanup faults.
            return new(LifecycleResult.Fail("host_route_delivery_failed",
                "the staged outcome was selected but completion, cleanup, or wake failed"),
                staged.Decision, staged.Opened, error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(ReservationRefusal(), default);
        }
    }

    private async ValueTask<LifecycleResult> SwitchUnderReservationAsync(LifecycleTarget target, HostSwitchPublication? publication,
        HostClosedRouteStaging.Attempt? staged, CancellationToken ct)
    {
        // Both switch wrappers enter here. Only a positively commissioned legacy slot can
        // settle, reconcile or grant on the switch path, regardless of its local phase.
        var cut = staged?.CommissionedCut;
        if (cut is not null && !MatchesScope(cut.Scope)) return ReservationRefusal();
        using var reservation = cut is null ? HostReservation.Open(_reservationPath) : HostReservation.OpenEnrolled(cut.Scope);
        if (reservation.State.Pending is { } pending)
        {
            if (staged is not null)
            {
                // Reattaching an enrolled launch requires the protected recovery authority.
                if (cut is not null) return PendingOperation();
                if (reservation.State.Phase != HostMutationPhase.LaunchApplied ||
                    reservation.State.LaunchedContainerId is not { } stagedContainerId)
                    return PendingOperation();

                // The saved launch already superseded the old route, even if occupancy cannot prove it now.
                staged.Close();
                if (!MatchesRequest(pending, target, staged.ExpectedPlanDigest)) return PendingOperation();
                return await RecoverLaunchAsync(reservation, pending, stagedContainerId, target, null, staged, ct)
                    .ConfigureAwait(false);
            }

            if (reservation.State.Phase == HostMutationPhase.Prepared)
                reservation.Settle(pending);
            else if (reservation.State.Phase == HostMutationPhase.Dispatching)
            {
                var outcome = await ReadOutcomeAsync(() => provider.ReconcileAsync(pending, ct)).ConfigureAwait(false);
                if (!TryRecordOutcome(reservation, pending, outcome)) return PendingOperation();
            }

            if (reservation.State.LaunchedContainerId is { } containerId)
            {
                var matchesRequest = MatchesRequest(pending, target);
                var recovered = await RecoverLaunchAsync(reservation, pending, containerId, target,
                    matchesRequest ? publication : null, null, ct).ConfigureAwait(false);
                if (!recovered.Succeeded || matchesRequest) return recovered;
            }
        }

        ct.ThrowIfCancellationRequested();
        var epoch = reservation.Grant(target.EngineName);
        return await SwitchReservedAsync(reservation, epoch, target, publication, staged, ct).ConfigureAwait(false);
    }

    private async ValueTask<LifecycleResult> SwitchReservedAsync(
        HostReservation reservation, long epoch, LifecycleTarget target, HostSwitchPublication? publication,
        HostClosedRouteStaging.Attempt? staged, CancellationToken ct)
    {
        var occupancy = await ObserveFreshAsync(ct).ConfigureAwait(false);
        if (occupancy.Kind == HostOccupancyKind.Owned)
        {
            if (occupancy.Resident is not { } incumbent || string.IsNullOrWhiteSpace(incumbent.ContainerId) ||
                string.IsNullOrWhiteSpace(incumbent.Engine)) return OccupancyRefusal();
        }
        else if (!IsVacant(occupancy)) return OccupancyRefusal();

        var preflight = await provider.PreflightAsync(target, ct).ConfigureAwait(false);
        if (!preflight.Succeeded) return preflight;
        var planDigest = staged?.ExpectedPlanDigest ?? provider.GetLaunchPlanDigest(target);
        if (!HostMutation.IsPlanDigest(planDigest))
            return LifecycleResult.Fail("host_plan_unbound", "the host authority did not bind the full launch plan");

        ct.ThrowIfCancellationRequested();
        var cut = staged?.CommissionedCut;
        using var deadline = cut?.HasIncumbent == true ? cut.StartBudget(ct) : null;
        if (cut is not null)
        {
            if (!cut.Matches(occupancy.Resident)) return OccupancyRefusal();
            if (!await cut.SealAndDrainAsync(deadline).ConfigureAwait(false)) return CommissionedHostCut.Unproven();
        }
        staged?.Close();
        // The physical port may still return a guard after expiry. This helper keeps
        // ownership of that guard until the caller either accepts it or abandons it.
        var providerDrain = provider.CloseAndDrainAsync(occupancy.Resident, ct);
        using var drain = deadline is null ? await providerDrain.ConfigureAwait(false)
            : await AwaitGuardAsync(providerDrain.AsTask(), deadline, ct).ConfigureAwait(false);
        if (drain is null) return UnprovenDrain();
        if (!drain.Result.Succeeded) return drain.Result;

        if (occupancy.Resident is { } owned)
        {
            var revalidated = deadline is null ? await provider.PreflightAsync(target, ct).ConfigureAwait(false)
                : await deadline.ObserveAsync(token => provider.PreflightAsync(target, token).AsTask()).ConfigureAwait(false) switch
                {
                    { Outcome: ReadinessStepOutcome.Completed, Observation: { } result } => result,
                    _ => UnprovenDrain(),
                };
            if (!revalidated.Succeeded) return revalidated;

            var current = deadline is null ? await ObserveFreshAsync(ct).ConfigureAwait(false)
                : (await deadline.ObserveAsync(token => ObserveFreshAsync(token).AsTask()).ConfigureAwait(false)).Observation;
            if (deadline is { OutcomeForSettlement: not ReadinessStepOutcome.Completed }) return UnprovenDrain();
            if (current.Kind != HostOccupancyKind.Owned || current.Resident != owned) return OccupancyRefusal();

            ct.ThrowIfCancellationRequested();
            using var permit = cut?.TryReserveStop(owned);
            if (cut is not null && permit is null) return OccupancyRefusal();
            var stop = reservation.Begin(epoch, HostMutationKind.Stop, owned.Engine, owned.ContainerId);
            reservation.MarkDispatching(stop);
            drain.WithdrawIncumbent();
            var stopped = await ReadOutcomeAsync(() => permit is null ?
                provider.StopAsync(stop, ct) : permit.StopAsync(stop, ct)).ConfigureAwait(false);
            if (!TryRecordOutcome(reservation, stop, stopped)) return PendingOperation();
            if (stopped.Status != HostMutationStatus.Applied)
                return LifecycleResult.Fail("host_stop_refused", "the host refused to stop the incumbent; check its state before retrying");
        }

        // A successful stop still needs a fresh vacancy proof. Preflight may also have outlived
        // the initial vacancy observation, so always check again before authorizing launch.
        var released = await ObserveFreshAsync(ct).ConfigureAwait(false);
        if (!IsVacant(released) || cut is not null && !cut.IsKnownVacant()) return OccupancyRefusal();

        ct.ThrowIfCancellationRequested();
        var launch = reservation.Begin(epoch, HostMutationKind.Launch, target.EngineName, target.Model,
            target.Profile?.Name ?? target.Definition.Profile, planDigest);
        reservation.MarkDispatching(launch);
        var launched = await ReadOutcomeAsync(() => provider.LaunchAsync(launch, target, ct)).ConfigureAwait(false);
        if (!TryRecordOutcome(reservation, launch, launched)) return PendingOperation();
        if (launched.Status != HostMutationStatus.Applied)
            return LifecycleResult.Fail("host_launch_refused", "the host refused the launch; check the target launch configuration");

        return await CompleteLaunchAsync(reservation, launch, launched.ContainerId!, target, drain, publication, staged, ct)
            .ConfigureAwait(false);

        LifecycleResult UnprovenDrain()
        {
            cut?.MarkUnproven();
            return CommissionedHostCut.Unproven();
        }
    }

    private static async Task<IHostDrain?> AwaitGuardAsync(Task<IHostDrain> guard, ReadinessDeadline deadline, CancellationToken ct)
    {
        try { return await guard.WaitAsync(deadline.Remaining, deadline.Clock, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _ = DisposeLateGuardAsync(guard);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ = DisposeLateGuardAsync(guard);
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected",
        Justification = "The timed-out acquisition transfers its late result here. There is no other owner to dispose it.")]
    private static async Task DisposeLateGuardAsync(Task<IHostDrain> pending)
    {
        await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (pending.IsCompletedSuccessfully) pending.Result.Dispose();
    }

    private bool MatchesRequest(HostMutation launch, LifecycleTarget target, string? expectedDigest = null) =>
        launch.Engine == target.EngineName && launch.Target == target.Model &&
        launch.Profile == (target.Profile?.Name ?? target.Definition.Profile) &&
        launch.PlanDigest == (expectedDigest ?? provider.GetLaunchPlanDigest(target));

    private async ValueTask<LifecycleResult> RecoverLaunchAsync(HostReservation reservation, HostMutation launch,
        string containerId, LifecycleTarget target, HostSwitchPublication? publication, HostClosedRouteStaging.Attempt? staged,
        CancellationToken ct)
    {
        var instance = new HostResident(launch.Engine, containerId);
        if (!await IsCurrentInstanceAsync(provider, instance, ct).ConfigureAwait(false)) return OccupancyRefusal();
        using var drain = await provider.CloseAndDrainAsync(instance, ct).ConfigureAwait(false);
        if (!drain.Result.Succeeded) return drain.Result;
        if (staged?.UsesDeadline != true && !await IsCurrentInstanceAsync(provider, instance, ct).ConfigureAwait(false))
            return OccupancyRefusal();
        return await CompleteLaunchAsync(reservation, launch, containerId, target, drain, publication, staged, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> IsCurrentInstanceAsync(IHostLifecycleProvider observer, HostResident expected, CancellationToken ct)
    {
        var current = await ObserveFreshAsync(observer, ct).ConfigureAwait(false);
        return current.Kind == HostOccupancyKind.Owned && current.Resident == expected;
    }

    private async ValueTask<LifecycleResult> CompleteLaunchAsync(HostReservation reservation, HostMutation launch,
        string containerId, LifecycleTarget target, IHostDrain drain, HostSwitchPublication? publication,
        HostClosedRouteStaging.Attempt? staged, CancellationToken ct)
    {
        if (staged?.UsesDeadline == true)
        {
            var observer = provider;
            var expected = new HostResident(launch.Engine, containerId);
            var published = await staged.VerifyAndPublishAsync(_reservationPath, reservation.State, launch, containerId,
                token => IsCurrentInstanceAsync(observer, expected, token), ct).ConfigureAwait(false);
            if (published.Succeeded && staged.CommissionedCut is not null) reservation.Settle(launch);
            return published;
        }

        var ready = await provider.VerifyReadyAsync(launch, containerId, ct).ConfigureAwait(false);
        if (!ready.Succeeded) return ready;

        ct.ThrowIfCancellationRequested();
        if (staged is not null)
            return staged.StageAndComplete(_reservationPath, reservation.State, launch, containerId, ct) ? ready : PublicationRefusal();

        reservation.Settle(launch);
        if (publication is null) return ready;
        var proof = new HostServingProof(target, launch.Epoch, containerId);
        if (!drain.CommitVerifiedRoute(proof, () => publication.TryPublish(proof), publication.TryAccept))
            return PublicationRefusal();

        publication.Complete();
        return ready;
    }

    private static bool TryRecordOutcome(HostReservation reservation, HostMutation operation, HostMutationOutcome outcome)
    {
        if (outcome.Operation != operation) return false;
        if (outcome.Status == HostMutationStatus.RejectedBeforeIssue && outcome.ContainerId is null)
        {
            reservation.Settle(operation);
            return true;
        }
        if (outcome.Status != HostMutationStatus.Applied || string.IsNullOrWhiteSpace(outcome.ContainerId)) return false;
        if (operation.Kind == HostMutationKind.Launch)
            reservation.RecordAppliedLaunch(operation, outcome.ContainerId);
        else if (outcome.ContainerId == operation.Target)
            reservation.Settle(operation);
        else
            return false;

        return true;
    }

    private static async ValueTask<HostMutationOutcome> ReadOutcomeAsync(Func<ValueTask<HostMutationOutcome>> read)
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failure to receive a receipt never proves that the worker did not issue the operation.
            return default;
        }
    }

    private ValueTask<HostOccupancy> ObserveFreshAsync(CancellationToken ct) => ObserveFreshAsync(provider, ct);

    private static async ValueTask<HostOccupancy> ObserveFreshAsync(IHostLifecycleProvider observer, CancellationToken ct)
    {
        var queryId = Guid.NewGuid();
        var occupancy = await observer.ObserveAsync(queryId, ct).ConfigureAwait(false);
        return occupancy.QueryId == queryId ? occupancy : default;
    }

    private static bool IsVacant(HostOccupancy occupancy) =>
        occupancy.Kind == HostOccupancyKind.Vacant && occupancy.Resident is null;

    private static LifecycleResult OccupancyRefusal() => LifecycleResult.Fail("host_occupancy_unproven",
        "the host authority could not prove exclusive occupancy or vacancy; reconcile the resident before switching");

    private static LifecycleResult PendingOperation() => LifecycleResult.Fail("host_operation_unsettled",
        "the previous host mutation is still unresolved; wait for the host authority to reconcile it before switching");

    private static LifecycleResult PublicationRefusal() => LifecycleResult.Fail("host_publication_refused",
        "the host could not publish and admit against the verified serving instance");

    private static LifecycleResult RouteNotOpened() => LifecycleResult.Fail("host_route_not_opened",
        "the committed route stayed closed; recover the applied launch with a fresh authorized generation");

    private static LifecycleResult ReservationRefusal() => LifecycleResult.Fail("host_reservation_unavailable",
        "the router could not reserve this host or persist its operation; check the host authority and its reservation file");
}
