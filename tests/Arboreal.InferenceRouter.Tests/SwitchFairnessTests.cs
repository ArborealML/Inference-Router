using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>Single-flight is only fair if a waiter is answered by its *own* target. These tests hold a
/// switch open and arrive behind it with a different serving profile: the switch in flight says nothing
/// about the window the newcomer needs, so it has to take its own turn rather than being told ready —
/// or failed for a switch that was never its problem.</summary>
public sealed class SwitchFairnessTests
{
    /// <summary>A lifecycle provider whose launches are held by the test, so a switch can be kept in
    /// flight while other waiters arrive. It records what each launch was aimed at.</summary>
    private sealed class GatedProvider : IEngineLifecycleProvider
    {
        private readonly Queue<TaskCompletionSource> _gates = new();
        public List<string> Launches { get; } = [];
        public LifecycleResult LaunchOutcome { get; set; } = LifecycleResult.Ok();
        public string Kind => "gated";

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok("fake"));

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);

        public ValueTask<LifecycleResult> StopAsync(
            LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
            => ValueTask.FromResult(LifecycleResult.Ok());

        public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            Launches.Add($"{target.Model}@{target.Profile?.Name ?? "-"}");
            // One gate per launch, handed out in order: the test releases them one at a time so a switch
            // can be held in flight while another waiter arrives behind it.
            TaskCompletionSource gate;
            lock (_gates)
            {
                gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates.Enqueue(gate);
            }
            await gate.Task.ConfigureAwait(false);
            return LaunchOutcome;
        }

        public void ReleaseOne()
        {
            TaskCompletionSource? gate;
            lock (_gates) { gate = _gates.Count > 0 ? _gates.Dequeue() : null; }
            gate?.TrySetResult();
        }

        // wait_health=false: the switch's own success is the proof, so no HTTP probes are needed and the
        // window gauge stays unknown (0), which never blocks a profile.
        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.FromSeconds(1));
    }

    private static (EngineRuntime engine, GatedProvider provider) Engine()
    {
        var def = new EngineDefinition
        {
            Name = "fake",
            Kind = EngineKind.OpenAiCompatible,
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Catalog = new Dictionary<string, EngineModelDefinition>
            {
                ["m"] = new() { Name = "m" },
            },
        };
        var config = new RouterConfig
        {
            Engines = new Dictionary<string, EngineDefinition> { ["fake"] = def },
        };
        var provider = new GatedProvider();
        return (new EngineRuntime(def, config, new EventRing(), lifecycle: provider), provider);
    }

    private static ServingProfile Profile(string name, long window) =>
        new() { Name = name, MaxContext = window };

    [Fact(Timeout = 30_000)]
    public async Task AWaiterBehindAnotherProfilesSwitchTakesItsOwnTurn()
    {
        var (engine, provider) = Engine();
        await using var _ = engine;

        var small = Profile("small", 4_096);
        var large = Profile("large", 32_768);

        var first = engine.WaitForProfileAsync("m", small, TimeSpan.FromSeconds(20), CancellationToken.None);
        await WaitUntil(() => provider.Launches.Count == 1);

        // Arrives while the "small" switch is in flight. It joins the wait — but the switch in flight is
        // not its transaction, so it must not inherit that switch's answer.
        var second = engine.WaitForProfileAsync("m", large, TimeSpan.FromSeconds(20), CancellationToken.None);
        await Task.Delay(150);

        provider.ReleaseOne();                       // the "small" switch completes
        await WaitUntil(() => first.IsCompleted);
        Assert.True((await first).IsReady);

        // The bug this pins: the newcomer used to be answered Ready here, on another profile's switch,
        // and the request would have been dispatched against a window it cannot serve. It is still waiting,
        // because its own transaction has not run.
        Assert.False(second.IsCompleted);

        await WaitUntil(() => provider.Launches.Count == 2);   // its own switch, to its own profile
        Assert.Equal("m@large", provider.Launches[1]);
        provider.ReleaseOne();

        var secondResult = await second;
        Assert.True(secondResult.IsReady);
        Assert.Equal(["m@small", "m@large"], provider.Launches);
    }

    [Fact(Timeout = 30_000)]
    public async Task ASwitchThatFailedForAnotherTargetDoesNotFailThisRequest()
    {
        var (engine, provider) = Engine();
        await using var _ = engine;

        var first = engine.WaitForProfileAsync("m", Profile("small", 4_096),
            TimeSpan.FromSeconds(20), CancellationToken.None);
        await WaitUntil(() => provider.Launches.Count == 1);

        var second = engine.WaitForProfileAsync("m", Profile("large", 32_768),
            TimeSpan.FromSeconds(20), CancellationToken.None);
        await Task.Delay(150);

        // Fail the first switch. The newcomer shares the engine, not the transaction: it should still get
        // its own attempt rather than a switch_failed it did not earn.
        provider.LaunchOutcome = LifecycleResult.Fail("image_missing", "pull the pinned image first");
        provider.ReleaseOne();
        var firstResult = await first;
        Assert.Equal(EngineRuntime.SwitchOutcome.Failed, firstResult.Outcome);

        // This time the selector works: the newcomer's own attempt is what decides its answer.
        provider.LaunchOutcome = LifecycleResult.Ok();
        await WaitUntil(() => provider.Launches.Count == 2);
        provider.ReleaseOne();
        var secondResult = await second;
        Assert.True(secondResult.IsReady);
        Assert.Equal("m@large", provider.Launches[1]);
    }

    private static async Task WaitUntil(Func<bool> condition, int limitMs = 5_000)
    {
        var deadline = Environment.TickCount64 + limitMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(25);
        }
    }
}
