using System.Net;
using System.Text;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>What an engine is entitled to claim about itself, and for how long. The capability snapshot is
/// the router's only evidence for two opposite decisions — send a request here, or refuse one with 503 —
/// so the rule under test is narrow: a snapshot is either current-and-verified, or it says it does not
/// know. Three things used to make an engine confidently wrong, and each is pinned here: a model probe
/// whose round trip straddled a model switch, a switch that failed after it had already stopped the
/// engine, and a poll that learned nothing but still moved the version the refusal test depends on
/// (§5.5).</summary>
public sealed class CapabilityPublicationTests
{
    /// <summary>An engine endpoint the test steers: what <c>/models</c> reports can change at any point, and
    /// one answer can be held in flight so a probe's bytes provably carry the older set.</summary>
    private sealed class ProbeHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string[] _models;
        private bool _holdArmed;
        private int _modelsRequests;

        public ProbeHandler(params string[] models) => _models = models;

        public void SetModels(params string[] models) => _models = models;

        /// <summary>Status the fake answers <c>/models</c> with. Non-200 means the router cannot ask what is
        /// loaded, which is a different situation from an engine that answers and names nothing.</summary>
        public int ModelsStatus { get; set; } = 200;
        public int ModelsRequests => Volatile.Read(ref _modelsRequests);
        public Task EnteredAsync => _entered.Task;
        public Task AnsweredAsync => _answered.Task;

        /// <summary>Arm once: the next <c>/models</c> request takes its answer as if the engine had replied,
        /// announces that it is in flight, and then waits to be released. The answer it eventually writes
        /// is the set as it was when the request started — which is exactly the state a switch can overtake.
        /// Later requests are answered normally, so only the one straddling probe is affected.</summary>
        public void HoldNextProbe() => _holdArmed = true;
        public void ReleaseProbe() => _released.TrySetResult();

        private static bool Matches(string path, string route) =>
            path == route || path.EndsWith("/" + route, StringComparison.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            if (Matches(path, "models"))
            {
                Interlocked.Increment(ref _modelsRequests);
                if (ModelsStatus != 200)
                    return new HttpResponseMessage((HttpStatusCode)ModelsStatus) { Content = new StringContent("{}") };
                var answer = _models;                       // bytes as they would have arrived
                if (_holdArmed)
                {
                    _holdArmed = false;
                    _entered.TrySetResult();
                    await _released.Task.ConfigureAwait(false);
                    _answered.TrySetResult();
                }
                var items = string.Join(",", answer.Select(
                    m => "{\"id\":\"" + m + "\",\"status\":{\"value\":\"loaded\"}}"));
                return Ok("{\"object\":\"list\",\"data\":[" + items + "]}");
            }
            if (Matches(path, "props"))
                return Ok("""{"total_slots": 4, "n_ctx": 8192}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }

        private static HttpResponseMessage Ok(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>The lifecycle seam: launches either succeed or fail on request, optionally gated so a switch
    /// can be held in flight while the test looks at what the router believes.</summary>
    private sealed class ScriptedProvider : IEngineLifecycleProvider
    {
        private readonly Queue<TaskCompletionSource> _gates = new();

        public string Kind => "scripted";
        public LifecycleResult LaunchOutcome { get; set; } = LifecycleResult.Ok();
        public bool ProveHealth { get; set; } = true;
        public int ReadinessMs { get; set; } = 10_000;
        public bool GateLaunches { get; set; }
        public int LaunchCount { get; private set; }
        public List<string> Launches { get; } = [];

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok("fake"));

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(
            LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok());

        public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            LaunchCount++;
            Launches.Add(target.Model);
            if (GateLaunches)
            {
                TaskCompletionSource gate;
                lock (_gates)
                {
                    gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates.Enqueue(gate);
                }
                await gate.Task.ConfigureAwait(false);
            }
            return LaunchOutcome;
        }

        public void ReleaseOne()
        {
            TaskCompletionSource? gate;
            lock (_gates) { gate = _gates.Count > 0 ? _gates.Dequeue() : null; }
            gate?.TrySetResult();
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) =>
            new(ProveHealth, TimeSpan.FromMilliseconds(ReadinessMs));
    }

    /// <summary>Records what the capability snapshot said at the instant the router nudged dispatch, so an
    /// ordering claim ("the nudge comes after the transition commits") is measured rather than asserted from
    /// the code's shape.</summary>
    private sealed class HintProbe
    {
        public List<(string? Toward, bool Healthy)> Samples { get; } = [];
        public EngineRuntime? Engine { get; set; }
        public void Sample()
        {
            var cap = Engine!.Capability;
            Samples.Add((cap.SwitchingToward, cap.Healthy));
        }
    }

    private static EngineDefinition Def(HealthProbeDefinition? health = null) => new()
    {
        Name = "fake",
        Kind = EngineKind.OpenAiCompatible,
        Endpoint = new Uri("http://127.0.0.1:9/v1"),
        Health = health,
        Catalog = new Dictionary<string, EngineModelDefinition>
        {
            ["m"] = new() { Name = "m" },
            ["extra"] = new() { Name = "extra" },
            ["second"] = new() { Name = "second" },
        },
    };

    private static EngineRuntime NewEngine(
        EngineDefinition def, ScriptedProvider provider, ProbeHandler? handler = null, Action? hint = null) =>
        new(def, new RouterConfig { Engines = new Dictionary<string, EngineDefinition> { ["fake"] = def } },
            new EventRing(), handler, lifecycle: provider, dispatchHint: hint);

    private static ServingProfile Profile(string name, long window) =>
        new() { Name = name, MaxContext = window };

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

    [Fact(Timeout = 60_000)]
    public async Task AProbeTakenBeforeASwitchCannotOverwriteWhatTheSwitchProved()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 50, TimeoutMs = 30_000 });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        using var loopCts = new CancellationTokenSource();
        engine.StartHealthLoop(loopCts.Token);
        await WaitUntil(() => engine.Healthy);

        // One probe is now in flight, carrying the answer "just m".
        handler.HoldNextProbe();
        await handler.EnteredAsync;
        Assert.True(engine.Healthy);

        // A switch lands while that probe is still on the wire: the engine now reports two models and the
        // switch's own proof publishes them.
        handler.SetModels("m", "extra");
        var switched = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(switched.IsReady);
        Assert.Contains("extra", engine.LoadedModels);
        var proofVersion = engine.Capability.Version;

        // Stop the loop, then let the straddling probe answer with the set it captured. Writing it would
        // put the engine back to one model, and a request for the model that is loaded would then be
        // refused with 503 for up to a poll interval.
        loopCts.Cancel();
        handler.ReleaseProbe();
        await handler.AnsweredAsync;
        await Task.Delay(300);

        Assert.Contains("extra", engine.LoadedModels);
        Assert.True(engine.Healthy);
        Assert.Equal(proofVersion, engine.Capability.Version);   // the stale answer was dropped, not merged
    }

    [Fact(Timeout = 60_000)]
    public async Task TheMarkerGoesUpBeforeTheEngineIsClaimed()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider { GateLaunches = true };
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        string? markerAtClaim = null;
        engine.AfterClaimForTests = () => markerAtClaim = engine.SwitchingToward;

        handler.SetModels("m", "second");
        var pending = engine.WaitForModelAsync("second", TimeSpan.FromSeconds(30), CancellationToken.None);
        await WaitUntil(() => provider.LaunchCount == 1);
        provider.ReleaseOne();
        Assert.True((await pending).IsReady);

        // Dispatch and the admission refusal both read the snapshot rather than the runtime's private claim, so
        // the marker has to be visible no later than the claim itself: an engine that has been claimed for a
        // switch but still looks idle in the snapshot is an engine a pass will route into.
        Assert.Equal("second", markerAtClaim);
    }

    [Fact(Timeout = 60_000)]
    public async Task QuietPollsOfUnchangedContentLeaveTheGenerationAlone()
    {
        using var handler = new ProbeHandler("a", "b");
        var provider = new ScriptedProvider();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 50, TimeoutMs = 5_000 });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        using var loopCts = new CancellationTokenSource();
        engine.StartHealthLoop(loopCts.Token);
        await WaitUntil(() => engine.Healthy);
        var quiet = engine.Capability.Version;

        // Several ordinary polls, same set each time. Every one used to allocate a fresh array, so every
        // one moved the version, and the refusal path's "nothing changed while I decided" test then burned
        // its retry on a poll that had learned nothing — which is why the 503 rarely fired.
        await Task.Delay(500);
        Assert.Equal(quiet, engine.Capability.Version);
        Assert.True(engine.Healthy);

        // Spelling case is not a capability change: every consumer of the list compares names
        // case-insensitively, so this is the same answer.
        handler.SetModels("A", "b");
        await Task.Delay(300);
        Assert.Equal(quiet, engine.Capability.Version);

        // A report order change still counts as a change: nothing in the router treats that as noise today,
        // and pretending otherwise would be a claim the code does not support.
        handler.SetModels("b", "a");
        await WaitUntil(() => engine.Capability.Version != quiet);
        Assert.Equal(["b", "a"], engine.LoadedModels);
    }

    [Fact(Timeout = 60_000)]
    public async Task AProvenSwitchMakesTheEngineEligibleWithoutWaitingForAnotherPoll()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider();                       // ProveHealth = true
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        // Nothing has ever probed this engine: it is unknown, which is the honest answer and the one that
        // refuses nothing.
        Assert.False(engine.Healthy);
        Assert.False(engine.IsReadyFor("extra", null));

        handler.SetModels("m", "extra");
        var switched = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(switched.IsReady);

        // The proof is the evidence: health arrives with the model list in one publication, rather than
        // waiting for a poll that, at this interval, would never come. Before this, a switch that had just
        // proved the engine healthy left it reading unhealthy — and the scheduler skips unhealthy engines.
        Assert.True(engine.Healthy);
        Assert.Null(engine.SwitchingToward);
        Assert.Contains("extra", engine.LoadedModels);
        Assert.True(engine.IsReadyFor("extra", null));
    }

    [Fact(Timeout = 60_000)]
    public async Task ASwitchThatFailedAfterStoppingTheEngineStopsClaimingAnything()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider { LaunchOutcome = LifecycleResult.Fail("launch_failed", "selector exited 1") };
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 50, TimeoutMs = 5_000 });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        using var loopCts = new CancellationTokenSource();
        engine.StartHealthLoop(loopCts.Token);
        await WaitUntil(() => engine.Healthy);

        var failed = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(failed.IsReady);

        // The engine was stopped and nothing was relaunched, so the last good probe describes a process
        // that is gone. The list survives as history; the claim does not.
        Assert.False(engine.Healthy);
        Assert.Null(engine.SwitchingToward);
        Assert.Contains("m", engine.LoadedModels);

        // Capability unknown is also the answer that cannot refuse: a hopeless-looking model is not refused
        // while the engine's state is unknown, and is refused once it has answered again.
        ICapabilitySource[] sources = [engine];
        Assert.False(CapabilityScan.DefinitelyHopeless(sources, "ghost"));
        await WaitUntil(() => engine.Healthy);
        Assert.True(CapabilityScan.DefinitelyHopeless(sources, "ghost"));
    }

    [Fact(Timeout = 60_000)]
    public async Task AReadinessTimeoutLeavesTheEngineUnknownNotConfidentlyWrong()
    {
        using var handler = new ProbeHandler("m");            // never reports "extra"
        var provider = new ScriptedProvider { ReadinessMs = 400 };
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        var first = await engine.WaitForModelAsync("m", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(first.IsReady);                     // m is already reported: readiness proof lands
        Assert.True(engine.Healthy);

        var timedOut = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(timedOut.IsReady);
        Assert.False(engine.Healthy);                   // the launch happened; its outcome is not known
        Assert.Null(engine.SwitchingToward);
        Assert.False(engine.IsReadyFor("m", null));     // a stale list cannot answer Ready on its own
    }

    [Fact(Timeout = 60_000)]
    public async Task TheDispatchNudgeArrivesOnlyAfterTheCapabilityTransitionCommits()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider();
        var probe = new HintProbe();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler, hint: probe.Sample);
        probe.Engine = engine;
        await using var _ = engine;

        handler.SetModels("m", "extra");
        var switched = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(switched.IsReady);

        // Switch outcome events fire *before* the transition commits, so a dispatcher woken by them sees the
        // marker still up and skips this engine. The nudge has to land after the commit: every sample taken
        // inside it shows a settled, verified engine.
        Assert.NotEmpty(probe.Samples);
        Assert.All(probe.Samples, s =>
        {
            Assert.Null(s.Toward);
            Assert.True(s.Healthy);
        });

        provider.LaunchOutcome = LifecycleResult.Fail("launch_failed", "selector exited 1");
        var failed = await engine.WaitForModelAsync("second", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(failed.IsReady);

        // A failed switch commits too, and commits as unknown: the nudge still arrives marker-free, so the
        // dispatcher is never told to look at an engine that is mid-mutation.
        var last = probe.Samples[^1];
        Assert.Null(last.Toward);
        Assert.False(last.Healthy);
    }

    [Fact(Timeout = 60_000)]
    public async Task ASwitchTrustingTheExecThatCannotAskWhatIsLoadedClaimsNothing()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider { ProveHealth = false };
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        handler.SetModels("m", "extra");
        var first = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(first.IsReady);
        Assert.True(engine.Healthy);
        Assert.Contains("extra", engine.LoadedModels);

        // wait_health=false: the provider's exec says it launched, and the router asks what is loaded.
        // This time the engine will not answer. The switch still completes — the exec did what it was
        // told — but the old list must stop being presented as current, because a model loaded by hand or
        // dropped by a restart would otherwise be claimed or refused on evidence from before the switch.
        handler.ModelsStatus = 503;
        var second = await engine.WaitForModelAsync("second", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(second.IsReady);
        Assert.False(engine.Healthy);
        Assert.Null(engine.SwitchingToward);
        Assert.Contains("extra", engine.LoadedModels);      // kept as history, not as a claim

        ICapabilitySource[] sources = [engine];
        Assert.False(CapabilityScan.DefinitelyHopeless(sources, "ghost"));
    }

    [Fact(Timeout = 60_000)]
    public async Task ClaimingASwitchStopsTheEngineClaimingWhatItKnewBeforeIt()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        // Get to a verified baseline first: a proven switch leaves the engine healthy and reporting extras,
        // so "unknown" below can only come from the switch being claimed, not from never having looked.
        handler.SetModels("m", "extra");
        var proven = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(proven.IsReady);
        Assert.True(engine.Healthy);

        provider.GateLaunches = true;                       // only the next switch is held open
        handler.SetModels("m", "extra", "second");
        var pending = engine.WaitForModelAsync("second", TimeSpan.FromSeconds(30), CancellationToken.None);
        await WaitUntil(() => provider.LaunchCount == 2);

        // The mutation has been decided but has not run: the engine is about to be stopped, so the answer it
        // gave beforehand is already history. The marker goes up and health goes unknown in one publication,
        // before the switch state is even installed — which is what keeps a dispatch pass from routing into
        // this engine, and keeps the admission refusal from treating "not in the list" as evidence.
        Assert.Equal("second", engine.SwitchingToward);
        Assert.False(engine.Healthy);
        Assert.Contains("extra", engine.LoadedModels);          // history is kept, the claim is not
        ICapabilitySource[] sources = [engine];
        Assert.False(CapabilityScan.DefinitelyHopeless(sources, "ghost"));

        provider.ReleaseOne();
        var result = await pending;
        Assert.True(result.IsReady);
        Assert.True(engine.Healthy);
        Assert.Null(engine.SwitchingToward);
    }

    [Fact(Timeout = 60_000)]
    public async Task AClaimedSwitchIsInvisibleToDispatchAndOutlivesAStaleEndOfTheLastOne()
    {
        var provider = new ScriptedProvider { ProveHealth = false, GateLaunches = true };
        // No handler: the engine's own HTTP probes are refused, the way a stopped engine answers.
        var engine = NewEngine(Def(), provider);
        await using var _ = engine;

        var small = Profile("small", 1_024);
        var large = Profile("large", 2_048);

        var first = engine.WaitForProfileAsync("m", small, TimeSpan.FromSeconds(30), CancellationToken.None);
        await WaitUntil(() => provider.LaunchCount == 1);

        // While that switch is claimed, the snapshot already carries its marker and has stopped claiming
        // health — so the scheduler, which reads both from the snapshot, cannot route anything into an
        // engine that is about to be stopped. This is the ordering the transition gate exists for: the
        // marker and the unknown land in one publication, before the switch state is installed.
        Assert.Equal("m", engine.SwitchingToward);
        Assert.False(engine.Healthy);
        Assert.False(engine.IsReadyFor("m", null));

        var second = engine.WaitForProfileAsync("m", large, TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(150);
        provider.ReleaseOne();                                   // the first switch ends
        await WaitUntil(() => provider.LaunchCount == 2);        // the second waiter's own switch is in flight

        // A late EndSwitch for the finished attempt must not erase the successor's marker: it clears the
        // marker only while the marker still names its own switch.
        Assert.Equal("m", engine.SwitchingToward);
        Assert.False(engine.IsReadyFor("m", null));

        provider.ReleaseOne();
        var secondResult = await second;
        Assert.True(secondResult.IsReady);
        Assert.Null(engine.SwitchingToward);
        Assert.Equal(["m", "m"], provider.Launches);
    }
}
