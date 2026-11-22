using System.Collections.Concurrent;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class LocalLifecycleGroupTests
{
    [Theory(Timeout = 30_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OriginalQueuedRequestsSwitchThreeEnginesAfterDrainWhileAnotherGroupProgresses(bool cancelQueued, bool lateCleanup)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        using var queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var first = new HeldHandler { LoadedModels = ["model-0"] };
        using var second = new FakeEngineHandler { LoadedModels = ["model-1"] };
        using var third = new FakeEngineHandler { LoadedModels = ["model-2"] };
        using var independent = new FakeEngineHandler { LoadedModels = ["model-3"] };
        FakeEngineHandler[] handlers = [first, second, third, independent];
        var providers = Enumerable.Range(0, 4).Select(_ => new Provider()).ToArray();
        var engines = Enumerable.Range(0, 4).ToDictionary(i => $"engine-{i}", i => new EngineDefinition
        {
            Name = $"engine-{i}", Endpoint = new Uri($"http://127.0.0.1:{12000 + i}/v1"),
            Lifecycle = EngineLifecycleKind.Docker, LifecycleOwner = EngineLifecycleOwner.Router,
            LifecycleGroup = i == 3 ? "independent" : "shared",
            ResponseTimeoutMs = i == 0 && lateCleanup ? 100 : 10_000,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" },
            Health = new HealthProbeDefinition { Path = "/props", IntervalMs = 20 },
            Catalog = new Dictionary<string, EngineModelDefinition>
            {
                [$"model-{i}"] = new() { Name = $"model-{i}" },
            },
        }, StringComparer.Ordinal);
        var config = new RouterConfig { AutoSwitch = true, Engines = engines };
        config.HttpHandlerFactory = def => handlers[Array.IndexOf(engines.Keys.ToArray(), def.Name)];
        await using var router = new RouterCore(config, scheduler: null, events: null, clock: TimeProvider.System,
            lifecycleFactory: def => providers[Array.IndexOf(engines.Keys.ToArray(), def.Name)]);
        router.Start();
        var running = await Enqueue(0);
        await first.Entered.Task.WaitAsync(ct);
        var queued = await Enqueue(1);
        var later = await Enqueue(2);
        var other = await Enqueue(3);
        await other.CompletionTask.WaitAsync(ct);
        Assert.Equal(TicketState.Completed, other.State);
        Assert.False(queued.CompletionTask.IsCompleted);
        Assert.False(later.CompletionTask.IsCompleted);
        Assert.DoesNotContain("launch", providers[1].Calls);
        Assert.DoesNotContain("launch", providers[2].Calls);
        if (cancelQueued)
        {
            await queuedCancellation.CancelAsync();
            await Assert.ThrowsAsync<RouterRequestException>(() => queued.CompletionTask.WaitAsync(ct));
        }
        if (lateCleanup)
        {
            await Assert.ThrowsAsync<RouterRequestException>(() => running.CompletionTask.WaitAsync(ct));
            Assert.Equal("engine_timeout", running.ErrorCode);
            Assert.DoesNotContain("launch", providers[1].Calls);
            Assert.DoesNotContain("launch", providers[2].Calls);
        }
        first.Release();
        if (!lateCleanup) await running.CompletionTask.WaitAsync(ct);
        if (!cancelQueued) await queued.CompletionTask.WaitAsync(ct);
        await later.CompletionTask.WaitAsync(ct);
        if (!lateCleanup) Assert.Equal(TicketState.Completed, running.State);
        if (!cancelQueued) Assert.Equal(TicketState.Completed, queued.State);
        else Assert.Equal("cancelled", queued.ErrorCode);
        Assert.Equal(TicketState.Completed, later.State);
        Assert.Equal("engine-0", running.Engine);
        if (!cancelQueued) Assert.Equal("engine-1", queued.Engine);
        Assert.Equal("engine-2", later.Engine);
        Assert.Equal(cancelQueued ? 0 : 1, second.CompletionCalls);
        Assert.Equal(1, third.CompletionCalls);
        Assert.Equal(cancelQueued ? 0 : 1, providers[1].Calls.Count(call => call == "launch"));
        Assert.Single(providers[2].Calls, call => call == "launch");

        async Task<RouterTicket> Enqueue(int index) => await router.EnqueueAsync($"client-{index}", new InferenceRequest
        {
            Model = $"model-{index}",
            Body = System.Text.Encoding.UTF8.GetBytes($"{{\"model\":\"model-{index}\",\"messages\":[{{\"role\":\"user\",\"content\":\"hello\"}}]}}"),
            Options = new(),
        }, index == 1 ? queuedCancellation.Token : ct);
    }

    private sealed class HeldHandler : FakeEngineHandler
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Entered.TrySetResult();
                // Deliberately ignore cancellation: the router must retain physical use until this response is disposed.
                await _release.Task;
                return await base.SendAsync(request, CancellationToken.None);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Provider : IEngineLifecycleProvider
    {
        internal ConcurrentQueue<string> Calls { get; } = new();
        public string Kind => "docker";
        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct) => Result("preflight");
        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);
        public ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct) => Result("stop");
        public ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct) => Result("launch");
        public ReadinessPolicy Readiness(LifecycleTarget target) => new(true, TimeSpan.FromSeconds(5));
        private ValueTask<LifecycleResult> Result(string operation)
        {
            Calls.Enqueue(operation);
            return ValueTask.FromResult(LifecycleResult.Ok());
        }
    }
}
