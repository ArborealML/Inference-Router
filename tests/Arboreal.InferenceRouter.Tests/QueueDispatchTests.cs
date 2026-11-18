namespace Arboreal.InferenceRouter.Tests;

public sealed class QueueDispatchTests
{
    [Fact]
    public async Task SchedulerEvaluatesTheThreadAdmissibleTicketAndItsOwnContext()
    {
        using var handler = new FakeEngineHandler();
        var scheduler = new RecordingScheduler();
        await using var router = CreateRouter(handler, scheduler);
        var blocked = router.RegisterThread("blocked", ThreadPriority.High());
        blocked.IncrementInflight();
        var head = await router.EnqueueAsync("blocked", Request("a", context: 32));
        var eligible = await router.EnqueueAsync("eligible", Request("a", context: 96));

        Assert.True(router.TryDispatchOnce());
        var seen = Assert.Single(scheduler.Seen);
        Assert.Same(eligible, seen.Ticket);
        Assert.Equal(96L, seen.RequiredContextTokens);
        Assert.Equal(eligible.Id, SelectedTicket(router));
        Assert.Equal(TicketState.Queued, head.State);
        await eligible.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationBetweenSelectionAndTakeRestartsThePassWithoutSubstitution()
    {
        using var handler = new FakeEngineHandler();
        var scheduler = new RecordingScheduler();
        await using var router = CreateRouter(handler, scheduler);
        var first = await router.EnqueueAsync("first", Request("a", context: 32));
        var remaining = await router.EnqueueAsync("remaining", Request("a", context: 96));
        await router.EnqueueAsync("other", Request("b", context: 32));
        await router.EnqueueAsync("other", Request("b", context: 32));
        scheduler.OnSelect = request =>
        {
            if (ReferenceEquals(request.Ticket, first)) Assert.True(router.TryCancel(first.Id));
        };

        Assert.True(router.TryDispatchOnce());
        Assert.Equal(0, router.Engines["fake"].Inflight);
        Assert.DoesNotContain(router.Events.Replay(0), entry => entry.Kind == "request.selected");
        Assert.Equal(TicketState.Queued, remaining.State);
        Assert.Same(first, Assert.Single(scheduler.Seen).Ticket);

        Assert.True(router.TryDispatchOnce());
        Assert.Equal(2, scheduler.Seen.Count);
        Assert.Same(remaining, scheduler.Seen[1].Ticket);
        Assert.Equal(96L, scheduler.Seen[1].RequiredContextTokens);
        Assert.Equal(remaining.Id, SelectedTicket(router));
        await remaining.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CustomSchedulerIsCalledOnlyForTheFirstDispatchableQueueInDepthOrder()
    {
        using var handler = new FakeEngineHandler();
        var scheduler = new RecordingScheduler();
        await using var router = CreateRouter(handler, scheduler);
        router.RegisterThread("priority", ThreadPriority.High());
        var deeperHead = await router.EnqueueAsync("priority", Request("a", context: 32));
        await router.EnqueueAsync("priority", Request("a", context: 32));
        var shallowHead = await router.EnqueueAsync("normal", Request("b", context: 32));
        await router.EnqueueAsync("later-queue", Request("c", context: 32));

        Assert.True(router.TryDispatchOnce());
        Assert.Same(shallowHead, Assert.Single(scheduler.Seen).Ticket);
        Assert.Equal(shallowHead.Id, SelectedTicket(router));
        Assert.Equal(TicketState.Queued, deeperHead.State);
        await shallowHead.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DispatchDeadlineUsesElapsedTimeWhenTheWallClockMoves()
    {
        using var handler = new FakeEngineHandler();
        var scheduler = new RecordingScheduler { Decision = SchedulerDecision.Wait("unavailable") };
        var clock = new ManualClock();
        await using var router = CreateRouter(handler, scheduler, clock);
        var request = Request("a", context: 32, maxQueueTime: TimeSpan.Zero);
        var ticket = await router.EnqueueAsync("first", request);

        clock.UtcNow += TimeSpan.FromDays(1);
        Assert.False(router.TryDispatchOnce());
        Assert.Equal(TicketState.Queued, ticket.State);
        Assert.Equal(DateTimeOffset.UnixEpoch, ticket.QueuedAt);

        clock.UtcNow -= TimeSpan.FromDays(2);
        clock.Timestamp++;
        Assert.False(router.TryDispatchOnce());
        Assert.Equal("deadline_exceeded", ticket.ErrorCode);
        await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask);
    }

    private static string? SelectedTicket(RouterCore router) =>
        Assert.Single(router.Events.Replay(0), entry => entry.Kind == "request.selected").TicketId;

    private static RouterCore CreateRouter(FakeEngineHandler handler, IRequestScheduler scheduler,
                                           TimeProvider? clock = null) =>
        new(new RouterConfig
        {
            AutoSwitch = false,
            Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal)
            {
                ["fake"] = new() { Name = "fake", Endpoint = new Uri("http://engine.invalid/v1/") },
            },
            HttpHandlerFactory = _ => handler,
        }, scheduler, events: null, clock: clock ?? TimeProvider.System);

    private static InferenceRequest Request(string model, long context, TimeSpan? maxQueueTime = null) =>
        new()
        {
            Model = model,
            Body = """{"messages":[{"role":"user","content":"queue dispatch"}]}"""u8.ToArray(),
            Options = new RouterRequestOptions { RequiredContext = context, MaxQueueTime = maxQueueTime },
        };

    private sealed class RecordingScheduler : IRequestScheduler
    {
        internal List<QueuedRequest> Seen { get; } = [];
        internal Action<QueuedRequest>? OnSelect { get; set; }
        internal SchedulerDecision Decision { get; init; } = new() { Engine = "fake" };

        public SchedulerDecision SelectNext(QueuedRequest request, SchedulerView view, RouterConfig config)
        {
            Seen.Add(request);
            OnSelect?.Invoke(request);
            return Decision;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
        internal long Timestamp { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
