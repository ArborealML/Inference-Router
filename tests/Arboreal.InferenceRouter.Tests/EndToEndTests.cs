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
    /// <summary>When set, /models re-reads the loaded set from this file (one id per line), so a
    /// lifecycle command can "load" a model mid-test the way a real switch does.</summary>
    public string? LoadedModelsFile { get; set; }
    /// <summary>Context window reported by /props (0 = omit the field entirely).</summary>
    public long NCtx { get; set; }
    /// <summary>Slots reported by /props; also the engine's physical concurrency ceiling.</summary>
    public int TotalSlots { get; set; } = 4;
    /// <summary>When set, /props re-reads the window from this file (lets a lifecycle command
    /// change the advertised window mid-test, which is what a profile switch does).</summary>
    public string? NCtxFile { get; set; }

    private string[] ActiveLoadedModels()
    {
        if (LoadedModelsFile is { Length: > 0 } path && File.Exists(path))
            return [.. from line in File.ReadAllLines(path)
                      where line.Trim().Length > 0
                      select line.Trim()];
        return LoadedModels;
    }

    private long ActiveContextTokens()
    {
        if (NCtxFile is { Length: > 0 } path && File.Exists(path) &&
            long.TryParse(File.ReadAllText(path).Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return v;
        return NCtx;
    }
    /// <summary>First user-message content per executed completion, in execution order.</summary>
    public List<string> ExecutionOrder { get; } = [];

    public IReadOnlyList<string> Order => ExecutionOrder;
    public void ReleaseGate() => _gate.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery.TrimStart('/');
        if (path.StartsWith("models", StringComparison.Ordinal))
        {
            // The loaded set is mutable so tests can exercise availability: a model the engine does
            // not report must not be scheduled as if it were loaded.
            var items = string.Join(",", ActiveLoadedModels().Select(
                m => "{\"id\":\"" + m + "\",\"status\":{\"value\":\"loaded\"}}"));
            return Ok("{\"object\":\"list\",\"data\":[" + items + "]}");
        }
        if (path.StartsWith("props", StringComparison.Ordinal))
        {
            var nctx = ActiveContextTokens();
            return Ok($@"{{""total_slots"": {TotalSlots}{(nctx > 0 ? $",\"n_ctx\": {nctx}" : "")}}}");
        }
        if (path.StartsWith("chat/completions", StringComparison.Ordinal) || path.StartsWith("completions", StringComparison.Ordinal))
        {
            var body = ReadBody(request);
            var tag = ExtractFirstContent(body);
            if (HoldCompletions)
            {
                try { await _gate.Task.WaitAsync(cancellationToken); }
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

    /// <summary>The fake engine's loaded set is what the router sees: mutating it must change the
    /// engine's reported models, which is the input every availability decision uses.</summary>
    [Fact]
    public async Task Engine_Loaded_Set_Flows_From_The_Fake_To_The_Router()
    {
        var (router, fake) = CreateRouter();
        try
        {
            fake.LoadedModels = ["only-this-one"];
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
            string[] seen = [];
            while (Stopwatch.GetTimestamp() < deadline)
            {
                seen = router.Engines["fake"].LoadedModels;
                if (seen.Length == 1 && seen[0] == "only-this-one") break;
                Thread.Sleep(100);
            }
            Assert.Equal(["only-this-one"], seen);
            Assert.DoesNotContain("test-model", router.Engines["fake"].LoadedModels);
        }
        finally
        {
            await router.DisposeAsync();
        }
    }

    private static InferenceRequest Request(string tag, bool stream = false, string? model = null,
        ThreadPriority? priority = null, long requiredContext = 0)
        => new()
        {
            Model = model,
            Stream = stream,
            Body = Encoding.UTF8.GetBytes($$"""
                {"model":"{{model ?? "test-model"}}","stream":{{(stream ? "true" : "false")}},"messages":[{"role":"user","content":"{{tag}}"}]}
                """),
            Options = new RouterRequestOptions
            {
                Priority = priority,
                RequiredContext = requiredContext > 0 ? requiredContext : null,
            },
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
    public async Task Streaming_Channel_Is_Published_With_Executing_State()
    {
        // The channel must be readable the moment a listener observes Executing; otherwise a fast
        // engine can finish before the SSE relay ever finds a channel.
        var (router, fake) = await WithHealthyRouterAsync(maxInflight: 1, maxQueue: 8);
        await using var _ = router;
        fake.HoldCompletions = true;
        var ticket = await router.EnqueueAsync("t1", Request("ping", stream: true));
        await WaitStateAsync(ticket, TicketState.Executing);
        Assert.NotNull(ticket.StreamChannel);
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
        var loadedFile = Path.Combine(Path.GetTempPath(), $"air-loaded-{Guid.NewGuid():N}.txt");
        // Nothing is loaded to start with; the lifecycle command below is what puts the model on the
        // engine's /models list, exactly as a real select-model switch does.
        using var fake = new FakeEngineHandler { LoadedModels = [], LoadedModelsFile = loadedFile };
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
                            Exec = "/bin/sh",           // "loads" the model; wait_health=false
                            Args = ["-c", $"printf 'test-model\n' > {loadedFile}"],
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
        File.Delete(loadedFile);
    }

    /// <summary>Context-window escalation (§7.6): the model is loaded but serving a smaller window
    /// than the request needs, so the request queues behind a profile switch instead of compacting.</summary>
    [Fact]
    public async Task Escalation_Queues_Behind_Profile_Switch()
    {
        var windowFile = Path.Combine(Path.GetTempPath(), $"air-escalation-{Guid.NewGuid():N}.txt");
        File.WriteAllText(windowFile, "131072");
        try
        {
            using var fake = new FakeEngineHandler { NCtxFile = windowFile, TotalSlots = 2 };
            var cfg = new RouterConfig
            {
                DefaultModel = "test-model",
                Scheduler = new SchedulerConfig { MaxInflightPerEngine = 8, CharsPerTokenEstimate = 4.0 },
                HttpHandlerFactory = _ => fake,
            };
            cfg.Engines = new Dictionary<string, EngineDefinition>
            {
                ["fake"] = new EngineDefinition
                {
                    Name = "fake",
                    Kind = EngineKind.OpenAiCompatible,
                    Endpoint = new Uri("http://127.0.0.1:9/v1"),
                    Health = new HealthProbeDefinition { Path = "/props", IntervalMs = 50 },
                    Catalog = new Dictionary<string, EngineModelDefinition>
                    {
                        ["test-model"] = new EngineModelDefinition
                        {
                            Name = "test-model",
                            Profile = "small",
                            ServingProfiles =
                            [
                                new ServingProfile { Name = "small", MaxContext = 131_072, MaxSlots = 2 },
                                new ServingProfile { Name = "big", MaxContext = 1_048_576, MaxSlots = 1 },
                            ],
                            Select = new LifecycleCommand
                            {
                                // The lifecycle command is what widens the window: it rewrites the
                                // file the fake engine reports as n_ctx.
                                Exec = "/bin/sh",
                                Args = ["-c", $"printf 1048576 > {windowFile}"],
                                WaitHealth = true,
                                WaitTimeoutMs = 15_000,
                            },
                        },
                    },
                },
            };
            await using var router = new RouterCore(cfg, events: new EventRing());
            router.Start();
            await WaitHealthyAsync(router);

            // Small request first: served on the existing profile, no switch.
            var small = await router.EnqueueAsync("t-small", Request("ping-small", requiredContext: 1_000));
            await small.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(131_072, router.Engines["fake"].ActiveContextWindow);
            Assert.Equal("small", router.Engines["fake"].ActiveProfile);
            Assert.False(router.Engines["fake"].Switching);

            // Oversized request: must escalate before executing.
            var big = await router.EnqueueAsync("t-big", Request("ping-big", requiredContext: 200_000));
            var body = await big.CompletionTask.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Contains("pong", Encoding.UTF8.GetString(body));
            Assert.Equal(1_048_576, router.Engines["fake"].ActiveContextWindow);
            Assert.Equal("big", router.Engines["fake"].ActiveProfile);
            Assert.Equal(TicketState.Completed, big.State);

            var events = router.Events.Replay(0);
            Assert.Contains(events, e => e.Kind == "engine.profile" && e.Value == 1_048_576 && e.Reason == "big");
            Assert.Contains(events, e => e.Kind == "engine.switch_started" && e.Reason == "profile=big");
            Assert.DoesNotContain(events, e => e.TicketId == small.Id && e.Kind == "request.failed");

            var stats = router.GetStats();
            var eng = stats.Engines.Single(e => e.Engine == "fake");
            Assert.Equal("big", eng.ActiveProfile);
            Assert.Equal(1_048_576, eng.ActiveContextWindow);
            // the fake advertises a constant slot count; a real engine would report the
            // profile's capacity here and admission would follow it (min with max_inflight).
            Assert.Equal(2, eng.ActiveSlots);
        }
        finally
        {
            File.Delete(windowFile);
        }
    }
}
