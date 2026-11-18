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

        // The trust path: the exec's own success is the switch's claim, so no HTTP probes are needed here.
        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.FromSeconds(1));

    }

    private static EngineRuntime Engine(
        GatedProvider provider, EventRing? feed = null, Action? dispatchHint = null)
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
        return new EngineRuntime(def, config, feed ?? new EventRing(), lifecycle: provider, dispatchHint: dispatchHint);
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
        var engine = Engine(provider, feed, dispatchHint: () => throw new InvalidOperationException("dispatcher blew up"));
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
        var engine = Engine(provider, dispatchHint: () => nudges++);
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
        var engine = Engine(provider);
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
        var engine = Engine(provider);

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

        // The caller whose switch was already in flight is released the same way, and the switch transaction
        // settles and clears its marker: a marker left up tells the scheduler this engine is busy forever, and
        // an unsettled readiness task parks every other waiter out its ceiling.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
        await WaitUntil(() => engine.SwitchingToward is null);
        Assert.Null(engine.SwitchingToward);
    }

    /// <summary>A claim hook that throws has its claim undone — but undone after leaving the transition gate,
    /// because undoing it means settling waiters and nudging dispatch, and the gate is reentrant, so doing it
    /// inside would run both while every later claim queues behind the gate. The seam reports the gate state so
    /// the ordering is pinned rather than asserted about.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AClaimHookThatThrowsRollsBackOutsideTheTransitionGate()
    {
        var provider = new GatedProvider();
        var engine = Engine(provider);
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
}
