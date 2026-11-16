using System.Diagnostics;
using System.Net;
using System.Text;
using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>In-process fake OpenAI-compatible engine served through a stub HttpMessageHandler
/// (no sockets): /models, /props, /chat/completions, /completions.</summary>
public class FakeEngineHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Models reported by /models (the engine's "loaded" set).</summary>
    public string[] LoadedModels { get; set; } = ["test-model", "other-model"];
    /// <summary>Block every completion until <see cref="ReleaseGate"/> (for ordering tests).</summary>
    public bool HoldCompletions { get; set; }
    /// <summary>First user-message content per executed completion, in execution order.</summary>
    public List<string> ExecutionOrder = [];

    public IReadOnlyList<string> Order => ExecutionOrder;
    public void ReleaseGate() => _gate.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.PathAndQuery.TrimStart('/');
        if (path.StartsWith("models"))
            return Ok("{\"data\":[{\"id\":\"test-model\"},{\"id\":\"other-model\"}]}");
        if (path.StartsWith("props"))
            return Ok("{\"total_slots\": 4}");
        if (path.StartsWith("chat/completions") || path.StartsWith("completions"))
        {
            var body = ReadBody(request);
            var tag = ExtractFirstContent(body);
            if (HoldCompletions)
            {
                try { await _gate.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { }
            }
            ExecutionOrder.Add(tag);
            if (body.Contains("\"stream\":true"))
                return Ok("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n", "text/event-stream");
            return Ok("""
                {"id":"c1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"pong"}}]}
                """);
        }
        return Ok("{}");
    }

    private static string ExtractFirstContent(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("messages", out var msgs) &&
                msgs.EnumerateArray().FirstOrDefault() is { } m &&
                m.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                return c.GetString() ?? "";
            return "";
        }
        catch (System.Text.Json.JsonException) { return ""; }
    }

    private static HttpResponseMessage Ok(string json, string contentType = "application/json")
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)),
        };
        res.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return res;
    }

    private static string ReadBody(HttpRequestMessage request)
        => Encoding.UTF8.GetString(
            request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult());
}

/// <summary>End-to-end dispatch: enqueue → queue → scheduler → engine execution → completion,
/// all against an in-process fake engine (no sockets).</summary>
public class EndToEndTests
{
    private static (RouterCore Router, FakeEngineHandler Fake) CreateRouter(
        int maxInflight = 8, int maxQueue = 8)
    {
        var fake = new FakeEngineHandler();
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
        var router = new RouterCore(cfg, events: new EventRing());
        router.Start();
        return (router, fake);
    }

    private static InferenceRequest Request(string tag, bool stream = false, string? model = null,
        ThreadPriority? priority = null)
        => new()
        {
            Model = model,
            Stream = stream,
            Body = Encoding.UTF8.GetBytes($$"""
                {"model":"{{model ?? "test-model"}}","stream":{{(stream ? "true" : "false")}},"messages":[{"role":"user","content":"{{tag}}"}]}
                """),
            Options = new RouterRequestOptions { Priority = priority },
        };

    private static async Task<(RouterCore, FakeEngineHandler)> WithHealthyRouterAsync(int maxInflight = 8, int maxQueue = 8)
    {
        var (router, fake) = CreateRouter(maxInflight, maxQueue);
        await WaitHealthyAsync(router);
        return (router, fake);
    }

    private static async Task WaitHealthyAsync(RouterCore router)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline &&
               !router.Engines["fake"].Healthy)
            await Task.Delay(25);
        Assert.True(router.Engines["fake"].Healthy);
    }

    private static async Task WaitStateAsync(RouterTicket ticket, TicketState state)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && ticket.State != state)
            await Task.Delay(25);
        Assert.Equal(state, ticket.State);
    }

    [Fact]
    public async Task NonStreaming_Completes_With_Engine_Body()
    {
        var (router, _) = await WithHealthyRouterAsync();
        await using var _ = router;
        var ticket = await router.EnqueueAsync("t1", Request("ping"));
        var body = await ticket.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("pong", Encoding.UTF8.GetString(body));
        Assert.Equal(TicketState.Completed, ticket.State);
        Assert.Equal("fake", ticket.Engine);
    }

    [Fact]
    public async Task Streaming_Produces_Channeled_Chunks()
    {
        var (router, fake) = await WithHealthyRouterAsync();
        await using var _ = router;
        fake.HoldCompletions = true;
        var ticket = await router.EnqueueAsync("t1", Request("ping", stream: true));
        await WaitStateAsync(ticket, TicketState.Executing);
        Assert.NotNull(ticket.StreamChannel);
        fake.ReleaseGate();
        var got = new List<byte>();
        while (await ticket.StreamChannel!.Reader.WaitToReadAsync())
            while (ticket.StreamChannel.Reader.TryRead(out var chunk))
                got.AddRange(chunk.ToArray());
        Assert.Contains("hi", Encoding.UTF8.GetString(got.ToArray()));
        Assert.Equal(TicketState.Completed, ticket.State);
    }

    [Fact]
    public async Task Priority_Orders_Execution_Across_Threads()
    {
        var (router, fake) = await WithHealthyRouterAsync(maxInflight: 1, maxQueue: 8);
        await using var _ = router;
        fake.HoldCompletions = true;

        var low = await router.EnqueueAsync("t-low", Request("ping-low", priority: ThreadPriority.Background()));
        await WaitStateAsync(low, TicketState.Executing);

        var high = await router.EnqueueAsync("t-high", Request("ping-high", priority: ThreadPriority.Interactive()));
        var mid = await router.EnqueueAsync("t-mid", Request("ping-mid", priority: ThreadPriority.Normal()));
        Assert.Equal(TicketState.Queued, high.State);
        Assert.Equal(TicketState.Queued, mid.State);

        fake.ReleaseGate();
        await low.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await high.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await mid.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["ping-low", "ping-high", "ping-mid"], fake.ExecutionOrder);
    }

    [Fact]
    public async Task Queue_Overflow_Fails_Ticket()
    {
        var (router, fake) = await WithHealthyRouterAsync(maxInflight: 1, maxQueue: 1);
        await using var _ = router;
        fake.HoldCompletions = true;

        var first = await router.EnqueueAsync("t1", Request("ping-1"));
        await WaitStateAsync(first, TicketState.Executing);   // capacity 1 is now reserved
        var second = await router.EnqueueAsync("t2", Request("ping-2")); // queued
        var third = await router.EnqueueAsync("t3", Request("ping-3"));  // overflows
        var ex = await Assert.ThrowsAsync<RouterRequestException>(
            () => third.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("queue_overflow", ex.Code);

        fake.ReleaseGate();
        await first.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await second.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancel_Queued_Ticket()
    {
        var (router, fake) = await WithHealthyRouterAsync();
        await using var _ = router;
        fake.HoldCompletions = true;
        var a = await router.EnqueueAsync("t1", Request("ping-a"));
        await WaitStateAsync(a, TicketState.Executing);
        var b = await router.EnqueueAsync("t2", Request("ping-b")); // queued
        Assert.True(router.TryCancel(b.Id));
        var ex = await Assert.ThrowsAsync<RouterRequestException>(
            () => b.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("cancelled", ex.Code);
        fake.ReleaseGate();
        await a.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stats_Report_Engines_And_Queues()
    {
        var (router, _) = await WithHealthyRouterAsync();
        await using var _ = router;
        var ticket = await router.EnqueueAsync("t1", Request("ping"));
        await ticket.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        var stats = router.GetStats();
        Assert.Contains(stats.Engines, e => e.Engine == "fake" && e.Healthy);
        Assert.Contains(stats.Threads, t => t.ThreadId == "t1" && t.Completed == 1);
    }

    [Fact]
    public async Task AutoSwitch_Runs_Lifecycle_Command()
    {
        var fake = new FakeEngineHandler { LoadedModels = [] }; // nothing loaded yet
        var cfg = new RouterConfig
        {
            DefaultModel = "test-model",
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 8 },
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
                    ["test-model"] = new EngineModelDefinition
                    {
                        Name = "test-model",
                        Select = new LifecycleCommand
                        {
                            Exec = "/bin/true",         // trivially succeeds; wait_health=false
                            WaitHealth = false,
                        },
                    },
                },
            },
        };
        await using var router = new RouterCore(cfg, events: new EventRing());
        router.Start();
        await WaitHealthyAsync(router);

        var ticket = await router.EnqueueAsync("t1", Request("ping"));
        await ticket.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TicketState.Completed, ticket.State);
        // the lifecycle command ran and the completion went through afterwards
        Assert.Contains("ping", fake.ExecutionOrder);
    }
}
