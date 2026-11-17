using System.Net;
using System.Text;
using Arboreal.InferenceRouter;
using Arboreal.InferenceRouter.Server;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>
/// Request-side phase timeouts (TASK-10 AC #10). A ticket's deadline bounds only its time in the queue,
/// so the two waits that come after admission — a model switch, and an engine's first response byte —
/// need their own ceilings. Without them a wedged engine or a load that never finishes parks a ticket,
/// the engine slot behind it, and a caller who is still waiting.
/// </summary>
public class RequestPhaseTimeoutTests
{
    [Fact(Timeout = 30_000)]
    public async Task AnEngineThatSendsNoByteEndsTheRequestAsEngineTimeoutAndFreesTheSlot()
    {
        // One slot only: if the cap did not release it, the second request below would never be served.
        var (app, fake, port) = await WireHarness.StartAsync(
            holdCompletions: false, maxInflight: 1, responseTimeoutMs: 300);
        using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        fake.NeverAnswersCompletions = true;

        var started = Environment.TickCount64;
        using (var wedged = new StringContent(
            """{"model":"test-model","stream":false,"messages":[{"role":"user","content":"wedged"}]}""",
            Encoding.UTF8, "application/json"))
        {
            var res = await http.PostAsync("/v1/chat/completions", wedged);
            var text = await res.Content.ReadAsStringAsync();

            Assert.Equal((HttpStatusCode)504, res.StatusCode);
            Assert.Contains("engine_timeout", text);
            Assert.DoesNotContain("\"Error\"", text);             // one lowercase contract on every surface

            // It did wait for the engine, and it gave up on the cap rather than on the caller.
            Assert.True(Environment.TickCount64 - started >= 250,
                $"answered after {Environment.TickCount64 - started} ms: the cap did not apply");
        }

        fake.NeverAnswersCompletions = false;
        using (var again = new StringContent(
            """{"model":"test-model","stream":false,"messages":[{"role":"user","content":"again"}]}""",
            Encoding.UTF8, "application/json"))
        {
            var followUp = await http.PostAsync("/v1/chat/completions", again);
            Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);  // the wedge did not hold the only slot
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task TheCapBoundsOnlyTheFirstByteSoAStreamThatStartedRunsToTheEnd()
    {
        var (app, fake, port) = await WireHarness.StartAsync(holdCompletions: false, responseTimeoutMs: 400);
        using var _ = app;
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        fake.TrickleStreamMs = 40;    // a stream that keeps producing for seconds, far past the 400 ms cap

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                """{"model":"test-model","stream":true,"messages":[{"role":"user","content":"trickle"}]}""",
                Encoding.UTF8, "application/json"),
        };
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();

        await using var stream = await res.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var frames = 0;
        var endedNaturally = false;
        while (true)
        {
            using var idle = new CancellationTokenSource(5_000);
            string? line;
            try { line = await reader.ReadLineAsync(idle.Token); }
            catch (OperationCanceledException) { break; }
            if (line is null) { endedNaturally = true; break; }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            frames++;
        }

        // The trickle fake emits no [DONE] frame, so what is pinned here is that the relay kept up past
        // the 400 ms cap and finished when the engine finished, rather than being cut off mid-stream.
        Assert.True(frames > 5, $"only {frames} frames arrived: the response cap cut a stream that had already started");
        Assert.True(endedNaturally, "the relayed stream never closed: the pump did not end when the engine stopped");
    }

    [Fact(Timeout = 30_000)]
    public async Task AModelSwitchThatOutlivesTheCeilingEndsTheRequestInsteadOfParkingIt()
    {
        using var fake = new FakeEngineHandler();
        var events = new EventRing();
        var cfg = new RouterConfig
        {
            DefaultModel = "slow-model",
            Scheduler = new SchedulerConfig
            {
                MaxInflightPerEngine = 1,
                MaxQueuePerModel = 4,
                SwitchWaitTimeoutMs = 250,
            },
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
                    // A load that outlives any reasonable request: the selector process stays up four seconds.
                    ["slow-model"] = new EngineModelDefinition
                    {
                        Name = "slow-model",
                        Select = new LifecycleCommand
                        {
                            Exec = "/bin/sh",
                            Args = ["-c", "sleep 4"],
                            WaitHealth = false,
                            WaitTimeoutMs = 30_000,
                        },
                    },
                },
            },
        };

        await using var router = new RouterCore(cfg, events: events);
        router.Start();
        await WaitHealthyAsync(router);

        var started = Environment.TickCount64;
        var ticket = await router.EnqueueAsync("thread-slow", Request("slow"));
        var ex = await Record.ExceptionAsync(() => ticket.CompletionTask);
        var elapsed = Environment.TickCount64 - started;

        Assert.NotNull(ex);
        var failure = Assert.IsType<RouterRequestException>(ex);
        Assert.Equal("switch_failed", failure.Code);
        Assert.Contains("did not become ready to serve slow-model within 250 ms", failure.Message);

        // The whole point: the caller is answered at the ceiling, not when the load eventually finishes.
        Assert.True(elapsed < 3_000, $"the request waited {elapsed} ms: the switch ceiling did not apply");
        Assert.Equal(TicketState.Failed, ticket.State);
        Assert.Equal(0, router.GetStats().QueueDepths.GetValueOrDefault("slow-model"));
        var feed = events.Replay(0);
        Assert.True(feed.Any(e => e.Kind == "engine.switch_started" && e.Model == "slow-model"),
            string.Join(",", feed.Select(e => $"{e.Kind}/{e.Model}/{e.Reason}")));
    }

    private static InferenceRequest Request(string tag) => new()
    {
        Model = "slow-model",
        Stream = false,
        Body = Encoding.UTF8.GetBytes(
            $$"""{"model":"slow-model","stream":false,"messages":[{"role":"user","content":"{{tag}}"}]}"""),
        Options = new RouterRequestOptions(),
    };

    private static async Task WaitHealthyAsync(RouterCore router)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && !router.Engines["fake"].Healthy)
            await Task.Delay(25);
        Assert.True(router.Engines["fake"].Healthy);
    }
}
