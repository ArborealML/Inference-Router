using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;

namespace Arboreal.InferenceRouter.Tests;

public sealed class ExactTicketAdmissionTests
{
    [Fact(Timeout = 20_000)]
    public async Task DispatchParkCanObserveShutdownAfterTheRouterDisposesItsCancellationSource()
    {
        using var parked = new AdmissionFixtureBarrier();
        await using var rig = new Rig();
        rig.Router.AfterDispatchParkForTests = _ => rig.Hold(parked);
        rig.StartDispatcher();
        try
        {
            await parked.Reached.WaitAsync(rig.Token);
            // Enter the final wait only after RouterCore has cancelled and disposed its source.
            await rig.StopRouterAsync().WaitAsync(rig.Token);
        }
        finally
        {
            parked.Release();
        }
        // Rig disposal joins the real loop and propagates any unexpected dispatcher fault.
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseBeforeExactTakePreservesTheTicketAcrossTheParkBoundary(bool afterArm)
    {
        await using var rig = new Rig();
        await rig.ProbeAsync();
        var ticket = await rig.EnqueueAsync("a", "original");
        await rig.PrepareAsync(ticket);

        using var commit = new AdmissionFixtureBarrier();
        rig.Router.BeforeAdmissionCommitForTests = _ => rig.Hold(commit);
        var dispatch = Task.Run(rig.Router.TryDispatchOnce);
        try
        {
            await commit.Reached.WaitAsync(rig.Token);
            rig.Publish("a", generation: 1, open: false);
        }
        finally
        {
            rig.Router.BeforeAdmissionCommitForTests = null;
            commit.Release();
        }
        Assert.False(await dispatch.WaitAsync(rig.Token));
        Assert.Equal(TicketState.Queued, ticket.State);
        Assert.Empty(rig.Selected);
        Assert.Equal(0, rig.Admission.PhysicalUses);
        Assert.Equal(0, rig.Sink.Calls);
        Assert.Empty(rig.Sink.ReceivedBodies);

        using var arm = new AdmissionFixtureBarrier();
        rig.Router.BeforeDispatchParkForTests = armed =>
        {
            if (armed == afterArm) rig.Hold(arm);
        };
        rig.StartDispatcher();
        try
        {
            await arm.Reached.WaitAsync(rig.Token);
            rig.Publish("a", generation: 1, open: true);
        }
        finally
        {
            rig.Router.BeforeDispatchParkForTests = null;
            arm.Release();
        }
        await ticket.CompletionTask.WaitAsync(rig.Token);
        Assert.Equal(ticket.Id, Assert.Single(rig.Selected).TicketId);
        Assert.Equal(1, rig.Sink.Calls);
        Assert.Equal(Payload("a", "original"), Assert.Single(rig.Sink.ReceivedBodies));
        Assert.Equal(1, rig.Attempts[ticket.Id]);
    }

    [Fact(Timeout = 20_000)]
    public async Task CompletionAndProfileWakesLeaveTheExactTicketQueuedUntilMatchingOpen()
    {
        using var bootProbe = new AdmissionFixtureBarrier();
        using var response = new AdmissionFixtureBarrier();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = lifetime.Token;
        await using var rig = new Rig(respond: RespondAsync, singleModel: true);
        await rig.ProbeAsync();
        var ticket = await rig.EnqueueAsync("a", "completion-open");
        await rig.PrepareAsync(ticket);
        var originalClaim = rig.Claims[ticket.Id];
        rig.Publish("a", originalClaim.Generation, open: false);

        var parks = Channel.CreateUnbounded<Task>();
        var admitted = new ConcurrentQueue<ExactTicketAdmission.Claim>();
        var closedScans = 0;
        var submissions = 0;
        var engine = rig.Router.Engines["fixture"];
        engine.BeforeModelsFetchForTests = () => bootProbe.HoldAsync(token).GetAwaiter().GetResult();
        rig.Router.AfterDispatchParkForTests = wake => parks.Writer.TryWrite(wake);
        rig.Router.BeforeAdmissionPreparationForTests = current =>
        {
            if (ReferenceEquals(current, ticket)) Interlocked.Increment(ref closedScans);
        };
        rig.Admission.BeforeSubmitForTests = claim =>
        {
            admitted.Enqueue(claim);
            return Task.CompletedTask;
        };
        rig.Admission.BeforeSendForTests = _ =>
        {
            Interlocked.Increment(ref submissions);
            return Task.CompletedTask;
        };
        var released = rig.ArmRelease();

        try
        {
            rig.Router.Start();
            // Setup wakes have already fired. The automatic health probe stays held until teardown.
            await bootProbe.Reached.WaitAsync(token);
            var initialPark = await ReadClosedParkAsync(expectedScans: 1);

            rig.Router.Events.Append(new RouterEvent { Kind = "engine.switch_completed", Engine = "fixture", Model = "a" });
            await initialPark.WaitAsync(token);
            var afterCompletion = await ReadClosedParkAsync(expectedScans: 2);
            Assert.NotSame(initialPark, afterCompletion);

            rig.Router.Events.Append(new RouterEvent { Kind = "engine.profile", Engine = "fixture", Model = "a" });
            await afterCompletion.WaitAsync(token);
            var afterProfile = await ReadClosedParkAsync(expectedScans: 3);
            Assert.NotSame(initialPark, afterProfile);
            Assert.NotSame(afterCompletion, afterProfile);

            rig.Publish("a", originalClaim.Generation, open: true);
            await afterProfile.WaitAsync(token);
            await response.Reached.WaitAsync(token);
            Assert.Equal(originalClaim, Assert.Single(admitted));
            Assert.Equal(1, Volatile.Read(ref submissions));
            Assert.Equal(ticket.Id, Assert.Single(rig.Selected).TicketId);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Admission.Releases);
            Assert.Equal(1, rig.Sink.Calls);
            Assert.Equal(Payload("a", "completion-open"), Assert.Single(rig.Sink.ReceivedBodies));
            Assert.False(ticket.CompletionTask.IsCompleted);

            response.Release();
            Assert.Equal("{}"u8.ToArray(), await ticket.CompletionTask.WaitAsync(token));
            await released.WaitAsync(token);
            Assert.Equal("request.completed", Assert.Single(TerminalEvents(rig, ticket)).Kind);
            Assert.Equal(1, rig.Admission.Releases);
            Assert.Equal(0, rig.Admission.PhysicalUses);
            Assert.Equal(1, rig.Attempts[ticket.Id]);
            Assert.Single(admitted);
            Assert.Equal(1, Volatile.Read(ref submissions));
            Assert.Single(rig.Selected);
            Assert.Equal(1, rig.Sink.Calls);
            Assert.Single(rig.Sink.ReceivedBodies);
        }
        finally
        {
            engine.BeforeModelsFetchForTests = null;
            rig.Router.AfterDispatchParkForTests = null;
            rig.Router.BeforeAdmissionPreparationForTests = null;
            rig.Admission.BeforeSubmitForTests = null;
            rig.Admission.BeforeSendForTests = null;
            rig.Admission.AfterReleaseForTests = null;
            response.Release();
            bootProbe.Release();
        }

        async Task<Task> ReadClosedParkAsync(int expectedScans)
        {
            var park = await parks.Reader.ReadAsync(token);
            Assert.False(park.IsCompleted);
            Assert.Equal(expectedScans, Volatile.Read(ref closedScans));
            Assert.Equal(TicketState.Queued, ticket.State);
            Assert.Equal(originalClaim, rig.Claims[ticket.Id]);
            lock (rig.Admission.Gate)
            {
                Assert.True(rig.Admission.IsPrepared(originalClaim));
                Assert.False(rig.Admission.IsOpen(originalClaim));
            }
            Assert.Equal(1, rig.Admission.PreparationCount);
            Assert.Equal(1, rig.Attempts[ticket.Id]);
            Assert.Empty(rig.Selected);
            Assert.Empty(admitted);
            Assert.Equal(0, Volatile.Read(ref submissions));
            Assert.Equal(0, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Admission.Releases);
            Assert.Equal(0, rig.Sink.Calls);
            Assert.Empty(rig.Sink.ReceivedBodies);
            return park;
        }

        async Task<HttpResponseMessage> RespondAsync()
        {
            await response.HoldAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationAndSubmissionHaveOneWinner(bool submissionClaimed, bool senderEntered)
    {
        using var beforeByte = new AdmissionFixtureBarrier();
        using var executionClaimedBeforeSend = new AdmissionFixtureBarrier();
        using var senderBeforeResponse = new AdmissionFixtureBarrier();
        using var cleanup = new AdmissionFixtureBarrier();
        using var cleanupLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var claimed = new TaskCompletionSource<ExactTicketAdmission.Invocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resourceDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(prepare: (_, _) => ValueTask.FromResult<IAsyncDisposable?>(
            new HeldResource(cleanup, cleanupLifetime.Token, () => resourceDisposed.TrySetResult())), respond: RespondAsync);
        await rig.ProbeAsync();
        var ticket = await rig.EnqueueAsync("a", "one-writer");
        await rig.PrepareAsync(ticket);
        rig.Admission.BeforeSubmitForTests = _ => beforeByte.HoldAsync(rig.Token);
        rig.Admission.BeforeSendForTests = invocation =>
        {
            claimed.TrySetResult(invocation);
            return executionClaimedBeforeSend.HoldAsync(rig.Token);
        };

        Assert.True(rig.Router.TryDispatchOnce());
        var released = rig.ArmRelease();
        try
        {
            await beforeByte.Reached.WaitAsync(rig.Token);
            rig.Publish("a", generation: 1, open: false);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            if (submissionClaimed)
            {
                beforeByte.Release();
                var invoking = await claimed.Task.WaitAsync(rig.Token);
                await invoking.AbortAsync();
                Assert.Equal(1, rig.Admission.PhysicalUses);
            }
            if (senderEntered)
            {
                executionClaimedBeforeSend.Release();
                await senderBeforeResponse.Reached.WaitAsync(rig.Token);
                Assert.Equal(Payload("a", "one-writer"), Assert.Single(rig.Sink.ReceivedBodies));
            }

            Assert.True(rig.Router.TryCancel(ticket.Id));
            Assert.Equal(submissionClaimed ? 1 : 0, rig.Admission.PhysicalUses);
            Assert.Equal(submissionClaimed ? 0 : 1, rig.Admission.Releases);
            Assert.Equal(senderEntered ? 1 : 0, rig.Sink.Calls);
            Assert.Equal(senderEntered ? 1 : 0, rig.Sink.ReceivedBodies.Length);
            Assert.False(responseDisposed.Task.IsCompleted);
            beforeByte.Release();
            executionClaimedBeforeSend.Release();

            if (submissionClaimed)
            {
                await senderBeforeResponse.Reached.WaitAsync(rig.Token);
                Assert.Equal(1, rig.Admission.PhysicalUses);
                Assert.False(responseDisposed.Task.IsCompleted);
                Assert.Equal(Payload("a", "one-writer"), Assert.Single(rig.Sink.ReceivedBodies));
            }
            senderBeforeResponse.Release();

            var error = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(rig.Token));
            Assert.Equal("cancelled", error.Code);
            await cleanup.Reached.WaitAsync(rig.Token);
            Assert.Equal(submissionClaimed, responseDisposed.Task.IsCompletedSuccessfully);
            Assert.Equal(submissionClaimed ? 1 : 0, rig.Admission.PhysicalUses);
            Assert.Equal(submissionClaimed ? 0 : 1, rig.Admission.Releases);
        }
        finally
        {
            beforeByte.Release();
            executionClaimedBeforeSend.Release();
            senderBeforeResponse.Release();
            cleanup.Release();
        }

        await resourceDisposed.Task.WaitAsync(rig.Token);
        await released.WaitAsync(rig.Token);
        Assert.Equal(0, rig.Admission.PhysicalUses);
        Assert.Equal(1, rig.Admission.Releases);
        Assert.Equal("request.cancelled", Assert.Single(TerminalEvents(rig, ticket)).Kind);
        Assert.Equal(submissionClaimed ? 1 : 0, rig.Sink.Calls);
        Assert.Equal(submissionClaimed ? 1 : 0, rig.Sink.ReceivedBodies.Length);

        async Task<HttpResponseMessage> RespondAsync()
        {
            await senderBeforeResponse.HoldAsync(cleanupLifetime.Token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ObservedResponse(() => responseDisposed.TrySetResult()),
            };
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortBeforeRunSharesCleanupWithRepeatedAbortAndCancelsLateExecution(bool cleanupThrows)
    {
        using var cleanup = new AdmissionFixtureBarrier();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var disposals = 0;
        var disposeError = new InvalidOperationException("The aborted fixture could not confirm prepared-resource disposal.");
        await using var rig = new Rig(prepare: (_, _) => ValueTask.FromResult<IAsyncDisposable?>(
            new HeldResource(cleanup, lifetime.Token, DisposePreparation)), singleModel: true);
        await rig.ProbeAsync();
        rig.Publish("a", generation: 2, open: false);
        var owner = await rig.StageOwnerAsync();
        rig.Publish("a", generation: 2, open: true);
        var invocation = Assert.IsType<ExactTicketAdmission.Invocation>(
            rig.AdmitStagedOwner(owner));
        Assert.Equal(1, rig.Admission.PhysicalUses);
        var firstAbort = invocation.AbortAsync().AsTask();
        try
        {
            await cleanup.Reached.WaitAsync(rig.Token);
            var repeatedAbort = invocation.AbortAsync().AsTask();
            var lateRun = AttemptRunAsync();
            // No submission can follow the abort winner, so the physical count can already be released.
            Assert.Equal(0, rig.Admission.PhysicalUses);
            Assert.Equal(1, rig.Admission.Releases);
            Assert.False(firstAbort.IsCompleted);
            Assert.False(repeatedAbort.IsCompleted);
            Assert.False(lateRun.IsCompleted);
            Assert.Equal(0, rig.Sink.Calls);
            Assert.Empty(rig.Sink.ReceivedBodies);

            cleanup.Release();
            if (cleanupThrows)
            {
                var firstError = await Assert.ThrowsAsync<InvalidOperationException>(() => firstAbort.WaitAsync(rig.Token));
                var repeatedError = await Assert.ThrowsAsync<InvalidOperationException>(() => repeatedAbort.WaitAsync(rig.Token));
                Assert.Same(disposeError, firstError.InnerException);
                Assert.Same(firstError, repeatedError);
            }
            else
            {
                await Task.WhenAll(firstAbort, repeatedAbort).WaitAsync(rig.Token);
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lateRun.WaitAsync(rig.Token));
            Assert.Equal(1, Volatile.Read(ref disposals));
            Assert.Equal(0, rig.Admission.PhysicalUses);
            Assert.Equal(1, rig.Admission.Releases);
            Assert.Equal(0, rig.Sink.Calls);
        }
        finally
        {
            cleanup.Release();
        }

        void DisposePreparation()
        {
            Interlocked.Increment(ref disposals);
            if (cleanupThrows) throw disposeError;
        }

        async Task AttemptRunAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://fixture.invalid/v1/completions");
            await invocation.RunAsync(request, _ => Task.CompletedTask, reportFailure: null, rig.Token);
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task SubmittedUseSurvivesHeadersAndBodyUntilResponseAndPreparationCleanup()
    {
        using var body = new AdmissionFixtureBarrier();
        using var cleanup = new AdmissionFixtureBarrier();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var responseDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(
            prepare: (_, _) => ValueTask.FromResult<IAsyncDisposable?>(new HeldResource(cleanup, lifetime.Token)),
            respond: () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new HeldResponseBody(body, () => responseDisposed.TrySetResult(), lifetime.Token),
            }));
        await rig.ProbeAsync();
        var ticket = await rig.EnqueueAsync("a", "held-body");
        await rig.PrepareAsync(ticket);
        var released = rig.ArmRelease();
        Assert.True(rig.Router.TryDispatchOnce());
        try
        {
            await body.Reached.WaitAsync(rig.Token);
            rig.Publish("a", generation: 1, open: false);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Admission.Releases);
            Assert.False(ticket.CompletionTask.IsCompleted);
            Assert.False(responseDisposed.Task.IsCompleted);
            Assert.Equal(Payload("a", "held-body"), Assert.Single(rig.Sink.ReceivedBodies));

            body.Release();
            Assert.Equal("{}"u8.ToArray(), await ticket.CompletionTask.WaitAsync(rig.Token));
            await cleanup.Reached.WaitAsync(rig.Token);
            Assert.True(responseDisposed.Task.IsCompletedSuccessfully);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Admission.Releases);
        }
        finally
        {
            body.Release();
            cleanup.Release();
        }
        await released.WaitAsync(rig.Token);
        Assert.Equal(0, rig.Admission.PhysicalUses);
        Assert.Equal(1, rig.Admission.Releases);
        Assert.Equal("request.completed", Assert.Single(TerminalEvents(rig, ticket)).Kind);
    }

    [Fact(Timeout = 20_000)]
    public async Task ThrowingSubmittedDisposalReportsTheFaultAndRetainsTheUse()
    {
        using var cleanup = new AdmissionFixtureBarrier();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var disposeError = new InvalidOperationException("The fixture could not confirm prepared-resource disposal.");
        var outcome = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(prepare: (_, _) => ValueTask.FromResult<IAsyncDisposable?>(
            new HeldResource(cleanup, lifetime.Token, () => throw disposeError)),
            respond: () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ObservedResponse(() => responseDisposed.TrySetResult()),
            }), singleModel: true);
        await rig.ProbeAsync();
        rig.Publish("a", generation: 2, open: false);
        var owner = await rig.StageOwnerAsync();
        rig.Admission.AfterPreparationCleanupForTests = (claim, error) =>
        {
            if (claim == owner) outcome.TrySetResult(error);
        };
        rig.Publish("a", generation: 2, open: true);
        var run = rig.SendStagedOwnerAsync(owner);
        try
        {
            await cleanup.Reached.WaitAsync(rig.Token);
            Assert.True(responseDisposed.Task.IsCompletedSuccessfully);
            Assert.False(outcome.Task.IsCompleted);
            Assert.Equal(owner.Request.Request.Body, Assert.Single(rig.Sink.ReceivedBodies));
            Assert.Equal(1, rig.Sink.Calls);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Admission.Releases);
        }
        finally
        {
            cleanup.Release();
        }

        Assert.Same(disposeError, await outcome.Task.WaitAsync(rig.Token));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(rig.Token));
        Assert.Same(disposeError, failure.InnerException);
        Assert.Equal(1, rig.Admission.PhysicalUses);
        Assert.Equal(0, rig.Admission.Releases);
    }

    [Fact(Timeout = 20_000)]
    public async Task OrdinaryCleanupFaultFencesTheSuccessorAfterTheTicketSucceeds()
    {
        using var cleanup = new AdmissionFixtureBarrier();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failure = new InvalidOperationException("The submitted preparation did not retire.");
        await using var rig = new Rig(singleModel: true, prepare: (claim, _) =>
            ValueTask.FromResult<IAsyncDisposable?>(claim.Ticket.ThreadId == "faulted-cleanup"
                ? new HeldResource(cleanup, lifetime.Token, () => throw failure)
                : new Resource()));
        await rig.ProbeAsync();
        var first = await rig.EnqueueAsync("a", "faulted-cleanup");
        await rig.PrepareAsync(first);
        Assert.True(rig.Router.TryDispatchOnce());
        try
        {
            await cleanup.Reached.WaitAsync(rig.Token);
            Assert.NotEmpty(await first.CompletionTask.WaitAsync(rig.Token));
            Assert.Equal(TicketState.Completed, first.State);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            var successor = await rig.EnqueueAsync("a", "must-remain-queued");
            rig.Publish("a", generation: 2, open: true);

            var attempted = 0;
            var parked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Router.BeforeAdmissionCommitForTests = ticket =>
            {
                if (ReferenceEquals(ticket, successor)) Interlocked.Exchange(ref attempted, 1);
            };
            rig.Router.AfterDispatchParkForTests = _ =>
            {
                if (Volatile.Read(ref attempted) == 1) parked.TrySetResult(true);
            };
            rig.Router.Events.Subscribe(evt =>
            {
                if (evt.Kind == "request.selected" && evt.TicketId == successor.Id) parked.TrySetResult(false);
            });

            rig.StartDispatcher();
            cleanup.Release();
            Assert.True(await parked.Task.WaitAsync(rig.Token), "Uncertain ordinary retirement must fence the successor.");
            Assert.Equal(0, rig.Router.Engines["fixture"].Inflight);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Admission.Releases);
            Assert.Equal(TicketState.Queued, successor.State);
            Assert.Equal(2L, rig.Claims[successor.Id].Generation);
            Assert.Equal(first.Id, Assert.Single(rig.Selected).TicketId);
            Assert.Equal(1, rig.Sink.Calls);
            Assert.Equal(Payload("a", "faulted-cleanup"), Assert.Single(rig.Sink.ReceivedBodies));

            var entry = Assert.Single(rig.Router.Events.Replay(0), evt => evt.Kind == "request.cleanup_failed");
            Assert.Equal(first.Id, entry.TicketId);
            Assert.Equal("fixture", entry.Engine);
            Assert.Equal(failure.Message, entry.Reason);
            var terminal = Assert.Single(TerminalEvents(rig, first));
            Assert.Equal("request.completed", terminal.Kind);
            Assert.True(entry.Seq > terminal.Seq);
        }
        finally
        {
            cleanup.Release();
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task LiveOrdinaryInvocationConsumesOnlyOneOfTwoEnginePermits()
    {
        using var firstResponse = new AdmissionFixtureBarrier();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var responseCount = 0;
        await using var rig = new Rig(slots: 2, respond: async () =>
        {
            if (Interlocked.Increment(ref responseCount) == 1)
                await firstResponse.HoldAsync(lifetime.Token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        await rig.ProbeAsync();
        var first = await rig.EnqueueAsync("a", "held-live-use");
        await rig.PrepareAsync(first);
        Assert.True(rig.Router.TryDispatchOnce());
        try
        {
            await firstResponse.Reached.WaitAsync(rig.Token);
            Assert.Equal(1, rig.Router.Engines["fixture"].Inflight);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            var second = await rig.EnqueueAsync("b", "second-live-use");
            await rig.PrepareAsync(second);
            var released = rig.ArmRelease();
            Assert.True(rig.Router.TryDispatchOnce());
            await second.CompletionTask.WaitAsync(rig.Token);
            await released.WaitAsync(rig.Token);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            Assert.Equal(1, rig.Admission.Releases);
            Assert.Collection(rig.Sink.ReceivedBodies,
                body => Assert.Equal(Payload("a", "held-live-use"), body),
                body => Assert.Equal(Payload("b", "second-live-use"), body));
        }
        finally
        {
            firstResponse.Release();
        }
        await first.CompletionTask.WaitAsync(rig.Token);
    }

    [Fact(Timeout = 20_000)]
    public async Task APreparingHeadKeepsItsQueueOrderWhileAnotherQueueSends()
    {
        using var held = new AdmissionFixtureBarrier();
        using var preparationLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var rig = new Rig(prepare: async (claim, _) =>
        {
            if (claim.Target.Model == "a")
                await held.HoldAsync(preparationLifetime.Token);
            return new Resource();
        });
        await rig.ProbeAsync();
        var first = await rig.EnqueueAsync("a", "held-first");
        Assert.False(rig.Router.TryDispatchOnce());
        await held.Reached.WaitAsync(rig.Token);

        var follower = await rig.EnqueueAsync("a", "same-queue-follower");
        var independent = await rig.EnqueueAsync("b", "independent");
        await rig.PrepareAsync(independent);
        Assert.True(rig.Router.TryDispatchOnce());
        await independent.CompletionTask.WaitAsync(rig.Token);
        for (var i = 0; i < 5; i++) Assert.False(rig.Router.TryDispatchOnce());
        Assert.Equal(1, rig.Sink.Calls);
        Assert.Equal(1, rig.Attempts[first.Id]);
        Assert.False(rig.Attempts.ContainsKey(follower.Id));
        Assert.Equal(1, rig.Admission.PreparationCount);

        held.Release();
        await rig.Prepared[first.Id].Task.WaitAsync(rig.Token);
        Assert.True(rig.Router.TryDispatchOnce());
        await first.CompletionTask.WaitAsync(rig.Token);
        await rig.PrepareAsync(follower);
        Assert.True(rig.Router.TryDispatchOnce());
        await follower.CompletionTask.WaitAsync(rig.Token);
        Assert.Equal(new[] { independent.Id, first.Id, follower.Id }, rig.Selected.Select(evt => evt.TicketId));
        Assert.Collection(rig.Sink.ReceivedBodies,
            body => Assert.Equal(Payload("b", "independent"), body),
            body => Assert.Equal(Payload("a", "held-first"), body),
            body => Assert.Equal(Payload("a", "same-queue-follower"), body));
    }

    [Fact(Timeout = 20_000)]
    public async Task RetiringPreparationConsumesItsSlotUntilItSettles()
    {
        using var held = new AdmissionFixtureBarrier();
        using var preparationLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var disposed = 0;
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(prepare: async (claim, ct) =>
        {
            using var registration = claim.Target.Model == "a"
                ? ct.Register(() => retired.TrySetResult())
                : default;
            if (claim.Target.Model != "c")
                await held.HoldAsync(preparationLifetime.Token); // Deliberately ignores retirement until released.
            return new Resource(() => Interlocked.Increment(ref disposed));
        });
        await rig.ProbeAsync();
        var first = await rig.EnqueueAsync("a", "retiring");
        var other = await rig.EnqueueAsync("b", "other");
        Assert.False(rig.Router.TryDispatchOnce());
        await Task.WhenAll(rig.Started[first.Id].Task, rig.Started[other.Id].Task).WaitAsync(rig.Token);

        Assert.True(rig.Router.TryCancel(first.Id));
        await retired.Task.WaitAsync(rig.Token);
        Assert.Equal(2, rig.Admission.PreparationCount);
        for (var generation = 2; generation <= 6; generation++)
        {
            rig.Publish("a", generation, open: true);
            Assert.False(rig.Router.TryDispatchOnce());
        }
        var third = await rig.EnqueueAsync("c", "bounded");
        Assert.False(rig.Router.TryDispatchOnce());
        Assert.False(rig.Attempts.ContainsKey(third.Id));
        Assert.Equal(1, rig.Attempts[first.Id]);
        Assert.Equal(2, rig.Admission.PreparationCount);
        Assert.False(rig.Prepared[first.Id].Task.IsCompleted);
        Assert.False(rig.Router.TryDispatchOnce());
        Assert.False(rig.Attempts.ContainsKey(third.Id));
        held.Release();

        Assert.False(await rig.Prepared[first.Id].Task.WaitAsync(rig.Token));
        Assert.True(await rig.Prepared[other.Id].Task.WaitAsync(rig.Token));
        Assert.Equal(1, disposed);
        Assert.Equal(1, rig.Admission.PreparationCount);
        Assert.Equal(0, rig.Sink.Calls);
        await Assert.ThrowsAsync<RouterRequestException>(() => first.CompletionTask);
        Assert.True(rig.Router.TryDispatchOnce());
        await other.CompletionTask.WaitAsync(rig.Token);
        await rig.PrepareAsync(third);
        Assert.True(rig.Router.TryDispatchOnce());
        await third.CompletionTask.WaitAsync(rig.Token);
        Assert.Equal(2, rig.Sink.Calls);
        Assert.Collection(rig.Sink.ReceivedBodies,
            body => Assert.Equal(Payload("b", "other"), body),
            body => Assert.Equal(Payload("c", "bounded"), body));
    }

    [Fact(Timeout = 20_000)]
    public async Task ExactTakeChecksMonotonicExpiryWhileTheExpiryTimerHasNotRun()
    {
        var disposed = 0;
        var retired = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(prepare: (_, _) =>
            ValueTask.FromResult<IAsyncDisposable?>(new Resource(() => Interlocked.Increment(ref disposed))));
        rig.Admission.AfterPreparationCleanupForTests = (_, error) => retired.TrySetResult(error);
        await rig.ProbeAsync();
        var ticket = await rig.EnqueueAsync("a", "expired", deadline: TimeSpan.FromHours(1));
        await rig.PrepareAsync(ticket);
        rig.Router.BeforeAdmissionCommitForTests = _ => rig.Clock.Timestamp = TimeSpan.FromHours(2).Ticks;

        Assert.False(rig.Router.TryDispatchOnce());
        await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(rig.Token));
        Assert.Equal("deadline_exceeded", ticket.ErrorCode);
        Assert.Empty(rig.Selected);
        Assert.Equal(0, rig.Sink.Calls);
        Assert.Equal(0, rig.Admission.PhysicalUses);
        Assert.Equal("request.failed", Assert.Single(TerminalEvents(rig, ticket)).Kind);
        Assert.Null(await retired.Task.WaitAsync(rig.Token));
        Assert.Equal(1, Volatile.Read(ref disposed));
        Assert.Equal(0, rig.Admission.PreparationCount);

        rig.Router.BeforeAdmissionCommitForTests = null;
        var successor = await rig.EnqueueAsync("a", "after-expiry");
        await rig.PrepareAsync(successor);
        Assert.True(rig.Router.TryDispatchOnce());
        await successor.CompletionTask.WaitAsync(rig.Token);
        Assert.Equal(successor.Id, Assert.Single(rig.Selected).TicketId);
        Assert.Equal(Payload("a", "after-expiry"), Assert.Single(rig.Sink.ReceivedBodies));
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    public async Task ExactTakeUsesLiveWindowAndPermitsWithoutTreatingTheEstimateAsAnExplicitFloor(
        bool explicitFloor, bool shrinkWindow, bool occupyLastPermit, bool sends)
    {
        await using var rig = new Rig(singleModel: true);
        await rig.ProbeAsync();
        var engine = rig.Router.Engines["fixture"];
        Assert.Equal(1_024L, engine.Capability.Serving.WindowFor("a"));
        const string marker = "identical prompt";
        var estimate = RouterCore.EstimateContextTokens(marker.Length, rig.Router.Config.Scheduler.CharsPerTokenEstimate);
        Assert.True(estimate > 0);
        var ticket = await rig.EnqueueAsync("a", marker, context: explicitFloor ? estimate : null);
        await rig.PrepareAsync(ticket);
        Assert.Equal(estimate, rig.Claims[ticket.Id].Request.RequiredContextTokens);

        using var commit = new AdmissionFixtureBarrier();
        rig.Router.BeforeAdmissionCommitForTests = _ => rig.Hold(commit);
        var dispatch = Task.Run(rig.Router.TryDispatchOnce);
        try
        {
            await commit.Reached.WaitAsync(rig.Token);
            if (shrinkWindow)
            {
                rig.SetWindow(0);
                await rig.ProbeAsync();
                Assert.Equal(0L, engine.Capability.Serving.WindowFor("a"));
            }
            if (occupyLastPermit)
            {
                engine.BeginRequest(0);
                Assert.False(engine.HasCapacity);
            }
        }
        finally
        {
            rig.Router.BeforeAdmissionCommitForTests = null;
            commit.Release();
        }

        try
        {
            Assert.Equal(sends, await dispatch.WaitAsync(rig.Token));
            if (sends)
            {
                await ticket.CompletionTask.WaitAsync(rig.Token);
                Assert.Equal(ticket.Id, Assert.Single(rig.Selected).TicketId);
                Assert.Single(rig.Sink.ReceivedBodies);
                Assert.Equal(1, rig.Sink.Calls);
            }
            else
            {
                Assert.Equal(TicketState.Queued, ticket.State);
                Assert.Empty(rig.Selected);
                Assert.Empty(rig.Sink.ReceivedBodies);
                Assert.Equal(0, rig.Sink.Calls);
            }
        }
        finally
        {
            if (occupyLastPermit) engine.EndRequest(0);
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task StagedOwnersAndJoinersShareOneEnginePermitAcrossRoutes()
    {
        using var ownerResponse = new AdmissionFixtureBarrier();
        await using var rig = new Rig(slots: 1);
        await rig.ProbeAsync();
        var joiner = await rig.EnqueueAsync("c", "cross-route-joiner");
        await rig.PrepareAsync(joiner);
        rig.Publish("a", generation: 1, open: false);
        var owner = await rig.StageOwnerAsync();
        // Replacing another route must preserve this engine's exact owner.
        rig.Publish("b", generation: 2, open: false);
        var competitor = rig.CaptureOwner("b", "competing-owner");
        Assert.False(rig.Admission.StageOpeningOwnerForTests(competitor));
        await rig.AssertOwnerCannotAuthorizeAsync(owner);

        rig.Publish("a", generation: 1, open: true);
        Assert.False(rig.CanJoinerTake(joiner));
        Assert.False(rig.Router.TryDispatchOnce());
        Assert.Equal(0, rig.Sink.Calls);
        var ownerSend = rig.SendStagedOwnerAsync(owner, ownerResponse);
        try
        {
            await ownerResponse.Reached.WaitAsync(rig.Token);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            Assert.True(rig.Admission.StageOpeningOwnerForTests(competitor));
            rig.Admission.Prepare(competitor);
            Assert.True(await rig.Prepared[competitor.Ticket.Id].Task.WaitAsync(rig.Token));
            rig.Publish("b", generation: 2, open: true);
            await rig.AssertOwnerCannotAuthorizeAsync(competitor);
            Assert.False(rig.Router.TryDispatchOnce());
            Assert.Empty(rig.Selected);
            Assert.Equal(1, rig.Sink.Calls);
            Assert.Equal(owner.Request.Request.Body.ToArray(), Assert.Single(rig.Sink.ReceivedBodies));
        }
        finally
        {
            ownerResponse.Release();
        }

        Assert.True(await ownerSend.WaitAsync(rig.Token));
        Assert.Equal(0, rig.Admission.PhysicalUses);
        Assert.Equal(1, rig.Admission.Releases);
        Assert.False(rig.Router.TryDispatchOnce());
        Assert.True(await rig.SendStagedOwnerAsync(competitor).WaitAsync(rig.Token));
        var joinerRelease = rig.ArmRelease();
        Assert.True(rig.Router.TryDispatchOnce());
        await joiner.CompletionTask.WaitAsync(rig.Token);
        await joinerRelease.WaitAsync(rig.Token);
        Assert.Equal(0, rig.Admission.PhysicalUses);
        Assert.Equal(3, rig.Admission.Releases);
        Assert.Equal(joiner.Id, Assert.Single(rig.Selected).TicketId);
        Assert.Collection(rig.Sink.ReceivedBodies,
            body => Assert.Equal(owner.Request.Request.Body.ToArray(), body),
            body => Assert.Equal(competitor.Request.Request.Body.ToArray(), body),
            body => Assert.Equal(Payload("c", "cross-route-joiner"), body));
    }

    [Fact(Timeout = 20_000)]
    public async Task MatchingOpenGivesEligibleOwnerTheLastSlotEvenWhenTheJoinerTriesFirst()
    {
        await using var rig = new Rig(singleModel: true);
        await rig.ProbeAsync();
        var joiner = await rig.EnqueueAsync("a", "ordinary");
        await rig.PrepareAsync(joiner);
        rig.Publish("a", generation: 1, open: false);
        var owner = await rig.StageOwnerAsync();
        await rig.AssertOwnerCannotAuthorizeAsync(owner);
        Assert.False(rig.Router.TryDispatchOnce());
        Assert.Equal(0, rig.Admission.PhysicalUses);
        Assert.Empty(rig.Sink.ReceivedBodies);

        using var ownerFirstWrite = new AdmissionFixtureBarrier();
        using var ownerResponse = new AdmissionFixtureBarrier();
        var ownerSend = RunOwnerAsync();
        try
        {
            await ownerFirstWrite.Reached.WaitAsync(rig.Token);
            var deferred = rig.ArmJoinerAttempt(joiner);
            rig.Publish("a", generation: 1, open: true);
            rig.StartDispatcher();
            await deferred.WaitAsync(rig.Token);
            Assert.Empty(rig.Selected);
            Assert.False(rig.CanJoinerTake(joiner));
            Assert.Equal(TicketState.Queued, joiner.State);
            Assert.Equal(0, rig.Router.Engines["fixture"].Inflight);
            Assert.Equal(0, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Sink.Calls);

            ownerFirstWrite.Release();
            await ownerResponse.Reached.WaitAsync(rig.Token);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            Assert.Equal(1, rig.Sink.Calls);
            Assert.Equal(owner.Request.Request.Body.ToArray(), Assert.Single(rig.Sink.ReceivedBodies));
            Assert.Equal(TicketState.Queued, joiner.State);
        }
        finally
        {
            ownerFirstWrite.Release();
            ownerResponse.Release();
        }

        Assert.True(await ownerSend.WaitAsync(rig.Token));
        await joiner.CompletionTask.WaitAsync(rig.Token);
        Assert.Equal(joiner.Id, Assert.Single(rig.Selected).TicketId);
        Assert.Equal(2, rig.Sink.Calls);
        Assert.Equal(Payload("a", "ordinary"), rig.Sink.ReceivedBodies[1]);

        async Task<bool> RunOwnerAsync()
        {
            await ownerFirstWrite.HoldAsync(rig.Token);
            return await rig.SendStagedOwnerAsync(owner, ownerResponse);
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellingOrExpiringAStagedOwnerHandsOffWithoutWaitingForItsContinuationOrCleanup(
        bool expire, bool afterOpen)
    {
        using var cleanup = new AdmissionFixtureBarrier();
        using var cleanupLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var rig = new Rig(singleModel: true, prepare: (claim, _) =>
            ValueTask.FromResult<IAsyncDisposable?>(claim.Ticket.Id == "staged-owner"
                ? new HeldResource(cleanup, cleanupLifetime.Token)
                : new Resource()));
        await rig.ProbeAsync();
        var joiner = await rig.EnqueueAsync("a", "handoff");
        await rig.PrepareAsync(joiner);
        rig.Publish("a", generation: 1, open: false);
        var owner = await rig.StageOwnerAsync();
        await rig.AssertOwnerCannotAuthorizeAsync(owner);
        Assert.False(rig.Router.TryDispatchOnce());
        Assert.Equal(0, rig.Sink.Calls);
        Assert.Equal(0, rig.Admission.PhysicalUses);

        using var heldOwner = new AdmissionFixtureBarrier();
        var ownerSend = RunOwnerAsync();
        try
        {
            await heldOwner.Reached.WaitAsync(rig.Token);
            var deferred = rig.ArmJoinerAttempt(joiner);
            if (afterOpen) rig.Publish("a", generation: 1, open: true);
            rig.StartDispatcher();
            Task? waiter = null;
            if (afterOpen)
            {
                waiter = await deferred.WaitAsync(rig.Token);
                Assert.Equal(TicketState.Queued, joiner.State);
                Assert.Equal(0, rig.Admission.PhysicalUses);
                Assert.Equal(0, rig.Sink.Calls);
            }

            if (expire)
            {
                rig.Clock.Timestamp = TimeSpan.FromHours(2).Ticks;
                Assert.True(owner.Request.QueueExpired(rig.Clock));
            }
            // This is the offline owner's terminal callback, including the fake deadline delivery.
            rig.Admission.Invalidate(owner.Ticket);
            await cleanup.Reached.WaitAsync(rig.Token);
            if (waiter is not null)
                await waiter.WaitAsync(rig.Token);
            if (!afterOpen)
            {
                Assert.Equal(0, rig.Sink.Calls);
                rig.Publish("a", generation: 1, open: true);
            }

            await joiner.CompletionTask.WaitAsync(rig.Token);
            Assert.False(ownerSend.IsCompleted);
            Assert.Equal(joiner.Id, Assert.Single(rig.Selected).TicketId);
            Assert.Equal(1, rig.Sink.Calls);
            Assert.Equal(Payload("a", "handoff"), Assert.Single(rig.Sink.ReceivedBodies));
        }
        finally
        {
            heldOwner.Release();
            cleanup.Release();
        }

        Assert.False(await ownerSend.WaitAsync(rig.Token));
        Assert.Equal(1, rig.Sink.Calls);

        async Task<bool> RunOwnerAsync()
        {
            await heldOwner.HoldAsync(rig.Token);
            return await rig.SendStagedOwnerAsync(owner);
        }
    }

    private static byte[] Payload(string model, string marker) =>
        Encoding.UTF8.GetBytes($$"""{"model":"{{model}}","prompt":"{{marker}}"}""");

    private static IEnumerable<RouterEvent> TerminalEvents(Rig rig, RouterTicket ticket) =>
        rig.Router.Events.Replay(0).Where(evt => evt.TicketId == ticket.Id &&
            evt.Kind is "request.completed" or "request.failed" or "request.cancelled");

    private sealed class ObservedResponse(Action disposed) : StringContent("{}")
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) disposed();
        }
    }

    private sealed class HeldResponseBody(AdmissionFixtureBarrier body, Action disposed, CancellationToken lifetime)
        : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await body.HoldAsync(lifetime);
            await stream.WriteAsync("{}"u8.ToArray(), lifetime);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 2;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) disposed();
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedStagedOwnerPreparationReleasesTheParkedJoiner(bool throws, bool? afterArm)
    {
        using var preparation = new AdmissionFixtureBarrier();
        using var arm = new AdmissionFixtureBarrier();
        using var beforeRetake = new AdmissionFixtureBarrier();
        using var beforeSend = new AdmissionFixtureBarrier();
        using var preparationLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var intendedBranch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedFault = new InvalidOperationException("The staged owner's preparation failed.");
        await using var rig = new Rig(singleModel: true, prepare: async (claim, _) =>
        {
            if (claim.Ticket.Id != "staged-owner") return new Resource();
            await preparation.HoldAsync(preparationLifetime.Token);
            preparationLifetime.Token.ThrowIfCancellationRequested();
            if (throws)
            {
                intendedBranch.TrySetResult(true);
                throw expectedFault;
            }
            intendedBranch.TrySetResult(false);
            return null;
        });
        await rig.ProbeAsync();
        var joiner = await rig.EnqueueAsync("a", "after-owner-preparation-failure");
        await rig.PrepareAsync(joiner);
        rig.Publish("a", generation: 1, open: false);
        var owner = rig.StartOwnerPreparation();
        Exception? observedFault = null;
        rig.Admission.AfterPreparationFaultForTests = (claim, error) =>
        {
            if (claim == owner) observedFault = error;
        };

        try
        {
            await preparation.Reached.WaitAsync(rig.Token);
            Task<Task>? deferred = null;
            if (afterArm is { } holdAfterArm)
            {
                var attempted = false;
                rig.Router.BeforeAdmissionCommitForTests = ticket => attempted |= ReferenceEquals(ticket, joiner);
                rig.Router.BeforeDispatchParkForTests = armed =>
                {
                    if (attempted && armed == holdAfterArm) rig.Hold(arm);
                };
            }
            else
            {
                deferred = rig.ArmJoinerAttempt(joiner);
            }

            rig.Publish("a", generation: 1, open: true);
            rig.StartDispatcher();
            Task? waiter = null;
            if (deferred is null)
                await arm.Reached.WaitAsync(rig.Token);
            else
            {
                waiter = await deferred.WaitAsync(rig.Token);
                rig.Router.BeforeAdmissionCommitForTests = ticket =>
                {
                    if (ReferenceEquals(ticket, joiner)) rig.Hold(beforeRetake);
                };
                rig.Admission.BeforeSendForTests = _ => beforeSend.HoldAsync(rig.Token);
            }

            Assert.False(rig.CanJoinerTake(joiner));
            Assert.False(await rig.SendStagedOwnerAsync(owner).WaitAsync(rig.Token));
            Assert.Empty(rig.Selected);
            Assert.Equal(0, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Sink.Calls);

            preparation.Release();
            Assert.False(await rig.Prepared[owner.Ticket.Id].Task.WaitAsync(rig.Token));
            Assert.Equal(throws, await intendedBranch.Task.WaitAsync(rig.Token));
            Assert.False(preparationLifetime.IsCancellationRequested);
            if (throws) Assert.Same(expectedFault, observedFault);
            else Assert.Null(observedFault);
            if (waiter is not null)
            {
                // The failure wakes dispatch before it reports preparation completion to the fixture.
                await waiter.WaitAsync(rig.Token);
                await beforeRetake.Reached.WaitAsync(rig.Token);
            }
            Assert.True(rig.CanJoinerTake(joiner),
                "The failed owner preparation must release the reservation before the joiner takes it.");
            Assert.Equal(0, rig.Admission.PhysicalUses);
            Assert.Empty(rig.Selected);
            arm.Release();
            beforeRetake.Release();

            if (waiter is not null)
            {
                // The same capacity check is false once the joiner owns the last slot.
                await beforeSend.Reached.WaitAsync(rig.Token);
                Assert.Equal(joiner.Id, Assert.Single(rig.Selected).TicketId);
                Assert.Equal(1, rig.Admission.PhysicalUses);
                Assert.False(rig.CanJoinerTake(joiner));
                Assert.Equal(0, rig.Sink.Calls);
                beforeSend.Release();
            }

            await joiner.CompletionTask.WaitAsync(rig.Token);
            Assert.Equal(joiner.Id, Assert.Single(rig.Selected).TicketId);
            Assert.Equal(1, rig.Attempts[owner.Ticket.Id]);
            Assert.Equal(1, rig.Sink.Calls);
            Assert.Equal(Payload("a", "after-owner-preparation-failure"), Assert.Single(rig.Sink.ReceivedBodies));
            Assert.False(await rig.SendStagedOwnerAsync(owner));
            Assert.Equal(1, rig.Sink.Calls);
            rig.Publish("a", generation: 1, open: false);
            Assert.False(rig.Admission.StageOpeningOwnerForTests(owner));
        }
        finally
        {
            preparation.Release();
            arm.Release();
            beforeRetake.Release();
            beforeSend.Release();
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task FullPreparationTableGivesThePendingOwnerPriorityOverAnInProgressCompetingScan()
    {
        using var firstRetirement = new AdmissionFixtureBarrier();
        using var secondRetirement = new AdmissionFixtureBarrier();
        using var competingScan = new AdmissionFixtureBarrier();
        using var ownerResponse = new AdmissionFixtureBarrier();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstNewPreparation = new TaskCompletionSource<RouterTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(prepare: (claim, _) =>
        {
            if (claim.Request.Thread.ThreadId == "retire-b")
                return ValueTask.FromResult<IAsyncDisposable?>(new HeldResource(firstRetirement, lifetime.Token));
            if (claim.Request.Thread.ThreadId == "retire-c")
                return ValueTask.FromResult<IAsyncDisposable?>(new HeldResource(secondRetirement, lifetime.Token));
            firstNewPreparation.TrySetResult(claim.Ticket);
            return ValueTask.FromResult<IAsyncDisposable?>(new Resource());
        });
        await rig.ProbeAsync();
        var first = await rig.EnqueueAsync("b", "retire-b");
        var second = await rig.EnqueueAsync("c", "retire-c");
        await rig.PrepareAsync(first);
        Assert.True(await rig.Prepared[second.Id].Task.WaitAsync(rig.Token));
        var retired = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Admission.AfterPreparationCleanupForTests = (claim, error) =>
        {
            if (ReferenceEquals(claim.Ticket, first)) retired.TrySetResult(error);
        };

        try
        {
            Assert.True(rig.Router.TryCancel(first.Id));
            Assert.True(rig.Router.TryCancel(second.Id));
            await firstRetirement.Reached.WaitAsync(rig.Token);
            await secondRetirement.Reached.WaitAsync(rig.Token);
            await Assert.ThrowsAsync<RouterRequestException>(() => first.CompletionTask.WaitAsync(rig.Token));
            await Assert.ThrowsAsync<RouterRequestException>(() => second.CompletionTask.WaitAsync(rig.Token));

            rig.Publish("a", generation: 1, open: false);
            var owner = rig.StartOwnerPreparation();
            Assert.Equal(2, rig.Admission.PreparationCount);
            Assert.False(rig.Attempts.ContainsKey(owner.Ticket.Id));

            var competitor = await rig.EnqueueAsync("b", "competing-route");
            var heldScan = 0;
            rig.Router.BeforeAdmissionPreparationForTests = ticket =>
            {
                if (ReferenceEquals(ticket, competitor) && Interlocked.Exchange(ref heldScan, 1) == 0)
                    rig.Hold(competingScan);
            };
            rig.StartDispatcher();
            await competingScan.Reached.WaitAsync(rig.Token);
            // This pass already checked pending owners while both slots were occupied.
            firstRetirement.Release();
            Assert.Null(await retired.Task.WaitAsync(rig.Token));
            Assert.Equal(1, rig.Admission.PreparationCount);
            competingScan.Release();

            Assert.Same(owner.Ticket, await firstNewPreparation.Task.WaitAsync(rig.Token));
            Assert.True(await rig.Prepared[owner.Ticket.Id].Task.WaitAsync(rig.Token));
            Assert.Equal(2, rig.Admission.PreparationCount);
            Assert.False(rig.Attempts.ContainsKey(competitor.Id));
            Assert.Equal(0, rig.Sink.Calls);

            var openWait = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Router.AfterDispatchParkForTests = wake =>
            {
                lock (rig.Admission.Gate)
                    if (rig.Admission.IsOpen(owner)) openWait.TrySetResult(wake);
            };
            rig.Publish("a", generation: 1, open: true);
            var waiter = await openWait.Task.WaitAsync(rig.Token);
            var ownerSend = rig.SendStagedOwnerAsync(owner, ownerResponse);
            await ownerResponse.Reached.WaitAsync(rig.Token);
            await waiter.WaitAsync(rig.Token);
            await competitor.CompletionTask.WaitAsync(rig.Token);
            Assert.Equal(1, rig.Attempts[owner.Ticket.Id]);
            Assert.Equal(1, rig.Attempts[competitor.Id]);
            Assert.Equal(competitor.Id, Assert.Single(rig.Selected).TicketId);
            Assert.Equal(2, rig.Sink.Calls);
            var expectedOwner = owner.Request.Request.Body.ToArray();
            var expectedCompetitor = Payload("b", "competing-route");
            Assert.Single(rig.Sink.ReceivedBodies, body => body.AsSpan().SequenceEqual(expectedOwner));
            Assert.Single(rig.Sink.ReceivedBodies, body => body.AsSpan().SequenceEqual(expectedCompetitor));
            ownerResponse.Release();
            Assert.True(await ownerSend.WaitAsync(rig.Token));
        }
        finally
        {
            firstRetirement.Release();
            secondRetirement.Release();
            competingScan.Release();
            ownerResponse.Release();
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalePreparationRetirementWakesTheSameTicketsNewGenerationOwner(bool openBeforeRetirement)
    {
        using var preparation = new AdmissionFixtureBarrier();
        using var preparationLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var retirementSignalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalPrepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementPrepared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleDisposals = 0;
        await using var rig = new Rig(singleModel: true, prepare: async (claim, ct) =>
        {
            if (claim.Ticket.Id != "staged-owner" || claim.Generation != 1) return new Resource();
            using var registration = ct.Register(() => retirementSignalled.TrySetResult());
            await preparation.HoldAsync(preparationLifetime.Token);
            // A resource returned after route retirement must be disposed without publishing.
            return new Resource(() => Interlocked.Increment(ref staleDisposals));
        });
        await rig.ProbeAsync();
        var joiner = await rig.EnqueueAsync("a", "after-replacement-owner");
        rig.Publish("a", generation: 1, open: false);
        var original = rig.StartOwnerPreparation();
        var replacement = original with { Generation = 2 };
        rig.Admission.AfterPreparationForTests += (claim, ready) =>
        {
            if (claim == original) originalPrepared.TrySetResult(ready);
            if (claim == replacement) replacementPrepared.TrySetResult(ready);
        };
        try
        {
            await preparation.Reached.WaitAsync(rig.Token);
            rig.Publish("a", generation: 2, open: false);
            await retirementSignalled.Task.WaitAsync(rig.Token);
            Assert.True(rig.Admission.StageOpeningOwnerForTests(replacement));
            rig.Admission.Prepare(replacement);
            Task<Task> parked;
            if (openBeforeRetirement)
            {
                rig.Publish("a", generation: 2, open: true);
                // Settle the joiner's preparation wake before the dispatcher starts parking.
                await rig.PrepareAsync(joiner);
                parked = rig.ArmJoinerAttempt(joiner);
            }
            else
            {
                var parkedClosed = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                rig.Router.AfterDispatchParkForTests = wake => parkedClosed.TrySetResult(wake);
                parked = parkedClosed.Task;
            }
            rig.StartDispatcher();
            var waiter = await parked.WaitAsync(rig.Token);
            Assert.Equal(1, rig.Attempts[original.Ticket.Id]);
            Assert.Equal(openBeforeRetirement ? 2 : 1, rig.Admission.PreparationCount);
            Assert.Empty(rig.Selected);
            Assert.Equal(0, rig.Sink.Calls);

            // No second Prepare, manual scan, or route publication can supply this wake.
            preparation.Release();
            await waiter.WaitAsync(rig.Token);
            Assert.False(await originalPrepared.Task.WaitAsync(rig.Token));
            Assert.True(await replacementPrepared.Task.WaitAsync(rig.Token));
            Assert.Equal(1, Volatile.Read(ref staleDisposals));
            Assert.Equal(2, rig.Attempts[replacement.Ticket.Id]);
            Assert.False(await rig.SendStagedOwnerAsync(original).WaitAsync(rig.Token));
            if (!openBeforeRetirement)
            {
                await rig.AssertOwnerCannotAuthorizeAsync(replacement);
                var parkedOpen = rig.ArmJoinerAttempt(joiner);
                rig.Publish("a", generation: 2, open: true);
                await parkedOpen.WaitAsync(rig.Token);
            }

            Assert.Equal(replacement.Generation, rig.Claims[joiner.Id].Generation);
            Assert.False(rig.CanJoinerTake(joiner));
            Assert.Empty(rig.Selected);
            Assert.Equal(0, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Sink.Calls);

            var bothReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Admission.AfterReleaseForTests = () =>
            {
                if (rig.Admission.Releases == 2) bothReleased.TrySetResult();
            };
            Assert.True(await rig.SendStagedOwnerAsync(replacement).WaitAsync(rig.Token));
            await joiner.CompletionTask.WaitAsync(rig.Token);
            await bothReleased.Task.WaitAsync(rig.Token);
            Assert.Equal(joiner.Id, Assert.Single(rig.Selected).TicketId);
            Assert.Equal(2, rig.Sink.Calls);
            Assert.Equal(2, rig.Admission.Releases);
            Assert.Equal(0, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Admission.PreparationCount);
            Assert.Collection(rig.Sink.ReceivedBodies,
                body => Assert.Equal(replacement.Request.Request.Body.ToArray(), body),
                body => Assert.Equal(Payload("a", "after-replacement-owner"), body));
        }
        finally
        {
            preparation.Release();
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
        private Task _dispatcher = Task.CompletedTask;
        private Task? _routerDisposal;
        private readonly FakeEngineHandler _discovery;

        internal Rig(Func<ExactTicketAdmission.Claim, CancellationToken, ValueTask<IAsyncDisposable?>>? prepare = null,
                     Func<Task<HttpResponseMessage>>? respond = null, bool singleModel = false, int? slots = null)
        {
            _discovery = singleModel
                ? new FakeEngineHandler { LoadedModels = ["a"], NCtx = 1_024, TotalSlots = 1 }
                : new FakeEngineHandler { LoadedModels = ["a", "b", "c"] };
            if (slots is { } limit) _discovery.TotalSlots = limit;
            Sink = new AdmissionFixtureSink(respond);
            Admission = new ExactTicketAdmission(Prepare, (request, _) => Sink.AcceptAsync(request));
            Admission.AfterPreparationForTests = (claim, ready) => Prepared[claim.Ticket.Id].TrySetResult(ready);
            Router = new RouterCore(new RouterConfig
            {
                AutoSwitch = false,
                Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal)
                {
                    ["fixture"] = new()
                    {
                        Name = "fixture",
                        Endpoint = new Uri("http://fixture.invalid/v1/"),
                        Catalog = new Dictionary<string, EngineModelDefinition>(StringComparer.Ordinal)
                        {
                            ["a"] = new() { Name = "a" },
                        },
                    },
                },
                HttpHandlerFactory = _ => _discovery,
            }, scheduler: null, events: null, clock: Clock, admission: Admission);

            Publish("a", generation: 1, open: true);
            Publish("b", generation: 1, open: true);
            Publish("c", generation: 1, open: true);

            ValueTask<IAsyncDisposable?> Prepare(ExactTicketAdmission.Claim claim, CancellationToken ct)
            {
                Claims[claim.Ticket.Id] = claim;
                Attempts.AddOrUpdate(claim.Ticket.Id, 1, static (_, count) => count + 1);
                Started[claim.Ticket.Id].TrySetResult();
                return prepare?.Invoke(claim, ct) ?? ValueTask.FromResult<IAsyncDisposable?>(new Resource());
            }
        }

        internal RouterCore Router { get; }
        internal ExactTicketAdmission Admission { get; }
        internal AdmissionFixtureSink Sink { get; }
        internal ManualClock Clock { get; } = new();
        internal CancellationToken Token => _deadline.Token;
        internal ConcurrentDictionary<string, ExactTicketAdmission.Claim> Claims { get; } = new(StringComparer.Ordinal);
        internal void SetWindow(long window) => _discovery.NCtx = window;
        internal ConcurrentDictionary<string, int> Attempts { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, TaskCompletionSource> Started { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, TaskCompletionSource<bool>> Prepared { get; } = new(StringComparer.Ordinal);
        internal IEnumerable<RouterEvent> Selected => Router.Events.Replay(0).Where(evt => evt.Kind == "request.selected");

        internal async Task ProbeAsync()
        {
            var engine = Router.Engines["fixture"];
            var before = engine.Capability;
            Assert.True(await engine.ProbeHealthOnceAsync(Token).WaitAsync(Token));
            var after = engine.Capability;
            Assert.Equal(before.Version + 1, after.Version);
            Assert.True(after.Healthy);
            Assert.Equal(_discovery.TotalSlots, after.Serving.TotalSlots);
            if (_discovery.LoadedModels is ["a"])
                Assert.Equal(_discovery.NCtx, after.Serving.WindowFor("a"));
        }

        internal void Publish(string model, long generation, bool open) =>
            Admission.Publish(new ExactTicketAdmission.Route("fixture", model, null), generation, open);

        internal async Task<RouterTicket> EnqueueAsync(string model, string marker, TimeSpan? deadline = null, long? context = null)
        {
            var ticket = await Router.EnqueueAsync(marker, new InferenceRequest
            {
                Model = model,
                Body = Payload(model, marker),
                Options = new RouterRequestOptions { MaxQueueTime = deadline, RequiredContext = context },
            });
            Started[ticket.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Prepared[ticket.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return ticket;
        }

        internal async Task PrepareAsync(RouterTicket ticket)
        {
            Assert.False(Router.TryDispatchOnce());
            Assert.True(await Prepared[ticket.Id].Task.WaitAsync(Token));
        }

        internal async Task<ExactTicketAdmission.Claim> StageOwnerAsync()
        {
            var owner = StartOwnerPreparation();
            Assert.True(await Prepared[owner.Ticket.Id].Task.WaitAsync(Token));
            return owner;
        }

        internal ExactTicketAdmission.Claim StartOwnerPreparation()
        {
            var owner = CaptureOwner("a", "staged-owner");
            Assert.True(Admission.StageOpeningOwnerForTests(owner));
            Admission.Prepare(owner);
            return owner;
        }

        internal ExactTicketAdmission.Claim CaptureOwner(string model, string id)
        {
            var thread = Router.RegisterThread(id, ThreadPriority.High());
            var ticket = new RouterTicket(id, thread.ThreadId, model, Clock.GetUtcNow());
            var request = new QueuedRequest
            {
                Ticket = ticket,
                Thread = thread,
                PriorityNode = new PrioritizedRequest(ticket, thread.Priority, 0),
                EnqueuedSeq = 0,
                EnqueuedAtTimestamp = Clock.GetTimestamp(),
                PromptChars = 5,
                Request = new InferenceRequest
                {
                    Model = model,
                    Body = Payload(model, "owner"),
                    Options = new RouterRequestOptions { MaxQueueTime = TimeSpan.FromHours(1) },
                },
            };
            Started[ticket.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Prepared[ticket.Id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Assert.IsType<ExactTicketAdmission.Claim>(
                Admission.Capture(request, new ExactTicketAdmission.Route("fixture", model, null),
                    switchRequired: false, explicitContext: 0));
        }

        internal ExactTicketAdmission.Invocation? AdmitStagedOwner(ExactTicketAdmission.Claim owner) =>
            Admission.AuthorizeOpeningOwnerForTests(owner, Router.Engines[owner.Target.Engine]);

        internal async Task AssertOwnerCannotAuthorizeAsync(ExactTicketAdmission.Claim owner)
        {
            var deniedGrant = AdmitStagedOwner(owner);
            try
            {
                Assert.Null(deniedGrant);
            }
            finally
            {
                if (deniedGrant is not null) await deniedGrant.AbortAsync();
            }
        }

        internal async Task<bool> SendStagedOwnerAsync(ExactTicketAdmission.Claim owner,
                                                       AdmissionFixtureBarrier? responseHeld = null)
        {
            var invocation = AdmitStagedOwner(owner);
            if (invocation is null) return false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "http://fixture.invalid/v1/completions")
                {
                    Content = new ReadOnlyMemoryContent(owner.Request.Request.Body),
                };
                await invocation.RunAsync(request,
                    _ => responseHeld?.HoldAsync(Token) ?? Task.CompletedTask, reportFailure: null, Token);
            }
            finally
            {
                await invocation.AbortAsync();
            }
            return true;
        }

        internal Task<Task> ArmJoinerAttempt(RouterTicket ticket)
        {
            var attempted = 0;
            var parked = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            Router.BeforeAdmissionCommitForTests = current =>
            {
                if (ReferenceEquals(current, ticket)) Interlocked.Exchange(ref attempted, 1);
            };
            Router.AfterDispatchParkForTests = wake =>
            {
                if (Volatile.Read(ref attempted) == 1) parked.TrySetResult(wake);
            };
            return parked.Task;
        }

        internal bool CanJoinerTake(RouterTicket ticket)
        {
            lock (Admission.Gate)
                return Admission.CanTake(Claims[ticket.Id], Router.Engines["fixture"].ReadDispatchObservation());
        }

        internal void Hold(AdmissionFixtureBarrier barrier) => barrier.HoldAsync(Token).GetAwaiter().GetResult();

        internal Task ArmRelease()
        {
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Admission.AfterReleaseForTests = () => released.TrySetResult();
            return released.Task;
        }

        internal void StartDispatcher() => _dispatcher = Task.Run(Router.DispatchLoop, Token);

        internal Task StopRouterAsync() => _routerDisposal ??= Router.DisposeAsync().AsTask();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopRouterAsync();
                await _dispatcher.WaitAsync(Token);
            }
            finally
            {
                _discovery.Dispose();
                _deadline.Dispose();
            }
        }
    }

    private sealed class Resource(Action? dispose = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HeldResource(AdmissionFixtureBarrier cleanup, CancellationToken lifetime, Action? disposed = null)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await cleanup.HoldAsync(lifetime);
            disposed?.Invoke();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        internal long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
