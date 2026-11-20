namespace Arboreal.InferenceRouter;

public sealed partial class EngineRuntime
{
    private bool _retiring;
    private int _inferenceUses;
    private int _externalPublications;
    private ExternalActivation _reloadActivation;

    internal enum ExternalActivation { Pending, Registration, FirstProbe }

    internal bool ReloadableExternal => _reloadSource is not null &&
        _lifecycleBinding is null && _lifecycle is null && _hostEnrollment is null;

    internal object ReloadTransitionGate => _transitionGate;
    internal int InferenceUses => Volatile.Read(ref _inferenceUses);

    // Caller holds the transition gate. Check every affected runtime before fencing any of them.
    internal bool CanRetireExternal => ReloadableExternal && !_stopped && !_retiring &&
        _inferenceUses == 0 && _externalPublications == 0;

    internal bool ReportsReloadDefault(string model)
    {
        var capability = Capability;
        return ReloadableExternal && !Volatile.Read(ref _stopped) && !Volatile.Read(ref _retiring) &&
            capability.Healthy && capability.SwitchingToward is null &&
            capability.ModelRoster.Contains(model, StringComparer.OrdinalIgnoreCase);
    }

    internal void FenceExternalRetirement()
    {
        Volatile.Write(ref _retiring, true);
        WithdrawDiscoveryOnStop();
    }

    internal bool TryReserveExternalPublication()
    {
        lock (_transitionGate)
        {
            if (_stopped || _retiring) return false;
            _externalPublications++;
            return true;
        }
    }

    internal void EndExternalPublication()
    {
        lock (_transitionGate) _externalPublications--;
    }

    // Caller holds the router and engine transition gates. Record ownership at the same cut
    // that arbitrates closing and retirement. The worker performs the claimed action outside.
    internal bool TryClaimReloadActivation(ExternalActivation next)
    {
        var previous = next == ExternalActivation.Registration
            ? ExternalActivation.Pending : ExternalActivation.Registration;
        if (_stopped || _retiring || _externalPublications == 0 || _reloadActivation != previous) return false;
        _reloadActivation = next;
        return true;
    }

    internal int ExternalPublicationsForTests
    {
        get { lock (_transitionGate) return _externalPublications; }
    }

    private bool PublishExternalProbe(EngineCapability expected, ModelsObservation? observation,
        ServingCapacity serving, Exception? error, bool withdraw)
    {
        EngineCapability? next = null;
        lock (_transitionGate)
        {
            if (_stopped || _retiring) return false;
            // Externally managed runtimes cannot install a claim or restore an incumbent.
            // Stop, retirement, and these publications all write under this gate.
            if (ReferenceEquals(Capability, expected))
            {
                next = observation is { } models ? WithModelsObservation(expected, models, serving)
                    : withdraw && expected.Healthy ? WithoutProbeProof(expected) : null;
                if (next is not null) Volatile.Write(ref _cap, next);
            }
            _externalPublications++;
        }

        try
        {
            if (next is not null) NotifyServingChange(expected.Serving, next.Serving);
            Emit("engine.health", reason: error is null ? null : WireText.Bound(error.Message, ProviderReasonChars),
                value: observation?.LoadedModels.Count ?? 0);
            return true;
        }
        finally
        {
            EndExternalPublication();
        }
    }

    private void EmitUnclaimedRetryFailure(string model)
    {
        const string reason = "switch_error: serving observations changed during every claim attempt";
        if (!ReloadableExternal)
        {
            Emit("engine.switch_failed", model: model, reason: reason);
            return;
        }
        if (!TryReserveExternalPublication()) return;
        try { Emit("engine.switch_failed", model: model, reason: reason); }
        finally { EndExternalPublication(); }
    }
}
