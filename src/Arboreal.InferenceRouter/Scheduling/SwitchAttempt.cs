using System.Collections.Immutable;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal readonly record struct SwitchAttemptId(Guid RuntimeEpoch, long Sequence);

internal enum SwitchStage
{
    Queue,
    Ownership,
    Preflight,
    Drain,
    Shutdown,
    Launch,
    DelegatedCommand,
    Readiness,
}

internal enum SwitchStageOutcome { Incomplete, Completed, Failed }
internal enum SwitchAttemptOutcome { Ready, Failed, Cancelled, Overtaken }
internal enum SwitchObservationClass { Unknown, FreshContainer, WarmInPlace }

internal readonly record struct SwitchSampleKey(
    Guid RuntimeEpoch,
    string ProviderKind,
    string CanonicalModel,
    string? EffectiveProfile,
    long ConfigGeneration,
    SwitchObservationClass ObservationClass,
    string? ProviderIdentity)
{
    internal bool Matches(SwitchSampleKey other) =>
        RuntimeEpoch == other.RuntimeEpoch &&
        string.Equals(ProviderKind, other.ProviderKind, StringComparison.Ordinal) &&
        string.Equals(CanonicalModel, other.CanonicalModel, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(EffectiveProfile, other.EffectiveProfile, StringComparison.Ordinal) &&
        ConfigGeneration == other.ConfigGeneration &&
        ObservationClass == other.ObservationClass &&
        string.Equals(ProviderIdentity, other.ProviderIdentity, StringComparison.Ordinal);
}

internal readonly record struct SwitchQueueTiming(long EnqueuedAt, long SelectedAt);

internal sealed class SwitchServingProof(
    SwitchAttemptId attempt, long version, SwitchSampleKey target, NativeLaunch? nativeLaunch)
{
    private int _forwardObserved;
    internal SwitchAttemptId Attempt { get; } = attempt;
    internal long Version { get; } = version;
    internal SwitchSampleKey Target { get; } = target;
    internal NativeLaunch? NativeLaunch { get; } = nativeLaunch;
    internal bool ForwardObserved => Volatile.Read(ref _forwardObserved) != 0;
    internal void MarkForwardObserved() => Volatile.Write(ref _forwardObserved, 1);
}

/// <summary>An absent stage has no value. An incomplete stage was still open when the attempt ended.</summary>
internal readonly record struct SwitchStageObservation(
    long StartedAt, long ObservedUntil, SwitchStageOutcome Outcome);

internal sealed record SwitchAttemptRecord(
    SwitchAttemptId Id,
    SwitchSampleKey Target,
    SwitchSampleKey? ShutdownSource,
    ImmutableArray<NativePeerStop> NativeStops,
    NativeLaunch? NativeLaunch,
    long StartedAt,
    long EndedAt,
    SwitchAttemptOutcome Outcome,
    ImmutableArray<SwitchStageObservation?> Stages)
{
    internal SwitchStageObservation? GetStage(SwitchStage stage) => Stages[(int)stage];
}

/// <summary>Owns the observations for one installed claim. Sealing and stage writes use the same gate.</summary>
internal sealed class SwitchAttempt
{
    private readonly TimeProvider _clock;
    private readonly SwitchAttemptId _id;
    private readonly SwitchSampleKey _target;
    private readonly long _startedAt;
    private readonly object _gate = new();
    private readonly SwitchStageObservation?[] _stages = new SwitchStageObservation?[(int)SwitchStage.Readiness + 1];
    private SwitchSampleKey? _shutdownSource;
    private ImmutableArray<NativePeerStop> _nativeStops = [];
    private NativeLaunch? _nativeLaunch;
    private bool _sealed;

    internal SwitchAttempt(TimeProvider clock, SwitchAttemptId id, SwitchSampleKey target,
                           SwitchQueueTiming? queue = null)
    {
        _clock = clock;
        _id = id;
        _target = target;
        _startedAt = clock.GetTimestamp();
        if (queue is { } wait)
            _stages[(int)SwitchStage.Queue] = new(wait.EnqueuedAt, wait.SelectedAt, SwitchStageOutcome.Completed);
    }

    internal SwitchAttemptId Id => _id;
    internal SwitchSampleKey Target => _target;
    internal long StartedAt => _startedAt;

    internal bool TryStartStage(SwitchStage stage)
    {
        lock (_gate)
        {
            if (_sealed || _stages[(int)stage] is not null) return false;
            var stamp = _clock.GetTimestamp();
            _stages[(int)stage] = new(stamp, stamp, SwitchStageOutcome.Incomplete);
            return true;
        }
    }

    internal bool TryCompleteStage(SwitchStage stage, bool succeeded)
    {
        lock (_gate) return CompleteStage(stage, succeeded);
    }

    /// <summary>The caller supplies a source only when provider evidence attributes the entire stop.</summary>
    internal bool TryCompleteShutdown(bool succeeded, SwitchSampleKey? verifiedSource,
                                      ImmutableArray<NativePeerStop> nativeStops = default)
    {
        lock (_gate)
        {
            if (!CompleteStage(SwitchStage.Shutdown, succeeded)) return false;
            if (!nativeStops.IsDefault) _nativeStops = nativeStops;
            if (succeeded) _shutdownSource = verifiedSource;
            return true;
        }
    }

    internal void ObserveNativeStop(NativePeerStop observation)
    {
        lock (_gate)
        {
            if (_sealed) return;
            // The provider reports one peer at a time, then updates it after removal.
            if (!_nativeStops.IsEmpty && _nativeStops[^1].Peer.Id == observation.Peer.Id)
                _nativeStops = _nativeStops.SetItem(_nativeStops.Length - 1, observation);
            else
                _nativeStops = _nativeStops.Add(observation);
        }
    }

    internal bool TryCompleteNativeLaunch(bool succeeded, NativeLaunch? evidence)
    {
        lock (_gate)
        {
            if (!CompleteStage(SwitchStage.Launch, succeeded)) return false;
            if (succeeded && evidence is { } launch && launch.Matches(_target)) _nativeLaunch = launch;
            return true;
        }
    }

    internal SwitchServingProof Proof(long version)
    {
        lock (_gate) return new SwitchServingProof(_id, version, _target, _nativeLaunch);
    }

    private bool CompleteStage(SwitchStage stage, bool succeeded)
    {
        if (_sealed || _stages[(int)stage] is not { Outcome: SwitchStageOutcome.Incomplete } observation)
            return false;
        _stages[(int)stage] = observation with
        {
            ObservedUntil = _clock.GetTimestamp(),
            Outcome = succeeded ? SwitchStageOutcome.Completed : SwitchStageOutcome.Failed,
        };
        return true;
    }

    internal SwitchAttemptRecord? TrySeal(SwitchAttemptOutcome outcome)
    {
        lock (_gate)
        {
            if (_sealed) return null;
            var endedAt = _clock.GetTimestamp();
            for (var i = 0; i < _stages.Length; i++)
            {
                if (_stages[i] is { Outcome: SwitchStageOutcome.Incomplete } active)
                    _stages[i] = active with { ObservedUntil = endedAt };
            }
            _sealed = true;
            return new SwitchAttemptRecord(_id, _target, _shutdownSource, _nativeStops, _nativeLaunch,
                                           _startedAt, endedAt, outcome,
                                           ImmutableArray.CreateRange(_stages));
        }
    }
}
