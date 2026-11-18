using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal readonly record struct RetainedSwitchAttempt(SwitchAttemptRecord Terminal, long? FirstForwardedAt);

/// <summary>Process-local diagnostic history and successful native transition estimates for one runtime.</summary>
internal sealed class SwitchAttemptHistory(TimeProvider clock, Guid runtimeEpoch)
{
    private const int AttemptLimit = 64;
    private const int SampleLimit = 5;
    private const int MinimumSamples = 3;
    private static readonly TimeSpan MaximumSampleAge = TimeSpan.FromHours(24);
    private readonly object _gate = new();
    private readonly List<RetainedSwitchAttempt> _records = [];

    internal void Add(SwitchAttemptRecord attempt)
    {
        lock (_gate)
        {
            if (attempt.Id.RuntimeEpoch != runtimeEpoch ||
                _records.Exists(record => record.Terminal.Id == attempt.Id))
                return;
            if (_records.Count == AttemptLimit) _records.RemoveAt(0);
            _records.Add(new RetainedSwitchAttempt(attempt, null));
        }
    }

    internal IReadOnlyList<RetainedSwitchAttempt> Snapshot()
    {
        lock (_gate) return _records.ToArray();
    }

    /// <summary>The caller validates the serving proof before attaching the upstream invocation.</summary>
    internal bool TryAttachFirstForward(SwitchAttemptId attempt, long forwardedAt)
    {
        lock (_gate)
        {
            for (var i = _records.Count - 1; i >= 0; i--)
            {
                var record = _records[i];
                if (record.Terminal.Id != attempt) continue;
                if (record.FirstForwardedAt is not null || record.Terminal.Outcome != SwitchAttemptOutcome.Ready ||
                    forwardedAt < record.Terminal.EndedAt)
                    return false;
                _records[i] = record with { FirstForwardedAt = forwardedAt };
                return true;
            }
            return false;
        }
    }

    internal TimeSpan? EstimateTransition(SwitchSampleKey incumbent, SwitchSampleKey target, long? observedAt = null)
    {
        lock (_gate)
        {
            var now = observedAt ?? clock.GetTimestamp();
            var shutdown = EstimatePhase(incumbent, SwitchStage.Shutdown, now);
            var activation = EstimatePhase(target, SwitchStage.Launch, now);
            if (shutdown is not { } stop || activation is not { } load ||
                stop.Ticks > TimeSpan.MaxValue.Ticks - load.Ticks)
                return null;
            return stop + load;
        }
    }

    private TimeSpan? EstimatePhase(SwitchSampleKey key, SwitchStage phase, long now)
    {
        if (key.RuntimeEpoch != runtimeEpoch || key.ObservationClass == SwitchObservationClass.Unknown ||
            !string.Equals(key.ProviderKind, DockerLifecycleProvider.ProviderKind, StringComparison.Ordinal))
            return null;

        Span<long> samples = stackalloc long[SampleLimit];
        var count = 0;
        for (var i = _records.Count - 1; i >= 0 && count < samples.Length; i--)
        {
            var record = _records[i].Terminal;
            if (record.Outcome != SwitchAttemptOutcome.Ready) continue;
            var age = clock.GetElapsedTime(record.EndedAt, now);
            if (age < TimeSpan.Zero || age > MaximumSampleAge) continue;
            if (ObservedDuration(record, key, phase) is { } duration && duration >= TimeSpan.Zero)
                samples[count++] = duration.Ticks;
        }
        if (count < MinimumSamples) return null;

        samples[..count].Sort();
        var middle = count / 2;
        var median = samples[middle];
        if (count % 2 == 0)
        {
            var lower = samples[middle - 1];
            median = lower + (median - lower) / 2;
        }
        return TimeSpan.FromTicks(median);
    }

    private TimeSpan? ObservedDuration(SwitchAttemptRecord record, SwitchSampleKey key, SwitchStage phase)
    {
        if (record.GetStage(phase) is not { Outcome: SwitchStageOutcome.Completed } stage) return null;

        if (phase == SwitchStage.Shutdown)
            return record.ShutdownSource is { } source && source.Matches(key)
                ? clock.GetElapsedTime(stage.StartedAt, stage.ObservedUntil)
                : null;

        return record.Target.Matches(key) &&
            record.GetStage(SwitchStage.Readiness) is { Outcome: SwitchStageOutcome.Completed } readiness &&
            readiness.StartedAt >= stage.ObservedUntil
            ? clock.GetElapsedTime(stage.StartedAt, readiness.ObservedUntil)
            : null;
    }
}
