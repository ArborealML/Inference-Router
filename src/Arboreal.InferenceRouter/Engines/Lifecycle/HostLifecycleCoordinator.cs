namespace Arboreal.InferenceRouter.Lifecycle;

internal enum HostOccupancyKind { Unknown, Vacant, Owned, Foreign }

internal sealed record HostResident(string Engine, string ContainerId);

internal readonly record struct HostOccupancy(Guid QueryId, HostOccupancyKind Kind, HostResident? Resident = null);

internal enum HostMutationStatus { Unknown, Applied, Rejected }

internal readonly record struct HostMutationOutcome(Guid OperationId, HostMutationStatus Status, string? ContainerId = null);

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

    // The mutation methods bind to the recorded epoch, operation ID and target. Applied and
    // Rejected both prove that no delayed effect of this operation remains outstanding.
    ValueTask<HostMutationOutcome> StopAsync(HostMutation operation, CancellationToken ct);
    ValueTask<HostMutationOutcome> LaunchAsync(HostMutation operation, LifecycleTarget target, CancellationToken ct);

    // Read-only. A vacancy snapshot or an elapsed timeout cannot settle an outstanding operation.
    ValueTask<HostMutationOutcome> ReconcileAsync(HostMutation operation, CancellationToken ct);
    // Validate the exact launched ID and publish its readiness while the reservation is still held.
    ValueTask<LifecycleResult> VerifyReadyAsync(LifecycleTarget target, string containerId, CancellationToken ct);
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
                var outcome = await provider.ReconcileAsync(pending, ct).ConfigureAwait(false);
                if (!TrySettle(reservation, pending, outcome)) return PendingOperation();
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
            drain.WithdrawIncumbent();
            var stopped = await provider.StopAsync(stop, ct).ConfigureAwait(false);
            if (!TrySettle(reservation, stop, stopped)) return PendingOperation();
            if (stopped.Status != HostMutationStatus.Applied)
                return LifecycleResult.Fail("host_stop_refused", "the host refused to stop the incumbent; check its state before retrying");
        }

        // A successful stop still needs a fresh vacancy proof. Preflight may also have outlived
        // the initial vacancy observation, so always check again before authorizing launch.
        var released = await ObserveFreshAsync(ct).ConfigureAwait(false);
        if (!IsVacant(released)) return OccupancyRefusal();

        ct.ThrowIfCancellationRequested();
        var launch = reservation.Begin(epoch, HostMutationKind.Launch, target.EngineName, target.Model,
            target.Profile?.Name ?? target.Definition.Profile);
        var launched = await provider.LaunchAsync(launch, target, ct).ConfigureAwait(false);
        if (!TrySettle(reservation, launch, launched)) return PendingOperation();
        if (launched.Status != HostMutationStatus.Applied)
            return LifecycleResult.Fail("host_launch_refused", "the host refused the launch; check the target launch configuration");

        var containerId = launched.ContainerId!;
        var ready = await provider.VerifyReadyAsync(target, containerId, ct).ConfigureAwait(false);
        if (!ready.Succeeded || publication is null) return ready;

        ct.ThrowIfCancellationRequested();
        var proof = new HostServingProof(target, epoch, containerId);
        if (!drain.CommitVerifiedRoute(proof, () => publication.TryPublish(proof)))
            return LifecycleResult.Fail("host_publication_refused",
                "the host could not publish and admit against the verified serving instance");

        publication.Complete();
        return ready;
    }

    private static bool TrySettle(HostReservation reservation, HostMutation operation, HostMutationOutcome outcome)
    {
        if (outcome.OperationId != operation.Id ||
            outcome.Status is not HostMutationStatus.Applied and not HostMutationStatus.Rejected ||
            outcome.Status == HostMutationStatus.Applied && operation.Kind == HostMutationKind.Launch &&
            string.IsNullOrWhiteSpace(outcome.ContainerId)) return false;

        reservation.Settle(operation);
        return true;
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
