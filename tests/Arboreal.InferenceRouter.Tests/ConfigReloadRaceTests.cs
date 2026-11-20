using System.Diagnostics;
using System.Net;

namespace Arboreal.InferenceRouter.Tests;

public sealed class ConfigReloadRaceTests
{
    private const string A = "default_model = \"a\"\n";
    private const string B = "default_model = \"b\"\n";
    private static readonly string One = ConfigReloadWireTests.External("one", "a", "b");
    private static readonly string[] CancelledFeed = ["thread.registered", "request.enqueued", "request.cancelled"];

    [Theory(Timeout = 20_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task QueueCutsUseTheTokenAndDeadlineWithoutHookDelivery(bool expiry, bool atExactTake)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        using var fake = new FakeEngineHandler { LoadedModels = ["a"] };
        await using var router = CreateRouter(A + One, _ => fake);
        await ConfigReloadWireTests.ProbeAllAsync(router, bound.Token);
        router.SuppressQueueHooksForTests = true;
        router.QueueTimestampForTests = Stopwatch.GetTimestamp();
        void Cut(RouterTicket _)
        {
            if (expiry) Interlocked.Add(ref router.QueueTimestampForTests, Stopwatch.Frequency * 2);
            else caller.Cancel();
        }
        if (atExactTake) router.BeforeAdmissionCommitForTests = Cut;
        else router.BeforeEnqueueEligibilityForTests = Cut;

        var ticket = await router.EnqueueAsync("new", Request(timeout: TimeSpan.FromSeconds(1)), caller.Token);
        Assert.False(router.TryDispatchOnce());
        var error = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(bound.Token));
        Assert.Equal(expiry ? "deadline_exceeded" : "cancelled", error.Code);
        Assert.Equal(0, fake.CompletionCalls);
        Assert.Equal(0, router.Engines["one"].Inflight);
        var events = router.Events.Replay(0).Where(evt => evt.ThreadId == "new" || evt.TicketId == ticket.Id);
        Assert.Equal(new[] { "thread.registered", "request.enqueued", expiry ? "request.failed" : "request.cancelled" },
            events.Select(evt => evt.Kind));
    }

    [Fact(Timeout = 20_000)]
    public async Task ReentrantRegistrationAndEnqueueKeepTicketFeedCausal()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fake = new FakeEngineHandler { LoadedModels = ["a"] };
        await using var router = CreateRouter(A + One, _ => fake);
        await ConfigReloadWireTests.ProbeAllAsync(router, bound.Token);
        var nested = new TaskCompletionSource<Task<RouterTicket>>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.Events.Subscribe(evt =>
        {
            if (evt.Kind == "thread.registered" && evt.ThreadId == "reentrant")
                nested.TrySetResult(router.EnqueueAsync("reentrant", Request()).AsTask());
            if (evt.Kind == "request.enqueued")
            {
                Assert.False(router.TryDispatchOnce());
                Assert.True(router.TryCancel(evt.TicketId!));
                throw new InvalidOperationException("observer only");
            }
        });
        var first = await router.EnqueueAsync("reentrant", Request(), bound.Token);
        var second = await (await nested.Task.WaitAsync(bound.Token));

        foreach (var ticket in new[] { first, second })
        {
            var error = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(bound.Token));
            Assert.Equal("cancelled", error.Code);
            var events = router.Events.Replay(0).Where(evt => evt.TicketId == ticket.Id || evt.Kind == "thread.registered");
            Assert.Equal(CancelledFeed, events.Select(evt => evt.Kind));
        }
        Assert.Equal(2, router.Events.SubscriberFaults);
        Assert.Equal(0, fake.CompletionCalls);
    }

    [Fact(Timeout = 20_000)]
    public async Task IndexedPendingTicketCannotDispatchBeforeItsAppendAndStillCountsAgainstTheQueue()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fake = new FakeEngineHandler { LoadedModels = ["a"] };
        var settings = A + "[scheduler]\nmax_queue_per_model = 1\n" + One;
        await using var router = CreateRouter(settings, _ => fake);
        await ConfigReloadWireTests.ProbeAllAsync(router, bound.Token);
        using var pause = new Cut();
        string? indexed = null;
        router.BeforeEnqueueFeedForTests = ticket =>
        {
            if (ticket.ThreadId != "pending") return;
            indexed = ticket.Id;
            pause.Hold();
        };
        var enqueue = Task.Run(async () => await router.EnqueueAsync("pending", Request(), bound.Token));
        try
        {
            await pause.Entered.Task.WaitAsync(bound.Token);
            Assert.Equal("Queued", router.TryGetTicket(indexed!)?.State);
            Assert.False(router.TryDispatchOnce());
            Assert.True(router.TryCancel(indexed!));
            var overflow = await router.EnqueueAsync("overflow", Request(), bound.Token);
            Assert.Equal("Failed", router.TryGetTicket(overflow.Id)?.State);
            var error = await Assert.ThrowsAsync<RouterRequestException>(() => overflow.CompletionTask);
            Assert.Equal("queue_overflow", error.Code);
            Assert.Single(router.Events.Replay(0), evt => evt.TicketId == overflow.Id && evt.Kind == "queue.rejected");
            Assert.DoesNotContain(router.Events.Replay(0), evt => evt.TicketId == indexed);
            Assert.DoesNotContain(router.Events.Replay(0), evt => evt.TicketId == overflow.Id && evt.Kind == "request.enqueued");
        }
        finally { pause.Release(); }
        var ticket = await enqueue.WaitAsync(bound.Token);
        await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask);
        Assert.Equal(CancelledFeed, router.Events.Replay(0).Where(evt => evt.ThreadId == "pending").Select(evt => evt.Kind));
        Assert.Equal(0, fake.CompletionCalls);
    }

    [Fact(Timeout = 20_000)]
    public async Task AStaleExactTakeDoesNotChargeOrRebindAnAcceptedTicket()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fake = new FakeEngineHandler { LoadedModels = ["a", "b"] };
        await using var router = CreateRouter(A + One, _ => fake);
        await ConfigReloadWireTests.ProbeAllAsync(router, bound.Token);
        var committed = await router.EnqueueAsync("old", Request(), bound.Token);
        using var pause = new Cut();
        router.BeforeAdmissionCommitForTests = _ => pause.Hold();
        var stale = Task.Run(router.TryDispatchOnce);
        try
        {
            await pause.Entered.Task.WaitAsync(bound.Token);
            Assert.Equal((ConfigReloadStatus.Applied, 2L), await ApplyAsync(router, B + One));
        }
        finally { pause.Release(); }

        Assert.True(await stale.WaitAsync(bound.Token));
        Assert.Equal(0, router.Engines["one"].Inflight);
        Assert.Equal(0, fake.CompletionCalls);
        Assert.Equal(TicketState.Queued, committed.State);
        Assert.Equal("a", committed.Model);
        router.BeforeAdmissionCommitForTests = null;
        Assert.True(router.TryDispatchOnce());
        await committed.CompletionTask.WaitAsync(bound.Token);
        Assert.Equal(1, fake.CompletionCalls);
        Assert.Single(router.Events.Replay(0), evt => evt.Kind == "request.selected" && evt.TicketId == committed.Id);
    }

    [Fact(Timeout = 20_000)]
    public async Task StaleEnqueueResolvesAgainBeforePublishingAnyTicket()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fake = new FakeEngineHandler { LoadedModels = ["a", "b"] };
        await using var router = CreateRouter(A + One, _ => fake);
        await ConfigReloadWireTests.ProbeAllAsync(router, bound.Token);
        using var pause = new Cut();
        var runs = 0;
        router.BeforeEnqueueCommitForTests = () =>
        {
            if (Interlocked.Increment(ref runs) == 1) pause.Hold();
        };
        var enqueue = Task.Run(async () => await router.EnqueueAsync("new", Request(), bound.Token));
        try
        {
            await pause.Entered.Task.WaitAsync(bound.Token);
            Assert.DoesNotContain(router.Events.Replay(0), evt => evt.ThreadId == "new");
            Assert.Equal((ConfigReloadStatus.Applied, 2L), await ApplyAsync(router, B + One));
        }
        finally { pause.Release(); }
        var ticket = await enqueue.WaitAsync(bound.Token);
        Assert.Equal("b", ticket.Model);
        Assert.Equal(2, runs);
        Assert.Single(router.Events.Replay(0), evt => evt.ThreadId == "new" && evt.Kind == "request.enqueued");
    }

    [Fact(Timeout = 20_000)]
    public async Task LateResponseKeepsPhysicalUseAfterTimeoutAndBlocksRemoval()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var late = new LateEngine { LoadedModels = ["a"] };
        using var other = new FakeEngineHandler { LoadedModels = ["b"] };
        var timeout = One.Replace("lifecycle_owner = ", "response_timeout_ms = 100\nlifecycle_owner = ", StringComparison.Ordinal);
        var two = ConfigReloadWireTests.External("two", "b");
        await using var router = CreateRouter(A + timeout + two, def => def.Name == "one" ? late : other);
        var clock = new ReadinessDeadlineTests.ManualClock();
        router.ResponseTimeoutClockForTests = clock;
        await ConfigReloadWireTests.ProbeAllAsync(router, bound.Token);

        var ticket = await router.EnqueueAsync("old", Request(), bound.Token);
        Assert.True(router.TryDispatchOnce());
        var engine = router.Engines["one"];
        var released = ConfigReloadWireTests.Gate();
        engine.AfterForwardReleaseForTests = () => released.TrySetResult();
        try
        {
            await late.Entered.Task.WaitAsync(bound.Token);
            clock.AdvanceTo(101);
            var error = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(bound.Token));
            Assert.Equal("engine_timeout", error.Code);
            await ConfigReloadWireTests.WaitUntilAsync(() => engine.Inflight == 0, bound.Token);
            Assert.Equal(1, engine.InferenceUses);
            Assert.Equal((ConfigReloadStatus.Busy, 1L), await ApplyAsync(router, B + two));

            late.Release.TrySetResult();
            await released.Task.WaitAsync(bound.Token);
            Assert.True(late.Disposed.Task.IsCompleted);
            Assert.Equal(0, engine.InferenceUses);
            Assert.Equal((ConfigReloadStatus.Applied, 2L), await ApplyAsync(router, B + two));
            Assert.False(engine.TryBeginForward(bound.Token));
        }
        finally { late.Release.TrySetResult(); }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualProbeAppendRefusesAllRemovalsUntilTheObserverReturns(bool exception)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stable = new FakeEngineHandler { LoadedModels = ["a"] };
        using var publishing = new FakeEngineHandler { LoadedModels = ["z"] };
        var earlier = ConfigReloadWireTests.External("drop-a", "a");
        var later = ConfigReloadWireTests.External("drop-z", "z");
        await using var router = CreateRouter(A + One + earlier + later, def => def.Name == "drop-z" ? publishing : stable);
        await ConfigReloadWireTests.ProbeAllAsync(router, bound.Token);
        var front = router.Engines["drop-a"];
        var back = router.Engines["drop-z"];
        using var pause = new Cut();
        router.Events.Subscribe(evt =>
        {
            if (evt.Engine == "drop-z" && evt.Kind == "engine.health") pause.Hold();
        });
        if (exception) publishing.ModelsStatus = 503;
        var probe = Task.Run(() => back.ProbeHealthOnceAsync(bound.Token));
        try
        {
            await pause.Entered.Task.WaitAsync(bound.Token);
            Assert.Equal((ConfigReloadStatus.Busy, 1L), await ApplyAsync(router, A + One));
            Assert.Same(front, router.Engines["drop-a"]);
            Assert.True(front.TryBeginForward(bound.Token));
            front.EndForward();
        }
        finally { pause.Release(); }

        Assert.True(await probe.WaitAsync(bound.Token));
        Assert.Equal((ConfigReloadStatus.Applied, 2L), await ApplyAsync(router, A + One));
        Assert.False(front.TryBeginForward(bound.Token));
        Assert.False(back.TryBeginForward(bound.Token));
    }

    [Fact(Timeout = 20_000)]
    public async Task RetiredProbeCannotTakeASecondHttpLeaseOrPublish()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var held = new HeldModelsEngine { LoadedModels = ["a"] };
        using var stable = new FakeEngineHandler { LoadedModels = ["a"] };
        await using var router = CreateRouter(A + One + ConfigReloadWireTests.External("drop", "a"),
            def => def.Name == "drop" ? held : stable);
        await ConfigReloadWireTests.ProbeAllAsync(router, bound.Token);
        var old = router.Engines["drop"];
        held.Hold = true;
        var probe = old.ProbeHealthOnceAsync(bound.Token);
        try
        {
            await held.Entered.Task.WaitAsync(bound.Token);
            Assert.Equal(1, old.HttpUsesForTests);
            Assert.Equal((ConfigReloadStatus.Applied, 2L), await ApplyAsync(router, A + One));
            var cursor = router.Events.CurrentSeq;
            var props = held.Props;
            var capability = old.Capability;
            held.Release.TrySetResult();
            Assert.False(await probe.WaitAsync(bound.Token));
            Assert.Same(capability, old.Capability);
            Assert.Equal(props, held.Props);
            Assert.Equal(0, old.HttpUsesForTests);
            Assert.DoesNotContain(router.Events.Replay(cursor), evt => evt.Engine == "drop");
        }
        finally { held.Release.TrySetResult(); }
    }

    [Fact(Timeout = 20_000)]
    public async Task LostExternalDefaultWitnessRefusesBeforeFencingAnyRuntime()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var witness = new FakeEngineHandler { LoadedModels = ["a", "live"] };
        using var other = new FakeEngineHandler { LoadedModels = ["a"] };
        var drop = ConfigReloadWireTests.External("drop", "a");
        await using var router = CreateRouter(A + One + drop, def => def.Name == "one" ? witness : other);
        await ConfigReloadWireTests.ProbeAllAsync(router, bound.Token);
        var removed = router.Engines["drop"];
        using var pause = new Cut();
        router.BeforeReloadCommitForTests = pause.Hold;
        var candidate = "default_model = \"live\"\n" + One;
        var apply = Task.Run(() => ApplyAsync(router, candidate));
        try
        {
            await pause.Entered.Task.WaitAsync(bound.Token);
            witness.ModelsStatus = 503;
            await router.Engines["one"].ProbeHealthOnceAsync(bound.Token);
        }
        finally { pause.Release(); }

        Assert.Equal((ConfigReloadStatus.Invalid, 1L), await apply.WaitAsync(bound.Token));
        Assert.Same(removed, router.Engines["drop"]);
        Assert.True(removed.TryBeginForward(bound.Token));
        removed.EndForward();
        router.BeforeReloadCommitForTests = null;
        witness.ModelsStatus = 200;
        await router.Engines["one"].ProbeHealthOnceAsync(bound.Token);
        Assert.Equal((ConfigReloadStatus.Applied, 2L), await ApplyAsync(router, candidate));
    }

    [Fact(Timeout = 20_000)]
    public async Task ClosingRejectsAcceptanceBeforeTheShutdownTokenFires()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fake = new FakeEngineHandler { LoadedModels = ["a", "b"] };
        await using var router = CreateRouter(A + One, _ => fake);
        await ConfigReloadWireTests.ProbeAllAsync(router, bound.Token);
        var ticket = await router.EnqueueAsync("old", Request(), bound.Token);
        using var selection = new Cut();
        using var closing = new Cut();
        router.BeforeAdmissionCommitForTests = _ => selection.Hold();
        router.BeforeShutdownCancellationForTests = closing.Hold;
        var dispatch = Task.Run(router.TryDispatchOnce);
        await selection.Entered.Task.WaitAsync(bound.Token);
        var shutdown = Task.Run(async () => await router.DisposeAsync());
        try
        {
            await closing.Entered.Task.WaitAsync(bound.Token);
            Assert.False(router.ShutdownToken.IsCancellationRequested);
            await Assert.ThrowsAsync<RouterRequestException>(async () => await router.EnqueueAsync("new", Request()));
            Assert.Null(router.AcceptEngineAdmin("one"));
            Assert.Equal((ConfigReloadStatus.Closing, 1L), await ApplyAsync(router, B + One));
            selection.Release();
            Assert.False(await dispatch.WaitAsync(bound.Token));
            Assert.DoesNotContain(router.Events.Replay(0), evt => evt.Kind == "request.selected");
            Assert.Equal(0, router.Engines["one"].Inflight);
        }
        finally
        {
            selection.Release();
            closing.Release();
        }
        await shutdown.WaitAsync(bound.Token);
        var error = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask);
        Assert.Equal("cancelled", error.Code);
    }

    [Fact(Timeout = 20_000)]
    public async Task RegistrationObserverDoesNotOwnHousekeepingOrPreventProgressToTheRingBoundary()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fake = new FakeEngineHandler { LoadedModels = ["a"] };
        await using var router = CreateRouter(A + One, _ => fake);
        using var observer = new Cut();
        var reachedRing = ConfigReloadWireTests.Gate();
        var housekeepingAtObserver = new TaskCompletionSource<Task?>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.Events.Subscribe(evt =>
        {
            if (evt.Kind != "engine.registered" || evt.Engine != "two") return;
            housekeepingAtObserver.TrySetResult(router.ReloadHousekeepingForTests);
            observer.Hold();
        });
        router.BeforeShutdownUnsubscribeForTests = () => reachedRing.TrySetResult();
        Assert.Equal((ConfigReloadStatus.Applied, 2L),
            await ApplyAsync(router, A + One + ConfigReloadWireTests.External("two", "b")));
        var added = router.Engines["two"];
        var shutdown = Task.CompletedTask;
        try
        {
            await observer.Entered.Task.WaitAsync(bound.Token);
            Assert.Null(await housekeepingAtObserver.Task.WaitAsync(bound.Token));
            Assert.False(added.Healthy);
            shutdown = Task.Run(async () => await router.DisposeAsync());
            await reachedRing.Task.WaitAsync(bound.Token);
        }
        finally { observer.Release(); }
        await shutdown.WaitAsync(bound.Token);
        Assert.False(added.TryBeginForward(bound.Token));
    }

    [Fact(Timeout = 20_000)]
    public async Task DisconnectBeforeAndAfterCommitRespectsTopologyOwnership()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var before = new CancellationTokenSource();
        using var after = new CancellationTokenSource();
        using var fake = new FakeEngineHandler { LoadedModels = ["a"] };
        await using var router = CreateRouter(A + One, _ => fake);
        var candidate = RouterConfigLoader.LoadForReload(A + One + ConfigReloadWireTests.External("two", "b"), "fixture.toml");
        router.BeforeReloadCommitForTests = before.Cancel;
        Assert.Equal((ConfigReloadStatus.Cancelled, 1L),
            await router.ApplyConfigurationAsync(candidate, 1, before.Token));
        Assert.False(router.Engines.ContainsKey("two"));
        Assert.DoesNotContain(router.Events.Replay(0), evt => evt.Engine == "two");

        router.BeforeReloadCommitForTests = null;
        var registered = ConfigReloadWireTests.Gate();
        router.Events.Subscribe(evt =>
        {
            if (evt.Engine == "two" && evt.Kind == "engine.registered") registered.TrySetResult();
        });
        router.BeforeReloadActivationHandoffForTests = after.Cancel;
        Assert.Equal((ConfigReloadStatus.Applied, 2L),
            await router.ApplyConfigurationAsync(candidate, 1, after.Token));
        Assert.True(after.IsCancellationRequested);
        Assert.True(router.Engines.ContainsKey("two"));
        await registered.Task.WaitAsync(bound.Token);
    }

    [Fact(Timeout = 20_000)]
    public async Task SchedulingFallbackReleasesRegistrationAndPreservesAcceptedGeneration()
    {
        using var fake = new FakeEngineHandler();
        await using var router = CreateRouter(A + One, _ => fake);
        router.BeforeReloadActivationHandoffForTests = () => throw new InvalidOperationException("scheduling fixture");
        Assert.Equal((ConfigReloadStatus.Applied, 2L),
            await ApplyAsync(router, A + One + ConfigReloadWireTests.External("two", "b")));
        Assert.Null(router.ReloadHousekeepingForTests);
        Assert.Equal(1, router.ReloadCleanupFaults);
        Assert.False(router.Engines["two"].Healthy);
        Assert.DoesNotContain(router.Events.Replay(0), evt => evt.Engine == "two");
        Assert.Equal((ConfigReloadStatus.Applied, 3L), await ApplyAsync(router, A + One));
    }

    private static InferenceRequest Request(TimeSpan? timeout = null) => new()
    {
        Body = """{"prompt":"queued"}"""u8.ToArray(),
        Options = new RouterRequestOptions { MaxQueueTime = timeout },
    };

    private static RouterCore CreateRouter(string text, Func<EngineDefinition, HttpMessageHandler> handler)
    {
        var config = RouterConfigLoader.LoadForReload(text, "fixture.toml");
        config.Config.HttpHandlerFactory = handler;
        return new RouterCore(config.Config, null, null, TimeProvider.System, reload: config);
    }

    private static Task<(ConfigReloadStatus Status, long Generation)> ApplyAsync(RouterCore router, string text)
        => router.ApplyConfigurationAsync(RouterConfigLoader.LoadForReload(text, "fixture.toml"),
            router.CurrentGeneration.Number, CancellationToken.None);

    private sealed class Cut : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal TaskCompletionSource Entered { get; } = ConfigReloadWireTests.Gate();
        internal void Hold()
        {
            Entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(12))) throw new TimeoutException("fixture did not release");
        }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class HeldModelsEngine : FakeEngineHandler
    {
        internal bool Hold { get; set; }
        internal int Props { get; private set; }
        internal TaskCompletionSource Entered { get; } = ConfigReloadWireTests.Gate();
        internal TaskCompletionSource Release { get; } = ConfigReloadWireTests.Gate();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models", StringComparison.Ordinal) && Hold)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            if (path.EndsWith("/props", StringComparison.Ordinal)) Props++;
            return await base.SendAsync(request, ct);
        }
    }

    private sealed class LateEngine : FakeEngineHandler
    {
        internal TaskCompletionSource Entered { get; } = ConfigReloadWireTests.Gate();
        internal TaskCompletionSource Release { get; } = ConfigReloadWireTests.Gate();
        internal TaskCompletionSource Disposed { get; } = ConfigReloadWireTests.Gate();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/completions", StringComparison.Ordinal))
                return await base.SendAsync(request, ct);
            Entered.TrySetResult();
            await Release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new DisposalWitness(Disposed) };
        }

        private sealed class DisposalWitness(TaskCompletionSource signal) : StringContent("{}")
        {
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing) signal.TrySetResult();
            }
        }
    }
}
