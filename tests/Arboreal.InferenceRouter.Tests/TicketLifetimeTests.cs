using System.Text;
using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>
/// Ticket lifetime (TASK-10 AC #3): the caller's cancellation token is the ticket's cancel signal
/// from the moment it exists, capacity reserved for a ticket is released exactly once, a ticket
/// reaches exactly one terminal state, and a queued wait is bounded by the deadline the request
/// declared.
/// </summary>
public class TicketLifetimeTests
{
    private static readonly string[] TerminalKinds = ["request.completed", "request.failed", "request.cancelled"];
    private static readonly string[] AllowedRaceCodes = ["cancelled", "engine_error", "engine_unreachable"];

    private (RouterCore Router, FakeEngineHandler Fake, EventRing Events) CreateRouter(
        bool holdCompletions, int maxInflight = 1, int maxQueue = 8, bool start = true)
    {
        var fake = new FakeEngineHandler { HoldCompletions = holdCompletions };
        var events = new EventRing();
        var cfg = new RouterConfig
        {
            DefaultModel = "test-model",
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = maxInflight, MaxQueuePerModel = maxQueue },
            HttpHandlerFactory = _ => fake,
        };
        cfg.Engines = new Dictionary<string, EngineDefinition>
        {
            ["fake"] = new EngineDefinition
            {
                Name = "fake",
                Kind = EngineKind.OpenAiCompatible,
                Endpoint = new Uri("http://127.0.0.1:9/v1"),
                Catalog = new Dictionary<string, EngineModelDefinition>
                {
                    ["test-model"] = new EngineModelDefinition { Name = "test-model" },
                },
            },
        };
        var router = new RouterCore(cfg, events: events);
        _observedRouter = router;
        _observedEvents = events;
        if (start) router.Start();
        return (router, fake, events);
    }

    private static InferenceRequest Request(string tag, RouterRequestOptions? options = null) => new()
    {
        Model = "test-model",
        Stream = false,
        Body = Encoding.UTF8.GetBytes(
            $$"""{"model":"test-model","stream":false,"messages":[{"role":"user","content":"{{tag}}"}]}"""),
        Options = options ?? new RouterRequestOptions(),
    };

    private static int Inflight(RouterCore router) =>
        router.GetStats().Engines.Single(e => e.Engine == "fake").Inflight;

        // Generous bound: these tests wait on real dispatch, and the suite runs other classes in
    // parallel against in-process servers, so a loaded runner needs slack.
    private async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException(Describe());
            await Task.Delay(10);
        }
    }

    // A timeout in a lifetime test is otherwise a bare "condition not met": these capture the last
    // router and ring built by the test so the failure message says what the router was doing.
    private RouterCore? _observedRouter;
    private EventRing? _observedEvents;

    private string Describe()
    {
        var r = _observedRouter;
        if (r is null) return "no router";
        var s = r.GetStats();
        IReadOnlyList<RouterEvent> ev = _observedEvents?.Replay(0) ?? System.Array.Empty<RouterEvent>();
        return "engines=[" + string.Join(",", s.Engines.Select(e =>
            $"{e.Engine} healthy={e.Healthy} inflight={e.Inflight} slots={e.ActiveSlots} " +
            $"loaded={string.Join('|', e.LoadedModels)} switching={e.SwitchingToward}")) +
            "] queues=[" + string.Join(",", s.QueueDepths) + "] events=[" +
            string.Join(",", ev.Select(e => $"{e.Kind}:{e.TicketId}:{e.Reason}")) + "]";
    }

    private static async Task<RouterRequestException?> FailureOfAsync(RouterTicket ticket)
    {
        try { await ticket.CompletionTask; return null; }
        catch (RouterRequestException ex) { return ex; }
    }

    /// <summary>A caller that hangs up while its request is still waiting must lose the request: the
    /// queue gives its place back and no engine is ever asked to serve it.</summary>
    [Fact]
    public async Task AbortingTheCallerWhileQueuedCancelsTheTicketAndFreesItsQueuePlace()
    {
        var (router, fake, _) = CreateRouter(holdCompletions: true, maxInflight: 1);
        try
        {
            var blocker = await router.EnqueueAsync("thread-block", Request("block"));
            await WaitUntilAsync(() => Inflight(router) == 1);

            using var caller = new CancellationTokenSource();
            var queued = await router.EnqueueAsync("thread-cancel", Request("cancel"), caller.Token);
            Assert.Equal(1, router.GetStats().QueueDepths["test-model"]);

            caller.Cancel();

            var ex = await FailureOfAsync(queued);
            Assert.NotNull(ex);
            Assert.Equal("cancelled", ex!.Code);
            Assert.Equal(TicketState.Cancelled, queued.State);
            Assert.DoesNotContain("test-model", router.GetStats().QueueDepths.Keys);
            Assert.Equal(0, fake.CancelledCompletions);          // it never reached an engine
            Assert.Equal(1, Inflight(router));                   // the blocker still holds its slot

            fake.ReleaseGate();
            await blocker.CompletionTask;
            await WaitUntilAsync(() => Inflight(router) == 0);
        }
        finally { await router.DisposeAsync(); }
    }

    /// <summary>Aborting during execution must cancel the upstream engine call and hand the engine
    /// slot back exactly once.</summary>
    [Fact]
    public async Task AbortingTheCallerMidFlightCancelsUpstreamAndReleasesCapacity()
    {
        var (router, fake, events) = CreateRouter(holdCompletions: true, maxInflight: 1);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.AfterHeldCompletionEnteredForTests = () => entered.TrySetResult();
            fake.AfterHeldCompletionCancellationForTests = () => cancelled.TrySetResult();
            using var caller = new CancellationTokenSource();
            var ticket = await router.EnqueueAsync("thread-inflight", Request("in-flight"), caller.Token);
            await entered.Task.WaitAsync(limit.Token);
            Assert.Equal(1, Inflight(router));
            Assert.Equal(1, fake.CompletionCalls);

            caller.Cancel();

            await cancelled.Task.WaitAsync(limit.Token);
            var ex = await FailureOfAsync(ticket).WaitAsync(limit.Token);
            Assert.NotNull(ex);
            Assert.Equal("cancelled", ex.Code);
            await WaitUntilAsync(() => Inflight(router) == 0);
            Assert.Equal(1, fake.CancelledCompletions);
            Assert.Equal(1, fake.CompletionCalls);
            var terminal = Assert.Single(events.Replay(0),
                e => e.TicketId == ticket.Id && TerminalKinds.Contains(e.Kind));
            Assert.Equal("request.cancelled", terminal.Kind);
        }
        finally { await router.DisposeAsync(); }
    }

    [Fact]
    public async Task AbortingAfterSelectionBeforeExecutionReleasesCapacityWithoutSending()
    {
        var (router, fake, events) = CreateRouter(holdCompletions: true, start: false);
        await using var ownedRouter = router;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var selected = new AdmissionFixtureBarrier();
        using var caller = new CancellationTokenSource();
        Assert.True(await router.Engines["fake"].ProbeHealthOnceAsync(limit.Token));
        var ticket = await router.EnqueueAsync("thread-before-send", Request("no-send"), caller.Token);
        events.Subscribe(evt =>
        {
            if (evt.Kind == "request.selected" && evt.TicketId == ticket.Id)
                selected.HoldAsync(limit.Token).GetAwaiter().GetResult();
        });

        var dispatch = Task.Run(router.TryDispatchOnce, limit.Token);
        try
        {
            await selected.Reached.WaitAsync(limit.Token);
            Assert.Equal(1, Inflight(router));
            Assert.Equal(0, fake.CompletionCalls);
            caller.Cancel();
        }
        finally
        {
            selected.Release();
        }

        Assert.True(await dispatch.WaitAsync(limit.Token));
        var error = await FailureOfAsync(ticket).WaitAsync(limit.Token);
        Assert.NotNull(error);
        Assert.Equal("cancelled", error.Code);
        await WaitUntilAsync(() => Inflight(router) == 0);
        Assert.Equal(0, fake.CompletionCalls);
        Assert.Equal(0, fake.CancelledCompletions);
        var terminal = Assert.Single(events.Replay(0),
            e => e.TicketId == ticket.Id && TerminalKinds.Contains(e.Kind));
        Assert.Equal("request.cancelled", terminal.Kind);
    }

    /// <summary>Racing an explicit cancel against a caller abort and a normal completion must still
    /// produce one terminal state, one terminal event and no leaked capacity.</summary>
    [Fact]
    public async Task CancelRaceLeavesOneTerminalStateAndNoLeakedSlots()
    {
        var (router, fake, events) = CreateRouter(holdCompletions: false, maxInflight: 2, maxQueue: 32);
        try
        {
            var tickets = new List<RouterTicket>();
            for (var i = 0; i < 40; i++)
            {
                var caller = new CancellationTokenSource();
                var ticket = await router.EnqueueAsync($"thread-{i}", Request($"x{i}"), caller.Token);
                tickets.Add(ticket);
                if (i % 3 == 0) router.TryCancel(ticket.Id);
                if (i % 3 == 1) caller.Cancel();
                caller.Dispose();
            }

            foreach (var t in tickets)
            {
                var ex = await FailureOfAsync(t);
                if (ex is not null)
                    Assert.Contains(ex.Code, AllowedRaceCodes);
                Assert.Equal(1, events.Replay(0)
                    .Count(e => e.TicketId == t.Id && TerminalKinds.Contains(e.Kind)));
                Assert.True(t.State is TicketState.Completed or TicketState.Cancelled or TicketState.Failed,
                    $"{t.Id} ended in {t.State}");
            }

            await WaitUntilAsync(() => Inflight(router) == 0);
            Assert.Equal(0, Inflight(router));
            Assert.True(router.GetStats().QueueDepths.Values.All(d => d == 0), "queue drained");
            _ = fake;
        }
        finally { await router.DisposeAsync(); }
    }

    /// <summary>A queued wait is bounded by the deadline the request declared, even while the engine
    /// stays busy and never gives the dispatcher a reason to look at the queue again.</summary>
    [Fact]
    public async Task QueueWaitFailsOnItsDeadlineWithoutADispatchSignal()
    {
        var (router, fake, _) = CreateRouter(holdCompletions: true, maxInflight: 1);
        try
        {
            var blocker = await router.EnqueueAsync("thread-block2", Request("block"));
            await WaitUntilAsync(() => Inflight(router) == 1);

            var queued = await router.EnqueueAsync("thread-deadline",
                Request("wait", new RouterRequestOptions { MaxQueueTime = TimeSpan.FromMilliseconds(150) }));

            var ex = await FailureOfAsync(queued);
            Assert.NotNull(ex);
            Assert.Equal("deadline_exceeded", ex!.Code);
            Assert.DoesNotContain("test-model", router.GetStats().QueueDepths.Keys);
            Assert.Equal(TicketState.Failed, queued.State);

            fake.ReleaseGate();
            await blocker.CompletionTask;
        }
        finally { await router.DisposeAsync(); }
    }

    /// <summary>Shutdown must give queued tickets a terminal state: a relay waiting on one would
    /// otherwise stay open until the process exits.</summary>
    [Fact]
    public async Task ShutdownTerminatesTicketsStillWaiting()
    {
        var (router, fake, _) = CreateRouter(holdCompletions: true, maxInflight: 1);
        var blocker = await router.EnqueueAsync("thread-shutdown", Request("block"));
        await WaitUntilAsync(() => Inflight(router) == 1);
        var queued = await router.EnqueueAsync("thread-queued", Request("wait"));

        await router.DisposeAsync();

        var ex = await FailureOfAsync(queued);
        Assert.NotNull(ex);
        Assert.Equal("cancelled", ex!.Code);
        Assert.Equal(TicketState.Cancelled, queued.State);
        fake.ReleaseGate();
        _ = blocker;
    }
}
