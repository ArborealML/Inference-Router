namespace Arboreal.InferenceRouter.Lifecycle;

internal enum HostOccupancyKind { Unknown, Vacant, Owned, Foreign }

internal sealed record HostResident(string Engine, string ContainerId);

internal readonly record struct HostOccupancy(Guid QueryId, HostOccupancyKind Kind, HostResident? Resident = null);

internal enum HostMutationStatus { UnknownAfterIssue, Applied, RejectedBeforeIssue }

internal readonly record struct HostMutationOutcome(HostMutation? Operation, HostMutationStatus Status, string? ContainerId = null);

internal sealed record HostServingProof(LifecycleTarget Target, long Epoch, string ContainerId);

internal sealed record HostSwitchPublication(Func<HostServingProof, bool> TryPublish, Action Complete);

internal interface IHostDrain : IDisposable
{
    LifecycleResult Result { get; }

    // Withdraw serving authority after the stop intent is durable and before dispatching it.
    // Disposing the admission guard must never restore this authority without fresh evidence.
    void WithdrawIncumbent();

    // Commit admission for this exact proof and publish it while the guard is held. A declined
    // publication grants no admission. No other launch can intervene in the handoff.
    bool CommitVerifiedRoute(HostServingProof proof, Func<bool> publish);
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
internal sealed class HostLifecycleCoordinator(string reservationPath, IHostLifecycleProvider provider)
{
    public async ValueTask<LifecycleResult> SwitchAsync(
        LifecycleTarget target, CancellationToken ct, HostSwitchPublication? publication = null)
    {
        try
        {
            using var reservation = HostReservation.Open(reservationPath);
            if (reservation.State.Pending is { } pending)
            {
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
                        matchesRequest ? publication : null, ct).ConfigureAwait(false);
                    if (!recovered.Succeeded || matchesRequest) return recovered;
                }
            }

            ct.ThrowIfCancellationRequested();
            var epoch = reservation.Grant(target.EngineName);
            return await SwitchReservedAsync(reservation, epoch, target, publication, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LifecycleResult.Fail("host_reservation_unavailable",
                "the router could not reserve this host or persist its operation; check the host authority and its reservation file");
        }
    }

    private async ValueTask<LifecycleResult> SwitchReservedAsync(
        HostReservation reservation, long epoch, LifecycleTarget target, HostSwitchPublication? publication,
        CancellationToken ct)
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
        var planDigest = provider.GetLaunchPlanDigest(target);
        if (!HostMutation.IsPlanDigest(planDigest))
            return LifecycleResult.Fail("host_plan_unbound", "the host authority did not bind the full launch plan");

        ct.ThrowIfCancellationRequested();
        using var drain = await provider.CloseAndDrainAsync(occupancy.Resident, ct).ConfigureAwait(false);
        if (!drain.Result.Succeeded) return drain.Result;

        if (occupancy.Resident is { } owned)
        {
            var revalidated = await provider.PreflightAsync(target, ct).ConfigureAwait(false);
            if (!revalidated.Succeeded) return revalidated;

            var current = await ObserveFreshAsync(ct).ConfigureAwait(false);
            if (current.Kind != HostOccupancyKind.Owned || current.Resident != owned) return OccupancyRefusal();

            ct.ThrowIfCancellationRequested();
            var stop = reservation.Begin(epoch, HostMutationKind.Stop, owned.Engine, owned.ContainerId);
            reservation.MarkDispatching(stop);
            drain.WithdrawIncumbent();
            var stopped = await ReadOutcomeAsync(() => provider.StopAsync(stop, ct)).ConfigureAwait(false);
            if (!TryRecordOutcome(reservation, stop, stopped)) return PendingOperation();
            if (stopped.Status != HostMutationStatus.Applied)
                return LifecycleResult.Fail("host_stop_refused", "the host refused to stop the incumbent; check its state before retrying");
        }

        // A successful stop still needs a fresh vacancy proof. Preflight may also have outlived
        // the initial vacancy observation, so always check again before authorizing launch.
        var released = await ObserveFreshAsync(ct).ConfigureAwait(false);
        if (!IsVacant(released)) return OccupancyRefusal();

        ct.ThrowIfCancellationRequested();
        var launch = reservation.Begin(epoch, HostMutationKind.Launch, target.EngineName, target.Model,
            target.Profile?.Name ?? target.Definition.Profile, planDigest);
        reservation.MarkDispatching(launch);
        var launched = await ReadOutcomeAsync(() => provider.LaunchAsync(launch, target, ct)).ConfigureAwait(false);
        if (!TryRecordOutcome(reservation, launch, launched)) return PendingOperation();
        if (launched.Status != HostMutationStatus.Applied)
            return LifecycleResult.Fail("host_launch_refused", "the host refused the launch; check the target launch configuration");

        return await CompleteLaunchAsync(reservation, launch, launched.ContainerId!, target, drain, publication, ct)
            .ConfigureAwait(false);
    }

    private bool MatchesRequest(HostMutation launch, LifecycleTarget target) =>
        launch.Engine == target.EngineName && launch.Target == target.Model &&
        launch.Profile == (target.Profile?.Name ?? target.Definition.Profile) &&
        launch.PlanDigest == provider.GetLaunchPlanDigest(target);

    private async ValueTask<LifecycleResult> RecoverLaunchAsync(HostReservation reservation, HostMutation launch,
        string containerId, LifecycleTarget target, HostSwitchPublication? publication, CancellationToken ct)
    {
        var instance = new HostResident(launch.Engine, containerId);
        if (!await IsCurrentInstanceAsync(instance, ct).ConfigureAwait(false)) return OccupancyRefusal();
        using var drain = await provider.CloseAndDrainAsync(instance, ct).ConfigureAwait(false);
        if (!drain.Result.Succeeded) return drain.Result;
        if (!await IsCurrentInstanceAsync(instance, ct).ConfigureAwait(false)) return OccupancyRefusal();
        return await CompleteLaunchAsync(reservation, launch, containerId, target, drain, publication, ct)
            .ConfigureAwait(false);
    }

    private async ValueTask<bool> IsCurrentInstanceAsync(HostResident expected, CancellationToken ct)
    {
        var current = await ObserveFreshAsync(ct).ConfigureAwait(false);
        return current.Kind == HostOccupancyKind.Owned && current.Resident == expected;
    }

    private async ValueTask<LifecycleResult> CompleteLaunchAsync(HostReservation reservation, HostMutation launch,
        string containerId, LifecycleTarget target, IHostDrain drain, HostSwitchPublication? publication, CancellationToken ct)
    {
        var ready = await provider.VerifyReadyAsync(launch, containerId, ct).ConfigureAwait(false);
        if (!ready.Succeeded) return ready;

        ct.ThrowIfCancellationRequested();
        reservation.Settle(launch);
        if (publication is null) return ready;
        var proof = new HostServingProof(target, launch.Epoch, containerId);
        if (!drain.CommitVerifiedRoute(proof, () => publication.TryPublish(proof)))
            return LifecycleResult.Fail("host_publication_refused",
                "the host could not publish and admit against the verified serving instance");

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

    private async ValueTask<HostOccupancy> ObserveFreshAsync(CancellationToken ct)
    {
        var queryId = Guid.NewGuid();
        var occupancy = await provider.ObserveAsync(queryId, ct).ConfigureAwait(false);
        return occupancy.QueryId == queryId ? occupancy : default;
    }

    private static bool IsVacant(HostOccupancy occupancy) =>
        occupancy.Kind == HostOccupancyKind.Vacant && occupancy.Resident is null;

    private static LifecycleResult OccupancyRefusal() => LifecycleResult.Fail("host_occupancy_unproven",
        "the host authority could not prove exclusive occupancy or vacancy; reconcile the resident before switching");

    private static LifecycleResult PendingOperation() => LifecycleResult.Fail("host_operation_unsettled",
        "the previous host mutation is still unresolved; wait for the host authority to reconcile it before switching");
}
