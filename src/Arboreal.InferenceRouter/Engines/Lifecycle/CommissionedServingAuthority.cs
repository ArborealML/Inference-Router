namespace Arboreal.InferenceRouter.Lifecycle;

// Implemented by the protected host authority. The synchronous reads describe its current
// held partition and process publications. They neither perform I/O nor grant authority.
internal interface ICommissionedServingAuthority : IVerifiedDispatchIssuer
{
    HostReservationScope Scope { get; }
    string Daemon { get; }
    string Partition { get; }
    bool Approves(VerifiedDispatchTarget target);
    bool IsDisjointFromLocalController(EngineDefinition engine);
    long ResourceEpoch { get; }
    bool IsKnownVacant(long resourceEpoch);
    CommissionedServingStamp? ReadServing(VerifiedInstanceRoute route);

    // Seal durable issuance and ingress for the exact generation and return only after all
    // issued uses have authoritative terminal evidence. Cancellation cannot reopen the seal.
    ValueTask<CommissionedServingRetirement?> SealAndDrainAsync(CommissionedServingStamp stamp, CancellationToken ct);

    // State-only reservation of a stop at the exact sealed cutoff. The permit must not
    // authorize a different process, epoch, route, or mutation after the authority changes.
    ICommissionedStopPermit? TryReserveStop(CommissionedServingRetirement retirement);
}

internal interface ICommissionedStopPermit : IDisposable
{
    ValueTask<HostMutationOutcome> StopAsync(HostMutation operation, CancellationToken ct);
}

internal sealed record CommissionedServingStamp(HostReservationScope Scope, VerifiedInstanceRoute Route,
    long ResourceEpoch, long IngressGeneration, string HandlerIncarnation, string WorkerIncarnation)
{
    internal bool Matches(HostReservationScope scope, VerifiedInstanceRoute route, long resourceEpoch) =>
        Scope == scope && Route == route && ResourceEpoch == resourceEpoch && resourceEpoch > 0 &&
        IngressGeneration > 0 && !string.IsNullOrWhiteSpace(HandlerIncarnation) && !string.IsNullOrWhiteSpace(WorkerIncarnation);
}

internal sealed record CommissionedServingRetirement(CommissionedServingStamp Stamp, Guid Cutoff);

// Local issuance and first write share staging's short gate. The external issuer still
// owns certified checkout, durable issuance, and conclusive retirement of its grants.
internal sealed class CommissionedRouteIssuer(ICommissionedServingAuthority authority, HostClosedRouteStaging staging)
    : IVerifiedDispatchIssuer
{
    private readonly HostReservationScope _scope = authority.Scope;
    private CommissionedServingStamp? _stamp;
    private bool _sealed;

    internal ICommissionedServingAuthority Authority => authority;
    internal HostReservationScope Scope => _scope;
    internal bool IsCurrentScope => authority.Scope == _scope;
    internal Lock Gate => staging.IssuerGate;

    internal CommissionedServingStamp? Attach(VerifiedInstanceRoute route)
    {
        lock (Gate)
        {
            var stamp = authority.ReadServing(route);
            if (!IsCurrentScope || !staging.CanMint(route) || stamp?.Matches(_scope, route, authority.ResourceEpoch) != true) return null;
            _stamp = stamp;
            _sealed = false;
            return stamp;
        }
    }

    internal CommissionedServingStamp? Capture(VerifiedInstanceRoute route) =>
        IsCurrentScope && _stamp is { } stamp && stamp.Route == route && stamp.ResourceEpoch == authority.ResourceEpoch &&
            authority.ReadServing(route) == stamp ? stamp : null;

    internal bool Seal(CommissionedServingStamp stamp)
    {
        if (_stamp != stamp) return false;
        return _sealed || (_sealed = staging.SealCommissionedRoute(stamp.Route));
    }

    internal bool IsSealed(CommissionedServingStamp stamp) =>
        _sealed && Capture(stamp.Route) == stamp && !staging.CanMint(stamp.Route);

    private bool CanIssue(VerifiedInstanceRoute route) => staging.CanMint(route) && Capture(route) is not null;

    public ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken ct) =>
        ValueTask.FromException<VerifiedInstanceGrant?>(new InvalidOperationException("Certified checkout is required."));

    public ValueTask<VerifiedCheckout> CheckoutVerifiedAsync(VerifiedInstanceInvocation invocation, CancellationToken ct)
    {
        lock (Gate)
            if (!CanIssue(invocation.Route)) return ValueTask.FromResult(new VerifiedCheckout(VerifiedCheckoutDisposition.NoTransfer));
        return authority.CheckoutVerifiedAsync(invocation, ct);
    }

    public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
    {
        lock (Gate) return CanIssue(grant.Invocation.Route) ? authority.TryAuthorizeFirstWrite(grant) : null;
    }
}

// Only the transition that captured the incumbent can produce a spendable stop cut.
internal sealed class CommissionedHostCut(CommissionedRouteIssuer issuer, long epoch,
    CommissionedServingStamp? stamp, Func<CommissionedServingRetirement, ICommissionedStopPermit?> reserveStop, Func<bool> seal,
    Func<CancellationToken, Task<bool>> awaitZero, TimeProvider clock, TimeSpan budget)
{
    private CommissionedServingRetirement? _retirement;
    private bool _stopReserved;
    private long _startedAt;

    internal HostReservationScope Scope => issuer.Scope;
    internal bool HasIncumbent => stamp is not null;

    internal ReadinessDeadline StartBudget(CancellationToken ct)
    {
        _startedAt = clock.GetTimestamp();
        return ReadinessDeadline.Start(clock, budget, ct);
    }

    internal static LifecycleResult Unproven() => LifecycleResult.Fail("host_drain_unproven",
        "the exact incumbent did not retire before the drain deadline");
    internal bool DrainUnproven { get; private set; }

    internal bool Matches(HostResident? resident) => issuer.IsCurrentScope && (stamp is null
        ? resident is null && IsKnownVacant()
        : resident is not null && stamp.Route.Engine == resident.Engine && stamp.Route.ContainerId == resident.ContainerId);

    internal async ValueTask<bool> SealAndDrainAsync(ReadinessDeadline? deadline)
    {
        if (stamp is null) return Matches(null);
        DrainUnproven = true;
        if (deadline is null || !seal()) return false;
        var physical = await deadline.ObserveAsync(token => issuer.Authority.SealAndDrainAsync(stamp, token).AsTask())
            .ConfigureAwait(false);
        if (physical is not { Outcome: ReadinessStepOutcome.Completed, Observation: { } proof } ||
            proof.Stamp != stamp || proof.Cutoff == Guid.Empty) return false;
        var local = await deadline.ObserveAsync(awaitZero).ConfigureAwait(false);
        if (local is not { Outcome: ReadinessStepOutcome.Completed, Observation: true }) return false;
        _retirement = proof;
        DrainUnproven = false;
        return true;
    }

    internal void MarkUnproven() => DrainUnproven = true;

    internal ICommissionedStopPermit? TryReserveStop(HostResident resident)
    {
        if (_stopReserved || !Matches(resident) || _retirement is not { } proof ||
            clock.GetElapsedTime(_startedAt) >= budget || issuer.Authority.ResourceEpoch != epoch)
        {
            DrainUnproven = true;
            return null;
        }
        _stopReserved = true;
        var permit = reserveStop(proof);
        DrainUnproven = permit is null;
        return permit;
    }

    internal bool IsKnownVacant() => issuer.IsCurrentScope && issuer.Authority.ResourceEpoch == epoch &&
        issuer.Authority.IsKnownVacant(epoch);
}
