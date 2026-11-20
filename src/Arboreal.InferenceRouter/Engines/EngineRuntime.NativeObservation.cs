using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

public sealed partial class EngineRuntime
{
    private static ReadinessFailure NativeObservationFailure =>
        new(ReadinessMiss.NativeObservation);

    private static NativeLaunchWatch? UnknownNativeWatch(NativeLaunchWatch? watch) =>
        watch is { Health: NativeLaunchHealth.Eligible } ? watch with { Health = NativeLaunchHealth.Unknown } : watch;

    private bool RecordPendingNativeLaunch(SwitchState state, NativeLaunch launch, NativeContainerObservation baseline)
    {
        lock (_transitionGate)
        {
            if (_stopped || _retiring || !ReferenceEquals(_switchState, state) || state.End != SwitchEnd.Pending) return false;
            var current = Capability;
            var watch = new NativeLaunchWatch(state.Timing!.Id, launch,
                baseline.Status == NativeContainerStatus.Running ? baseline.Stamp : null,
                baseline.Status == NativeContainerStatus.Invalid ? NativeLaunchHealth.Invalid : NativeLaunchHealth.Pending);
            Volatile.Write(ref _cap, WithoutProbeProof(current) with { NativeWatch = watch });
            return true;
        }
    }

    // Called under the transition gate. A new switch only replaces this block with its own tracked candidate.
    private void InvalidateNativeWatch(EngineCapability current)
    {
        if (current.NativeWatch is not { Health: NativeLaunchHealth.Eligible or NativeLaunchHealth.Unknown } watch) return;
        Volatile.Write(ref _cap, WithoutProbeProof(current) with { NativeWatch = watch with { Health = NativeLaunchHealth.Invalid } });
        _restoredIncumbent = null;
    }

    private bool CanRestoreNativeWatch(SwitchState state, NativeLaunchWatch watch, DockerLifecycleProvider native)
    {
        if (native.HasIssuedStopOrDeleteUnderGate(watch.Launch.ContainerId))
            InvalidateNativeWatch(Capability);
        else if (watch.Health == NativeLaunchHealth.Eligible &&
                 state.IncumbentProof?.NativeWatch?.Attempt == watch.Attempt &&
                 native.CanRestoreIncumbentUnderGate(watch.Launch.ContainerId))
            return true;
        return false;
    }

    private async Task<LegacyReadinessCommit?> ConfirmNativeReadinessAsync(
        SwitchState state, LegacyReadinessObservation result, ReadinessFailure lastMiss, NativeLaunchWatch watch,
        DockerLifecycleProvider native, ReadinessDeadline deadline, CancellationToken stop)
    {
        if (result.Models is null || !WindowFits(result.Serving.WindowFor(state.Model!), state.RequiredWindow))
            return CommitLegacyReadiness(state, result, lastMiss, deadline);

        using var dockerDeadline = ReadinessDeadline.Start(ReadinessClockForTests, native.ObservationTimeout, stop);
        var wait = Shorter(deadline.Remaining, dockerDeadline.Remaining);
        if (!await EnterNativeGateAsync(native, wait, ReadinessClockForTests, stop).ConfigureAwait(false))
            return CommitLegacyReadiness(state, new(Failure: NativeObservationFailure), lastMiss, deadline);

        try
        {
            var confirmed = await native.ObserveContainerAsync(watch.Launch, deadline, dockerDeadline.Remaining)
                .ConfigureAwait(false);
            if (confirmed.Outcome == ReadinessStepOutcome.Completed &&
                confirmed.Observation is { Status: NativeContainerStatus.Running } sample && sample.Stamp == watch.Baseline &&
                native.CanRestoreIncumbentUnderGate(watch.Launch.ContainerId) && !dockerDeadline.IsExpired)
                return CommitLegacyReadiness(state, result, lastMiss, deadline,
                    watch with { Health = NativeLaunchHealth.Eligible }, dockerDeadline, native: native);
            var invalid = confirmed.Outcome == ReadinessStepOutcome.Completed &&
                (ContradictsNativeWatch(confirmed.Observation, watch) || native.HasIssuedStopOrDeleteUnderGate(watch.Launch.ContainerId));
            return CommitLegacyReadiness(state, new(Failure: NativeObservationFailure), lastMiss, deadline,
                invalidNativeAttempt: invalid ? watch.Attempt : null);
        }
        finally { native.ExitOperation(); }
    }

    private async Task<bool> ProbeTrackedNativeAsync(EngineCapability expected, NativeLaunchWatch watch,
        DockerLifecycleProvider native, CancellationToken loopToken)
    {
        if (watch.Health == NativeLaunchHealth.Pending || watch.Baseline is null)
            return !Volatile.Read(ref _stopped);

        ModelsObservation? observation = null;
        ServingCapacity serving = default;
        Exception? error = null;
        using (var budget = new CancellationTokenSource(_def.Health?.TimeoutMs ?? 5_000))
        {
            if (!TryBeginHttpUse(loopToken)) return false;
            try
            {
                var models = await FetchModelsObservationAsync(budget.Token).ConfigureAwait(false);
                if (!Volatile.Read(ref _stopped))
                {
                    var state = await ReadServingProbeAsync(budget.Token).ConfigureAwait(false);
                    budget.Token.ThrowIfCancellationRequested();
                    if (state.Answered)
                    {
                        observation = models;
                        serving = BindServing(models, state);
                    }
                }
            }
            catch (Exception ex) { error = ex; }
            finally { EndHttpUse(); }
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(loopToken, _switchCts.Token);
        if (stop.IsCancellationRequested) return false;
        using var deadline = ReadinessDeadline.Start(_clock, native.ObservationTimeout, stop.Token);
        var read = await native.ObserveContainerAsync(watch.Launch, deadline).ConfigureAwait(false);
        if (stop.IsCancellationRequested) return false;
        var sample = read.Outcome == ReadinessStepOutcome.Completed ? read.Observation : default;
        EngineCapability? published = null;
        var reconcileApplication = false;
        if (ContradictsNativeWatch(sample, watch))
        {
            published = CommitNativePoll(expected, watch, NativeLaunchHealth.Invalid);
        }
        else if (await EnterNativeGateAsync(native, deadline.Remaining, _clock, stop.Token).ConfigureAwait(false))
        {
            try
            {
                // A local Stop/DELETE can run during the unlocked GET. It cannot run between this check and CAS.
                var status = native.HasIssuedStopOrDeleteUnderGate(watch.Launch.ContainerId) ? NativeLaunchHealth.Invalid :
                    sample.Status == NativeContainerStatus.Running && observation is not null &&
                    native.CanRestoreIncumbentUnderGate(watch.Launch.ContainerId)
                        ? NativeLaunchHealth.Eligible : NativeLaunchHealth.Unknown;
                published = CommitNativePoll(expected, watch, status, observation, serving, deadline);
                reconcileApplication = status == NativeLaunchHealth.Eligible && expected.SwitchingToward is not null;
            }
            finally { native.ExitOperation(); }
        }
        else if (!stop.IsCancellationRequested)
        {
            published = CommitNativePoll(expected, watch, NativeLaunchHealth.Unknown);
        }

        if (reconcileApplication) ReconcileIncumbentObservation(expected, observation, serving);
        if (published is not null) NotifyServingChange(expected.Serving, published.Serving);
        if (Volatile.Read(ref _stopped)) return false;
        if (Capability.NativeWatch?.Attempt != watch.Attempt) return true;
        Emit("engine.health", reason: error is null ? null : WireText.Bound(error.Message, ProviderReasonChars),
            value: observation?.LoadedModels.Count ?? 0);
        return true;
    }

    private EngineCapability? CommitNativePoll(EngineCapability expected, NativeLaunchWatch captured,
        NativeLaunchHealth health, ModelsObservation? observation = null, ServingCapacity serving = default,
        ReadinessDeadline? deadline = null)
    {
        lock (_transitionGate)
        {
            var current = Capability;
            if (_stopped || _retiring || current.NativeWatch is not { } watch || watch.Attempt != captured.Attempt ||
                watch.Health is NativeLaunchHealth.Pending or NativeLaunchHealth.Invalid)
                return null;
            if (health == NativeLaunchHealth.Eligible && deadline?.OutcomeForSettlement != ReadinessStepOutcome.Completed)
                health = NativeLaunchHealth.Unknown;

            if (health == NativeLaunchHealth.Invalid)
            {
                InvalidateNativeWatch(current);
                return Capability;
            }

            if (health == NativeLaunchHealth.Unknown)
            {
                if (watch.Health == NativeLaunchHealth.Unknown) return null;
                var unknown = WithoutProbeProof(current) with { NativeWatch = watch with { Health = health } };
                Volatile.Write(ref _cap, unknown);
                _restoredIncumbent = null;
                return unknown;
            }

            if (!ReferenceEquals(current, expected) || current.SwitchingToward is not null || observation is not { } models)
                return null;
            var next = WithModelsObservation(current, models, serving);
            if (watch.Health == health && ReferenceEquals(next, current)) return null;
            if (ReferenceEquals(next, current)) next = next with { Version = current.Version + 1 };
            next = next with { NativeWatch = watch with { Health = health } };
            Volatile.Write(ref _cap, next);
            return next;
        }
    }

    private static async ValueTask<bool> EnterNativeGateAsync(DockerLifecycleProvider native, TimeSpan wait,
        TimeProvider clock, CancellationToken stop)
    {
        if (wait <= TimeSpan.Zero || stop.IsCancellationRequested) return false;
        using var timeout = new CancellationTokenSource(wait, clock);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(stop, timeout.Token);
        try
        {
            await native.EnterOperationAsync(combined.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (combined.IsCancellationRequested) { return false; }
    }

    private static TimeSpan Shorter(TimeSpan first, TimeSpan second) => first <= second ? first : second;

    private static bool ContradictsNativeWatch(NativeContainerObservation sample, NativeLaunchWatch watch) =>
        sample.Status == NativeContainerStatus.Invalid ||
        sample.Status == NativeContainerStatus.Running && sample.Stamp != watch.Baseline;
}
