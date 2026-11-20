using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public class StreamFrameIdentityTests
{
    private const int KeepaliveFrameBytes = 1_727;
    private const int ErrorTerminalBytes = 9_878;
    private const string Request = """{"model":"test-model","stream":true,"messages":[{"role":"user","content":"ping"}]}""";
    private const string CompletionChunk = """
        {"id":"chatcmpl-wire","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}
        """;
    private const string CompletePrefix = "data: {\"delta\":\"é🌲\"}\r\n\r\n";
    private static readonly byte[] CompletionFixture = Encoding.UTF8.GetBytes(
        $"data: {CompletionChunk}\r\n\r\ndata: [DONE]\r\n\r\n");
    private static readonly byte[] ShortFixture = Encoding.UTF8.GetBytes(
        CompletePrefix + "data:\n\n: upstream comment\n\nid: 02\nx-extra: ignored\ndata: second\ndata: line two\n\ndata: [DONE]\r\n\r\n");

    [Fact]
    public Task Every_Byte_Split_Preserves_The_Short_Stream()
        => CheckSplitsAsync(ShortFixture, Enumerable.Range(0, ShortFixture.Length + 1), ShortFixture.Length);

    [Theory(Timeout = 30_000)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    public async Task CoreShutdownWritesOneCancellationTerminalAfterACompleteDelta(
        bool priorDone, bool matchingCancellation, bool adminFirst)
    {
        var prefix = Encoding.UTF8.GetBytes($"data: {CompletionChunk}\r\n\r\n" +
            (priorDone ? "data: [DONE]\r\n\r\n" : ""));
        var upstreamBytes = prefix.Concat("data: late delta\r\n\r\n"u8.ToArray()).ToArray();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = bound.Token;
        var rawToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var upstream = new FragmentedBody(upstreamBytes, prefix.Length, upstreamBytes.Length)
        {
            HeldRead = async token =>
            {
                rawToken.TrySetResult(token);
                await releaseRead.Task.WaitAsync(ct);
                if (matchingCancellation) token.ThrowIfCancellationRequested();
            },
        };

        using var fake = new FragmentedEngine();
        fake.Responses.Enqueue(upstream);
        var (app, _, port) = await WireHarness.StartAsync(fake: fake, streamClock: new ReadinessDeadlineTests.ManualClock());
        await using var server = app;
        using var http = Client(port);
        var router = app.Services.GetRequiredService<RouterCore>();
        var enqueued = new TaskCompletionSource<RouterTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeforeEnqueueFeedForTests = ticket => enqueued.TrySetResult(ticket);
        var armed = new TaskCompletionSource<(string Ticket, long Arm)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coreRead = new TaskCompletionSource<(string Ticket, long Arm)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherRead = new TaskCompletionSource<(string Ticket, long Arm)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var qualified = new TaskCompletionSource<(string Ticket, long Arm)>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.DefaultStreamReadObservedForTests = (ticket, arm, observation) =>
        {
            var receipt = (ticket, arm);
            switch (observation)
            {
                case RouterCore.DefaultStreamReadObservation.Armed: armed.TrySetResult(receipt); break;
                case RouterCore.DefaultStreamReadObservation.CoreCancellation: coreRead.TrySetResult(receipt); break;
                case RouterCore.DefaultStreamReadObservation.OtherCancellation: otherRead.TrySetResult(receipt); break;
                case RouterCore.DefaultStreamReadObservation.QualifiedResult: qualified.TrySetResult(receipt); break;
            }
        };

        var coreStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stoppedRegistration = router.ShutdownToken.Register(() => coreStopped.TrySetResult());
        Task? stopping = null;
        try
        {
            var (id, response) = await WireHarness.StartStreamAsync(http, "shutdown-active", Request, ct);
            using var res = response;
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            await using var clientBody = await res.Content.ReadAsStreamAsync(ct);
            var keepalive = await ReadFirstKeepaliveAsync(clientBody, ct);
            Assert.True(keepalive.AsSpan().StartsWith(": [router keepalive seq=0"u8));
            Assert.True(keepalive.AsSpan().EndsWith("\n\n"u8));
            await ReadExpectedAsync(clientBody, prefix, ct);

            var read = await armed.Task.WaitAsync(ct);
            Assert.Equal(id, read.Ticket);
            var token = await rawToken.Task.WaitAsync(ct);
            if (adminFirst)
            {
                using var admin = await http.DeleteAsync($"/admin/tickets/{id}", ct);
                Assert.Equal(HttpStatusCode.NoContent, admin.StatusCode);
                Assert.Equal(read, await otherRead.Task.WaitAsync(ct));
            }

            stopping = Task.Run(async () => await router.DisposeAsync(), CancellationToken.None);
            await coreStopped.Task.WaitAsync(ct);
            if (!adminFirst)
                Assert.Equal(read, await coreRead.Task.WaitAsync(ct));
            Assert.True(token.IsCancellationRequested);
            releaseRead.TrySetResult();

            using var suffix = new MemoryStream();
            await clientBody.CopyToAsync(suffix, ct);
            var ticket = await enqueued.Task.WaitAsync(ct);
            Assert.Equal(id, ticket.Id);
            Assert.Equal(TicketState.Cancelled, ticket.State);
            var cancelled = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
            Assert.Equal("cancelled", cancelled.Code);
            var outcome = Assert.Single(router.Events.Replay(0), evt => evt.TicketId == id &&
                evt.Kind is "request.cancelled" or "request.failed" or "request.completed");
            Assert.Equal("request.cancelled", outcome.Kind);
            Assert.Equal(!adminFirst, ticket.DefaultStreamReadShutdown);
            if (adminFirst) Assert.False(qualified.Task.IsCompleted);
            else Assert.Equal(read, await qualified.Task.WaitAsync(ct));

            byte[] expected = priorDone || adminFirst ? [] :
                "data: {\"error\":{\"message\":\"cancelled\",\"code\":\"cancelled\",\"type\":\"air_error\"}}\n\ndata: [DONE]\n\n"u8.ToArray();
            Assert.Equal(expected, suffix.ToArray());
        }
        finally
        {
            releaseRead.TrySetResult();
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("data: {\"delta\":\"cut", false, "engine_error")]
    [InlineData("data: {\"delta\":\"cut\"}\r\n", true, "engine_unreachable")]
    public async Task Incomplete_Upstream_Block_Ends_With_A_Standalone_Error(string unfinished, bool fault, string code)
    {
        var prefix = Encoding.UTF8.GetBytes(CompletePrefix);
        var bytes = Encoding.UTF8.GetBytes(CompletePrefix + unfinished);
        var releaseEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var upstream = new FragmentedBody(bytes, bytes.Length, 1, releaseEnd.Task, endFailure: fault ? Disconnect() : null);
        await AssertUpstreamOutcomeAsync(upstream, prefix, code, releaseEnd);
    }

    [Fact]
    public async Task Committed_Stream_Error_Uses_Reported_Reason_And_Encoded_Budget()
    {
        var prefix = Encoding.UTF8.GetBytes(CompletePrefix);
        var reason = "scripted upstream disconnect: " + new string('\u0001', 2_048);
        var releaseFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var upstream = new FragmentedBody(prefix, prefix.Length, prefix.Length, releaseFault.Task,
            endFailure: new IOException(reason));

        try
        {
            // The reader reaches its gated fault after one complete block, and the HTTP status is committed.
            await AssertUpstreamOutcomeAsync(upstream, prefix, "engine_unreachable", releaseFault,
                expectedReason: WireText.Bound(reason, WireText.ReasonChars));
        }
        finally
        {
            releaseFault.TrySetResult();
        }
    }

    [Theory]
    [InlineData("data: {\"delta\":\"cut\"}\n", false, "engine_error")]
    [InlineData("data: [DONE]\n", false, "engine_error")]
    [InlineData("data: {\"delta\":\"cut", true, "engine_unreachable")]
    public async Task Bytewise_Incomplete_Blocks_Are_Discarded(string unfinished, bool fault, string code)
    {
        var bytes = Encoding.UTF8.GetBytes(CompletePrefix + unfinished);
        using var upstream = new FragmentedBody(bytes, 0, 1, endFailure: fault ? Disconnect() : null);
        await AssertUpstreamOutcomeAsync(upstream, Encoding.UTF8.GetBytes(CompletePrefix), code);
    }

    [Theory]
    [InlineData("data: complete\r\n\r", "\ndata: cut", "data: complete\r\n\r\n")]
    [InlineData("data: complete\r\r", "data: cut", "data: complete\r\r")]
    [InlineData("data: complete\r\r", "", "data: complete\r\r")]
    [InlineData(CompletePrefix + "data: cut\r", "\ndata: next", CompletePrefix)]
    [InlineData(CompletePrefix + "data: cut\r", "data: next", CompletePrefix)]
    public async Task Read_Fault_Keeps_Only_The_Complete_Prefix_Across_CR(
        string first, string second, string completed)
    {
        var bytes = Encoding.UTF8.GetBytes(first + second);
        using var upstream = new FragmentedBody(bytes, Encoding.UTF8.GetByteCount(first), bytes.Length, endFailure: Disconnect());
        await AssertUpstreamOutcomeAsync(upstream, Encoding.UTF8.GetBytes(completed), "engine_unreachable");
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("data: {\"delta\":\"cut\"}\r\n", "engine_error")]
    public async Task Upstream_Only_Read_Cancellation_Checks_For_An_Incomplete_Block(string unfinished, string? code)
    {
        var bytes = Encoding.UTF8.GetBytes(CompletePrefix + unfinished);
        var upstreamCancellation = new OperationCanceledException("scripted upstream cancellation", new CancellationToken(canceled: true));
        using var upstream = new FragmentedBody(bytes, 0, 1, endFailure: upstreamCancellation);
        await AssertUpstreamOutcomeAsync(upstream, Encoding.UTF8.GetBytes(CompletePrefix), code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(CompletePrefix)]
    public async Task Clean_EOF_At_A_Block_Boundary_Adds_No_Bytes(string complete)
    {
        var bytes = Encoding.UTF8.GetBytes(complete);
        using var upstream = new FragmentedBody(bytes, 0, 1);
        await AssertUpstreamOutcomeAsync(upstream, bytes, errorCode: null);
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public async Task Block_Limit_Includes_Split_Nonblank_CRLF_But_Excludes_The_Blank_Line(string delimiter, bool overflow)
    {
        const int maxBlockBytes = 1024 * 1024;
        const string data = "data: {\"delta\":\"";
        const string lineEnd = "\"}\r\n";
        var payloadBytes = maxBlockBytes - data.Length - lineEnd.Length + (overflow ? 1 : 0);
        var line = data + new string('x', payloadBytes) + lineEnd;
        var bytes = Encoding.UTF8.GetBytes(CompletePrefix + line + delimiter);
        var split = Encoding.UTF8.GetByteCount(CompletePrefix) + line.Length - 1;
        using var upstream = new FragmentedBody(bytes, split, 16 * 1024);
        if (overflow)
            await AssertUpstreamOutcomeAsync(upstream, Encoding.UTF8.GetBytes(CompletePrefix), "engine_error");
        else
            await AssertUpstreamOutcomeAsync(upstream, bytes, errorCode: null);
    }

    [Fact]
    public void Overflow_Preserves_An_Earlier_Block_Completed_In_The_Same_Read()
    {
        var blocks = new SseBlockBuffer(maxBlockBytes: 7);
        var (first, firstExceeded) = blocks.Append("data"u8);
        Assert.True(first.IsEmpty);
        Assert.False(firstExceeded);

        // The second read completes one block, then includes an over-budget but delimited block.
        var (complete, limitExceeded) = blocks.Append(":\n\ndata:xx\n\n"u8);
        Assert.True(limitExceeded);
        Assert.Equal("data:\n\n"u8.ToArray(), complete.ToArray());
    }

    [Fact]
    public async Task Strict_OpenAI_Sse_Parser_Only_Receives_Upstream_Completion_Chunks()
    {
        using var fake = new FragmentedEngine();
        var (app, _, port) = await WireHarness.StartAsync(fake: fake);
        await using var server = app;
        using var http = Client(port);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var upstream = new FragmentedBody(CompletionFixture, 0, CompletionFixture.Length);
        fake.Responses.Enqueue(upstream);
        var (_, response) = await WireHarness.StartStreamAsync(http, "strict-parser", Request);
        using var res = response;
        await using var body = await res.Content.ReadAsStreamAsync(bound.Token);

        Assert.Equal(CompletionChunk, Assert.Single(await ReadCompletionChunksAsync(body, bound.Token)));
    }

    [Fact]
    public async Task Strict_OpenAI_Sse_Parser_Rejects_The_Legacy_Keepalive_Data_Payload()
    {
        var legacy = "data: [router keepalive seq=0 status=\"queued model=test-model\"]\n\n";
        using var body = new MemoryStream([.. Encoding.UTF8.GetBytes(legacy), .. CompletionFixture]);
        await Assert.ThrowsAnyAsync<JsonException>(() => ReadCompletionChunksAsync(body, CancellationToken.None));
    }

    [Fact]
    public async Task Keepalive_Status_Cannot_Introduce_Sse_Data_Lines()
    {
        // An unhealthy upstream leaves this model queued, so the first comment carries its name.
        using var fake = new FakeEngineHandler { ModelsStatus = 503 };
        var (app, _, port) = await WireHarness.StartAsync(fake: fake);
        await using var server = app;
        using var http = Client(port);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, response) = await WireHarness.StartStreamAsync(http, "escaped-status",
            """{"model":"m\u0022]\r\ndata: [DONE]\n\n\\suffix","stream":true}""");
        using var res = response;
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(bound.Token));
        const string prefix = ": [router keepalive seq=0 status=";
        var comment = await reader.ReadLineAsync(bound.Token);
        Assert.NotNull(comment);
        Assert.StartsWith(prefix, comment, StringComparison.Ordinal);
        Assert.EndsWith("]", comment, StringComparison.Ordinal);
        using var status = JsonDocument.Parse(comment[prefix.Length..^1]);
        Assert.Equal("queued model=m\"]\r\ndata: [DONE]\n\n\\suffix", status.RootElement.GetString());
        Assert.Equal("", await reader.ReadLineAsync(bound.Token));
    }

    [Fact]
    public async Task Queued_Keepalive_Caps_Escaped_Model_Bytes()
    {
        var model = "queued-" + new string('\u0001', 2_048) + "-model-tail";
        var request = "{\"model\":\"" + JsonEncodedText.Encode(model).ToString() + "\",\"stream\":true}";
        using var fake = new FakeEngineHandler { ModelsStatus = 503 };
        var (app, _, port) = await WireHarness.StartAsync(fake: fake);
        await using var server = app;
        using var http = Client(port);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, response) = await WireHarness.StartStreamAsync(http, "long-queued-status", request, bound.Token);
        using var res = response;
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await using var body = await res.Content.ReadAsStreamAsync(bound.Token);

        var frame = await ReadFirstKeepaliveAsync(body, bound.Token);
        AssertKeepaliveStatus(frame, $"queued model={ReportedIdentifier(model)}");
    }

    [Theory]
    [InlineData(TicketState.WaitingForModel, true)]
    [InlineData(TicketState.WaitingForModel, false)]
    [InlineData(TicketState.Executing, true)]
    [InlineData(TicketState.Executing, false)]
    public async Task Switching_And_Executing_Keepalives_Cap_Reported_Identifiers(TicketState state, bool hasEngine)
    {
        var model = "switch-" + new string('\u0001', 2_048) + "-model-tail";
        var engine = hasEngine ? "engine-" + new string('\u0002', 2_048) + "-engine-tail" : null;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var wire = new MemoryStream();
        var context = new DefaultHttpContext { RequestAborted = bound.Token };
        context.Response.Body = wire;

        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        Assert.True(channel.Writer.TryComplete());
        var ticket = new RouterTicket("ticket", "thread", model, DateTimeOffset.UnixEpoch)
        {
            State = state,
            Engine = engine,
            StreamChannel = channel,
        };
        await ServerApp.StreamTicketAsync(context, ticket, TimeProvider.System);

        Assert.Equal(200, context.Response.StatusCode);
        var reportedEngine = ReportedIdentifier(engine ?? "");
        var expected = state == TicketState.WaitingForModel
            ? $"switching model={ReportedIdentifier(model)} engine={reportedEngine}"
            : $"engine={reportedEngine}";
        AssertKeepaliveStatus(wire.ToArray(), expected);
    }

    [Fact]
    public Task Every_64_Byte_Alignment_Preserves_The_Long_Stream()
    {
        var payload = new string('x', 45);
        var frames = Enumerable.Range(0, 320).Select(i =>
            FormattableString.Invariant($"id: {i}\r\ndata: {{\"delta\":\"{i} é🌲 {payload}\"}}\r\n\r\n"));
        var fixture = Encoding.UTF8.GetBytes(string.Concat(frames) + "data: [DONE]\r\n\r\n");
        return CheckSplitsAsync(fixture, Enumerable.Range(0, 64), 64);
    }

    [Fact]
    public async Task Queued_Keepalives_Precede_An_Upstream_Frame_Delivered_In_Two_Reads()
    {
        using var fake = new FragmentedEngine();
        var (app, _, port) = await WireHarness.StartAsync(maxInflight: 1, fake: fake);
        await using var server = app;
        using var http = Client(port);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = bound.Token;

        var releaseSlot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var occupyingBody = new FragmentedBody(ShortFixture, 0, ShortFixture.Length, releaseSlot.Task);
        fake.Responses.Enqueue(occupyingBody);
        var (_, occupyingResponse) = await WireHarness.StartStreamAsync(http, "occupying", Request);
        using var occupying = occupyingResponse;
        // The fake has been read, so this request owns the only slot before the next request starts.
        await occupyingBody.AtGate.Task.WaitAsync(ct);

        var prefix = Encoding.UTF8.GetBytes(CompletePrefix);
        var split = prefix.Length + "dat"u8.Length;
        var releaseSuffix = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pendingBody = new FragmentedBody(ShortFixture, split, ShortFixture.Length, releaseSuffix.Task);
        fake.Responses.Enqueue(pendingBody);
        var (_, pendingResponse) = await WireHarness.StartStreamAsync(http, "queued", Request);
        using var pending = pendingResponse;
        await using var clientBody = await pending.Content.ReadAsStreamAsync(ct);

        // Sequence 2 requires an actual periodic tick. Keep the slot occupied until the client
        // sees it, so admission cannot win a race against the timer.
        var queuedFrames = (": [router keepalive seq=0 status=\"queued model=test-model\"]\n\n" +
                            ": [router keepalive seq=1]\n\n" +
                            ": [router keepalive seq=2]\n\n");
        await ReadExpectedAsync(clientBody, Encoding.UTF8.GetBytes(queuedFrames), ct);

        releaseSlot.SetResult();
        var start = new byte["data: {"u8.Length];
        var nextSequence = 3;
        while (true)
        {
            await clientBody.ReadExactlyAsync(start, ct);
            if (prefix.AsSpan().StartsWith(start)) break;

            // Admission can race another tick after the slot is released. Accept only complete,
            // consecutive keepalives before the first upstream byte, including a status change.
            var keepalive = Encoding.UTF8.GetBytes(FormattableString.Invariant($": [router keepalive seq={nextSequence++}"));
            Assert.Equal(keepalive[..start.Length], start);
            await ReadExpectedAsync(clientBody, keepalive.AsMemory(start.Length), ct);
            var end = await ReadKeepaliveEndAsync(clientBody, ct);
            Assert.True(end is "]" or " status=\"engine=\"]" or " status=\"engine=fake\"]",
                $"The relay emitted an unexpected keepalive ending: {end}");
        }

        // The first complete block arrives while the next block is still missing its suffix.
        await ReadExpectedAsync(clientBody, prefix.AsMemory(start.Length), ct);
        await pendingBody.AtGate.Task.WaitAsync(ct);
        releaseSuffix.SetResult();
        using var tail = new MemoryStream();
        await clientBody.CopyToAsync(tail, ct);
        Assert.True(ShortFixture.AsSpan(prefix.Length).SequenceEqual(tail.ToArray()),
            $"The relay tore, changed, duplicated, or inserted a keepalive into the upstream frame split at byte {split}.");
    }

    private static async Task AssertUpstreamOutcomeAsync(
        FragmentedBody upstream, byte[] prefix, string? errorCode, TaskCompletionSource? releaseEnd = null,
        string? expectedReason = null)
    {
        using var fake = new FragmentedEngine();
        var (app, _, port) = await WireHarness.StartAsync(fake: fake);
        await using var server = app;
        using var http = Client(port);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        fake.Responses.Enqueue(upstream);
        var (ticketId, response) = await WireHarness.StartStreamAsync(http, "frame-ending", Request, ct);
        using var res = response;
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        if (releaseEnd is not null)
        {
            try { await upstream.AtGate.Task.WaitAsync(ct); }
            finally { releaseEnd.TrySetResult(); }
        }

        // Reading through EOF proves the writer sent a complete HTTP response and closed its body.
        var wire = await res.Content.ReadAsByteArrayAsync(ct);
        var output = wire.AsMemory(LeadingKeepaliveLength(wire));
        Assert.True(output.Span.StartsWith(prefix), "The relay changed or omitted a complete upstream block.");
        var terminalBytes = output[prefix.Length..];
        var terminal = Encoding.UTF8.GetString(terminalBytes.Span);
        var ticket = app.Services.GetRequiredService<RouterCore>().TryGetTicket(ticketId);
        Assert.NotNull(ticket);

        if (errorCode is null)
        {
            Assert.Empty(terminal);
            Assert.Equal("Completed", ticket.State);
            return;
        }

        const string data = "data: ";
        const string ending = "\n\ndata: [DONE]\n\n";
        Assert.InRange(terminalBytes.Length, 1, ErrorTerminalBytes);
        Assert.StartsWith(data + "{\"error\":", terminal, StringComparison.Ordinal);
        Assert.EndsWith(ending, terminal, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(terminal[data.Length..^ending.Length]);
        Assert.Single(json.RootElement.EnumerateObject());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(3, error.EnumerateObject().Count());
        Assert.Equal("air_error", error.GetProperty("type").GetString());
        Assert.Equal(errorCode, error.GetProperty("code").GetString());
        var reason = error.GetProperty("message").GetString();
        Assert.NotNull(reason);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.InRange(reason.Length, 1, WireText.ReasonChars);
        if (expectedReason is not null) Assert.Equal(expectedReason, reason);
        Assert.Equal("Failed", ticket.State);
        Assert.Equal(errorCode, ticket.ErrorCode);
    }

    private static async Task CheckSplitsAsync(byte[] fixture, IEnumerable<int> splits, int stride)
    {
        using var fake = new FragmentedEngine();
        var (app, _, port) = await WireHarness.StartAsync(fake: fake);
        await using var server = app;
        using var http = Client(port);
        foreach (var split in splits)
            await CheckSplitAsync(http, fake, fixture, split, stride);
    }

    private static async Task CheckSplitAsync(HttpClient http, FragmentedEngine fake, byte[] fixture, int split, int stride)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var upstream = new FragmentedBody(fixture, split, stride);
        fake.Responses.Enqueue(upstream);
        var (_, response) = await WireHarness.StartStreamAsync(http, "identity", Request);
        using var res = response;
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        var received = await res.Content.ReadAsByteArrayAsync(bound.Token);
        var upstreamStart = LeadingKeepaliveLength(received);
        Assert.True(fixture.AsSpan().SequenceEqual(received.AsSpan(upstreamStart)),
            $"SSE bytes changed at split {split} with stride {stride}. Expected {fixture.Length} upstream bytes, received {received.Length - upstreamStart}.");
    }

    private static int LeadingKeepaliveLength(ReadOnlySpan<byte> bytes)
    {
        var start = 0;
        // Strip only the contiguous router prefix. Any extra bytes after the first upstream
        // byte, including an injected keepalive at an SSE boundary, fail full byte equality.
        while (bytes[start..].StartsWith(": [router keepalive seq="u8))
        {
            var end = bytes[start..].IndexOf("\n\n"u8);
            Assert.True(end >= 0, "The relay emitted an unterminated keepalive.");
            start += end + 2;
        }
        Assert.True(start > 0, "The relay omitted the initial keepalive.");
        return start;
    }

    private static async Task<List<string>> ReadCompletionChunksAsync(Stream body, CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, leaveOpen: true);
        var chunks = new List<string>();
        var data = new List<string>();
        var done = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line is "data" || line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.Length > 4 ? line[5..] : "";
                data.Add(value.StartsWith(' ') ? value[1..] : value);
            }
            if (line.Length != 0 || data.Count == 0) continue;

            Assert.False(done, "Data event followed the upstream [DONE].");
            var payload = string.Join('\n', data);
            data.Clear();
            done = payload == "[DONE]";
            if (done) continue;

            using var chunk = JsonDocument.Parse(payload);
            Assert.Equal("chat.completion.chunk", chunk.RootElement.GetProperty("object").GetString());
            Assert.Equal(JsonValueKind.Array, chunk.RootElement.GetProperty("choices").ValueKind);
            chunks.Add(payload);
        }
        Assert.Empty(data);
        Assert.True(done, "The completion stream omitted [DONE].");
        return chunks;
    }

    private static HttpClient Client(int port) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static async Task ReadExpectedAsync(Stream stream, ReadOnlyMemory<byte> expected, CancellationToken ct)
    {
        var received = new byte[expected.Length];
        await stream.ReadExactlyAsync(received, ct);
        Assert.Equal(expected.ToArray(), received);
    }

    private static async Task<byte[]> ReadFirstKeepaliveAsync(Stream stream, CancellationToken ct)
    {
        // An over-budget frame trips the byte assertion before waiting for its delimiter.
        var frame = new byte[KeepaliveFrameBytes + 1];
        for (var length = 1; length <= frame.Length; length++)
        {
            await stream.ReadExactlyAsync(frame.AsMemory(length - 1, 1), ct);
            if (frame.AsSpan(0, length).EndsWith("\n\n"u8))
                return frame[..length];
        }
        return frame;
    }

    private static string ReportedIdentifier(string value)
    {
        var reported = WireText.Ident(value);
        Assert.InRange(reported.Length, 0, 128);
        return reported;
    }

    private static void AssertKeepaliveStatus(ReadOnlySpan<byte> frame, string expected)
    {
        Assert.InRange(frame.Length, 1, KeepaliveFrameBytes);
        const string prefix = ": [router keepalive seq=0 status=";
        const string ending = "]\n\n";
        var text = Encoding.UTF8.GetString(frame);
        Assert.StartsWith(prefix, text, StringComparison.Ordinal);
        Assert.EndsWith(ending, text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text[..^2], StringComparison.Ordinal);
        using var status = JsonDocument.Parse(text[prefix.Length..^ending.Length]);
        Assert.Equal(expected, status.RootElement.GetString());
    }

    private static async Task<string> ReadKeepaliveEndAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[32];
        for (var length = 1; length < buffer.Length; length++)
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(length - 1, 1), ct);
            if (length >= 2 && buffer.AsSpan(0, length).EndsWith("\n\n"u8))
                return Encoding.UTF8.GetString(buffer, 0, length - 2);
        }
        throw new InvalidDataException("The relay did not finish its keepalive before upstream data.");
    }

    private sealed class FragmentedEngine : FakeEngineHandler
    {
        public ConcurrentQueue<Stream> Responses { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post) return base.SendAsync(request, cancellationToken);

            Assert.True(Responses.TryDequeue(out var body), "No scripted upstream response for this request.");
            var content = new StreamContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static IOException Disconnect() => new("scripted upstream disconnect");

    // The HTTP handler supplies this stream directly to the upstream pump, so TCP coalescing
    // cannot erase the scripted boundaries. A zero-length partition is skipped: a zero read is EOF.
    private sealed class FragmentedBody(byte[] bytes, int split, int stride, Task? gate = null, Exception? endFailure = null) : Stream
    {
        private int _offset;
        private int _nextBoundary = split == 0 ? stride : split;
        public TaskCompletionSource AtGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task>? HeldRead { get; init; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (HeldRead is { } hold && _offset == split)
            {
                AtGate.TrySetResult();
                await hold(cancellationToken).ConfigureAwait(false);
            }
            if (gate is not null && _offset == split)
            {
                AtGate.TrySetResult();
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (endFailure is not null && _offset == bytes.Length)
                throw endFailure;

            var end = Math.Min(_nextBoundary, bytes.Length);
            var count = Math.Min(end - _offset, buffer.Length);
            bytes.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == _nextBoundary) _nextBoundary += stride;
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
