using System.Collections.Immutable;

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

/// <summary>An absent stage has no value. An incomplete stage was still open when the attempt ended.</summary>
internal readonly record struct SwitchStageObservation(
    long StartedAt, long ObservedUntil, SwitchStageOutcome Outcome);

internal sealed record SwitchAttemptRecord(
    SwitchAttemptId Id,
    SwitchSampleKey Target,
    SwitchSampleKey? ShutdownSource,
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
    internal bool TryCompleteShutdown(bool succeeded, SwitchSampleKey? verifiedSource)
    {
        lock (_gate)
        {
            if (!CompleteStage(SwitchStage.Shutdown, succeeded)) return false;
            if (succeeded) _shutdownSource = verifiedSource;
            return true;
        }
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
            return new SwitchAttemptRecord(_id, _target, _shutdownSource, _startedAt, endedAt, outcome,
                                           ImmutableArray.CreateRange(_stages));
        }
    }
}
