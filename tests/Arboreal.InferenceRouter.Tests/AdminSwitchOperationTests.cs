using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arboreal.InferenceRouter.Tests;

public sealed class AdminSwitchOperationTests
{
    private const string Full = "The explicit switch operation limit is full. Retry later.";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task AdminSwitch_AcceptedAckCarriesOperationId()
    {
        var (app, _, port) = await WireHarness.StartAsync();
        await using var server = app;
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using var content = new StringContent("""{"engine":"fake","model":"test-model"}""", Encoding.UTF8, "application/json");
        using var reply = await client.PostAsync("/admin/switch", content, bound.Token);
        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        using var ack = JsonDocument.Parse(await reply.Content.ReadAsByteArrayAsync(bound.Token));
        Assert.True(ack.RootElement.TryGetProperty("OperationId", out var id), "Accepted ACK omitted OperationId.");
        Assert.True(Guid.TryParseExact(id.GetString(), "N", out _));
    }

    [Fact]
    public async Task AdminSwitch_ImmediateRefusalsAndUnknownLookup()
    {
        await using var rig = await Fixture.StartAsync();
        using var accepted = await rig.PostAsync("b");
        var id = await AckAsync(accepted, "b", rig.Token);
        await rig.Provider.Entered.Task.WaitAsync(rig.Token);
        await rig.AssertLookupAsync(id, "pending", null);
        using var conflict = await rig.PostAsync("c");
        await ErrorAsync(conflict, HttpStatusCode.Conflict, "switch_in_progress", rig.Token);
        using var unknown = await rig.PostAsync("b", "missing");
        await ErrorAsync(unknown, HttpStatusCode.BadRequest, "unknown_profile", rig.Token);
        Assert.Equal(1, rig.Provider.Launches);
        await rig.AssertMissingAsync("not-a-guid");
        await rig.AssertMissingAsync(Guid.NewGuid().ToString("N"));
        rig.Provider.Release.TrySetResult();
        await rig.AwaitLookupAsync(id, "succeeded", null);
        await rig.AssertTopologyAppliesAsync();
    }

    [Fact]
    public async Task AdminSwitch_AcknowledgesWhilePendingAndReturnsReadyById()
    {
        await using var rig = await Fixture.StartAsync();
        using var response = await rig.PostAsync("b");
        var id = await AckAsync(response, "b", rig.Token);
        await rig.Provider.Entered.Task.WaitAsync(rig.Token);
        await rig.AssertLookupAsync(id, "pending", null);
        rig.Provider.Release.TrySetResult();
        await rig.AwaitLookupAsync(id, "succeeded", null);
        await rig.AssertLookupAsync(id, "succeeded", null);
        using var alreadyReady = await rig.PostAsync("b");
        var next = await AckAsync(alreadyReady, "b", rig.Token);
        Assert.NotEqual(id, next);
        await rig.AwaitLookupAsync(next, "succeeded", null);
        Assert.Equal(1, rig.Provider.Launches);
    }

    [Theory]
    [InlineData(false, "no_select_command")]
    [InlineData(true, "no_lifecycle_owner")]
    public async Task AdminSwitch_AcceptedNoLaunchRefusalHasExactReason(bool external, string reason)
    {
        await using var rig = await Fixture.StartAsync(external: external);
        using var response = await rig.PostAsync("absent");
        var id = await AckAsync(response, "absent", rig.Token);
        await rig.AwaitLookupAsync(id, "failed", reason);
        Assert.Equal(0, rig.Provider.Launches);
        Assert.Null(rig.Engine.SwitchingToward);
        Assert.DoesNotContain(rig.Router.Events.Replay(0), e => e.Kind == "engine.switch_started" && e.Model == "absent");
    }

    [Fact]
    public async Task AdminSwitch_JoinedLaunchFailureKeepsDistinctIds()
    {
        await using var rig = await Fixture.StartAsync();
        var entered = new ConcurrentDictionary<Guid, bool>();
        var wakes = 0;
        rig.Operations.ObserveWaiterForTests = (id, pending) => entered.TryAdd(id, pending);
        rig.Engine.AfterReadyWaitForTests = cancelled =>
        {
            if (!cancelled) Interlocked.Increment(ref wakes);
        };
        rig.Provider.FailB = true;
        using var first = await rig.PostAsync("b");
        var firstId = await AckAsync(first, "b", rig.Token);
        await rig.Provider.Entered.Task.WaitAsync(rig.Token);
        await ConfigReloadWireTests.WaitUntilAsync(() => entered.ContainsKey(firstId), rig.Token);
        using var second = await rig.PostAsync("b");
        var secondId = await AckAsync(second, "b", rig.Token);
        Assert.NotEqual(firstId, secondId);
        await ConfigReloadWireTests.WaitUntilAsync(() => entered.ContainsKey(secondId), rig.Token);
        Assert.True(entered[firstId]);
        Assert.True(entered[secondId]);
        await rig.AssertLookupAsync(firstId, "pending", null);
        await rig.AssertLookupAsync(secondId, "pending", null);
        rig.Provider.Release.TrySetResult();
        await rig.AwaitLookupAsync(firstId, "failed", "launch_failed");
        await rig.AwaitLookupAsync(secondId, "failed", "launch_failed");
        Assert.Equal(2, Volatile.Read(ref wakes));
        Assert.Equal(1, rig.Provider.Launches);
        Assert.Single(rig.Router.Events.Replay(0), e => e.Kind == "engine.switch_failed" && e.Model == "b");

        using var third = await rig.PostAsync("c");
        var thirdId = await AckAsync(third, "c", rig.Token);
        Assert.NotEqual(firstId, thirdId);
        Assert.NotEqual(secondId, thirdId);
        await rig.AwaitLookupAsync(thirdId, "succeeded", null);
        await rig.AssertLookupAsync(firstId, "failed", "launch_failed");
        await rig.AssertLookupAsync(secondId, "failed", "launch_failed");
        Assert.Equal(2, rig.Provider.Launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminSwitch_EarlyWorkerExceptionFinalizesLease(bool observer)
    {
        await using var rig = await Fixture.StartAsync();
        using var cut = new ConfigReloadRaceTests.Cut();
        var observed = Gate();
        if (observer)
            rig.Operations.ObserveWaiterForTests = (_, _) =>
            {
                observed.TrySetResult();
                throw new InvalidOperationException("private observer diagnostic");
            };
        else
            rig.Engine.BeforeSwitchWaitForTests = () =>
            {
                cut.Hold();
                throw new InvalidOperationException("private preclaim diagnostic");
            };

        try
        {
            using var reply = await rig.PostAsync("b");
            var id = await AckAsync(reply, "b", rig.Token);
            if (observer)
            {
                await observed.Task.WaitAsync(rig.Token);
                await rig.Provider.Entered.Task.WaitAsync(rig.Token);
            }
            else
            {
                await cut.Entered.Task.WaitAsync(rig.Token);
                Assert.Null(rig.Engine.SwitchingToward);
                Assert.Equal(0, rig.Provider.Launches);
            }
            await rig.AssertLookupAsync(id, "pending", null);
            await rig.AssertTopologyBusyAsync();
            cut.Release();
            rig.Provider.Release.TrySetResult();
            await rig.AwaitLookupAsync(id, "failed", "switch_error");
            await rig.AssertTopologyAppliesAsync();
        }
        finally { cut.Release(); }
    }

    [Fact]
    public async Task AdminSwitch_SynchronousScheduleRejectionReturnsRegisteredId()
    {
        await using var rig = await Fixture.StartAsync();
        using var cut = new ConfigReloadRaceTests.Cut();
        var waiters = 0;
        rig.Operations.ObserveWaiterForTests = (_, _) => Interlocked.Increment(ref waiters);
        rig.Operations.BeforeScheduleForTests = () =>
        {
            cut.Hold();
            throw new TaskSchedulerException("private submission diagnostic");
        };
        var sending = rig.PostAsync("b");
        try
        {
            await cut.Entered.Task.WaitAsync(rig.Token);
            Assert.Null(rig.Engine.SwitchingToward);
            await rig.AssertTopologyBusyAsync();
            cut.Release();
            using var reply = await sending.WaitAsync(rig.Token);
            var id = await AckAsync(reply, "b", rig.Token);
            await rig.AssertLookupAsync(id, "failed", "switch_error");
            Assert.Equal(0, Volatile.Read(ref waiters));
            Assert.Equal(0, rig.Provider.Launches);
            await rig.AssertTopologyAppliesAsync();
        }
        finally { cut.Release(); }
    }

    [Fact]
    public async Task AdminSwitch_CoreStopSettlesAcceptedIdAsCancelled()
    {
        await using var rig = await Fixture.StartAsync();
        using var reply = await rig.PostAsync("b");
        var id = await AckAsync(reply, "b", rig.Token);
        await rig.Provider.Entered.Task.WaitAsync(rig.Token);
        await rig.AssertLookupAsync(id, "pending", null);
        await rig.Router.DisposeAsync().AsTask().WaitAsync(rig.Token);
        await rig.Provider.Cancelled.Task.WaitAsync(rig.Token);
        await rig.AwaitLookupAsync(id, "cancelled", "cancelled");
        await rig.AssertLookupAsync(id, "cancelled", "cancelled");
    }

    [Fact]
    public async Task AdminSwitch_HttpCapacityDrainsSharedWaitersBeforeRefill()
    {
        await using var rig = await Fixture.StartAsync();
        var entered = new ConcurrentDictionary<Guid, bool>();
        rig.Operations.ObserveWaiterForTests = (id, pending) => entered.TryAdd(id, pending);
        var ids = new HashSet<Guid>();
        for (var index = 0; index < 32; index++)
        {
            using var response = await rig.PostAsync("b");
            var id = await AckAsync(response, "b", rig.Token);
            await rig.Provider.Entered.Task.WaitAsync(rig.Token);
            await ConfigReloadWireTests.WaitUntilAsync(() => entered.ContainsKey(id), rig.Token);
            Assert.True(entered[id]);
            Assert.True(ids.Add(id));
            await rig.AssertLookupAsync(id, "pending", null);
        }
        using var refused = await rig.PostAsync("b");
        await ErrorAsync(refused, HttpStatusCode.ServiceUnavailable, "too_many_switch_operations", rig.Token, Full);
        Assert.Equal(32, entered.Count);
        Assert.Equal(1, rig.Provider.Launches);

        rig.Provider.Release.TrySetResult();
        foreach (var id in ids)
            await rig.AwaitLookupAsync(id, "succeeded", null);
        using var refill = await rig.PostAsync("b");
        var next = await AckAsync(refill, "b", rig.Token);
        Assert.DoesNotContain(next, ids);
        await rig.AwaitLookupAsync(next, "succeeded", null);
        Assert.Equal(1, rig.Provider.Launches);
        await rig.AssertTopologyAppliesAsync();
    }

    [Fact]
    public void AdminSwitch_StoreProtectsBothHalvesAndPendingSlots()
    {
        var clock = new TestClock();
        var store = new AdminSwitchOperations(clock);
        var pending = Enumerable.Range(0, 32).Select(_ => Reserve(store)).ToArray();
        Assert.False(store.TryReserve(out _));
        var ready = new EngineRuntime.SwitchResult(EngineRuntime.SwitchOutcome.Ready, null);
        store.RecordResult(pending[0], ready);
        var spare = Reserve(store);
        Assert.False(store.TryReserve(out _));
        store.CloseAck(spare);
        foreach (var operation in pending.Skip(1))
            store.RecordResult(operation, ready);
        var thirtyThirdOpenAck = Reserve(store);
        store.RecordResult(thirtyThirdOpenAck, ready);

        clock.AdvanceTo(Window + TimeSpan.FromSeconds(1));
        Assert.Equal("succeeded", store.Lookup(pending[0].Id)?.Status);
        Assert.Equal("pending", store.Lookup(spare.Id)?.Status);
        store.CloseAck(pending[0]);
        store.RecordResult(spare, ready);
        var expiry = Window + Window + TimeSpan.FromSeconds(1);
        clock.AdvanceTo(expiry - TimeSpan.FromTicks(1));
        store.CloseAck(pending[0]);
        store.RecordResult(spare, EngineRuntime.SwitchResult.RouterStopped);
        Assert.Equal("succeeded", store.Lookup(pending[0].Id)?.Status);
        Assert.Equal("succeeded", store.Lookup(spare.Id)?.Status);
        clock.AdvanceTo(expiry);
        Assert.Null(store.Lookup(pending[0].Id));
        Assert.Null(store.Lookup(spare.Id));
        Assert.Equal("succeeded", store.Lookup(thirtyThirdOpenAck.Id)?.Status);
    }

    private static AdminSwitchOperations.Operation Reserve(AdminSwitchOperations store)
    {
        Assert.True(store.TryReserve(out var operation));
        return operation!;
    }

    [Fact]
    public async Task AdminSwitch_TotalCapacityRefusesUntilOneRecordExpires()
    {
        await using var rig = await Fixture.StartAsync();
        var submitted = 0;
        rig.Operations.BeforeScheduleForTests = () => Interlocked.Increment(ref submitted);
        var oldest = await rig.PostReadyAsync();
        rig.Clock.AdvanceTo(TimeSpan.FromSeconds(1));
        var newer = new HashSet<Guid>();
        for (var index = 0; index < 287; index++)
            Assert.True(newer.Add(await rig.PostReadyAsync()));

        rig.Clock.AdvanceTo(Window - TimeSpan.FromTicks(1));
        using var full = await rig.PostAsync("a");
        await ErrorAsync(full, HttpStatusCode.ServiceUnavailable, "too_many_switch_operations", rig.Token, Full);
        Assert.Equal(288, Volatile.Read(ref submitted));
        await rig.AssertLookupAsync(oldest, "succeeded", null);
        rig.Clock.AdvanceTo(Window);
        await rig.AssertMissingAsync(oldest.ToString("N"));

        var start = Gate();
        var first = CompeteAsync();
        var second = CompeteAsync();
        start.TrySetResult();
        using var one = await first;
        using var two = await second;
        var replies = new[] { one, two };
        var accepted = Assert.Single(replies, r => r.StatusCode == HttpStatusCode.Accepted);
        var refused = Assert.Single(replies, r => r.StatusCode == HttpStatusCode.ServiceUnavailable);
        await ErrorAsync(refused, HttpStatusCode.ServiceUnavailable, "too_many_switch_operations", rig.Token, Full);
        var id = await AckAsync(accepted, "a", rig.Token);
        Assert.NotEqual(oldest, id);
        Assert.DoesNotContain(id, newer);
        await rig.AwaitLookupAsync(id, "succeeded", null);
        Assert.All(newer, other => Assert.Equal("succeeded", rig.Operations.Lookup(other)?.Status));
        await rig.AssertMissingAsync(oldest.ToString("N"));
        Assert.Equal(289, Volatile.Read(ref submitted));
        Assert.Equal(0, rig.Provider.Launches);
        await rig.AssertTopologyAppliesAsync();

        async Task<HttpResponseMessage> CompeteAsync()
        {
            await start.Task.WaitAsync(rig.Token);
            return await rig.PostAsync("a");
        }
    }

    [Fact]
    public async Task AdminSwitch_BufferedAckSurvivesChurnAndStartsRetentionWhenClosed()
    {
        var gate = new AckGate(holdWrite: true);
        await using var rig = await Fixture.StartAsync(ackGate: gate);
        var initiating = rig.PostAsync("a", gated: true);
        try
        {
            var id = await gate.Id.Task.WaitAsync(rig.Token);
            await rig.AwaitLookupAsync(id, "succeeded", null);
            for (var index = 0; index < 256; index++)
                await rig.PostReadyAsync();
            rig.Clock.AdvanceTo(TimeSpan.FromSeconds(10));
            gate.Release.TrySetResult();
            using var reply = await initiating.WaitAsync(rig.Token);
            Assert.Equal(id, await AckAsync(reply, "a", rig.Token));
            await gate.Exited.Task.WaitAsync(rig.Token);
            await rig.AssertLookupAsync(id, "succeeded", null);

            rig.Clock.AdvanceTo(Window);
            await rig.AssertLookupAsync(id, "succeeded", null);
            rig.Clock.AdvanceTo(Window + TimeSpan.FromSeconds(10));
            await rig.AssertMissingAsync(id.ToString("N"));
            Assert.Equal(0, rig.Provider.Launches);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task AdminSwitch_AckAbandonmentKeepsWorkerAndLease()
    {
        var gate = new AckGate(holdWrite: true);
        await using var rig = await Fixture.StartAsync(ackGate: gate);
        using var preclaim = new ConfigReloadRaceTests.Cut();
        rig.Engine.BeforeSwitchWaitForTests = preclaim.Hold;
        using var peer = new TcpClient();
        try
        {
            await SendRawAsync(peer, rig.Port, "b", rig.Token);
            var id = await gate.Id.Task.WaitAsync(rig.Token);
            await preclaim.Entered.Task.WaitAsync(rig.Token);
            peer.LingerState = new LingerOption(true, 0);
            peer.Close();
            await gate.RequestAborted.Task.WaitAsync(rig.Token);
            await gate.Exited.Task.WaitAsync(rig.Token);
            Assert.False(gate.Forwarded);
            Assert.Null(rig.Engine.SwitchingToward);
            Assert.Equal(0, rig.Provider.Launches);
            await rig.AssertLookupAsync(id, "pending", null);
            await rig.AssertTopologyBusyAsync();
            preclaim.Release();
            await rig.Provider.Entered.Task.WaitAsync(rig.Token);
            await rig.AssertLookupAsync(id, "pending", null);
            rig.Provider.Release.TrySetResult();
            await rig.AwaitLookupAsync(id, "succeeded", null);
            await rig.AssertTopologyAppliesAsync();
        }
        finally
        {
            preclaim.Release();
            gate.Release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminSwitch_AckDeadlineWriteOrCompletionOrchestration(bool completion)
    {
        var deadline = TimeSpan.FromSeconds(30);
        var gate = new AckGate(holdWrite: true, holdCompletion: completion);
        using var transport = completion ? null : new AckTransport(holdOutput: false);
        await using var rig = await Fixture.StartAsync(ackGate: gate, transport: transport);
        using var peer = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await SendRawAsync(peer, transport?.Port ?? rig.Port, completion ? "b" : "a", rig.Token);
            var accepted = transport is null ? null : await transport.Accepted.Task.WaitAsync(rig.Token);
            if (accepted is not null) Assert.Equal(peer.Client.LocalEndPoint, accepted.RemoteEndPoint);
            var id = await gate.Id.Task.WaitAsync(rig.Token);
            var originalWrite = await gate.OriginalWrite.Task.WaitAsync(rig.Token);
            await ConfigReloadWireTests.WaitUntilAsync(() => rig.Clock.HasTimer, rig.Token);
            Assert.Equal(deadline, Assert.Single(rig.Clock.GetScheduledDeadlines()));
            if (completion)
            {
                await rig.Provider.Entered.Task.WaitAsync(rig.Token);
                await rig.AssertLookupAsync(id, "pending", null);
                Assert.Equal(0, rig.Clock.AdvanceTo(TimeSpan.FromSeconds(20)));
                gate.Release.TrySetResult();
                await gate.CompletionEntered.Task.WaitAsync(rig.Token);
            }
            else
            {
                await rig.AwaitLookupAsync(id, "succeeded", null);
            }
            Assert.Equal(0, rig.Clock.AdvanceTo(deadline - TimeSpan.FromTicks(1)));
            Assert.False(gate.Exited.Task.IsCompleted);
            if (accepted is not null)
            {
                Assert.False(originalWrite.IsCompleted);
                Assert.False(accepted.AbortedRequest.Task.IsCompleted);
                Assert.False(accepted.Closed.Task.IsCompleted);
            }

            Assert.Equal(1, rig.Clock.AdvanceTo(deadline));
            if (accepted is not null)
            {
                var abortedRequest = await accepted.AbortedRequest.Task.WaitAsync(rig.Token);
                await accepted.Closed.Task.WaitAsync(rig.Token);
                await AssertPeerClosedAsync(peer.GetStream(), rig.Token);
                var exit = await gate.WriteAtExit.Task.WaitAsync(rig.Token);
                Assert.Equal<Guid?>(id, exit.OperationId);
                Assert.Equal(accepted.ConnectionId, exit.ConnectionId);
                Assert.Equal(abortedRequest, exit.RequestId);
                Assert.Same(originalWrite, exit.Write);
                Assert.True(exit.WasCancelled);
            }
            else
            {
                await gate.RequestAborted.Task.WaitAsync(rig.Token);
            }
            await gate.Exited.Task.WaitAsync(rig.Token);
            Assert.Equal(completion, gate.Forwarded);
            if (completion)
            {
                rig.Clock.AdvanceTo(TimeSpan.FromSeconds(31));
                await rig.AssertLookupAsync(id, "pending", null);
                rig.Provider.Release.TrySetResult();
                await rig.AwaitLookupAsync(id, "succeeded", null);
            }
            var expiry = Window + TimeSpan.FromSeconds(completion ? 31 : 30);
            rig.Clock.AdvanceTo(expiry - TimeSpan.FromTicks(1));
            await rig.AssertLookupAsync(id, "succeeded", null);
            rig.Clock.AdvanceTo(expiry);
            await rig.AssertMissingAsync(id.ToString("N"));
        }
        finally
        {
            gate.Release.TrySetResult();
            gate.CompletionRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task AdminSwitch_AckCompletesJustBeforeDeadline()
    {
        var deadline = TimeSpan.FromSeconds(30);
        var gate = new AckGate(holdWrite: true);
        await using var rig = await Fixture.StartAsync(ackGate: gate);
        var sending = rig.PostAsync("a", gated: true);
        try
        {
            var id = await gate.Id.Task.WaitAsync(rig.Token);
            await ConfigReloadWireTests.WaitUntilAsync(() => rig.Clock.HasTimer, rig.Token);
            Assert.Equal(deadline, Assert.Single(rig.Clock.GetScheduledDeadlines()));
            Assert.Equal(0, rig.Clock.AdvanceTo(deadline - TimeSpan.FromTicks(1)));
            gate.Release.TrySetResult();
            using var reply = await sending.WaitAsync(rig.Token);
            Assert.Equal(id, await AckAsync(reply, "a", rig.Token));
            await gate.Exited.Task.WaitAsync(rig.Token);
            await ConfigReloadWireTests.WaitUntilAsync(() => !rig.Clock.HasTimer, rig.Token);
            Assert.Equal(0, rig.Clock.AdvanceTo(deadline));
            await rig.AwaitLookupAsync(id, "succeeded", null);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task AdminSwitch_AckWaitFaultClosesSendWithoutReplacingOutcome()
    {
        var gate = new AckGate(faultWrite: true);
        await using var rig = await Fixture.StartAsync(ackGate: gate);
        using var peer = new TcpClient();
        await SendRawAsync(peer, rig.Port, "a", rig.Token);
        var id = await gate.Id.Task.WaitAsync(rig.Token);
        await gate.Exited.Task.WaitAsync(rig.Token);
        await AssertPeerClosedAsync(peer.GetStream(), rig.Token);
        Assert.False(gate.Forwarded);
        await rig.AwaitLookupAsync(id, "succeeded", null);
        await rig.AssertTopologyAppliesAsync();
        rig.Clock.AdvanceTo(Window - TimeSpan.FromTicks(1));
        await rig.AssertLookupAsync(id, "succeeded", null);
        rig.Clock.AdvanceTo(Window);
        await rig.AssertMissingAsync(id.ToString("N"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminSwitch_AckDeadlineRetiresStartedKestrelCompletionBeforeClosing(bool remainParked)
    {
        var deadline = TimeSpan.FromSeconds(30);
        var gate = new AckGate();
        using var transport = new AckTransport();
        await using var rig = await Fixture.StartAsync(ackGate: gate, transport: transport);
        using var peer = new TcpClient();
        try
        {
            await SendRawAsync(peer, transport.Port, "a", rig.Token);
            await transport.FirstFlush.Task.WaitAsync(rig.Token);
            await ConfigReloadWireTests.WaitUntilAsync(() => rig.Clock.HasTimer, rig.Token);
            Assert.Equal(deadline, Assert.Single(rig.Clock.GetScheduledDeadlines()));
            Assert.Equal(0, rig.Clock.AdvanceTo(TimeSpan.FromSeconds(20)));
            transport.ForwardBody.TrySetResult();

            using var reader = new StreamReader(peer.GetStream(), leaveOpen: true);
            var firstLine = await reader.ReadLineAsync(rig.Token);
            Assert.StartsWith("HTTP/1.1 202 ", firstLine, StringComparison.Ordinal);
            var headers = new List<string>();
            while (await reader.ReadLineAsync(rig.Token) is { Length: > 0 } line)
                headers.Add(line);
            Assert.Contains(headers, line => line.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase));
            var length = Convert.ToInt32(await reader.ReadLineAsync(rig.Token), 16);
            var chunk = new char[length];
            Assert.Equal(length, await reader.ReadBlockAsync(chunk.AsMemory(), rig.Token));
            Assert.Equal("", await reader.ReadLineAsync(rig.Token));
            using var ack = JsonDocument.Parse(new string(chunk));
            var id = ReadAck(ack.RootElement, "a");
            Assert.Equal(id, await gate.Id.Task.WaitAsync(rig.Token));
            Assert.Equal("0\r\n\r\n", await transport.Suffix.Task.WaitAsync(rig.Token));
            var original = await gate.OriginalCompletion.Task.WaitAsync(rig.Token);
            await rig.AwaitLookupAsync(id, "succeeded", null);

            // No timer was dispatched at either advance, so a watchdog abort cannot be queued.
            Assert.Equal(0, rig.Clock.AdvanceTo(deadline - TimeSpan.FromTicks(1)));
            Assert.False(transport.AbortLogged.Task.IsCompleted);
            Assert.False(original.IsCompleted);
            Assert.False(transport.Closed.Task.IsCompleted);
            Assert.Equal(1, rig.Clock.AdvanceTo(deadline));
            await transport.AbortLogged.Task.WaitAsync(rig.Token);
            await transport.Closed.Task.WaitAsync(rig.Token);
            var continuation = await transport.Scheduler.Parked.Task.WaitAsync(rig.Token);
            await AssertNoSuffixAsync(reader, rig.Token);
            Assert.False(original.IsCompleted);
            Assert.False(gate.Exited.Task.IsCompleted);
            rig.Clock.AdvanceTo(TimeSpan.FromSeconds(31));
            if (remainParked)
            {
                rig.Clock.AdvanceTo(Window + TimeSpan.FromSeconds(31));
                Assert.False(original.IsCompleted);
                Assert.False(gate.Exited.Task.IsCompleted);
                await rig.AssertLookupAsync(id, "succeeded", null);
                Assert.False(original.IsCompleted);
                Assert.False(gate.Exited.Task.IsCompleted);
            }
            else
            {
                continuation.Dispatch();
                await ObserveCompletionAsync(original, rig.Token);
                Assert.True(original.IsCompleted);
                await gate.Exited.Task.WaitAsync(rig.Token);

                rig.Clock.AdvanceTo(Window + TimeSpan.FromSeconds(30));
                await rig.AssertLookupAsync(id, "succeeded", null);
                rig.Clock.AdvanceTo(Window + TimeSpan.FromSeconds(31) - TimeSpan.FromTicks(1));
                await rig.AssertLookupAsync(id, "succeeded", null);
                rig.Clock.AdvanceTo(Window + TimeSpan.FromSeconds(31));
                await rig.AssertMissingAsync(id.ToString("N"));
            }
        }
        finally
        {
            transport.ForwardBody.TrySetResult();
            transport.Scheduler.ReleaseForCleanup();
            peer.Close();
            if (gate.OriginalCompletion.Task.IsCompletedSuccessfully)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var original = await gate.OriginalCompletion.Task;
                await ObserveCompletionAsync(original, cleanup.Token);
                await gate.Exited.Task.WaitAsync(cleanup.Token);
            }
        }
    }

    private static async Task ObserveCompletionAsync(Task original, CancellationToken bound)
    {
        try { await original.WaitAsync(bound); }
        catch (Exception) when (!bound.IsCancellationRequested && (original.IsFaulted || original.IsCanceled))
        {
            // After abort the transport can finish with a fault or cancellation.
        }
        bound.ThrowIfCancellationRequested();
    }

    private static async Task AssertPeerClosedAsync(Stream stream, CancellationToken ct)
    {
        try { Assert.Equal(0, await stream.ReadAsync(new byte[1], ct)); }
        catch (IOException) { }
    }

    private static async Task AssertNoSuffixAsync(StreamReader reader, CancellationToken ct)
    {
        try { Assert.Null(await reader.ReadLineAsync(ct)); }
        catch (IOException) { }
    }

    private static async Task SendRawAsync(TcpClient peer, int port, string model, CancellationToken ct)
    {
        await peer.ConnectAsync(IPAddress.Loopback, port, ct);
        var body = $$"""{"engine":"native","model":"{{model}}"}""";
        var request = FormattableString.Invariant(
            $"POST /admin/switch HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nX-Test-Ack: 1\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request), ct);
    }

    private static async Task<Guid> AckAsync(HttpResponseMessage reply, string model, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        Assert.Equal("application/json", reply.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await reply.Content.ReadAsByteArrayAsync(ct));
        return ReadAck(document.RootElement, model);
    }

    private static Guid ReadAck(JsonElement root, string model)
    {
        Assert.Equal(["Engine", "Model", "OperationId", "Profile", "Switching"], Names(root));
        Assert.Equal("native", root.GetProperty("Engine").GetString());
        Assert.Equal(model, root.GetProperty("Model").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("Profile").ValueKind);
        Assert.True(root.GetProperty("Switching").GetBoolean());
        var text = root.GetProperty("OperationId").GetString();
        Assert.True(Guid.TryParseExact(text, "N", out var id));
        Assert.Equal(id.ToString("N"), text);
        return id;
    }

    private static string[] Names(JsonElement element) =>
        [.. element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    private static async Task ErrorAsync(HttpResponseMessage reply, HttpStatusCode status, string code,
        CancellationToken ct, string? message = null)
    {
        Assert.Equal(status, reply.StatusCode);
        Assert.Equal("application/json", reply.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await reply.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(["error"], Names(document.RootElement));
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(["code", "message", "type"], Names(error));
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal("air_error", error.GetProperty("type").GetString());
        var actual = error.GetProperty("message").GetString();
        Assert.NotNull(actual);
        if (message is not null) Assert.Equal(message, actual);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("air-admin-switch-");
        private readonly FakeEngineHandler _models = new() { LoadedModels = ["a"] };
        private readonly FakeEngineHandler _spectator = new() { LoadedModels = ["spectator"] };
        private readonly HttpClient _client = new();
        private readonly CancellationTokenSource _bound = new(TimeSpan.FromSeconds(45));
        private readonly ConcurrentDictionary<Guid, byte> _exits = new();
        private readonly AckGate? _ackGate;
        private WebApplication? _app;

        private Fixture(AckGate? ackGate)
        {
            _ackGate = ackGate;
            Provider = new LaunchGate(_models);
        }

        private const string Retained = """
            default_model = "a"
            max_request_body_bytes = 1024
            [engine.native]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"
            """;
        private const string Managed = """
            lifecycle = "docker"
            [engine.native.docker]
            image = "offline-image"
            router_id = "offline-owner"
            """;
        private const string Catalog = """
            [engine.native.health_probe]
            interval_ms = 3600000
            [engine.native.catalog.a]
            [engine.native.catalog.b]
            [engine.native.catalog.c]
            """;
        private const string Spectator = """
            [engine.spectator]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"
            lifecycle_owner = "external"
            [engine.spectator.catalog.spectator]
            """;

        internal CancellationToken Token => _bound.Token;
        internal int Port => _client.BaseAddress!.Port;
        internal RouterCore Router => _app!.Services.GetRequiredService<RouterCore>();
        internal EngineRuntime Engine => Router.Engines["native"];
        internal AdminSwitchOperations Operations => _app!.Services.GetRequiredService<AdminSwitchOperations>();
        internal LaunchGate Provider { get; }
        internal TestClock Clock { get; } = new();

        private string PathToConfig => Path.Combine(_directory.FullName, "router.toml");

        internal static async Task<Fixture> StartAsync(bool external = false, AckGate? ackGate = null,
            AckTransport? transport = null)
        {
            var fixture = new Fixture(ackGate);
            try
            {
                await fixture.StartHostAsync(external, transport);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private async Task StartHostAsync(bool external, AckTransport? transport)
        {
            await File.WriteAllTextAsync(PathToConfig, (external
                ? Retained + "\nlifecycle_owner = \"external\"\n"
                : Retained + "\n" + Managed + "\n") + Catalog + "\n" + Spectator, Token);

            string[] args = ["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Microsoft.AspNetCore.Server.Kestrel.Connections=Trace"];
            var app = _app = ServerApp.Build(args, PathToConfig, null, TimeProvider.System,
                lifecycleFactory: external ? null : _ => Provider,
                httpHandlerFactoryForTests: definition => definition.Name == "native" ? _models : _spectator,
                reloadTokenForTests: "fixture", adminSwitchClockForTests: Clock);

            app.Use(async (HttpContext ctx, RequestDelegate next) =>
            {
                if (!HttpMethods.IsPost(ctx.Request.Method) || ctx.Request.Path != "/admin/switch")
                {
                    await next(ctx);
                    return;
                }

                var gate = ctx.Request.Headers.ContainsKey("X-Test-Ack") ? _ackGate : null;
                var original = ctx.Features.Get<IHttpResponseBodyFeature>()!;
                using var feature = new ObservedAckFeature(original, ctx, gate);
                ctx.Features.Set<IHttpResponseBodyFeature>(feature);
                try { await next(ctx); }
                finally
                {
                    if (gate is { HoldWrite: true, HoldCompletion: false })
                    {
                        var write = feature.SavedWrite;
                        gate.WriteAtExit.TrySetResult(new(feature.Id, ctx.Connection.Id, ctx.TraceIdentifier,
                            write, write?.IsCanceled == true));
                    }
                    ctx.Features.Set(original);
                    if (feature.Id is { } id) _exits.TryAdd(id, 0);
                    gate?.Exited.TrySetResult();
                }
            });

            ListenOptions? plain = null;
            if (transport is not null)
            {
                var options = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
                options.Limits.MinResponseDataRate = null;
                options.Listen(IPAddress.Loopback, 0, listen => plain = listen);
                options.Listen(IPAddress.Loopback, 0, listen =>
                {
                    transport.Listener = listen;
                    listen.Protocols = HttpProtocols.Http1;
                    _ = listen.Use(transport.Run);
                });
                app.Services.GetRequiredService<ILoggerFactory>().AddProvider(transport);
            }

            await app.StartAsync(Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            _client.BaseAddress = plain is null
                ? new Uri(addresses.Addresses.Single())
                : new Uri($"http://127.0.0.1:{plain.IPEndPoint!.Port}");
            await ConfigReloadWireTests.WaitUntilAsync(() => Router.Engines.Values.All(e => e.Healthy), Token);
        }

        internal async Task<HttpResponseMessage> PostAsync(string model, string? profile = null, bool gated = false)
        {
            var body = profile is null
                ? $$"""{"engine":"native","model":"{{model}}"}"""
                : $$"""{"engine":"native","model":"{{model}}","profile":"{{profile}}"}""";
            using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/switch");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (gated) request.Headers.Add("X-Test-Ack", "1");
            return await _client.SendAsync(request, Token);
        }

        internal async Task<Guid> PostReadyAsync()
        {
            using var reply = await PostAsync("a");
            var id = await AckAsync(reply, "a", Token);
            await ConfigReloadWireTests.WaitUntilAsync(() => _exits.ContainsKey(id), Token);
            await AwaitLookupAsync(id, "succeeded", null);
            return id;
        }

        private async Task<string> ReadLookupAsync(Guid id, string? reason, string? expectedStatus)
        {
            using var reply = await _client.GetAsync($"/admin/switch/{id:N}", Token);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            Assert.Equal("application/json", reply.Content.Headers.ContentType?.MediaType);
            Assert.True(reply.Headers.CacheControl?.NoStore == true);
            using var document = JsonDocument.Parse(await reply.Content.ReadAsByteArrayAsync(Token));
            var root = document.RootElement;
            Assert.Equal(["OperationId", "Reason", "Status"], Names(root));
            Assert.Equal(id.ToString("N"), root.GetProperty("OperationId").GetString());
            var status = root.GetProperty("Status").GetString()!;
            if (expectedStatus is not null) Assert.Equal(expectedStatus, status);
            var actual = root.GetProperty("Reason");
            if (status == "pending" || reason is null) Assert.Equal(JsonValueKind.Null, actual.ValueKind);
            else Assert.Equal(reason, actual.GetString());
            return status;
        }

        internal async Task AssertLookupAsync(Guid id, string status, string? reason) =>
            _ = await ReadLookupAsync(id, reason, status);

        internal async Task AwaitLookupAsync(Guid id, string status, string? reason)
        {
            string observed;
            while ((observed = await ReadLookupAsync(id, reason, expectedStatus: null)) == "pending")
                await Task.Delay(1, Token);
            Assert.Equal(status, observed);
        }

        internal async Task AssertMissingAsync(string id)
        {
            using var reply = await _client.GetAsync($"/admin/switch/{id}", Token);
            Assert.True(reply.Headers.CacheControl?.NoStore == true);
            await ErrorAsync(reply, HttpStatusCode.NotFound, "not_found", Token, "Switch operation not found.");
        }

        internal async Task AssertTopologyBusyAsync()
        {
            var generation = Router.CurrentGeneration.Number;
            var retained = Engine;
            Assert.Equal((ConfigReloadStatus.Busy, generation), await ApplyWithoutSpectatorAsync(generation));
            Assert.Same(retained, Engine);
            Assert.Contains("spectator", Router.Engines.Keys);
        }

        internal async Task AssertTopologyAppliesAsync()
        {
            var generation = Router.CurrentGeneration.Number;
            var retained = Engine;
            Assert.Equal((ConfigReloadStatus.Applied, generation + 1), await ApplyWithoutSpectatorAsync(generation));
            Assert.Same(retained, Engine);
            Assert.DoesNotContain("spectator", Router.Engines.Keys);
        }

        private Task<(ConfigReloadStatus Status, long Generation)> ApplyWithoutSpectatorAsync(long generation)
        {
            var candidate = RouterConfigLoader.LoadForReload(Retained + "\n" + Managed + "\n" + Catalog, PathToConfig);
            return Router.ApplyConfigurationAsync(candidate, generation, Token);
        }

        public async ValueTask DisposeAsync()
        {
            Provider.Release.TrySetResult();
            _ackGate?.Release.TrySetResult();
            _ackGate?.CompletionRelease.TrySetResult();
            _client.Dispose();
            _bound.Dispose();
            try
            {
                if (_app is { } app)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await app.StopAsync(cleanup.Token); }
                    finally { await app.DisposeAsync().AsTask().WaitAsync(cleanup.Token); }
                }
            }
            finally
            {
                _models.Dispose();
                _spectator.Dispose();
                _directory.Delete(recursive: true);
            }
        }
    }

    private sealed class LaunchGate(FakeEngineHandler models) : IEngineLifecycleProvider
    {
        private int _launches;

        public string Kind => "docker";
        internal int Launches => Volatile.Read(ref _launches);
        internal bool FailB { get; set; }
        internal TaskCompletionSource Entered { get; } = Gate();
        internal TaskCompletionSource Release { get; } = Gate();
        internal TaskCompletionSource Cancelled { get; } = Gate();

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult(LifecycleResult.Ok());

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct) =>
            ValueTask.FromResult(LifecycleResult.Ok());

        public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            Interlocked.Increment(ref _launches);
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(ct); }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
            if (target.Model == "b" && FailB)
                return LifecycleResult.Fail("launch refused", "private provider diagnostic");
            models.LoadedModels = [target.Model];
            return LifecycleResult.Ok();
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.FromSeconds(5));
    }

    private sealed record HeldWriteExit(Guid? OperationId, string ConnectionId, string RequestId, Task? Write,
        bool WasCancelled);

    private sealed class AckGate(bool holdWrite = false, bool holdCompletion = false, bool faultWrite = false)
    {
        internal bool HoldWrite { get; } = holdWrite;
        internal bool HoldCompletion { get; } = holdCompletion;
        internal bool FaultWrite { get; } = faultWrite;
        internal bool Forwarded { get; set; }
        internal TaskCompletionSource<Guid> Id { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = Gate();
        internal TaskCompletionSource RequestAborted { get; } = Gate();
        internal TaskCompletionSource CompletionRelease { get; } = Gate();
        internal TaskCompletionSource CompletionEntered { get; } = Gate();
        internal TaskCompletionSource<Task> OriginalWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Task> OriginalCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<HeldWriteExit> WriteAtExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Exited { get; } = Gate();

        internal async Task WaitForReleaseAsync(Task release, CancellationToken abort)
        {
            try { await release.WaitAsync(abort); }
            catch (OperationCanceledException) when (abort.IsCancellationRequested)
            {
                RequestAborted.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ObservedAckFeature : IHttpResponseBodyFeature, IDisposable
    {
        private readonly IHttpResponseBodyFeature _original;
        private readonly HttpContext _context;
        private readonly AckGate? _gate;
        private readonly AckStream _stream;

        internal ObservedAckFeature(IHttpResponseBodyFeature original, HttpContext context, AckGate? gate)
        {
            _original = original;
            _context = context;
            _gate = gate;
            _stream = new AckStream(this);
        }

        internal Guid? Id { get; private set; }
        internal Task? SavedWrite { get; private set; }
        public Stream Stream => _stream;
        public PipeWriter Writer => _original.Writer;
        public void DisableBuffering() => _original.DisableBuffering();
        public Task StartAsync(CancellationToken cancellationToken = default) => _original.StartAsync(cancellationToken);
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            _original.SendFileAsync(path, offset, count, cancellationToken);

        public Task CompleteAsync()
        {
            if (_gate is { HoldCompletion: true } gate) return CompleteAfterGateAsync(gate);
            var original = _original.CompleteAsync();
            _gate?.OriginalCompletion.TrySetResult(original);
            return original;
        }

        private async Task CompleteAfterGateAsync(AckGate gate)
        {
            gate.CompletionEntered.TrySetResult();
            await gate.WaitForReleaseAsync(gate.CompletionRelease.Task, _context.RequestAborted);
            await _original.CompleteAsync();
        }

        private async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            if (_context.Response.StatusCode == StatusCodes.Status202Accepted)
            {
                using var doc = JsonDocument.Parse(bytes);
                Id = Guid.ParseExact(doc.RootElement.GetProperty("OperationId").GetString()!, "N");
                if (_gate is { } gate)
                {
                    gate.Id.TrySetResult(Id.Value);
                    if (gate.FaultWrite) throw new IOException("fixture ACK write fault");
                    if (gate.HoldWrite)
                        await gate.WaitForReleaseAsync(gate.Release.Task, token);
                    gate.Forwarded = true;
                }
            }
            await _original.Stream.WriteAsync(bytes, token);
        }

        public void Dispose() => _stream.Dispose();

        private sealed class AckStream(ObservedAckFeature owner) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var write = owner.WriteAsync(buffer, cancellationToken);
                if (owner._gate is not { HoldWrite: true } gate) return write;
                var original = write.AsTask();
                owner.SavedWrite = original;
                gate.OriginalWrite.TrySetResult(original);
                return new ValueTask(original);
            }
        }
    }

    private sealed class AckTransport(bool holdOutput = true) : ILoggerProvider, ILogger
    {
        private PassiveConnection? _passiveConnection;

        internal ListenOptions? Listener { get; set; }
        internal int Port => Listener!.IPEndPoint!.Port;
        internal TaskCompletionSource FirstFlush { get; } = Gate();
        internal TaskCompletionSource ForwardBody { get; } = Gate();
        internal TaskCompletionSource<string> Suffix { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = Gate();
        internal TaskCompletionSource AbortLogged { get; } = Gate();
        internal FlushScheduler Scheduler { get; } = new();
        internal TaskCompletionSource<PassiveConnection> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ConnectionDelegate Run(ConnectionDelegate next) => async connection =>
        {
            if (!holdOutput)
            {
                var accepted = new PassiveConnection(connection.ConnectionId, connection.RemoteEndPoint);
                using var passiveClosed = connection.ConnectionClosed.Register(() => accepted.Closed.TrySetResult());
                if (Interlocked.CompareExchange(ref _passiveConnection, accepted, null) is null)
                    Accepted.TrySetResult(accepted);
                await next(connection);
                return;
            }

            using var closed = connection.ConnectionClosed.Register(() => Closed.TrySetResult());
            var original = connection.Transport;
            var pipe = new Pipe(new PipeOptions(writerScheduler: Scheduler, pauseWriterThreshold: 1,
                resumeWriterThreshold: 1, useSynchronizationContext: false));
            connection.Transport = new OutputBridge(original.Input, pipe.Writer);
            var forwarding = ForwardAsync(pipe.Reader, original.Output, connection.ConnectionClosed);
            try { await next(connection); }
            finally
            {
                await pipe.Writer.CompleteAsync();
                await forwarding;
                connection.Transport = original;
            }
        };

        private async Task ForwardAsync(PipeReader reader, PipeWriter socket, CancellationToken closed)
        {
            try
            {
                var body = await reader.ReadAsync(closed);
                FirstFlush.TrySetResult();
                await ForwardBody.Task.WaitAsync(closed);
                foreach (var segment in body.Buffer)
                    await socket.WriteAsync(segment, closed);
                reader.AdvanceTo(body.Buffer.End);

                var suffix = await reader.ReadAsync(closed);
                Scheduler.ParkNext();
                Suffix.TrySetResult(Encoding.ASCII.GetString(suffix.Buffer.ToArray()));
                // Leave the suffix unread by the peer. Only the original socket's close ends this reader.
                await Closed.Task;
            }
            catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
            finally
            {
                await reader.CompleteAsync();
            }
        }

        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name != "ApplicationAbortedConnection") return;
            if (holdOutput)
            {
                AbortLogged.TrySetResult();
                return;
            }

            var observed = Volatile.Read(ref _passiveConnection);
            if (observed is null || state is not IReadOnlyList<KeyValuePair<string, object?>> fields) return;

            string? connectionId = null;
            string? requestId = null;
            foreach (var (key, value) in fields)
            {
                if (key == "ConnectionId") connectionId = value as string;
                if (key == "TraceIdentifier") requestId = value as string;
            }
            if (connectionId == observed.ConnectionId && requestId is not null)
                observed.AbortedRequest.TrySetResult(requestId);
        }

        public void Dispose() { }

        internal sealed record PassiveConnection(string ConnectionId, EndPoint? RemoteEndPoint)
        {
            internal TaskCompletionSource Closed { get; } = Gate();
            internal TaskCompletionSource<string> AbortedRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed record OutputBridge(PipeReader Input, PipeWriter Output) : IDuplexPipe;
    }

    private sealed class FlushScheduler : PipeScheduler
    {
        private readonly object _gate = new();
        private bool _parkNext;
        private bool _cleaning;
        internal TaskCompletionSource<Continuation> Parked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ParkNext()
        {
            lock (_gate)
            {
                if (!_cleaning) _parkNext = true;
            }
        }

        internal void ReleaseForCleanup()
        {
            lock (_gate)
            {
                _cleaning = true;
                _parkNext = false;
            }
            if (Parked.Task.IsCompletedSuccessfully) Parked.Task.Result.Dispatch();
        }

        public override void Schedule(Action<object?> action, object? state)
        {
            lock (_gate)
            {
                if (_parkNext)
                {
                    _parkNext = false;
                    Parked.TrySetResult(new Continuation(action, state));
                    return;
                }
            }
            PipeScheduler.ThreadPool.Schedule(action, state);
        }

        internal sealed class Continuation(Action<object?> action, object? state)
        {
            private int _dispatched;

            internal void Dispatch()
            {
                if (Interlocked.Exchange(ref _dispatched, 1) == 0)
                    PipeScheduler.ThreadPool.Schedule(action, state);
            }
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ClockTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        internal bool HasTimer
        {
            get { lock (_gate) return _timers.Count > 0; }
        }

        internal TimeSpan[] GetScheduledDeadlines()
        {
            lock (_gate) return _timers.Select(timer => TimeSpan.FromTicks(timer.Deadline)).ToArray();
        }

        internal int AdvanceTo(TimeSpan target)
        {
            ClockTimer[] due;
            lock (_gate)
            {
                Assert.True(target.Ticks >= _timestamp);
                Interlocked.Exchange(ref _timestamp, target.Ticks);
                due = _timers.Where(timer => timer.Deadline <= _timestamp).ToArray();
            }
            var fired = 0;
            foreach (var timer in due)
            {
                if (timer.TryFire()) fired++;
            }
            return fired;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            lock (_gate)
            {
                var timer = new ClockTimer(this, _timestamp + dueTime.Ticks, callback, state);
                _timers.Add(timer);
                return timer;
            }
        }

        private void Remove(ClockTimer timer)
        {
            lock (_gate) _timers.Remove(timer);
        }

        private sealed class ClockTimer(TestClock clock, long deadline, TimerCallback callback, object? state) : ITimer
        {
            private int _retired;
            internal long Deadline => deadline;

            internal bool TryFire()
            {
                if (Interlocked.Exchange(ref _retired, 1) != 0) return false;
                clock.Remove(this);
                callback(state);
                return true;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _retired, 1) == 0) clock.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
