using System.Net;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class ReadinessDeadlineTests
{
    [Fact]
    public async Task NamedTrustStateCapSettlesWhileTheOnlyStateReadIsHeld()
    {
        var clock = new ManualClock();
        var feed = new EventRing();
        var held = new HeldRead<byte[]>("""{"n_ctx":32768,"total_slots":2}"""u8.ToArray());
        using var content = new HeldJsonContent(held);
        using var handler = new TrustReadinessHandler(content);
        var tier = new ServingProfile { Name = "named", MaxContext = 32768, MaxSlots = 1 };
        await using var engine = new EngineRuntime(TrustEngine(tier), new RouterConfig(), feed,
            handler, lifecycle: new TrustReadinessProvider())
        {
            ReadinessClockForTests = clock,
        };

        var settlement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalCommits = 0;
        var proofPublications = 0;
        engine.AfterSwitchProofForTests = () => Interlocked.Increment(ref proofPublications);
        engine.AfterEndCommitForTests = gateHeld =>
        {
            Assert.False(gateHeld);
            Interlocked.Increment(ref terminalCommits);
            settlement.TrySetResult();
        };

        try
        {
            var wait = engine.WaitForProfileAsync("model", tier, Watchdog, CancellationToken.None);
            await held.Entered.Task.WaitAsync(Watchdog);
            Assert.Equal(1, handler.ModelsCalls);
            Assert.Equal(1, handler.StateCalls);
            clock.AdvanceTo(39);
            Assert.False(wait.IsCompleted);

            // The state cap is 40 ms and the global readiness budget is 100 ms.
            clock.AdvanceTo(40);
            var result = await wait.WaitAsync(Watchdog);
            Assert.Equal(EngineRuntime.SwitchOutcome.Failed, result.Outcome);
            Assert.Equal(SwitchReason.WindowTooSmall, result.Detail);
            await settlement.Task.WaitAsync(Watchdog);
            Assert.Equal(1, Volatile.Read(ref terminalCommits));
            Assert.Equal(0, Volatile.Read(ref proofPublications));
            Assert.Equal(1, handler.StateCalls);
            Assert.False(held.Released.Task.IsCompleted);
            Assert.False(engine.IsReadyFor("model", tier));
            Assert.Null(engine.SwitchingToward);

            var retired = engine.Capability;
            Assert.Equal(0L, retired.Serving.WindowFor("model"));
            var beforeLate = feed.Replay(0).ToArray();
            Assert.Single(beforeLate, e => e.Kind == "engine.switch_failed");
            Assert.DoesNotContain(beforeLate, e => e.Kind is "engine.switch_completed" or "engine.profile");

            held.Release.TrySetResult();
            await content.Disposed.Task.WaitAsync(Watchdog);
            Assert.Same(retired, engine.Capability);
            Assert.Equal(retired.Version, engine.Capability.Version);
            Assert.Equal(beforeLate, feed.Replay(0).ToArray());
            Assert.Equal(1, Volatile.Read(ref terminalCommits));
            Assert.Equal(0, Volatile.Read(ref proofPublications));
            Assert.Equal(1, handler.StateCalls);
        }
        finally
        {
            held.Release.TrySetResult();
            if (held.Entered.Task.IsCompleted) await content.Disposed.Task.WaitAsync(Watchdog);
        }
    }

    [Fact]
    public async Task TrueModelOnlyTrustPathUsesFreshModelsWithUnknownCapacity()
    {
        var clock = new ManualClock();
        var feed = new EventRing();
        var held = new HeldRead<byte[]>("""{"n_ctx":32768,"total_slots":2}"""u8.ToArray());
        using var content = new HeldJsonContent(held);
        using var handler = new TrustReadinessHandler(content);
        var proofPublications = 0;
        await using var engine = new EngineRuntime(TrustEngine(tier: null), new RouterConfig(), feed,
            handler, lifecycle: new TrustReadinessProvider())
        {
            ReadinessClockForTests = clock,
            AfterSwitchProofForTests = () => Interlocked.Increment(ref proofPublications),
        };

        try
        {
            var result = await engine.WaitForModelAsync("model", Watchdog, CancellationToken.None).WaitAsync(Watchdog);
            Assert.True(result.IsReady);
            Assert.Equal(1, handler.ModelsCalls);
            Assert.Equal(0, handler.StateCalls);
            Assert.Equal(1, Volatile.Read(ref proofPublications));
            Assert.Equal(0L, engine.Capability.Serving.WindowFor("model"));
            Assert.True(engine.IsReadyFor("model", null));
            Assert.Single(feed.Replay(0), e => e.Kind == "engine.switch_completed");
        }
        finally
        {
            held.Release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task HeldRuntimeBodyCannotOutliveTheReadinessAttempt(bool holdModels, bool proveHealth)
    {
        var clock = new ManualClock();
        var feed = new EventRing();
        var bytes = holdModels
            ? """{"data":[{"id":"model"}]}"""u8.ToArray()
            : """{"n_ctx":32768,"total_slots":2}"""u8.ToArray();
        var held = new HeldRead<byte[]>(bytes);
        using var content = new HeldJsonContent(held);
        using var handler = new TrustReadinessHandler(content, holdModels);
        var proofs = 0;
        await using var engine = new EngineRuntime(TrustEngine(tier: null), new RouterConfig(), feed,
            handler, lifecycle: new TrustReadinessProvider(proveHealth))
        {
            ReadinessClockForTests = clock,
            AfterSwitchProofForTests = () => Interlocked.Increment(ref proofs),
        };

        try
        {
            var wait = engine.WaitForModelAsync("model", Watchdog, CancellationToken.None);
            await held.Entered.Task.WaitAsync(Watchdog);
            clock.AdvanceTo(99);
            Assert.False(wait.IsCompleted);

            clock.AdvanceTo(100);
            var answer = await wait.WaitAsync(Watchdog);
            Assert.Equal(EngineRuntime.SwitchOutcome.Failed, answer.Outcome);
            Assert.Equal(SwitchReason.HealthTimeout, answer.Detail);
            Assert.False(content.Disposed.Task.IsCompleted);
            Assert.Equal(1, handler.ModelsCalls);
            Assert.Equal(holdModels ? 0 : 1, handler.StateCalls);
            Assert.Equal(0, Volatile.Read(ref proofs));

            var retired = engine.Capability;
            Assert.False(retired.Healthy);
            Assert.Null(retired.SwitchingToward);
            var events = feed.Replay(0).ToArray();
            Assert.Single(events, e => e.Kind == "engine.switch_failed");

            held.Release.TrySetResult();
            await content.Disposed.Task.WaitAsync(Watchdog);
            Assert.Same(retired, engine.Capability);
            Assert.Equal(events, feed.Replay(0).ToArray());
            Assert.Equal(0, Volatile.Read(ref proofs));
        }
        finally
        {
            held.Release.TrySetResult();
            if (held.Entered.Task.IsCompleted) await content.Disposed.Task.WaitAsync(Watchdog);
        }
    }

    [Theory]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public async Task RuntimePublicationRechecksTheDeadlineWithoutATimerWake(int atCommit, bool expectedReady)
    {
        var clock = new ManualClock();
        var feed = new EventRing();
        using var handler = new FakeEngineHandler { LoadedModels = ["model"] };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proofs = 0;
        await using var engine = new EngineRuntime(TrustEngine(tier: null), new RouterConfig(), feed,
            handler, lifecycle: new TrustReadinessProvider())
        {
            ReadinessClockForTests = clock,
            AfterSwitchProofForTests = () => Interlocked.Increment(ref proofs),
            BeforeReadinessCommitForTests = () =>
            {
                entered.TrySetResult();
                release.Task.WaitAsync(Watchdog).GetAwaiter().GetResult();
            },
        };

        var wait = engine.WaitForModelAsync("model", Watchdog, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            Assert.Equal(0, Volatile.Read(ref proofs));
            clock.AdvanceTo(atCommit, fireTimers: false);
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await wait.WaitAsync(Watchdog);
        Assert.Equal(expectedReady, result.IsReady);
        Assert.Equal(expectedReady ? null : SwitchReason.HealthTimeout, result.Detail);
        Assert.Equal(expectedReady ? 1 : 0, Volatile.Read(ref proofs));
        Assert.Equal(expectedReady, engine.Capability.Healthy);
        Assert.Single(feed.Replay(0), e => e.Kind is "engine.switch_completed" or "engine.switch_failed");
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    [InlineData(true, -10)]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    [InlineData(false, -10)]
    public async Task NonpositiveLegacyReadinessBudgetsExpireBeforeAnyRead(bool proveHealth, int timeoutMs)
    {
        var clock = new ManualClock();
        var feed = new EventRing();
        using var handler = new FakeEngineHandler { LoadedModels = ["model"] };
        await using var engine = new EngineRuntime(TrustEngine(tier: null), new RouterConfig(), feed,
            handler, lifecycle: new TrustReadinessProvider(proveHealth, timeoutMs))
        {
            ReadinessClockForTests = clock,
        };

        var result = await engine.WaitForModelAsync("model", Watchdog, CancellationToken.None).WaitAsync(Watchdog);
        Assert.Equal(EngineRuntime.SwitchOutcome.Failed, result.Outcome);
        Assert.Equal(SwitchReason.HealthTimeout, result.Detail);
        Assert.Empty(handler.SeenRoutes);
        Assert.Single(feed.Replay(0), e => e.Kind == "engine.switch_failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverallExpiryOrStopCanWinAfterAStepCapBeforeSettlement(bool stopFirst)
    {
        var clock = new ManualClock();
        var feed = new EventRing();
        var held = new HeldRead<byte[]>("""{"n_ctx":32768,"total_slots":2}"""u8.ToArray());
        using var content = new HeldJsonContent(held);
        using var handler = new TrustReadinessHandler(content);
        var tier = new ServingProfile { Name = "named", MaxContext = 32_768 };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        var proofs = 0;
        await using var engine = new EngineRuntime(TrustEngine(tier), new RouterConfig(), feed,
            handler, lifecycle: new TrustReadinessProvider())
        {
            ReadinessClockForTests = clock,
            AfterSwitchProofForTests = () => Interlocked.Increment(ref proofs),
            BeforeReadinessCommitForTests = () =>
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None)
                    .GetAwaiter().GetResult();
            },
            AfterEndCommitForTests = gateHeld =>
            {
                Assert.False(gateHeld);
                Interlocked.Increment(ref commits);
                settled.TrySetResult();
            },
        };

        var capDelivery = Task.CompletedTask;
        try
        {
            var wait = engine.WaitForProfileAsync("model", tier, Timeout.InfiniteTimeSpan, CancellationToken.None);
            await held.Entered.Task.WaitAsync(Watchdog);

            // Timer delivery can resume readiness inline and hold the thread at the commit hook.
            capDelivery = Task.Run(() => clock.AdvanceTo(40), CancellationToken.None);
            await entered.Task.WaitAsync(Watchdog);
            await held.Cancelled.Task.WaitAsync(Watchdog);
            Assert.False(wait.IsCompleted);
            Assert.False(settled.Task.IsCompleted);
            Assert.False(content.Disposed.Task.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref proofs));

            clock.AdvanceTo(100, fireTimers: false);
            if (stopFirst)
            {
                await engine.DisposeAsync().AsTask().WaitAsync(Watchdog);
                Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, (await wait.WaitAsync(Watchdog)).Outcome);
            }
            else
            {
                Assert.False(wait.IsCompleted);
            }

            Assert.False(settled.Task.IsCompleted);
            release.TrySetResult();
            await capDelivery.WaitAsync(Watchdog);

            var answer = await wait.WaitAsync(Watchdog);
            Assert.Equal(stopFirst ? EngineRuntime.SwitchOutcome.RouterStopped : EngineRuntime.SwitchOutcome.Failed,
                answer.Outcome);
            Assert.Equal(stopFirst ? null : SwitchReason.HealthTimeout, answer.Detail);
            await settled.Task.WaitAsync(Watchdog);

            Assert.False(content.Disposed.Task.IsCompleted);
            Assert.Equal(1, handler.StateCalls);
            Assert.Equal(1, Volatile.Read(ref commits));
            Assert.Equal(0, Volatile.Read(ref proofs));
            var retired = engine.Capability;
            Assert.False(retired.Healthy);
            Assert.Null(retired.SwitchingToward);
            var events = feed.Replay(0).ToArray();
            Assert.Equal(stopFirst ? 0 : 1,
                events.Count(e => e.Kind is "engine.switch_completed" or "engine.switch_failed"));

            held.Release.TrySetResult();
            await content.Disposed.Task.WaitAsync(Watchdog);
            Assert.Same(retired, engine.Capability);
            Assert.Equal(events, feed.Replay(0).ToArray());
            Assert.Equal(1, Volatile.Read(ref commits));
            Assert.Equal(0, Volatile.Read(ref proofs));
        }
        finally
        {
            release.TrySetResult();
            held.Release.TrySetResult();
            var bodyCleanup = held.Entered.Task.IsCompleted ? content.Disposed.Task : Task.CompletedTask;
            await Task.WhenAll(capDelivery, bodyCleanup).WaitAsync(Watchdog);
        }
    }

    [Fact]
    public async Task AFreshAttemptCanPublishWhileItsRetiredPredecessorOnlyDisposesLate()
    {
        var clock = new ManualClock();
        var feed = new EventRing();
        var oldBody = new HeldRead<byte[]>("""{"n_ctx":65536,"total_slots":8}"""u8.ToArray());
        using var content = new HeldJsonContent(oldBody);
        using var handler = new TrustReadinessHandler(content, freshStateOnRetry: true);
        var tier = new ServingProfile { Name = "named", MaxContext = 32_768 };
        var proofs = 0;
        await using var engine = new EngineRuntime(TrustEngine(tier), new RouterConfig(), feed,
            handler, lifecycle: new TrustReadinessProvider())
        {
            ReadinessClockForTests = clock,
            AfterSwitchProofForTests = () => Interlocked.Increment(ref proofs),
        };

        try
        {
            var first = engine.WaitForProfileAsync("model", tier, Watchdog, CancellationToken.None);
            await oldBody.Entered.Task.WaitAsync(Watchdog);
            clock.AdvanceTo(40);
            var refusal = await first.WaitAsync(Watchdog);
            Assert.Equal(SwitchReason.WindowTooSmall, refusal.Detail);
            await oldBody.Cancelled.Task.WaitAsync(Watchdog);
            Assert.Equal(0, Volatile.Read(ref proofs));
            Assert.False(content.Disposed.Task.IsCompleted);

            var fresh = await engine.WaitForProfileAsync("model", tier, Watchdog, CancellationToken.None)
                .WaitAsync(Watchdog);
            Assert.True(fresh.IsReady);
            Assert.Equal(2, handler.ModelsCalls);
            Assert.Equal(2, handler.StateCalls);
            Assert.Equal(1, Volatile.Read(ref proofs));
            Assert.False(content.Disposed.Task.IsCompleted);
            var proved = engine.Capability;
            Assert.Equal(32_768L, proved.Serving.WindowFor("model"));
            Assert.Equal(2, proved.Serving.TotalSlots);
            var events = feed.Replay(0).ToArray();
            Assert.Single(events, e => e.Kind == "engine.switch_failed");
            Assert.Single(events, e => e.Kind == "engine.switch_completed");

            oldBody.Release.TrySetResult();
            await content.Disposed.Task.WaitAsync(Watchdog);
            Assert.Same(proved, engine.Capability);
            Assert.Equal(events, feed.Replay(0).ToArray());
            Assert.Equal(1, Volatile.Read(ref proofs));
        }
        finally
        {
            oldBody.Release.TrySetResult();
            if (oldBody.Entered.Task.IsCompleted) await content.Disposed.Task.WaitAsync(Watchdog);
        }
    }

    [Fact]
    public void ConfiguredMinusOneReachesBothActualReadinessProvidersAsAnExhaustedBudget()
    {
        var config = RouterConfigLoader.Load("""
            [engine.selector]
            kind = "openai-compatible"
            endpoint = "http://localhost:18001/v1"
            [engine.selector.catalog.model]
            select = { exec = "/bin/true", wait_health = false, wait_timeout_ms = -1 }

            [engine.native]
            kind = "openai-compatible"
            endpoint = "http://localhost:18002/v1"
            [engine.native.docker]
            image = "example/fixture:1"
            router_id = "fixture-router"
            wait_timeout_ms = -1
            [engine.native.catalog.model]
            """);
        var selector = config.Engines["selector"];
        var native = config.Engines["native"];
        var selectorProvider = new DelegatedExecProvider();
        using var handler = new FakeEngineHandler();
        using var nativeProvider = new DockerLifecycleProvider(native.Docker!, native.Name, handler);

        var selectorPolicy = selectorProvider.Readiness(new(selector.Name, "model", null, selector.Catalog["model"]));
        var nativePolicy = nativeProvider.Readiness(new(native.Name, "model", null, native.Catalog["model"]));
        Assert.False(selectorPolicy.ProveHealth);
        Assert.True(nativePolicy.ProveHealth);
        Assert.Equal(Timeout.InfiniteTimeSpan, selectorPolicy.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, nativePolicy.Timeout);

        var clock = new ManualClock();
        using var selectorDeadline = ReadinessDeadline.Start(clock, selectorPolicy.Timeout, CancellationToken.None);
        using var nativeDeadline = ReadinessDeadline.Start(clock, nativePolicy.Timeout, CancellationToken.None);
        Assert.Equal(ReadinessStepOutcome.Expired, selectorDeadline.OutcomeForSettlement);
        Assert.Equal(ReadinessStepOutcome.Expired, nativeDeadline.OutcomeForSettlement);
        Assert.Empty(handler.SeenRoutes);
    }

    [Theory]
    [InlineData("other", 200, false, 99, false, SwitchReason.RefreshOmits)]
    [InlineData("other", 200, false, 100, false, SwitchReason.RefreshOmits)]
    [InlineData("model", 503, false, 99, false, SwitchReason.RefreshUnanswered)]
    [InlineData("model", 503, false, 100, false, SwitchReason.RefreshUnanswered)]
    [InlineData("model", 200, true, 99, false, SwitchReason.WindowTooSmall)]
    [InlineData("model", 200, true, 100, false, SwitchReason.WindowTooSmall)]
    [InlineData("other", 200, false, 100, true, null)]
    public async Task CompletedTrustDiagnosisSurvivesGuardedDeadlineEquality(
        string reported, int modelsStatus, bool named, int atCommit, bool stopFirst, string? expectedReason)
    {
        var clock = new ManualClock();
        var feed = new EventRing();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proofs = 0;
        var tier = named ? new ServingProfile { Name = "named", MaxContext = 32_768 } : null;
        using var handler = new FakeEngineHandler { LoadedModels = [reported], ModelsStatus = modelsStatus, NCtx = 8_192 };
        await using var engine = new EngineRuntime(TrustEngine(tier), new RouterConfig(), feed, handler,
            lifecycle: new TrustReadinessProvider())
        {
            ReadinessClockForTests = clock,
            AfterSwitchProofForTests = () => Interlocked.Increment(ref proofs),
            BeforeReadinessCommitForTests = () =>
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).GetAwaiter().GetResult();
            },
            AfterEndCommitForTests = _ => settled.TrySetResult(),
        };

        var wait = tier is null
            ? engine.WaitForModelAsync("model", Timeout.InfiniteTimeSpan, CancellationToken.None)
            : engine.WaitForProfileAsync("model", tier, Timeout.InfiniteTimeSpan, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            Assert.False(wait.IsCompleted);
            Assert.Equal(named ? 2 : 1, handler.SeenRoutes.Count);
            clock.AdvanceTo(atCommit, fireTimers: false);
            if (stopFirst)
            {
                await engine.DisposeAsync().AsTask().WaitAsync(Watchdog);
                Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, (await wait.WaitAsync(Watchdog)).Outcome);
                Assert.False(settled.Task.IsCompleted);
            }
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await wait.WaitAsync(Watchdog);
        await settled.Task.WaitAsync(Watchdog);
        Assert.Equal(stopFirst ? EngineRuntime.SwitchOutcome.RouterStopped : EngineRuntime.SwitchOutcome.Failed,
            result.Outcome);
        Assert.Equal(expectedReason, result.Detail);
        Assert.Equal(0, Volatile.Read(ref proofs));
        Assert.Null(engine.Capability.SwitchingToward);
        Assert.Equal(stopFirst ? 0 : 1, feed.Replay(0).Count(e => e.Kind == "engine.switch_failed"));
        Assert.DoesNotContain(feed.Replay(0), e => e.Kind == "engine.switch_completed");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrustDiagnosisIgnoresMismatchFirstReturnedAfterTheDeadline(bool holdModels)
    {
        var clock = new ManualClock();
        var feed = new EventRing();
        var bytes = holdModels
            ? """{"data":[{"id":"other"}]}"""u8.ToArray()
            : """{"n_ctx":8192,"total_slots":2}"""u8.ToArray();
        var held = new HeldRead<byte[]>(bytes);
        using var content = new HeldJsonContent(held);
        using var handler = new TrustReadinessHandler(content, holdModels);
        var tier = holdModels ? null : new ServingProfile { Name = "named", MaxContext = 32_768 };
        var proofs = 0;
        await using var engine = new EngineRuntime(TrustEngine(tier), new RouterConfig(), feed,
            handler, lifecycle: new TrustReadinessProvider())
        {
            ReadinessClockForTests = clock,
            AfterSwitchProofForTests = () => Interlocked.Increment(ref proofs),
        };
        var wait = tier is null
            ? engine.WaitForModelAsync("model", Timeout.InfiniteTimeSpan, CancellationToken.None)
            : engine.WaitForProfileAsync("model", tier, Timeout.InfiniteTimeSpan, CancellationToken.None);
        try
        {
            await held.Entered.Task.WaitAsync(Watchdog);
            clock.AdvanceTo(101, fireTimers: false);
            Assert.False(wait.IsCompleted);
            held.Release.TrySetResult();

            var result = await wait.WaitAsync(Watchdog);
            await content.Disposed.Task.WaitAsync(Watchdog);
            Assert.Equal(EngineRuntime.SwitchOutcome.Failed, result.Outcome);
            Assert.Equal(SwitchReason.HealthTimeout, result.Detail);
            Assert.Equal(1, handler.ModelsCalls);
            Assert.Equal(holdModels ? 0 : 1, handler.StateCalls);
            Assert.Equal(0, Volatile.Read(ref proofs));
            Assert.False(engine.Capability.Healthy);
            Assert.Null(engine.Capability.SwitchingToward);
            Assert.Single(feed.Replay(0), e => e.Kind == "engine.switch_failed");
            Assert.DoesNotContain(feed.Replay(0), e => e.Kind == "engine.switch_completed");
        }
        finally
        {
            held.Release.TrySetResult();
            if (held.Entered.Task.IsCompleted) await content.Disposed.Task.WaitAsync(Watchdog);
        }
    }

    private static EngineDefinition TrustEngine(ServingProfile? tier) => new()
    {
        Name = "fixture",
        Endpoint = new Uri("http://localhost/v1"),
        Lifecycle = EngineLifecycleKind.Docker,
        Docker = new DockerLifecycleConfig { Image = "example/fixture:1", RouterId = "fixture-router" },
        Health = new HealthProbeDefinition { Path = "/props", TimeoutMs = 40 },
        Catalog = new Dictionary<string, EngineModelDefinition>(StringComparer.Ordinal)
        {
            ["model"] = new() { Name = "model", ServingProfiles = tier is null ? [] : [tier] },
        },
    };

    private sealed class TrustReadinessHandler(HttpContent held, bool holdModels = false, bool freshStateOnRetry = false) : HttpMessageHandler
    {
        private int _modelsCalls;
        private int _stateCalls;
        public int ModelsCalls => Volatile.Read(ref _modelsCalls);
        public int StateCalls => Volatile.Read(ref _stateCalls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _modelsCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = holdModels ? held : new StringContent("""{"data":[{"id":"model"}]}"""),
                });
            }
            Assert.EndsWith("/props", path, StringComparison.Ordinal);
            var call = Interlocked.Increment(ref _stateCalls);
            if (call == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = held });

            Assert.True(freshStateOnRetry);
            Assert.Equal(2, call);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"n_ctx":32768,"total_slots":2}"""),
            });
        }
    }

    private sealed class TrustReadinessProvider(bool proveHealth = false, int timeoutMs = 100) : IEngineLifecycleProvider
    {
        public string Kind => DockerLifecycleProvider.ProviderKind;

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult(LifecycleResult.Ok());

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct) =>
            ValueTask.FromResult(LifecycleResult.Ok());

        public ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult(LifecycleResult.Ok());

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(proveHealth, Ms(timeoutMs));
    }
}
