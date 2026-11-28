using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.Lifecycle;

// The supervisor pins this generation independently of the queued caller. The guarantees
// must be enforced in the supervised helper domain for the entire lifetime of the handle.
internal sealed record CommissionedServingGeneration(ServingBackendIdentity Identity, ServingHostNamespace Namespace,
    SafeFileHandle NamespaceHandle, ServingHostPinnedProcess Handler, ServingHostPinnedProcess Worker,
    ServingHostAddress Listener, ServingBackendProfile Profile, ServingBrokerProbePolicy ProbePolicy)
{
    internal bool Matches(ServingBrokerBinding binding, CheckedEnrolledTarget target) =>
        binding.IsValid() && Identity == ServingBrokerIssuer.ServingIdentity(binding) &&
        binding.Applied.State.AppliedLaunch?.Plan == target.Plan && target.Target.Matches(binding.Route) &&
        target.Endpoint.Scheme == "http" && Profile.CanonicalModel == target.Key.Model && Profile.Profile == target.Key.Profile &&
        Profile.Context > 0 && Profile.Slots == binding.TotalSlots && ProbePolicy.Digest == binding.PolicyDigest &&
        ProbePolicy.Models.IsValid() && ProbePolicy.State.IsValid();
}

// A single issuer reference follows the commissioned startup, even when distinct approved
// plans share an engine or listener address. A grant retains its original connection for cleanup.
internal sealed class CommissionedServingIssuer(EnrolledSuccessorScope scope, ServingBrokerLedger ledger, Lock issuerGate,
    Func<VerifiedInstanceRoute, bool> canMint, Func<VerifiedInstanceInvocation, bool> firstWriteAllowed,
    TimeSpan cleanupTimeout) : IVerifiedDispatchIssuer
{
    private PreparedGeneration? _current;
    private bool _sealed;

    internal Lock IssuerGate => issuerGate;

    internal PreparedGeneration? Prepare(ServingBrokerBinding binding, CheckedEnrolledTarget target,
        CommissionedServingGeneration generation, long tierWindow, Func<ServingHostExclusion?> readPublishedExclusion,
        Func<ServingHostAcceptedSocket, CancellationToken, ValueTask<ServingHostTerminalProof?>> readTerminal)
    {
        if (!OperatingSystem.IsLinux() || binding.Scope != scope.Reservation || binding.CatalogHead != scope.Commission.Head ||
            !scope.Approves(target) || !generation.Matches(binding, target) ||
            tierWindow <= 0 || generation.Profile.Context < tierWindow || cleanupTimeout <= TimeSpan.Zero ||
            readPublishedExclusion() is not { } exclusion || !exclusion.IsValid() ||
            exclusion.Binding != generation.Identity || exclusion.Namespace != generation.Namespace ||
            exclusion.Handler != generation.Handler.Identity || exclusion.Worker != generation.Worker.Identity ||
            exclusion.Endpoint != generation.Listener)
            return null;

        var evidence = new LinuxServingHostSocketEvidence(generation.Handler, generation.Worker, exclusion,
            readPublishedExclusion, readTerminal);
        var endpoint = new ServingBackendEndpoint(generation.Listener, generation.NamespaceHandle, generation.Namespace,
            generation.Handler.Identity, generation.Worker.Identity, generation.Profile, evidence);
        var resident = new VerifiedDispatchResident(target.Target, binding.Route, tierWindow, generation.Profile.Context,
            binding.TotalSlots, target.Endpoint, generation.ProbePolicy.Credential, this);
        var issuer = new ServingBrokerIssuer(ledger, binding, endpoint, target.Endpoint, issuerGate,
            route => !_sealed && ReferenceEquals(_current?.Resident, resident) && canMint(route),
            firstWriteAllowed, cleanupTimeout);
        return new PreparedGeneration(binding, resident, endpoint, generation.ProbePolicy, issuer);
    }

    internal CommissionedServingStartupPort? PrepareStartup(PreparedGeneration prepared, ReadinessDeadline budget,
        Func<ServingBrokerUseKind, ReadOnlyMemory<byte>, bool> validateReply) =>
        ReferenceEquals(prepared.Resident.Issuer, this) && prepared.Binding.Applied.State.Epoch == 1 &&
        prepared.Binding.AppliedJournalDigest is null && prepared.Endpoint.Evidence.IsGenerationCurrent &&
        ledger.IsCurrentClosed(prepared.Binding) && budget.OutcomeForSettlement == ReadinessStepOutcome.Completed
            ? new(this, prepared, budget, validateReply) : null;

    internal IVerifiedDispatchIssuer? ReadIssuer(VerifiedInstanceRoute route)
    {
        lock (issuerGate)
            return IsCurrentUnderGate(route) ? this : null;
    }

    internal VerifiedDispatchResident? ReadResident(VerifiedInstanceRoute route)
    {
        lock (issuerGate) return IsCurrentUnderGate(route) ? _current!.Resident : null;
    }

    internal bool IsCurrentUnderGate(VerifiedInstanceRoute route) =>
        issuerGate.IsHeldByCurrentThread && !_sealed && _current is { } current && current.Binding.Route == route &&
        current.Endpoint.Evidence.IsGenerationCurrent;

    internal bool TryAttachInitial(OpenDelegation delegation, ReadinessDeadline budget, CancellationToken ct)
    {
        var prepared = delegation.Prepared;
        if (!ReferenceEquals(prepared.Resident.Issuer, this) || !ledger.CanAttachInitial(prepared.Binding)) return false;
        lock (issuerGate)
        {
            if (_current is not null || ct.IsCancellationRequested || !prepared.Endpoint.Evidence.IsGenerationCurrent ||
                budget.OutcomeForSettlement != ReadinessStepOutcome.Completed) return false;
            _current = prepared;
            return true;
        }
    }

    internal ValueTask<ReadinessStep<ServingBrokerProbePair?>> ReadClosedAsync(PreparedGeneration prepared,
        ReadinessDeadline originalDeadline, Func<ServingBrokerUseKind, ReadOnlyMemory<byte>, bool> validateReply) =>
        new ServingBrokerReadiness(ledger, issuerGate, cleanupTimeout)
            .ReadAsync(prepared.Binding, prepared.Endpoint, prepared.ProbePolicy, originalDeadline, validateReply);

    internal Task<ServingBrokerProbePair?> ReadStagedAsync(PreparedGeneration prepared, HostClosedRouteStage stage,
        ReadinessDeadline originalDeadline, Func<ServingBrokerUseKind, ReadOnlyMemory<byte>, bool> validateReply,
        CancellationToken ct)
    {
        if (!ReferenceEquals(prepared.Resident.Issuer, this) || prepared.Binding.Route != stage.Route ||
            prepared.Binding.Applied.State.AppliedLaunch != stage.Launch)
            return Task.FromResult<ServingBrokerProbePair?>(null);

        return new ServingBrokerReadiness(ledger, issuerGate, cleanupTimeout)
            .ReadWithinAsync(prepared.Binding, prepared.Endpoint, prepared.ProbePolicy, originalDeadline, validateReply, ct);
    }

    internal OpenDelegation? DelegateOpen(PreparedGeneration prepared, ServingBrokerProbePair observations) =>
        ReferenceEquals(prepared.Resident.Issuer, this) && prepared.Binding == observations.Binding &&
        prepared.Endpoint.Profile == observations.Profile && ledger.TryDelegateOpen(prepared.Binding, observations.ExchangeId)
            ? new OpenDelegation(prepared) : null;

    // This is part of the local admission -> issuer close. Drain and the independent observer
    // use the durable CloseId after this exact route has been fenced.
    internal bool Seal(VerifiedInstanceRoute route)
    {
        if (!issuerGate.IsHeldByCurrentThread || _current?.Binding.Route != route || canMint(route)) return false;
        SealExactResidentUnderGate();
        return true;
    }

    internal bool CanSealExactResidentUnderGate(VerifiedDispatchResident resident) =>
        issuerGate.IsHeldByCurrentThread && !_sealed && _current is { } current &&
        ReferenceEquals(current.Resident, resident) && current.Binding.Route == resident.Route;

    internal void SealExactResidentUnderGate() => _sealed = true;

    internal bool CanCommitOpen(OpenDelegation delegation, VerifiedInstanceRoute retiredRoute) =>
        issuerGate.IsHeldByCurrentThread && _sealed && ReferenceEquals(delegation.Prepared.Resident.Issuer, this) &&
        _current?.Binding.Route == retiredRoute && delegation.Prepared.Binding.Route.RouteIncarnation > retiredRoute.RouteIncarnation &&
        !canMint(retiredRoute) && delegation.Prepared.Endpoint.Evidence.IsGenerationCurrent;

    internal void CommitOpen(OpenDelegation delegation)
    {
        _current = delegation.Prepared;
        _sealed = false;
    }

    public ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken ct) =>
        ValueTask.FromException<VerifiedInstanceGrant?>(new InvalidOperationException("Certified checkout is required."));

    public ValueTask<VerifiedCheckout> CheckoutVerifiedAsync(VerifiedInstanceInvocation invocation, CancellationToken ct)
    {
        ServingBrokerIssuer? issuer;
        lock (issuerGate)
            issuer = _current?.Binding.Route == invocation.Route ? _current.Issuer : null;
        return issuer is null ? ValueTask.FromResult(new VerifiedCheckout(VerifiedCheckoutDisposition.NoTransfer)) :
            issuer.CheckoutVerifiedAsync(invocation, ct);
    }

    public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
    {
        lock (issuerGate)
            return _current?.Binding.Route == grant.Invocation.Route ? _current.Issuer.TryAuthorizeFirstWrite(grant) : null;
    }

    internal sealed record PreparedGeneration(ServingBrokerBinding Binding, VerifiedDispatchResident Resident,
        ServingBackendEndpoint Endpoint, ServingBrokerProbePolicy ProbePolicy, ServingBrokerIssuer Issuer);

    internal sealed record OpenDelegation(PreparedGeneration Prepared);

}
