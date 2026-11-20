using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed class ConfigReloadWireTests : IDisposable
{
    private const string ReloadPath = "/admin/config/reload";
    private const string Token = "offline-reload-credential";
    private const string Initial = "default_model = \"a\"\n";
    private static readonly int[] ContendingStatuses = [200, 412];
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("air-reload-wire-");

    [Fact(Timeout = 30_000)]
    public async Task DefaultCutoverKeepsTheOriginalStreamBytesAndBusyRemovalPreservesTheGeneration()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = bound.Token;
        var path = Path.Combine(_dir.FullName, "router.toml");
        var cut = Gate();
        var finish = Gate();
        var first = "data: {\"delta\":\"é🌲\"}\r\n\r\n"u8.ToArray();
        var suffix = "data: exact\n\ndata: [DONE]\n\n"u8.ToArray();
        using var a = new WireEngine("a", first, suffix, cut, finish.Task);
        using var b = new WireEngine("b");
        var declarations = External("one", "a") + External("two", "b");
        await File.WriteAllTextAsync(path, Initial + declarations, ct);
        await using var app = await StartAsync(path, def => def.Name == "one" ? a : b, ct);
        using var http = Client(app);
        var router = app.Services.GetRequiredService<RouterCore>();
        await WaitUntilAsync(() => router.Engines.Values.All(engine => engine.Healthy), ct);
        var (firstTag, _) = await ReadGenerationAsync(http, ct);

        var (ticket, completion) = await WireHarness.StartStreamAsync(http, "old", """{"stream":true,"prompt":"old"}""", ct);
        using var response = completion;
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        try
        {
            await cut.Task.WaitAsync(ct);
            Assert.Equal(first, await ReadUpstreamBlockAsync(body, ct));
            await File.WriteAllTextAsync(path, "default_model = \"b\"\n" + declarations, ct);
            using var changed = await ApplyAsync(http, firstTag, ct);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.Equal("{\"Generation\":2}", await changed.Content.ReadAsStringAsync(ct));

            using var freshBody = new StringContent("""{"prompt":"fresh"}""", Encoding.UTF8, "application/json");
            using var fresh = await http.PostAsync("/v1/completions", freshBody, ct);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            Assert.Equal("""{"prompt":"fresh","model":"b"}""", Assert.Single(b.Requests));
            Assert.Equal("""{"stream":true,"prompt":"old","model":"a"}""", Assert.Single(a.Requests));

            await File.WriteAllTextAsync(path, "default_model = \"b\"\n" + External("two", "b"), ct);
            using var busy = await ApplyAsync(http, changed.Headers.ETag!.Tag, ct);
            await AssertErrorAsync(busy, HttpStatusCode.Conflict, "reload_busy", ct);
            Assert.Equal(2L, (await ReadGenerationAsync(http, ct)).Generation);
            Assert.True(router.Engines.ContainsKey("one"));

            finish.TrySetResult();
            using var remaining = new MemoryStream();
            await body.CopyToAsync(remaining, ct);
            Assert.Equal(suffix, remaining.ToArray());
            Assert.Equal("Completed", router.TryGetTicket(ticket)?.State);
            Assert.Single(router.Events.Replay(0), evt => evt.TicketId == ticket && evt.Kind == "request.completed");
            await WaitUntilAsync(() => router.Engines.Values.All(engine => engine.Inflight == 0 && engine.InferenceUses == 0), ct);

            var old = router.Engines["one"];
            using var removed = await ApplyAsync(http, changed.Headers.ETag.Tag, ct);
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.Equal(3L, (await ReadGenerationAsync(http, ct)).Generation);
            Assert.False(router.Engines.ContainsKey("one"));
            Assert.False(old.TryBeginForward(ct));
        }
        finally { finish.TrySetResult(); }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidStaleAndNoOpAppliesPreserveTheGeneration(bool json)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = bound.Token;
        var path = Path.Combine(_dir.FullName, json ? "router.json" : "router.toml");
        var config = json
            ? """{"default_model":"a","engine":{"one":{"kind":"openai-compatible","endpoint":"http://127.0.0.1:9/v1","remote":true,"catalog":{"a":{},"b":{}}}}}"""
            : Initial + External("one", "a", "b");
        using var fake = new FakeEngineHandler { LoadedModels = ["a", "b"] };
        await File.WriteAllTextAsync(path, config, ct);
        await using var app = await StartAsync(path, _ => fake, ct);
        using var http = Client(app);
        var router = app.Services.GetRequiredService<RouterCore>();
        await WaitUntilAsync(() => router.Engines.Values.All(engine => engine.Healthy), ct);
        var (tag, _) = await ReadGenerationAsync(http, ct);

        using var unauthorized = await http.GetAsync(ReloadPath, ct);
        await AssertErrorAsync(unauthorized, HttpStatusCode.Unauthorized, "reload_unauthorized", ct);
        Assert.Equal("Bearer", Assert.Single(unauthorized.Headers.WwwAuthenticate).Scheme);
        using var malformedTag = await ApplyAsync(http, "W/" + tag, ct);
        await AssertErrorAsync(malformedTag, HttpStatusCode.BadRequest, "invalid_reload_request", ct);
        using var previousProcess = await ApplyAsync(http, $"\"reload-{Guid.NewGuid():N}-1\"", ct);
        await AssertErrorAsync(previousProcess, HttpStatusCode.PreconditionFailed, "reload_stale", ct);

        await File.WriteAllTextAsync(path, json ? """{"path-that-must-stay-private":""" : "[path-that-must-stay-private", ct);
        using var invalid = await ApplyAsync(http, tag, ct);
        await AssertErrorAsync(invalid, HttpStatusCode.UnprocessableEntity, "reload_invalid_config", ct);
        Assert.DoesNotContain("path-that-must-stay-private", await invalid.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal((tag, 1L), await ReadGenerationAsync(http, ct));

        var restartOnly = config.Replace("127.0.0.1:9", "127.0.0.1:8", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, restartOnly, ct);
        using var refused = await ApplyAsync(http, tag, ct);
        await AssertErrorAsync(refused, HttpStatusCode.UnprocessableEntity, "reload_requires_restart", ct);

        if (json)
        {
            await File.WriteAllTextAsync(path, config.Replace("\"default_model\":\"a\"", "\"default_model\":\"b\",\"default_model\":\"a\"", StringComparison.Ordinal), ct);
            using var duplicate = await ApplyAsync(http, tag, ct);
            await AssertErrorAsync(duplicate, HttpStatusCode.UnprocessableEntity, "reload_invalid_config", ct);
        }

        await File.WriteAllTextAsync(path, config, ct);
        using var identical = await ApplyAsync(http, tag, ct);
        Assert.Equal(HttpStatusCode.OK, identical.StatusCode);
        Assert.Equal(tag, identical.Headers.ETag!.Tag);

        var changed = json ? config.Replace("\"default_model\":\"a\"", "\"default_model\":\"b\"", StringComparison.Ordinal)
            : config.Replace(Initial, "default_model = \"b\"\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, changed, ct);
        var first = ApplyAsync(http, tag, ct);
        var second = ApplyAsync(http, tag, ct);
        using var firstReply = await first;
        using var secondReply = await second;
        Assert.Equal(ContendingStatuses, new[] { (int)firstReply.StatusCode, (int)secondReply.StatusCode }.Order());
        Assert.Equal(2L, (await ReadGenerationAsync(http, ct)).Generation);
    }

    [Fact(Timeout = 30_000)]
    public async Task NativeClaimAndJoinKeepTheirOriginalRuntimeAndTargetAcrossTheRealApply()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = bound.Token;
        var path = Path.Combine(_dir.FullName, "router.toml");
        const string native = """
            [engine.native]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"
            lifecycle_owner = "router"
            lifecycle = "docker"
            [engine.native.docker]
            image = "offline-image"
            router_id = "offline-owner"
            [engine.native.health_probe]
            interval_ms = 3600000
            [engine.native.catalog.a]
            [engine.native.catalog.b]
            profile = "original-options"
            aliases = ["original-alias"]
            """;
        using var fake = new FakeEngineHandler { LoadedModels = ["a"] };
        var provider = new HeldNativeProvider(fake);
        await File.WriteAllTextAsync(path, Initial + native, ct);
        await using var app = await StartAsync(path, _ => fake, ct, _ => provider);
        using var http = Client(app);
        var router = app.Services.GetRequiredService<RouterCore>();
        await WaitUntilAsync(() => router.Engines.Values.All(engine => engine.Healthy), ct);
        var engine = router.Engines["native"];
        var (tag, _) = await ReadGenerationAsync(http, ct);

        var first = engine.WaitForModelAsync("b", Timeout.InfiniteTimeSpan, ct);
        try
        {
            await provider.Launched.Task.WaitAsync(ct);
            var joined = engine.WaitForModelAsync("b", Timeout.InfiniteTimeSpan, ct);
            await File.WriteAllTextAsync(path, "default_model = \"b\"\n" + native, ct);
            using var applied = await ApplyAsync(http, tag, ct);
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
            Assert.Same(engine, router.Engines["native"]);
            engine.Definition.Catalog["b"].Aliases[0] = "mutated external observation";
            router.Config.DefaultModel = "unpublished";
            Assert.Equal("b", router.CurrentGeneration.Settings.DefaultModel);
            await File.WriteAllTextAsync(path, "default_model = \"b\"\n" +
                native.Replace("original-options", "replacement-options", StringComparison.Ordinal), ct);
            using var rejected = await ApplyAsync(http, applied.Headers.ETag!.Tag, ct);
            await AssertErrorAsync(rejected, HttpStatusCode.UnprocessableEntity, "reload_requires_restart", ct);

            provider.Release.TrySetResult();
            Assert.True((await first.WaitAsync(ct)).IsReady);
            Assert.True((await joined.WaitAsync(ct)).IsReady);
            Assert.Equal("b", provider.Target!.Model);
            Assert.Equal("original-options", provider.Target.EffectiveProfile);
            Assert.Equal("original-alias", Assert.Single(provider.Target.Definition.Aliases));
            Assert.Single(router.Events.Replay(0), evt => evt.Kind == "engine.switch_started");

            using var freshBody = new StringContent("""{"prompt":"new"}""", Encoding.UTF8, "application/json");
            using var fresh = await http.PostAsync("/v1/completions", freshBody, ct);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            Assert.Equal(1, fake.CompletionCalls);
        }
        finally { provider.Release.TrySetResult(); }
    }

    [Fact(Timeout = 20_000)]
    public async Task IdleMembershipAndDefaultPublishTogetherBeforeFirstDiscovery()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var path = Path.Combine(_dir.FullName, "router.toml");
        using var a = new WireEngine("a");
        using var b = new WireEngine("b");
        var detecting = Gate();
        var publish = Gate();
        using var c = new WireEngine("c") { BeforeModels = async token =>
        {
            detecting.TrySetResult();
            await publish.Task.WaitAsync(token);
        } };
        await File.WriteAllTextAsync(path, Initial + External("one", "a") + External("two", "b"), ct);
        await using var app = await StartAsync(path, def => def.Name switch
        {
            "one" => a,
            "two" => b,
            _ => c,
        }, ct);
        using var http = Client(app);
        var router = app.Services.GetRequiredService<RouterCore>();
        await WaitUntilAsync(() => router.Engines.Values.All(engine => engine.Healthy), ct);
        router.RegisterThread("retired-affinity", ThreadPriority.Normal(), new RouterThreadOptions { ModelAffinity = "a" });
        var (tag, _) = await ReadGenerationAsync(http, ct);
        try
        {
            await File.WriteAllTextAsync(path, "default_model = \"c\"\n" + External("two", "b") + External("three", "c"), ct);
            using var applied = await ApplyAsync(http, tag, ct);
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
            await detecting.Task.WaitAsync(ct);
            Assert.False(router.Engines["three"].Healthy);
            Assert.Equal("c", router.Config.DefaultModel);
            var discovery = await http.GetStringAsync("/v1/models", ct);
            Assert.Contains("\"id\":\"b\"", discovery, StringComparison.Ordinal);
            Assert.DoesNotContain("\"id\":\"a\"", discovery, StringComparison.Ordinal);
            Assert.DoesNotContain("\"id\":\"c\"", discovery, StringComparison.Ordinal);

            publish.TrySetResult();
            await WaitUntilAsync(() => router.Engines["three"].Healthy, ct);
            using var oldAffinity = new HttpRequestMessage(HttpMethod.Post, "/v1/completions")
            {
                Content = new StringContent("""{"prompt":"preserve affinity"}""", Encoding.UTF8, "application/json"),
            };
            oldAffinity.Headers.Add("X-Inference-Thread", "retired-affinity");
            using var unavailable = await http.SendAsync(oldAffinity, ct);
            await AssertErrorAsync(unavailable, HttpStatusCode.ServiceUnavailable, "model_unavailable", ct);
            using var freshBody = new StringContent("""{"prompt":"new topology"}""", Encoding.UTF8, "application/json");
            using var fresh = await http.PostAsync("/v1/completions", freshBody, ct);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            Assert.Equal("""{"prompt":"new topology","model":"c"}""", Assert.Single(c.Requests));
            Assert.Empty(a.Requests);
        }
        finally { publish.TrySetResult(); }
    }

    [Fact(Timeout = 20_000)]
    public async Task AConfigOverrideHasNoFileReloadRoute()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var fake = new FakeEngineHandler();
        var config = RouterConfigLoader.Load(Initial + External("one", "a"));
        await using var app = ServerApp.Build(["--urls", "http://127.0.0.1:0"], null, config,
            TimeProvider.System, httpHandlerFactoryForTests: _ => fake, reloadTokenForTests: Token);
        await app.StartAsync(ct);
        using var http = Client(app);
        using var disabled = await ApplyAsync(http, "*", ct);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
    }

    private static async Task<WebApplication> StartAsync(string path, Func<EngineDefinition, HttpMessageHandler> handler,
        CancellationToken ct, Func<EngineDefinition, IEngineLifecycleProvider>? lifecycle = null)
    {
        var app = ServerApp.Build(["--urls", "http://127.0.0.1:0"], path, null, TimeProvider.System, lifecycle, handler, Token);
        try
        {
            await app.StartAsync(ct);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };

    private static async Task<(string Tag, long Generation)> ReadGenerationAsync(HttpClient http, CancellationToken ct)
    {
        using var request = ReloadRequest(HttpMethod.Get, null);
        using var response = await http.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        return (response.Headers.ETag!.Tag, json.RootElement.GetProperty("Generation").GetInt64());
    }

    private static async Task<HttpResponseMessage> ApplyAsync(HttpClient http, string tag, CancellationToken ct)
    {
        using var request = ReloadRequest(HttpMethod.Post, tag);
        return await http.SendAsync(request, ct);
    }

    private static HttpRequestMessage ReloadRequest(HttpMethod method, string? tag)
    {
        var request = new HttpRequestMessage(method, ReloadPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return request;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code,
        CancellationToken ct)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Null(response.Headers.ETag);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
        Assert.Equal("air_error", error.GetProperty("type").GetString());
    }

    internal static string External(string name, params string[] models) =>
        FormattableString.Invariant($"""
            [engine.{name}]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"
            lifecycle_owner = "external"
            [engine.{name}.health_probe]
            interval_ms = 3600000

            """) + string.Concat(models.Select(model => $"[engine.{name}.catalog.\"{model}\"]\n"));

    internal static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static async Task ProbeAllAsync(RouterCore core, CancellationToken ct)
    {
        foreach (var engine in core.Engines.Values)
            await engine.ProbeHealthOnceAsync(ct);
        Assert.All(core.Engines.Values, engine => Assert.True(engine.Healthy));
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static async Task<byte[]> ReadUpstreamBlockAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var unit = new byte[1];
        while (true)
        {
            await body.ReadExactlyAsync(unit, ct);
            buffer.WriteByte(unit[0]);
            var block = buffer.ToArray();
            if (!block.AsSpan().EndsWith("\n\n"u8) && !block.AsSpan().EndsWith("\r\n\r\n"u8)) continue;
            if (!block.AsSpan().StartsWith(": [router keepalive"u8)) return block;
            buffer.SetLength(0);
        }
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private sealed class WireEngine(string model, byte[]? first = null, byte[]? suffix = null,
        TaskCompletionSource? cut = null, Task? resume = null) : HttpMessageHandler
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();
        internal Func<CancellationToken, Task>? BeforeModels { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models", StringComparison.Ordinal))
            {
                if (BeforeModels is { } before) await before(cancellationToken);
                return Reply(string.Create(CultureInfo.InvariantCulture, $"{{\"data\":[{{\"id\":\"{model}\"}}]}}"));
            }
            if (path.EndsWith("/props", StringComparison.Ordinal)) return Reply("""{"total_slots":2}""");

            Requests.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (first is null) return Reply("{}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GatedSse(first, suffix!, cut!, resume!))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") },
                },
            };
        }

        private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private sealed class GatedSse(byte[] head, byte[] tail, TaskCompletionSource cut, Task resume) : Stream
    {
        private int _index;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_index == head.Length)
            {
                cut.TrySetResult();
                await resume.WaitAsync(cancellationToken);
            }
            if (_index == head.Length + tail.Length) return 0;
            buffer.Span[0] = _index < head.Length ? head[_index] : tail[_index - head.Length];
            _index++;
            return 1;
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HeldNativeProvider(FakeEngineHandler upstream) : IEngineLifecycleProvider
    {
        public string Kind => "docker";
        internal TaskCompletionSource Launched { get; } = Gate();
        internal TaskCompletionSource Release { get; } = Gate();
        internal LifecycleTarget? Target { get; private set; }
        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult(LifecycleResult.Ok());
        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);
        public ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct) =>
            ValueTask.FromResult(LifecycleResult.Ok());

        public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            Target = target;
            Launched.TrySetResult();
            await Release.Task.WaitAsync(ct);
            upstream.LoadedModels = [target.Model];
            return LifecycleResult.Ok();
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.FromSeconds(5));
    }
}
