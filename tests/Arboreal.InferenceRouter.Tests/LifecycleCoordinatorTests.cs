using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>A provider that records what it was asked to do, in order, so the coordinator's ordering is
/// observable. It never performs a mutation without being told to, which is the point: the ordering
/// lives in the coordinator, not in the provider.</summary>
public sealed class RecordingLifecycleProvider : IEngineLifecycleProvider
{
    public List<string> Calls { get; } = [];
    public LifecycleResult PreflightOutcome { get; set; } = LifecycleResult.Ok("fake");
    public LifecycleResult StopOutcome { get; set; } = LifecycleResult.Ok();
    public LifecycleResult LaunchOutcome { get; set; } = LifecycleResult.Ok();
    public List<EnginePeer> Peers { get; set; } = [];
    public IReadOnlyList<EnginePeer> StoppedPeers { get; private set; } = [];

    public string Kind => "fake";

    public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
    {
        Calls.Add("preflight");
        return ValueTask.FromResult(PreflightOutcome);
    }

    public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
    {
        Calls.Add("ownership");
        return ValueTask.FromResult<IReadOnlyList<EnginePeer>>(Peers);
    }

    public ValueTask<LifecycleResult> StopAsync(
        LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
    {
        Calls.Add("stop");
        StoppedPeers = peers;
        return ValueTask.FromResult(StopOutcome);
    }

    public ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
    {
        Calls.Add("launch");
        return ValueTask.FromResult(LaunchOutcome);
    }

    public ReadinessPolicy Readiness(LifecycleTarget target) => new(true, TimeSpan.FromSeconds(5));
}

public sealed class LifecycleCoordinatorTests
{
    private static LifecycleTarget Target() => new(
        "engine-a", "model-x", null,
        new EngineModelDefinition { Name = "model-x" });

    [Fact(Timeout = 20_000)]
    public async Task Transaction_RunsOwnershipThenPreflightThenStopThenLaunch()
    {
        var provider = new RecordingLifecycleProvider
        {
            Peers = [new EnginePeer("c1", "img", "model-old", OwnedByUs: true)],
        };
        using var coordinator = new EngineLifecycleCoordinator(provider);

        var result = await coordinator.SwitchAsync(Target(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["ownership", "preflight", "stop", "launch"], provider.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task ARefusedPreflightMutatesNothing()
    {
        // AC: every admissibility check runs before any container is stopped or started. A refusal that
        // happens after the stop is a switch that broke a working engine for nothing.
        var provider = new RecordingLifecycleProvider
        {
            Peers = [new EnginePeer("c1", "img", "model-old", OwnedByUs: true)],
            PreflightOutcome = LifecycleResult.Fail("image_missing", "pull the pinned image first"),
        };
        using var coordinator = new EngineLifecycleCoordinator(provider);

        var result = await coordinator.SwitchAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("image_missing", result.Reason);
        Assert.Equal("pull the pinned image first", result.Action);
        Assert.Equal(["ownership", "preflight"], provider.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task ContainersWithoutOurLabelsAreNeverHandedToTheStopStep()
    {
        var provider = new RecordingLifecycleProvider
        {
            Peers =
            [
                new EnginePeer("mine", "img", "model-old", OwnedByUs: true),
                new EnginePeer("someone-elses", "img", "model-old", OwnedByUs: false),
            ],
        };
        using var coordinator = new EngineLifecycleCoordinator(provider);

        var result = await coordinator.SwitchAsync(Target(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["mine"], provider.StoppedPeers.Select(p => p.Id));
    }

    [Fact(Timeout = 20_000)]
    public async Task AModelWeDoNotOwnIsNeverStoppedAtAll()
    {
        var provider = new RecordingLifecycleProvider
        {
            Peers = [new EnginePeer("theirs", "img", null, OwnedByUs: false)],
        };
        using var coordinator = new EngineLifecycleCoordinator(provider);

        await coordinator.SwitchAsync(Target(), CancellationToken.None);

        Assert.DoesNotContain("stop", provider.Calls);
        Assert.Equal(["ownership", "preflight", "launch"], provider.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task AFailedLaunchStopsTheCoordinatorAndReportsOneActionableReason()
    {
        var provider = new RecordingLifecycleProvider
        {
            LaunchOutcome = LifecycleResult.Fail("image_pin_mismatch", "the running image is not the pinned one"),
        };
        using var coordinator = new EngineLifecycleCoordinator(provider);

        var result = await coordinator.SwitchAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("pinned", result.Action);
        Assert.Equal(["ownership", "preflight", "launch"], provider.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task SwitchesOnOneEngineAreSingleFlight()
    {
        // Two tickets asking for the same model at once must not run two transactions: the second would
        // stop what the first just started.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new BlockingProvider(gate.Task);
        using var coordinator = new EngineLifecycleCoordinator(provider);

        var first = coordinator.SwitchAsync(Target(), CancellationToken.None);
        while (provider.InFlight < 1) await Task.Delay(10);
        var second = coordinator.SwitchAsync(Target(), CancellationToken.None);
        await Task.Delay(50);
        var concurrent = provider.InFlight > 1;

        gate.TrySetResult();
        await first;
        await second;

        Assert.False(concurrent);
        Assert.Equal(2, provider.LaunchCount);
    }

    [Fact(Timeout = 20_000)]
    public async Task TheJournalExplainsARefusalWithoutReadingTheLogs()
    {
        var provider = new RecordingLifecycleProvider
        {
            PreflightOutcome = LifecycleResult.Fail("no_select_command", "add a select block"),
        };
        using var coordinator = new EngineLifecycleCoordinator(provider);

        var result = await coordinator.SwitchAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(coordinator.Journal, line => line.StartsWith("preflight_refused:", StringComparison.Ordinal));
    }

    private sealed class BlockingProvider(Task block) : IEngineLifecycleProvider
    {
        private int _inFlight;
        private int _launchCount;

        public int InFlight => Volatile.Read(ref _inFlight);
        public int LaunchCount => Volatile.Read(ref _launchCount);
        public string Kind => "blocking";

        public async ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                await block.WaitAsync(ct).ConfigureAwait(false);
                return LifecycleResult.Ok();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(
            LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct) =>
            ValueTask.FromResult(LifecycleResult.Ok());

        public ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            Interlocked.Increment(ref _launchCount);
            return ValueTask.FromResult(LifecycleResult.Ok());
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.Zero);
    }
}
