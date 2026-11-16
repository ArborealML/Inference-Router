using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>
/// The agent-facing contract we ship (samples/pi/inference-router-tools.json + the thread/priority
/// vocabulary) must keep matching the running server. Every path and method in the manifest is
/// replayed against a live ServerApp, so a renamed route or a phantom tool fails the build.
/// </summary>
public class AgentSurfaceTests
{
    private static string? RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static async Task<(WebApplication App, FakeEngineHandler Fake, int Port)> StartHeldServerAsync()
    {
        var fake = new FakeEngineHandler { HoldCompletions = true };
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

    private static string Name(JsonElement tool) => tool.GetProperty("name").GetString()!;

    private static string Normalise(string path)
    {
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        return path;
    }

    [Fact]
    public async Task Shipped_Tool_Manifest_Describes_The_Running_Surface()
    {
        var file = RepoFile("samples/pi/inference-router-tools.json");
        Assert.NotNull(file);
        using var doc = JsonDocument.Parse(File.ReadAllText(file!));
        var root = doc.RootElement;

        // the manifest's default front door must be the port the server binds by default
        Assert.Equal(new RouterConfig().Listen, root.GetProperty("server_url").GetString());
        Assert.Equal("X-Inference-Thread", root.GetProperty("thread_header").GetString());

        var tools = root.GetProperty("tools").EnumerateArray().ToList();
        Assert.True(tools.Count >= 6, "manifest must describe the whole surface");

        // priority classes named in the manifest are exactly the classes the router parses
        var classes = tools.Single(t => Name(t) == "register_thread")
            .GetProperty("parameters").GetProperty("properties").GetProperty("priority")
            .GetProperty("properties").GetProperty("class").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        foreach (var name in new[] { "background", "low", "normal", "high", "interactive" })
            Assert.Contains(name, classes);
        Assert.All(classes, c => Assert.True(ThreadPriorityClass.TryParseClass(c, out _), c));

        var (app, _, port) = await StartHeldServerAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            // a held streaming request gives us a live ticket id to point the ticket tools at
            string ticketId;
            using (var probe = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions"))
            {
                probe.Headers.Add("X-Inference-Thread", "agent-test");
                probe.Content = Json("{\"model\":\"test-model\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hold\"}]}");
                using var res = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                ticketId = res.Headers.TryGetValues("X-Inference-Ticket", out var v)
                    ? v.Single() : throw new InvalidOperationException("no X-Inference-Ticket header");
            }

            var exercised = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in tools)
            {
                var method = tool.GetProperty("method").GetString()!;
                var rawPath = tool.GetProperty("path").GetString()!;
                var path = rawPath.Replace("{id}", ticketId).Replace("{since_seq}", "0");
                using var req = new HttpRequestMessage(new HttpMethod(method), path);

                switch (Name(tool))
                {
                    case "chat_completion":
                        req.Headers.Add("X-Inference-Thread", "agent-test");
                        req.Content = Json("{\"model\":\"test-model\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
                        break;
                    case "register_thread":
                        req.Content = Json("{\"threadId\":\"agent-test\",\"priority\":{\"class\":\"interactive\"}}");
                        break;
                    case "switch_model":
                        req.Content = Json("{\"engine\":\"fake\",\"model\":\"test-model\"}");
                        break;
                    case "inspect_ticket":
                    case "cancel_ticket":
                        Assert.Equal(ticketId, path["/admin/tickets/".Length..]);
                        break;
                }

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                exercised.Add(Normalise(rawPath));

                // an unmapped route answers 404 with no body; the router's own 404 carries not_found
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    Assert.Contains("\"not_found\"", body);
                }

                switch (Name(tool))
                {
                    case "chat_completion":
                        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                        Assert.True(resp.Headers.TryGetValues("X-Inference-Ticket", out var ids)
                                    && ids.Count() == 1, "streaming responses must expose the ticket");
                        break;
                    case "list_models":
                    case "engine_status":
                    case "read_events":
                    case "inspect_ticket":
                        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                        break;
                    case "cancel_ticket":
                        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
                        break;
                }
            }

            // two tools share /admin/tickets/{id} (inspect + cancel), so paths may repeat
            var declared = tools.Select(t => Normalise(t.GetProperty("path").GetString()!)).Distinct().ToList();
            Assert.Equal(tools.Count, declared.Count + 1);
            Assert.True(exercised.SetEquals(declared),
                $"manifest paths {string.Join(",", declared)} vs probed {string.Join(",", exercised)}");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
