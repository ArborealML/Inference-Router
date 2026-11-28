using System.Text;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class IndependentProgressTests
{
    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteCaptureSeesSameAndDifferentModelsWhileLocalWorkIsHeld(bool holdActivation)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var entered = NewBarrier();
        using var local = new FakeEngineHandler
        {
            LoadedModels = ["shared"],
            HoldCompletions = !holdActivation,
            AfterHeldCompletionEnteredForTests = () => entered.TrySetResult(),
        };
        using var remote = new FakeEngineHandler { LoadedModels = ["other"] };
        var provider = new LocalProvider(holdActivation ? entered : null);
        await using var router = MakeRouter(local, remote, provider, ["shared"], ["other"], maxSlots: 1);
        router.Start();
        var held = await router.EnqueueAsync("held", Request("shared", "held"), ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            remote.LoadedModels = ["shared", "other"];
            Assert.True(await router.Engines["remote"].ProbeHealthOnceAsync(ct));
            var same = await router.EnqueueAsync("same", Request("shared", "same"), ct);
            var different = await router.EnqueueAsync("different", Request("other", "different"), ct);
            await Task.WhenAll(same.CompletionTask, different.CompletionTask).WaitAsync(ct);

            Assert.Equal("remote", same.Engine);
            Assert.Equal("remote", different.Engine);
            Assert.Contains("same", remote.ExecutionOrder);
            Assert.Contains("different", remote.ExecutionOrder);
            Assert.Equal(2, remote.CompletionCalls);
            Assert.False(held.CompletionTask.IsCompleted);
        }
        finally
        {
            provider.Release();
            local.ReleaseGate();
        }
        await held.CompletionTask.WaitAsync(ct);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedDrainIsRetiredWhenItsTicketCancelsOrFindsARemoteRoute(bool cancel)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var heldEntered = NewBarrier();
        var followerEntered = NewBarrier();
        using var local = new FakeEngineHandler { LoadedModels = ["resident"], HoldCompletions = true, TotalSlots = 2 };
        local.AfterHeldCompletionEnteredForTests = () =>
        {
            if (local.CompletionCalls == 1) heldEntered.TrySetResult();
            else followerEntered.TrySetResult();
        };
        using var remote = new FakeEngineHandler { LoadedModels = ["other"] };
        var provider = new LocalProvider();
        await using var router = MakeRouter(local, remote, provider, ["resident", "cold"], ["cold", "other"], maxSlots: 2);
        var draining = NewBarrier();
        var blocked = NewBarrier();
        router.Events.Subscribe(entry =>
        {
            if (entry.Kind != "request.waiting") return;
            if (entry.ThreadId == "cold" && entry.Reason == "lifecycle_group_draining (private-gpu)")
                draining.TrySetResult();
            if (entry.ThreadId == "follower" && entry.Reason == "lifecycle_group_reserved (private-gpu)")
                blocked.TrySetResult();
        });
        router.Start();
        var held = await router.EnqueueAsync("held", Request("resident", "held"), ct);
        try
        {
            await heldEntered.Task.WaitAsync(ct);
            var cold = await router.EnqueueAsync("cold", Request("cold", "cold"), ct);
            await draining.Task.WaitAsync(ct);
            var wait = Assert.IsType<TicketSnapshot>(router.TryGetTicket(cold.Id));
            Assert.Equal("lifecycle_group_draining", wait.QueueWaitReason);
            Assert.Equal("private-gpu", wait.QueueWaitScope);
            var follower = await router.EnqueueAsync("follower", Request("resident", "follower"), ct);
            await blocked.Task.WaitAsync(ct);
            Assert.Equal(1, local.CompletionCalls);

            if (cancel)
            {
                Assert.True(router.TryCancel(cold.Id));
                var failure = await Assert.ThrowsAsync<RouterRequestException>(() => cold.CompletionTask.WaitAsync(ct));
                Assert.Equal("cancelled", failure.Code);
                Assert.Contains(router.Events.Replay(0), entry => entry.TicketId == cold.Id && entry.Kind == "request.cancelled");
            }
            else
            {
                remote.LoadedModels = ["cold", "other"];
                Assert.True(await router.Engines["remote"].ProbeHealthOnceAsync(ct));
                await cold.CompletionTask.WaitAsync(ct);
                Assert.Equal("remote", cold.Engine);
                Assert.Equal("cold", Assert.Single(remote.ExecutionOrder));
            }
            await followerEntered.Task.WaitAsync(ct);
            Assert.Equal("local", follower.Engine);
            Assert.Equal(1, provider.Launches);
            Assert.False(held.CompletionTask.IsCompleted);
            Assert.Null(router.TryGetTicket(cold.Id)?.QueueWaitReason);
        }
        finally { local.ReleaseGate(); }
        await held.CompletionTask.WaitAsync(ct);
    }

    [Fact(Timeout = 20_000)]
    public async Task AFullRemoteRouteDoesNotHideTheOtherReadyRoute()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var entered = NewBarrier();
        using var preferred = new FakeEngineHandler
        {
            LoadedModels = ["held", "shared"],
            HoldCompletions = true,
            AfterHeldCompletionEnteredForTests = () => entered.TrySetResult(),
        };
        using var alternate = new FakeEngineHandler { LoadedModels = ["shared", "other"] };
        await using var router = MakeRouter(preferred, alternate, null, ["held", "shared"], ["shared", "other"], maxSlots: 1);
        try
        {
            router.Start();
            var held = await router.EnqueueAsync("held", Request("held", "held"), ct);
            await entered.Task.WaitAsync(ct);
            // Hold the same accounting split as the existing pressure fixture so the full route
            // ranks first and the test exercises capacity fallback rather than simple load balancing.
            router.Engines["remote"].BeginRequest(10_000);
            router.Engines["remote"].EndRequest(0); // Keep busy score high at zero logical in-flight.
            try
            {
                var same = await router.EnqueueAsync("same", Request("shared", "same"), ct);
                var different = await router.EnqueueAsync("different", Request("other", "different"), ct);
                await Task.WhenAll(same.CompletionTask, different.CompletionTask).WaitAsync(ct);
                Assert.Equal("remote", same.Engine);
                Assert.Equal("remote", different.Engine);
                Assert.Equal(2, alternate.CompletionCalls);
                Assert.Contains(router.Events.Replay(0), entry => entry.TicketId == same.Id && entry.Kind == "request.selected" &&
                    entry.Reason == "engine_concurrency_limit (local)");
                Assert.False(held.CompletionTask.IsCompleted);
            }
            finally
            {
                router.Engines["remote"].BeginRequest(0);
                router.Engines["remote"].EndRequest(10_000);
                preferred.ReleaseGate();
            }
            await held.CompletionTask.WaitAsync(ct);
        }
        finally { preferred.ReleaseGate(); }
    }

    [Fact(Timeout = 20_000)]
    public async Task RouteLosingCapacityAtExactTakeUsesTheNextRouteOnlyOnce()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var first = new FakeEngineHandler { LoadedModels = ["shared"] };
        using var second = new FakeEngineHandler { LoadedModels = ["shared"] };
        await using var router = MakeRouter(first, second, null, ["shared"], ["shared"], maxSlots: 1);
        await ProbeAllAsync(router, ct);
        var ticket = await router.EnqueueAsync("caller", Request("shared", "original"), ct);
        var forced = false;
        router.BeforeAdmissionCommitForTests = _ =>
        {
            if (forced) return;
            forced = true;
            router.Engines["local"].BeginRequest(0);
        };
        try
        {
            Assert.True(router.TryDispatchOnce());
            await ticket.CompletionTask.WaitAsync(ct);
            Assert.Equal("remote", ticket.Engine);
            Assert.Equal("original", Assert.Single(second.ExecutionOrder));
            Assert.Equal(0, first.CompletionCalls);
            Assert.Single(router.Events.Replay(0), entry => entry.TicketId == ticket.Id && entry.Kind == "request.selected");
        }
        finally
        {
            if (forced) router.Engines["local"].EndRequest(0);
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task OneCapturedRoundPassesABlockedPrefixAndIgnoresNewArrivals()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var early = new FakeEngineHandler { LoadedModels = ["early"], NCtx = 128 };
        using var later = new FakeEngineHandler { LoadedModels = ["later"], NCtx = 128 };
        await using var router = MakeRouter(early, later, null, ["early"], ["later"], maxSlots: 1);
        await ProbeAllAsync(router, ct);
        var original = await router.EnqueueAsync("first", Request("early", "original"), ct);
        const int blockedCount = 40;
        for (var i = 0; i < blockedCount; i++)
            await router.EnqueueAsync("blocked-prefix", Request("later", "requires a larger window", context: 256), ct);
        var follower = await router.EnqueueAsync("independent", Request("later", "follower", context: 64), ct);
        RouterTicket? late = null;
        router.BeforeAdmissionCommitForTests = ticket =>
        {
            if (!ReferenceEquals(ticket, original)) return;
            late = EnqueueDuringCut(router, "first", Request("early", "late"), ct);
        };

        Assert.True(router.TryDispatchRound());
        await Task.WhenAll(original.CompletionTask, follower.CompletionTask).WaitAsync(ct);
        Assert.Equal("original", Assert.Single(early.ExecutionOrder));
        Assert.Equal("follower", Assert.Single(later.ExecutionOrder));
        Assert.Equal(TicketState.Queued, Assert.IsType<RouterTicket>(late).State);
        Assert.Equal(blockedCount, router.GetStats().QueueDepths["later"]);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactTakeRechecksANewOrNewlyAdmissibleEarlierCompetitor(bool releaseThread)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var one = new FakeEngineHandler { LoadedModels = ["model"] };
        using var two = new FakeEngineHandler { LoadedModels = [] };
        await using var router = MakeRouter(one, two, null, ["model"], [], maxSlots: 1);
        await ProbeAllAsync(router, ct);
        var earlier = router.RegisterThread("earlier", ThreadPriority.High());
        RouterTicket? predecessor = null;
        if (releaseThread)
        {
            earlier.IncrementInflight();
            predecessor = await router.EnqueueAsync(earlier.ThreadId, Request("model", "earlier"), ct);
        }
        var follower = await router.EnqueueAsync("follower", Request("model", "follower"), ct);
        router.BeforeAdmissionCommitForTests = ticket =>
        {
            Assert.Same(follower, ticket);
            if (releaseThread) earlier.DecrementInflight();
            else predecessor = EnqueueDuringCut(router, earlier.ThreadId, Request("model", "earlier"), ct);
        };

        Assert.False(router.TryDispatchRound());
        Assert.Equal(0, one.CompletionCalls);
        Assert.Equal(TicketState.Queued, follower.State);
        router.BeforeAdmissionCommitForTests = null;
        Assert.True(router.TryDispatchOnce());
        await Assert.IsType<RouterTicket>(predecessor).CompletionTask.WaitAsync(ct);
        Assert.Equal("earlier", Assert.Single(one.ExecutionOrder));
    }

    [Fact(Timeout = 20_000)]
    public async Task AnEarlierRouteBecomingReadyAfterTheProbeProtectsTheLastSharedThreadSlot()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var selected = new FakeEngineHandler { LoadedModels = ["second"] };
        using var other = new FakeEngineHandler { LoadedModels = [] };
        var clock = new ExactTakeClock();
        await using var router = MakeRouter(selected, other, null, ["second"], ["first"], maxSlots: 1, clock);
        await ProbeAllAsync(router, ct);
        var earlier = await router.EnqueueAsync("shared", Request("first", "earlier"), ct);
        var later = await router.EnqueueAsync("shared", Request("second", "later"), ct);
        using var cut = new ConfigReloadRaceTests.Cut();
        router.BeforeAdmissionCommitForTests = ticket =>
        {
            Assert.Same(later, ticket);
            clock.AtNextTimestamp = cut.Hold;
        };

        var dispatch = Task.Run(router.TryDispatchRound, ct);
        try
        {
            // TryTake timestamps the candidate after probing earlier contenders for this slot.
            await cut.Entered.Task.WaitAsync(ct);
            other.LoadedModels = ["first"];
            Assert.True(await router.Engines["remote"].ProbeHealthOnceAsync(ct));
            Assert.True(router.Engines["remote"].IsReadyFor("first", null));
        }
        finally { cut.Release(); }

        Assert.False(await dispatch.WaitAsync(ct));
        Assert.Equal(TicketState.Queued, later.State);
        Assert.Equal(0, selected.CompletionCalls);
        router.BeforeAdmissionCommitForTests = null;
        Assert.True(router.TryDispatchOnce());
        await earlier.CompletionTask.WaitAsync(ct);
        Assert.Equal("earlier", Assert.Single(other.ExecutionOrder));
    }

    private static RouterCore MakeRouter(FakeEngineHandler first, FakeEngineHandler second, LocalProvider? provider,
                                         string[] firstCatalog, string[] secondCatalog, int maxSlots, TimeProvider? clock = null)
    {
        var config = new RouterConfig
        {
            AutoSwitch = provider is not null,
            Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal)
            {
                ["local"] = Definition("local", firstCatalog, provider is not null),
                ["remote"] = Definition("remote", secondCatalog, managed: false),
            },
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = maxSlots, PrefillCostAlpha = 1 },
            HttpHandlerFactory = engine => engine.Name == "local" ? first : second,
        };
        return new RouterCore(config, scheduler: null, events: null, clock ?? TimeProvider.System,
            lifecycleFactory: provider is null ? null : _ => provider);
    }

    private static EngineDefinition Definition(string name, string[] catalog, bool managed) => new()
    {
        Name = name,
        Endpoint = managed ? new Uri("http://127.0.0.1:12000/v1") : new Uri($"http://{name}.invalid/v1"),
        Remote = !managed,
        Lifecycle = managed ? EngineLifecycleKind.Docker : EngineLifecycleKind.Delegated,
        LifecycleOwner = managed ? EngineLifecycleOwner.Router : null,
        LifecycleGroup = managed ? "private-gpu" : null,
        Docker = managed ? new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" } : null,
        Health = new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 },
        Catalog = catalog.ToDictionary(model => model, model => new EngineModelDefinition { Name = model }, StringComparer.Ordinal),
    };

    private static InferenceRequest Request(string model, string text, long? context = null) => new()
    {
        Model = model,
        Body = Encoding.UTF8.GetBytes($$"""{"model":"{{model}}","messages":[{"role":"user","content":"{{text}}"}]}"""),
        Options = new RouterRequestOptions { RequiredContext = context },
    };

    private static async Task ProbeAllAsync(RouterCore router, CancellationToken ct)
    {
        foreach (var engine in router.Engines.Values)
            Assert.True(await engine.ProbeHealthOnceAsync(ct));
    }

    private static RouterTicket EnqueueDuringCut(RouterCore router, string thread, InferenceRequest request, CancellationToken ct)
    {
        var enqueue = router.EnqueueAsync(thread, request, ct);
        if (!enqueue.IsCompletedSuccessfully) throw new InvalidOperationException("The enqueue must complete inside the dispatch cut.");
        return enqueue.Result;
    }

    private static TaskCompletionSource NewBarrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ExactTakeClock : TimeProvider
    {
        internal Action? AtNextTimestamp;

        public override long GetTimestamp()
        {
            Interlocked.Exchange(ref AtNextTimestamp, null)?.Invoke();
            return base.GetTimestamp();
        }
    }

    private sealed class LocalProvider(TaskCompletionSource? entered = null) : IEngineLifecycleProvider
    {
        private readonly TaskCompletionSource _release = NewBarrier();
        private int _launches;
        public string Kind => "docker";
        internal int Launches => Volatile.Read(ref _launches);
        internal void Release() => _release.TrySetResult();
        public async ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
        {
            if (entered is not null)
            {
                entered.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
            return LifecycleResult.Ok();
        }
        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);
        public ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct) =>
            ValueTask.FromResult(LifecycleResult.Ok());
        public ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            Interlocked.Increment(ref _launches);
            return ValueTask.FromResult(LifecycleResult.Ok());
        }
        public ReadinessPolicy Readiness(LifecycleTarget target) => new(true, TimeSpan.FromSeconds(5));
    }
}
