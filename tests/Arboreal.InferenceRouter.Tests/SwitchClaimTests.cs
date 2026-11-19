using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>The edges of *claiming* a switch: who is told the switch ended, in what order, and what happens
/// when the machinery around the claim fails. A switch transaction is engine-owned and can outlive its
/// caller, so every one of these edges has to settle the readiness task and clear the marker — a stranded
/// waiter sits out its ceiling on a switch that no longer exists, and a marker left up tells the scheduler
/// this engine is busy forever (§5.5).</summary>
public sealed class SwitchClaimTests
{
    /// <summary>A lifecycle provider whose launches the test holds open, on the <c>wait_health = false</c>
    /// path, and which *observes cancellation*: a shutdown has to reach an in-flight load, and a provider
    /// that ignores the token would turn that into a hang rather than an outcome.</summary>
    private sealed class GatedProvider : IEngineLifecycleProvider
    {
        private readonly Queue<TaskCompletionSource> _gates = new();
        public List<string> Launches { get; } = [];
        public bool GateLaunches { get; set; }
        /// <summary>When set, the launch fails on its own with this reason — no shutdown involved — so the
        /// control can tell a broken engine from a stopping one.</summary>
        public string? FailLaunchWith { get; set; }
        public string Kind => "docker";

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok("fake"));

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(
            LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok());

        public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            Launches.Add(target.Model);
            if (FailLaunchWith is { } why)
            {
                return LifecycleResult.Fail(why, "the exec said no");
            }
            if (GateLaunches)
            {
                TaskCompletionSource gate;
                lock (_gates)
                {
                    gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates.Enqueue(gate);
                }
                try { await gate.Task.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return LifecycleResult.Fail("cancelled_during_launch", "gate"); }
            }
            return LifecycleResult.Ok();
        }

        public void ReleaseOne()
        {
            TaskCompletionSource? gate;
            lock (_gates) { gate = _gates.Count > 0 ? _gates.Dequeue() : null; }
            gate?.TrySetResult();
        }

        /// <summary>Launches gated and not yet released. A test must wait on this rather than on
        /// <see cref="Launches"/> before releasing: the launch is recorded a statement before its gate is
        /// queued, so releasing on the record can dequeue nothing and hang the switch.</summary>
        public int GateCount
        {
            get { lock (_gates) return _gates.Count; }
        }

        // The trust path: the exec's own success starts the switch, but since decision-27 the engine still has
        // to say what it holds, so a pin that needs a switch to succeed supplies the answer below.
        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.FromSeconds(1));

    }

    /// <summary>An engine that answers <c>/models</c> with the names it is given. A switch the engine cannot
    /// confirm is now refused rather than served, so the claim-path edges pinned here — the launch boundary,
    /// the marker, the nudge — need an engine that answers; the refusal is not what they are about.</summary>
    private static FakeEngineHandler Reports(params string[] models) => new() { LoadedModels = models };

    private static EngineRuntime Engine(
        GatedProvider provider, EventRing? feed = null, Action? dispatchHint = null,
        FakeEngineHandler? models = null)
    {
        var def = new EngineDefinition
        {
            Name = "fake",
            Kind = EngineKind.OpenAiCompatible,
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "test" },
            Catalog = new Dictionary<string, EngineModelDefinition>
            {
                ["m"] = new() { Name = "m" },
                ["extra"] = new() { Name = "extra" },
                // A third selectable model, so a test can put one caller behind another's switch and still ask
                // for a target of its own.
                ["second"] = new() { Name = "second" },
            },
        };
        var config = new RouterConfig { Engines = new Dictionary<string, EngineDefinition> { ["fake"] = def } };
        return new EngineRuntime(def, config, feed ?? new EventRing(), models, lifecycle: provider, dispatchHint: dispatchHint);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false, 8)]
    [InlineData(true, 1)]
    public async Task LostClaimReleasesBothLocksAndStopsAtTheCountOrOriginalDeadline(bool deadlineWins, int expectedLosses)
    {
        var provider = new GatedProvider();
        using var reported = Reports("m");
        var feed = new EventRing();
        await using var engine = Engine(provider, feed, models: reported);
        var offers = 0;
        if (deadlineWins) engine.SwitchWaitTimeForTests = deadline => deadline;

        void SupersedeOffer()
        {
            var offer = ++offers;
            Assert.InRange(offer, 1, 8);
            if (offer == 2)
            {
                // This other caller must acquire both locks while the retry is outside them.
                engine.BeforeSwitchWaitForTests = null;
                try
                {
                    var other = Task.Run(() => engine.WaitForModelAsync("undeclared", TimeSpan.FromSeconds(5),
                        CancellationToken.None));
                    var refusal = other.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    Assert.Equal(SwitchReason.NoSelectCommand, refusal.Detail);
                }
                finally
                {
                    engine.BeforeSwitchWaitForTests = SupersedeOffer;
                }
            }

            using var stop = new CancellationTokenSource();
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe(RouterEvent entry)
            {
                if (entry.Kind == "engine.health") observed.TrySetResult();
            }

            var before = engine.Capability;
            reported.NCtx = 128 + offer;
            feed.Subscribe(Observe);
            try
            {
                engine.StartHealthLoop(stop.Token);
                observed.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            finally
            {
                feed.Unsubscribe(Observe);
                stop.Cancel();
            }
            Assert.NotSame(before, engine.Capability);
            Assert.Null(engine.SwitchingToward);
        }

        engine.BeforeSwitchWaitForTests = SupersedeOffer;
        var result = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.Equal(expectedLosses, offers);
        Assert.Equal(deadlineWins ? EngineRuntime.SwitchOutcome.TimedOut : EngineRuntime.SwitchOutcome.Failed,
            result.Outcome);
        Assert.Equal(deadlineWins ? null : SwitchReason.SwitchError, result.Detail);
        Assert.Empty(provider.Launches);
        Assert.Empty(engine.SwitchHistory);
        Assert.Null(engine.SwitchingToward);
        Assert.DoesNotContain(feed.Replay(0), entry => entry.Kind == "engine.switch_started");
        Assert.Equal(deadlineWins ? 0 : 1, feed.Replay(0).Count(entry =>
            entry.Kind == "engine.switch_failed" && entry.Model == "extra"));
    }

    [Fact(Timeout = 20_000)]
    public async Task FirstAcquisitionExpiresWhileAnotherClaimStillHoldsBothLocks()
    {
        var provider = new GatedProvider();
        using var reported = Reports("extra");
        var feed = new EventRing();
        await using var engine = Engine(provider, feed, models: reported);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var limit = TimeSpan.FromSeconds(5);
        engine.AfterClaimForTests = () =>
        {
            claimed.TrySetResult();
            release.Task.WaitAsync(limit).GetAwaiter().GetResult();
        };

        var first = Task.Run(() => engine.WaitForModelAsync("extra", Timeout.InfiniteTimeSpan, CancellationToken.None));
        try
        {
            await claimed.Task.WaitAsync(limit);
            var offers = 0;
            engine.BeforeSwitchWaitForTests = () => Interlocked.Increment(ref offers);
            var second = await engine.WaitForModelAsync("second", TimeSpan.FromMilliseconds(100), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(EngineRuntime.SwitchOutcome.TimedOut, second.Outcome);
            Assert.Null(second.Detail);
            Assert.Equal(1, Volatile.Read(ref offers));
            Assert.False(first.IsCompleted);
            Assert.Equal("extra", engine.SwitchingToward);
            Assert.Empty(provider.Launches);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.True((await first.WaitAsync(limit)).IsReady);
        Assert.Equal("extra", Assert.Single(provider.Launches));
        Assert.DoesNotContain(feed.Replay(0), entry => entry.Model == "second" && entry.Kind == "engine.switch_started");
    }

    [Fact(Timeout = 20_000)]
    public async Task ExpiredIdleOfferKeepsImmediateReadyButStartsNoSwitch()
    {
        var provider = new GatedProvider();
        using var reported = Reports("extra");
        await using var engine = Engine(provider, models: reported);

        var idle = engine.Capability;
        var expired = await engine.WaitForModelAsync("extra", TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(EngineRuntime.SwitchOutcome.TimedOut, expired.Outcome);
        Assert.Same(idle, engine.Capability);
        Assert.Empty(provider.Launches);

        var loaded = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(loaded.IsReady);
        var current = engine.Capability;

        var immediate = await engine.WaitForModelAsync("extra", TimeSpan.Zero, CancellationToken.None);
        Assert.True(immediate.IsReady);
        Assert.Same(current, engine.Capability);
        Assert.Equal("extra", Assert.Single(provider.Launches));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(5);
        if (!condition()) throw new TimeoutException("condition never became true");
    }

    /// <summary>The nudge to dispatch runs after the switch has committed and settled, and its failure is
    /// swallowed. Before, the callback ran first: a throw escaped into the switch's own catch and rewrote a
    /// switch that had already succeeded — failed event, health withdrawn, waiter told Failed — and left the
    /// readiness task un-settled until that catch ran.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AThrowingDispatchNudgeNeitherRewritesTheSwitchNorStrandsTheWaiter()
    {
        var provider = new GatedProvider();
        var feed = new EventRing();
        using var reported = Reports("m", "extra");
        var engine = Engine(provider, feed,
            dispatchHint: () => throw new InvalidOperationException("dispatcher blew up"), models: reported);
        await using var _ = engine;

        var switched = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(switched.IsReady);                        // the switch stands on its own proof
        Assert.Null(engine.SwitchingToward);
        var kinds = feed.Replay(0).Select(e => e.Kind).ToArray();
        Assert.Contains("engine.switch_completed", kinds);
        Assert.DoesNotContain("engine.switch_failed", kinds); // the nudge's failure is not the switch's
    }

    /// <summary>The reordering must not cost the normal path anything: an unsuperseded switch still ends Ready,
    /// still nudges dispatch exactly once, and still does it with the transition gate released — otherwise the
    /// nudge that exists to avoid a poll-interval delay has become a reason to wait on the gate instead.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AnUnsupersededSwitchStillNudgesDispatchOnceOutsideTheGate()
    {
        var provider = new GatedProvider();
        var nudges = 0;
        var insideGate = new List<bool>();
        using var reported = Reports("m", "extra");
        var engine = Engine(provider, dispatchHint: () => nudges++, models: reported);
        await using var _ = engine;
        engine.AfterEndCommitForTests = gate => insideGate.Add(gate);

        var switched = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.True(switched.IsReady);
        Assert.Equal(1, nudges);                    // one commit, one nudge: none lost, none doubled
        Assert.Equal([false], insideGate);
    }

    /// <summary>A synchronous failure at the scheduling boundary (the seam pins this shape; a real
    /// thread-pool rejection looks the same from here) has to undo the claim: settle the waiters, clear the
    /// marker, and leave the engine able to be claimed again rather than permanently "switching".</summary>
    [Fact(Timeout = 30_000)]
    public async Task ASchedulingFailureAtTheLaunchBoundaryUndoesItsClaim()
    {
        var provider = new GatedProvider();
        using var reported = Reports("m", "extra");
        var engine = Engine(provider, models: reported);
        await using var _ = engine;
        engine.BeforeScheduleSwitchForTests = () => throw new InvalidOperationException("the pool said no");

        var refused = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(refused.IsReady);
        Assert.Equal(SwitchReason.SwitchError, refused.Detail);   // the catch-all name, never the switch's target
        Assert.Null(engine.SwitchingToward);
        Assert.Empty(provider.Launches);                      // never launched, so nothing to stop

        engine.BeforeScheduleSwitchForTests = null;
        var retry = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(retry.IsReady);                           // the engine is claimable again, not stuck
        Assert.Single(provider.Launches);
    }

    /// <summary>Shutdown during a claim: the waiter parked short of the switch lock has to be released by
    /// cancellation, not by a disposed semaphore. <c>ObjectDisposedException</c> reaching the request path is
    /// reported as <c>engine_unreachable</c> — a router shutting down described as a broken engine — and the
    /// switch in flight still has to settle rather than hang to the caller's ceiling.</summary>
    [Fact(Timeout = 30_000)]
    public async Task ShutdownDuringAClaimEndsCancelledNotEngineUnreachable()
    {
        var provider = new GatedProvider { GateLaunches = true };
        var feed = new EventRing();
        var engine = Engine(provider, feed);

        var inFlight = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);   // the launch is parked on its gate, not merely recorded

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.BeforeSwitchWaitForTests = () =>
        {
            entered.TrySetResult();
            go.Task.GetAwaiter().GetResult();
        };
        var newcomer = Task.Run(() => engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None));
        await entered.Task;                                   // parked at the last point before the lock

        await engine.DisposeAsync();                          // cancels the switch token; the lock stays alive
        go.TrySetResult();

        Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, (await newcomer).Outcome);

        // Shutdown releases the in-flight caller with the same cause. The transaction still clears its marker.
        Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, (await inFlight).Outcome);
        await WaitUntil(() => engine.SwitchingToward is null);
        Assert.Null(engine.SwitchingToward);

        // The load itself has to have felt the stop: the provider reports the cancellation it observed, so the
        // two answers above are responses to a real shutdown and not to a switch that never reached the exec.
        Assert.Contains("engine.switch_failed", await WaitUntilFeed(feed, "cancelled_during_launch"));
    }

    /// <summary>Shutdown releases the readiness waiter with the recorded stop cause, regardless of which
    /// wake-up wins. The transaction still ends and clears its marker.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AWaiterInsideTheReadinessWaitIsToldTheRouterStopped()
    {
        var provider = new GatedProvider { GateLaunches = true };
        var feed = new EventRing();
        var engine = Engine(provider, feed);

        var waiting = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);      // the load is in flight, the caller is parked

        await engine.DisposeAsync();

        var stopped = await waiting;
        Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, stopped.Outcome);
        Assert.Null(stopped.Detail);                         // the response names no target
        Assert.Contains("engine.switch_failed", await WaitUntilFeed(feed, "cancelled_during_launch"));
        await WaitUntil(() => engine.SwitchingToward is null);
        Assert.Null(engine.SwitchingToward);
    }

    /// <summary>Waits for a feed event whose reason carries <c>marker</c>, and returns every kind on the feed when
    /// it appears. A switch's own event is written on the switch's thread, so it lands after the waiters release.</summary>
    private static async Task<string[]> WaitUntilFeed(EventRing feed, string marker)
    {
        for (var i = 0; i < 400; i++)
        {
            var kinds = feed.Replay(0).Select(e => e.Kind).ToArray();
            if (feed.Replay(0).Any(e => e.Kind == "engine.switch_failed" &&
                                        e.Reason is { } r && r.Contains(marker, StringComparison.Ordinal)))
                return kinds;
            await Task.Delay(5);
        }
        throw new TimeoutException(
            $"no engine.switch_failed carrying {marker} reached the feed; feed="
            + string.Join("; ", feed.Replay(0).Select(e => $"{e.Kind}:{e.Reason}")));
    }

    /// <summary>The shutdown rule must not swallow a real failure. A launch that fails on its own, with the
    /// engine's switch token untouched, is still told to its caller as a failed switch: the point of reporting
    /// cancellation on shutdown is to stop calling a stopping router a broken engine, not to stop calling a
    /// broken engine broken.</summary>
    [Fact(Timeout = 30_000)]
    public async Task ALaunchThatFailsOnItsOwnIsStillToldAsAFailedSwitch()
    {
        var provider = new GatedProvider { FailLaunchWith = "exec_exit_1" };
        var feed = new EventRing();
        var engine = Engine(provider, feed);
        await using var _ = engine;

        var refused = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.False(refused.IsReady);
        Assert.Equal(EngineRuntime.SwitchOutcome.Failed, refused.Outcome);   // not cancelled, not timed out
        Assert.Equal(SwitchReason.LaunchFailed, refused.Detail);   // the category, and nothing wrapped around it
        Assert.Null(engine.SwitchingToward);
        var reasons = feed.Replay(0).Where(e => e.Kind == "engine.switch_failed").Select(e => e.Reason).ToArray();
        Assert.Contains("exec_exit_1 — the exec said no", reasons);          // the operator still reads why
    }

    /// <summary>A claim hook that throws has its claim undone — but undone after leaving the transition gate,
    /// because undoing it means settling waiters and nudging dispatch, and the gate is reentrant, so doing it
    /// inside would run both while every later claim queues behind the gate. The seam reports the gate state so
    /// the ordering is pinned rather than asserted about.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AClaimHookThatThrowsRollsBackOutsideTheTransitionGate()
    {
        var provider = new GatedProvider();
        using var reported = Reports("m", "extra");
        var engine = Engine(provider, models: reported);
        await using var _ = engine;

        var insideGate = new List<bool>();
        engine.AfterEndCommitForTests = gate => insideGate.Add(gate);
        engine.AfterClaimForTests = () => throw new InvalidOperationException("hook failed after claiming");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None));

        Assert.Null(engine.SwitchingToward);                  // the claim was undone, not left up
        Assert.Empty(provider.Launches);
        Assert.Equal([false], insideGate);                    // rollback ran with the gate released

        engine.AfterClaimForTests = null;
        var retry = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(retry.IsReady);
        Assert.Single(provider.Launches);
    }

    /// <summary>§5.5, TASK-61 AC #3 and #4: the waiter takes its own turn — not a refusal, not an inherited
    /// answer — when something **other than a later claim** contradicts the target its own switch proved. Here the
    /// contradiction is the engine's own report: the successor's switch runs to completion and publishes a proof
    /// that names only <c>m</c>, so <c>extra</c> is gone from the reported list. The seam holds the waiter between
    /// the readiness await and the resume snapshot read across all of that, so the ordering is a fact the test
    /// makes true rather than one it hopes the scheduler produces. Before this rule the waiter was told "switch to
    /// extra failed" for a switch that had just succeeded, and its request died over a model it had itself loaded.
    /// The successor's launch is gated and released by the test, so neither caller's answer turns on timing.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AWaiterOvertakenAtTheResumeReadTakesItsOwnTurnInsteadOfAnAnswerItCannotSupport()
    {
        var provider = new GatedProvider { GateLaunches = true };
        using var reported = new FakeEngineHandler { LoadedModels = ["extra"] };
        var feed = new EventRing();
        var engine = Engine(provider, feed, models: reported);
        await using var _ = engine;

        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AfterReadyWaitForTests = _ =>
        {
            parked.TrySetResult();
            resume.Task.GetAwaiter().GetResult();     // held after the await, before the snapshot read
        };

        var first = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(25), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);
        provider.ReleaseOne();                        // A's own switch proves "extra" and settles true
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10));   // A is held at the resume read

        // A successor claims the engine for another target while A is deciding, and *finishes*: its proof is the
        // engine's answer about what it now serves. A marker alone would not contradict anything — that is the
        // next test — so the successor's launch is gated and released here, and its proof is waited for.
        var second = engine.WaitForModelAsync("m", TimeSpan.FromSeconds(25), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1 && provider.Launches.Count == 2);
        reported.LoadedModels = ["m"];                // one model at a time: the load that landed displaced it
        provider.ReleaseOne();
        await WaitUntil(() => engine.SwitchingToward is null && engine.Healthy &&
                              engine.LoadedModels.Contains("m") && !engine.LoadedModels.Contains("extra"));

        resume.TrySetResult();                        // A resumes and reads a snapshot that says no

        // Not answered Ready with a model the engine no longer reports, and not refused either: it queues behind
        // a new attempt of its own, which is the loop's whole point. One own turn, then an answer.
        await WaitUntil(() => provider.GateCount == 1 && provider.Launches.Count == 3);
        Assert.Equal("extra", provider.Launches[^1]);  // A's own turn, not a refusal
        Assert.True((await second).IsReady);           // the successor answers off its own switch
        reported.LoadedModels = ["extra"];
        provider.ReleaseOne();

        var answer = await first;
        Assert.True(answer.IsReady,
            $"answer={answer.Outcome}/{answer.Detail}; launches=[{string.Join(",", provider.Launches)}] " +
            $"gates={provider.GateCount}; feed=[{string.Join(",", feed.Replay(0).Select(e => e.Kind + ":" + e.Model))}]");
        Assert.DoesNotContain("engine.switch_failed", feed.Replay(0).Select(e => e.Kind));
        Assert.Equal(EngineRuntime.SwitchOutcome.Ready, answer.Outcome);
        Assert.Equal(["extra", "m", "extra"], provider.Launches);
    }

    /// <summary>§5.5, TASK-61 AC #5 (decision-36): the marker a **later** claim raises is somebody else's
    /// transaction starting, not an observation about what this engine serves, so it does not withdraw the proof
    /// this waiter's own switch published. The two publications are one snapshot — health down, marker up — and the
    /// reported list the proof carried is untouched by them, so a waiter that holds its proof is answered from it.
    /// This is the case that made v0.12.34 red: the waiter fell through, claimed again, and drove the other caller
    /// into the same fall-through, so two callers wanting different models on a one-model engine alternated
    /// lifecycle transactions until one hit its ceiling. The successor's launch stays on its gate until after the
    /// answer, so the answer cannot be timing.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AWaiterHoldingItsOwnProofIsNotQueuedBehindAnotherCallersClaim()
    {
        var provider = new GatedProvider { GateLaunches = true };
        using var reported = new FakeEngineHandler { LoadedModels = ["extra"] };
        var feed = new EventRing();
        var engine = Engine(provider, feed, models: reported);
        await using var _ = engine;

        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AfterReadyWaitForTests = _ =>
        {
            parked.TrySetResult();
            resume.Task.GetAwaiter().GetResult();
        };

        var first = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(25), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);
        provider.ReleaseOne();                        // A's switch proves "extra": health and list in one publication
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(engine.Healthy);                  // the proof is out there before the successor arrives

        // The successor claims the engine for "m" while A is deciding. Its claim takes health down and raises the
        // marker; it publishes no new list, so the engine's last report still names what A loaded.
        var second = engine.WaitForModelAsync("m", TimeSpan.FromSeconds(25), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1 && provider.Launches.Count == 2);
        Assert.False(engine.Healthy);
        Assert.Equal("m", engine.SwitchingToward);
        Assert.Contains("extra", engine.LoadedModels);

        resume.TrySetResult();                        // A resumes into exactly that snapshot

        // Ready off its own proof, and — the half the launch sequence proves — no third switch of A's own to buy
        // that answer back. What it is *not* is a guarantee the model survives the successor's switch: holding
        // authority across the handoff is TASK-25 and TASK-72, and the window is stated in SPEC §15.
        var answer = await first;
        Assert.Equal(EngineRuntime.SwitchOutcome.Ready, answer.Outcome);
        Assert.DoesNotContain("engine.switch_failed", feed.Replay(0).Select(e => e.Kind));
        Assert.Equal(["extra", "m"], provider.Launches);

        // The successor's transaction is completed, not rolled back (decision-34): it finishes on its own terms.
        reported.LoadedModels = ["m"];
        provider.ReleaseOne();
        Assert.True((await second).IsReady);
        Assert.Equal(["extra", "m"], provider.Launches);
    }

    /// <summary>§5.5, TASK-61 AC #7: the proof that survives a later claim does <b>not</b> survive a probe that
    /// could not ask what the engine serves. Both publish the same <c>Healthy = false</c>, and only one of them is
    /// an observation — the claim is a transaction starting, the failed probe is the router learning that it does
    /// not know. This is the pair of the test above: a marker alone leaves the waiter holding its proof (two
    /// launches), a failed probe behind the marker takes the proof away and the waiter pays for its own switch
    /// again. The order is made, not raced: the failing status is armed only after the proof is published, with no
    /// switch in flight, so the probe is the only possible author of the health the waiter then loses; the health
    /// loop starts after that proof, so the engine still begins unknown and the waiter's own switch is needed.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AFailedProbeBetweenProofAndClaimWithdrawsTheWaitersOwnProof()
    {
        var provider = new GatedProvider { GateLaunches = true };
        using var reported = new FakeEngineHandler { LoadedModels = ["extra"] };
        var feed = new EventRing();
        var engine = Engine(provider, feed, models: reported);
        await using var _ = engine;

        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AfterReadyWaitForTests = _ =>
        {
            parked.TrySetResult();
            resume.Task.GetAwaiter().GetResult();
        };

        var first = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(25), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);
        provider.ReleaseOne();                        // A's own switch proves "extra": health and list at once
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(engine.Healthy);

        // The engine stops answering /models, and the probe loop — started only now, so it cannot have moved
        // health before the proof — reports that it no longer knows. No switch is in flight, so whoever took the
        // health down, it was not a claim.
        reported.ModelsStatus = 503;
        using var loopCts = new CancellationTokenSource();
        engine.StartHealthLoop(loopCts.Token);
        await WaitUntil(() => !engine.Healthy && engine.SwitchingToward is null);
        var unknownByProbe = engine.Capability.Version;

        // The successor claims while A is deciding. The marker goes up over an engine that is already unknown, so
        // this claim authors no withdrawal (AC #8) and cannot make the probe's answer look like a transaction.
        var second = engine.WaitForModelAsync("m", TimeSpan.FromSeconds(25), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1 && provider.Launches.Count == 2);
        Assert.False(engine.Healthy);
        Assert.Equal("m", engine.SwitchingToward);
        Assert.Contains("extra", engine.LoadedModels);      // the list A's proof carried is untouched

        resume.TrySetResult();                        // A resumes into exactly that snapshot

        // Not answered Ready off its own proof. The successor's switch is completed on its own terms, and the
        // third launch — A's own turn, for its own target — is the assertion that the proof was withdrawn.
        reported.ModelsStatus = 200;
        reported.LoadedModels = ["m"];
        provider.ReleaseOne();
        await WaitUntil(() => provider.GateCount == 1 && provider.Launches.Count == 3);
        Assert.Equal("extra", provider.Launches[^1]);
        Assert.DoesNotContain("engine.switch_failed", feed.Replay(0).Select(e => e.Kind));

        reported.LoadedModels = ["extra"];
        provider.ReleaseOne();
        var answer = await first;
        Assert.Equal(EngineRuntime.SwitchOutcome.Ready, answer.Outcome);
        Assert.Equal(["extra", "m", "extra"], provider.Launches);
        Assert.True((await second).IsReady);              // the successor answers off its own switch
        Assert.True(engine.Capability.Version > unknownByProbe,
            $"nothing published between the probe's negative and the answer: {engine.Capability}");
    }

    /// <summary>§5.5, TASK-61 AC #6 (decision-36): the withdrawn rule did not answer a caller late, it bought
    /// lifecycle transactions nobody asked for. Two callers wanting <b>different models on a one-model engine</b>
    /// — one switch per caller, in arrival order, is the whole claim, and it is asserted as a launch sequence, not
    /// as the absence of a timeout. The ordering is made, not hoped for: the first caller is held in the seam
    /// between its readiness wake-up and its snapshot read while its own switch is already committed and idle, the
    /// successor then arrives and claims for its own target, and the successor's launch stays on its gate while the
    /// first caller answers. Under the withdrawn rule this pin fails on the sequence — the first caller lost its
    /// proof to a marker it did not raise, coalesced onto the successor's switch, and claimed again, so the
    /// sequence reached three launches with the first caller's answer arriving only after someone else's load.</summary>
    [Fact(Timeout = 30_000)]
    public async Task TwoCallersWantingDifferentModelsTakeOneSwitchEach()
    {
        var provider = new GatedProvider { GateLaunches = true };
        using var reported = new FakeEngineHandler { LoadedModels = ["extra"] };
        var feed = new EventRing();
        var engine = Engine(provider, feed, models: reported);
        await using var _ = engine;

        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AfterReadyWaitForTests = _ =>
        {
            parked.TrySetResult();
            resume.Task.GetAwaiter().GetResult();
        };

        var first = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(25), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);
        provider.ReleaseOne();                        // its own switch proves "extra" and commits
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(engine.Switching);               // the attempt is over: a latecomer claims for itself
        Assert.True(engine.Healthy);

        // The successor arrives after that commit, so it takes its own turn, and its launch is held: while the
        // first caller is still deciding, the engine carries a marker for a target it does not want.
        var second = engine.WaitForModelAsync("m", TimeSpan.FromSeconds(25), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1 && provider.Launches.Count == 2);
        Assert.Equal("m", engine.SwitchingToward);

        resume.TrySetResult();
        var answer = await first;
        Assert.Equal(EngineRuntime.SwitchOutcome.Ready, answer.Outcome);
        Assert.Equal(1, provider.GateCount);          // answered while the successor's load was still held

        // AC #6 itself: one launch per caller, in arrival order.
        Assert.Equal(["extra", "m"], provider.Launches);

        reported.LoadedModels = ["m"];
        provider.ReleaseOne();                        // the successor finishes on its own terms
        Assert.True((await second).IsReady);
        Assert.Equal(["extra", "m"], provider.Launches);
        Assert.Equal(0, provider.GateCount);          // no third transaction for anybody to lose
        Assert.DoesNotContain("engine.switch_failed", feed.Replay(0).Select(e => e.Kind));
    }

    /// <summary>A refusal that is not this caller's must not read the other caller's request back to them. The
    /// ceiling sentence used to name the target of the switch in flight, so caller B learned which model caller A
    /// had asked for. A caller's own ceiling and their own target stay — they are the reader's own identifiers —
    /// and another request's target never appears (§5.5).</summary>
    [Fact(Timeout = 30_000)]
    public async Task AWaitersRefusalNeverNamesAnotherCallsTarget()
    {
        var provider = new GatedProvider { GateLaunches = true };
        var feed = new EventRing();
        using var reported = Reports("m");            // "second" is selectable, and not loaded
        var engine = Engine(provider, feed, models: reported);
        await using var _ = engine;

        var first = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);   // the switch in front is claimed and held

        var waiting = await engine.WaitForModelAsync("second", TimeSpan.FromMilliseconds(700), CancellationToken.None);
        Assert.False(waiting.IsReady);
        Assert.Equal(EngineRuntime.SwitchOutcome.TimedOut, waiting.Outcome);
        Assert.Contains("700 ms", waiting.Detail);                    // their own ceiling, in their own message
        Assert.Contains("another request", waiting.Detail);
        Assert.DoesNotContain("a switch to", waiting.Detail);          // no target named at all…
        Assert.DoesNotContain("extra", waiting.Detail);                // …and specifically not the other's

        provider.ReleaseOne();
        await first;
        Assert.DoesNotContain("extra", waiting.Detail);                // still clean after the switch settles
    }
}
