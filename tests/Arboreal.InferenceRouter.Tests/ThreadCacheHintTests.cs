using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Arboreal.InferenceRouter.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed class ThreadCacheHintTests
{
    private const string Header = "X-Inference-Thread";

    [Fact(Timeout = 30_000)]
    public async Task HttpCompletionsAndStreamsScopeHintsToLocalGroupedEngines()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var first = new Observer { LoadedModels = ["first"] };
        using var second = new Observer { LoadedModels = ["second"] };
        using var external = new Observer { LoadedModels = ["external"] };
        using var legacy = new Observer { LoadedModels = ["legacy"] };
        var handlers = new Dictionary<string, Observer>
        {
            ["first"] = first, ["second"] = second, ["external"] = external, ["legacy"] = legacy,
        };
        var config = new RouterConfig
        {
            Listen = "http://127.0.0.1:0",
            AutoSwitch = true,
            StreamBufferLimitBytes = 32768,
            HttpHandlerFactory = def => handlers[def.Name],
            Engines = handlers.Keys.ToDictionary(name => name, name => Definition(name, new Uri("http://127.0.0.1:9/v1"))),
        };
        await using var app = ServerApp.Build([], null, config, TimeProvider.System,
            lifecycleFactory: _ => new RecordingLifecycleProvider { Kind = "docker" });
        await app.StartAsync(deadline.Token);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        try
        {
            var firstHint = await Send("first", "private-42");
            Assert.Equal("9e4947e723a82182cd5d88aacbc544dfa40f9518640302461dd9f5d9085bba64", firstHint);
            Assert.Equal(firstHint, await Send("first", "private-42", stream: true));
            var otherThread = await Send("first", "private-43");
            var otherEngine = await Send("second", "private-42");
            Assert.Matches("^[a-f0-9]{64}$", otherThread);
            Assert.Matches("^[a-f0-9]{64}$", otherEngine);
            Assert.NotEqual(firstHint, otherThread);
            Assert.NotEqual(firstHint, otherEngine);
            Assert.Null(await Send("first", null));
            var router = app.Services.GetRequiredService<RouterCore>();
            router.BeforeAdmissionCommitForTests = pending =>
            {
                if (pending.IsStateless)
                    router.RegisterThread(pending.ThreadId, ThreadPriority.Normal());
            };
            try { Assert.Null(await Send("first", null, stream: true)); }
            finally { router.BeforeAdmissionCommitForTests = null; }
            Assert.Null(await Send("first", "anon"));
            Assert.Null(await Send("external", "private-42"));
            Assert.Null(await Send("legacy", "private-42"));
            foreach (var handler in handlers.Values)
            {
                Assert.Contains(handler.Observed, r => r.Method == HttpMethod.Get);
                Assert.All(handler.Observed.Where(r => r.Method == HttpMethod.Get), r => Assert.Null(r.Hint));
            }
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }

        async Task<string?> Send(string model, string? thread, bool stream = false)
        {
            // The model route is chosen from the incoming body. Capture exactly what reaches the engine.
            string body = $$"""{"model":"{{model}}","messages":[{"role":"user","content":"question"}],"stream":{{(stream ? "true" : "false")}}}""";
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Add("X-Private-Caller", "session display title");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer caller-value");
            if (thread is not null) request.Headers.Add(Header, thread);
            using var response = await client.SendAsync(request, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            if (stream) Assert.Contains("[DONE]", await response.Content.ReadAsStringAsync(deadline.Token));
            var observed = handlers[model].Observed.Last(r => r.Method == HttpMethod.Post);
            Assert.Equal(body, observed.Body);
            Assert.Equal("Bearer backend-value", observed.Authorization);
            Assert.False(observed.PrivateHeader);
            return observed.Hint;
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task ProductionLocalHandlerReturnsRedirectWithoutFollowingIt()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        destination.Start();
        var endpoint = new Uri($"http://{listener.LocalEndpoint}/v1");
        var def = Definition("local", endpoint);
        var config = new RouterConfig { Engines = new Dictionary<string, EngineDefinition> { ["local"] = def } };
        await using var runtime = new EngineRuntime(def, config, new EventRing(), handler: null,
            lifecycle: new RecordingLifecycleProvider { Kind = "docker" }, dispatchHint: null,
            hostEnrollment: null, localManaged: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, runtime.CompletionUri("chat/completions"));
        request.Headers.Add(Header, new string('a', 64));
        var send = runtime.Http.SendAsync(request, deadline.Token);
        using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
        using var transport = connection.GetStream();
        using var reader = new StreamReader(transport, leaveOpen: true);
        while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
        var redirect = Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://{destination.LocalEndpoint}/other\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await transport.WriteAsync(redirect, deadline.Token);
        using var response = await send;
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.False(destination.Pending());
    }

    private static EngineDefinition Definition(string name, Uri endpoint) => new()
    {
        Name = name,
        Endpoint = endpoint,
        ApiKey = "backend-value",
        LifecycleOwner = name switch
        {
            "external" => EngineLifecycleOwner.External,
            "legacy" => null,
            _ => EngineLifecycleOwner.Router,
        },
        Lifecycle = name == "external" ? EngineLifecycleKind.Delegated : EngineLifecycleKind.Docker,
        LifecycleGroup = name is "external" or "legacy" ? null : name,
        Docker = name == "external" ? null : new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" },
        Catalog = new Dictionary<string, EngineModelDefinition> { [name] = new() { Name = name } },
        Health = new HealthProbeDefinition { Path = "/props", IntervalMs = 50 },
    };

    private sealed record Observation(HttpMethod Method, string? Hint, string? Authorization, bool PrivateHeader, string? Body);

    private sealed class Observer : FakeEngineHandler
    {
        public ConcurrentQueue<Observation> Observed { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var hint = request.Headers.TryGetValues(Header, out var values) ? values.Single() : null;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Observed.Enqueue(new(request.Method, hint, request.Headers.Authorization?.ToString(),
                request.Headers.Contains("X-Private-Caller"), body));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
