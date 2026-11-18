using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class ShutdownStopCauseTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private const string Model = "extra";

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

        try
        {
            await provider.Launched.WaitAsync(Limit);
            await engine.DisposeAsync();
            await resume.Entered.WaitAsync(Limit);
            await provider.CancellationObserved.WaitAsync(Limit);
            provider.Release(LifecycleResult.Fail("late_failure", "the fake launch failed after the stop"));
            await ended.Task.WaitAsync(Limit);
        }
        finally
        {
            provider.Release(LifecycleResult.Fail("cleanup", "the test released the fake launch"));
            resume.Release();
        }

        var result = await waiter.WaitAsync(Limit);
        Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, result.Outcome);
        Assert.Null(result.Detail);
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
        feed.Subscribe(e =>
        {
            if (e.Kind is "engine.switch_completed") readyBeforeEnd.Enter();
        });
        await using var engine = Engine(provider, handler, feed, () => ended.TrySetResult());
        (bool Switching, string? Target) atClaim = default;
        engine.AfterClaimForTests = () => atClaim = (engine.Switching, engine.SwitchingToward);
        var waiter = engine.WaitForModelAsync(Model, Limit, CancellationToken.None);
        EngineRuntime.SwitchResult stopped;
        (bool Switching, string? Target) whileStopped;

        try
        {
            await provider.Launched.WaitAsync(Limit);
            provider.Release(LifecycleResult.Ok());
            await readyBeforeEnd.Entered.WaitAsync(Limit);
            await engine.DisposeAsync();
            stopped = await waiter.WaitAsync(Limit);
            whileStopped = (engine.Switching, engine.SwitchingToward);
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

    private static EngineRuntime Engine(
        HeldProvider provider, HttpMessageHandler handler, EventRing? feed = null, Action? dispatchHint = null)
    {
        var def = new EngineDefinition
        {
            Name = "fake",
            Kind = EngineKind.OpenAiCompatible,
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
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
            _release.Task.WaitAsync(Limit).GetAwaiter().GetResult();
        }
    }

    private sealed class HeldProvider : IEngineLifecycleProvider
    {
        private readonly TaskCompletionSource<LifecycleResult> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => "held";
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
            using var registration = ct.Register(() => _cancellationObserved.TrySetResult());
            _launched.TrySetResult();
            return await _release.Task.WaitAsync(Limit, CancellationToken.None).ConfigureAwait(false);
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, Limit);
    }
}
