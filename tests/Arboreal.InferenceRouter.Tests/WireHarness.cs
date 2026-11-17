using System.Net;
using System.Net.Sockets;
using Arboreal.InferenceRouter;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>One place that stands up a real ServerApp in-process against the fake engine, on a free
/// loopback port. Wire tests need the whole HTTP stack (headers, SSE framing, route matching), so they
/// all start here rather than each keeping a private copy that can drift from the others.</summary>
internal static class WireHarness
{
    public static async Task<(WebApplication App, FakeEngineHandler Fake, int Port)> StartAsync(
        bool holdCompletions = true, int maxInflight = 8)
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
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = maxInflight, MaxQueuePerModel = 8 },
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

    /// <summary>Start one streaming request and return its ticket id (X-Inference-Ticket) plus the
    /// response, whose body the caller drains when it wants the completion to finish.</summary>
    public static async Task<(string TicketId, HttpResponseMessage Response)> StartStreamAsync(
        HttpClient http, string thread, string content)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Headers.Add("X-Inference-Thread", thread);
        req.Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json");
        var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        var id = res.Headers.TryGetValues("X-Inference-Ticket", out var v)
            ? v.Single() : throw new InvalidOperationException("no X-Inference-Ticket header");
        return (id, res);
    }

    /// <summary>Read SSE `data:` frames until the stream goes quiet for idleMs, then give up (these
    /// routes live-tail by design, so "no more events" is only observable as silence).</summary>
    public static async Task<List<string>> ReadSseFramesAsync(
        HttpClient http, string path, int idleMs = 1200, int maxMs = 8000)
    {
        using var cts = new CancellationTokenSource(maxMs);
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var frames = new List<string>();
        while (true)
        {
            using var idle = new CancellationTokenSource(idleMs);
            string? line;
            try { line = await reader.ReadLineAsync(idle.Token); }
            catch (OperationCanceledException) { break; }   // quiet long enough: end of replay
            if (line is null) break;                        // stream closed
            if (line.StartsWith("data:", StringComparison.Ordinal)) frames.Add(line[5..].Trim());
        }
        return frames;
    }
}
