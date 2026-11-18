using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class LifecycleRefusalTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly LifecycleResult Rejected = LifecycleResult.Fail("image_missing", "check the configured image");

    [Theory(Timeout = 40_000)]
    [InlineData("ownership", true)]
    [InlineData("preflight", true)]
    [InlineData("stop", false)]
    [InlineData("launch", false)]
    public async Task FailureOnlyRestoresProofWhenTheCoordinatorRefusedBeforeMutation(string phase, bool restored)
    {
        using var handler = new FakeEngineHandler { LoadedModels = ["incumbent"] };
        var provider = new RecordingLifecycleProvider
        {
            Kind = "docker",
            Peers = phase == "ownership"
                ? [new EnginePeer("unavailable", "", null, OwnedByUs: false, QueryFailed: true)]
                : [new EnginePeer("owned", "fixture", "incumbent", OwnedByUs: true)],
            PreflightOutcome = phase == "preflight" ? Rejected : LifecycleResult.Ok(),
            StopOutcome = phase == "stop" ? LifecycleResult.Fail("stop_timed_out", "check the incumbent") : LifecycleResult.Ok(),
            LaunchOutcome = phase == "launch" ? LifecycleResult.Fail("launch_timed_out", "check the target") : LifecycleResult.Ok(),
        };
        var feed = new EventRing();
        await using var engine = Engine(handler, provider, feed);
        using var probing = new CancellationTokenSource();
        await EstablishProofAsync(engine, probing.Token);
        var before = engine.Capability;

        EngineCapability? seenAtWake = null;
        var failuresAtWake = 0;
        engine.AfterReadyWaitForTests = _ =>
        {
            seenAtWake = engine.Capability;
            failuresAtWake = feed.Replay(0).Count(e => e.Kind == "engine.switch_failed");
        };

        var refused = await engine.WaitForModelAsync("target", Limit, CancellationToken.None);
        Assert.False(refused.IsReady);
        Assert.Equal("launch_failed", refused.Detail);
        Assert.Equal(1, failuresAtWake);
        var after = Assert.IsType<EngineCapability>(seenAtWake);
        Assert.Same(after, engine.Capability);
        Assert.Equal(restored, after.Healthy);
        Assert.Null(after.SwitchingToward);
        Assert.False(after.HealthFromClaim);
        Assert.Same(before.ModelRoster, after.ModelRoster);
        Assert.True(after.Version > before.Version);
        Assert.Single(feed.Replay(0), e => e.Kind == "engine.switch_failed");
        Assert.DoesNotContain(feed.Replay(0), e => e.Kind == "engine.switch_completed");

        var expectedCalls = phase switch
        {
            "ownership" => new[] { "ownership" },
            "preflight" => new[] { "ownership", "preflight" },
            "stop" => new[] { "ownership", "preflight", "stop" },
            _ => new[] { "ownership", "preflight", "stop", "launch" },
        };
        Assert.Equal(expectedCalls, provider.Calls);

        if (restored)
        {
            // The incumbent is only known from /models, and a ready call needs no lifecycle.
            var incumbent = await engine.WaitForModelAsync("incumbent", Limit, CancellationToken.None);
            Assert.True(incumbent.IsReady);
            Assert.Equal(expectedCalls, provider.Calls);
        }
    }

    [Theory(Timeout = 40_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AContraryProbeDuringPreflightPreventsRestoringTheIncumbent(bool unanswered)
    {
        using var handler = new FakeEngineHandler { LoadedModels = ["incumbent"] };
        using var probing = new CancellationTokenSource();
        var gate = new HeldPreflight();
        var provider = new RecordingLifecycleProvider { Kind = "docker", PreflightHandler = gate.WaitAsync };
        var feed = new EventRing();
        var contradiction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.Subscribe(e =>
        {
            if (e.Kind != "engine.health" || e.Value != (unanswered ? 0 : 2)) return;
            // Stop polling after this observation so no later probe can decide the assertion.
            probing.Cancel();
            contradiction.TrySetResult();
        });
        await using var engine = Engine(handler, provider, feed, intervalMs: 25);
        await EstablishProofAsync(engine, probing.Token);
        var pending = engine.WaitForModelAsync("target", Limit, CancellationToken.None);
        try
        {
            await gate.Entered.Task.WaitAsync(Limit);
            Assert.True(engine.Capability.HealthFromClaim);
            if (unanswered) handler.ModelsStatus = 503;
            else handler.LoadedModels = ["different", "also-different"];
            await contradiction.Task.WaitAsync(Limit);
            Assert.Equal("target", engine.SwitchingToward);
        }
        finally
        {
            gate.Refuse();
        }

        Assert.Equal("launch_failed", (await pending).Detail);
        Assert.False(engine.Healthy);
        Assert.Null(engine.SwitchingToward);
        Assert.Equal(["incumbent"], engine.LoadedModels);
        Assert.Equal(["ownership", "preflight"], provider.Calls);
    }

    [Fact(Timeout = 40_000)]
    public async Task ARefusalWithoutPriorProofDoesNotInventOne()
    {
        using var handler = new FakeEngineHandler();
        var provider = new RecordingLifecycleProvider { Kind = "docker", PreflightOutcome = Rejected };
        await using var engine = Engine(handler, provider, new EventRing());

        var result = await engine.WaitForModelAsync("target", Limit, CancellationToken.None);
        Assert.Equal("launch_failed", result.Detail);
        Assert.False(engine.Healthy);
        Assert.Empty(engine.LoadedModels);
        Assert.Null(engine.SwitchingToward);
    }

    [Fact(Timeout = 40_000)]
    public async Task ShutdownCannotBeUndoneByALateVerifiedRefusal()
    {
        using var handler = new FakeEngineHandler { LoadedModels = ["incumbent"] };
        using var probing = new CancellationTokenSource();
        var gate = new HeldPreflight();
        var provider = new RecordingLifecycleProvider { Kind = "docker", PreflightHandler = gate.WaitAsync };
        await using var engine = Engine(handler, provider, new EventRing());
        await EstablishProofAsync(engine, probing.Token);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AfterEndCommitForTests = _ => ended.TrySetResult();

        var pending = engine.WaitForModelAsync("target", Limit, CancellationToken.None);
        try
        {
            await gate.Entered.Task.WaitAsync(Limit);
            await engine.DisposeAsync();
            Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, (await pending).Outcome);
        }
        finally
        {
            gate.Refuse();
        }

        await ended.Task.WaitAsync(Limit);
        Assert.False(engine.Healthy);
        Assert.Null(engine.SwitchingToward);
        Assert.Equal(["ownership", "preflight"], provider.Calls);
    }

    private static EngineRuntime Engine(FakeEngineHandler handler, RecordingLifecycleProvider provider, EventRing feed,
        int intervalMs = 3_600_000) =>
        new(new EngineDefinition
        {
            Name = "fixture",
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Health = new HealthProbeDefinition { Path = "/props", IntervalMs = intervalMs },
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "test" },
            Catalog = new Dictionary<string, EngineModelDefinition> { ["target"] = new() { Name = "target" } },
        }, new RouterConfig(), feed, handler, lifecycle: provider);

    private static async Task EstablishProofAsync(EngineRuntime engine, CancellationToken probing)
    {
        engine.StartHealthLoop(probing);
        using var deadline = new CancellationTokenSource(Limit);
        while (!engine.Healthy) await Task.Delay(10, deadline.Token);
        Assert.Equal(["incumbent"], engine.LoadedModels);
    }

    private sealed class HeldPreflight
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<LifecycleResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The fixture can return an explicit read-only refusal even after router cancellation.
        public ValueTask<LifecycleResult> WaitAsync()
        {
            Entered.TrySetResult();
            return new ValueTask<LifecycleResult>(_result.Task);
        }

        public void Refuse() => _result.TrySetResult(Rejected);
    }
}
