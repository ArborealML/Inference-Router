using System.Collections.Immutable;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed record HostEngineEnrollment(string RouterId, string Engine, HostLifecycleCoordinator Controller,
    HostStagedRuntimeFixture? StagedForTests = null);

internal sealed record HostStagedRuntimeFixture(
    Func<HostClosedRouteStage, CancellationToken, Task<HostStagedReadiness?>> ReadAsync,
    Action? AfterStageInstalled = null, Action<SwitchServingProof>? AfterProof = null, Action? AfterComplete = null,
    Action? AfterRun = null);

public sealed partial class EngineRuntime
{
    private readonly HostEngineEnrollment? _hostEnrollment;

    private async ValueTask<EngineLifecycleCoordinator.SwitchReport> SwitchEnrolledHostAsync(
        HostEngineEnrollment enrollment, SwitchState state, LifecycleTarget target, CancellationToken ct)
    {
        var publication = new HostSwitchPublication(
            proof => TryPublishProof(state, new ModelsObservation(ImmutableList.Create(proof.Target.Model),
                ImmutableDictionary<string, long>.Empty)),
            () =>
            {
                Emit("engine.switch_completed", model: target.Model,
                    reason: state.Profile is null ? null : $"profile={WireText.Ident(state.Profile)}",
                    value: _clock.GetElapsedTime(state.Timing!.StartedAt).TotalMilliseconds);
                EndSwitch(state, true);
            });
        var result = await enrollment.Controller.SwitchAsync(target, ct, publication).ConfigureAwait(false);

        // This slice carries no incumbent instance proof to restore. Physical preflight refusal
        // still leaves the incumbent running, and the legacy path retains its own restoration check.
        return new EngineLifecycleCoordinator.SwitchReport(result, RefusedBeforeMutation: false);
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
        var publication = new HostStagedDeadlinePublication(claimId, timing.Id, timing.Target, ReadinessClockForTests, fixture.ReadAsync,
            Settle, Deliver, TryOpen, NotifyOpened,
            fixture.AfterStageInstalled is { } installed ? _ => installed() : null);
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
                return !_stopped && state.End == SwitchEnd.Ready && ReferenceEquals(state.Proof, proof) &&
                    ReferenceEquals(Capability.SwitchProof, proof) && MatchesClaim(binding) && open();
            }
        }

        void NotifyOpened()
        {
            // Let the staged result report notification faults after the route has opened.
            if (!Volatile.Read(ref _stopped)) _dispatchHint?.Invoke();
        }
    }
}
