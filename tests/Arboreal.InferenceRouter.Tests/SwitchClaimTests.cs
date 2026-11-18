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
        public string Kind => "gated";

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
            Catalog = new Dictionary<string, EngineModelDefinition>
            {
                ["m"] = new() { Name = "m" },
                ["extra"] = new() { Name = "extra" },
            },
        };
        var config = new RouterConfig { Engines = new Dictionary<string, EngineDefinition> { ["fake"] = def } };
        return new EngineRuntime(def, config, feed ?? new EventRing(), models, lifecycle: provider, dispatchHint: dispatchHint);
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
        Assert.Contains("could not start the switch", refused.Detail);
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
        await WaitUntil(() => provider.Launches.Count == 1);

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

        // TaskCanceledException from the semaphore wait — an OperationCanceledException, which the request path
        // reports as "cancelled". The point of the assertion is the type it is *not*: ObjectDisposedException,
        // which a disposed switch lock produced and the request path could only call engine_unreachable.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => newcomer);

        // The caller whose switch was already in flight is released the same way. Its switch settled *false* —
        // the provider saw the engine's token cancelled and reported a lifecycle failure — and reporting that to
        // the caller as a failed model load would describe a router that stopped as an engine that broke. So the
        // switch records, before it settles the waiters, that the stop came from shutdown, and the waiter answers
        // cancellation. The transaction also settles and clears its marker: a marker left up tells the scheduler
        // this engine is busy forever, and an unsettled readiness task parks every other waiter out its ceiling.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
        await WaitUntil(() => engine.SwitchingToward is null);
        Assert.Null(engine.SwitchingToward);

        // The load itself has to have felt the stop: the provider reports the cancellation it observed, so the
        // two answers above are responses to a real shutdown and not to a switch that never reached the exec.
        Assert.Contains("engine.switch_failed", await WaitUntilFeed(feed, "cancelled_during_launch"));
    }

    /// <summary>The waiter that is inside the readiness wait when the router stops. Shutdown settles that
    /// switch's waiters **before** it cancels anything, so this waiter wakes on a switch that has ended and the
    /// seam records that it woke on the ending, not on a token: the ordering the answer depends on is a fact the
    /// test observes, not one it hopes the scheduler produces. The answer is cancellation — a router that stopped
    /// in the middle of a load, described as cancellation — and the transaction still ends and clears its marker.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AWaiterInsideTheReadinessWaitIsToldTheRouterStopped()
    {
        var provider = new GatedProvider { GateLaunches = true };
        var feed = new EventRing();
        var engine = Engine(provider, feed);

        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? wake = null;
        engine.AfterReadyWaitForTests = byCancellation =>
        {
            wake = byCancellation ? "cancelled" : "settled";
            parked.TrySetResult();
            go.Task.GetAwaiter().GetResult();
        };

        var waiting = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        await WaitUntil(() => provider.Launches.Count == 1);      // the load is in flight, the caller is parked

        await engine.DisposeAsync();
        await parked.Task;                                       // parked at the decision point

        // Woken by a switch that had ended, not by a token: settling a waiter is not ending a transaction, so
        // the provider's own end is what clears the marker, and that happens after this point.
        Assert.Equal("settled", wake);
        go.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
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
        Assert.Contains("switch to extra failed", refused.Detail);
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

    /// <summary>§5.5, TASK-61 AC #3 and #4: a waiter whose own switch *succeeded* but whose snapshot no longer
    /// authorises the target is neither answered Ready nor refused — it takes its own turn, which is what
    /// decision-9 says a caller overtaken by another target does. The seam sits between the readiness await and
    /// the resume snapshot read, so the successor wins the engine while the first waiter is deciding: the answer
    /// cannot come out right by thread-scheduling luck. Before this rule the waiter was told "switch to extra
    /// failed" for a switch that had just succeeded, and its request died while the model was about to be unloaded
    /// by someone else's claim.</summary>
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

        // A successor claims the engine for another target while A is deciding — and the engine, one model at a
        // time, no longer holds what A was promised.
        var second = engine.WaitForModelAsync("m", TimeSpan.FromSeconds(25), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1 && provider.Launches.Count == 2);
        reported.LoadedModels = ["m"];
        resume.TrySetResult();                        // A resumes and reads a snapshot that says no

        // Not answered with a model the successor is about to unload, and not refused either: it queues behind
        // a new attempt of its own, which is the loop's whole point.
        Assert.False(first.IsCompleted);
        Assert.Equal(["extra", "m"], provider.Launches);

        provider.ReleaseOne();                        // the successor's switch to "m" ends
        await WaitUntil(() => provider.GateCount == 1 && provider.Launches.Count == 3);
        Assert.Equal("extra", provider.Launches[^1]);  // A's own turn, not a refusal
        reported.LoadedModels = ["extra"];
        provider.ReleaseOne();

        var answer = await first;
        Assert.True(answer.IsReady);                  // Ready, but only off its own attempt
        Assert.DoesNotContain("engine.switch_failed", feed.Replay(0).Select(e => e.Kind));
        Assert.Equal(EngineRuntime.SwitchOutcome.Ready, answer.Outcome);
    }
}
