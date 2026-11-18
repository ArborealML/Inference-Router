using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class SwitchAttemptTests
{
    private static readonly Guid Runtime = new("34b2bf88-eddb-4252-9bc5-6ada29c60e9e");
    private static readonly SwitchSampleKey Incumbent = Key("incumbent");
    private static readonly SwitchSampleKey Target = Key("target");

    [Fact]
    public void SealPreservesZeroMissingAndIncompleteStagesAndRejectsLateWrites()
    {
        var clock = new ManualClock();
        var enqueued = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromSeconds(1));
        var attempt = new SwitchAttempt(clock, new SwitchAttemptId(Runtime, 1), Target,
                                       new SwitchQueueTiming(enqueued, clock.GetTimestamp()));
        Assert.True(attempt.TryStartStage(SwitchStage.Preflight));
        Assert.True(attempt.TryCompleteStage(SwitchStage.Preflight, succeeded: true));
        Assert.True(attempt.TryStartStage(SwitchStage.Shutdown));
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.UtcNow -= TimeSpan.FromDays(1);

        var terminal = Assert.IsType<SwitchAttemptRecord>(attempt.TrySeal(SwitchAttemptOutcome.Cancelled));

        Assert.Equal(SwitchAttemptOutcome.Cancelled, terminal.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.GetElapsedTime(terminal.StartedAt, terminal.EndedAt));
        AssertStage(terminal, SwitchStage.Queue, SwitchStageOutcome.Completed, TimeSpan.FromSeconds(1), clock);
        AssertStage(terminal, SwitchStage.Preflight, SwitchStageOutcome.Completed, TimeSpan.Zero, clock);
        AssertStage(terminal, SwitchStage.Shutdown, SwitchStageOutcome.Incomplete, TimeSpan.FromSeconds(2), clock);
        Assert.Null(terminal.GetStage(SwitchStage.Launch));
        Assert.Null(terminal.ShutdownSource);

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(attempt.TryCompleteShutdown(succeeded: true, Incumbent));
        Assert.False(attempt.TryStartStage(SwitchStage.Launch));
        Assert.Null(attempt.TrySeal(SwitchAttemptOutcome.Ready));
        AssertStage(terminal, SwitchStage.Shutdown, SwitchStageOutcome.Incomplete, TimeSpan.FromSeconds(2), clock);
    }

    [Fact]
    public void EstimateRequiresThreeDistinctSuccessesForBothPhases()
    {
        var clock = new ManualClock();
        var history = new SwitchAttemptHistory(clock, Runtime);
        var first = Measure(clock, 1, stopMs: 10, launchMs: 2);
        history.Add(first);
        history.Add(first);
        Assert.Null(history.EstimateTransition(Incumbent, Target));

        history.Add(Measure(clock, 2, stopMs: 20, launchMs: 4));
        Assert.Null(history.EstimateTransition(Incumbent, Target));

        // Readiness after the launch also contributes to the incoming activation interval.
        history.Add(Measure(clock, 3, stopMs: 30, launchMs: 3, readinessMs: 5));
        Assert.Equal(TimeSpan.FromMilliseconds(24), history.EstimateTransition(Incumbent, Target));
    }

    [Fact]
    public void UsesOnlyTheFiveNewestMatchingSuccesses()
    {
        var clock = new ManualClock();
        var history = new SwitchAttemptHistory(clock, Runtime);
        int[] activation = [1, 20, 2, 8, 4, 6];
        for (var i = 0; i < activation.Length; i++)
            history.Add(Measure(clock, i + 1, stopMs: 10, launchMs: activation[i]));

        Assert.Equal(TimeSpan.FromMilliseconds(16), history.EstimateTransition(Incumbent, Target));
    }

    [Fact]
    public void ZeroCostIsUsableThroughTheExactExpiryBoundary()
    {
        var clock = new ManualClock();
        var history = new SwitchAttemptHistory(clock, Runtime);
        for (var i = 1; i <= 3; i++)
            history.Add(Measure(clock, i));

        clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(TimeSpan.Zero, history.EstimateTransition(Incumbent, Target));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null(history.EstimateTransition(Incumbent, Target));
    }

    [Fact]
    public void FrozenComparisonTimeExcludesAFutureObservation()
    {
        var clock = new ManualClock();
        var history = new SwitchAttemptHistory(clock, Runtime);
        history.Add(Measure(clock, 1));
        history.Add(Measure(clock, 2));
        var comparisonTime = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromTicks(1));
        history.Add(Measure(clock, 3));

        Assert.Null(history.EstimateTransition(Incumbent, Target, comparisonTime));
        Assert.Equal(TimeSpan.Zero, history.EstimateTransition(Incumbent, Target));
    }

    [Fact]
    public void FailedCensoredAndUnattributedAttemptsDoNotSupplyMissingEvidence()
    {
        var clock = new ManualClock();
        var history = new SwitchAttemptHistory(clock, Runtime);
        history.Add(Measure(clock, 1));
        history.Add(Measure(clock, 2));
        var failed = Measure(clock, 3, outcome: SwitchAttemptOutcome.Failed, readiness: SwitchStageOutcome.Failed);
        var censored = Measure(clock, 4, readiness: SwitchStageOutcome.Incomplete);
        var missing = Measure(clock, 5, verifiedStop: false, readiness: null);
        history.Add(failed);
        history.Add(censored);
        history.Add(missing);

        Assert.Null(history.EstimateTransition(Incumbent, Target));
        Assert.Equal(5, history.Snapshot().Count);
        AssertStage(failed, SwitchStage.Readiness, SwitchStageOutcome.Failed, TimeSpan.Zero, clock);
        AssertStage(censored, SwitchStage.Readiness, SwitchStageOutcome.Incomplete, TimeSpan.Zero, clock);
        Assert.Null(missing.GetStage(SwitchStage.Readiness));
        Assert.Null(missing.ShutdownSource);

        var noStopHistory = new SwitchAttemptHistory(clock, Runtime);
        for (var i = 1; i <= 3; i++)
            noStopHistory.Add(Measure(clock, i, verifiedStop: false));
        Assert.Null(noStopHistory.EstimateTransition(Incumbent, Target));
    }

    [Fact]
    public void EstimateRequiresMatchingComparableProvenance()
    {
        var clock = new ManualClock();
        var history = new SwitchAttemptHistory(clock, Runtime);
        for (var i = 1; i <= 3; i++)
            history.Add(Measure(clock, i, stopMs: 1, launchMs: 2));

        Assert.Equal(TimeSpan.FromMilliseconds(3), history.EstimateTransition(Incumbent,
                     Target with { CanonicalModel = "TARGET" }));
        Assert.Null(history.EstimateTransition(Incumbent, Target with { RuntimeEpoch = Guid.Empty }));
        Assert.Null(history.EstimateTransition(Incumbent, Target with { EffectiveProfile = "another" }));
        Assert.Null(history.EstimateTransition(Incumbent, Target with { ConfigGeneration = 2 }));
        Assert.Null(history.EstimateTransition(Incumbent, Target with { CanonicalModel = "different" }));
        Assert.Null(history.EstimateTransition(Incumbent, Target with { ProviderIdentity = null }));
        Assert.Null(history.EstimateTransition(Incumbent, Target with { ObservationClass = SwitchObservationClass.WarmInPlace }));
        Assert.Null(history.EstimateTransition(Incumbent, Target with { ObservationClass = SwitchObservationClass.Unknown }));
        Assert.Null(history.EstimateTransition(Incumbent, Target with { ProviderKind = DelegatedExecProvider.ProviderKind }));
        Assert.Null(history.EstimateTransition(Incumbent with { ConfigGeneration = 2 }, Target));
    }

    [Fact]
    public void AReloadAfterEvictionUsesMatchingFreshSamplesOrHasNoEstimate()
    {
        var clock = new ManualClock();
        var history = new SwitchAttemptHistory(clock, Runtime);
        var warmTarget = Target with { ObservationClass = SwitchObservationClass.WarmInPlace };
        for (var i = 1; i <= 3; i++)
            history.Add(Measure(clock, i, stopMs: 1, launchMs: 2, target: warmTarget));
        Assert.Equal(TimeSpan.FromMilliseconds(3), history.EstimateTransition(Incumbent, warmTarget));

        // The next native launch after eviction calls for a fresh-container observation.
        Assert.Null(history.EstimateTransition(Incumbent, Target));
        for (var i = 4; i <= 6; i++)
            history.Add(Measure(clock, i, stopMs: 1, launchMs: 10));
        Assert.Equal(TimeSpan.FromMilliseconds(11), history.EstimateTransition(Incumbent, Target));
    }

    [Fact]
    public void TheSixtyFifthTerminalRecordEvictsTheOldestEvenWhenNewRecordsFailed()
    {
        var clock = new ManualClock();
        var history = new SwitchAttemptHistory(clock, Runtime);
        for (var i = 1; i <= 3; i++)
            history.Add(Measure(clock, i));
        for (var i = 4; i <= 64; i++)
            history.Add(Measure(clock, i, outcome: SwitchAttemptOutcome.Cancelled));

        var oldest = new SwitchAttemptId(Runtime, 1);
        Assert.True(history.TryAttachFirstForward(oldest, clock.GetTimestamp()));
        Assert.Equal(64, history.Snapshot().Count);
        Assert.Equal(TimeSpan.Zero, history.EstimateTransition(Incumbent, Target));
        history.Add(Measure(clock, 65, outcome: SwitchAttemptOutcome.Failed));

        var retained = history.Snapshot();
        Assert.Equal(64, retained.Count);
        Assert.Equal(2L, retained[0].Terminal.Id.Sequence);
        Assert.Equal(65L, retained[^1].Terminal.Id.Sequence);
        Assert.False(history.TryAttachFirstForward(oldest, clock.GetTimestamp()));
        Assert.Null(history.EstimateTransition(Incumbent, Target));
    }

    [Fact]
    public void FirstForwardAttachesOnceAndLeavesTerminalRecordAndEstimateIntact()
    {
        var clock = new ManualClock();
        var history = new SwitchAttemptHistory(clock, Runtime);
        history.Add(Measure(clock, 1, stopMs: 10));
        history.Add(Measure(clock, 2, stopMs: 10));
        var ready = Measure(clock, 3, stopMs: 10);
        history.Add(ready);
        var failed = Measure(clock, 4, outcome: SwitchAttemptOutcome.Failed);
        history.Add(failed);
        var originalEstimate = history.EstimateTransition(Incumbent, Target);

        Assert.False(history.TryAttachFirstForward(ready.Id, ready.EndedAt - 1));
        Assert.False(history.TryAttachFirstForward(failed.Id, clock.GetTimestamp()));
        var first = clock.GetTimestamp();
        Assert.True(history.TryAttachFirstForward(ready.Id, first));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(history.TryAttachFirstForward(ready.Id, clock.GetTimestamp()));

        var observation = Assert.Single(history.Snapshot(), attempt => attempt.Terminal.Id == ready.Id);
        Assert.Equal(first, observation.FirstForwardedAt);
        Assert.Same(ready, observation.Terminal);
        Assert.Equal(originalEstimate, history.EstimateTransition(Incumbent, Target));
    }

    private static SwitchSampleKey Key(string model) =>
        new(Runtime, DockerLifecycleProvider.ProviderKind, model, "default", 1,
            SwitchObservationClass.FreshContainer, "observed-image");

    private static SwitchAttemptRecord Measure(ManualClock clock, long sequence, int stopMs = 0,
        int launchMs = 0, int readinessMs = 0, SwitchAttemptOutcome outcome = SwitchAttemptOutcome.Ready,
        bool verifiedStop = true, SwitchStageOutcome? readiness = SwitchStageOutcome.Completed,
        SwitchSampleKey? target = null)
    {
        var attempt = new SwitchAttempt(clock, new SwitchAttemptId(Runtime, sequence), target ?? Target);
        Assert.True(attempt.TryStartStage(SwitchStage.Shutdown));
        clock.Advance(TimeSpan.FromMilliseconds(stopMs));
        Assert.True(attempt.TryCompleteShutdown(succeeded: true, verifiedStop ? Incumbent : null));
        Assert.True(attempt.TryStartStage(SwitchStage.Launch));
        clock.Advance(TimeSpan.FromMilliseconds(launchMs));
        Assert.True(attempt.TryCompleteStage(SwitchStage.Launch, succeeded: true));
        if (readiness is { } observed)
        {
            Assert.True(attempt.TryStartStage(SwitchStage.Readiness));
            clock.Advance(TimeSpan.FromMilliseconds(readinessMs));
            if (observed != SwitchStageOutcome.Incomplete)
                Assert.True(attempt.TryCompleteStage(SwitchStage.Readiness, observed == SwitchStageOutcome.Completed));
        }
        return Assert.IsType<SwitchAttemptRecord>(attempt.TrySeal(outcome));
    }

    private static void AssertStage(SwitchAttemptRecord record, SwitchStage stage, SwitchStageOutcome outcome,
                                    TimeSpan elapsed, ManualClock clock)
    {
        var observation = Assert.IsType<SwitchStageObservation>(record.GetStage(stage));
        Assert.Equal(outcome, observation.Outcome);
        Assert.Equal(elapsed, clock.GetElapsedTime(observation.StartedAt, observation.ObservedUntil));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;

        internal void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            UtcNow += elapsed;
        }
    }
}
