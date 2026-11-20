using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>
/// The error contract an OpenAI-compatible client can rely on: status comes from the code, and the
/// body is lowercase `{"error":{"type":"air_error","code":…,"message":…}}` on **every** surface
/// (SPEC §12). Task-10 AC #4 pins this table so a rename on one side fails a test.
/// </summary>
public class ErrorContractTests
{
    private const int UpstreamErrorExcerptBytes = 256;
    private const string ErrorBodyRequest = """{"model":"test-model","messages":[]}""";

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
    // EndToEndTests.Queue_Overflow_Fails_Ticket and in EventRingTests, which also pins its sentence), and reaches
    // the client through the same WriteErrorAsync writer as every case above, so its body shape is covered here;
    // driving it over HTTP would need a held-connection pool dance for no added coverage.

    /// <summary>§10, TASK-93 AC #6, end to end. An upstream engine's own words reach the caller embedded in the
    /// router's sentence (§6.1), and their length is nobody's choice but the caller's. The criterion is the whole
    /// contract, not an exception's <c>Message</c>: the <c>/v1</c> status, the one lowercase shape, the message
    /// length, and a ceiling on the <b>serialized bytes</b> — escaping means the body is longer than the character
    /// count, so a character assertion alone would let the wire grow while the test stayed green.
    /// This test bounds the reported error. The counted-stream fixture below measures upstream consumption.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AnUpstreamBodyOfAnyLengthLeavesTheCallersErrorBodyBounded()
    {
        using var fake = new FakeEngineHandler
        {
            CompletionStatus = 400,
            CompletionErrorBody = "{\"error\":{\"type\":\"server_error\",\"message\":\"" + new string('m', 5_000) + "\"}}",
        };
        var (app, _, port) = await StartAsync(fake);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var res = await http.PostAsync("/v1/chat/completions",
                new StringContent("""{"model":"test-model","messages":[]}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            var body = Encoding.UTF8.GetString(bytes);
            AssertShape(body, "engine_error");

            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.GetProperty("error").GetProperty("message").GetString()!;
            Assert.True(message.Length <= WireText.ReasonChars,
                $"the caller's error message is {message.Length} characters, over the documented {WireText.ReasonChars}");
            Assert.Contains("server_error", message);       // "the engine said this" still reads
            Assert.DoesNotContain(new string('m', 400), message);   // …and the caller did not choose the size

            // The ceiling in bytes: the message is at most ReasonChars characters, and one character costs at
            // most six bytes once JSON escapes it (\uXXXX), so the body cannot exceed that plus its envelope —
            // whatever the caller's text happens to contain.
            Assert.True(bytes.Length <= WireText.ReasonChars * 6 + 256,
                $"the serialized error body is {bytes.Length} bytes, over the ceiling of {WireText.ReasonChars * 6 + 256}");
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    [Fact(Timeout = 30_000)]
    public async Task UpstreamErrorBodyReadsAtMost256Bytes()
    {
        // The UTF-8 sequence at byte 256 crosses the boundary. Preserve the existing replacement fallback.
        var head = new string('x', UpstreamErrorExcerptBytes - 1);
        var bytes = Encoding.UTF8.GetBytes(head + "é" + new string('y', 4_743));
        using var upstream = new CountedErrorBody(bytes);
        using var errorContent = new StreamContent(upstream);
        Assert.Null(errorContent.Headers.ContentLength);
        using var fake = new ErrorBodyEngine(errorContent);
        var (app, _, port) = await WireHarness.StartAsync(fake: fake);
        await using var server = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new StringContent(ErrorBodyRequest, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("/v1/chat/completions", request, bound.Token);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(bound.Token);
        using var actual = JsonDocument.Parse(body);
        using var expected = JsonDocument.Parse(
            $$"""{"error":{"message":"400: {{head}}\uFFFD","code":"engine_error","type":"air_error"}}""");
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), body);

        Assert.Equal(UpstreamErrorExcerptBytes, upstream.BytesConsumed);
        Assert.Equal(UpstreamErrorExcerptBytes, upstream.FurthestRequestedPosition);
        Assert.Equal(0, upstream.ReadsAtOrPastCeiling);
    }

    [Fact(Timeout = 30_000)]
    public async Task UpstreamErrorBodyStallingBelowTheReadCeilingTimesOut()
    {
        using var upstream = new CountedErrorBody("short error"u8.ToArray(), holdAtEnd: true);
        using var errorContent = new StreamContent(upstream);
        using var fake = new ErrorBodyEngine(errorContent);
        var clock = new ReadinessDeadlineTests.ManualClock();
        var (app, _, port) = await WireHarness.StartAsync(fake: fake, responseTimeoutMs: 100);
        await using var server = app;
        app.Services.GetRequiredService<RouterCore>().ResponseTimeoutClockForTests = clock;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new StringContent(ErrorBodyRequest, Encoding.UTF8, "application/json");
        var response = http.PostAsync("/v1/chat/completions", request, bound.Token);

        try
        {
            await upstream.HeldReadEntered.Task.WaitAsync(bound.Token);
            Assert.Equal("short error"u8.Length, upstream.BytesConsumed);
            clock.AdvanceTo(99);
            Assert.False(response.IsCompleted);

            clock.AdvanceTo(100);
            using var timedOut = await response.WaitAsync(bound.Token);
            Assert.Equal(HttpStatusCode.GatewayTimeout, timedOut.StatusCode);
            AssertShape(await timedOut.Content.ReadAsStringAsync(bound.Token), "engine_timeout");
        }
        finally
        {
            upstream.ReleaseHeldRead();
        }
    }

    private sealed class ErrorBodyEngine(StreamContent content) : FakeEngineHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post) return base.SendAsync(request, cancellationToken);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = content });
        }
    }

    // StreamContent passes this source through for streaming reads and copies from it for full reads.
    // Route both reader overloads through the counter, which remains available after disposal.
    private sealed class CountedErrorBody(byte[] bytes, bool holdAtEnd = false) : Stream
    {
        private const int ShortReadBytes = 17;
        private readonly TaskCompletionSource _releaseHeldRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _bytesConsumed;
        private int _furthestRequestedPosition;
        private int _readsAtOrPastCeiling;

        public TaskCompletionSource HeldReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BytesConsumed => Volatile.Read(ref _bytesConsumed);
        public int FurthestRequestedPosition => Volatile.Read(ref _furthestRequestedPosition);
        public int ReadsAtOrPastCeiling => Volatile.Read(ref _readsAtOrPastCeiling);

        public void ReleaseHeldRead() => _releaseHeldRead.TrySetResult();

        public override int Read(byte[] buffer, int offset, int count) => CountAndCopy(buffer.AsSpan(offset, count));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (holdAtEnd && BytesConsumed == bytes.Length)
            {
                HeldReadEntered.TrySetResult();
                await _releaseHeldRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return CountAndCopy(buffer.Span);
        }

        private int CountAndCopy(Span<byte> buffer)
        {
            var position = BytesConsumed;
            Volatile.Write(ref _furthestRequestedPosition, Math.Max(FurthestRequestedPosition, position + buffer.Length));
            if (position >= UpstreamErrorExcerptBytes) Interlocked.Increment(ref _readsAtOrPastCeiling);
            var count = Math.Min(ShortReadBytes, Math.Min(buffer.Length, bytes.Length - position));
            bytes.AsSpan(position, count).CopyTo(buffer);
            Volatile.Write(ref _bytesConsumed, position + count);
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>§10, AC #6, the writer's own ceiling: an admin refusal that quotes the caller's own rejected value.
    /// The writer, not the caller, decides how much of it the body carries — a 4,000-character profile name is a
    /// legal thing to send, and the answer is still one bounded lowercase body. Take the cap out of the writer and
    /// this is the pin that goes red.</summary>
    [Fact(Timeout = 30_000)]
    public async Task ACallerCannotChooseTheSizeOfAnAdminRefusalThatQuotesItsOwnValue()
    {
        using var fake = new FakeEngineHandler();
        var (app, _, port) = await StartAsync(fake);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var longProfile = new string('p', 4_000);
            using var content = new StringContent(
                "{\"engine\":\"fake\",\"model\":\"test-model\",\"profile\":\"" + longProfile + "\"}",
                Encoding.UTF8, "application/json");
            var res = await http.PostAsync("/admin/switch", content);

            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            var body = Encoding.UTF8.GetString(bytes);
            AssertShape(body, "unknown_profile");

            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.GetProperty("error").GetProperty("message").GetString()!;
            Assert.DoesNotContain(longProfile, message);
            Assert.Contains("… (+", message);              // cut, and it says how much is missing
            Assert.True(message.Length <= WireText.ReasonChars,
                $"the caller's error message is {message.Length} characters, over the documented {WireText.ReasonChars}");
            Assert.True(bytes.Length <= WireText.ReasonChars * 6 + 256,
                $"the serialized error body is {bytes.Length} bytes, over the ceiling of {WireText.ReasonChars * 6 + 256}");
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }

    /// <summary>§10, AC #6, the other producer of a long sentence: a caller's text inside a sentence this assembly
    /// writes, with no ticket and no terminal path involved. Nothing upstream bounds a 4,000-character path
    /// segment, so the ceiling has to be the router's, and the shape stays the contract.</summary>
    [Fact(Timeout = 30_000)]
    public async Task TheErrorWriterCapsTextTheRouterNeverWrote()
    {
        using var fake = new FakeEngineHandler();
        var (app, _, port) = await StartAsync(fake);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var longId = new string('q', 4_000);
            var res = await http.GetAsync($"/admin/tickets/{longId}");

            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            var body = Encoding.UTF8.GetString(bytes);
            AssertShape(body, "not_found");

            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.GetProperty("error").GetProperty("message").GetString()!;
            Assert.DoesNotContain(longId, message);
            Assert.Contains("… (+", message);
            Assert.True(message.Length <= WireText.ReasonChars,
                $"the caller's error message is {message.Length} characters, over the documented {WireText.ReasonChars}");
            Assert.True(bytes.Length <= WireText.ReasonChars * 6 + 256,
                $"the serialized error body is {bytes.Length} bytes, over the ceiling of {WireText.ReasonChars * 6 + 256}");
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
