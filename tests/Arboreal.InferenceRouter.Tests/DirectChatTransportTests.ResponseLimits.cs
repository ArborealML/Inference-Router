using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DirectChatTransportTests
{
    [Theory(Timeout = 20_000)]
    [InlineData(false, "malformed", 400, "commercial_provider_bad_request")]
    [InlineData(true, "malformed", 200, "commercial_provider_bad_request")]
    [InlineData(false, "busy", 503, "commercial_provider_unavailable")]
    [InlineData(true, "busy", 200, "commercial_provider_unavailable")]
    [InlineData(false, "quota", 429, "commercial_rate_limited")]
    [InlineData(true, "quota", 200, "commercial_rate_limited")]
    [InlineData(false, "unknown", 502, "commercial_exchange_failed")]
    [InlineData(true, "unknown", 200, "commercial_exchange_failed")]
    public async Task ProviderCodesKeepTheirSanitizedClassificationInJsonAndSse(bool stream, string providerCode, int status,
        string routerCode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        var error = $$$"""{"error":{"code":"{{{providerCode}}}","message":"private provider diagnostics"}}""";
        var capture = new Capture(stream ? Started + "data: " + error + "\n\n" : error, stream);
        await using var upstream = await capture.StartAsync(ct);
        var text = Declaration(new(upstream.Urls.Single())) + """
            [commercial_provider.fixture.response.codes]
            malformed = "bad_request"
            busy = "unavailable"
            quota = "rate_limited"
            """;
        await using var app = await RouterAsync(RouterConfigLoader.Load(text), PermitAll(), ct);
        using var transport = DirectHandler();
        using var client = ClientAt(app, transport);
        using var result = await SendAsync(client, Chat, Request("public", stream), ct);
        Assert.Equal(status, (int)result.StatusCode);
        var delivered = await result.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"code\":\"" + routerCode + "\"", delivered, StringComparison.Ordinal);
        Assert.DoesNotContain("private provider diagnostics", delivered, StringComparison.Ordinal);
        Assert.Equal(stream, delivered.Contains("\"allowed\"", StringComparison.Ordinal));
        var ticket = await TicketAsync(client, result, ct);
        Assert.Equal(routerCode, ticket.GetProperty("ErrorCode").GetString());
        Assert.True(ticket.GetProperty("CommercialMayHaveSent").GetBoolean());
        Assert.Single(capture.Requests);
    }

    [Theory(Timeout = 22_000)]
    [InlineData("idle")]
    [InlineData("progress")]
    [InlineData("total")]
    public async Task StreamProgressHasIndependentDeadlinesAndAStallReleasesThePhysicalSlot(string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(14));
        var ct = deadline.Token;
        var chunks = Channel.CreateUnbounded<string>();
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        var capture = new Capture(string.Empty, stream: true)
        {
            Write = async context =>
            {
                if (Interlocked.Increment(ref invocations) != 1)
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(Standard, context.RequestAborted);
                    return;
                }
                try
                {
                    await context.Response.WriteAsync(Started, context.RequestAborted);
                    await foreach (var chunk in chunks.Reader.ReadAllAsync(context.RequestAborted))
                        await context.Response.WriteAsync(chunk, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    aborted.TrySetResult();
                }
            },
        };
        await using var upstream = await capture.StartAsync(ct);
        var text = Declaration(new(upstream.Urls.Single())).Replace("allows_streaming = true",
            "allows_streaming = true\nfirst_output_ms = 100\nsemantic_idle_ms = 1000\nmaximum_stream_ms = 2500", StringComparison.Ordinal);
        var config = RouterConfigLoader.Load(text);
        config.Scheduler.MaxInflightPerEngine = 1;
        await using var app = await RouterAsync(config, PermitAll(), ct);
        var clock = new ReadinessDeadlineTests.ManualClock();
        var core = app.Services.GetRequiredService<RouterCore>();
        core.ResponseTimeoutClockForTests = clock;
        using var transport = DirectHandler();
        using var client = ClientAt(app, transport);
        using var input = new HttpRequestMessage(HttpMethod.Post, Chat)
        {
            Content = new StringContent(Request("public", stream: true), Encoding.UTF8, "application/json"),
        };
        try
        {
            using var response = await client.SendAsync(input, HttpCompletionOption.ResponseHeadersRead, ct);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(body);
            await ReadUntilAsync("allowed");
            clock.AdvanceTo(800);
            if (mode == "idle")
            {
                await chunks.Writer.WriteAsync(": fixture heartbeat\n\n", ct);
                await ReadUntilAsync("fixture heartbeat");
                var successor = SendAsync(client, Chat, Request("public"), ct);
                while (!core.Events.Replay(0).Any(static row => row.Kind == "request.enqueued" && row.ThreadId == "sensitive-thread"))
                    await Task.Delay(TimeSpan.FromMilliseconds(2), ct);
                Assert.Single(capture.Requests);
                Assert.False(successor.IsCompleted);
                Assert.Equal(1, core.Engines["fixture"].Inflight);
                clock.AdvanceTo(999);
                Assert.False(successor.IsCompleted);
                clock.AdvanceTo(1000);
                Assert.Contains("commercial_stream_idle_timeout", await reader.ReadToEndAsync(ct), StringComparison.Ordinal);
                await aborted.Task.WaitAsync(ct);
                using var next = await successor.WaitAsync(TimeSpan.FromSeconds(4), ct);
                Assert.Equal(HttpStatusCode.OK, next.StatusCode);
                Assert.Equal(Standard, await next.Content.ReadAsStringAsync(ct));
                Assert.Equal(2, capture.Requests.Count);
                Assert.True((await TicketAsync(client, response, ct)).GetProperty("CommercialMayHaveSent").GetBoolean());
                return;
            }

            await SendProgressAsync("second");
            clock.AdvanceTo(1600);
            await SendProgressAsync("third");
            if (mode == "progress")
            {
                await chunks.Writer.WriteAsync(Done, ct);
                chunks.Writer.TryComplete();
                Assert.EndsWith(Done, await reader.ReadToEndAsync(ct), StringComparison.Ordinal);
                Assert.Equal("Completed", (await TicketAsync(client, response, ct)).GetProperty("State").GetString());
            }
            else
            {
                clock.AdvanceTo(2400);
                await SendProgressAsync("fourth");
                clock.AdvanceTo(2499);
                Assert.Equal(1, core.Engines["fixture"].Inflight);
                clock.AdvanceTo(2500);
                Assert.Contains("commercial_stream_deadline", await reader.ReadToEndAsync(ct), StringComparison.Ordinal);
                await aborted.Task.WaitAsync(ct);
            }
            Assert.Single(capture.Requests);

            async Task SendProgressAsync(string text)
            {
                await chunks.Writer.WriteAsync(Started.Replace("allowed", text, StringComparison.Ordinal), ct);
                await ReadUntilAsync(text);
            }

            async Task ReadUntilAsync(string text)
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                    if (line.Contains(text, StringComparison.Ordinal)) return;
                Assert.Fail("the expected verified stream text never reached the caller");
            }
        }
        finally { chunks.Writer.TryComplete(); }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, "missing")]
    [InlineData(false, "noninteger")]
    [InlineData(false, "out_of_range")]
    [InlineData(false, "duplicate")]
    [InlineData(true, "missing")]
    [InlineData(true, "noninteger")]
    [InlineData(true, "out_of_range")]
    [InlineData(true, "duplicate")]
    public async Task UnverifiedChoiceIndexesDoNotReleaseTheirStreamBlock(bool afterProgress, string invalid)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        var choices = invalid switch
        {
            "missing" => "{\"delta\":{\"content\":\"blocked\"}}",
            "noninteger" => "{\"index\":0.5,\"delta\":{\"content\":\"blocked\"}}",
            "out_of_range" => "{\"index\":1,\"delta\":{\"content\":\"blocked\"}}",
            _ => "{\"index\":0,\"delta\":{\"content\":\"blocked\"}},{\"index\":0,\"delta\":{}}",
        };
        var malformed = "data: {\"model\":\"actual\",\"choices\":[" + choices + "]}\n\n";
        var capture = new Capture((afterProgress ? Started : string.Empty) + malformed + Done, stream: true);
        await using var upstream = await capture.StartAsync(ct);
        await using var app = await RouterAsync(RouterConfigLoader.Load(Declaration(new(upstream.Urls.Single()))), PermitAll(), ct);
        using var transport = DirectHandler();
        using var client = ClientAt(app, transport);
        using var response = await SendAsync(client, Chat, Request("public", stream: true), ct);
        var delivered = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(afterProgress, delivered.Contains("allowed", StringComparison.Ordinal));
        Assert.DoesNotContain("blocked", delivered, StringComparison.Ordinal);
        Assert.DoesNotContain(Done, delivered, StringComparison.Ordinal);
        Assert.Contains("commercial_exchange_failed", delivered, StringComparison.Ordinal);
        var ticket = await TicketAsync(client, response, ct);
        Assert.Equal(afterProgress ? JsonValueKind.Number : JsonValueKind.Null,
            ticket.GetProperty("CommercialEvidence").GetProperty("FirstSemanticMilliseconds").ValueKind);
        Assert.Single(capture.Requests);
    }

    [Fact(Timeout = 20_000)]
    public async Task StreamForTwoRequestedChoicesAcceptsBothIndexesAndUsageOnlyFrames()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        var second = Started.Replace("\"index\":0", "\"index\":1", StringComparison.Ordinal);
        var whole = Meta + second + Started + Done;
        var capture = new Capture(string.Empty, stream: true)
        {
            Write = async context =>
            {
                var splitAt = Meta.Length + second.Length - 3;
                await context.Response.WriteAsync(whole[..splitAt], context.RequestAborted);
                await context.Response.WriteAsync(whole[splitAt..], context.RequestAborted);
            },
        };
        await using var upstream = await capture.StartAsync(ct);
        await using var app = await RouterAsync(RouterConfigLoader.Load(Declaration(new(upstream.Urls.Single()))), PermitAll(), ct);
        using var transport = DirectHandler();
        using var client = ClientAt(app, transport);
        using var response = await SendAsync(client, Chat, Request("public", stream: true).Insert(1, "\"n\":2,"), ct);
        Assert.Equal(whole, StripRouterComments(await response.Content.ReadAsStringAsync(ct)));
        Assert.Equal("Completed", (await TicketAsync(client, response, ct)).GetProperty("State").GetString());
        using var sent = JsonDocument.Parse(Assert.Single(capture.Requests).Body);
        Assert.Equal(2, sent.RootElement.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task ValidFragmentedChoiceIndexesAndUsageRemainAcceptedWithoutRenewingOnMalformedActivity()
    {
        var provider = RouterConfigLoader.Load(Declaration(new Uri("http://127.0.0.1:14141")))
            .CommercialProviders["fixture"] with { SemanticIdleTimeout = TimeSpan.FromSeconds(1) };
        var clock = new ReadinessDeadlineTests.ManualClock();
        var model = provider.Models["public"];
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        using var validation = new DirectChatResponse(provider, model, clock, 0b11, CancellationToken.None);
        validation.Bind(response);
        var observer = new SemanticStreamObserver(clock, model.ServedId, 0b11);
        validation.Observe(observer);
        var frames = new SseBlockBuffer(observer: observer, filter: validation.Filter);
        var delivered = new StringBuilder();
        var otherChoice = Started.Replace("\"index\":0", "\"index\":1", StringComparison.Ordinal);
        await Receive(Meta + Started[..^3]);
        Assert.Empty(delivered.ToString());
        await Receive(Started[^3..] + otherChoice[..^4]);
        Assert.Equal(Meta + Started, delivered.ToString());
        clock.AdvanceTo(750);
        await Receive(otherChoice[^4..]);
        Assert.Equal(Meta + Started + otherChoice, delivered.ToString());
        clock.AdvanceTo(1500);
        const string malformed = "data: {\"model\":\"actual\",\"choices\":[{\"delta\":{\"content\":\"blocked\"}}]}\n\n";
        var refused = await Assert.ThrowsAsync<RouterRequestException>(() => Receive(malformed).AsTask());
        Assert.Equal("commercial_exchange_failed", refused.Code);
        Assert.Equal(Meta + Started + otherChoice, delivered.ToString());
        clock.AdvanceTo(1749);
        Assert.False(validation.TimedOut);
        clock.AdvanceTo(1750);
        Assert.True(validation.TimedOut);

        ValueTask Receive(string text)
        {
            var (complete, limitExceeded) = frames.Append(Encoding.UTF8.GetBytes(text));
            Assert.False(limitExceeded);
            return validation.RelayAsync(frames, complete, heldBeforeRead: false, bytes =>
            {
                delivered.Append(Encoding.UTF8.GetString(bytes.Span));
                return ValueTask.CompletedTask;
            });
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalCreditLimitAndExpiryNeverReleaseAFalseSuccess(bool overCredit)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(14));
        var ct = deadline.Token;
        var invocations = 0;
        var capture = new Capture(string.Empty, stream: true)
        {
            Write = async context =>
            {
                if (Interlocked.Increment(ref invocations) == 1)
                    await context.Response.WriteAsync(Started + Done + (overCredit ? ": " + new string('z', 300) + "\n\n" : ""),
                        context.RequestAborted);
                else
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(Standard, context.RequestAborted);
                }
            },
        };
        await using var upstream = await capture.StartAsync(ct);
        var declaration = Declaration(new(upstream.Urls.Single())).Replace("allows_streaming = true",
            "allows_streaming = true\nfirst_output_ms = 100\nsemantic_idle_ms = 2000\nmaximum_stream_ms = 500", StringComparison.Ordinal);
        var prefixBytes = Encoding.UTF8.GetByteCount(Started);
        var settings = RouterConfigLoader.Load(FormattableString.Invariant($"stream_buffer_limit_bytes = {prefixBytes + 1}\n") + declaration);
        settings.Scheduler.MaxInflightPerEngine = 1;
        await using var app = await RouterAsync(settings, PermitAll(), ct);
        var router = app.Services.GetRequiredService<RouterCore>();
        var clock = new ReadinessDeadlineTests.ManualClock();
        router.ResponseTimeoutClockForTests = clock;
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.OnDirectTerminalPublishForTests = () => publishing.TrySetResult();
        var first = await router.EnqueueHttpAsync("held-writer", Input(stream: true), ct);
        var writer = new StreamRelayTests.GatedWriter(overCredit ? 3 : 2);
        var context = new DefaultHttpContext { RequestAborted = ct };
        context.Features.Set<IHttpResponseBodyFeature>(new StreamRelayTests.BodyFeature(writer));
        var relay = ServerApp.StreamTicketAsync(context, first, TimeProvider.System, router);
        try
        {
            var blockedLength = await writer.Blocked.Task.WaitAsync(ct);
            await publishing.Task.WaitAsync(ct);
            var delivery = Assert.IsType<HttpStreamDelivery>(first.HttpDelivery);
            Assert.Equal(overCredit ? 0 : prefixBytes, delivery.ReservedBytes);
            var successor = await router.EnqueueAsync("independent-writer", Input(stream: false), ct);
            if (!overCredit)
            {
                Assert.Equal(prefixBytes, blockedLength);
                clock.AdvanceTo(499);
                Assert.False(successor.CompletionTask.IsCompleted);
                Assert.Equal(1, router.Engines["fixture"].Inflight);
                Assert.Single(capture.Requests);
                clock.AdvanceTo(500);
            }
            var expected = overCredit ? "commercial_exchange_failed" : "commercial_stream_deadline";
            var failure = await Assert.ThrowsAsync<RouterRequestException>(() => first.CompletionTask.WaitAsync(ct));
            Assert.Equal(expected, failure.Code);
            Assert.Equal(Standard, Encoding.UTF8.GetString(await successor.CompletionTask.WaitAsync(ct)));
            Assert.Equal(2, capture.Requests.Count);
            Assert.True(first.CommercialMayHaveSent);

            writer.Release.TrySetResult(new FlushResult(false, false));
            await relay.WaitAsync(ct);
            var output = Encoding.UTF8.GetString(writer.Received.SelectMany(static chunk => chunk).ToArray());
            Assert.StartsWith(Started, StripRouterComments(output), StringComparison.Ordinal);
            Assert.Contains(expected, output, StringComparison.Ordinal);
            Assert.DoesNotContain(Done, output, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('z', 30), output, StringComparison.Ordinal);
            Assert.Equal(1, output.Split("[DONE]", StringSplitOptions.None).Length - 1);
        }
        finally { writer.Release.TrySetResult(new FlushResult(true, false)); }

        static InferenceRequest Input(bool stream) => new()
        {
            Model = "public", Stream = stream, Body = Encoding.UTF8.GetBytes(Request("public", stream)), Options = new(),
        };
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TerminalTailCountsCompletedAndPartialBytesAcrossFragmentedReads(bool oversized, bool sameReadAsSentinel)
    {
        const int limit = 1024;
        const string comment = ": complete\n\n";
        var provider = RouterConfigLoader.Load(Declaration(new Uri("http://127.0.0.1:14141")))
            .CommercialProviders["fixture"] with { IdentityBufferBytes = limit };
        var clock = new ReadinessDeadlineTests.ManualClock();
        var model = provider.Models["public"];
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        using var validation = new DirectChatResponse(provider, model, clock, 1, CancellationToken.None);
        validation.Bind(response);
        var observer = new SemanticStreamObserver(clock, model.ServedId, 1);
        validation.Observe(observer);
        var frames = new SseBlockBuffer(observer: observer, filter: validation.Filter);
        var delivered = new StringBuilder();
        var incomplete = ":" + new string('x', limit - Done.Length - comment.Length - (oversized ? 0 : 3));

        await Receive(sameReadAsSentinel ? Started : Started + Done[..^1]);
        if (oversized)
        {
            if (!sameReadAsSentinel) await Receive(Done[^1..] + comment);
            var failure = await Assert.ThrowsAsync<RouterRequestException>(() =>
                Receive((sameReadAsSentinel ? Done + comment : string.Empty) + incomplete).AsTask());
            Assert.Equal("commercial_exchange_failed", failure.Code);
            Assert.Equal(Started, delivered.ToString());
            return;
        }

        if (sameReadAsSentinel) await Receive(Done + comment + incomplete);
        else
        {
            await Receive(Done[^1..] + comment);
            await Receive(incomplete);
        }
        Assert.Equal(Started, delivered.ToString());
        await Receive("\n\n");
        Assert.False(frames.HasIncompleteBlock);
        var terminal = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        await validation.PublishTerminalAsync(terminal.Writer, new HttpStreamDelivery(limit));
        Assert.True(terminal.Reader.TryRead(out var terminalBytes));
        await Publish(terminalBytes);
        Assert.False(terminal.Reader.TryRead(out _));
        clock.AdvanceTo(60_000);
        Assert.False(validation.TimedOut);
        Assert.Equal(limit, delivered.Length - Started.Length);
        Assert.Equal(Started + Done + comment + incomplete + "\n\n", delivered.ToString());

        ValueTask Receive(string text)
        {
            var held = validation.HasTerminal;
            var (complete, limitExceeded) = frames.Append(Encoding.UTF8.GetBytes(text));
            Assert.False(limitExceeded);
            return validation.RelayAsync(frames, complete, held, Publish);
        }

        ValueTask Publish(ReadOnlyMemory<byte> bytes)
        {
            delivered.Append(Encoding.UTF8.GetString(bytes.Span));
            return ValueTask.CompletedTask;
        }
    }
}
