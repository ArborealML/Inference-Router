using System.Net;
using System.Text;
using System.Threading.Channels;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Http;

namespace Arboreal.InferenceRouter.Tests;

public class StreamHeartbeatTests
{
    private const string Request = """{"model":"test-model","stream":true,"messages":[{"role":"user","content":"ping"}]}""";
    private const string EarlyFailure = "failure before first byte";
    private static readonly double[] ExpectedReceiveSeconds = [0d, 4d, 8d, 12d, 16d];

    [Fact]
    public async Task OneCadenceSpansAdmissionAndTheFirstByteWithoutSplittingAnUpstreamFrame()
    {
        var payload = "data: {\"delta\":\"🌲\"}\r\n\r\ndata: [DONE]\r\n\r\n"u8.ToArray();
        var split = payload.AsSpan().IndexOf("🌲"u8) + 1;
        using var upstream = new HeldBody(payload.AsMemory(0, split), payload.AsMemory(split));
        using var fake = new GatedEngine(upstream);
        var clock = new HeartbeatClock();
        var (app, _, port) = await WireHarness.StartAsync(fake: fake, streamClock: clock);
        await using var server = app;
        using var http = Client(port);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = bound.Token;
        var (_, response) = await WireHarness.StartStreamAsync(http, "cadence", Request, ct);
        using var res = response;
        await using var body = await res.Content.ReadAsStreamAsync(ct);

        try
        {
            var received = new List<TimeSpan> { await ReadCommentAsync(body, 0, clock, ct) };
            for (var seq = 1; seq <= 2; seq++)
            {
                var tick = await clock.NextTimerAsync(ct);
                Assert.Equal(TimeSpan.FromSeconds(seq * 4), tick.Due);
                clock.Reach(tick.Due, tick);
                received.Add(await ReadCommentAsync(body, seq, clock, ct));
            }
            Assert.False(upstream.FirstRead.Task.IsCompleted);

            var carriedTick = await clock.NextTimerAsync(ct);
            Assert.Equal(TimeSpan.FromSeconds(12), carriedTick.Due);
            clock.Reach(TimeSpan.FromSeconds(10));
            fake.ModelsReady.TrySetResult();
            await upstream.FirstRead.Task.WaitAsync(ct); // admission installed the channel
            Assert.False(carriedTick.Disposed.Task.IsCompleted);

            clock.Reach(carriedTick.Due, carriedTick);
            received.Add(await ReadCommentAsync(body, 3, clock, ct));
            var nextTick = await clock.NextTimerAsync(ct);
            Assert.Equal(TimeSpan.FromSeconds(16), nextTick.Due);
            clock.Reach(nextTick.Due, nextTick);
            received.Add(await ReadCommentAsync(body, 4, clock, ct));
            Assert.Equal(ExpectedReceiveSeconds, received.Select(t => t.TotalSeconds));

            var pending = await clock.NextTimerAsync(ct);
            clock.Reach(TimeSpan.FromSeconds(18));
            upstream.AllowFirst.TrySetResult();
            var prefix = new byte[split];
            await body.ReadExactlyAsync(prefix, ct);
            Assert.Equal(payload[..split], prefix);
            await upstream.SecondRead.Task.WaitAsync(ct);
            await pending.Disposed.Task.WaitAsync(ct);

            // Split inside a UTF-8 codepoint and hold the second part beyond two deadlines.
            clock.Reach(TimeSpan.FromSeconds(30), pending);
            upstream.AllowSecond.TrySetResult();
            using var tail = new MemoryStream();
            await body.CopyToAsync(tail, ct);
            Assert.Equal(payload[split..], tail.ToArray());
            Assert.False(clock.HasUnreadTimer);
        }
        finally
        {
            fake.ModelsReady.TrySetResult();
            upstream.Release();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyUpstreamKeepsTheExistingSuccessOrFailureEnding(bool failed)
    {
        using var upstream = new HeldBody(fail: failed);
        using var fake = new GatedEngine(upstream);
        fake.ModelsReady.TrySetResult();
        var clock = new HeartbeatClock();
        var (app, _, port) = await WireHarness.StartAsync(fake: fake, streamClock: clock);
        await using var server = app;
        using var http = Client(port);
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = bound.Token;
        var (_, response) = await WireHarness.StartStreamAsync(http, "empty", Request, ct);
        using var res = response;
        await using var body = await res.Content.ReadAsStreamAsync(ct);

        try
        {
            await ReadCommentAsync(body, 0, clock, ct);
            await upstream.FirstRead.Task.WaitAsync(ct);
            var tick = await clock.NextTimerAsync(ct);
            clock.Reach(tick.Due, tick);
            await ReadCommentAsync(body, 1, clock, ct);
            var pending = await clock.NextTimerAsync(ct);

            upstream.AllowFirst.TrySetResult();
            using var tail = new MemoryStream();
            await body.CopyToAsync(tail, ct);
            Assert.Equal(failed ? ErrorFrame("engine_unreachable", EarlyFailure) : "", Encoding.UTF8.GetString(tail.ToArray()));
            await AssertTimerStoppedAsync(clock, pending, ct);
        }
        finally
        {
            upstream.Release();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutOrClientAbortReleasesTheFirstReadAndStopsItsHeartbeat(bool abort)
    {
        using var upstream = new HeldBody();
        using var fake = new GatedEngine(upstream);
        fake.ModelsReady.TrySetResult();
        var clock = new HeartbeatClock();
        var (app, _, port) = await WireHarness.StartAsync(fake: fake, streamClock: clock, maxInflight: 1,
            responseTimeoutMs: abort ? 600_000 : 4_000);
        await using var server = app;
        using var handler = new SocketsHttpHandler { MaxResponseDrainSize = 0 };
        using var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = bound.Token;
        var (_, response) = await WireHarness.StartStreamAsync(http, "stopped", Request, ct);
        using var res = response;
        await using var body = await res.Content.ReadAsStreamAsync(ct);

        try
        {
            await ReadCommentAsync(body, 0, clock, ct);
            await upstream.FirstRead.Task.WaitAsync(ct);
            var tick = await clock.NextTimerAsync(ct);
            clock.Reach(tick.Due, tick);
            await ReadCommentAsync(body, 1, clock, ct);
            var pending = await clock.NextTimerAsync(ct);
            if (abort)
            {
                res.Dispose();
            }
            else
            {
                using var tail = new MemoryStream();
                await body.CopyToAsync(tail, ct);
                Assert.Equal(ErrorFrame("engine_timeout", "engine fake sent no response to test-model within 4000 ms"),
                    Encoding.UTF8.GetString(tail.ToArray()));
            }

            await upstream.FirstReadCancelled.Task.WaitAsync(ct);
            await AssertTimerStoppedAsync(clock, pending, ct);

            using var followUpBody = new StringContent("""{"model":"test-model","messages":[{"role":"user","content":"next"}]}""",
                Encoding.UTF8, "application/json");
            using var followUp = await http.PostAsync("/v1/chat/completions", followUpBody, ct);
            Assert.Equal(HttpStatusCode.OK, followUp.StatusCode); // the original request released the only slot
        }
        finally
        {
            upstream.Release();
        }
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("data")]
    [InlineData("terminal")]
    [InlineData("terminal before admission")]
    [InlineData("buffered failure")]
    [InlineData("client abort")]
    public async Task ReadyDataOrTerminalTakesPrecedenceAtTheHeartbeatDeadline(string transition)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ctx = new DefaultHttpContext { RequestAborted = bound.Token };
        using var bytes = new MemoryStream();
        ctx.Response.Body = bytes;
        var ticket = new RouterTicket("ticket", "thread", "test-model", DateTimeOffset.UnixEpoch);
        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = true,
        });
        if (transition is not ("admission" or "terminal before admission")) ticket.StreamChannel = channel;
        var supplied = transition switch
        {
            "admission" or "data" => "data: [DONE]\n\n",
            "buffered failure" => "data: partial\n\n",
            _ => "",
        };
        var failed = transition is "terminal" or "terminal before admission" or "buffered failure" or "client abort";
        var clock = new HeartbeatClock
        {
            AtFirstDeadline = () =>
            {
                // Both signals become available before the relay can observe the completed timer.
                if (transition == "client abort") bound.Cancel();
                if (transition == "admission") ticket.StreamChannel = channel;
                if (supplied.Length > 0) Assert.True(channel.Writer.TryWrite(Encoding.UTF8.GetBytes(supplied)));
                if (failed)
                {
                    ticket.ErrorCode = "engine_unreachable";
                    ticket.Error = EarlyFailure;
                }
                ticket.State = failed ? TicketState.Failed : TicketState.Completed;
                Assert.True(channel.Writer.TryComplete());
            },
        };

        await ServerApp.StreamTicketAsync(ctx, ticket, clock);
        var output = Encoding.UTF8.GetString(bytes.ToArray());
        var expected = ": [router keepalive seq=0 status=\"queued model=test-model\"]\n\n" + supplied;
        if (failed && transition != "client abort") expected += ErrorFrame("engine_unreachable", EarlyFailure);
        Assert.Equal(expected, output);
    }

    private static string ErrorFrame(string code, string message) =>
        "data: {\"error\":{\"message\":\"" + message + "\",\"code\":\"" + code + "\",\"type\":\"air_error\"}}\n\ndata: [DONE]\n\n";

    private static HttpClient Client(int port) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static async Task<TimeSpan> ReadCommentAsync(Stream body, int sequence, HeartbeatClock clock, CancellationToken ct)
    {
        var bytes = new byte[256];
        for (var length = 1; length <= bytes.Length; length++)
        {
            await body.ReadExactlyAsync(bytes.AsMemory(length - 1, 1), ct);
            if (length < 2 || !bytes.AsSpan(0, length).EndsWith("\n\n"u8)) continue;

            var prefix = FormattableString.Invariant($": [router keepalive seq={sequence}");
            var comment = Encoding.UTF8.GetString(bytes, 0, length);
            Assert.StartsWith(prefix, comment, StringComparison.Ordinal);
            Assert.True(comment[prefix.Length..] is "]\n\n" ||
                comment.AsSpan(prefix.Length).StartsWith(" status=", StringComparison.Ordinal));
            return TimeSpan.FromTicks(clock.GetTimestamp());
        }
        throw new InvalidDataException("The relay omitted a complete heartbeat comment.");
    }

    private static async Task AssertTimerStoppedAsync(HeartbeatClock clock, HeartbeatTick pending, CancellationToken ct)
    {
        await pending.Disposed.Task.WaitAsync(ct);
        clock.Reach(pending.Due + TimeSpan.FromSeconds(12), pending);
        Assert.False(clock.HasUnreadTimer);
    }

    private sealed class GatedEngine(HeldBody body) : FakeEngineHandler
    {
        private int _started;
        public TaskCompletionSource ModelsReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
                await ModelsReady.Task.WaitAsync(ct);
            if (request.Method == HttpMethod.Post && Interlocked.Increment(ref _started) == 1)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };

            return await base.SendAsync(request, ct);
        }
    }

    private sealed class HeldBody(ReadOnlyMemory<byte> prefix = default, ReadOnlyMemory<byte> suffix = default, bool fail = false) : Stream
    {
        private int _read;
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release()
        {
            AllowFirst.TrySetResult();
            AllowSecond.TrySetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            switch (_read++)
            {
                case 0:
                    FirstRead.TrySetResult();
                    try { await AllowFirst.Task.WaitAsync(cancellationToken); }
                    catch (OperationCanceledException)
                    {
                        FirstReadCancelled.TrySetResult();
                        throw;
                    }
                    if (fail) throw new IOException(EarlyFailure);
                    prefix.CopyTo(buffer);
                    return prefix.Length;
                case 1:
                    SecondRead.TrySetResult();
                    await AllowSecond.Task.WaitAsync(cancellationToken);
                    suffix.CopyTo(buffer);
                    return suffix.Length;
                default:
                    return 0;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HeartbeatClock : TimeProvider
    {
        private readonly Channel<HeartbeatTick> _timers = Channel.CreateUnbounded<HeartbeatTick>();
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public Action? AtFirstDeadline { get; set; }
        public bool HasUnreadTimer => _timers.Reader.TryPeek(out _);
        public ValueTask<HeartbeatTick> NextTimerAsync(CancellationToken ct) => _timers.Reader.ReadAsync(ct);

        public void Reach(TimeSpan time, HeartbeatTick? tick = null)
        {
            Interlocked.Exchange(ref _timestamp, time.Ticks);
            tick?.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var tick = new HeartbeatTick(TimeSpan.FromTicks(GetTimestamp()) + dueTime, callback, state);
            if (AtFirstDeadline is { } atDeadline)
            {
                AtFirstDeadline = null;
                Reach(tick.Due, tick);
                atDeadline();
            }
            else
            {
                Assert.True(_timers.Writer.TryWrite(tick));
            }
            return tick;
        }
    }

    private sealed class HeartbeatTick(TimeSpan due, TimerCallback callback, object? state) : ITimer
    {
        private int _stopped;
        public TimeSpan Due { get; } = due;
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Fire()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0) callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose()
        {
            Interlocked.Exchange(ref _stopped, 1);
            Disposed.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
