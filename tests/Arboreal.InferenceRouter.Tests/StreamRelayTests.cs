using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public class StreamRelayTests
{
    private const int Credit = 262_144;
    private const string Chat = """{"model":"test-model","stream":true,"messages":[{"role":"user","content":"ping"}]}""";
    private static readonly byte[] Done = "data: [DONE]\r\n\r\n"u8.ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedLoadersDefaultOffAndRequireRestartForLimitChanges(bool json)
    {
        var path = json ? "relay.json" : "relay.toml";
        var disabled = RouterConfigLoader.LoadForReload(Declaration(json, 0, 0), path);
        var positive = RouterConfigLoader.LoadForReload(Declaration(json, Credit, 300_000), path);
        Assert.Equal(0, disabled.Config.StreamBufferLimitBytes);
        Assert.Equal(0, disabled.Config.Engines["fake"].StreamIdleTimeoutMs);
        Assert.Equal(Credit, positive.Config.StreamBufferLimitBytes);
        Assert.Equal(300_000, positive.Config.Engines["fake"].StreamIdleTimeoutMs);
        var bufferOnly = RouterConfigLoader.LoadForReload(Declaration(json, Credit, 0), path);
        var idleOnly = RouterConfigLoader.LoadForReload(Declaration(json, 0, 300_000), path);
        Assert.Equal(ConfigReloadStatus.RestartRequired, disabled.Compare(bufferOnly, out _, out _));
        Assert.Equal(ConfigReloadStatus.RestartRequired, disabled.Compare(idleOnly, out _, out _));
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.LoadForReload(Declaration(json, -1, 0), path));
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.LoadForReload(Declaration(json, 0, -1), path));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(Credit, 0)]
    [InlineData(0, 300_000)]
    [InlineData(Credit, 300_000)]
    public async Task OptInCombinationsPreserveHalfMegabyteEventAndUpstreamDone(int credit, int idle)
    {
        var expected = CompleteEvent(512 * 1024).Concat(Done).ToArray();
        using var fake = new StreamEngine(new MemoryStream(expected));
        var (app, _, port) = await WireHarness.StartAsync(fake: fake,
            streamBufferLimit: credit, streamIdleTimeoutMs: idle);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var router = app.Services.GetRequiredService<RouterCore>();
        var enqueued = new TaskCompletionSource<RouterTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeforeEnqueueFeedForTests = ticket => enqueued.TrySetResult(ticket);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var (id, response) = await WireHarness.StartStreamAsync(client, "relay", Chat, bound.Token);
        using var stream = response;
        var wire = await stream.Content.ReadAsByteArrayAsync(bound.Token);
        Assert.Equal(expected, WithoutComments(wire));
        var ticket = await enqueued.Task.WaitAsync(bound.Token);
        Assert.Equal(id, ticket.Id);
        await ticket.CompletionTask.WaitAsync(bound.Token);
        var snapshot = Assert.IsType<TicketSnapshot>(router.TryGetTicket(id));
        Assert.Equal(nameof(TicketState.Completed), snapshot.State);
        if (credit > 0)
            Assert.Equal(0, Assert.IsType<HttpStreamDelivery>(ticket.HttpDelivery).ReservedBytes);
        else
            Assert.Null(ticket.HttpDelivery);
    }

    [Fact]
    public async Task CreditsCoverDequeuedBytesAndSlicesHaveIndependentBoundedBackings()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var delivery = new HttpStreamDelivery(Credit);
        var output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var batch = CompleteEvent(512 * 1024);
        var publishing = delivery.PublishAsync(output.Writer, batch, bound.Token).AsTask();
        var first = await output.Reader.ReadAsync(bound.Token);
        Assert.False(publishing.IsCompleted);
        Assert.Equal(Credit, delivery.ReservedBytes);
        Assert.True(MemoryMarshal.TryGetArray(first, out var firstBacking));
        Assert.Equal(Credit, firstBacking.Array!.Length);
        Assert.NotSame(batch, firstBacking.Array);
        Assert.False(output.Reader.TryRead(out _));
        delivery.Release(first.Length);
        var second = await output.Reader.ReadAsync(bound.Token);
        await publishing.WaitAsync(bound.Token);
        Assert.Equal(Credit, delivery.ReservedBytes);
        Assert.True(MemoryMarshal.TryGetArray(second, out var secondBacking));
        Assert.Equal(Credit, secondBacking.Array!.Length);
        Assert.NotSame(firstBacking.Array, secondBacking.Array);
        Assert.Equal(batch, first.ToArray().Concat(second.ToArray()).ToArray());
        delivery.Release(second.Length);
        Assert.Equal(0, delivery.ReservedBytes);
    }

    [Fact]
    public async Task FullCreditPausesRawReadsAndAnIdleErrorFollowsWholeValidatedEvent()
    {
        var complete = CompleteEvent(512 * 1024);
        using var body = new HeldRead(complete);
        using var fake = new StreamEngine(body);
        var (app, _, _) = await WireHarness.StartAsync(fake: fake, streamBufferLimit: Credit,
            streamIdleTimeoutMs: 100);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var router = app.Services.GetRequiredService<RouterCore>();
        var idle = new ReadinessDeadlineTests.ManualClock();
        router.StreamIdleClockForTests = idle;
        var ticket = await router.EnqueueHttpAsync("pressure", Request(), bound.Token);
        var ctx = new DefaultHttpContext { RequestAborted = bound.Token };
        var writer = new GatedWriter(2);
        ctx.Features.Set<IHttpResponseBodyFeature>(new BodyFeature(writer));
        var relay = ServerApp.StreamTicketAsync(ctx, ticket, TimeProvider.System, router);
        try
        {
            Assert.Equal(Credit, await writer.Blocked.Task.WaitAsync(bound.Token));
            Assert.Equal(Credit, Assert.IsType<HttpStreamDelivery>(ticket.HttpDelivery).ReservedBytes);
            Assert.False(body.ReadingLate.Task.IsCompleted);
            Assert.Equal(0, idle.ActiveTimers);
            idle.AdvanceTo(101);
            Assert.False(ticket.CompletionTask.IsCompleted);
            writer.Release.TrySetResult(new FlushResult(false, false));

            await body.ReadingLate.Task.WaitAsync(bound.Token);
            await UntilAsync(() => idle.ActiveTimers == 1, bound.Token);
            idle.AdvanceTo(202);
            await relay.WaitAsync(bound.Token);
            var payload = WithoutComments(writer.Received.SelectMany(static bytes => bytes).ToArray());
            Assert.True(payload.AsSpan().StartsWith(complete));
            var terminal = Encoding.UTF8.GetString(payload.AsSpan(complete.Length));
            Assert.Contains("\"code\":\"engine_timeout\"", terminal, StringComparison.Ordinal);
            Assert.EndsWith("\n\ndata: [DONE]\n\n", terminal, StringComparison.Ordinal);
        }
        finally
        {
            writer.Release.TrySetResult(new FlushResult(true, false));
            body.Finish.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false, Credit, false)]
    [InlineData(false, 0, true)]
    [InlineData(true, Credit, false)]
    public async Task IdleAfterFirstRawByteFinishesWithOneDoneAndKeepsLateOrdinaryUse(
        bool upstreamDone, int credit, bool unfinished)
    {
        var prefix = upstreamDone ? Done : "data: partial but complete\n\n"u8.ToArray();
        var source = unfinished ? prefix.Concat("data: staged partial"u8.ToArray()).ToArray() : prefix;
        using var body = new HeldRead(source);
        using var fake = new StreamEngine(body);
        var (app, _, port) = await WireHarness.StartAsync(fake: fake, maxInflight: 1,
            streamBufferLimit: credit, streamIdleTimeoutMs: 300_000);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var router = app.Services.GetRequiredService<RouterCore>();
        var idle = new ReadinessDeadlineTests.ManualClock();
        router.StreamIdleClockForTests = idle;
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        try
        {
            var (id, response) = await WireHarness.StartStreamAsync(client, "idle", Chat, bound.Token);
            using var stream = response;
            await body.ReadingLate.Task.WaitAsync(bound.Token);
            await UntilAsync(() => idle.ActiveTimers == 1, bound.Token);
            idle.AdvanceTo(299_999);
            var active = Assert.IsType<TicketSnapshot>(router.TryGetTicket(id));
            Assert.Equal(nameof(TicketState.Executing), active.State);
            Assert.Null(active.ErrorCode);
            idle.AdvanceTo(300_001);
            var wire = await stream.Content.ReadAsByteArrayAsync(bound.Token);
            var payload = WithoutComments(wire);
            Assert.True(payload.AsSpan().StartsWith(prefix));
            if (upstreamDone)
                Assert.Equal(prefix, payload);
            else
            {
                var terminal = Encoding.UTF8.GetString(payload.AsSpan(prefix.Length));
                Assert.Contains("\"code\":\"engine_timeout\"", terminal, StringComparison.Ordinal);
                Assert.EndsWith("\n\ndata: [DONE]\n\n", terminal, StringComparison.Ordinal);
                Assert.DoesNotContain("staged partial", terminal, StringComparison.Ordinal);
            }
            var ended = Assert.IsType<TicketSnapshot>(router.TryGetTicket(id));
            Assert.Equal(nameof(TicketState.Failed), ended.State);
            Assert.Equal("engine_timeout", ended.ErrorCode);
            var engine = router.Engines["fake"];
            await UntilAsync(() => router.GetStats().Engines.Single().Inflight == 0, bound.Token);
            Assert.Equal(1, engine.HttpUsesForTests);
            Assert.False(body.Disposed.Task.IsCompleted);
            body.Finish.TrySetResult();
            await body.Disposed.Task.WaitAsync(bound.Token);
            await UntilAsync(() => engine.HttpUsesForTests == 0, bound.Token);
        }
        finally { body.Finish.TrySetResult(); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task FlushAcknowledgmentControlsSuccessAndOneByteWatchdog(bool empty, bool stall, bool downstreamEnded)
    {
        using var fake = new StreamEngine(new MemoryStream(empty ? [] : [(byte)'\n']));
        var clock = new ReadinessDeadlineTests.ManualClock();
        var responseClock = new ReadinessDeadlineTests.ManualClock();
        var (app, _, _) = await WireHarness.StartAsync(fake: fake, maxInflight: 1, responseTimeoutMs: 100,
            streamBufferLimit: Credit, streamClock: clock);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var router = app.Services.GetRequiredService<RouterCore>();
        router.ResponseTimeoutClockForTests = responseClock;
        var ticket = await router.EnqueueHttpAsync("flush", Request(), bound.Token);
        var ctx = new DefaultHttpContext();
        ctx.Request.Protocol = "HTTP/2";
        ctx.RequestAborted = bound.Token;
        var writer = new GatedWriter(empty ? 1 : 2);
        ctx.Features.Set<IHttpResponseBodyFeature>(new BodyFeature(writer));
        var relay = ServerApp.StreamTicketAsync(ctx, ticket, clock, router);
        var delivery = Assert.IsType<HttpStreamDelivery>(ticket.HttpDelivery);
        try
        {
            var pendingBytes = await writer.Blocked.Task.WaitAsync(bound.Token);
            await delivery.Sealed.WaitAsync(bound.Token);
            Assert.False(ticket.CompletionTask.IsCompleted);
            Assert.Equal(empty ? 0 : 1, delivery.ReservedBytes);
            Assert.False(ctx.Response.Headers.ContainsKey("Connection"));
            Assert.Equal(0, responseClock.ActiveTimers);
            responseClock.AdvanceTo(101);
            Assert.False(ticket.CompletionTask.IsCompleted);
            await UntilAsync(() => clock.ActiveTimers == 1, bound.Token);
            clock.AdvanceTo(29_999);
            Assert.False(ticket.CompletionTask.IsCompleted);
            if (stall)
            {
                Assert.Equal(1, pendingBytes);
                clock.AdvanceTo(30_001);
                var error = await Assert.ThrowsAsync<RouterRequestException>(
                    () => ticket.CompletionTask.WaitAsync(bound.Token));
                Assert.Equal("slow_consumer", error.Code);
                var failed = Assert.Single(router.Events.Replay(0), row =>
                    row.TicketId == ticket.Id && row.Kind == "request.failed");
                Assert.StartsWith("slow_consumer", failed.Reason, StringComparison.Ordinal);
            }
            else if (downstreamEnded)
            {
                writer.Release.TrySetResult(new FlushResult(isCanceled: false, isCompleted: true));
                var error = await Assert.ThrowsAsync<RouterRequestException>(
                    () => ticket.CompletionTask.WaitAsync(bound.Token));
                Assert.Equal("cancelled", error.Code);
            }
            else
            {
                writer.Release.TrySetResult(new FlushResult(isCanceled: false, isCompleted: false));
                await ticket.CompletionTask.WaitAsync(bound.Token);
                Assert.Equal(TicketState.Completed, ticket.State);
            }
            await relay.WaitAsync(bound.Token);
            Assert.Equal(0, delivery.ReservedBytes);
        }
        finally { writer.Release.TrySetResult(new FlushResult(isCanceled: true, isCompleted: false)); }
    }

    [Fact]
    public async Task LibraryCompletionCanPrecedeDrainAboveHttpLimit()
    {
        var expected = CompleteEvent(512 * 1024);
        using var fake = new StreamEngine(new MemoryStream(expected));
        var (app, _, _) = await WireHarness.StartAsync(fake: fake,
            streamBufferLimit: Credit, streamIdleTimeoutMs: 300_000);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var router = app.Services.GetRequiredService<RouterCore>();
        var ticket = await router.EnqueueAsync("library", Request(), bound.Token);
        await ticket.CompletionTask.WaitAsync(bound.Token);
        Assert.Contains(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.completed");
        Assert.Null(ticket.HttpDelivery);
        var channel = Assert.IsAssignableFrom<Channel<ReadOnlyMemory<byte>>>(await ticket.StreamReady.WaitAsync(bound.Token));
        using var bytes = new MemoryStream();
        await foreach (var chunk in channel.Reader.ReadAllAsync(bound.Token))
            bytes.Write(chunk.Span);
        Assert.Equal(expected, bytes.ToArray());
    }

    private static InferenceRequest Request() => new()
    {
        Model = "test-model", Stream = true, Body = Encoding.UTF8.GetBytes(Chat), Options = new RouterRequestOptions(),
    };

    private static string Declaration(bool json, int credit, int idle) =>
        json ? $$"""
        {
          "stream_buffer_limit_bytes": {{credit}},
          "engine": {
            "fake": { "kind": "openai-compatible", "endpoint": "http://localhost:9/v1", "stream_idle_timeout_ms": {{idle}} }
          }
        }
        """ : FormattableString.Invariant($"""
        stream_buffer_limit_bytes = {credit}

        [engine.fake]
        kind = "openai-compatible"
        endpoint = "http://localhost:9/v1"
        stream_idle_timeout_ms = {idle}
        """);

    private static byte[] CompleteEvent(int length) =>
        Encoding.UTF8.GetBytes("data: " + new string('x', length - 8) + "\n\n");

    private static byte[] WithoutComments(byte[] wire)
    {
        var body = wire.AsSpan();
        while (body.StartsWith(": [router keepalive "u8))
        {
            var ending = body.IndexOf("\n\n"u8);
            Assert.True(ending >= 0);
            body = body[(ending + 2)..];
        }
        return body.ToArray();
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed class StreamEngine(Stream body) : FakeEngineHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            return await base.SendAsync(request, ct);
        }
    }

    private sealed class HeldRead(byte[] complete) : MemoryStream
    {
        private int _offset;
        public TaskCompletionSource ReadingLate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < complete.Length)
            {
                var count = Math.Min(buffer.Length, complete.Length - _offset);
                complete.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            ReadingLate.TrySetResult();
            await Finish.Task;
            buffer.Span[0] = (byte)'x';
            return 1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private sealed class GatedWriter(int blockWrite) : PipeWriter
    {
        private int _writes;
        public TaskCompletionSource<int> Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<FlushResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Received { get; } = [];

        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            Received.Add(source.ToArray());
            if (++_writes != blockWrite) return ValueTask.FromResult(new FlushResult(false, false));
            Blocked.TrySetResult(source.Length);
            return new ValueTask<FlushResult>(Release.Task);
        }

        public override void CancelPendingFlush() => Release.TrySetResult(new FlushResult(true, false));
        public override void Complete(Exception? exception = null) { }
        public override void Advance(int bytes) => throw new NotSupportedException();
        public override Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();
        public override Span<byte> GetSpan(int sizeHint = 0) => throw new NotSupportedException();
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BodyFeature(PipeWriter writer) : IHttpResponseBodyFeature
    {
        public Stream Stream => Stream.Null;
        public PipeWriter Writer => writer;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteAsync() => Task.CompletedTask;
        public void DisableBuffering() { }
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
