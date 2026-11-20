using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class ConfigReloadWireTests
{
    private const int AdminBodyCap = 1024;

    [Theory(Timeout = 30_000)]
    [InlineData(false, AdminBodyCap, false)]
    [InlineData(true, AdminBodyCap, false)]
    [InlineData(false, AdminBodyCap + 1, false)]
    [InlineData(true, AdminBodyCap + 1, false)]
    [InlineData(true, AdminBodyCap + 1, true)]
    [InlineData(false, 1, true)]
    public async Task AdminThreads_EnforcesConfiguredBodyCapBeforeRegistration(bool chunked, int size, bool malformed)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = bound.Token;
        var (app, _, _) = await WireHarness.StartAsync(holdCompletions: false, maxRequestBodyBytes: AdminBodyCap);
        await using var ownedApp = app;
        using var http = Client(app);
        var router = app.Services.GetRequiredService<RouterCore>();
        const string threadId = "body-cap-thread";
        using var request = AdminBodyRequest("/admin/threads", """{"threadId":"body-cap-thread"}""",
            chunked, size, malformed);
        Assert.Equal(chunked ? null : (long?)size, request.Content!.Headers.ContentLength);
        using var response = await http.SendAsync(request, ct);
        var refused = malformed || size > AdminBodyCap;
        if (refused)
        {
            await AssertAdminBodyErrorAsync(response, chunked, size, ct);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
        }

        using var status = JsonDocument.Parse(await http.GetByteArrayAsync("/admin/status", ct));
        var threads = status.RootElement.GetProperty("Threads").EnumerateArray();
        var registered = router.Events.Replay(0).Where(evt => evt.Kind == "thread.registered" && evt.ThreadId == threadId);
        if (refused)
        {
            Assert.DoesNotContain(threads, thread => thread.GetProperty("ThreadId").GetString() == threadId);
            Assert.Empty(registered);
        }
        else
        {
            Assert.Single(threads, thread => thread.GetProperty("ThreadId").GetString() == threadId);
            Assert.Single(registered);
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false, AdminBodyCap, false)]
    [InlineData(true, AdminBodyCap, false)]
    [InlineData(false, AdminBodyCap + 1, false)]
    [InlineData(true, AdminBodyCap + 1, false)]
    [InlineData(true, AdminBodyCap + 1, true)]
    [InlineData(false, 1, true)]
    public async Task AdminSwitch_EnforcesConfiguredBodyCapBeforeTakingAdminUse(bool chunked, int size, bool malformed)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = bound.Token;
        var path = Path.Combine(_dir.FullName, "router.toml");
        const string retainedConfig = """
            default_model = "a"
            max_request_body_bytes = 1024
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
            """;
        using var fake = new FakeEngineHandler { LoadedModels = ["a"] };
        using var spectator = new FakeEngineHandler { LoadedModels = ["spectator-model"] };
        var provider = new HeldNativeProvider(fake);
        await File.WriteAllTextAsync(path, retainedConfig + "\n" + External("spectator", "spectator-model"), ct);
        await using var app = await StartAsync(path, definition => definition.Name == "native" ? fake : spectator,
            ct, _ => provider);
        using var http = Client(app);
        var router = app.Services.GetRequiredService<RouterCore>();
        try
        {
            await WaitUntilAsync(() => router.Engines.Values.All(engine => engine.Healthy), ct);
            Assert.Equal(AdminBodyCap, router.MaxRequestBodyBytes);
            Assert.True(router.Engines.ContainsKey("spectator"));
            var native = router.Engines["native"];
            using var request = AdminBodyRequest("/admin/switch", """{"engine":"native","model":"b"}""", chunked,
                size, malformed);
            Assert.Equal(chunked ? null : (long?)size, request.Content!.Headers.ContentLength);
            using var response = await http.SendAsync(request, ct);
            var refused = malformed || size > AdminBodyCap;
            if (refused)
            {
                await AssertAdminBodyErrorAsync(response, chunked, size, ct);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                using var ack = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                Assert.Equal("native", ack.RootElement.GetProperty("Engine").GetString());
                Assert.Equal("b", ack.RootElement.GetProperty("Model").GetString());
                Assert.True(ack.RootElement.GetProperty("Switching").GetBoolean());
            }

            // A topology change checks the admin lease even if the deferred switch has yet to run.
            // If the switch did start, the fake holds its launch until this check finishes.
            var candidate = RouterConfigLoader.LoadForReload(retainedConfig, path);
            var generation = router.CurrentGeneration.Number;
            var result = await router.ApplyConfigurationAsync(candidate, generation, ct);
            Assert.Same(native, router.Engines["native"]);
            if (refused)
            {
                Assert.Equal((ConfigReloadStatus.Applied, generation + 1), result);
                Assert.False(router.Engines.ContainsKey("spectator"));
                Assert.Equal(0, provider.LaunchCount);
                Assert.DoesNotContain(router.Events.Replay(0),
                    evt => evt.Kind == "engine.switch_started" && evt.Engine == "native" && evt.Model == "b");
            }
            else
            {
                Assert.Equal((ConfigReloadStatus.Busy, generation), result);
                Assert.True(router.Engines.ContainsKey("spectator"));
                await provider.Launched.Task.WaitAsync(ct);
                Assert.Equal(1, provider.LaunchCount);
                Assert.Equal("b", provider.Target!.Model);
            }
        }
        finally { provider.Release.TrySetResult(); }
    }

    private static HttpRequestMessage AdminBodyRequest(string path, string validJson, bool chunked, int size, bool malformed)
    {
        var bytes = new byte[size];
        bytes.AsSpan().Fill((byte)' ');
        var json = Encoding.UTF8.GetBytes(malformed ? "{" : validJson);
        json.CopyTo(bytes, 0);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = chunked ? new ChunkedAdminBody(bytes) : new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (chunked) request.Headers.TransferEncodingChunked = true;
        return request;
    }

    private static async Task AssertAdminBodyErrorAsync(HttpResponseMessage response, bool chunked, int size,
        CancellationToken ct)
    {
        var oversized = size > AdminBodyCap;
        Assert.Equal(oversized ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        var root = document.RootElement;
        Assert.Equal("error", Assert.Single(root.EnumerateObject()).Name);
        var error = root.GetProperty("error");
        var fields = error.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["code", "message", "type"], fields);
        Assert.Equal("air_error", error.GetProperty("type").GetString());
        Assert.Equal(oversized ? "request_body_too_large" : "invalid_request", error.GetProperty("code").GetString());
        var message = error.GetProperty("message").GetString();
        if (!oversized)
        {
            Assert.Equal("body is not valid JSON", message);
            return;
        }
        var prefix = chunked
            ? "the request body exceeded 1024 bytes"
            : "the request body is 1025 bytes and this router reads at most 1024";
        Assert.StartsWith(prefix, message, StringComparison.Ordinal);
    }

    private sealed class ChunkedAdminBody(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length - 1), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await stream.WriteAsync(bytes.AsMemory(bytes.Length - 1), cancellationToken);
        }
    }
}
