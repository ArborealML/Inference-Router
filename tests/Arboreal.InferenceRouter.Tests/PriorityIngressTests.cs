using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using Arboreal.InferenceRouter;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>The `X-Inference-Priority` edge on the inference surface (§6, §6.1). An absent header is the
/// configured default; a value the grammar does not admit is a 400 `invalid_priority` in the one lowercase
/// error envelope, written while the headers are still being read — no ticket, no thread registration, no
/// queue depth, no completion sent to an engine. Before this the router dropped the bad value and served the
/// request at the default, so a caller that asked for a boost could not tell it had not gotten one. The
/// refusal is read ahead of the body, so a bad body cannot answer first with 413 or `invalid_request`; that
/// precedence is pinned for this header alone and for nothing else.</summary>
public class PriorityIngressTests
{
    private const string ThreadId = "priority-ingress-thread";

    private static Task<(WebApplication App, FakeEngineHandler Fake, int Port)> StartAsync(
        int maxRequestBodyBytes = 16 * 1024 * 1024)
        => WireHarness.StartAsync(holdCompletions: false, maxRequestBodyBytes: maxRequestBodyBytes);

    /// <summary>A request whose body the test chooses, so the body's own defect (truncated JSON, a size over
    /// the cap) can be put in front of a malformed priority header.</summary>
    private static HttpRequestMessage WithBody(string thread, string? priorityValue, string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Headers.TryAddWithoutValidation("X-Inference-Thread", thread);
        if (priorityValue is not null)
            Assert.True(req.Headers.TryAddWithoutValidation("X-Inference-Priority", priorityValue),
                $"the test could not put X-Inference-Priority=\"{priorityValue}\" on the request");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return req;
    }

    private const string WellFormedBody = """{"model":"test-model","messages":[{"role":"user","content":"ping"}]}""";
    private const string WellFormedStreamBody = """{"model":"test-model","stream":true,"messages":[{"role":"user","content":"ping"}]}""";
    private const string TruncatedJsonBody = """{"model":"test-model","messages":[""";
    private const string TruncatedJsonStreamBody = """{"model":"test-model","stream":true,"messages":[""";

    /// <summary>A well-formed body with a `content` string long enough to pass the harness's
    /// `max_request_body_bytes`, streaming or not.</summary>
    private static string PaddedBody(int contentChars, bool stream = false)
        => "{\"model\":\"test-model\"," + (stream ? "\"stream\":true," : "") +
           "\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('x', contentChars) + "\"}]}";

    private static HttpRequestMessage Completion(string thread, bool stream, string headerName = "X-Inference-Priority",
        params string[] priorityValues)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        req.Headers.TryAddWithoutValidation("X-Inference-Thread", thread);
        foreach (var value in priorityValues)
        {
            Assert.True(req.Headers.TryAddWithoutValidation(headerName, value),
                $"the test could not put {headerName}=\"{value}\" on the request");
        }

        req.Content = new StringContent(stream ? WellFormedStreamBody : WellFormedBody,
            Encoding.UTF8, "application/json");
        return req;
    }

    private static async Task<JsonElement> StatsAsync(HttpClient http)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync("/admin/status"));
        return doc.RootElement.Clone();
    }

    /// <summary>The priority the router recorded for a thread, once it has appeared in the operator view.</summary>
    private static async Task<(byte Class, int Sub)> RecordedPriorityAsync(HttpClient http, string thread)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var entry in (await StatsAsync(http)).GetProperty("Threads").EnumerateArray())
            {
                if (!string.Equals(entry.GetProperty("ThreadId").GetString(), thread, StringComparison.Ordinal)) continue;
                var priority = entry.GetProperty("Priority");
                return (priority.GetProperty("Class").GetByte(), priority.GetProperty("Sub").GetInt32());
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"thread {thread} never appeared in /admin/status");
    }

    private static bool RegistersThread(JsonElement stats, string thread) =>
        stats.GetProperty("Threads").EnumerateArray().Any(entry =>
            string.Equals(entry.GetProperty("ThreadId").GetString(), thread, StringComparison.Ordinal));

    /// <summary>Zero scheduler mutation, proved by counters and absence rather than by a log line: the engine
    /// was asked for nothing more, the thread was never registered, no model queue holds a member, and the
    /// caller got no ticket id to watch or cancel. <paramref name="dispatches"/> is what the engine had already
    /// been asked for by earlier requests in the same test, so one server can show both a refusal and a
    /// neighbouring accepted request.</summary>
    private static async Task AssertNoEffectAsync(HttpClient http, FakeEngineHandler fake, HttpResponseMessage res,
        string thread, int dispatches = 0)
    {
        Assert.False(res.Headers.TryGetValues("X-Inference-Ticket", out _), "a refused request must not expose a ticket");
        Assert.Equal(dispatches, fake.CompletionCalls);
        var stats = await StatsAsync(http);
        if (dispatches == 0) Assert.Empty(stats.GetProperty("QueueDepths").EnumerateObject());
        Assert.False(RegistersThread(stats, thread));
        foreach (var engine in stats.GetProperty("Engines").EnumerateArray())
            Assert.Equal(0, engine.GetProperty("Inflight").GetInt32());
    }

    private static void AssertErrorShape(string body, string code)
    {
        Assert.Contains("\"error\"", body);
        Assert.Contains("\"message\"", body);
        Assert.Contains($"\"code\":\"{code}\"", body);
        Assert.Contains("\"type\":\"air_error\"", body);
        Assert.DoesNotContain("\"Error\"", body);              // the envelope stays lowercase (§6.1)
        Assert.DoesNotContain("\"Code\"", body);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnAbsentPriorityHeaderIsTheConfiguredDefault()
    {
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = Completion(ThreadId, stream: false);
        using var res = await http.SendAsync(request);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Contains("pong", await res.Content.ReadAsStringAsync());
        Assert.Equal(1, fake.CompletionCalls);
        var (cls, sub) = await RecordedPriorityAsync(http, ThreadId);
        Assert.Equal(ThreadPriorityClass.Normal, cls);
        Assert.Equal(0, sub);
    }

    [Fact(Timeout = 30_000)]
    public async Task AWellFormedPriorityIsSnapshottedWithItsThread()
    {
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = Completion(ThreadId, stream: false, priorityValues: ["interactive:200"]);
        using var res = await http.SendAsync(request);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Equal(1, fake.CompletionCalls);
        var (cls, sub) = await RecordedPriorityAsync(http, ThreadId);
        Assert.Equal(ThreadPriorityClass.Interactive, cls);
        Assert.Equal(200, sub);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("background:0", ThreadPriorityClass.Background, 0)]
    [InlineData("normal", ThreadPriorityClass.Normal, 0)]
    [InlineData("interactive:255", ThreadPriorityClass.Interactive, 255)]
    public async Task EveryAdmissibleClassAndSubPriorityIsAccepted(string value, byte expectedClass, int expectedSub)
    {
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = Completion(ThreadId, stream: false, priorityValues: [value]);
        using var res = await http.SendAsync(request);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Equal(1, fake.CompletionCalls);
        var (cls, sub) = await RecordedPriorityAsync(http, ThreadId);
        Assert.Equal(expectedClass, cls);
        Assert.Equal(expectedSub, sub);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    [InlineData("High")]
    [InlineData("NORMAL")]
    [InlineData("high:")]
    [InlineData("high:1junk")]
    [InlineData("high:256")]
    [InlineData("high:-1")]
    [InlineData(":5")]
    [InlineData("high:200:1")]
    [InlineData("high low")]
    [InlineData("3")]
    public async Task APriorityOutsideTheGrammarIsRefusedBeforeItReachesTheQueue(string value)
    {
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = Completion(ThreadId, stream: false, priorityValues: [value]);
        using var res = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertErrorShape(await res.Content.ReadAsStringAsync(), "invalid_priority");
        await AssertNoEffectAsync(http, fake, res, ThreadId);
    }

    [Fact(Timeout = 30_000)]
    public async Task TwoPriorityHeadersForOneRequestAreRefusedNotResolved()
    {
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = Completion(ThreadId, stream: false, priorityValues: ["high", "interactive:200"]);
        using var res = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertErrorShape(await res.Content.ReadAsStringAsync(), "invalid_priority");
        await AssertNoEffectAsync(http, fake, res, ThreadId);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheHeaderNameIsCaseInsensitiveAndTheClassNamesAreNot()
    {
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var acceptedRequest = Completion("priority-lower-name", stream: false,
            headerName: "x-inference-priority", priorityValues: ["high"]);
        using var accepted = await http.SendAsync(acceptedRequest);
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        Assert.Equal(1, fake.CompletionCalls);
        var (cls, _) = await RecordedPriorityAsync(http, "priority-lower-name");
        Assert.Equal(ThreadPriorityClass.High, cls);

        // The five class names are lowercase on the wire; a value cased differently is a present-but-invalid
        // value, so it is refused rather than read as a guess.
        using var refusedRequest = Completion("priority-cased-value", stream: false,
            headerName: "x-inference-priority", priorityValues: ["HIGH"]);
        using var refused = await http.SendAsync(refusedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        AssertErrorShape(await refused.Content.ReadAsStringAsync(), "invalid_priority");
        await AssertNoEffectAsync(http, fake, refused, "priority-cased-value", dispatches: 1);
    }

    [Fact(Timeout = 30_000)]
    public async Task AStreamingRequestWithABadPriorityEndsBeforeTheStreamStarts()
    {
        // A stream commits its 200 and its SSE headers on the first write, so the whole refusal argument is
        // that this request never gets that far: 400 answered with the ordinary JSON error body.
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = Completion(ThreadId, stream: true, priorityValues: ["high:1junk"]);
        using var res = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("[router keepalive", body);
        Assert.DoesNotContain("[DONE]", body);
        AssertErrorShape(body, "invalid_priority");
        await AssertNoEffectAsync(http, fake, res, ThreadId);

        // The feed is the scheduler's own record: no enqueue was published for a request that was refused at
        // the header, so a caller following the feed sees that the request never existed.
        var events = await WireHarness.ReadSseFramesAsync(http, "/admin/events?since=0");
        Assert.DoesNotContain(events, frame => frame.Contains("request.enqueued", StringComparison.Ordinal)
                                             && frame.Contains(ThreadId, StringComparison.Ordinal));
    }

    // ── the priority refusal precedes the body (§6): the header is the defect, so the header answers ──

    private const string MalformedPriority = "high:+12";

    /// <summary>The whole ordinary-path refusal, byte for byte: one lowercase envelope in the order the
    /// server writes it — error → message, code, type (§6.1). Written as a raw string so the backslash-u
    /// sequences are the bytes on the wire, including the encoder's escape of the quoted value's `'` and `+`.</summary>
    private const string MalformedPriorityRefusalBody =
        """{"error":{"message":"X-Inference-Priority \u0027high:\u002B12\u0027 is not one of background, low, normal, high, interactive with an optional :sub of 0-255.","code":"invalid_priority","type":"air_error"}}""";

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMalformedPriorityIsRefusedBeforeAnOversizedBodyIsRead(bool stream)
    {
        // The body cap used to answer first, so a caller with a bad header was told its body was too big —
        // the wrong defect, and the wrong fix. No byte of the body is read before the header is validated.
        var (app, fake, port) = await StartAsync(maxRequestBodyBytes: 2_048);
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = WithBody(ThreadId, MalformedPriority, PaddedBody(4_096, stream));
        using var res = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        AssertErrorShape(body, "invalid_priority");
        Assert.DoesNotContain("request_body_too_large", body);
        await AssertNoEffectAsync(http, fake, res, ThreadId);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMalformedPriorityIsRefusedBeforeAnInvalidBodyIsProbed(bool stream)
    {
        // The JSON probe used to answer 400 invalid_request for this pair, which reads as "your body is
        // malformed" to a caller whose body is fine and whose header is not.
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = WithBody(ThreadId, MalformedPriority, stream ? TruncatedJsonStreamBody : TruncatedJsonBody);
        using var res = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        AssertErrorShape(body, "invalid_priority");
        Assert.DoesNotContain("invalid_request", body);
        await AssertNoEffectAsync(http, fake, res, ThreadId);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(null)]
    [InlineData("interactive:200")]
    public async Task AnOversizedBodyIsStillRefusedWith413WhenThePriorityIsAbsentOrValid(string? priorityValue)
    {
        // The precedence moved for one header and for nothing else: an absent or well-formed priority leaves
        // the size cap exactly where it was.
        var (app, fake, port) = await StartAsync(maxRequestBodyBytes: 2_048);
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = WithBody(ThreadId, priorityValue, PaddedBody(4_096));
        using var res = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        AssertErrorShape(body, "request_body_too_large");
        Assert.Contains("2048", body);
        await AssertNoEffectAsync(http, fake, res, ThreadId);
    }

    [Fact(Timeout = 30_000)]
    public async Task AnInvalidBodyIsStillRefusedWithInvalidRequestWhenThePriorityIsAbsent()
    {
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = WithBody(ThreadId, null, TruncatedJsonBody);
        using var res = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        AssertErrorShape(body, "invalid_request");
        await AssertNoEffectAsync(http, fake, res, ThreadId);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheOrdinaryRefusalBodyIsThePinnedEnvelope()
    {
        // Not "contains the code" but the exact bytes, so a renamed key, a reordered field or a capitalized
        // envelope is a test failure rather than a wire break nobody noticed (§6.1).
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = WithBody(ThreadId, MalformedPriority, WellFormedBody);
        using var res = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(MalformedPriorityRefusalBody, await res.Content.ReadAsStringAsync());
        await AssertNoEffectAsync(http, fake, res, ThreadId);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheStreamingRefusalIsThatEnvelopeAndNoStreamFraming()
    {
        // A stream commits its 200 and its SSE framing on the first write. The refusal writes none of it:
        // the body is the same JSON error, field for field, with no `data:` frame anywhere in it.
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var request = WithBody(ThreadId, MalformedPriority, WellFormedStreamBody);
        using var res = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var body = Encoding.UTF8.GetString(bytes);

        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(3, error.EnumerateObject().Count());
        Assert.Equal(@$"X-Inference-Priority '{MalformedPriority}' is not one of background, low, normal, high, interactive with an optional :sub of 0-{ThreadPriority.MaxSubPriority}.",
            error.GetProperty("message").GetString());
        Assert.Equal("invalid_priority", error.GetProperty("code").GetString());
        Assert.Equal("air_error", error.GetProperty("type").GetString());

        Assert.DoesNotContain("data:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("[DONE]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("[router keepalive", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("retry:", body, StringComparison.Ordinal);
        // The two surfaces share one refusal body: the streaming answer is the ordinary answer's bytes.
        Assert.Equal(MalformedPriorityRefusalBody, body);
        await AssertNoEffectAsync(http, fake, res, ThreadId);
    }

    [Fact(Timeout = 30_000)]
    public async Task ARefusalQuotesTheRejectedValueAtTheRoutersLengthNotTheCallers()
    {
        // The refusal names what the caller sent, so the quote has to be bounded where the router writes it:
        // a four-thousand-character header value cannot choose the size of the answer (§10).
        var (app, fake, port) = await StartAsync();
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var longValue = new string('q', 4_000);
        using var request = Completion(ThreadId, stream: false, priorityValues: [longValue]);
        using var res = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        var bytes = Encoding.UTF8.GetBytes(body);
        AssertErrorShape(body, "invalid_priority");
        using var doc = JsonDocument.Parse(body);
        var message = doc.RootElement.GetProperty("error").GetProperty("message").GetString()!;
        Assert.DoesNotContain(longValue, message);
        Assert.Contains("… (+", message);
        Assert.True(message.Length <= WireText.ReasonChars, $"message is {message.Length} characters");
        Assert.True(bytes.Length <= WireText.ReasonChars * 6 + 256, $"body is {bytes.Length} bytes");
        await AssertNoEffectAsync(http, fake, res, ThreadId);
    }
}
