namespace Arboreal.InferenceRouter.Lifecycle;

// The caller supplies one budget and the launch-approved body validator. This adapter can
// verify an existing closed resident and attach its issuer. The startup controller separately
// verifies installed occupancy and the original anchor before publishing capability/admission.
internal sealed class CommissionedServingStartupPort(CommissionedServingIssuer issuer,
    CommissionedServingIssuer.PreparedGeneration prepared, ReadinessDeadline budget,
    Func<ServingBrokerUseKind, ReadOnlyMemory<byte>, bool> validateReply)
{
    private int _started;

    internal VerifiedDispatchResident Resident => prepared.Resident;
    internal Lock IssuerGate => issuer.IssuerGate;

    internal IVerifiedDispatchIssuer? ReadIssuer(VerifiedInstanceRoute route) =>
        issuer.ReadIssuer(route) ??
        (route == Resident.Route && Volatile.Read(ref _started) == 0 &&
            prepared.Endpoint.Evidence.IsGenerationCurrent &&
            budget.OutcomeForSettlement == ReadinessStepOutcome.Completed ? issuer : null);

    // Call with IssuerGate already held before transition, after the final installed read.
    // The startup budget is independent of later requests and must not gate their first writes.
    internal bool CanPublishInitial(VerifiedInstanceRoute route) =>
        route == Resident.Route && issuer.IsCurrentUnderGate(route) &&
        budget.OutcomeForSettlement == ReadinessStepOutcome.Completed;

    internal async Task<EnrolledInitialReadiness?> ObserveReadinessAsync(VerifiedInstanceRoute route, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (route != Resident.Route || budget.OutcomeForSettlement != ReadinessStepOutcome.Completed ||
            !prepared.Endpoint.Evidence.IsGenerationCurrent || Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return null;

        var result = await issuer.ReadClosedAsync(prepared, budget, validateReply).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (result is not { Outcome: ReadinessStepOutcome.Completed, Observation: { } pair } ||
            budget.OutcomeForSettlement != ReadinessStepOutcome.Completed ||
            !prepared.Endpoint.Evidence.IsGenerationCurrent ||
            issuer.DelegateOpen(prepared, pair) is not { } delegation || !issuer.TryAttachInitial(delegation, budget, ct))
            return null;

        var profile = pair.Profile;
        return new(route, profile.CanonicalModel, new(profile.CanonicalModel, profile.Context, profile.Slots, profile.Profile));
    }
}
