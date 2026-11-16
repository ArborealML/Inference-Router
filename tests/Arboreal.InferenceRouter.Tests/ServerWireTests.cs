using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>Full wire-surface test: ServerApp.Build on a random localhost port, real HTTP
/// requests, a fake engine behind HttpMessageHandler. Covers passthrough, SSE, admin API.</summary>
public class ServerWireTests
{
    private static async Task<(WebApplication App, FakeEngineHandler Fake, int Port)> StartServerAsync(bool holdCompletions = false)
    {
        var fake = new FakeEngineHandler { HoldCompletions = holdCompletions };
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
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 8, MaxQueuePerModel = 8 },
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

    private static HttpClient Client(int port)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        return http;
    }

    private static async Task<int> FreePortAsync()
    {
        using var latch = new TcpListener(IPAddress.Loopback, 0);
        latch.Start();
        var port = ((IPEndPoint)latch.LocalEndpoint).Port;
        latch.Stop();
        return port;
    }

    private static RouterConfig WireConfig(int listenPort, FakeEngineHandler fake)
    {
        var cfg = new RouterConfig
        {
            Listen = $"http://127.0.0.1:{listenPort}",
            DefaultModel = "test-model",
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 8, MaxQueuePerModel = 8 },
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
        return cfg;
    }

    /// <summary>An explicit --urls (CLI --listen) replaces the config listen address, not adds to it.</summary>
    [Fact]
    public async Task CommandLine_Urls_Override_Config_Listen()
    {
        var configPort = await FreePortAsync();
        var urlPort = await FreePortAsync();
        using var fake = new FakeEngineHandler();
        var app = ServerApp.Build([$"--urls=http://127.0.0.1:{urlPort}"], configOverride: WireConfig(configPort, fake));
        await app.StartAsync();
        await using var _ = app;
        using var http = Client(urlPort);
        using var res = await http.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<Exception>(() => probe.ConnectAsync(IPAddress.Loopback, configPort).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    /// <summary>/admin/status carries the live serving gauges read from the engine probe (§7.6).</summary>
    [Fact]
    public async Task Status_Exports_Serving_Gauges()
    {
        var port = await FreePortAsync();
        using var fake = new FakeEngineHandler { NCtx = 131_072, TotalSlots = 3 };
        var app = ServerApp.Build([], configOverride: WireConfig(port, fake));
        await app.StartAsync();
        await using var _ = app;
        using var http = Client(port);

        long window = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var res = await http.GetAsync("/admin/status");
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var engine = doc.RootElement.GetProperty("Engines")[0];
            Assert.True(engine.TryGetProperty("ActiveContextWindow", out var w));
            Assert.True(engine.TryGetProperty("ActiveSlots", out var sl));
            window = w.GetInt64();
            Assert.Equal(3, sl.GetInt32());
            if (window > 0) break;
            await Task.Delay(50);
        }
        Assert.Equal(131_072, window);
    }

    [Fact]
    public async Task Chat_Passthrough_Completes()
    {
        var (app, _, port) = await StartServerAsync();
        await using var _ = app;
        using var http = Client(port);
        using var res = await http.PostAsync("/v1/chat/completions",
            new StringContent("""{"model":"test-model","messages":[{"role":"user","content":"ping"}]}""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("pong", body);
        Assert.Equal("application/json", res.Content!.Headers!.ContentType!.MediaType);
    }

    [Fact]
    public async Task Invalid_Json_Returns_400()
    {
        var (app, _, port) = await StartServerAsync();
        await using var _ = app;
        using var http = Client(port);
        using var res = await http.PostAsync("/v1/chat/completions",
            new StringContent("not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("invalid_request", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Thread_And_Priority_Headers_Admit_Admin_Thread()
    {
        var (app, fake, port) = await StartServerAsync(holdCompletions: true);
        await using var _ = app;
        using var http = Client(port);

        fake.HoldCompletions = true;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Content = new StringContent("""{"model":"test-model","messages":[{"role":"user","content":"ping"}]}""",
            Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("X-Inference-Thread", "chat-ui");
        req.Headers.TryAddWithoutValidation("X-Inference-Priority", "interactive:200");
        var sending = Task.Run(() => http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead));

        // registered thread shows up in stats with the wire-parsed priority (poll until registered)
        var status = "";
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline)
        {
            status = await http.GetStringAsync("/admin/status");
            if (status.Contains("chat-ui")) break;
            await Task.Delay(50);
        }
        Assert.True(status.Contains("\"ThreadId\":\"chat-ui\""), $"status={status}");
        Assert.Contains("\"Sub\":200", status);

        var events = await ReadSseBoundedAsync(http, "/admin/events?since=0");
        Assert.Contains("request.enqueued", events);

        fake.ReleaseGate();
        using var res = await sending;
        var body = await res.Content!.ReadAsStringAsync();
        Assert.Contains("pong", body);
        sending.Dispose();
    }

    [Fact]
    public async Task Streaming_SSE_Carries_Keepalive_Then_Frames()
    {
        var (app, fake, port) = await StartServerAsync(holdCompletions: true);
        await using var _ = app;
        using var http = Client(port);

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Content = new StringContent(
            """{"model":"test-model","stream":true,"messages":[{"role":"user","content":"ping"}]}""",
            Encoding.UTF8, "application/json");
        using var sending = Task.Run(() => http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead));
        await Task.Delay(400);
        fake.ReleaseGate();
        using var res = await sending;
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content!.Headers!.ContentType!.MediaType);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"delta\"", body);   // upstream SSE frames pass through verbatim
    }

    [Fact]
    public async Task Admin_Ticket_And_Cancel()
    {
        var (app, fake, port) = await StartServerAsync(holdCompletions: true);
        await using var _ = app;
        using var http = Client(port);

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Content = new StringContent("""{"model":"test-model","messages":[{"role":"user","content":"ping"}]}""",
            Encoding.UTF8, "application/json");
        using var sending = Task.Run(() => http.SendAsync(req));

        var events = await ReadSseBoundedAsync(http, "/admin/events?since=0");
        var ticketId = ExtractTicketId(events);
        Assert.False(string.IsNullOrEmpty(ticketId));

        using var ticketRes = await http.GetAsync($"/admin/tickets/{Uri.EscapeDataString(ticketId!)}");
        Assert.Equal(HttpStatusCode.OK, ticketRes.StatusCode);
        Assert.Contains("\"State\":\"Executing\"", await ticketRes.Content.ReadAsStringAsync());

        using var notFound = await http.GetAsync("/admin/tickets/nope");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        fake.ReleaseGate();
        using var done = await sending;
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }

    private static async Task<string> ReadSseBoundedAsync(HttpClient http, string path)
    {
        using var res = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await res.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var sb = new StringBuilder();
        var deadline = Environment.TickCount64 + 2_000;
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) break;
            string? line;
            try
            {
                // bounded read without a stray Task.Delay timer
                line = await reader.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(remaining, 1, 2_000)));
            }
            catch (TimeoutException) { break; }
            if (line is null) break;
            sb.AppendLine(line);
            if (sb.Length > 8_000) break;
        }
        return sb.ToString();
    }

    private static string? ExtractTicketId(string sseReplay)
    {
        foreach (var line in sseReplay.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line["data: ".Length..]);
                if (doc.RootElement.TryGetProperty("TicketId", out var t) &&
                    t.ValueKind == JsonValueKind.String)
                    return t.GetString();
            }
            catch (JsonException) { }
        }
        return null;
    }

    [Fact]
    public async Task Admin_Switch_Acks()
    {
        var (app, _, port) = await StartServerAsync();
        await using var _ = app;
        using var http = Client(port);
        using var res = await http.PostAsync("/admin/switch",
            new StringContent("""{"engine":"fake","model":"test-model"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Contains("Switching", await res.Content.ReadAsStringAsync());

        using var bad = await http.PostAsync("/admin/switch",
            new StringContent("""{"engine":"nope","model":"x"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Admin_Threads_Register()
    {
        var (app, _, port) = await StartServerAsync();
        await using var _ = app;
        using var http = Client(port);
        using var res = await http.PostAsync("/admin/threads",
            new StringContent("""{"threadId":"ui","priority":{"class":"high","sub":10},"weight":2}""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var status = await http.GetStringAsync("/admin/status");
        Assert.Contains("\"ThreadId\":\"ui\"", status);
        Assert.Contains("\"Sub\":10", status);
    }
}
