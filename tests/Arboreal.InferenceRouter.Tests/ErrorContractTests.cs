using System.Net;
using System.Net.Sockets;
using System.Text;
using Arboreal.InferenceRouter;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>
/// The error contract an OpenAI-compatible client can rely on: status comes from the code, and the
/// body is lowercase `{"error":{"type":"air_error","code":…,"message":…}}` on **every** surface
/// (SPEC §12). Task-10 AC #4 pins this table so a rename on one side fails a test.
/// </summary>
public class ErrorContractTests
{

    [Fact(Timeout = 30_000)]
    public async Task AnOversizedBodyIsRefusedWithTheErrorContract_NotAnOpaqueHostFailure()
    {
        // The router buffers a request body to forward it, so this cap is also the bound on what one
        // request can make the process hold. Kestrel's own limit would answer something a caller cannot
        // branch on; the router answers the shape every other error uses.
        var (app, _, port) = await WireHarness.StartAsync(holdCompletions: false, maxRequestBodyBytes: 2048);
        using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var oversized = """
            {"model":"test-model","messages":[{"role":"user","content":"X"}]}
            """.Trim().Replace("X", new string('x', 4096));
        using var content = new StringContent(oversized, Encoding.UTF8, "application/json");
        var res = await http.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains("request_body_too_large", text);
        Assert.Contains("2048", text);
        Assert.DoesNotContain("\"Error\"", text);        // error bodies stay lowercase on every surface
    }
    private static async Task<(WebApplication App, FakeEngineHandler Fake, int Port)> StartAsync(
        FakeEngineHandler fake, int maxInflight = 2, int maxQueue = 4)
    {
        int port;
        using (var latch = new TcpListener(IPAddress.Loopback, 0))
        {
            latch.Start();
            port = ((IPEndPoint)latch.LocalEndpoint).Port;
            latch.Stop();
        }

        var cfg = new RouterConfig
        {
            Listen = $"http://127.0.0.1:{port}",
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
        var app = ServerApp.Build([], configOverride: cfg);
        await app.StartAsync();
        return (app, fake, port);
    }

    private static void AssertShape(string body, string code)
    {
        Assert.Contains("\"error\"", body);
        Assert.Contains("\"message\"", body);
        Assert.Contains($"\"code\":\"{code}\"", body);
        Assert.Contains("\"type\":\"air_error\"", body);
        Assert.DoesNotContain("\"Error\"", body);
        Assert.DoesNotContain("\"Code\"", body);
        Assert.DoesNotContain("inference_router_error", body);
    }

    [Fact]
    public async Task Malformed_Body_Is_400_Invalid_Request()
    {
        using var fake = new FakeEngineHandler();
        var (app, _, port) = await StartAsync(fake);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var res = await http.PostAsync("/v1/chat/completions",
                new StringContent("{not json", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            AssertShape(await res.Content.ReadAsStringAsync(), "invalid_request");
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Unknown_Admin_Ticket_Is_404_Not_Found()
    {
        using var fake = new FakeEngineHandler();
        var (app, _, port) = await StartAsync(fake);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var res = await http.GetAsync("/admin/tickets/t99999999-dead");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            // the admin surface shares the lowercase error shape with /v1
            AssertShape(await res.Content.ReadAsStringAsync(), "not_found");
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Upstream_Failure_Is_502_Engine_Error_And_Keeps_The_Reason()
    {
        using var fake = new FakeEngineHandler { CompletionStatus = 400 };
        var (app, _, port) = await StartAsync(fake);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var res = await http.PostAsync("/v1/chat/completions",
                new StringContent("""{"model":"test-model","messages":[]}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            AssertShape(body, "engine_error");
            // the upstream reason survives, so the caller can tell "bad request" from "engine down"
            Assert.Contains("messages must be a non-empty list", body);
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Fact(Timeout = 30_000)]
    public async Task AModelNothingHereCanServeIsRefusedWithTheDeclaredUnavailable503()
    {
        // §6.1 declares model_unavailable → 503 and MapStatus maps it, and until this change nothing in
        // the router emitted it: the shape already existed, so the refusal adds no wire field. The 503 is
        // the honest code — retryable if the configuration changes — and it costs the caller no queue time.
        using var fake = new FakeEngineHandler { LoadedModels = ["test-model"] };
        var (app, _, port) = await StartAsync(fake);
        using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        await WaitEngineHealthyAsync(http);

        using var content = new StringContent(
            """{"model":"not-here","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json");
        var res = await http.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        AssertShape(body, "model_unavailable");
        Assert.Contains("not-here", body);
    }

    [Fact(Timeout = 30_000)]
    public async Task AStreamedRequestForAnUnservableModelEndsInOneErrorFrameNotASilent200()
    {
        // A stream has committed its 200 before the router knows the model is unservable, so the failure
        // has to reach the caller inside the stream (§5.5): an error frame naming the code, then the end.
        using var fake = new FakeEngineHandler { LoadedModels = ["test-model"] };
        var (app, _, port) = await StartAsync(fake);
        using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        await WaitEngineHealthyAsync(http);

        var (_, res) = await WireHarness.StartStreamAsync(http, "t-stream-unknown",
            """{"model":"not-here","stream":true,"messages":[{"role":"user","content":"hi"}]}""");
        var lines = await ReadUntilQuietAsync(res, TimeSpan.FromSeconds(10));
        Assert.Contains(lines, l => l.Contains("model_unavailable", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("[DONE]", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("\"Error\"", StringComparison.Ordinal));
        res.Dispose();
    }

    private static async Task<List<string>> ReadUntilQuietAsync(HttpResponseMessage res, TimeSpan bound)
    {
        using var cts = new CancellationTokenSource(bound);
        await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var lines = new List<string>();
        while (true)
        {
            string? line;
            try { line = await reader.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            if (line is null) break;                // the stream closed: the caller can stop waiting
            lines.Add(line);
        }

        return lines;
    }

    private static async Task WaitEngineHealthyAsync(HttpClient http)
    {
        // The refusal is only ever correct once an engine has answered the model probe, so a wire test
        // that wants one has to wait for that answer rather than race the first probe.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var json = await http.GetStringAsync("/admin/status");
            if (json.Contains("\"Healthy\":true", StringComparison.Ordinal)) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("the fake engine never reported healthy in /admin/status");
    }

    // 429 queue_overflow is refused by the queue (pinned at library level in
    // EndToEndTests.Queue_Overflow_Fails_Ticket) and reaches the client through the same
    // WriteErrorAsync writer as every case above, so its body shape is covered here; driving it
    // over HTTP would need a held-connection pool dance for no added coverage.

}
