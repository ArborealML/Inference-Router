using System.Collections.Immutable;
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

    /// <summary>A fault at terminal success leaves both joined callers ready and the same event
    /// available in replay when readiness settles and when each caller wakes.</summary>
    [Fact(Timeout = 60_000)]
    public async Task AThrowingSubscriberDoesNotStrandTheSwitchItWoke()
    {
        using var reported = new FakeEngineHandler { LoadedModels = ["m"] };
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = bound.Token;
        var feed = new EventRing();
        var provider = new GatedLaunch();
        await using var engine = Engine(feed, provider, reported);

        var firstWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new TaskCompletionSource<ImmutableArray<long>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWake = new TaskCompletionSource<ImmutableArray<long>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWake = new TaskCompletionSource<ImmutableArray<long>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var followingObserver = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = TimeSpan.FromSeconds(20);
        var arrivals = 0;
        engine.SwitchWaitTimeForTests = deadline =>
        {
            var arrival = Interlocked.Increment(ref arrivals) == 1 ? firstWaiting : secondWaiting;
            arrival.TrySetResult();
            return deadline - wait;
        };

        static bool MatchesCompletion(RouterEvent evt) =>
            evt.Kind == "engine.switch_completed" && evt.Engine == "fake" && evt.Model == "extra";

        ImmutableArray<long> CompletionSequences() =>
            [.. feed.Replay(0).Where(MatchesCompletion).Select(evt => evt.Seq)];

        engine.AfterEndCommitForTests = _ =>
        {
            var snapshot = CompletionSequences();
            producer.TrySetResult(snapshot);
        };

        var wakes = 0;
        engine.AfterReadyWaitForTests = cancelled =>
        {
            if (cancelled) return;

            var snapshot = CompletionSequences();
            var wake = Interlocked.Increment(ref wakes) == 1 ? firstWake : secondWake;
            wake.TrySetResult(snapshot);
        };

        var matchingCallbacks = 0;
        Assert.True(feed.TrySubscribeReplay(0, evt =>
        {
            if (MatchesCompletion(evt) && Interlocked.Increment(ref matchingCallbacks) == 1)
                throw new InvalidOperationException("the fixture observer failed at switch completion");
        }, out _));
        Assert.True(feed.TrySubscribeReplay(0, evt =>
        {
            if (MatchesCompletion(evt)) followingObserver.TrySetResult(evt.Seq);
        }, out _));
        var faultsBefore = feed.SubscriberFaults;

        var first = engine.WaitForModelAsync("extra", wait, ct);
        try
        {
            await provider.Entered.Task.WaitAsync(ct);
            await firstWaiting.Task.WaitAsync(ct);
            var joined = engine.WaitForModelAsync("extra", wait, ct);
            await secondWaiting.Task.WaitAsync(ct);
            Assert.Equal(2, Volatile.Read(ref arrivals));
            Assert.False(first.IsCompleted);
            Assert.False(joined.IsCompleted);
            Assert.Equal(1, provider.LaunchCount);

            reported.LoadedModels = ["m", "extra"];
            provider.Release.TrySetResult();

            Assert.True((await first.WaitAsync(ct)).IsReady);
            Assert.True((await joined.WaitAsync(ct)).IsReady);
            var atSettlement = await producer.Task.WaitAsync(ct);
            var atFirstWake = await firstWake.Task.WaitAsync(ct);
            var atSecondWake = await secondWake.Task.WaitAsync(ct);

            // A retained event at settlement has already gone through the synchronous fanout.
            // For an inverted emit, let the observer prove the terminal eventually arrived.
            if (!atSettlement.IsEmpty)
                Assert.True(followingObserver.Task.IsCompletedSuccessfully, "The observer after the throw missed the completion.");
            var observedSeq = await followingObserver.Task.WaitAsync(ct);
            var terminalSeq = Assert.Single(CompletionSequences());
            Assert.Equal(terminalSeq, observedSeq);
            Assert.Equal(terminalSeq, Assert.Single(atSettlement));
            Assert.Equal(terminalSeq, Assert.Single(atFirstWake));
            Assert.Equal(terminalSeq, Assert.Single(atSecondWake));
            Assert.Equal(2, Volatile.Read(ref arrivals));
            Assert.Equal(2, Volatile.Read(ref wakes));
            Assert.Equal(1, provider.LaunchCount);
            Assert.Equal(1, Volatile.Read(ref matchingCallbacks));
            Assert.Equal(faultsBefore + 1, feed.SubscriberFaults);
            Assert.Null(engine.SwitchingToward);
        }
        finally
        {
            provider.Release.TrySetResult();
        }
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

    /// <summary>Holds the launch open while the second caller joins.</summary>
    private sealed class GatedLaunch : IEngineLifecycleProvider
    {
        private int _launchCount;

        public string Kind => "exec";
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int LaunchCount => Volatile.Read(ref _launchCount);

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok("fake"));

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(
            LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok());

        public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            Interlocked.Increment(ref _launchCount);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            return LifecycleResult.Ok();
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.FromSeconds(1));
    }
}
