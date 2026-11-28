using System.Collections.Immutable;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed record HostEngineEnrollment(string RouterId, string Engine, HostLifecycleCoordinator Controller,
    HostStagedRuntimeFixture? StagedForTests = null, HostImmediateRuntimeFixture? ImmediateForTests = null,
    InProcessHostManager? Manager = null, Action<HostClosedRouteStage, SwitchServingProof>? RouteOpened = null);

internal sealed record HostImmediateRuntimeFixture(Action? BeforeComplete = null, Action<LifecycleResult>? AfterResult = null,
    Action? AfterRun = null);

internal sealed record HostStagedRuntimeFixture(
    Func<HostClosedRouteStage, CancellationToken, Task<HostStagedReadiness?>> ReadAsync,
    Action? AfterStageInstalled = null, Action<SwitchServingProof>? AfterProof = null, Action? AfterComplete = null,
    Action? AfterRun = null, Action<HostStagedSwitchResult>? AfterHostResult = null,
    Action<Task>? CaptureReadinessCleanup = null);

public sealed partial class EngineRuntime
{
    private readonly HostEngineEnrollment? _hostEnrollment;

    private async ValueTask RunImmediateHostSwitchAsync(
        HostEngineEnrollment enrollment, SwitchState state, LifecycleTarget target, CancellationToken ct)
    {
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SwitchServingProof? provisional = null;
        var accepted = false;
        var publication = new HostSwitchPublication(TryPublish, TryAccept, Complete);
        var result = await enrollment.Controller.SwitchAsync(target, ct, publication).ConfigureAwait(false);
        enrollment.ImmediateForTests?.AfterResult?.Invoke(result);

        // The controller can return an I/O refusal after completion. Only a pending claim can fail.
        if (!result.Succeeded && TrySelect(ready: false)) Deliver(result);

        bool TryPublish(HostServingProof hostProof)
        {
            var observation = new ModelsObservation(ImmutableList.Create(hostProof.Target.Model),
                EngineCapability.NoModelContextWindows);
            lock (_transitionGate)
            {
                if (!PendingClaim() || !WindowFits(0, state.RequiredWindow) ||
                    !TryPublishClaimProof(state, observation, default, deadline: null, out _)) return false;
                provisional = state.Proof;
            }

            AfterSwitchProofForTests?.Invoke();
            lock (_transitionGate) return PendingClaim() && HasCurrentProof();
        }

        bool TryAccept(Action store)
        {
            lock (_transitionGate)
            {
                if (accepted || !PendingClaim() || !HasCurrentProof()) return false;
                // The host guard is already held. This callback only stores the exact host proof.
                store();
                accepted = true;
                return true;
            }
        }

        void Complete()
        {
            // The coordinator enters this callback only after the host's acceptance call returns.
            enrollment.ImmediateForTests?.BeforeComplete?.Invoke();
            if (TrySelect(ready: true)) Deliver(refusal: null);
        }

        bool TrySelect(bool ready)
        {
            lock (_transitionGate)
            {
                if (!PendingClaim() || (ready && (!accepted || !HasCurrentProof()))) return false;
                if (!ready) state.FailureReason = SwitchReason.LaunchFailed;
                state.ReadinessOutcomeDelivered = delivery;
                state.End = ready ? SwitchEnd.Ready : SwitchEnd.Failed;
                return true;
            }
        }

        // Both checks describe one claim under the transition lock.
        bool PendingClaim() => !_stopped && ReferenceEquals(_switchState, state) && state.End == SwitchEnd.Pending;

        bool HasCurrentProof() => provisional is not null && ReferenceEquals(state.Proof, provisional) &&
            ReferenceEquals(Capability.SwitchProof, provisional);

        void Deliver(LifecycleResult? refusal)
        {
            try
            {
                // The host has returned from its admission callback before either notification.
                if (refusal is null)
                {
                    Emit("engine.switch_completed", model: target.Model,
                        reason: state.Profile is null ? null : $"profile={WireText.Ident(state.Profile)}",
                        value: _clock.GetElapsedTime(state.Timing!.StartedAt).TotalMilliseconds);
                }
                else
                {
                    try { Emit("engine.switch_failed", model: target.Model, reason: DescribeLaunchFailure(refusal)); }
                    finally
                    {
                        // The immediate host path supplies no incumbent proof to restore.
                        var unknown = PublishUnknown();
                        AfterFailedMutationUnknownForTests?.Invoke(unknown);
                    }
                }
            }
            finally
            {
                try { EndSwitch(state, ready: refusal is null); }
                finally { delivery.TrySetResult(); }
            }
        }
    }

    private ValueTask<HostStagedSwitchResult> SwitchStagedHostAsync(
        HostLifecycleCoordinator controller, HostStagedRuntimeFixture fixture, SwitchState state,
        LifecycleTarget target, CancellationToken stop)
    {
        var claimId = Guid.NewGuid();
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = target.Profile ?? GetProfile(target.Model, target.Definition.Profile);
        var requiredWindow = RequiredWindow(profile, state.RequiredWindow);
        var timing = state.Timing!;
        HostClosedRouteStage? openedStage = null;
        SwitchServingProof? openedProof = null;
        var publication = new HostStagedDeadlinePublication(claimId, timing.Id, timing.Target, ReadinessClockForTests, fixture.ReadAsync,
            Settle, Deliver, TryOpen, NotifyOpened,
            fixture.AfterStageInstalled is { } installed ? _ => installed() : null,
            fixture.CaptureReadinessCleanup, state.HostTransition?.Recipe.PlanDigest, state.HostTransition?.Cut);
        return controller.SwitchStagedAsync(target, publication, stop);

        bool MatchesClaim(HostClosedRouteStage binding) =>
            binding.ClaimId == claimId && binding.Launch.Plan.Engine == target.EngineName &&
            binding.Launch.Plan.Model == target.Model && binding.Launch.Plan.Tier == (target.Profile?.Name ?? target.Definition.Profile);

        HostStagedDecision Settle(HostClosedRouteStage binding, HostStagedReadiness? evidence,
            ReadinessDeadline deadline, Action<SwitchServingProof> commitClosedRoute)
        {
            // The host gate is already held. The supplied store only installs this closed route.
            lock (_transitionGate)
            {
                if (_stopped) return new(HostStagedVerdict.Stopped);
                if (!ReferenceEquals(_switchState, state) || state.End != SwitchEnd.Pending || !MatchesClaim(binding))
                    return default;

                var outcome = deadline.OutcomeForSettlement;
                if (outcome != ReadinessStepOutcome.Completed) return Retire(outcome);
                if (evidence is null || evidence.Binding != binding || evidence.CanonicalModel != target.Model)
                    return Retire(deadline.OutcomeForSettlement);

                var serving = evidence.Serving;
                if (((serving.Model is not null || serving.ContextWindow > 0) && serving.Model != evidence.CanonicalModel) ||
                    !WindowFits(serving.WindowFor(evidence.CanonicalModel), requiredWindow) ||
                    (state.ExactProfile is { } exact && !serving.MatchesExactly(evidence.CanonicalModel, exact)) ||
                    (profile is not null && (serving.Profile != profile.Name || serving.TotalSlots <= 0)))
                    return Retire(deadline.OutcomeForSettlement);

                var models = new ModelsObservation(ImmutableList.Create(evidence.CanonicalModel),
                    EngineCapability.NoModelContextWindows, evidence.CanonicalModel);
                while (!TryPublishClaimProof(state, models, serving, deadline, out outcome))
                {
                    // A lost CAS retries under the same gates, binding and original deadline.
                    if (outcome != ReadinessStepOutcome.Completed) return Retire(outcome);
                }

                var proof = state.Proof!;
                commitClosedRoute(proof);
                state.ReadinessOutcomeDelivered = delivery;
                state.End = SwitchEnd.Ready;
                return new(HostStagedVerdict.Committed, proof);
            }
        }

        HostStagedDecision Retire(ReadinessStepOutcome outcome)
        {
            if (outcome == ReadinessStepOutcome.Stopped) return new(HostStagedVerdict.Stopped);
            if (outcome != ReadinessStepOutcome.Expired) return default;
            state.FailureReason = SwitchReason.HealthTimeout;
            state.ReadinessOutcomeDelivered = delivery;
            state.End = SwitchEnd.Failed;
            return new(HostStagedVerdict.Expired);
        }

        void Deliver(HostStagedDecision decision)
        {
            if (decision.Verdict is not (HostStagedVerdict.Committed or HostStagedVerdict.Expired)) return;
            var ready = decision.Verdict == HostStagedVerdict.Committed;
            try
            {
                if (ready)
                {
                    try
                    {
                        if (decision.Proof is { } proof) fixture.AfterProof?.Invoke(proof);
                    }
                    finally
                    {
                        Emit("engine.switch_completed", model: target.Model,
                            reason: profile is null ? null : $"profile={WireText.Ident(profile.Name)}",
                            value: _clock.GetElapsedTime(state.Timing!.StartedAt).TotalMilliseconds);
                    }
                }
                else
                {
                    Emit("engine.switch_failed", model: target.Model,
                        reason: "health_timeout: the original launch readiness deadline expired");
                }
            }
            finally
            {
                try { EndSwitch(state, ready); }
                finally
                {
                    delivery.TrySetResult();
                    if (ready) fixture.AfterComplete?.Invoke();
                }
            }
        }

        bool TryOpen(HostClosedRouteStage binding, SwitchServingProof proof, Func<bool> open)
        {
            lock (_transitionGate)
            {
                if (_stopped || state.End != SwitchEnd.Ready || !ReferenceEquals(state.Proof, proof) ||
                    !ReferenceEquals(Capability.SwitchProof, proof) || !MatchesClaim(binding) || !open()) return false;
                openedStage = binding;
                openedProof = proof;
                return true;
            }
        }

        void NotifyOpened()
        {
            // Let the staged result report notification faults after the route has opened.
            if (Volatile.Read(ref _stopped)) return;
            if (openedStage is not null && openedProof is not null) _hostEnrollment?.RouteOpened?.Invoke(openedStage, openedProof);
            _dispatchHint?.Invoke();
        }
    }
}
