using System.IO.Pipelines;
using System.Threading.Channels;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Arboreal.InferenceRouter.Tests;

public partial class StreamRelayTests
{
    [Theory]
    [InlineData("positive", true, true)]
    [InlineData("positive", false, false)]
    [InlineData("matching", true, true)]
    [InlineData("matching", false, true)]
    [InlineData("eof", true, false)]
    [InlineData("eof", false, false)]
    [InlineData("io", true, false)]
    [InlineData("io", false, false)]
    [InlineData("different", true, false)]
    [InlineData("different", false, false)]
    [InlineData("matching-live", false, false)]
    public void DefaultShutdownReadUsesOnlyTheFirstQualifiedObservation(string result, bool callbackFirst, bool expected)
    {
        using var core = new CancellationTokenSource();
        using var execution = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(core.Token, execution.Token, caller.Token);
        var read = new RouterCore.DefaultStreamShutdown(linked.Token, core.Token, execution.Token, caller.Token);
        var arm = read.TryArm();
        Assert.NotEqual(0, arm);

        if (callbackFirst || result == "matching") core.Cancel();
        if (callbackFirst) read.ObserveLinkedCancellation();

        var failure = result switch
        {
            "matching" or "matching-live" => new OperationCanceledException(linked.Token),
            "different" => new OperationCanceledException(new CancellationToken(canceled: true)),
            "io" => new IOException("independent upstream fault"),
            _ => (Exception?)null,
        };
        Assert.Equal(expected, read.ObserveResult(arm, result == "positive" ? 10 : 0, failure));

        core.Cancel();
        read.ObserveLinkedCancellation();
        Assert.False(read.ObserveResult(arm, 10));
        Assert.Equal(expected, read.Qualified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultShutdownReadDoesNotReplaceAnEarlierOrAmbiguousCancellation(bool ambiguous)
    {
        using var core = new CancellationTokenSource();
        using var execution = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(core.Token, execution.Token, caller.Token);
        var read = new RouterCore.DefaultStreamShutdown(linked.Token, core.Token, execution.Token, caller.Token);
        var arm = read.TryArm();
        Assert.NotEqual(0, arm);

        execution.Cancel();
        if (ambiguous) core.Cancel();
        read.ObserveLinkedCancellation();
        core.Cancel();
        read.ObserveLinkedCancellation();

        Assert.False(read.ObserveResult(arm, 0, new OperationCanceledException(linked.Token)));
        Assert.False(read.Qualified);
        Assert.Equal(0, read.TryArm());
    }

    [Fact]
    public void DefaultShutdownReadCannotQualifyWithoutAnArm()
    {
        using var core = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(core.Token);
        var read = new RouterCore.DefaultStreamShutdown(linked.Token, core.Token, default, default);
        core.Cancel();
        read.ObserveLinkedCancellation();
        Assert.Equal(0, read.TryArm());
        Assert.False(read.ObserveResult(0, 0, new OperationCanceledException(linked.Token)));
        Assert.False(read.Qualified);
    }

    public enum ShutdownOutputCall
    {
        InitialKeepaliveWrite,
        InitialKeepaliveFlush,
        RepeatKeepaliveWrite,
        RepeatKeepaliveFlush,
        Payload,
        Drain,
        Terminal,
        TerminalFlush,
    }

    [Theory(Timeout = 20_000)]
    [InlineData(null, false, 1)]
    [InlineData(ShutdownOutputCall.InitialKeepaliveWrite, false, 1)]
    [InlineData(ShutdownOutputCall.InitialKeepaliveWrite, true, 1)]
    [InlineData(ShutdownOutputCall.InitialKeepaliveFlush, false, 1)]
    [InlineData(ShutdownOutputCall.InitialKeepaliveFlush, true, 1)]
    [InlineData(ShutdownOutputCall.RepeatKeepaliveWrite, false, 1)]
    [InlineData(ShutdownOutputCall.RepeatKeepaliveWrite, true, 1)]
    [InlineData(ShutdownOutputCall.RepeatKeepaliveFlush, false, 1)]
    [InlineData(ShutdownOutputCall.RepeatKeepaliveFlush, true, 1)]
    [InlineData(ShutdownOutputCall.Payload, false, 1)]
    [InlineData(ShutdownOutputCall.Payload, true, 1)]
    [InlineData(ShutdownOutputCall.Drain, false, 1)]
    [InlineData(ShutdownOutputCall.Drain, true, 1)]
    [InlineData(ShutdownOutputCall.Payload, false, 3)]
    [InlineData(ShutdownOutputCall.Payload, true, 3)]
    [InlineData(ShutdownOutputCall.Drain, false, 2)]
    [InlineData(ShutdownOutputCall.Drain, true, 2)]
    public async Task DefaultShutdownOutputRequiresEveryLegacyWriteAndFlush(
        ShutdownOutputCall? failed, bool completed, int occurrence)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var clock = new ReadinessDeadlineTests.ManualClock();
        var writer = new ShutdownWriter(failed, new FlushResult(!completed, completed), occurrence: occurrence);
        var ctx = ShutdownContext(writer, ct);
        var (ticket, channel) = ShutdownTicket();
        var relay = ServerApp.StreamTicketAsync(ctx, ticket, clock);
        try
        {
            await UntilAsync(() => clock.ActiveTimers == 1, ct);
            clock.AdvanceTo(4_000);
            await writer.RepeatFlushed.Task.WaitAsync(ct);
            Assert.True(channel.Writer.TryWrite("data: first\n\n"u8.ToArray()));
            await writer.FirstPayload.Task.WaitAsync(ct);
            Assert.True(channel.Writer.TryWrite("data: second\n\n"u8.ToArray()));
            await writer.FirstDrain.Task.WaitAsync(ct);
            Assert.True(channel.Writer.TryWrite("data: third\n\n"u8.ToArray()));
            await writer.SecondDrain.Task.WaitAsync(ct);

            ticket.State = TicketState.Cancelled;
            channel.Writer.TryComplete();
            await relay.WaitAsync(ct);

            ShutdownOutputCall[] expected =
            [
                ShutdownOutputCall.InitialKeepaliveWrite, ShutdownOutputCall.InitialKeepaliveFlush,
                ShutdownOutputCall.RepeatKeepaliveWrite, ShutdownOutputCall.RepeatKeepaliveFlush,
                ShutdownOutputCall.Payload, ShutdownOutputCall.Payload, ShutdownOutputCall.Drain,
                ShutdownOutputCall.Payload, ShutdownOutputCall.Drain,
            ];
            if (failed is null)
                expected = [.. expected, ShutdownOutputCall.Terminal, ShutdownOutputCall.TerminalFlush];
            Assert.Equal(expected, writer.Calls.ToArray());
            Assert.Equal(": [router keepalive seq=0 status=\"queued model=test-model\"]\n\n"u8.ToArray(), writer.Written[0]);
            Assert.Equal(": [router keepalive seq=1]\n\n"u8.ToArray(), writer.Written[1]);
            Assert.Equal("data: first\n\n"u8.ToArray(), writer.Written[2]);
            Assert.Equal("data: second\n\n"u8.ToArray(), writer.Written[3]);
            Assert.Equal("data: third\n\n"u8.ToArray(), writer.Written[4]);
            Assert.Equal(failed.HasValue, writer.Injected);
            Assert.Equal(TicketState.Cancelled, ticket.State);
        }
        finally
        {
            channel.Writer.TryComplete();
            await relay.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData("data:[DONE]\n\n", "", false)]
    [InlineData("data: [DONE]\r\n\r\n", "", false)]
    [InlineData("data: [DONE]\r\r", "\n", false)]
    [InlineData("data: [DONE]\n", "data\n\n", true)]
    [InlineData("data: [DONE]\n", "", false)]
    [InlineData(": data: [DONE]\n\n", "", true)]
    [InlineData("data: not-[DONE]\n\n", "", true)]
    public async Task DefaultShutdownOutputChecksTheAcknowledgedEventBoundary(string data, string next, bool emits)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var writer = new ShutdownWriter(null, default);
        var ctx = ShutdownContext(writer, ct);
        var (ticket, channel) = ShutdownTicket();
        Assert.True(channel.Writer.TryWrite("data: delta\n\n"u8.ToArray()));
        Assert.True(channel.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(data)));
        if (next.Length != 0) Assert.True(channel.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(next)));
        ticket.State = TicketState.Cancelled;
        channel.Writer.TryComplete();

        await ServerApp.StreamTicketAsync(ctx, ticket, TimeProvider.System).WaitAsync(ct);
        Assert.Equal(emits ? 1 : 0, writer.Calls.Count(call => call == ShutdownOutputCall.Terminal));
        Assert.Equal(TicketState.Cancelled, ticket.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultShutdownOutputDoesNotWriteATerminalOnAbortOrWriterException(bool abort)
    {
        using var caller = new CancellationTokenSource();
        var writer = new ShutdownWriter(null, default, throwOnPayload: !abort);
        var ctx = ShutdownContext(writer, caller.Token);
        var (ticket, channel) = ShutdownTicket();
        Assert.True(channel.Writer.TryWrite("data: delta\n\n"u8.ToArray()));
        ticket.State = TicketState.Cancelled;
        channel.Writer.TryComplete();
        if (abort)
        {
            caller.Cancel();
            await ServerApp.StreamTicketAsync(ctx, ticket, TimeProvider.System);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => ServerApp.StreamTicketAsync(ctx, ticket, TimeProvider.System));
        }
        Assert.DoesNotContain(ShutdownOutputCall.Terminal, writer.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task DefaultShutdownOutputDoesNotAttemptTerminalAfterPostPrefixAbort()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(bound.Token);
        var heldDrain = new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new ShutdownWriter(null, default, heldFirstDrain: heldDrain.Task);
        var ctx = ShutdownContext(writer, caller.Token);
        var (ticket, channel) = ShutdownTicket();
        var first = "data: first\n\n"u8.ToArray();
        var second = "data: second\n\n"u8.ToArray();
        Assert.True(channel.Writer.TryWrite(first));
        Assert.True(channel.Writer.TryWrite(second));
        var relay = ServerApp.StreamTicketAsync(ctx, ticket, TimeProvider.System);
        try
        {
            // The payload writes returned successfully and the existing drain flush is pending.
            await writer.FirstDrain.Task.WaitAsync(bound.Token);
            Assert.False(relay.IsCompleted);
            Assert.False(heldDrain.Task.IsCompleted);
            ticket.State = TicketState.Cancelled;
            caller.Cancel();
            channel.Writer.TryComplete();
            heldDrain.TrySetResult(new FlushResult(false, false));
            await relay.WaitAsync(bound.Token);

            ShutdownOutputCall[] expected =
            [
                ShutdownOutputCall.InitialKeepaliveWrite, ShutdownOutputCall.InitialKeepaliveFlush,
                ShutdownOutputCall.Payload, ShutdownOutputCall.Payload, ShutdownOutputCall.Drain,
            ];
            Assert.Equal(expected, writer.Calls.ToArray());
            Assert.Equal(first, writer.Written[1]);
            Assert.Equal(second, writer.Written[2]);
            Assert.True(ticket.DefaultStreamReadShutdown);
            Assert.Equal(TicketState.Cancelled, ticket.State);
        }
        finally
        {
            channel.Writer.TryComplete();
            heldDrain.TrySetResult(new FlushResult(false, false));
            await relay.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    private static (RouterTicket Ticket, Channel<ReadOnlyMemory<byte>> Channel) ShutdownTicket()
    {
        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var ticket = new RouterTicket("ticket", "thread", "test-model", DateTimeOffset.UnixEpoch)
        {
            IsHttpStream = true,
            StreamChannel = channel,
            DefaultStreamReadShutdown = true,
            ErrorCode = "cancelled",
            Error = "cancelled",
        };
        return (ticket, channel);
    }

    private static DefaultHttpContext ShutdownContext(ShutdownWriter writer, CancellationToken caller)
    {
        var ctx = new DefaultHttpContext { RequestAborted = caller };
        ctx.Features.Set<IHttpResponseFeature>(new CommittedResponse());
        ctx.Features.Set<IHttpResponseBodyFeature>(new BodyFeature(writer));
        return ctx;
    }

    private sealed class CommittedResponse : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class ShutdownWriter(ShutdownOutputCall? failed, FlushResult bad, bool throwOnPayload = false,
        int occurrence = 1, Task<FlushResult>? heldFirstDrain = null) : PipeWriter
    {
        private ShutdownOutputCall _lastWrite;
        private int _payloads;
        private int _drains;
        private int _matchingCalls;

        internal List<ShutdownOutputCall> Calls { get; } = [];
        internal List<byte[]> Written { get; } = [];
        internal bool Injected { get; private set; }
        internal TaskCompletionSource RepeatFlushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstPayload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstDrain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondDrain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            _lastWrite = source.Span switch
            {
                var bytes when bytes.StartsWith(": [router keepalive seq=0"u8) => ShutdownOutputCall.InitialKeepaliveWrite,
                var bytes when bytes.StartsWith(": [router keepalive seq="u8) => ShutdownOutputCall.RepeatKeepaliveWrite,
                var bytes when bytes.StartsWith("data: {\"error\":"u8) => ShutdownOutputCall.Terminal,
                _ => ShutdownOutputCall.Payload,
            };
            Written.Add(source.ToArray());
            var result = Record(_lastWrite);
            if (_lastWrite == ShutdownOutputCall.Payload)
            {
                if (throwOnPayload) throw new IOException("scripted lost writer");
                if (++_payloads == 1) FirstPayload.TrySetResult();
            }
            return result;
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            var call = _lastWrite switch
            {
                ShutdownOutputCall.InitialKeepaliveWrite => ShutdownOutputCall.InitialKeepaliveFlush,
                ShutdownOutputCall.RepeatKeepaliveWrite => ShutdownOutputCall.RepeatKeepaliveFlush,
                ShutdownOutputCall.Terminal => ShutdownOutputCall.TerminalFlush,
                _ => ShutdownOutputCall.Drain,
            };
            var result = Record(call);
            if (call == ShutdownOutputCall.RepeatKeepaliveFlush) RepeatFlushed.TrySetResult();
            if (call == ShutdownOutputCall.Drain)
            {
                if (++_drains == 1)
                {
                    FirstDrain.TrySetResult();
                    if (heldFirstDrain is not null) return new ValueTask<FlushResult>(heldFirstDrain);
                }
                else SecondDrain.TrySetResult();
            }
            return result;
        }

        private ValueTask<FlushResult> Record(ShutdownOutputCall call)
        {
            Calls.Add(call);
            if (call != failed || ++_matchingCalls != occurrence)
                return ValueTask.FromResult(new FlushResult(false, false));
            Injected = true;
            return ValueTask.FromResult(bad);
        }

        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception = null) { }
        public override void Advance(int bytes) => throw new NotSupportedException();
        public override Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();
        public override Span<byte> GetSpan(int sizeHint = 0) => throw new NotSupportedException();
    }
}
