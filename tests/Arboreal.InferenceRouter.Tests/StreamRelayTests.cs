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

public partial class StreamRelayTests
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
            Assert.Equal("failed", ended.UpstreamOutcome);
            Assert.Equal("failed", ended.UpstreamEndReason);
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
                Assert.Equal(TicketState.Failed, ticket.State);
                Assert.Equal("incomplete", failed.UpstreamOutcome);
                Assert.Equal("upstream_closed_without_terminator", failed.UpstreamEndReason);
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
            if (stall)
            {
                var flush = await writer.Release.Task.WaitAsync(bound.Token);
                Assert.True(flush.IsCanceled);
                // The detached writer can return its credit after the relay has exited.
                await UntilAsync(() => delivery.ReservedBytes == 0, bound.Token);
            }
            Assert.Equal(0, delivery.ReservedBytes);
        }
        finally { writer.Release.TrySetResult(new FlushResult(isCanceled: true, isCompleted: false)); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NonIoWriterFailureSettlesSealedRelayEvenWithoutRequestAbort(bool initialComment)
    {
        using var fake = new StreamEngine(new MemoryStream(initialComment ? [] : [(byte)'\n']));
        var (app, _, _) = await WireHarness.StartAsync(fake: fake, streamBufferLimit: Credit);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var router = app.Services.GetRequiredService<RouterCore>();
        var ticket = await router.EnqueueHttpAsync("writer-fault", Request(), bound.Token);
        var ctx = new DefaultHttpContext();
        var lifetime = new PassiveLifetime { RequestAborted = bound.Token };
        ctx.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var writer = new GatedWriter(initialComment ? 1 : 2);
        ctx.Features.Set<IHttpResponseBodyFeature>(new BodyFeature(writer));
        var delivery = Assert.IsType<HttpStreamDelivery>(ticket.HttpDelivery);
        var relay = ServerApp.StreamTicketAsync(ctx, ticket, TimeProvider.System, router);
        try
        {
            await writer.Blocked.Task.WaitAsync(bound.Token);
            await delivery.Sealed.WaitAsync(bound.Token);
            Assert.False(ticket.CompletionTask.IsCompleted);
            writer.Release.TrySetException(initialComment
                ? new InvalidOperationException("scripted initial-comment failure")
                : new ObjectDisposedException("scripted final payload writer"));
            await relay.WaitAsync(bound.Token);
            var error = await Assert.ThrowsAsync<RouterRequestException>(
                () => ticket.CompletionTask.WaitAsync(bound.Token));
            Assert.Equal("cancelled", error.Code);
            Assert.False(ctx.RequestAborted.IsCancellationRequested);
            Assert.True(lifetime.Aborted);
            Assert.Equal(0, delivery.ReservedBytes);
            Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.cancelled");
        }
        finally { writer.Release.TrySetResult(new FlushResult(true, false)); }
    }

    [Fact]
    public async Task UncooperativeWriteKeepsItsCreditWhileAbortDiscardsTheOtherSlice()
    {
        using var body = new SeparateBlankBlocks();
        using var fake = new StreamEngine(body);
        var clock = new ReadinessDeadlineTests.ManualClock();
        var (app, _, _) = await WireHarness.StartAsync(fake: fake, streamBufferLimit: Credit, streamClock: clock);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var router = app.Services.GetRequiredService<RouterCore>();
        var ticket = await router.EnqueueHttpAsync("uncooperative-sink", Request(), bound.Token);
        var ctx = new DefaultHttpContext();
        var lifetime = new PassiveLifetime { RequestAborted = bound.Token };
        ctx.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var writer = new GatedWriter(2, ignoreCancel: true);
        ctx.Features.Set<IHttpResponseBodyFeature>(new BodyFeature(writer));
        var relay = ServerApp.StreamTicketAsync(ctx, ticket, clock, router);
        var delivery = Assert.IsType<HttpStreamDelivery>(ticket.HttpDelivery);
        try
        {
            Assert.Equal(1, await writer.Blocked.Task.WaitAsync(bound.Token));
            await delivery.Sealed.WaitAsync(bound.Token);
            Assert.Equal(2, delivery.ReservedBytes);
            await UntilAsync(() => clock.ActiveTimers == 1, bound.Token);
            clock.AdvanceTo(30_001);

            await relay.WaitAsync(bound.Token);
            var error = await Assert.ThrowsAsync<RouterRequestException>(
                () => ticket.CompletionTask.WaitAsync(bound.Token));
            Assert.Equal("slow_consumer", error.Code);
            Assert.True(lifetime.Aborted);
            Assert.False(writer.Release.Task.IsCompleted);
            Assert.False(ticket.StreamChannel!.Reader.TryRead(out _));
            Assert.Equal(1, delivery.ReservedBytes);
            await UntilAsync(() => router.GetStats().Engines.Single().Inflight == 0, bound.Token);

            writer.Release.TrySetException(new IOException("scripted late write fault"));
            await UntilAsync(() => delivery.ReservedBytes == 0, bound.Token);
            Assert.Equal("slow_consumer", ticket.ErrorCode);
            Assert.Equal(2, writer.Received.Count); // Initial comment and the only attempted payload.
        }
        finally { writer.Release.TrySetResult(new FlushResult(true, false)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAtWatchdogClaimPreservesCallerOrShutdownCause(bool shutdown)
    {
        using var fake = new StreamEngine(new MemoryStream([(byte)'\n']));
        var clock = new ReadinessDeadlineTests.ManualClock();
        var (app, _, _) = await WireHarness.StartAsync(fake: fake, streamBufferLimit: Credit, streamClock: clock);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(bound.Token);
        var router = app.Services.GetRequiredService<RouterCore>();
        Task? stopped = null;
        var settle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeforeOptInStreamSettlementForTests = _ => settle.Task;
        router.BeforeSlowStreamDecisionForTests = () =>
        {
            if (shutdown) stopped = router.DisposeAsync().AsTask();
            else caller.Cancel();
        };
        var ticket = await router.EnqueueHttpAsync("watchdog-race", Request(), caller.Token);
        var ctx = new DefaultHttpContext { RequestAborted = caller.Token };
        var writer = new GatedWriter(2);
        ctx.Features.Set<IHttpResponseBodyFeature>(new BodyFeature(writer));
        var relay = ServerApp.StreamTicketAsync(ctx, ticket, clock, router);
        try
        {
            Assert.Equal(1, await writer.Blocked.Task.WaitAsync(bound.Token));
            var delivery = Assert.IsType<HttpStreamDelivery>(ticket.HttpDelivery);
            await delivery.Sealed.WaitAsync(bound.Token);
            await UntilAsync(() => clock.ActiveTimers == 1, bound.Token);
            clock.AdvanceTo(30_001);
            await relay.WaitAsync(bound.Token);
            Assert.Equal("cancelled", (await delivery.Outcome.WaitAsync(bound.Token)).Code);
            Assert.False(ticket.CompletionTask.IsCompleted);
            settle.TrySetResult();
            var error = await Assert.ThrowsAsync<RouterRequestException>(
                () => ticket.CompletionTask.WaitAsync(bound.Token));
            Assert.Equal("cancelled", error.Code);
            var outcome = Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id &&
                row.Kind is "request.cancelled" or "request.failed" or "request.completed");
            Assert.Equal("request.cancelled", outcome.Kind);
            Assert.Equal("incomplete", outcome.UpstreamOutcome);
            Assert.Equal("upstream_closed_without_terminator", outcome.UpstreamEndReason);
            if (stopped is not null) await stopped.WaitAsync(bound.Token);
        }
        finally
        {
            settle.TrySetResult();
            writer.Release.TrySetResult(new FlushResult(true, false));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalAcknowledgmentAndCancellationKeepTheFirstDecision(bool acknowledgeFirst)
    {
        using var fake = new StreamEngine(new MemoryStream(Done));
        var (app, _, _) = await WireHarness.StartAsync(fake: fake, streamBufferLimit: Credit);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(bound.Token);
        var router = app.Services.GetRequiredService<RouterCore>();
        var deciding = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeforeOptInStreamSettlementForTests = id =>
        {
            deciding.TrySetResult(id);
            return settle.Task;
        };
        var ticket = await router.EnqueueHttpAsync("flush-race", Request(), caller.Token);
        var ctx = new DefaultHttpContext { RequestAborted = caller.Token };
        var writer = new GatedWriter(2);
        ctx.Features.Set<IHttpResponseBodyFeature>(new BodyFeature(writer));
        var relay = ServerApp.StreamTicketAsync(ctx, ticket, TimeProvider.System, router);
        var delivery = Assert.IsType<HttpStreamDelivery>(ticket.HttpDelivery);
        try
        {
            Assert.Equal(Done.Length, await writer.Blocked.Task.WaitAsync(bound.Token));
            await delivery.Sealed.WaitAsync(bound.Token);
            Assert.Equal(Done, writer.Received[1]);
            Assert.False(writer.Release.Task.IsCompleted);
            Assert.False(delivery.Outcome.IsCompleted);
            if (acknowledgeFirst)
                writer.Release.TrySetResult(new FlushResult(false, false));
            else
                Assert.True(router.TryCancel(ticket.Id));
            Assert.Equal(ticket.Id, await deciding.Task.WaitAsync(bound.Token));
            Assert.False(ticket.CompletionTask.IsCompleted);
            if (acknowledgeFirst)
            {
                Assert.Null((await delivery.Outcome.WaitAsync(bound.Token)).Code);
                // Hang up through the caller token while the accepted HTTP acknowledgment is
                // waiting for the dispatcher to publish success.
                await caller.CancelAsync();
            }
            else
            {
                Assert.Equal("cancelled", (await delivery.Outcome.WaitAsync(bound.Token)).Code);
                writer.Release.TrySetResult(new FlushResult(false, false));
            }
            settle.TrySetResult();
            if (acknowledgeFirst)
                await ticket.CompletionTask.WaitAsync(bound.Token);
            else
            {
                var error = await Assert.ThrowsAsync<RouterRequestException>(
                    () => ticket.CompletionTask.WaitAsync(bound.Token));
                Assert.Equal("cancelled", error.Code);
            }
            await relay.WaitAsync(bound.Token);
            Assert.Equal(Done, WithoutComments(writer.Received.SelectMany(static bytes => bytes).ToArray()));
            var outcome = Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id &&
                row.Kind is "request.cancelled" or "request.failed" or "request.completed");
            Assert.Equal(acknowledgeFirst ? "request.completed" : "request.cancelled", outcome.Kind);
            Assert.Equal("complete", outcome.UpstreamOutcome);
            Assert.Equal("complete", outcome.UpstreamEndReason);
        }
        finally
        {
            settle.TrySetResult();
            writer.Release.TrySetResult(new FlushResult(true, false));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateContentAcquisitionStaysPhysicallyOwnedThroughResponseDisposal(bool fault)
    {
        using var content = new HeldAcquisitionContent();
        using var fake = new ContentEngine(content);
        using var late = new DisposalStream();
        var (app, _, _) = await WireHarness.StartAsync(fake: fake, maxInflight: 1, responseTimeoutMs: 100,
            streamBufferLimit: Credit);
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var router = app.Services.GetRequiredService<RouterCore>();
        var responseClock = new ReadinessDeadlineTests.ManualClock();
        router.ResponseTimeoutClockForTests = responseClock;
        var cleaned = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.AfterOptInTransportTaskForTests = id => cleaned.TrySetResult(id);
        var ticket = await router.EnqueueHttpAsync("acquisition", Request(), bound.Token);
        try
        {
            await content.Acquiring.Task.WaitAsync(bound.Token);
            responseClock.AdvanceTo(101);
            var error = await Assert.ThrowsAsync<RouterRequestException>(
                () => ticket.CompletionTask.WaitAsync(bound.Token));
            Assert.Equal("engine_timeout", error.Code);
            var engine = router.Engines["fake"];
            await UntilAsync(() => router.GetStats().Engines.Single().Inflight == 0, bound.Token);
            Assert.Equal(1, engine.HttpUsesForTests);
            Assert.Equal(0, fake.ResponseDisposals);
            Assert.Equal(0, content.DisposeCount);
            Assert.Equal(0, late.DisposeCount);
            Assert.False(content.Disposed.Task.IsCompleted);
            Assert.False(cleaned.Task.IsCompleted);
            if (fault) content.Finish.TrySetException(new IOException("scripted late acquisition"));
            else content.Finish.TrySetResult(late);
            Assert.Equal(ticket.Id, await cleaned.Task.WaitAsync(bound.Token));
            Assert.Equal(1, fake.ResponseDisposals);
            Assert.True(content.Disposed.Task.IsCompletedSuccessfully);
            Assert.Equal(1, content.DisposeCount);
            Assert.Equal(fault ? 0 : 1, late.DisposeCount);
            Assert.Equal(0, engine.HttpUsesForTests);
            Assert.False(ticket.StreamChannel!.Reader.TryRead(out _));
            Assert.Equal("engine_timeout", ticket.ErrorCode);
        }
        finally { content.Finish.TrySetResult(late); }
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

    private sealed class ContentEngine(HttpContent content) : FakeEngineHandler
    {
        private int _responseDisposals;
        public int ResponseDisposals => Volatile.Read(ref _responseDisposals);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
                return new DisposalResponse(() => Interlocked.Increment(ref _responseDisposals)) { Content = content };
            return await base.SendAsync(request, ct);
        }
    }

    private sealed class DisposalResponse(Action recordDisposal) : HttpResponseMessage(HttpStatusCode.OK)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) recordDisposal();
            base.Dispose(disposing);
        }
    }

    private sealed class HeldAcquisitionContent : HttpContent
    {
        public int DisposeCount { get; private set; }
        public TaskCompletionSource Acquiring { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Stream> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            Acquiring.TrySetResult();
            return Finish.Task;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                DisposeCount++;
                Disposed.TrySetResult();
            }
        }
    }

    private sealed class DisposalStream : MemoryStream
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
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

    private sealed class SeparateBlankBlocks() : MemoryStream([(byte)'\n', (byte)'\n'])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..1], cancellationToken);
    }

    internal sealed class GatedWriter(int blockWrite, bool ignoreCancel = false, bool blockFirstFlush = false) : PipeWriter
    {
        private int _writes;
        private int _flushes;
        private int _cancelPending;
        internal int CancelPendingCalls => Volatile.Read(ref _cancelPending);
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

        public override void CancelPendingFlush()
        {
            Interlocked.Increment(ref _cancelPending);
            if (!ignoreCancel) Release.TrySetResult(new FlushResult(true, false));
        }
        public override void Complete(Exception? exception = null) { }
        public override void Advance(int bytes) => throw new NotSupportedException();
        public override Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();
        public override Span<byte> GetSpan(int sizeHint = 0) => throw new NotSupportedException();
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (!blockFirstFlush || ++_flushes != 1) return ValueTask.FromResult(new FlushResult(false, false));
            Blocked.TrySetResult(0);
            return new ValueTask<FlushResult>(Release.Task);
        }
    }

    private sealed class PassiveLifetime : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    internal sealed class BodyFeature(PipeWriter writer) : IHttpResponseBodyFeature
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
