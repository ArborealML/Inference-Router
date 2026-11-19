using System.Collections.Concurrent;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class ShutdownStopCauseTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(30);
    private const string Model = "extra";

    [Fact(Timeout = 60_000)]
    public async Task SwitchingReportsThePublishedMarkerBeforePrivateStateIsInstalled()
    {
        var provider = new HeldProvider();
        provider.Release(LifecycleResult.Fail("cleanup", "the test released the fake launch"));
        using var handler = new FakeEngineHandler();
        await using var engine = Engine(provider, handler);
        var markerRaised = new Pause();
        engine.AfterSwitchMarkerRaisedForTests = markerRaised.Enter;
        var attempt = Task.Run(() => engine.WaitForModelAsync(Model, HoldLimit, CancellationToken.None));
        (bool Switching, string? Target) observed;

        try
        {
            await markerRaised.Entered.WaitAsync(Limit);
            observed = await Task.Run(() => (engine.Switching, engine.SwitchingToward)).WaitAsync(Limit);
        }
        finally
        {
            markerRaised.Release();
        }

        await attempt.WaitAsync(Limit);
        Assert.Equal(Model, observed.Target);
        Assert.True(observed.Switching);
    }

    [Fact(Timeout = 60_000)]
    public async Task AStoppedAnswerArrivesWhileTheProviderIsStillHeld()
    {
        var beforeWake = new Pause();
        var cancelling = new Pause();
        var provider = new HeldProvider(cancelling.Enter);
        using var handler = new FakeEngineHandler();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = Engine(provider, handler, dispatchHint: () => ended.TrySetResult());
        var atWake = new TaskCompletionSource<(SwitchAttemptRecord Record, RetainedSwitchAttempt Snapshot)[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AfterStopDecisionForTests = beforeWake.Enter;
        engine.AfterReadyWaitForTests = _ => atWake.TrySetResult(engine.SwitchHistory
            .Select(entry => (entry.Terminal, entry with { Terminal = entry.Terminal with { } }))
            .ToArray());
        var waiter = engine.WaitForModelAsync(Model, HoldLimit, CancellationToken.None);
        Task? shutdown = null;
        EngineRuntime.SwitchResult stopped;
        string? markerWhileHeld;
        SwitchAttemptRecord sealedBeforeWake;
        RetainedSwitchAttempt snapshotBeforeWake;

        try
        {
            await provider.Launched.WaitAsync(Limit);
            shutdown = Task.Run(async () => await engine.DisposeAsync());
            await beforeWake.Entered.WaitAsync(Limit);
            Assert.False(waiter.IsCompleted);
            Assert.False(provider.CancellationObserved.IsCompleted);
            var retained = Assert.Single(engine.SwitchHistory);
            sealedBeforeWake = retained.Terminal;
            snapshotBeforeWake = retained with { Terminal = sealedBeforeWake with { } };
            Assert.Equal(Model, snapshotBeforeWake.Terminal.Target.CanonicalModel);
            Assert.Equal(SwitchAttemptOutcome.Cancelled, snapshotBeforeWake.Terminal.Outcome);
            var launch = Assert.IsType<SwitchStageObservation>(snapshotBeforeWake.Terminal.GetStage(SwitchStage.Launch));
            Assert.Equal(SwitchStageOutcome.Incomplete, launch.Outcome);
            Assert.InRange(launch.StartedAt, snapshotBeforeWake.Terminal.StartedAt, snapshotBeforeWake.Terminal.EndedAt);
            Assert.Equal(snapshotBeforeWake.Terminal.EndedAt, launch.ObservedUntil);
            Assert.Null(snapshotBeforeWake.Terminal.GetStage(SwitchStage.Shutdown));
            Assert.Null(snapshotBeforeWake.Terminal.ShutdownSource);
            Assert.Empty(snapshotBeforeWake.Terminal.NativeStops);
            Assert.Null(snapshotBeforeWake.Terminal.NativeLaunch);
            Assert.Null(snapshotBeforeWake.FirstForwardedAt);
            beforeWake.Release();

            // Hold provider cancellation so the waiter can read the actual published terminal
            // while launch is still outstanding.
            await cancelling.Entered.WaitAsync(Limit);
            stopped = await waiter.WaitAsync(Limit);
            markerWhileHeld = engine.SwitchingToward;
            var observed = Assert.Single(await atWake.Task.WaitAsync(Limit));
            Assert.Same(sealedBeforeWake, observed.Record);
            Assert.Equal(snapshotBeforeWake, observed.Snapshot);
        }
        finally
        {
            beforeWake.Release();
            cancelling.Release();
            provider.Release(LifecycleResult.Fail("cancelled_during_launch", "the held launch observed cancellation"));
            if (shutdown is not null) await shutdown.WaitAsync(Limit);
        }

        await ended.Task.WaitAsync(Limit);
        Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, stopped.Outcome);
        Assert.Null(stopped.Detail);
        Assert.Equal(Model, markerWhileHeld);
        Assert.Null(engine.SwitchingToward);
        var afterProviderReturn = Assert.Single(engine.SwitchHistory);
        Assert.Same(sealedBeforeWake, afterProviderReturn.Terminal);
        Assert.Equal(snapshotBeforeWake, afterProviderReturn);
    }

    [Fact(Timeout = 60_000)]
    public async Task AProviderFailureCannotOverwriteThePublishedStop()
    {
        var provider = new HeldProvider();
        using var handler = new FakeEngineHandler();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = Engine(provider, handler, dispatchHint: () => ended.TrySetResult());
        var resume = new Pause();
        engine.AfterReadyWaitForTests = _ => resume.Enter();
        var waiter = engine.WaitForModelAsync(Model, Limit, CancellationToken.None);
        Task? shutdown = null;

        try
        {
            await provider.Launched.WaitAsync(Limit);
            shutdown = Task.Run(async () => await engine.DisposeAsync());
            await resume.Entered.WaitAsync(Limit);
            await provider.CancellationObserved.WaitAsync(Limit);
            provider.Release(LifecycleResult.Fail("late_failure", "the fake launch failed after the stop"));
            await ended.Task.WaitAsync(Limit);
        }
        finally
        {
            provider.Release(LifecycleResult.Fail("cleanup", "the test released the fake launch"));
            resume.Release();
            if (shutdown is not null) await shutdown.WaitAsync(Limit);
        }

        var result = await waiter.WaitAsync(Limit);
        Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, result.Outcome);
        Assert.Null(result.Detail);
        Assert.Null(engine.SwitchingToward);
    }

    [Fact(Timeout = 60_000)]
    public async Task WaiterCancelledAtSwitchLockIsReportedRouterStopped()
    {
        var provider = new HeldProvider();
        provider.Release(LifecycleResult.Fail("cleanup", "the test released the fake launch"));
        using var handler = new FakeEngineHandler();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = Engine(provider, handler, dispatchHint: () => ended.TrySetResult());
        var scheduling = new Pause();
        engine.BeforeScheduleSwitchForTests = scheduling.Enter;
        var readinessWakes = 0;
        engine.AfterReadyWaitForTests = _ => Interlocked.Increment(ref readinessWakes);
        var owner = Task.Run(() => engine.WaitForModelAsync(Model, HoldLimit, CancellationToken.None));
        Task? shutdown = null;
        EngineRuntime.SwitchResult stopped;
        bool wasWaiting;
        int wakesWhileLocked;

        try
        {
            await scheduling.Entered.WaitAsync(Limit);
            // The owner holds the lock at the scheduling seam. Calling directly leaves this
            // waiter suspended at the contended lock before shutdown can run.
            var waiter = engine.WaitForModelAsync(Model, HoldLimit, CancellationToken.None);
            wasWaiting = !waiter.IsCompleted;
            shutdown = Task.Run(async () => await engine.DisposeAsync());
            await shutdown.WaitAsync(Limit);
            stopped = await waiter.WaitAsync(Limit);
            wakesWhileLocked = Volatile.Read(ref readinessWakes);
        }
        finally
        {
            scheduling.Release();
            if (shutdown is not null) await shutdown.WaitAsync(Limit);
            await owner.WaitAsync(Limit);
        }

        await ended.Task.WaitAsync(Limit);
        Assert.True(wasWaiting);
        Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, stopped.Outcome);
        Assert.Null(stopped.Detail);
        Assert.Equal(0, wakesWhileLocked);
        Assert.Null(engine.SwitchingToward);
    }

    [Fact(Timeout = 60_000)]
    public async Task AClaimThatAcquiresTheSwitchLockAfterStopIsRefused()
    {
        var provider = new HeldProvider();
        provider.Release(LifecycleResult.Fail("unexpected_launch", "the fake launch was called after the stop"));
        using var handler = new FakeEngineHandler();
        await using var engine = Engine(provider, handler);
        var waiterBeforeLock = new Pause();
        var stopBeforeCancellation = new Pause();
        engine.BeforeSwitchWaitForTests = waiterBeforeLock.Enter;
        engine.AfterStopDecisionForTests = stopBeforeCancellation.Enter;
        var claims = 0;
        engine.AfterClaimForTests = () => Interlocked.Increment(ref claims);
        var waiter = Task.Run(() => engine.WaitForModelAsync(Model, Limit, CancellationToken.None));
        Task? shutdown = null;
        EngineRuntime.SwitchResult result;

        try
        {
            await waiterBeforeLock.Entered.WaitAsync(Limit);
            shutdown = Task.Run(async () => await engine.DisposeAsync());
            await stopBeforeCancellation.Entered.WaitAsync(Limit);

            // The caller's linked token is still live. Only the check inside the transition gate can
            // keep this caller from claiming the idle engine.
            waiterBeforeLock.Release();
            result = await waiter.WaitAsync(Limit);
        }
        finally
        {
            waiterBeforeLock.Release();
            stopBeforeCancellation.Release();
            if (shutdown is not null) await shutdown.WaitAsync(Limit);
        }

        Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, result.Outcome);
        Assert.Equal(0, claims);
        Assert.False(provider.Launched.IsCompleted);
        Assert.Null(engine.SwitchingToward);
    }

    [Fact(Timeout = 60_000)]
    public async Task AReadinessProofAlreadyCommittedSurvivesShutdownForItsWaiter()
    {
        var provider = new HeldProvider();
        using var handler = new FakeEngineHandler { LoadedModels = [Model] };
        await using var engine = Engine(provider, handler);
        var resume = new Pause();
        engine.AfterReadyWaitForTests = _ => resume.Enter();
        var waiter = engine.WaitForModelAsync(Model, Limit, CancellationToken.None);

        try
        {
            await provider.Launched.WaitAsync(Limit);
            provider.Release(LifecycleResult.Ok());
            await resume.Entered.WaitAsync(Limit);
            await engine.DisposeAsync();
        }
        finally
        {
            provider.Release(LifecycleResult.Fail("cleanup", "the test released the fake launch"));
            resume.Release();
        }

        Assert.Equal(EngineRuntime.SwitchOutcome.Ready, (await waiter.WaitAsync(Limit)).Outcome);
        var late = await engine.WaitForModelAsync(Model, Limit, CancellationToken.None).WaitAsync(Limit);
        Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, late.Outcome);
    }

    [Fact(Timeout = 60_000)]
    public async Task AProofPublishedByAStoppedSwitchCannotAuthorizeDispatch()
    {
        var provider = new HeldProvider();
        using var handler = new FakeEngineHandler { LoadedModels = [Model] };
        var readyBeforeEnd = new Pause();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new EventRing();
        await using var engine = Engine(provider, handler, feed, () => ended.TrySetResult());
        var proofs = 0;
        engine.BeforeReadinessCommitForTests = readyBeforeEnd.Enter;
        engine.AfterSwitchProofForTests = () => Interlocked.Increment(ref proofs);
        (bool Switching, string? Target) atClaim = default;
        engine.AfterClaimForTests = () => atClaim = (engine.Switching, engine.SwitchingToward);
        var waiter = engine.WaitForModelAsync(Model, Limit, CancellationToken.None);
        EngineRuntime.SwitchResult stopped;
        (bool Switching, string? Target) whileStopped;
        SwitchAttemptRecord sealedWhileHeld;

        try
        {
            await provider.Launched.WaitAsync(Limit);
            provider.Release(LifecycleResult.Ok());
            await readyBeforeEnd.Entered.WaitAsync(Limit);
            await engine.DisposeAsync();
            stopped = await waiter.WaitAsync(Limit);
            whileStopped = (engine.Switching, engine.SwitchingToward);
            sealedWhileHeld = Assert.Single(engine.SwitchHistory).Terminal;
        }
        finally
        {
            provider.Release(LifecycleResult.Fail("cleanup", "the test released the fake launch"));
            readyBeforeEnd.Release();
        }

        await ended.Task.WaitAsync(Limit);
        Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, stopped.Outcome);
        Assert.Equal((true, (string?)Model), atClaim);
        Assert.Equal((true, (string?)Model), whileStopped);
        Assert.False(engine.Switching);
        Assert.Null(engine.SwitchingToward);
        Assert.False(engine.IsReadyFor(Model, null));
        Assert.Equal(0, Volatile.Read(ref proofs));
        Assert.DoesNotContain(feed.Replay(0), e => e.Kind == "engine.switch_completed");
        Assert.Equal(SwitchAttemptOutcome.Cancelled, sealedWhileHeld.Outcome);
        Assert.Equal(SwitchStageOutcome.Incomplete,
            Assert.IsType<SwitchStageObservation>(sealedWhileHeld.GetStage(SwitchStage.Readiness)).Outcome);
        var retained = Assert.Single(engine.SwitchHistory);
        Assert.Same(sealedWhileHeld, retained.Terminal);
        Assert.Null(retained.FirstForwardedAt);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task EngineStopCancellationCannotDisseminateAHeldReadinessWinner(
        bool ready, bool cancelCaller, bool throwFromEvent)
    {
        var provider = new HeldProvider();
        using var handler = new FakeEngineHandler { LoadedModels = ready ? [Model] : ["other"] };
        var heldEvent = new Pause();
        var feed = new EventRing();
        var expectedEvent = ready ? "engine.switch_completed" : "engine.switch_failed";
        feed.Subscribe(e =>
        {
            if (e.Kind != expectedEvent) return;
            heldEvent.Enter();
            if (throwFromEvent) throw new InvalidOperationException("fixture subscriber");
        });
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = Engine(provider, handler, feed, () => ended.TrySetResult());
        using var starterCancel = new CancellationTokenSource();
        using var joinedCancel = new CancellationTokenSource();
        var linkedWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveryWait = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AfterReadyWaitForTests = cancelled =>
        {
            if (cancelled) linkedWake.TrySetResult();
        };
        engine.AfterOutcomeDeliveryWaitForTests = wait => deliveryWait.TrySetResult(wait);

        var starter = engine.WaitForModelAsync(Model, HoldLimit, starterCancel.Token);
        Task<EngineRuntime.SwitchResult>? joined = null;
        try
        {
            await provider.Launched.WaitAsync(Limit);
            joined = engine.WaitForModelAsync(Model, HoldLimit, joinedCancel.Token);
            Assert.False(joined.IsCompleted);

            await starterCancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter.WaitAsync(Limit));

            provider.Release(LifecycleResult.Ok());
            await heldEvent.Entered.WaitAsync(Limit);
            await engine.DisposeAsync().AsTask().WaitAsync(Limit);

            await linkedWake.Task.WaitAsync(Limit);
            var pendingDelivery = await deliveryWait.Task.WaitAsync(Limit);
            Assert.False(pendingDelivery.IsCompleted);
            Assert.False(joined.IsCompleted);
            Assert.False(engine.IsReadyFor(Model, null));
            Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped,
                (await engine.WaitForModelAsync(Model, Limit, CancellationToken.None).WaitAsync(Limit)).Outcome);

            if (cancelCaller)
            {
                await joinedCancel.CancelAsync();
                var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joined.WaitAsync(Limit));
                Assert.Equal(joinedCancel.Token, cancelled.CancellationToken);
            }
        }
        finally
        {
            provider.Release(LifecycleResult.Fail("cleanup", "fixture releases the provider"));
            heldEvent.Release();
        }

        if (!cancelCaller)
        {
            var result = await joined!.WaitAsync(Limit);
            Assert.Equal(ready ? EngineRuntime.SwitchOutcome.Ready : EngineRuntime.SwitchOutcome.Failed,
                result.Outcome);
            Assert.Equal(ready ? null : SwitchReason.RefreshOmits, result.Detail);
            Assert.False(engine.IsReadyFor(Model, null));
        }
        await ended.Task.WaitAsync(Limit);
        Assert.Single(feed.Replay(0), e => e.Kind is "engine.switch_completed" or "engine.switch_failed");
        var retained = Assert.Single(engine.SwitchHistory);
        Assert.Equal(ready ? SwitchAttemptOutcome.Ready : SwitchAttemptOutcome.Failed, retained.Terminal.Outcome);
        Assert.Equal(ready ? SwitchStageOutcome.Completed : SwitchStageOutcome.Incomplete,
            Assert.IsType<SwitchStageObservation>(retained.Terminal.GetStage(SwitchStage.Readiness)).Outcome);
        Assert.Null(retained.FirstForwardedAt);
    }

    [Fact]
    public async Task ExpiredOwnWaitCannotDisseminateAHeldReadinessWinner()
    {
        var provider = new HeldProvider();
        using var handler = new FakeEngineHandler { LoadedModels = [Model] };
        var beforeExpiry = new Pause();
        var heldEvent = new Pause();
        var feed = new EventRing();
        feed.Subscribe(e =>
        {
            if (e.Kind == "engine.switch_completed") heldEvent.Enter();
        });
        await using var engine = Engine(provider, handler, feed);
        var deliveryWait = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AfterOutcomeDeliveryWaitForTests = wait => deliveryWait.TrySetResult(wait);
        engine.SwitchWaitTimeForTests = deadline =>
        {
            beforeExpiry.Enter();
            return deadline;
        };

        var starter = engine.WaitForModelAsync(Model, Timeout.InfiniteTimeSpan, CancellationToken.None);
        Task<EngineRuntime.SwitchResult>? joined = null;
        try
        {
            await provider.Launched.WaitAsync(Limit);
            joined = Task.Run(() => engine.WaitForModelAsync(Model, HoldLimit, CancellationToken.None));
            await beforeExpiry.Entered.WaitAsync(Limit);

            provider.Release(LifecycleResult.Ok());
            await heldEvent.Entered.WaitAsync(Limit);
            beforeExpiry.Release();

            var pendingDelivery = await deliveryWait.Task.WaitAsync(Limit);
            Assert.False(pendingDelivery.IsCompleted);
            Assert.False(joined.IsCompleted);
        }
        finally
        {
            provider.Release(LifecycleResult.Fail("cleanup", "fixture releases the provider"));
            beforeExpiry.Release();
            heldEvent.Release();
        }

        Assert.True((await joined!.WaitAsync(Limit)).IsReady);
        Assert.True((await starter.WaitAsync(Limit)).IsReady);
        Assert.Single(feed.Replay(0), e => e.Kind == "engine.switch_completed");
    }

    [Fact(Timeout = 60_000)]
    public async Task AForeignWaitExpiringAfterStopReportsRouterStopped()
    {
        var provider = new HeldProvider();
        using var handler = new FakeEngineHandler { LoadedModels = [Model] };
        await using var engine = Engine(provider, handler);
        var beforeExpiry = new Pause();
        engine.SwitchWaitTimeForTests = deadline =>
        {
            beforeExpiry.Enter();
            return deadline;
        };
        var owner = engine.WaitForModelAsync(Model, Timeout.InfiniteTimeSpan, CancellationToken.None);
        Task<EngineRuntime.SwitchResult> foreign;
        EngineRuntime.SwitchResult ownResult;
        string? markerBeforeStop;

        try
        {
            await provider.Launched.WaitAsync(Limit);
            foreign = Task.Run(() => engine.WaitForModelAsync("another-model", Limit, CancellationToken.None));
            await beforeExpiry.Entered.WaitAsync(Limit);
            provider.Release(LifecycleResult.Ok());
            ownResult = await owner.WaitAsync(Limit);
            markerBeforeStop = engine.SwitchingToward;
            // Stop the idle runtime before advancing the foreign wait's clock to its deadline.
            await engine.DisposeAsync();
        }
        finally
        {
            provider.Release(LifecycleResult.Fail("cleanup", "the test released the fake launch"));
            beforeExpiry.Release();
        }

        var answer = await foreign.WaitAsync(Limit);
        Assert.Equal(EngineRuntime.SwitchOutcome.Ready, ownResult.Outcome);
        Assert.Null(markerBeforeStop);
        Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, answer.Outcome);
        Assert.Null(answer.Detail);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true, EngineRuntime.SwitchOutcome.Ready, null)]
    [InlineData(false, EngineRuntime.SwitchOutcome.Failed, "launch_failed")]
    public async Task AnExpiredOwnWaitKeepsItsCommittedOutcome(
        bool launchSucceeds, EngineRuntime.SwitchOutcome expected, string? detail)
    {
        var provider = new HeldProvider();
        using var handler = new FakeEngineHandler { LoadedModels = [Model] };
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = Engine(provider, handler, dispatchHint: () => ended.TrySetResult());
        var beforeExpiry = new Pause();
        engine.SwitchWaitTimeForTests = deadline =>
        {
            beforeExpiry.Enter();
            return deadline;
        };
        var owner = Task.Run(() => engine.WaitForModelAsync(Model, Limit, CancellationToken.None));

        try
        {
            await provider.Launched.WaitAsync(Limit);
            await beforeExpiry.Entered.WaitAsync(Limit);
            provider.Release(launchSucceeds
                ? LifecycleResult.Ok()
                : LifecycleResult.Fail("fake_failure", "the fake launch failed before shutdown"));
            await ended.Task.WaitAsync(Limit);
            await engine.DisposeAsync();
        }
        finally
        {
            provider.Release(LifecycleResult.Fail("cleanup", "the test released the fake launch"));
            beforeExpiry.Release();
        }

        var answer = await owner.WaitAsync(Limit);
        Assert.Equal(expected, answer.Outcome);
        Assert.Equal(detail, answer.Detail);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineLocalStopCannotAuthorizeAnAlreadyWaitingTicketToSend(bool stopBeforeSend)
    {
        var provider = new HeldProvider();
        using var handler = new CapturingCompletionHandler { LoadedModels = [Model] };
        var def = new EngineDefinition
        {
            Name = "fake",
            Kind = EngineKind.OpenAiCompatible,
            Endpoint = new Uri("http://localhost/v1"),
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "test" },
            Catalog = new Dictionary<string, EngineModelDefinition> { [Model] = new() { Name = Model } },
        };
        var config = new RouterConfig
        {
            AutoSwitch = true,
            Engines = new Dictionary<string, EngineDefinition> { [def.Name] = def },
            HttpHandlerFactory = _ => handler,
        };
        var heldOutcome = new Pause();
        var heldStop = new Pause();
        var feed = new EventRing();
        feed.Subscribe(e =>
        {
            if (e.Kind == "engine.switch_completed") heldOutcome.Enter();
        });
        await using var router = new RouterCore(config, new AlwaysSwitchScheduler(def.Name), feed,
            TimeProvider.System, lifecycleFactory: _ => provider);
        var engine = router.Engines[def.Name];
        engine.AfterStopDecisionForTests = heldStop.Enter;
        var body = """{"model":"extra","messages":[{"role":"user","content":"held stop"}]}"""u8.ToArray();
        var stopping = Task.CompletedTask;

        try
        {
            var ticket = await router.EnqueueAsync("fixture", new InferenceRequest
            {
                Model = Model,
                Body = body,
                Options = new RouterRequestOptions(),
            }, CancellationToken.None);
            Assert.True(router.TryDispatchOnce());
            await provider.Launched.WaitAsync(Limit);
            var joined = engine.WaitForModelAsync(Model, Timeout.InfiniteTimeSpan, CancellationToken.None);
            Assert.False(joined.IsCompleted);
            provider.Release(LifecycleResult.Ok());
            await heldOutcome.Entered.WaitAsync(Limit);
            Assert.Equal(TicketState.WaitingForModel, ticket.State);
            Assert.Equal(0, handler.WriteInvocations);

            if (stopBeforeSend)
            {
                // Keep the caller and router live, and pause engine disposal before it can close HTTP.
                stopping = Task.Run(async () => await engine.DisposeAsync(), CancellationToken.None);
                await heldStop.Entered.WaitAsync(Limit);
                Assert.False(stopping.IsCompleted);
                Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped,
                    (await engine.WaitForModelAsync(Model, Limit, CancellationToken.None)).Outcome);
            }

            heldOutcome.Release();
            Assert.True((await joined.WaitAsync(Limit)).IsReady);
            var failure = await Record.ExceptionAsync(() => ticket.CompletionTask.WaitAsync(Limit));
            Assert.IsNotType<TimeoutException>(failure);
            Assert.Equal(stopBeforeSend ? 0 : 1, handler.WriteInvocations);
            if (stopBeforeSend)
            {
                Assert.False(stopping.IsCompleted);
                Assert.Null(handler.WrittenBody);
                Assert.IsType<RouterRequestException>(failure);
            }
            else
            {
                Assert.Null(failure);
                Assert.Equal(body, Assert.IsType<byte[]>(handler.WrittenBody));
            }
        }
        finally
        {
            provider.Release(LifecycleResult.Fail("cleanup", "the test released the fake launch"));
            heldOutcome.Release();
            heldStop.Release();
            await stopping.WaitAsync(Limit);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JoinedTicketsKeepTheirOwnReadinessVerdictsAndRequireAnOpenEngineToSend(bool stopBeforeSend)
    {
        const long measuredWindow = 32_768;
        var profile = new ServingProfile { Name = "wide", MaxContext = measuredWindow };
        var provider = new HeldProvider();
        using var handler = new CapturingCompletionHandler { LoadedModels = [Model], NCtx = measuredWindow };
        var def = new EngineDefinition
        {
            Name = "fake",
            Kind = EngineKind.OpenAiCompatible,
            Endpoint = new Uri("http://localhost/v1"),
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "test" },
            Health = new HealthProbeDefinition { Path = "/props" },
            Catalog = new Dictionary<string, EngineModelDefinition>
            {
                [Model] = new() { Name = Model, ServingProfiles = [profile] },
            },
        };
        var config = new RouterConfig
        {
            AutoSwitch = true,
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 2, SwitchWaitTimeoutMs = 30_000 },
            Engines = new Dictionary<string, EngineDefinition> { [def.Name] = def },
            HttpHandlerFactory = _ => handler,
        };
        var heldOutcome = new Pause();
        var heldStop = new Pause();
        var feed = new EventRing();
        feed.Subscribe(e =>
        {
            if (e.Kind == "engine.switch_completed") heldOutcome.Enter();
        });
        // Fix the selection to the same tier so both real ticket continuations exercise one claim.
        await using var router = new RouterCore(config, new AlwaysSwitchScheduler(def.Name, profile.Name), feed,
            TimeProvider.System, lifecycleFactory: _ => provider);
        var engine = router.Engines[def.Name];
        var firstWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiters = 0;
        engine.SwitchWaitTimeForTests = deadline =>
        {
            // The launch is still held. Each caller has selected the installed state and released the switch lock.
            var arrival = Interlocked.Increment(ref waiters);
            if (arrival == 1) firstWaiting.TrySetResult();
            if (arrival == 2) secondWaiting.TrySetResult();
            return deadline - HoldLimit;
        };
        engine.AfterStopDecisionForTests = heldStop.Enter;
        var normalWakes = 0;
        engine.AfterReadyWaitForTests = cancelled =>
        {
            if (!cancelled) Interlocked.Increment(ref normalWakes);
        };
        var acceptedClaims = 0;
        engine.AfterForwardAdmissionForTests = () =>
        {
            Interlocked.Increment(ref acceptedClaims);
            return Task.CompletedTask;
        };
        var verdicts = new ConcurrentQueue<(string TicketId, EngineRuntime.SwitchOutcome Outcome)>();
        router.AfterModelReadinessForTests = (id, outcome) => verdicts.Enqueue((id, outcome));
        using var caller = new CancellationTokenSource();
        using var starterCancel = new CancellationTokenSource();
        var body = """{"model":"extra","messages":[{"role":"user","content":"joined switch"}]}"""u8.ToArray();
        ValueTask<RouterTicket> Enqueue(string threadId, long minimum) => router.EnqueueAsync(threadId,
            new InferenceRequest { Model = Model, Body = body, Options = new() { RequiredContext = minimum } },
            caller.Token);

        var stopping = Task.CompletedTask;
        try
        {
            var starter = engine.EnsureLoadedAsync(Model, profile, Timeout.InfiniteTimeSpan,
                queue: null, ct: starterCancel.Token);
            await provider.Launched.WaitAsync(Limit);
            var fitting = await Enqueue("fitting", measuredWindow);
            Assert.True(router.TryDispatchOnce());
            await firstWaiting.Task.WaitAsync(Limit);
            var stricter = await Enqueue("stricter", measuredWindow + 1);
            Assert.True(router.TryDispatchOnce());
            await secondWaiting.Task.WaitAsync(Limit);

            // Retire the starter's wait. The transaction belongs to the engine and both tickets have joined it.
            await starterCancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter.WaitAsync(Limit));
            provider.Release(LifecycleResult.Ok());
            await heldOutcome.Entered.WaitAsync(Limit);
            Assert.Equal(measuredWindow, engine.Capability.Serving.WindowFor(Model));
            if (stopBeforeSend)
            {
                // Hold disposal before linked cancellation and HTTP close. The outcome wakes both callers.
                stopping = Task.Run(async () => await engine.DisposeAsync(), CancellationToken.None);
                await heldStop.Entered.WaitAsync(Limit);
                Assert.Equal(0L, engine.Capability.Serving.WindowFor(Model));
            }

            Assert.Equal(TicketState.WaitingForModel, fitting.State);
            Assert.Equal(TicketState.WaitingForModel, stricter.State);
            Assert.Empty(verdicts);
            Assert.Equal(0, Volatile.Read(ref acceptedClaims));
            Assert.Equal(0, handler.WriteInvocations);
            heldOutcome.Release();

            var fittingFailure = await Record.ExceptionAsync(() => fitting.CompletionTask.WaitAsync(Limit));
            var stricterFailure = await Record.ExceptionAsync(() => stricter.CompletionTask.WaitAsync(Limit));
            Assert.Collection(verdicts.OrderBy(verdict => verdict.TicketId == stricter.Id),
                verdict => Assert.Equal((fitting.Id, EngineRuntime.SwitchOutcome.Ready), verdict),
                verdict => Assert.Equal((stricter.Id, stopBeforeSend ? EngineRuntime.SwitchOutcome.RouterStopped
                    : EngineRuntime.SwitchOutcome.Failed), verdict));
            Assert.Equal(2, Volatile.Read(ref normalWakes));
            Assert.Equal(stopBeforeSend ? 0 : 1, Volatile.Read(ref acceptedClaims));
            Assert.Equal(stopBeforeSend ? 0 : 1, handler.WriteInvocations);
            Assert.False(caller.IsCancellationRequested);
            if (stopBeforeSend)
            {
                Assert.False(stopping.IsCompleted);
                Assert.Equal("cancelled", Assert.IsType<RouterRequestException>(fittingFailure).Code);
                Assert.Equal("cancelled", Assert.IsType<RouterRequestException>(stricterFailure).Code);
                Assert.Null(handler.WrittenBody);
            }
            else
            {
                Assert.Null(fittingFailure);
                var refusal = Assert.IsType<RouterRequestException>(stricterFailure);
                Assert.Equal("switch_failed", refusal.Code);
                Assert.Contains($"({SwitchReason.WindowTooSmall})", refusal.Message, StringComparison.Ordinal);
                Assert.Equal(body, Assert.IsType<byte[]>(handler.WrittenBody));
            }

            var events = feed.Replay(0);
            Assert.Single(events, e => e.Kind == "engine.switch_started");
            var terminal = Assert.Single(events, e => e.Kind is "engine.switch_completed" or "engine.switch_failed");
            Assert.Equal("engine.switch_completed", terminal.Kind);
            Assert.Equal("profile=wide", terminal.Reason);
        }
        finally
        {
            provider.Release(LifecycleResult.Fail("cleanup", "the test released the fake launch"));
            heldOutcome.Release();
            heldStop.Release();
            await stopping.WaitAsync(Limit);
        }
    }

    private sealed class AlwaysSwitchScheduler(string engine, string? profile = null) : IRequestScheduler
    {
        public SchedulerDecision SelectNext(QueuedRequest request, SchedulerView view, RouterConfig config) =>
            new() { Engine = engine, SwitchRequired = true, TargetProfile = profile };
    }

    private sealed class CapturingCompletionHandler : FakeEngineHandler
    {
        private int _writeInvocations;
        public int WriteInvocations => Volatile.Read(ref _writeInvocations);
        public byte[]? WrittenBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _writeInvocations);
                Assert.False(cancellationToken.IsCancellationRequested);
                WrittenBody = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static EngineRuntime Engine(
        HeldProvider provider, HttpMessageHandler handler, EventRing? feed = null, Action? dispatchHint = null)
    {
        var def = new EngineDefinition
        {
            Name = "fake",
            Kind = EngineKind.OpenAiCompatible,
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "test" },
            Catalog = new Dictionary<string, EngineModelDefinition> { [Model] = new() { Name = Model } },
        };
        var config = new RouterConfig { Engines = new Dictionary<string, EngineDefinition> { [def.Name] = def } };
        return new EngineRuntime(def, config, feed ?? new EventRing(), handler, lifecycle: provider, dispatchHint: dispatchHint);
    }

    private sealed class Pause
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public void Enter()
        {
            _entered.TrySetResult();
            _release.Task.WaitAsync(HoldLimit).GetAwaiter().GetResult();
        }
    }

    private sealed class HeldProvider(Action? duringCancellation = null) : IEngineLifecycleProvider
    {
        private readonly TaskCompletionSource<LifecycleResult> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => "docker";
        public Task Launched => _launched.Task;
        public Task CancellationObserved => _cancellationObserved.Task;
        public void Release(LifecycleResult result) => _release.TrySetResult(result);

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok());

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok());

        public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            using var registration = ct.Register(() =>
            {
                _cancellationObserved.TrySetResult();
                duringCancellation?.Invoke();
            });
            _launched.TrySetResult();
            return await _release.Task.WaitAsync(HoldLimit, CancellationToken.None).ConfigureAwait(false);
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, Limit);
    }
}
