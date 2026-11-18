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

        /// <summary>Status the *held* request answers with once it is released. It has to be separate from
        /// <see cref="ModelsStatus"/>, which is applied before the hold is armed: with one status for both, a
        /// held probe could only ever answer 200, which is why a straddling <b>failed</b> poll — the case the
        /// fenced negative publish exists for — could not be built at all.</summary>
        public int HeldProbeStatus { get; set; } = 200;

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
                    if (HeldProbeStatus != 200)
                        return new HttpResponseMessage((HttpStatusCode)HeldProbeStatus)
                        { Content = new StringContent("{}") };
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
            if (ThrowOnLaunch is { } boom) throw new InvalidOperationException(boom);
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

        /// <summary>When set, <see cref="LaunchAsync"/> throws this text instead of answering. The switch's
        /// catch-all exit needs a throw to pin, and the text is deliberately machine-flavoured: it is the kind
        /// of sentence a provider writes (a socket path) that must reach the feed and never the caller.</summary>
        public string? ThrowOnLaunch { get; set; }
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
            // An aliased catalog key: the switch target is the key, and the alias is only how a caller may
            // spell the request. The engine never reports the alias, and a test needs that distinction.
            ["aliasy"] = new() { Name = "aliasy", Aliases = ["aliasy-short"] },
        },
    };

    private static EngineRuntime NewEngine(
        EngineDefinition def, ScriptedProvider provider, ProbeHandler? handler = null, Action? hint = null,
        EventRing? feed = null) =>
        new(def, new RouterConfig { Engines = new Dictionary<string, EngineDefinition> { ["fake"] = def } },
            feed ?? new EventRing(), handler, lifecycle: provider, dispatchHint: hint);

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
    public async Task APollThatFailedBeforeASwitchCannotWithdrawWhatTheSwitchProved()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 50, TimeoutMs = 30_000 });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        using var loopCts = new CancellationTokenSource();
        engine.StartHealthLoop(loopCts.Token);
        await WaitUntil(() => engine.Healthy);

        // One /models round trip is in flight, and it is going to come back bad: 503 on release.
        handler.HoldNextProbe();
        handler.HeldProbeStatus = 503;
        await handler.EnteredAsync;
        Assert.True(engine.Healthy);

        // A switch proves the engine while that request is still on the wire.
        handler.SetModels("m", "extra");
        var switched = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(switched.IsReady);
        var proofVersion = engine.Capability.Version;

        // Stop the loop, then let the straddling poll fail. An unfenced negative would mark this engine unknown
        // for a whole poll interval: the scheduler skips an unhealthy engine, so a working engine loses its
        // model to a failure that happened before the proof, and the refusal loses its grounds as well.
        loopCts.Cancel();
        handler.ReleaseProbe();
        await handler.AnsweredAsync;
        await Task.Delay(300);

        Assert.True(engine.Healthy);
        Assert.Equal(proofVersion, engine.Capability.Version);   // the stale failure was dropped, not applied
        Assert.True(engine.IsReadyFor("extra", null));
    }

    [Fact(Timeout = 60_000)]
    public async Task APollThatCannotAskWhatIsLoadedStillMarksTheEngineUnknown()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 50, TimeoutMs = 30_000 });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        using var loopCts = new CancellationTokenSource();
        engine.StartHealthLoop(loopCts.Token);
        await WaitUntil(() => engine.Healthy);

        // Nothing else publishes here, so the fence must not become "the health loop can never report bad
        // news": it drops answers that are *stale*, not answers that are the freshest thing known.
        handler.ModelsStatus = 503;
        await WaitUntil(() => !engine.Healthy);
        loopCts.Cancel();

        Assert.Contains("m", engine.LoadedModels);      // kept as history, not as a claim
        Assert.False(engine.IsReadyFor("m", null));
    }

    [Fact(Timeout = 60_000)]
    public async Task AServingStateRuleThatFailsMovesNoHealthFlagAndStillFeedsTheGauges()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider();
        // /props answers, with an 8192 window; the configured rule asks for a window the engine cannot offer.
        var def = Def(new HealthProbeDefinition
        {
            Path = "/props", IntervalMs = 50, TimeoutMs = 30_000, Expect = "n_ctx >= 200000",
        });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        using var loopCts = new CancellationTokenSource();
        engine.StartHealthLoop(loopCts.Token);
        await WaitUntil(() => engine.ActiveContextWindow == 8192);
        await Task.Delay(250);      // several more ticks, so the discarded verdict is a pattern, not a fluke

        // Two different questions: Healthy says this engine's /models answered, and health.expect says whether
        // a *switch* to it counts as ready. Folding the rule's failure into health would make a passing
        // /models answer look like a passing readiness rule in one direction and a failing rule look like a
        // dead engine in the other. The gauges still move, because that is what the same payload is for.
        Assert.True(engine.Healthy);
        Assert.True(engine.IsReadyFor("m", null));
        loopCts.Cancel();
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

    /// <summary>§5.5, TASK-61 AC #8. One <c>Healthy</c> flag says "we do not know" but not <b>who</b> made us not
    /// know, and that difference is what decides whether a waiter keeps a proof its own switch published — the
    /// other half of the pair is pinned beside
    /// <c>SwitchClaimTests.AWaiterHoldingItsOwnProofIsNotQueuedBehindAnotherCallersClaim</c>. The provenance reads
    /// true only while the claim that raised the current marker authored the withdrawal, and a claim that started
    /// from an already-unknown engine authors nothing. A probe that cannot answer while a marker is up publishes
    /// nothing at all — the fenced negative needs health true to withdraw — so it neither clears the provenance nor
    /// moves the version. And a switch exit that supersedes the claim as the author is a <b>provenance-only</b>
    /// publication: health is already false, so the old code returned early and published nothing, leaving a reader
    /// unable to tell "the claim withdrew health" from "the transaction made the engine unknown".</summary>
    [Fact(Timeout = 60_000)]
    public async Task TheClaimThatWithdrewHealthIsTheOnlyAuthorOfUnknownHealth()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider { GateLaunches = true };
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 50, TimeoutMs = 5_000 });
        var engine = NewEngine(def, provider, handler);
        await using var _ = engine;

        using var loopCts = new CancellationTokenSource();
        engine.StartHealthLoop(loopCts.Token);
        await WaitUntil(() => engine.Healthy);
        Assert.False(engine.Capability.HealthFromClaim);      // an answered probe authored no unknown health

        var switched = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        await WaitUntil(() => engine.SwitchingToward == "extra");
        Assert.False(engine.Healthy);
        Assert.True(engine.Capability.HealthFromClaim,
            "the claim raised the marker and withdrew the health in one publication, so it is the author");
        var claimVersion = engine.Capability.Version;

        // A probe that cannot answer while the marker is up is dropped by the fence, so it changes nothing — not
        // the provenance, not the version. The probe having *completed* is what is waited for: the loop is
        // sequential, so a second request starting proves the first one already answered and published (or did
        // not), which a bare delay could not show.
        handler.ModelsStatus = 503;
        var probesBefore = handler.ModelsRequests;
        await WaitUntil(() => handler.ModelsRequests > probesBefore + 1);
        Assert.True(engine.Capability.HealthFromClaim);
        Assert.Equal(claimVersion, engine.Capability.Version);

        // The launch fails after the engine was stopped. The exit supersedes the claim as author, which is a
        // provenance-only change over health that is already false — and it still has to advance the version.
        // Two publications land after the claim here: the exit's, and the marker clear. Under the early return
        // that the exit used to take on already-false health, only the marker clear ran: one publication, and the
        // snapshot at the end read the claim as the author of an unknown the transaction had created.
        provider.LaunchOutcome = LifecycleResult.Fail("launch_failed", "selector exited 1");
        provider.ReleaseOne();
        var answer = await switched;
        Assert.False(answer.IsReady);
        Assert.Null(engine.SwitchingToward);
        Assert.False(engine.Healthy);
        Assert.False(engine.Capability.HealthFromClaim,
            $"the snapshot at the failed exit still reads the claim as the author: {engine.Capability}");
        Assert.True(engine.Capability.Version >= claimVersion + 2,
            $"the exit published nothing of its own: version went {claimVersion} -> {engine.Capability.Version} " +
            "and only the marker clear moved it");

        // A claim that starts from an engine already unknown authors nothing. The marker goes up over an absence
        // of knowledge rather than over knowledge withdrawn, and a waiter must not read it as a transaction that
        // took the proof away. The launch answers and the refresh cannot, so this switch is refused (v0.12.31) and
        // the engine stays unknown either way. The gate is the observation: the claim publishes the marker before
        // the switch launches, and a gated launch cannot end, so reading the snapshot once the launch has begun
        // reads the claim itself rather than a window that may already have closed.
        var launchesBefore = provider.Launches.Count;
        var again = engine.WaitForModelAsync("second", TimeSpan.FromSeconds(30), CancellationToken.None);
        await WaitUntil(() => provider.Launches.Count > launchesBefore);
        Assert.Equal("second", engine.SwitchingToward);
        Assert.False(engine.Healthy);
        Assert.False(engine.Capability.HealthFromClaim,
            $"a claim over an already-unknown engine authored a withdrawal: {engine.Capability}");
        provider.ReleaseOne();
        Assert.False((await again).IsReady);

        // The engine answering again is a fresh author: health returns with no claim provenance at all.
        handler.ModelsStatus = 200;
        await WaitUntil(() => engine.Healthy);
        Assert.False(engine.Capability.HealthFromClaim);
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

    /// <summary>The other half of the trusting path: an engine that will not answer after the launch has not been
    /// verified, so the switch is refused rather than served on the exec's exit status. This is the test that used
    /// to pin the lenient answer — the switch completed, the request was served, and only the capability went
    /// unknown — and it moved with the owner's answer on the posture question (decision-27), not before it.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ASwitchTheEngineCannotConfirmAfterTheLaunchIsRefusedAndClaimsNothing()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider { ProveHealth = false };
        var feed = new EventRing();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler, feed: feed);
        await using var _ = engine;

        handler.SetModels("m", "extra");
        var first = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(first.IsReady);
        Assert.True(engine.Healthy);
        Assert.Contains("extra", engine.LoadedModels);

        // wait_health=false: the provider's exec says it launched, and the router asks what is loaded.
        // This time the engine will not answer. Nothing confirms the model, and an exit code is not a loaded
        // model: the switch is refused, and the old list stops being presented as current, because a model
        // loaded by hand or dropped by a restart would otherwise be claimed on evidence from before the switch.
        handler.ModelsStatus = 503;
        var second = await engine.WaitForModelAsync("second", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(second.IsReady);
        Assert.Contains("refresh_unanswered", second.Detail);      // the caller gets the category
        Assert.DoesNotContain("refresh_unanswered_second", second.Detail);   // the parameterised reason stays on the feed
        Assert.False(engine.Healthy);
        Assert.Null(engine.SwitchingToward);
        Assert.False(engine.IsReadyFor("second", null));
        Assert.Contains("extra", engine.LoadedModels);      // kept as history, not as a claim
        Assert.DoesNotContain("second", engine.LoadedModels);
        Assert.Equal(2, provider.LaunchCount);              // refused after the launch, never before it

        var refusal = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
        Assert.Contains("refresh_unanswered_second", refusal.Reason);
        Assert.Contains("did not answer /models", refusal.Reason);

        // Unknown is not an absence: refusing this switch must not turn into a claim that no engine can serve
        // a model, which is the mistake an accepted-gap refusal would make.
        ICapabilitySource[] sources = [engine];
        Assert.False(CapabilityScan.DefinitelyHopeless(sources, "ghost"));
    }

    /// <summary>The positive half of the path that trusts the exec: a refresh that answers *and* names the
    /// model is still a proof, and the switch is still ready. Without this control, a fix to the omission bug
    /// below could pass by moving the whole path instead of the hole in it.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ARefreshThatNamesTheTargetOnTheTrustingPathStillProvesTheSwitch()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider { ProveHealth = false };
        var feed = new EventRing();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler, feed: feed);
        await using var _ = engine;

        handler.SetModels("m", "extra");
        var switched = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(switched.IsReady);
        Assert.True(engine.Healthy);                        // the answered refresh is the evidence
        Assert.True(engine.IsReadyFor("extra", null));
        Assert.Equal(1, provider.LaunchCount);
        var kinds = feed.Replay(0).Select(e => e.Kind).ToArray();
        Assert.Contains("engine.switch_completed", kinds);
        Assert.DoesNotContain("engine.switch_failed", kinds);
    }

    /// <summary>The bug: on the <c>wait_health = false</c> path an answered refresh was published as a proof,
    /// and the switch ended ready whatever the list named. The router could vouch for a model nothing had ever
    /// reported, with its own say-so as the only evidence (§5.5).</summary>
    [Fact(Timeout = 60_000)]
    public async Task ARefreshThatNeverNamedTheRequestedModelRefusesTheSwitch()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider { ProveHealth = false };
        var feed = new EventRing();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler, feed: feed);
        await using var _ = engine;

        handler.SetModels("m", "extra");
        var first = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(first.IsReady);

        // The engine answers — it is reachable and talking — it just never names "second".
        handler.SetModels("m", "extra", "stranger");
        var failed = await engine.WaitForModelAsync("second", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(failed.IsReady);
        Assert.False(engine.Healthy);                       // relaunched: what it reported before is not current
        Assert.Null(engine.SwitchingToward);
        Assert.False(engine.IsReadyFor("second", null));
        Assert.Equal(2, provider.LaunchCount);              // refused after the launch, never before it

        // Ordering evidence, not a health assertion: the refused switch's list never became a claim, so the
        // membership check has to have run *before* the proof published. A check after PublishProof would
        // leave "stranger" presented as something this engine holds.
        Assert.DoesNotContain("stranger", engine.LoadedModels);
        Assert.Contains("extra", engine.LoadedModels);      // kept as history, not as a claim

        // The reason carries the actionable part: which name is missing and what the engine actually said.
        var events = feed.Replay(0);
        Assert.Equal("engine.switch_failed", events[^1].Kind);
        var refusal = events[^1];
        Assert.Contains("refresh_omits_second", refusal.Reason);
        Assert.Contains("stranger", refusal.Reason);
        Assert.Single(events, e => e.Kind == "engine.switch_completed");   // only the first switch completed
    }

    /// <summary>The refusal reason is bounded in **length** as well as in name count. A cap on how many names are
    /// quoted is not a cap when each name is a hundred-character provider spelling: the engine then decides how
    /// long the router's feed record is, and the event ring caps entries rather than characters. What survives the
    /// cut is the part an operator acts on — the head of each name, and the count of what was left out (§5.5).</summary>
    [Fact(Timeout = 60_000)]
    public async Task ARefusalReasonStaysBoundedWhenTheEngineReportsManyLongNames()
    {
        var longName = "qwen2.5-coder-32b-instruct-q5_k_m-ud-Q4_K_M-unsloth-bnb-4bit-GGUF-provider-side-spelling";
        var reported = new List<string> { longName };
        for (var i = 1; i <= 29; i++) reported.Add($"bulk-model-{i}");

        using var handler = new ProbeHandler(reported.ToArray());
        var provider = new ScriptedProvider { ProveHealth = false };
        var feed = new EventRing();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler, feed: feed);
        await using var _ = engine;

        var failed = await engine.WaitForModelAsync("second", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(failed.IsReady);

        var refusal = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
        Assert.NotNull(refusal.Reason);
        Assert.Contains("refresh_omits_second", refusal.Reason);
        Assert.Contains("(+22 more)", refusal.Reason);       // thirty reported, eight quoted, the rest counted
        Assert.DoesNotContain(longName, refusal.Reason);     // the long name is not quoted whole
        Assert.Contains(longName[..24], refusal.Reason);     // its head is, with the true length beside it
        Assert.Contains($"(+{longName.Length - 24}ch #", refusal.Reason);
        Assert.True(refusal.Reason!.Length < 900,
            $"a refusal reason of {refusal.Reason!.Length} characters is not a bounded one");
    }

    /// <summary>What namespace the omission rule reads: the engine's reported names and the catalog's model
    /// keys are one namespace compared case-insensitively — so a case difference is not a refusal — while a
    /// catalog **alias** is the router's request-side spelling and never satisfies the check (§5.5).</summary>
    [Fact(Timeout = 60_000)]
    public async Task TheReportedNamespaceFoldsCaseButNotAliases()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider { ProveHealth = false };
        var feed = new EventRing();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler, feed: feed);
        await using var _ = engine;

        handler.SetModels("EXTRA");
        var upper = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(upper.IsReady);                         // the same namespace, different case
        Assert.True(engine.IsReadyFor("extra", null));

        // An engine that reports only the alias is a config mistake, not a name the router may invent: the
        // fix is the catalog key, and refusing says so instead of quietly matching on the alias.
        handler.SetModels("aliasy-short");
        var failed = await engine.WaitForModelAsync("aliasy", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(failed.IsReady);
        Assert.False(engine.Healthy);
        var refusal = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
        Assert.Contains("refresh_omits_aliasy", refusal.Reason);
        Assert.Contains("aliasy-short", refusal.Reason);    // the near-miss is what the operator has to see
    }

    /// <summary>The claim decides against the snapshot it publishes from, so a switch that is already proved —
    /// by the switch in front of this caller, or by a poll landing while this caller queued — is taken rather
    /// than launched again. Both orderings give the same answer, which is what makes the pin independent of
    /// thread scheduling; the old shape re-decided nothing and launched a second time.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ASecondCallerTakesTheProofInsteadOfClaimingASecondSwitch()
    {
        using var handler = new ProbeHandler("m");
        var provider = new ScriptedProvider { GateLaunches = true };
        var feed = new EventRing();
        var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
        var engine = NewEngine(def, provider, handler, feed: feed);
        await using var _ = engine;

        var first = engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None);
        await WaitUntil(() => provider.LaunchCount == 1);         // claimed, and held inside the launch

        // Stop the newcomer at the last point before it would take the switch lock.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.BeforeSwitchWaitForTests = () =>
        {
            entered.TrySetResult();
            go.Task.GetAwaiter().GetResult();
        };
        var newcomer = Task.Run(() => engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(30), CancellationToken.None));
        await entered.Task;

        handler.SetModels("m", "extra");                          // the engine now really does serve it
        provider.ReleaseOne();                                    // the switch in front settles …
        Assert.True((await first).IsReady);

        // …and only now does the newcomer reach the claim, so its decision is taken against an idle switch
        // state and a capability snapshot that already proves the model. That is the race the claim's
        // expected-version CAS exists for: the old shape decided nothing at this point and launched again.
        go.TrySetResult();

        Assert.True((await newcomer).IsReady);
        Assert.Equal(1, provider.LaunchCount);                    // the newcomer never claimed a switch
        Assert.True(engine.IsReadyFor("extra", null));            // and it was right not to: the model is served
        Assert.Single(feed.Replay(0), e => e.Kind == "engine.switch_started" && e.Model == "extra");
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
        // The engine answers, but not with the model that was asked for — which, since decision-27, is a
        // refused switch. That is the shape this pin needs: an attempt that ends false without the router
        // stopping, so the waiting caller takes its own turn. (It used to get that from an engine that would
        // not answer at all, which now refuses too; what is under test here is the marker and the stale end,
        // not the refusal, so the answer is set only after the first attempt is out of the way.)
        using var handler = new ProbeHandler("extra");
        var engine = NewEngine(Def(), provider, handler);
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

        handler.SetModels("m");                                // this attempt can be confirmed
        provider.ReleaseOne();
        var secondResult = await second;
        Assert.True(secondResult.IsReady);
        Assert.Null(engine.SwitchingToward);
        Assert.Equal(["m", "m"], provider.Launches);
    }

    /// <summary>The caller-facing half of a refusal is one token from a named, closed set, at every exit. Each block
    /// reaches a different exit and reads back both halves: what the caller sees (<see cref="SwitchResult.Detail"/>)
    /// and what the operator sees (the <c>engine.switch_failed</c> reason). The split is the point — a provider's
    /// socket path, a readiness window, the names an engine actually reported, a window comparison — those are what
    /// an operator acts on, and none of them belongs in the message of whoever asked for the model (§5.5).</summary>
    [Fact(Timeout = 120_000)]
    public async Task ACallerIsToldACategoryNotAParameterAtEveryRefusalExit()
    {
        var categories = new List<string>();

        // 1. No select command for that model: the catalog was consulted and nothing was attempted.
        {
            using var handler = new ProbeHandler("m");
            var engine = NewEngine(Def(), new ScriptedProvider { ProveHealth = false }, handler);
            await using var _ = engine;
            var ghost = await engine.WaitForModelAsync("ghost", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(ghost.IsReady);
            categories.Add(CategoryOf(ghost));
            Assert.Equal(SwitchReason.NoSelectCommand, CategoryOf(ghost));
        }

        // 2. The provider refused the launch. Its own sentence names a socket path: that is the operator's half.
        {
            using var handler = new ProbeHandler("m");
            var provider = new ScriptedProvider
            {
                LaunchOutcome = LifecycleResult.Fail("selector exited 1", "connect refused: /run/arboreal/sockets/engine.sock"),
            };
            var feed = new EventRing();
            var engine = NewEngine(Def(), provider, handler, feed: feed);
            await using var _ = engine;
            var failed = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            categories.Add(CategoryOf(failed));
            Assert.Equal(SwitchReason.LaunchFailed, CategoryOf(failed));
            Assert.DoesNotContain("engine.sock", failed.Detail);
            Assert.DoesNotContain("selector exited 1", failed.Detail);
            var shown = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
            Assert.Contains("engine.sock", shown.Reason);           // greppable where the operator reads it
            Assert.Contains("selector exited 1", shown.Reason);
        }

        // 3. Readiness never arrived. The length of the wait is configuration, and the caller was never told it.
        {
            using var handler = new ProbeHandler("m");                // never names "extra"
            var provider = new ScriptedProvider { ReadinessMs = 300 };
            var feed = new EventRing();
            var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 });
            var engine = NewEngine(def, provider, handler, feed: feed);
            await using var _ = engine;
            var timedOut = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(timedOut.IsReady);
            categories.Add(CategoryOf(timedOut));
            Assert.Equal(SwitchReason.HealthTimeout, CategoryOf(timedOut));
            Assert.DoesNotContain("300", timedOut.Detail);
            var shown = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
            Assert.Contains("health_timeout after 300ms", shown.Reason);   // parameters stay here
        }

        // 4. The engine answered and named something else. Its words are the fix, and they are config-side.
        {
            using var handler = new ProbeHandler("m", "stranger");
            var provider = new ScriptedProvider { ProveHealth = false };
            var feed = new EventRing();
            var engine = NewEngine(Def(), provider, handler, feed: feed);
            await using var _ = engine;
            var failed = await engine.WaitForModelAsync("second", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            categories.Add(CategoryOf(failed));
            Assert.Equal(SwitchReason.RefreshOmits, CategoryOf(failed));
            Assert.DoesNotContain("stranger", failed.Detail);
            var shown = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
            Assert.Contains("refresh_omits_second", shown.Reason);
            Assert.Contains("stranger", shown.Reason);
        }

        // 5. The engine did not answer at all after the launch.
        {
            using var handler = new ProbeHandler("m") { ModelsStatus = 503 };
            var provider = new ScriptedProvider { ProveHealth = false };
            var engine = NewEngine(Def(), provider, handler);
            await using var _ = engine;
            var failed = await engine.WaitForModelAsync("second", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            categories.Add(CategoryOf(failed));
            Assert.Equal(SwitchReason.RefreshUnanswered, CategoryOf(failed));
        }

        // 6. The window the engine reports is smaller than the profile asked for. Both numbers are config-side.
        {
            using var handler = new ProbeHandler("m");                // answers, and names the target
            var provider = new ScriptedProvider { ProveHealth = false };
            var feed = new EventRing();
            var def = Def(new HealthProbeDefinition { Path = "/props", IntervalMs = 50, TimeoutMs = 5_000 });
            var engine = NewEngine(def, provider, handler, feed: feed);
            await using var _ = engine;
            using var loopCts = new CancellationTokenSource();
            engine.StartHealthLoop(loopCts.Token);
            await WaitUntil(() => engine.ActiveContextWindow == 8_192);   // the gauge is the router's evidence
            var failed = await engine.WaitForProfileAsync(
                "m", Profile("huge", 16_384), TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            categories.Add(CategoryOf(failed));
            Assert.Equal(SwitchReason.WindowTooSmall, CategoryOf(failed));
            Assert.DoesNotContain("16384", failed.Detail);
            Assert.DoesNotContain("8192", failed.Detail);
            var shown = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
            Assert.Contains("16384", shown.Reason);
            Assert.Contains("window_too_small", shown.Reason);        // the feed names the category as well
        }

        // 7. The attempt threw. Exception text is provider-visible and never travels to the caller.
        {
            using var handler = new ProbeHandler("m");
            var provider = new ScriptedProvider
            {
                ProveHealth = false,
                ThrowOnLaunch = "connect refused: /run/arboreal/sockets/engine.sock",
            };
            var feed = new EventRing();
            var engine = NewEngine(Def(), provider, handler, feed: feed);
            await using var _ = engine;
            var failed = await engine.WaitForModelAsync("extra", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(failed.IsReady);
            categories.Add(CategoryOf(failed));
            Assert.Equal(SwitchReason.SwitchError, CategoryOf(failed));
            Assert.DoesNotContain("engine.sock", failed.Detail);
            Assert.Contains("engine.sock", feed.Replay(0).Last(e => e.Kind == "engine.switch_failed").Reason);
        }

        // Seven exits, seven distinct tokens, and every one of them a member of the set named in the source.
        Assert.Equal(7, categories.Distinct(StringComparer.Ordinal).Count());
        string[] named =
        [
            SwitchReason.NoSelectCommand, SwitchReason.LaunchFailed, SwitchReason.HealthTimeout,
            SwitchReason.RefreshOmits, SwitchReason.RefreshUnanswered, SwitchReason.WindowTooSmall,
            SwitchReason.SwitchError,
        ];
        Assert.All(categories, category => Assert.Contains(category, named));
    }

    /// <summary>The token is the tail of the caller's sentence: <c>switch to &lt;their target&gt; failed:
    /// &lt;category&gt;</c>. Reading it back rather than matching a substring is what lets the pin above say the
    /// caller's message carries *only* the caller's own identifiers plus this.</summary>
    private static string CategoryOf(EngineRuntime.SwitchResult result)
    {
        Assert.NotNull(result.Detail);
        var cut = result.Detail!.LastIndexOf(": ", StringComparison.Ordinal);
        return cut < 0 ? result.Detail! : result.Detail![(cut + 2)..];
    }

    /// <summary>A caller-supplied model id is the caller's text, and nothing in the request path caps it. It reaches
    /// the feed twice — inside a reason sentence, and as the event's own <c>Model</c> field — so both are cut to the
    /// documented identifier length, with the marker that shows how much was cut. The id is still matched whole:
    /// the cap is on reporting, never on lookup (§5.5, §6.1).</summary>
    [Fact(Timeout = 60_000)]
    public async Task ACallerSuppliedModelIdIsCappedBeforeItReachesTheFeed()
    {
        var longId = "qwen/" + new string('x', 400);
        var def = new EngineDefinition
        {
            Name = "fake",
            Kind = EngineKind.OpenAiCompatible,
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Catalog = new Dictionary<string, EngineModelDefinition> { [longId] = new() { Name = longId } },
        };
        using var handler = new ProbeHandler("m");            // answers, and never names the long id
        var provider = new ScriptedProvider { ProveHealth = false };
        var feed = new EventRing();
        var config = new RouterConfig { Engines = new Dictionary<string, EngineDefinition> { ["fake"] = def } };
        var engine = new EngineRuntime(def, config, feed, handler, lifecycle: provider);
        await using var _ = engine;

        var failed = await engine.WaitForModelAsync(longId, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(failed.IsReady);

        var refusal = feed.Replay(0).Last(e => e.Kind == "engine.switch_failed");
        var head = WireText.IdentifierChars - WireText.MarkerReserve;   // what survives a cut at reporting length
        Assert.NotNull(refusal.Reason);
        Assert.Contains(longId[..head], refusal.Reason);       // the head is quoted…
        Assert.DoesNotContain(longId, refusal.Reason);        // …never whole…
        Assert.Contains("ch #", refusal.Reason);               // …and the reader sees that it was cut
        Assert.True(refusal.Reason!.Length <= WireText.ReasonChars,
            $"a feed reason of {refusal.Reason!.Length} characters exceeds the documented {WireText.ReasonChars}");

        Assert.NotNull(refusal.Model);
        Assert.DoesNotContain(longId, refusal.Model!);
        Assert.Contains(longId[..50], refusal.Model!);
        Assert.True(refusal.Model!.Length <= WireText.IdentifierChars + WireText.MarkerReserve,
            $"the event payload carries {refusal.Model!.Length} characters of model id");
    }
}
