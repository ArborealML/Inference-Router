using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>What happens to a streaming request that does not finish. These paths allocate a stream
/// channel the moment the request is admitted, so they are the ones where the caller can be left
/// waiting on a response that has already lost its only way to say anything.</summary>
public class StreamingFailureTests
{
    private static string Body(string content) =>
        $$"""{"model":"test-model","stream":true,"messages":[{"role":"user","content":"{{content}}"}]}""";

    [Fact(Timeout = 30_000)]
    public async Task EngineFailureOnAStreamingRequestEndsTheStreamItOpened()
    {
        var (app, fake, port) = await WireHarness.StartAsync(holdCompletions: false);
        using var _ = app;
        fake.CompletionStatus = 502;
        fake.CompletionErrorBody = "engine exploded";

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        var (ticket, response) = await WireHarness.StartStreamAsync(http, "stream-fail", Body("hello"));
        using var res = response;
        Assert.NotEmpty(ticket);

        // The response is already a committed 200 event stream by the time the engine answers, so the
        // only way the caller learns anything is the stream ending with an error frame. Until the
        // failure path completed the stream channel, this read simply waited.
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var body = await res.Content.ReadAsByteArrayAsync(bound.Token);
        var terminalStart = body.AsSpan().IndexOf("data: "u8);
        Assert.True(terminalStart > 0, "The stream omitted its keepalive or terminal event.");
        var expected = "data: {\"error\":{\"message\":\"502: engine exploded\",\"code\":\"engine_error\",\"type\":\"air_error\"}}\n\ndata: [DONE]\n\n"u8.ToArray();
        Assert.Equal(expected, body[terminalStart..]);
    }

    private const string DisconnectDelta = """
        data: {"choices":[{"delta":{"content":"hello"},"finish_reason":null}]}
        """;

    [Fact(Timeout = 30_000)]
    public async Task HangingUpMidStreamIsNotCountedAsACompletedRequest()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = bound.Token;
        using var upstream = new DisconnectBody();
        using var fake = new DisconnectEngine(upstream);
        var (app, _, port) = await StartDisconnectHarnessAsync(fake, ct);
        await using var server = app;
        using var handler = new SocketsHttpHandler { MaxResponseDrainSize = 0 };
        using var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var router = app.Services.GetRequiredService<RouterCore>();
        var engine = router.Engines["fake"];
        var enqueued = new TaskCompletionSource<RouterTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwards = 0;
        var releases = 0;
        router.BeforeEnqueueFeedForTests = ticket => enqueued.TrySetResult(ticket);
        engine.AfterForwardAdmissionForTests = () =>
        {
            Interlocked.Increment(ref forwards);
            return Task.CompletedTask;
        };
        engine.AfterForwardReleaseForTests = () =>
        {
            Interlocked.Increment(ref releases);
            released.TrySetResult();
        };

        // Deadlines bound the assertions. Only the response disposal below aborts a passing exchange.
        var sending = WireHarness.StartStreamAsync(http, "stream-abort", Body("hello"));
        Task<Stream>? opening = null;
        Task? reading = null;
        var checkedReplay = false;
        try
        {
            var (id, response) = await sending.WaitAsync(ct);
            using var res = response;
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(HttpVersion.Version11, res.Version);
            opening = res.Content.ReadAsStreamAsync();
            await using var body = await opening.WaitAsync(ct);
            using var reader = new StreamReader(body, leaveOpen: true);
            reading = ReadCompleteDeltaAsync(reader);
            await reading.WaitAsync(ct);

            var (readToken, heldRead) = await upstream.ReadEntered.Task.WaitAsync(ct);
            var ticket = await enqueued.Task.WaitAsync(ct);
            Assert.Equal(id, ticket.Id);

            Assert.False(ct.IsCancellationRequested);
            Assert.False(router.ShutdownToken.IsCancellationRequested);
            Assert.True(reader.BaseStream.CanRead);
            Assert.True(readToken.CanBeCanceled);
            Assert.False(readToken.IsCancellationRequested);
            Assert.False(heldRead.IsCompleted);
            Assert.False(upstream.CleanupRelease.Task.IsCompleted);
            Assert.False(upstream.ReadCancelled.Task.IsCompleted);
            Assert.False(upstream.ReadExited.Task.IsCompleted);
            Assert.False(ticket.IsTerminal);
            Assert.False(ticket.CompletionTask.IsCompleted);
            Assert.False(released.Task.IsCompleted);
            Assert.Equal(1, fake.Posts);
            Assert.Equal(1, Volatile.Read(ref forwards));
            Assert.Equal(0, Volatile.Read(ref releases));
            Assert.Equal(1, engine.InferenceUses);

            res.Dispose();

            var observed = await upstream.ReadCancelled.Task.WaitAsync(ct);
            Assert.Equal(readToken, observed);
            Assert.True(observed.IsCancellationRequested);
            await upstream.ReadExited.Task.WaitAsync(ct);
            await released.Task.WaitAsync(ct);
            var error = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
            Assert.Equal("cancelled", error.Code);
            Assert.Equal(TicketState.Cancelled, ticket.State);
            Assert.Equal(1, fake.Posts);
            Assert.Equal(1, Volatile.Read(ref forwards));
            Assert.Equal(1, Volatile.Read(ref releases));
            Assert.Equal(0, engine.InferenceUses);

            var replay = router.Events.Replay(0).Where(evt => evt.TicketId == id).ToArray();
            var queued = Assert.Single(replay, evt => evt.Kind == "request.enqueued");
            var outcome = Assert.Single(replay, evt =>
                evt.Kind is "queue.rejected" or "request.completed" or "request.failed" or "request.cancelled");
            Assert.Equal("request.cancelled", outcome.Kind);
            Assert.Equal("unknown", outcome.UpstreamOutcome);
            Assert.Equal("unknown", outcome.UpstreamEndReason);
            Assert.NotNull(ticket.StreamObservation?.FirstSemanticOutput);
            Assert.True(queued.Seq < outcome.Seq);
            checkedReplay = true;
        }
        finally
        {
            if (!checkedReplay)
            {
                http.Dispose();
                upstream.CleanupRelease.TrySetResult();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var outstanding = Task.WhenAll(sending, opening ?? Task.CompletedTask, reading ?? Task.CompletedTask);
                try { await outstanding.WaitAsync(cleanup.Token); }
                catch (Exception) when (outstanding.IsCompleted)
                {
                    // Closing a failed exchange can fault the outstanding send or read.
                }
                if (sending.IsCompletedSuccessfully)
                    (await sending).Response.Dispose();
            }
        }
    }

    private static async Task<(WebApplication App, FakeEngineHandler Fake, int Port)> StartDisconnectHarnessAsync(
        DisconnectEngine fake, CancellationToken ct)
    {
        var starting = WireHarness.StartAsync(maxInflight: 1, fake: fake);
        try
        {
            return await starting.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var (app, _, _) = await starting.WaitAsync(cleanup.Token);
            await app.DisposeAsync().AsTask().WaitAsync(cleanup.Token);
            throw;
        }
    }

    private static async Task ReadCompleteDeltaAsync(StreamReader reader)
    {
        var line = await reader.ReadLineAsync();
        while (line is not null && line.StartsWith(':'))
        {
            Assert.Equal("", await reader.ReadLineAsync());
            line = await reader.ReadLineAsync();
        }
        Assert.Equal(DisconnectDelta, line);
        Assert.Equal("", await reader.ReadLineAsync());
    }

    private sealed class DisconnectEngine(DisconnectBody body) : FakeEngineHandler
    {
        private int _posts;
        internal int Posts => Volatile.Read(ref _posts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post) return base.SendAsync(request, ct);
            Assert.EndsWith("/chat/completions", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Interlocked.Increment(ref _posts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        }
    }

    private sealed class DisconnectBody : Stream
    {
        private static readonly byte[] DeltaBytes = Encoding.UTF8.GetBytes(DisconnectDelta + "\n\n");
        private int _offset;

        internal TaskCompletionSource<(CancellationToken Token, Task Pending)> ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<CancellationToken> ReadCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < DeltaBytes.Length)
            {
                var count = Math.Min(buffer.Length, DeltaBytes.Length - _offset);
                DeltaBytes.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            var pending = CleanupRelease.Task.WaitAsync(cancellationToken);
            ReadEntered.TrySetResult((cancellationToken, pending));
            try
            {
                await pending;
                return 0;
            }
            catch (OperationCanceledException error)
            {
                ReadCancelled.TrySetResult(error.CancellationToken);
                throw;
            }
            finally
            {
                ReadExited.TrySetResult();
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
    }
}
