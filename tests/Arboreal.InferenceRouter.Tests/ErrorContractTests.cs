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

    // 429 queue_overflow is refused by the queue (pinned at library level in
    // EndToEndTests.Queue_Overflow_Fails_Ticket) and reaches the client through the same
    // WriteErrorAsync writer as every case above, so its body shape is covered here; driving it
    // over HTTP would need a held-connection pool dance for no added coverage.

}
