using System.Collections.Concurrent;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>Outage containment for TASK-189: a named-tier request whose demand is already satisfied by the
/// resident engine must not start a lifecycle transaction. The evidence is the provider's call list — a stop,
/// a delete or a recreate is what kills the requests already on the only healthy engine — not the feed text.</summary>
public sealed class ResidentProfileNoOpTests
{
    private const string Model = "fixture-model";
    private const string WideTier = "wide-8";
    private const string NarrowTier = "narrow-4";
    private const long WideWindow = 262_144;
    private const int WideSlots = 8;

    // ── the compatibility predicate itself (§7.6) ────────────────────────────────

    private static ServingProfile Wide => new() { Name = WideTier, MaxContext = WideWindow, MaxSlots = WideSlots };

    private static ServingProfile Narrow => new() { Name = NarrowTier, MaxContext = WideWindow, MaxSlots = 4 };

    [Fact]
    public void AMeasuredWindowSatisfiesTheNamedTierWhateverLabelIsRecorded()
    {
        // The measured window bound to the model is the proof. The label the engine carries can be a stale
        // narrow name, or absent altogether when more than one declared tier matches the observed pair.
        Assert.True(new ServingCapacity(Model, WideWindow, WideSlots, "narrow-4").Matches(Model, Wide));
        Assert.True(new ServingCapacity(Model, WideWindow, WideSlots, null).Matches(Model, Wide));
        // Reported slots are a floor, not an identity: an eight-lane resident satisfies the four-lane tier of
        // the same window.
        Assert.True(new ServingCapacity(Model, WideWindow, WideSlots, "narrow-4").Matches(Model, Narrow));
    }

    [Fact]
    public void ALaneShortfallIsNotTheNamedTier()
    {
        // The same window with fewer reported lanes than the tier declares is a different capacity: the request
        // refuses instead of running the named tier short of the concurrency it promises.
        Assert.False(new ServingCapacity(Model, WideWindow, 1, WideTier).Matches(Model, Wide));
        Assert.False(new ServingCapacity(Model, WideWindow, 0, WideTier).Matches(Model, Wide));
    }

    [Fact]
    public void AMeasuredCapacityNeverRewritesANamedTier()
    {
        // A window measured narrower or wider than the tier declares, a window bound to another model row, an
        // unread gauge, and a tier that declares no window are each a mismatch.
        Assert.False(new ServingCapacity(Model, 256_000, WideSlots, WideTier).Matches(Model, Wide));
        Assert.False(new ServingCapacity(Model, 524_288, WideSlots, WideTier).Matches(Model, Wide));
        Assert.False(new ServingCapacity("other-model", WideWindow, WideSlots, WideTier).Matches(Model, Wide));
        Assert.False(new ServingCapacity(Model, 0, WideSlots, WideTier).Matches(Model, Wide));
        Assert.False(new ServingCapacity(Model, WideWindow, WideSlots, WideTier).Matches(Model,
            new ServingProfile { Name = "undeclared", MaxContext = 0, MaxSlots = WideSlots }));
    }

    [Fact]
    public void ALaunchedTierIsPublishedOnlyAtTheObservedLaneCount()
    {
        // The other half of the pair (§5.3.1): publication asks whether the launched instance is the tier that
        // was asked for, so extra lanes are a mismatch exactly as missing lanes are. The same observation
        // satisfies the resident floor, which is the whole difference between the two predicates.
        Assert.False(new ServingCapacity(Model, WideWindow, WideSlots, WideTier).MatchesExactly(Model, Narrow));
        Assert.True(new ServingCapacity(Model, WideWindow, Narrow.MaxSlots, NarrowTier).MatchesExactly(Model, Narrow));
        Assert.True(new ServingCapacity(Model, WideWindow, WideSlots, WideTier).MatchesExactly(Model, Wide));
        Assert.False(new ServingCapacity(Model, WideWindow, 1, WideTier).MatchesExactly(Model, Wide));
        Assert.False(new ServingCapacity("other-model", WideWindow, Narrow.MaxSlots, NarrowTier)
            .MatchesExactly(Model, Narrow));
        Assert.False(new ServingCapacity(Model, 0, Narrow.MaxSlots, NarrowTier).MatchesExactly(Model, Narrow));
        Assert.False(new ServingCapacity(Model, WideWindow, Narrow.MaxSlots, NarrowTier).MatchesExactly(Model,
            new ServingProfile { Name = "undeclared", MaxContext = 0, MaxSlots = Narrow.MaxSlots }));
        Assert.True(new ServingCapacity(Model, WideWindow, WideSlots, "narrow-4").Matches(Model, Narrow));
    }

    // ── the pair that proves direction, at the lifecycle seam ────────────────────

    [Fact(Timeout = 30_000)]
    public async Task AResidentAlreadyAtTheNamedTierCapacityTakesNoLifecycleCall()
    {
        using var engine = new FakeEngineHandler { LoadedModels = [Model], NCtx = WideWindow, TotalSlots = WideSlots };
        var provider = new LifecycleRecorder();
        await using var router = Router(engine, provider, autoSwitch: true);
        router.Start();
        await WaitServingAsync(router, WideWindow, WideSlots);

        // The resident's inferred label is ambiguous here (two tiers declare this capacity), so the recorded
        // label names nothing; the measured window and slots are the only evidence the router holds.
        Assert.Null(router.Engines["strata"].ActiveProfile);
        Assert.Equal(WideWindow, router.Engines["strata"].ActiveContextWindow);
        Assert.Equal(WideSlots, router.Engines["strata"].ActiveSlots);

        var ticket = await router.EnqueueAsync("t-wide", Request("hello-wide", WideTier));
        RouterRequestException? refusal = null;
        byte[]? body = null;
        try { body = await ticket.CompletionTask.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (RouterRequestException error) { refusal = error; }

        // The defect: naming a tier the resident already serves stopped and relaunched the container. The
        // counted provider calls are the evidence, not the feed text.
        Assert.Empty(provider.Calls.ToArray());
        Assert.Null(refusal);
        Assert.Contains("pong", System.Text.Encoding.UTF8.GetString(body!));
        Assert.Equal(1, engine.CompletionCalls);
        Assert.Equal(TicketState.Completed, ticket.State);
        var events = router.Events.Replay(0);
        Assert.DoesNotContain(events, e => e.Kind is "engine.switch_started" or "engine.switch_completed");
    }

    [Fact(Timeout = 30_000)]
    public async Task AHandReplacedNarrowEngineNeverTakesTheNoOpPathOnAStaleWideGauge()
    {
        var windowFile = Path.Combine(Path.GetTempPath(), $"air-resident-noop-{Guid.NewGuid():N}.txt");
        File.WriteAllText(windowFile, WideWindow.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            using var engine = new FakeEngineHandler { LoadedModels = [Model], NCtxFile = windowFile, TotalSlots = WideSlots };
            var provider = new LifecycleRecorder();
            await using var router = Router(engine, provider, autoSwitch: true);
            router.Start();
            await WaitServingAsync(router, WideWindow, WideSlots);

            // The container under the router's feet is replaced by a narrower one; the wide gauge the router
            // already read is now stale history about a process that no longer exists. The fresh measurement
            // is what the no-op decision reads, and a 256000 window is not the 262144 tier whatever the label
            // or the slot count claims.
            File.WriteAllText(windowFile, "256000");
            engine.TotalSlots = 4;
            await WaitServingAsync(router, 256_000, 4);

            var ticket = await router.EnqueueAsync("t-oversized", Request("hello-narrow", WideTier,
                requiredContext: WideWindow));
            var failure = Assert.IsType<RouterRequestException>(
                await Record.ExceptionAsync(() => ticket.CompletionTask.WaitAsync(TimeSpan.FromSeconds(15))));

            // No oversized admission: nothing was forwarded on an engine that cannot hold the window, and the
            // ticket settled with a refusal rather than a completion.
            Assert.Equal(0, engine.CompletionCalls);
            Assert.Empty(engine.ExecutionOrder);
            Assert.Equal(TicketState.Failed, ticket.State);
            Assert.Equal("switch_failed", failure.Code);
            Assert.NotEqual(WideWindow, router.Engines["strata"].ActiveContextWindow);
        }
        finally
        {
            File.Delete(windowFile);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ALaunchThatObservedExtraLanesForTheNamedTierPublishesNothing()
    {
        // A cold engine: nothing is loaded, so the named-tier request owes a lifecycle transaction. The
        // instance the launch starts answers healthy at the wide window with EIGHT reported lanes, while the
        // request names narrow-4 — same window, four declared lanes. The satisfaction floor says the demand is
        // met; the launch proof must not, or the router would certify as the four-lane tier an instance that
        // was not started as one (§5.3.1).
        using var engine = new FakeEngineHandler { LoadedModels = [], NCtx = WideWindow, TotalSlots = WideSlots };
        var provider = new LifecycleRecorder { OnLaunch = () => engine.LoadedModels = [Model] };
        await using var router = Router(engine, provider, autoSwitch: true);
        router.Start();
        await WaitHealthyAsync(router);

        var ticket = await router.EnqueueAsync("t-named", Request("hello-named", NarrowTier));
        RouterRequestException? refusal = null;
        byte[]? body = null;
        try { body = await ticket.CompletionTask.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (RouterRequestException error) { refusal = error; }

        // The launch really ran: this is the publication gate, not the resident no-op path above.
        Assert.Equal(1, provider.Calls.Count(call => call == "launch"));

        // Nothing was published from an instance that is not the named tier: no Ready verdict, no completion
        // event, and not one byte forwarded.
        Assert.Null(body);
        Assert.NotNull(refusal);
        Assert.Equal("switch_failed", refusal!.Code);
        Assert.Equal(TicketState.Failed, ticket.State);
        Assert.Equal(0, engine.CompletionCalls);
        var events = router.Events.Replay(0);
        Assert.DoesNotContain(events, e => e.Kind == "engine.switch_completed");
        Assert.Contains(events, e => e.Kind == "engine.switch_failed");
    }

    // ── fixture ─────────────────────────────────────────────────────────────────

    private static RouterCore Router(FakeEngineHandler engine, LifecycleRecorder provider, bool autoSwitch)
    {
        var config = new RouterConfig
        {
            AutoSwitch = autoSwitch,
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 8, CharsPerTokenEstimate = 4.0 },
            HttpHandlerFactory = _ => engine,
        };
        var definition = new EngineDefinition
        {
            Name = "strata",
            Kind = EngineKind.OpenAiCompatible,
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Lifecycle = EngineLifecycleKind.Docker,
            LifecycleOwner = EngineLifecycleOwner.Router,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" },
            Health = new HealthProbeDefinition { Path = "/props", IntervalMs = 50 },
            Catalog = new Dictionary<string, EngineModelDefinition>
            {
                [Model] = new EngineModelDefinition
                {
                    Name = Model,
                    Profile = WideTier,
                    // Two spellings of the wide capacity plus a narrow one: the measured 262144/8 pair matches
                    // more than one tier, so no unique profile label can be inferred from it.
                    ServingProfiles =
                    [
                        new ServingProfile { Name = WideTier, MaxContext = WideWindow, MaxSlots = WideSlots },
                        new ServingProfile { Name = "wide-8-alt", MaxContext = WideWindow, MaxSlots = WideSlots },
                        new ServingProfile { Name = "narrow-4", MaxContext = WideWindow, MaxSlots = 4 },
                    ],
                },
            },
        };
        config.Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal) { [definition.Name] = definition };
        return new RouterCore(config, scheduler: null, events: new EventRing(), clock: TimeProvider.System,
            lifecycleFactory: _ => provider);
    }

    private static InferenceRequest Request(string text, string profile, long? requiredContext = null) => new()
    {
        Model = Model,
        Body = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"model\":\"{Model}\",\"messages\":[{{\"role\":\"user\",\"content\":\"{text}\"}}]}}"),
        Options = new RouterRequestOptions { Profile = profile, RequiredContext = requiredContext },
    };

    private static async Task WaitHealthyAsync(RouterCore router)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        var engine = router.Engines["strata"];
        while (DateTimeOffset.UtcNow < deadline && !engine.Healthy) await Task.Delay(25);
        Assert.True(engine.Healthy);
        Assert.Empty(engine.Capability.LoadedModels);
    }

    private static async Task WaitServingAsync(RouterCore router, long window, int slots)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        var engine = router.Engines["strata"];
        while (DateTimeOffset.UtcNow < deadline &&
               (engine.ActiveContextWindow != window || engine.ActiveSlots != slots))
            await Task.Delay(25);
        Assert.Equal(window, engine.ActiveContextWindow);
        Assert.Equal(slots, engine.ActiveSlots);
        Assert.True(engine.Healthy);
    }

    /// <summary>Records every lifecycle operation the router asked for, so "no switch" is a counted fact.
    /// A stop, a remove or a create here is the two-minute outage the task is about.</summary>
    private sealed class LifecycleRecorder : IEngineLifecycleProvider
    {
        internal ConcurrentQueue<string> Calls { get; } = [];
        /// <summary>What the started container does to the fake engine, so a test can model an instance whose
        /// measured capacity is not the tier the launch asked for.</summary>
        internal Action? OnLaunch { get; init; }
        public string Kind => "docker";

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct) =>
            Result("preflight");

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct) =>
            Result("ownership", ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]));

        public ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers,
            CancellationToken ct) => Result("stop");

        public ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            OnLaunch?.Invoke();
            return Result("launch");
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.FromSeconds(5));

        private ValueTask<LifecycleResult> Result(string operation)
        {
            Calls.Enqueue(operation);
            return ValueTask.FromResult(LifecycleResult.Ok());
        }

        private ValueTask<IReadOnlyList<EnginePeer>> Result(string operation, ValueTask<IReadOnlyList<EnginePeer>> answer)
        {
            Calls.Enqueue(operation);
            return answer;
        }
    }
}
