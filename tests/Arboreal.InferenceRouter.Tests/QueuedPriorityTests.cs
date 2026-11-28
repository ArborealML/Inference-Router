using System.Text;

namespace Arboreal.InferenceRouter.Tests;

public sealed class QueuedPriorityTests
{
    private static readonly string[] ExpectedPriorityOrder = ["first", "normal", "explicit", "second", "third"];

    [Fact(Timeout = 20_000)]
    public async Task PromotionAndDemotionKeepExplicitOverridesAndOriginalFifo()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var backend = new FakeEngineHandler { LoadedModels = ["model"], TotalSlots = 8 };
        await using var router = MakeRouter(backend);
        var engine = router.Engines["engine"];
        Assert.True(await engine.ProbeHealthOnceAsync(ct));
        var background = ThreadPriority.Background();
        var high = ThreadPriority.High(40);
        var thread = router.RegisterThread("changing", background, new() { Weight = 4, ModelAffinity = "model" });
        var unchanged = await router.EnqueueAsync("normal", Request("normal"), ct);
        var first = await router.EnqueueAsync(thread.ThreadId, Request("first"), ct);
        var second = await router.EnqueueAsync(thread.ThreadId, Request("second"), ct);
        var third = await router.EnqueueAsync(thread.ThreadId, Request("third"), ct);
        var explicitPriority = await router.EnqueueAsync(thread.ThreadId, Request("explicit", ThreadPriority.Low()), ct);
        var queued = new[] { unchanged, first, second, third, explicitPriority };
        var originalTimes = queued.Select(ticket => ticket.QueuedAt).ToArray();
        var notifications = new List<ThreadPriority>();
        thread.PriorityChanged += (_, priority) => notifications.Add(priority);

        Assert.Same(thread, router.RegisterThread(thread.ThreadId, high, new() { Weight = 1, ModelAffinity = "other" }));
        Assert.Equal(4, thread.MaxInflight);
        Assert.Equal("model", thread.ModelAffinity);
        await DispatchAsync(first);

        router.SetThreadPriority(thread.ThreadId, ThreadPriority.Interactive());
        thread.SetPriority(background);
        foreach (var ticket in new[] { unchanged, explicitPriority, second, third })
            await DispatchAsync(ticket);

        Assert.Equal(new[] { high, ThreadPriority.Interactive(), background }, notifications);
        Assert.Equal(new[] { (double)high.Class, (double)ThreadPriority.Interactive().Class, (double)background.Class },
            router.Events.Replay(0).Where(entry => entry.ThreadId == thread.ThreadId && entry.Kind == "thread.priority_changed")
                .Select(entry => entry.Value.GetValueOrDefault()));
        Assert.Equal(ExpectedPriorityOrder, backend.ExecutionOrder);
        Assert.Equal(originalTimes, queued.Select(ticket => ticket.QueuedAt));
        Assert.Equal(4, thread.MaxInflight);

        async Task DispatchAsync(RouterTicket expected)
        {
            // The completion waiter can resume before the previous execution releases its engine charge.
            await ConfigReloadWireTests.WaitUntilAsync(() => engine.Inflight == 0, ct);
            var before = router.Events.CurrentSeq;
            Assert.True(router.TryDispatchOnce());
            var selected = Assert.Single(router.Events.Replay(before), entry => entry.Kind == "request.selected");
            Assert.Equal(expected.Id, selected.TicketId);
            await expected.CompletionTask.WaitAsync(ct);
            Assert.Equal("engine", expected.Engine);
            Assert.Single(router.Events.Replay(0), entry => entry.TicketId == expected.Id && entry.Kind == "request.completed");
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task APreviousReleaseDuringManualSelectionReportsAStateChangeBeforeTheNextValidStep()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var backend = new FakeEngineHandler { LoadedModels = ["model"], TotalSlots = 8 };
        await using var router = MakeRouter(backend);
        var engine = router.Engines["engine"];
        Assert.True(await engine.ProbeHealthOnceAsync(ct));
        using var cleanup = new ConfigReloadRaceTests.Cut();
        using var selection = new ConfigReloadRaceTests.Cut();
        engine.AfterForwardReleaseForTests = cleanup.Hold;
        RouterTicket next;
        Task<bool> stale;
        try
        {
            var prior = await router.EnqueueAsync("prior", Request("prior"), ct);
            Assert.True(router.TryDispatchOnce());
            await prior.CompletionTask.WaitAsync(ct);
            await cleanup.Entered.Task.WaitAsync(ct);
            Assert.Equal(1, engine.Inflight);

            next = await router.EnqueueAsync("next", Request("next"), ct);
            router.BeforeAdmissionCommitForTests = _ => selection.Hold();
            stale = Task.Run(router.TryDispatchOnce, ct);
            await selection.Entered.Task.WaitAsync(ct);
            cleanup.Release();
            await ConfigReloadWireTests.WaitUntilAsync(() => engine.Inflight == 0, ct);
        }
        finally
        {
            engine.AfterForwardReleaseForTests = null;
            router.BeforeAdmissionCommitForTests = null;
            cleanup.Release();
            selection.Release();
        }

        Assert.True(await stale.WaitAsync(ct));
        Assert.Equal(TicketState.Queued, next.State);
        Assert.DoesNotContain(router.Events.Replay(0), entry => entry.TicketId == next.Id && entry.Kind == "request.selected");
        Assert.True(router.TryDispatchOnce());
        Assert.Single(router.Events.Replay(0), entry => entry.TicketId == next.Id && entry.Kind == "request.selected");
        await next.CompletionTask.WaitAsync(ct);
        Assert.Equal(2, backend.CompletionCalls);
    }

    [Fact(Timeout = 20_000)]
    public async Task AttachedPriorityChangePublishesAndWakesEvenWhenAThreadObserverThrows()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var backend = new FakeEngineHandler { LoadedModels = ["model"] };
        await using var router = MakeRouter(backend, maxSlots: 1);
        var engine = router.Engines["engine"];
        Assert.True(await engine.ProbeHealthOnceAsync(ct));
        var changed = router.RegisterThread("changing", ThreadPriority.Low());
        var older = await router.EnqueueAsync("normal", Request("older"), ct);
        var promoted = await router.EnqueueAsync(changed.ThreadId, Request("promoted"), ct);
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.AfterDispatchParkForTests = _ => parked.TrySetResult();
        engine.BeginRequest(0);
        var occupied = true;
        var dispatch = router.DispatchLoop();
        try
        {
            await parked.Task.WaitAsync(ct);
            engine.EndRequest(0); // The fixture changes capacity without a dispatch signal.
            occupied = false;
            var failure = new InvalidOperationException("fixture observer");
            changed.PriorityChanged += (_, _) => throw failure;

            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => changed.SetPriority(ThreadPriority.High())));
            await promoted.CompletionTask.WaitAsync(ct);
            var feed = router.Events.Replay(0);
            var mutation = Assert.Single(feed, entry => entry.ThreadId == changed.ThreadId && entry.Kind == "thread.priority_changed");
            var first = feed.First(entry => entry.Kind == "request.selected");
            Assert.Equal(promoted.Id, first.TicketId);
            Assert.True(mutation.Seq < first.Seq);
            await older.CompletionTask.WaitAsync(ct);
        }
        finally
        {
            if (occupied) engine.EndRequest(0);
            await router.DisposeAsync();
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task ReRegisteringAPredecessorDuringExactTakeDisplacesTheOriginalSelection()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var backend = new FakeEngineHandler { LoadedModels = ["model"] };
        await using var router = MakeRouter(backend);
        Assert.True(await router.Engines["engine"].ProbeHealthOnceAsync(ct));
        var changing = router.RegisterThread("changing", ThreadPriority.Low());
        var original = await router.EnqueueAsync("normal", Request("original"), ct);
        var promoted = await router.EnqueueAsync(changing.ThreadId, Request("promoted"), ct);
        using var cut = new ConfigReloadRaceTests.Cut();
        router.BeforeAdmissionCommitForTests = ticket =>
        {
            if (ReferenceEquals(ticket, original)) cut.Hold();
        };
        var dispatch = Task.Run(router.TryDispatchOnce, ct);
        try
        {
            await cut.Entered.Task.WaitAsync(ct);
            Assert.Same(changing, router.RegisterThread(changing.ThreadId, ThreadPriority.High()));
        }
        finally { cut.Release(); }

        Assert.True(await dispatch.WaitAsync(ct));
        await promoted.CompletionTask.WaitAsync(ct);
        Assert.Equal(TicketState.Queued, original.State);
        Assert.Equal("promoted", Assert.Single(backend.ExecutionOrder));
        Assert.Equal(promoted.Id, Assert.Single(router.Events.Replay(0), entry => entry.Kind == "request.selected").TicketId);
    }

    [Fact]
    public async Task ADetachedHandleCannotUpdateTheCurrentThreadWithTheSameId()
    {
        using var backend = new FakeEngineHandler();
        await using var router = MakeRouter(backend);
        var current = router.RegisterThread("same", ThreadPriority.Normal());
        var detached = new RouterThread(current.ThreadId, ThreadPriority.Low(), 1, null, router.ChangeOwnedThreadPriority);

        detached.SetPriority(ThreadPriority.High());

        Assert.Equal(ThreadPriority.High(), detached.Priority);
        Assert.Equal(ThreadPriority.Normal(), current.Priority);
        Assert.Equal("thread.registered", Assert.Single(router.Events.Replay(0), entry => entry.ThreadId == current.ThreadId).Kind);
    }

    private static RouterCore MakeRouter(FakeEngineHandler backend, int maxSlots = 8) => new(new RouterConfig
    {
        AutoSwitch = false,
        Scheduler = new SchedulerConfig { MaxInflightPerEngine = maxSlots },
        Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal)
        {
            ["engine"] = new() { Name = "engine", Endpoint = new Uri("http://engine.invalid/v1") },
        },
        HttpHandlerFactory = _ => backend,
    });

    private static InferenceRequest Request(string marker, ThreadPriority? priority = null) => new()
    {
        Model = "model",
        Body = Encoding.UTF8.GetBytes($$"""{"model":"model","messages":[{"role":"user","content":"{{marker}}"}]}"""),
        Options = new RouterRequestOptions { Priority = priority },
    };
}
