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

    [Theory(Timeout = 20_000)]
    [InlineData("vacant", true)]
    [InlineData("exited", true)]
    [InlineData("running", true)]
    [InlineData("foreign", false)]
    [InlineData("unreadable", false)]
    public async Task QueuedSuccessorReconcilesPeerStopOnceOrReceivesSwitchFailure(string state, bool serves)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = 0;
        var inventories = 0;
        var previous = new Provider
        {
            Inventory = () =>
            {
                Interlocked.Increment(ref inventories);
                if (Volatile.Read(ref stopped) == 0) return [Peer("running")];
                return state == "vacant" ? [] : [Peer(state)];
            },
            OnStop = async () =>
            {
                if (Interlocked.Increment(ref stopped) > 1) return LifecycleResult.Ok();
                stopping.TrySetResult();
                await release.Task.WaitAsync(ct);
                return LifecycleResult.Fail("daemon_call_timeout", "stop was not confirmed");
            },
        };
        var next = new Provider();
        using var incumbent = new FakeEngineHandler { LoadedModels = ["old"] };
        using var requested = new FakeEngineHandler { LoadedModels = ["new"] };
        string[] names = ["old", "new"];
        var engines = names.ToDictionary(name => name, name => new EngineDefinition
        {
            Name = name, Endpoint = new Uri(name == "old" ? "http://localhost:12351/v1" : "http://localhost:12352/v1"),
            Lifecycle = EngineLifecycleKind.Docker, LifecycleOwner = EngineLifecycleOwner.Router, LifecycleGroup = "shared",
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" },
            Catalog = new Dictionary<string, EngineModelDefinition> { [name] = new() { Name = name } },
        }, StringComparer.Ordinal);
        var config = new RouterConfig { AutoSwitch = true, Engines = engines,
            HttpHandlerFactory = def => def.Name == "old" ? incumbent : requested };
        await using var router = new RouterCore(config, null, null, TimeProvider.System,
            lifecycleFactory: def => def.Name == "old" ? previous : next);
        router.Start();
        var first = await Enqueue("first");
        RouterTicket successor;
        try
        {
            await stopping.Task.WaitAsync(ct);
            successor = await Enqueue("second");
            Assert.Equal(TicketState.Queued, successor.State);
        }
        finally { release.TrySetResult(); }
        var failed = await Assert.ThrowsAsync<RouterRequestException>(() => first.CompletionTask.WaitAsync(ct));
        Assert.Equal("switch_failed", failed.Code);
        if (serves) await successor.CompletionTask.WaitAsync(ct);
        else
        {
            var refused = await Assert.ThrowsAsync<RouterRequestException>(() => successor.CompletionTask.WaitAsync(ct));
            Assert.Equal("switch_failed", refused.Code);
            Assert.Contains(router.Events.Replay(0), item => item.Kind == "engine.switch_failed" &&
                item.Reason?.Contains("group", StringComparison.OrdinalIgnoreCase) == true);
        }
        Assert.Equal(2, Volatile.Read(ref inventories));
        Assert.Equal(serves ? 2 : 1, Volatile.Read(ref stopped));
        Assert.Equal(serves ? 1 : 0, next.Calls.Count(call => call == "launch"));
        Assert.Equal(serves ? 1 : 0, requested.CompletionCalls);
        Assert.Single(router.Events.Replay(0), item => item.Kind == "request.selected" && item.TicketId == successor.Id);
        if (!serves)
        {
            state = "vacant";
            var recovered = await Enqueue("third");
            await recovered.CompletionTask.WaitAsync(ct);
            Assert.Equal(1, requested.CompletionCalls);
            Assert.Equal(3, Volatile.Read(ref inventories));
        }

        static EnginePeer Peer(string status) => new("container", "fixture", "old", status != "foreign",
            QueryFailed: status == "unreadable") { ContainerState = status, EffectiveProfile = "" };

        async Task<RouterTicket> Enqueue(string thread) => await router.EnqueueAsync(thread,
            new InferenceRequest { Model = "new", Body = """{"model":"new","prompt":"hi"}"""u8.ToArray(), Options = new() }, ct);
    }

    [Theory(Timeout = 15_000)]
    [InlineData("caller")]
    [InlineData("execution")]
    [InlineData("budget")]
    [InlineData("issued")]
    public async Task LocalOwnerEndsUnclaimedAdmissionAndDrainsAnIssuedTransaction(string ending)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var caller = new CancellationTokenSource();
        using var execution = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var provider = new Provider { OnLaunch = _ =>
        {
            entered.TrySetResult();
            release.Wait(bound.Token);
        } };
        var definition = new EngineDefinition
        {
            Name = "engine", Endpoint = new Uri("http://localhost:12351/v1"), Lifecycle = EngineLifecycleKind.Docker,
            LifecycleOwner = EngineLifecycleOwner.Router, LifecycleGroup = "shared",
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" },
            Catalog = new Dictionary<string, EngineModelDefinition> { ["target"] = new() { Name = "target" } },
        };
        var config = new RouterConfig { Engines = new Dictionary<string, EngineDefinition> { ["engine"] = definition } };
        using var backend = new FakeEngineHandler { LoadedModels = ["target"] };
        await using var engine = new EngineRuntime(definition, config, new EventRing(), backend, provider,
            dispatchHint: null, hostEnrollment: null, localManaged: true);
        var group = new LocalLifecycleGroup("shared", static () => { });
        group.Attach(engine);
        var ticket = new RouterTicket("ticket", "thread", "target", DateTimeOffset.UtcNow);
        var priority = ThreadPriority.Normal();
        var queued = new QueuedRequest { Ticket = ticket, Thread = new RouterThread("thread", priority, 1, null),
            Request = new InferenceRequest { Model = "target", Body = "{}"u8.ToArray(), Options = new() },
            PriorityNode = new PrioritizedRequest(ticket, priority, 1), CallerToken = caller.Token, EnqueuedSeq = 1, PromptChars = 2 };
        var clock = TimeProvider.System;
        var lease = group.TryReserve(queued, engine, null, true, new OwnerSwitchBudget(clock, clock.GetTimestamp(),
            ending == "budget" ? 1000 : 0))!;
        if (ending != "issued")
        {
            engine.RetainNamedUse();
            engine.BeforeSwitchWaitForTests = () => entered.TrySetResult();
        }
        var preparation = lease.PrepareAsync(execution.Token, bound.Token);
        try
        {
            await entered.Task.WaitAsync(bound.Token);
            if (ending == "budget")
                Assert.Equal(EngineRuntime.SwitchOutcome.TimedOut, (await preparation.WaitAsync(bound.Token)).Result.Outcome);
            else
            {
                var cancelled = ending == "execution" ? execution : caller;
                await cancelled.CancelAsync();
                if (ending == "issued")
                {
                    lease.Release();
                    Assert.False(group.IsIdle);
                    Assert.False(preparation.IsCompleted);
                    release.Set();
                }
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation.WaitAsync(bound.Token));
                Assert.Equal(cancelled.Token, error.CancellationToken);
            }
            Assert.Equal(ending == "issued" ? 1 : 0, provider.Calls.Count(call => call == "launch"));
        }
        finally
        {
            release.Set();
            if (ending != "issued") engine.ReleaseNamedUse();
            lease.Release();
        }
        Assert.True(group.IsIdle);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndingOwnerPreventsFurtherSiblingStops(bool afterFirstStop)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var firstBackend = new FakeEngineHandler { LoadedModels = ["first"] };
        using var secondBackend = new FakeEngineHandler { LoadedModels = ["second"] };
        using var targetBackend = new FakeEngineHandler { LoadedModels = ["target"] };
        var stops = 0;
        ValueTask<LifecycleResult> StopSibling()
        {
            if (Interlocked.Increment(ref stops) == 1 && afterFirstStop) caller.Cancel();
            return ValueTask.FromResult(LifecycleResult.Ok());
        }
        var first = new Provider { OnStop = StopSibling };
        var second = new Provider { OnStop = StopSibling };
        var target = new Provider { OnPreflight = () => { if (!afterFirstStop) caller.Cancel(); } };
        string[] names = ["first", "second", "target"];
        var engines = names.ToDictionary(name => name, name => new EngineDefinition
        {
            Name = name, Endpoint = new Uri($"http://localhost:{12350 + Array.IndexOf(names, name)}/v1"),
            Lifecycle = EngineLifecycleKind.Docker, LifecycleOwner = EngineLifecycleOwner.Router, LifecycleGroup = "shared",
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" },
            Catalog = new Dictionary<string, EngineModelDefinition> { [name] = new() { Name = name } },
        }, StringComparer.Ordinal);
        var config = new RouterConfig { AutoSwitch = true, Engines = engines, HttpHandlerFactory = def => def.Name switch { "first" => firstBackend, "second" => secondBackend, _ => targetBackend } };
        await using var router = new RouterCore(config, null, null, TimeProvider.System,
            lifecycleFactory: def => def.Name switch { "first" => first, "second" => second, _ => target });
        router.Start();
        var ticket = await router.EnqueueAsync("caller", new InferenceRequest
            { Model = "target", Body = """{"model":"target","prompt":"hi"}"""u8.ToArray(), Options = new() }, caller.Token);
        var cancelled = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(deadline.Token));
        Assert.True(cancelled.Code == "cancelled", cancelled.Message);
        Assert.Equal(TicketState.Cancelled, ticket.State);
        Assert.Equal(afterFirstStop ? 1 : 0, Volatile.Read(ref stops));
        Assert.DoesNotContain("launch", target.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task RequestsShareAnAlreadyLoadedNamedProfile()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new FakeEngineHandler { LoadedModels = ["model"], NCtx = 8192, TotalSlots = 4 };
        var provider = new Provider();
        var engine = new EngineDefinition
        {
            Name = "engine", Endpoint = new Uri("http://localhost:12000/v1"),
            Lifecycle = EngineLifecycleKind.Docker, LifecycleOwner = EngineLifecycleOwner.Router, LifecycleGroup = "gpu",
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" },
            Catalog = new Dictionary<string, EngineModelDefinition>
            {
                ["model"] = new() { Name = "model", ServingProfiles = [new() { Name = "large", MaxContext = 8192, MaxSlots = 4 }] },
            },
        };
        var settings = new RouterConfig { AutoSwitch = true, Engines = new Dictionary<string, EngineDefinition>
            { ["engine"] = engine }, HttpHandlerFactory = _ => backend };
        settings.Scheduler.MaxInflightPerEngine = 4;
        await using var router = new RouterCore(settings, scheduler: null, events: null, clock: TimeProvider.System,
            lifecycleFactory: _ => provider);
        router.Start();
        var first = await Enqueue("initial", 6000);
        await first.CompletionTask.WaitAsync(deadline.Token);
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        backend.HoldCompletions = true;
        backend.AfterHeldCompletionEnteredForTests = () =>
        {
            if (Interlocked.Increment(ref count) == 2) bothEntered.TrySetResult();
        };
        var second = await Enqueue("second", null);
        var third = await Enqueue("third", null);
        try { await bothEntered.Task.WaitAsync(deadline.Token); }
        finally { backend.ReleaseGate(); }
        await Task.WhenAll(second.CompletionTask, third.CompletionTask).WaitAsync(deadline.Token);
        Assert.Single(provider.Calls, call => call == "launch");

        async Task<RouterTicket> Enqueue(string client, long? required) => await router.EnqueueAsync(client, new InferenceRequest
        {
            Model = "model", Body = """{"model":"model","messages":[{"role":"user","content":"hello"}]}"""u8.ToArray(),
            Options = new() { RequiredContext = required },
        }, deadline.Token);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(2048)]
    [InlineData(8192)]
    public async Task ExactSmallerProfileWaitsForDrainAndSurvivesAnotherCallersCancellation(long smallerWindow)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var backend = new FakeEngineHandler { LoadedModels = ["model"], HoldCompletions = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.AfterHeldCompletionEnteredForTests = () => entered.TrySetResult();
        var switched = new ConcurrentQueue<string>();
        var provider = new Provider { OnLaunch = target =>
        {
            var tier = target.Profile!;
            switched.Enqueue(tier.Name);
            backend.NCtx = tier.MaxContext;
            backend.TotalSlots = tier.MaxSlots;
        } };
        var engine = new EngineDefinition
        {
            Name = "engine", Endpoint = new Uri("http://localhost:12000/v1"),
            Lifecycle = EngineLifecycleKind.Docker, LifecycleOwner = EngineLifecycleOwner.Router, LifecycleGroup = "gpu",
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" },
            Catalog = new Dictionary<string, EngineModelDefinition>
            {
                ["model"] = new() { Name = "model", Aliases = ["alias"], ServingProfiles =
                    [ new() { Name = "large", MaxContext = 8192, MaxSlots = 2 },
                      new() { Name = "small", MaxContext = smallerWindow, MaxSlots = 4 } ] },
            },
        };
        var settings = new RouterConfig { AutoSwitch = true, Engines = new Dictionary<string, EngineDefinition>
            { ["engine"] = engine }, HttpHandlerFactory = _ => backend };
        settings.Scheduler.MaxInflightPerEngine = 4;
        await using var router = new RouterCore(settings, scheduler: null, events: null, clock: TimeProvider.System,
            lifecycleFactory: _ => provider);
        router.Start();
        var large = await Enqueue("first", "large", ct);
        RouterTicket survivor;
        try
        {
            await entered.Task.WaitAsync(ct);
            var withdrawn = await Enqueue("second", "small", cancelled.Token);
            survivor = await Enqueue("third", "small", ct);
            await cancelled.CancelAsync();
            await Assert.ThrowsAsync<RouterRequestException>(() => withdrawn.CompletionTask.WaitAsync(ct));
            Assert.Equal("cancelled", withdrawn.ErrorCode);
            Assert.Equal("large", Assert.Single(switched));
        }
        finally { backend.ReleaseGate(); }
        await Task.WhenAll(large.CompletionTask, survivor.CompletionTask).WaitAsync(ct);
        Assert.Collection(switched, first => Assert.Equal("large", first), second => Assert.Equal("small", second));
        Assert.Equal(2, backend.CompletionCalls);
        Assert.Equal("small", Assert.Single(router.GetStats().Engines).ActiveProfile);

        async Task<RouterTicket> Enqueue(string client, string profile, CancellationToken token) =>
            await router.EnqueueAsync(client, new InferenceRequest
            {
                Model = "alias", Body = """{"model":"alias","prompt":"hello"}"""u8.ToArray(),
                Options = new() { Profile = profile },
            }, token);
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
        internal Action? OnPreflight { get; init; }
        internal Func<IReadOnlyList<EnginePeer>>? Inventory { get; init; }
        internal Func<ValueTask<LifecycleResult>>? OnStop { get; init; }
        internal Action<LifecycleTarget>? OnLaunch { get; init; }
        public string Kind => "docker";
        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
        {
            OnPreflight?.Invoke();
            return Result("preflight");
        }
        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<EnginePeer>>(Inventory?.Invoke() ?? []);
        public ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct) => OnStop?.Invoke() ?? Result("stop");
        public ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            OnLaunch?.Invoke(target);
            return Result("launch");
        }
        public ReadinessPolicy Readiness(LifecycleTarget target) => new(true, TimeSpan.FromSeconds(5));
        private ValueTask<LifecycleResult> Result(string operation)
        {
            Calls.Enqueue(operation);
            return ValueTask.FromResult(LifecycleResult.Ok());
        }
    }
}
