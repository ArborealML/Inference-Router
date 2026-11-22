using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

public sealed partial class EngineRuntime
{
    private bool UsesVllmArgv => _def.Docker?.UsesVllmArgv == true;
    private static readonly TimeSpan ProfileVerificationLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProfileRetryInterval = TimeSpan.FromSeconds(2);

    private readonly record struct ManagedProfileRound(NativeVllmObservation Native, ServingCapacity? Capacity,
        ReadinessFailure Failure, bool Mismatch = false);

    private static bool HasAmbiguousProfileJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || HasAmbiguousProfileJson(property.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (HasAmbiguousProfileJson(item)) return true;
        }
        return false;
    }

    private static ReadinessFailure MissingProfileField(string field) =>
        new(ReadinessMiss.LoadedProfile, Error: $"Loaded profile could not verify {field}.");

    private static ManagedProfileRound ProfileMismatch(NativeVllmObservation native, string field, long expected, long observed) =>
        new(native, null, new(ReadinessMiss.LoadedProfile,
            Error: FormattableString.Invariant($"Loaded profile mismatch for {field}: expected={expected} observed={observed}.")),
            Mismatch: true);

    private async Task<ManagedProfileRound> VerifyManagedProfileAsync(NativeVllmObservation before, ModelsObservation? models, ServingProfile tier,
        NativeLaunchWatch watch, DockerLifecycleProvider native, ReadinessDeadline deadline)
    {
        var health = default(ServingProbe);
        if (models is not null)
        {
            var read = await deadline.ObserveAsync(ReadManagedHealthAsync).ConfigureAwait(false);
            if (read.Outcome != ReadinessStepOutcome.Completed)
                return new(default, null, MissingProfileField("HTTP health"));
            health = read.Observation;
        }

        var observation = await native.ObserveVllmAsync(watch.Launch, watch.Baseline!.Value, deadline).ConfigureAwait(false);
        var sample = observation.Outcome == ReadinessStepOutcome.Completed ? observation.Observation : default;
        if (before.Status == NativeContainerStatus.Invalid || sample.Status == NativeContainerStatus.Invalid ||
            before.Evidence is { } initial && sample.Evidence is { } final && initial.Process != final.Process ||
            sample.Evidence is { } process &&
            watch.ServingProcess is { } previous && previous != process.Process)
            return new(new(NativeContainerStatus.Invalid), null, MissingProfileField("serving process generation"));
        var model = watch.Launch.Model;
        if (models is not { } roster || !string.Equals(roster.ServingModel, model, StringComparison.OrdinalIgnoreCase))
            return new(sample, null, MissingProfileField("the unique loaded model row"));
        if (sample.Evidence is not { } evidence || before.Evidence != evidence)
            return new(sample, null, MissingProfileField("--max-num-seqs from the exact serving process"));

        var window = roster.ContextWindows.GetValueOrDefault(model);
        if (window > 0 && window != tier.MaxContext)
            return ProfileMismatch(sample, "model context", tier.MaxContext, window);
        if (evidence.Slots > 0 && evidence.Slots != tier.MaxSlots)
            return ProfileMismatch(sample, "--max-num-seqs", tier.MaxSlots, evidence.Slots);
        if (health.Window > 0 && health.Window != tier.MaxContext)
            return ProfileMismatch(sample, "HTTP context", tier.MaxContext, health.Window);
        if (health.Slots > 0 && health.Slots != tier.MaxSlots)
            return ProfileMismatch(sample, "HTTP slots", tier.MaxSlots, health.Slots);

        if (window == 0) return new(sample, null, MissingProfileField("model context"));
        if (evidence.Slots == 0) return new(sample, null, MissingProfileField("--max-num-seqs"));
        if (!health.Satisfied) return new(sample, null, MissingProfileField("HTTP health"));
        return new(sample, new(model, window, evidence.Slots, tier.Name), default);
    }

    private async Task RunManagedReadinessAsync(SwitchState state, LifecycleTarget target, ReadinessPolicy readiness,
        DockerLifecycleProvider native, CancellationToken stop)
    {
        var timeout = readiness.Timeout;
        if (state.LocalOwner is { } owner && owner.Budget.Remaining is var remaining && remaining != Timeout.InfiniteTimeSpan)
            timeout = Shorter(timeout, remaining);
        using var startup = ReadinessDeadline.Start(ReadinessClockForTests, timeout, stop);
        ReadinessDeadline? verification = null;
        var failure = default(ReadinessFailure);
        try
        {
            if (Capability.NativeWatch is not { Baseline: not null } watch ||
                GetProfile(target.Model, watch.Launch.Profile) is not { } tier)
            {
                failure = MissingProfileField("container generation and selected tier");
                FinishManagedReadiness(state, target, readiness, native, failure, startup);
                return;
            }

            while (true)
            {
                var deadline = verification ?? startup;
                var before = await native.ObserveVllmAsync(watch.Launch, watch.Baseline.Value, deadline).ConfigureAwait(false);
                var read = await deadline.ObserveAsync(async ct =>
                {
                    var models = await ReadManagedModelsAsync(ct).ConfigureAwait(false);
                    return (Models: models, Timestamp: ReadinessClockForTests.GetTimestamp());
                }).ConfigureAwait(false);
                if (read.Outcome != ReadinessStepOutcome.Completed) break;
                if (read.Observation.Models is { } models && models.LoadedModels.Contains(target.Model, StringComparer.OrdinalIgnoreCase))
                {
                    verification ??= startup.LimitFrom(read.Observation.Timestamp, ProfileVerificationLimit);
                    deadline = verification;
                    var round = await VerifyManagedProfileAsync(before.Observation, models, tier, watch, native, deadline).ConfigureAwait(false);
                    if (!deadline.IsExpired || failure.Miss != ReadinessMiss.LoadedProfile) failure = round.Failure;
                    if (round.Native.Status == NativeContainerStatus.Invalid || round.Mismatch || round.Capacity is not null)
                    {
                        BeforeReadinessCommitForTests?.Invoke();
                        if (await EnterNativeGateAsync(native, deadline.Remaining, ReadinessClockForTests, stop).ConfigureAwait(false))
                        {
                            LegacyReadinessCommit? commit;
                            try { commit = CommitManagedRound(state, watch, native, models, round, deadline); }
                            finally { native.ExitOperation(); }
                            if (commit is { } settled) DeliverLegacyReadiness(state, target, readiness, native.Kind, settled);
                            return;
                        }
                        failure = MissingProfileField("serving process generation before the deadline");
                        break;
                    }
                }
                else if (verification is null)
                {
                    if (read.Observation.Models is { } other)
                        failure = new(ReadinessMiss.ModelNotReported, other.LoadedModels);
                    var container = await native.ObserveContainerAsync(watch.Launch, startup).ConfigureAwait(false);
                    if (container.Outcome != ReadinessStepOutcome.Completed) break;
                    if (ContradictsNativeWatch(container.Observation, watch))
                    {
                        failure = NativeObservationFailure;
                        break;
                    }
                }
                // A later failed or incomplete model read cannot replace the last field diagnosis or restart the timer.
                if (await deadline.DelayAsync(ProfileRetryInterval).ConfigureAwait(false) != ReadinessStepOutcome.Completed) break;
            }

            FinishManagedReadiness(state, target, readiness, native, failure, verification ?? startup);
        }
        finally { verification?.Dispose(); }
    }

    private LegacyReadinessCommit? CommitManagedRound(SwitchState state, NativeLaunchWatch watch,
        DockerLifecycleProvider native, ModelsObservation models, ManagedProfileRound round, ReadinessDeadline deadline)
    {
        var expired = MissingProfileField("loaded profile before the deadline");
        if (round.Native.Status == NativeContainerStatus.Invalid || native.HasIssuedStopOrDeleteUnderGate(watch.Launch.ContainerId))
            return CommitLegacyReadiness(state, new(Failure: MissingProfileField("serving process generation")), expired,
                deadline, invalidNativeAttempt: watch.Attempt);
        if (round.Capacity is not { } capacity)
            return CommitLegacyReadiness(state, new(Failure: round.Failure), expired, deadline);
        if (!WindowFits(capacity.ContextWindow, state.RequiredWindow))
            return CommitLegacyReadiness(state, new(Failure: new(ReadinessMiss.WindowTooSmall, Window: capacity.ContextWindow)),
                expired, deadline);
        var qualified = watch with { Health = NativeLaunchHealth.Eligible, ServingProcess = round.Native.Evidence!.Value.Process };
        return CommitLegacyReadiness(state, new(models, capacity), expired, deadline, qualified, native: native);
    }

    private void FinishManagedReadiness(SwitchState state, LifecycleTarget target, ReadinessPolicy readiness,
        DockerLifecycleProvider native, ReadinessFailure failure, ReadinessDeadline deadline)
    {
        if (CommitLegacyReadiness(state, new(Failure: failure), failure, deadline) is { } settled)
            DeliverLegacyReadiness(state, target, readiness, native.Kind, settled);
    }

    private async Task<bool> ProbeManagedProfileAsync(EngineCapability expected, NativeLaunchWatch watch,
        DockerLifecycleProvider native, CancellationToken loopToken)
    {
        if (watch.Health is NativeLaunchHealth.Pending or NativeLaunchHealth.Invalid || watch.Baseline is null ||
            GetProfile(watch.Launch.Model, watch.Launch.Profile) is not { } tier)
            return !Volatile.Read(ref _stopped);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(loopToken, _switchCts.Token);
        var probeMs = _def.Health?.TimeoutMs ?? 5_000;
        var limit = probeMs == Timeout.Infinite ? ProfileVerificationLimit : TimeSpan.FromMilliseconds(probeMs);
        using var deadline = ReadinessDeadline.Start(_clock, Shorter(limit, native.ObservationTimeout), stop.Token);
        var before = await native.ObserveVllmAsync(watch.Launch, watch.Baseline.Value, deadline).ConfigureAwait(false);
        var read = await deadline.ObserveAsync(ReadManagedModelsAsync).ConfigureAwait(false);
        var models = read.Outcome == ReadinessStepOutcome.Completed ? read.Observation : null;
        var round = await VerifyManagedProfileAsync(before.Observation, models, tier, watch, native, deadline).ConfigureAwait(false);
        if (stop.IsCancellationRequested) return false;

        (ServingCapacity Previous, ServingCapacity Current)? published;
        if (round.Native.Status == NativeContainerStatus.Invalid)
            published = CommitNativePoll(expected, watch, NativeLaunchHealth.Invalid);
        else if (await EnterNativeGateAsync(native, deadline.Remaining, _clock, stop.Token).ConfigureAwait(false))
        {
            try
            {
                var status = native.HasIssuedStopOrDeleteUnderGate(watch.Launch.ContainerId) ? NativeLaunchHealth.Invalid :
                    round.Capacity is not null && native.CanRestoreIncumbentUnderGate(watch.Launch.ContainerId)
                        ? NativeLaunchHealth.Eligible : NativeLaunchHealth.Unknown;
                published = CommitNativePoll(expected, watch, status, models, round.Capacity.GetValueOrDefault(), deadline);
            }
            finally { native.ExitOperation(); }
        }
        else if (!stop.IsCancellationRequested)
            published = CommitNativePoll(expected, watch, NativeLaunchHealth.Unknown);
        else return false;

        if (published is { } change) NotifyServingChange(change.Previous, change.Current);
        if (Volatile.Read(ref _stopped)) return false;
        if (Capability.NativeWatch?.Attempt == watch.Attempt)
            Emit("engine.health", reason: round.Capacity is null ? round.Failure.Error : null, value: models?.LoadedModels.Count ?? 0);
        return true;
    }

    private async Task<ModelsObservation?> ReadManagedModelsAsync(CancellationToken ct)
    {
        if (!TryBeginHttpUse(ct)) return null;
        try { return await ReadReadinessModelsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { return null; }
        finally { EndHttpUse(); }
    }

    private async Task<ServingProbe> ReadManagedHealthAsync(CancellationToken ct)
    {
        if (!TryBeginHttpUse(ct)) return default;
        try { return await ReadReadinessStateAsync(ct).ConfigureAwait(false); }
        finally { EndHttpUse(); }
    }
}
