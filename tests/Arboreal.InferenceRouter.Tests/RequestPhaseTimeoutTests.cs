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

    [Fact(Timeout = 30_000)]
    public async Task HeadersWithoutABodyAreNotAnAnswerSoTheCeilingStillEndsTheRequest()
    {
        // The case a headers-only timeout misses: a backend that answers 200 and then goes quiet. Legal
        // HTTP, and what an overloaded engine really does — so the ceiling has to bound the bytes it owes,
        // not the headers it already sent. One slot: if the cap did not release it, nothing is served after.
        var (app, fake, port) = await WireHarness.StartAsync(
            holdCompletions: false, maxInflight: 1, responseTimeoutMs: 300);
        using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        fake.HeadersThenStallCompletions = true;

        var started = Environment.TickCount64;
        using (var stalled = new StringContent(
            """{"model":"test-model","stream":false,"messages":[{"role":"user","content":"headers only"}]}""",
            Encoding.UTF8, "application/json"))
        {
            var res = await http.PostAsync("/v1/chat/completions", stalled);
            var text = await res.Content.ReadAsStringAsync();

            Assert.Equal((HttpStatusCode)504, res.StatusCode);
            Assert.Contains("engine_timeout", text);
            Assert.True(Environment.TickCount64 - started >= 250,
                $"answered after {Environment.TickCount64 - started} ms: the cap did not wait out the body");
        }

        fake.HeadersThenStallCompletions = false;
        using (var again = new StringContent(
            """{"model":"test-model","stream":false,"messages":[{"role":"user","content":"again"}]}""",
            Encoding.UTF8, "application/json"))
        {
            var followUp = await http.PostAsync("/v1/chat/completions", again);
            Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);   // the stalled body did not hold the only slot
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AStreamThatNeverSendsAFrameEndsInsideTheStreamBecauseTheStatusIsAlreadySent()
    {
        // By the time a stream goes quiet the router has committed 200 and written keepalives, so there is
        // no 504 left to send: the failure has to arrive as an in-band error frame and a clean close. This
        // is what the docs promise, and it is only true because the cap still bounds the first frame.
        var (app, fake, port) = await WireHarness.StartAsync(holdCompletions: false, responseTimeoutMs: 300);
        using var _ = app;
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        fake.HeadersThenStallCompletions = true;

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                """{"model":"test-model","stream":true,"messages":[{"role":"user","content":"headers only"}]}""",
                Encoding.UTF8, "application/json"),
        };
        var started = Environment.TickCount64;
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);            // committed before the engine went quiet

        var sawFailure = false;
        var sawDone = false;
        await using var stream = await res.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (true)
        {
            using var idle = new CancellationTokenSource(5_000);
            string? line;
            try { line = await reader.ReadLineAsync(idle.Token); }
            catch (OperationCanceledException) { break; }
            if (line is null) break;
            if (line.Contains("engine_timeout", StringComparison.Ordinal)) sawFailure = true;
            if (line.Contains("[DONE]", StringComparison.Ordinal)) sawDone = true;
        }

        var elapsed = Environment.TickCount64 - started;
        Assert.True(sawFailure, "a stream whose engine sent headers and no frame ended without an error frame");
        Assert.True(sawDone, "the failed stream did not close with [DONE]");
        Assert.True(elapsed >= 250, $"the stream ended after {elapsed} ms: the first-frame cap did not apply");
    }

    [Fact(Timeout = 30_000)]
    public async Task ACallerThatHangsUpIsCancelledEvenIfTheEngineIgnoresTheCancellation()
    {
        // The ceiling and the caller's own abort race for the reason the ticket reports. A handler that
        // answers late anyway (the pathological one) must not turn an abandoned request into a timeout:
        // the caller's reason wins, because it is the one that actually happened.
        using var fake = new FakeEngineHandler
        {
            HoldCompletions = true,
            AnswerAfterCancellationMs = 700,     // ignores the cancellation, answers after the ceiling
        };
        var cfg = new RouterConfig
        {
            DefaultModel = "test-model",
            AutoSwitch = false,
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 1 },
            HttpHandlerFactory = _ => fake,
        };
        cfg.Engines = EngineWithCap(cfg, responseTimeoutMs: 250);
        await using var router = new RouterCore(cfg, events: new EventRing());
        router.Start();
        await WaitHealthyAsync(router);

        using var caller = new CancellationTokenSource(120);
        var ticket = await router.EnqueueAsync("thread-abort", Request("test-model", "abort"), caller.Token);

        var ex = await Record.ExceptionAsync(() => ticket.CompletionTask);
        var failure = Assert.IsType<RouterRequestException>(ex);
        Assert.Equal("cancelled", failure.Code);
        // The logical slot is back even though the upstream handler is still sitting on the request:
        // a caller that hangs up cannot be allowed to hold engine capacity behind a handler that ignores it.
        Assert.Equal(0, router.GetStats().Engines.Single(e => e.Engine == "fake").Inflight);
        fake.ReleaseGate();
    }

    [Fact(Timeout = 30_000)]
    public async Task AMissingSelectCommandFailsThatRequestWithoutWedgingTheEngine()
    {
        // A model with nothing to load it is a plain configuration failure, not a latch: the engine has to
        // stay switchable afterwards, and the next request has to get its own switch rather than joining a
        // terminal one.
        var loadedFile = Path.Combine(Path.GetTempPath(), $"air-loaded-{Guid.NewGuid():N}.txt");
        using var fake = new FakeEngineHandler { LoadedModels = [], LoadedModelsFile = loadedFile };
        var cfg = new RouterConfig
        {
            DefaultModel = "bare-model",
            AutoSwitch = true,
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 1, SwitchWaitTimeoutMs = 5_000 },
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
                    ["bare-model"] = new EngineModelDefinition { Name = "bare-model" },   // no select command
                    ["loadable-model"] = new EngineModelDefinition
                    {
                        Name = "loadable-model",
                        Select = new LifecycleCommand
                        {
                            Exec = "/bin/sh",
                            Args = ["-c", $"printf 'loadable-model\\n' > {loadedFile}"],
                            WaitHealth = false,
                        },
                    },
                },
            },
        };
        await using var router = new RouterCore(cfg, events: new EventRing());
        router.Start();
        await WaitHealthyAsync(router);

        var bare = await router.EnqueueAsync("thread-bare", Request("bare-model", "bare"));
        var bareFailure = Assert.IsType<RouterRequestException>(
            await Record.ExceptionAsync(() => bare.CompletionTask));
        Assert.Equal("switch_failed", bareFailure.Code);

        var loadable = await router.EnqueueAsync("thread-loadable", Request("loadable-model", "loadable"));
        await loadable.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TicketState.Completed, loadable.State);
        Assert.Contains("loadable", fake.ExecutionOrder);
        File.Delete(loadedFile);
    }

    [Fact(Timeout = 30_000)]
    public async Task ARequestThatGivesUpOnASwitchDoesNotStopTheSwitchItWasWaitingFor()
    {
        // The caller's ceiling bounds the caller, not the engine: giving up must not abort a model load
        // half way through, latch the engine as "switching", or strand the requests that come after.
        var loadedFile = Path.Combine(Path.GetTempPath(), $"air-slow-{Guid.NewGuid():N}.txt");
        using var fake = new FakeEngineHandler { LoadedModels = [], LoadedModelsFile = loadedFile };
        var events = new EventRing();
        var cfg = new RouterConfig
        {
            DefaultModel = "slow-model",
            AutoSwitch = true,
            Scheduler = new SchedulerConfig
            {
                MaxInflightPerEngine = 1,
                MaxQueuePerModel = 4,
                SwitchWaitTimeoutMs = 200,
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
                    ["slow-model"] = new EngineModelDefinition
                    {
                        Name = "slow-model",
                        Select = new LifecycleCommand
                        {
                            // Loads the model a second after the caller stopped waiting for it.
                            Exec = "/bin/sh",
                            Args = ["-c", $"sleep 1; printf 'slow-model\\n' > {loadedFile}"],
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

        var first = await router.EnqueueAsync("thread-first", Request("slow-model", "first"));
        var firstFailure = Assert.IsType<RouterRequestException>(
            await Record.ExceptionAsync(() => first.CompletionTask));
        Assert.Equal("switch_failed", firstFailure.Code);

        // The load carries on: watch the feed rather than sleeping, and only then send anyone else.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline &&
               !events.Replay(0).Any(e => e.Kind == "engine.switch_completed" && e.Model == "slow-model"))
        {
            await Task.Delay(25);
        }

        Assert.True(
            events.Replay(0).Any(e => e.Kind == "engine.switch_completed" && e.Model == "slow-model"),
            "the switch stopped when the request waiting on it ran out of patience");

        var second = await router.EnqueueAsync("thread-second", Request("slow-model", "second"));
        await second.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TicketState.Completed, second.State);
        Assert.Contains("second", fake.ExecutionOrder);
        File.Delete(loadedFile);
    }

    private static Dictionary<string, EngineDefinition> EngineWithCap(RouterConfig cfg, int responseTimeoutMs) =>
        new()
        {
            ["fake"] = new EngineDefinition
            {
                Name = "fake",
                Kind = EngineKind.OpenAiCompatible,
                Endpoint = new Uri("http://127.0.0.1:9/v1"),
                ResponseTimeoutMs = responseTimeoutMs,
                Catalog = new Dictionary<string, EngineModelDefinition>
                {
                    ["test-model"] = new EngineModelDefinition { Name = "test-model" },
                },
            },
        };

    private static InferenceRequest Request(string model, string tag) => new()
    {
        Model = model,
        Stream = false,
        Body = Encoding.UTF8.GetBytes(
            $$"""{"model":"{{model}}","stream":false,"messages":[{"role":"user","content":"{{tag}}"}]}"""),
        Options = new RouterRequestOptions(),
    };

    private static InferenceRequest Request(string tag) => Request("slow-model", tag);

    private static async Task WaitHealthyAsync(RouterCore router)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && !router.Engines["fake"].Healthy)
            await Task.Delay(25);
        Assert.True(router.Engines["fake"].Healthy);
    }
}
