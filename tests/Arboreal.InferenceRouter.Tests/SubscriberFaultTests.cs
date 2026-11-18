using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>§6.1, TASK-103: a feed observer is a stranger to the code that emitted the event, and its failure may
/// not reach that code. Every subscriber runs inside the append, on the emitter's thread — so before this was
/// isolated, an exception from an SSE handler whose client had hung up landed inside a capability publication, a
/// switch ending, or a terminal settle: the transition half-done, the readiness task stranded, the caller answered
/// by a stranger's bug. A fault now costs that observer its own event, is counted for the operator, and leaves the
/// producer's work and the other subscribers alone.</summary>
public sealed class SubscriberFaultTests
{
    /// <summary>The ring's own half: the append completes, the event is retained, the subscribers that did not throw
    /// still receive it, and the fault is counted. The emitter gets its sequence number back — an append that was
    /// interrupted halfway would leave a hole no reader could tell apart from a filtered window.</summary>
    // No Timeout: xUnit bounds only async tests, and this pin awaits nothing — the ring's dispatch is the whole
    // subject, and it is synchronous by design.
    [Fact]
    public void ARingAppendCompletesWhenASubscriberThrowsAndTheOthersStillSeeIt()
    {
        var feed = new EventRing();
        var first = new List<string>();
        var last = new List<string>();

        Assert.True(feed.TrySubscribeReplay(0, e => first.Add(e.Kind), out var replayed1));
        Assert.True(feed.TrySubscribeReplay(0, _ => throw new InvalidOperationException("the observer is broken"), out var replayed2));
        Assert.True(feed.TrySubscribeReplay(0, e => last.Add(e.Kind), out var replayed3));

        var seq = feed.Append(new RouterEvent { Kind = "engine.switch_failed", Model = "strata/extra", Reason = "launch_failed" });

        Assert.Equal(1, seq);
        Assert.Equal(["engine.switch_failed"], first);
        Assert.Equal(["engine.switch_failed"], last);
        Assert.Single(feed.Replay(0));
        Assert.Equal(1, feed.SubscriberFaults);
    }

    /// <summary>The producer's half: a subscriber that throws on <b>every</b> event — including the one that reports
    /// the launch and the one that settles health — does not change what the caller is told. The switch completes,
    /// the capability is published as a proof, and the answer is the model being served.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AThrowingSubscriberLeavesTheSwitchAnswerExactlyAsItWas()
    {
        using var reported = new FakeEngineHandler { LoadedModels = ["m", "extra"] };
        var feed = new EventRing();
        var provider = new LaunchesOk();
        var engine = Engine(feed, provider, reported);
        await using var _ = engine;

        Assert.True(feed.TrySubscribeReplay(0, _ => throw new InvalidOperationException("the observer is broken"), out var replayed));

        var answer = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.True(answer.IsReady);                                   // the caller's answer, not the observer's fault
        Assert.True(engine.Capability.Healthy);
        Assert.Contains("extra", engine.Capability.LoadedModels);
        Assert.True(feed.SubscriberFaults > 0);                        // and the failure is visible, not swallowed
        Assert.Null(engine.SwitchingToward);
    }

    /// <summary>The half that used to strand a request: an observer that throws at the emit nearest the transition —
    /// the record that the switch has begun, written by the switch task itself. A second caller parked behind that
    /// switch still takes the joined outcome, and the readiness task is settled rather than left for the caller's
    /// ceiling. A stranded switch is not a slow request, it is a request that never answers.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AThrowingSubscriberDoesNotStrandTheSwitchItWoke()
    {
        using var reported = new FakeEngineHandler { LoadedModels = ["m"] };
        var feed = new EventRing();
        var provider = new GatedLaunch();
        var engine = Engine(feed, provider, reported);
        await using var _ = engine;

        var started = 0;
        Assert.True(feed.TrySubscribeReplay(0, e =>
        {
            if (e.Kind != "engine.switch_started") return;
            started++;
            throw new InvalidOperationException("the SSE client hung up mid-write");
        }, out var replayed));

        var first = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(20), CancellationToken.None);
        await WaitUntil(() => provider.GateCount == 1);
        var joined = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(20), CancellationToken.None);

        reported.LoadedModels = ["m", "extra"];
        provider.ReleaseAll(LifecycleResult.Ok());
        var a = await first;
        var b = await joined;

        Assert.True(a.IsReady);
        Assert.True(b.IsReady);                       // the waiter was not left parked on a task nobody settles
        Assert.Equal(1, started);                     // the throw happened, at the emit nearest the transition
        Assert.Equal(1, feed.SubscriberFaults);
        Assert.Contains("engine.switch_started", feed.Replay(0).Select(e => e.Kind));
        Assert.Null(engine.SwitchingToward);
    }

    /// <summary>Counting is not enough if nobody can see the count: the fault total rides the status surface an
    /// operator already reads, so a live tail that has stopped being delivered is a number rather than a rumour.
    /// Real HTTP against <c>/admin/status</c>, because a field nobody serves is not visible.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AdminStatusCarriesTheSubscriberFaultCount()
    {
        var (app, _, port) = await WireHarness.StartAsync();
        try
        {
            var core = app.Services.GetRequiredService<RouterCore>();
            Assert.True(core.Events.TrySubscribeReplay(
                0, _ => throw new InvalidOperationException("the observer is broken"), out var replayed));
            var before = core.Events.SubscriberFaults;
            core.Events.Append(new RouterEvent { Kind = "router.admin_ack", Reason = "the probe event" });

            using var http = new HttpClient();
            using var res = await http.GetAsync($"http://127.0.0.1:{port}/admin/status");
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal(before + 1, doc.RootElement.GetProperty("FeedSubscriberFaults").GetInt64());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // ── harness ───────────────────────────────────────────────────────────────

    private static EngineRuntime Engine(EventRing feed, IEngineLifecycleProvider provider, FakeEngineHandler reported)
    {
        var def = new EngineDefinition
        {
            Name = "fake",
            Kind = EngineKind.OpenAiCompatible,
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Catalog = new Dictionary<string, EngineModelDefinition>
            {
                ["m"] = new() { Name = "m" },
                ["extra"] = new() { Name = "extra", Select = new LifecycleCommand { Exec = "/bin/true" } },
            },
        };
        var engines = new Dictionary<string, EngineDefinition> { ["fake"] = def };
        return new EngineRuntime(def, new RouterConfig { Engines = engines }, feed, reported, lifecycle: provider);
    }

    private sealed class LaunchesOk : IEngineLifecycleProvider
    {
        public string Kind => "exec";

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok("fake"));

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(
            LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok());

        public ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok("the fake launched it"));

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.FromSeconds(1));
    }

    /// <summary>Holds the launch open so the second caller is provably parked behind the switch while the observer
    /// throws at its nearest emit.</summary>
    private sealed class GatedLaunch : IEngineLifecycleProvider
    {
        private readonly Queue<TaskCompletionSource<LifecycleResult>> _gates = new();
        public string Kind => "exec";

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok("fake"));

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(
            LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok());

        public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            TaskCompletionSource<LifecycleResult> gate;
            lock (_gates)
            {
                gate = new TaskCompletionSource<LifecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates.Enqueue(gate);
            }

            return await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public void ReleaseAll(LifecycleResult outcome)
        {
            TaskCompletionSource<LifecycleResult>[] all;
            lock (_gates)
            {
                all = [.. _gates];
                _gates.Clear();
            }

            foreach (var gate in all) gate.TrySetResult(outcome);
        }

        public int GateCount
        {
            get { lock (_gates) return _gates.Count; }
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.FromSeconds(1));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException("condition not met within " + timeoutMs + " ms");
    }
}
