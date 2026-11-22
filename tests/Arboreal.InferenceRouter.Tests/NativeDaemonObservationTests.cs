using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class NativeDaemonObservationTests
{
    private const string A = DockerLifecycleProviderTests.FullPeerId;
    private const string B = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string ChangedStamp = "2026-01-01T00:00:00.123456782Z";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExitedContainerEndsReadinessWhileListenerIsStillUnavailable()
    {
        await using var fixture = new Fixture(proveHealth: true);
        fixture.Probe = _ => throw new HttpRequestException("Listener not bound.");
        fixture.Inspect = id => Task.FromResult(fixture.InspectCount(id) == 1
            ? InspectReply(id) : new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.False((await fixture.SwitchAsync("a").WaitAsync(Limit)).IsReady);
        Assert.Equal(2, fixture.InspectCount(A));
        fixture.AssertUnreadyOnce();
        Assert.Equal(2, fixture.Writes.Count);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task FirstReadyConfirmsTheStampAfterTheLastEndpointRead(bool proveHealth, bool sameStamp)
    {
        await using var fixture = new Fixture(proveHealth);
        var endpoint = new Barrier();
        fixture.Probe = async path =>
        {
            if (path == (proveHealth ? "/props" : "/v1/models")) await endpoint.HoldAsync();
            return fixture.AppResponse(path);
        };
        var ready = fixture.SwitchAsync("a");
        try
        {
            await endpoint.Entered.Task.WaitAsync(Limit);
            Assert.Equal(1, fixture.InspectCount(A));
            Assert.Null(fixture.Engine.Capability.SwitchProof);
            if (!sameStamp)
                fixture.Inspect = id => Task.FromResult(InspectReply(id, startedAt: ChangedStamp));
        }
        finally { endpoint.Release.TrySetResult(); }

        Assert.Equal(sameStamp, (await ready.WaitAsync(Limit)).IsReady);
        Assert.Equal(2, fixture.InspectCount(A));
        Assert.Equal(sameStamp, fixture.Engine.Capability.SwitchProof is not null);
        if (sameStamp)
        {
            Assert.Single(fixture.Feed.Replay(0), entry => entry.Kind == "engine.switch_completed");
            return;
        }
        fixture.AssertUnreadyOnce();
        fixture.Probe = null;
        fixture.Inspect = null;
        await fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
        fixture.AssertUnreadyOnce();
        Assert.Equal(2, fixture.Writes.Count);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task TimedOutInspectReleasesItsGateBeforeLateResponse(bool baseline, bool dockerLimitFirst)
    {
        await using var fixture = new Fixture(proveHealth: true,
            callTimeoutMs: dockerLimitFirst ? 100 : 500, waitTimeoutMs: dockerLimitFirst ? 500 : 100);
        var inspect = new Barrier();
        var stopAck = new Barrier();
        var lateDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ObservedResponse? lateResponse = null;
        var read = 0;
        fixture.Inspect = async id =>
        {
            if (Interlocked.Increment(ref read) != (baseline ? 1 : 2)) return InspectReply(id);
            await inspect.HoldAsync();
            var response = new ObservedResponse(lateDisposed) { Content = Json(InspectBody(id)) };
            lateResponse = response;
            return response;
        };
        fixture.StopReply = async () =>
        {
            await stopAck.HoldAsync();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };

        var ready = fixture.SwitchAsync("a");
        try
        {
            await inspect.Entered.Task.WaitAsync(Limit);
            var stop = fixture.StopAsync();
            Assert.False(stopAck.Entered.Task.IsCompleted);
            if (!baseline)
            {
                fixture.Clock.AdvanceTo(99);
                Assert.False(ready.IsCompleted);
            }
            fixture.Clock.AdvanceTo(100);
            Assert.False((await ready.WaitAsync(Limit)).IsReady);
            fixture.AssertUnreadyOnce();
            await stopAck.Entered.Task.WaitAsync(Limit);
            Assert.False(inspect.Release.Task.IsCompleted);
            Assert.False(lateDisposed.Task.IsCompleted);

            using var gateWait = new CancellationTokenSource(Limit);
            var nextGate = fixture.Provider.EnterOperationAsync(gateWait.Token);
            var retired = fixture.Cleanups.Last();
            var afterFailure = fixture.Engine.Capability;
            inspect.Release.TrySetResult();
            await lateDisposed.Task.WaitAsync(Limit);
            await retired.WaitAsync(Limit);
            Assert.Equal(1, Assert.IsType<ObservedResponse>(lateResponse).Disposals);
            Assert.Same(afterFailure, fixture.Engine.Capability);
            fixture.AssertUnreadyOnce();
            Assert.False(nextGate.IsCompleted);
            stopAck.Release.TrySetResult();
            Assert.True((await stop.WaitAsync(Limit)).Succeeded);
            await nextGate.WaitAsync(Limit);
            fixture.Provider.ExitOperation();
            Assert.Equal(3, fixture.Writes.Count);
        }
        finally
        {
            inspect.Release.TrySetResult();
            stopAck.Release.TrySetResult();
        }
        await fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
        fixture.AssertUnreadyOnce();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalStopAndFinalProofOrderUnderTheProviderGate(bool confirmationFirst)
    {
        await using var fixture = new Fixture();
        var barrier = new Barrier();
        var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        if (confirmationFirst)
            fixture.Inspect = async id =>
            {
                if (Interlocked.Increment(ref reads) == 2) await barrier.HoldAsync();
                return InspectReply(id);
            };
        else
            fixture.Probe = async path =>
            {
                if (path == "/props") await barrier.HoldAsync();
                return fixture.AppResponse(path);
            };
        fixture.StopReply = () =>
        {
            sent.TrySetResult(fixture.Engine.Capability.SwitchProof is not null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        };
        var ready = fixture.SwitchAsync("a");
        Task<LifecycleResult> stop;
        try
        {
            await barrier.Entered.Task.WaitAsync(Limit);
            stop = fixture.StopAsync();
            if (confirmationFirst) Assert.False(sent.Task.IsCompleted);
            else Assert.True((await stop.WaitAsync(Limit)).Succeeded);
        }
        finally { barrier.Release.TrySetResult(); }

        Assert.Equal(confirmationFirst, (await ready.WaitAsync(Limit)).IsReady);
        Assert.Equal(confirmationFirst, await sent.Task.WaitAsync(Limit));
        Assert.True((await stop.WaitAsync(Limit)).Succeeded);
        Assert.Equal(2, fixture.InspectCount(A));
        Assert.Equal(3, fixture.Writes.Count);
        if (confirmationFirst)
        {
            await fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
            Assert.Equal(NativeLaunchHealth.Invalid, fixture.Watch.Health);
            Assert.False(fixture.Engine.Healthy);
        }
        else fixture.AssertUnreadyOnce();
    }

    [Theory]
    [InlineData("removing", false)]
    [InlineData("removing", true)]
    [InlineData("created", false)]
    [InlineData("restart", false)]
    [InlineData("paused", false)]
    [InlineData("absent", false)]
    public async Task IdleLoopInvalidatesOnceEvenWithUnchangedApplicationReplies(string change, bool dead)
    {
        await using var fixture = new Fixture();
        Assert.True((await fixture.SwitchAsync("a")).IsReady);
        using var ticks = new CancellationTokenSource();
        var loop = fixture.Engine.RunHealthLoopAsync(ticks.Token);
        try
        {
            Assert.True(await fixture.FirstTick.WaitAsync(Limit));
            var steady = fixture.Engine.Capability;
            await fixture.TickAsync();
            Assert.Same(steady, fixture.Engine.Capability);
            fixture.Inspect = id => Task.FromResult(change switch
            {
                "removing" => InspectReply(id, state: "removing", running: false, dead: dead),
                "created" => InspectReply(id, state: "created", running: false, startedAt: "0001-01-01T00:00:00Z"),
                "restart" => InspectReply(id, state: "restarting", restarting: true),
                "paused" => InspectReply(id, state: "paused", paused: true),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
            await fixture.TickAsync();
            var invalid = fixture.Engine.Capability;
            Assert.Equal(NativeLaunchHealth.Invalid, fixture.Watch.Health);
            Assert.Equal(steady.Version + 1, invalid.Version);
            Assert.Equal(steady.LoadedModels, invalid.LoadedModels);
            Assert.False(invalid.Healthy);
            Assert.Null(invalid.SwitchProof);
            Assert.Equal(default, invalid.Serving);

            fixture.Inspect = null;
            await fixture.TickAsync();
            Assert.Same(invalid, fixture.Engine.Capability);
            Assert.Equal(6, fixture.InspectCount(A));
            Assert.Equal(2, fixture.Writes.Count);
        }
        finally
        {
            await ticks.CancelAsync();
            await loop.WaitAsync(Limit);
        }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("foreign-id")]
    [InlineData("conflicting-flags")]
    [InlineData("endpoint")]
    [InlineData("over-limit")]
    public async Task UnknownAndInvalidHaveDistinctCapabilityVersions(string fault)
    {
        await using var fixture = new Fixture();
        Assert.True((await fixture.SwitchAsync("a")).IsReady);
        var before = fixture.Engine.Capability;
        if (fault == "endpoint")
            fixture.Probe = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        else
            fixture.Inspect = id => Task.FromResult(fault switch
            {
                "foreign-id" => InspectReply(B),
                "conflicting-flags" => InspectReply(id, state: "removing"),
                "over-limit" => new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = Json(InspectBody(id).PadRight(256 * 1024 + 1)) },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Json("{") },
            });

        await fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
        Assert.Equal(3, fixture.InspectCount(A));
        Assert.Equal(NativeLaunchHealth.Unknown, fixture.Watch.Health);
        Assert.Equal(before.Version + 1, fixture.Engine.Capability.Version);
        Assert.False(fixture.Engine.Healthy);
        Assert.Null(fixture.Engine.Capability.SwitchProof);

        fixture.Probe = null;
        fixture.Inspect = id => Task.FromResult(InspectReply(id, startedAt: ChangedStamp));
        await fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
        var invalid = fixture.Engine.Capability;
        Assert.Equal(before.Version + 2, invalid.Version);
        Assert.Equal(NativeLaunchHealth.Invalid, fixture.Watch.Health);
        fixture.Inspect = null;
        await fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
        Assert.Same(invalid, fixture.Engine.Capability);
    }

    [Fact]
    public async Task FreshSameStampRecoversOnlyOrdinaryHealth()
    {
        await using var fixture = new Fixture();
        Assert.True((await fixture.SwitchAsync("a")).IsReady);
        fixture.Inspect = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
        Assert.Equal(NativeLaunchHealth.Unknown, fixture.Watch.Health);
        fixture.Inspect = null;
        await fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
        Assert.True(fixture.Engine.Healthy);
        Assert.Null(fixture.Engine.Capability.SwitchProof);
        Assert.Equal(NativeLaunchHealth.Eligible, fixture.Watch.Health);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start304CannotResetTracking(bool tracked)
    {
        await using var fixture = new Fixture();
        if (tracked)
        {
            Assert.True((await fixture.SwitchAsync("a")).IsReady);
            fixture.Inspect = id => Task.FromResult(InspectReply(id, startedAt: ChangedStamp));
            await fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
            Assert.Equal(NativeLaunchHealth.Invalid, fixture.Watch.Health);
        }

        fixture.StartStatus = HttpStatusCode.NotModified;
        var answer = await fixture.SwitchAsync("b");
        Assert.Equal(!tracked, answer.IsReady);
        Assert.Equal(0, fixture.InspectCount(B));
        if (!tracked) Assert.Null(fixture.Engine.Capability.NativeWatch);
        else
        {
            fixture.Inspect = null;
            await fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
            Assert.False(fixture.Engine.Healthy);
            Assert.Equal(NativeLaunchHealth.Invalid, fixture.Watch.Health);
            Assert.Null(fixture.Engine.Capability.SwitchProof);
        }
    }

    [Theory]
    [InlineData("successor")]
    [InlineData("displaced")]
    [InlineData("restored")]
    [InlineData("restored-unknown")]
    [InlineData("stopped")]
    public async Task HeldDaemonReadOnlyInvalidatesItsOwnWitness(string disposition)
    {
        await using var fixture = new Fixture();
        var firstEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Engine.AfterEndCommitForTests = _ => firstEnd.TrySetResult();
        Assert.True((await fixture.SwitchAsync("a")).IsReady);
        await firstEnd.Task.WaitAsync(Limit);
        fixture.Engine.AfterEndCommitForTests = null;
        var original = fixture.Engine.Capability;
        var delayed = new Barrier();
        var failure = new Barrier();
        fixture.Inspect = async id =>
        {
            if (id != A) return InspectReply(id);
            await delayed.HoldAsync();
            return disposition == "restored-unknown"
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : InspectReply(id, startedAt: ChangedStamp);
        };
        if (disposition is "displaced" or "restored" or "restored-unknown") fixture.RefusePreflight = true;
        if (disposition == "displaced")
            fixture.Engine.BeforeNativeFailureEventForTests = () => failure.HoldAsync().GetAwaiter().GetResult();
        var poll = fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
        Task<EngineRuntime.SwitchResult>? claim = null;
        try
        {
            await delayed.Entered.Task.WaitAsync(Limit);
            if (disposition == "stopped")
            {
                await fixture.Engine.DisposeAsync();
                var stopped = fixture.Engine.Capability;
                Assert.False(await poll.WaitAsync(Limit));
                var cleanup = fixture.Cleanups.Last();
                delayed.Release.TrySetResult();
                await cleanup.WaitAsync(Limit);
                Assert.Same(stopped, fixture.Engine.Capability);
                return;
            }

            claim = fixture.SwitchAsync("b");
            if (disposition == "displaced")
                await failure.Entered.Task.WaitAsync(Limit);
            else
                Assert.Equal(disposition == "successor", (await claim.WaitAsync(Limit)).IsReady);

            var current = fixture.Engine.Capability;
            Assert.NotSame(original, current);
            if (disposition is "restored" or "restored-unknown")
            {
                Assert.True(current.Healthy);
                Assert.Same(original.SwitchProof, current.SwitchProof);
            }
            var nudges = fixture.DispatchNudges;
            delayed.Release.TrySetResult();
            await poll.WaitAsync(Limit);
            if (disposition == "successor")
            {
                Assert.Same(current, fixture.Engine.Capability);
                Assert.Equal(B, fixture.Watch.Launch.ContainerId);
            }
            else if (disposition == "restored-unknown")
            {
                Assert.Same(current, fixture.Engine.Capability);
            }
            else
            {
                Assert.False(fixture.Engine.Healthy);
                Assert.Equal(NativeLaunchHealth.Invalid, fixture.Watch.Health);
                Assert.Equal(current.Version + 1, fixture.Engine.Capability.Version);
                if (disposition == "displaced")
                {
                    Assert.Equal(default, current.Serving);
                    Assert.Equal(nudges, fixture.DispatchNudges);
                }
            }
        }
        finally
        {
            delayed.Release.TrySetResult();
            failure.Release.TrySetResult();
        }
        if (claim is not null && disposition != "successor")
        {
            Assert.False((await claim.WaitAsync(Limit)).IsReady);
            if (disposition != "restored-unknown") Assert.Null(fixture.Engine.Capability.SwitchProof);
            Assert.Equal(2, fixture.Writes.Count);
        }
    }

    [Fact]
    public async Task PollCannotClearHeldStopOrRepeatIt()
    {
        await using var fixture = new Fixture();
        Assert.True((await fixture.SwitchAsync("a")).IsReady);
        fixture.StopReply = () => throw new HttpRequestException("Lost scripted stop reply.");
        Assert.False((await fixture.StopAsync()).Succeeded);
        await fixture.Engine.ProbeHealthOnceAsync(CancellationToken.None);
        Assert.Equal(NativeLaunchHealth.Invalid, fixture.Watch.Health);
        var writes = fixture.Writes.ToArray();
        Assert.Equal("native_peer_mutation_unresolved", (await fixture.StopAsync()).Reason);
        Assert.Equal(writes, fixture.Writes);
        Assert.Equal(3, fixture.InspectCount(A));
    }

    private static string InspectBody(string id, string startedAt = "2026-01-01T00:00:00.123456781Z",
        string state = "running", bool running = true, bool paused = false, bool restarting = false, bool dead = false) =>
        DockerLifecycleProviderTests.NativeContainerInspect(id, id == A ? "a" : "b", startedAt: startedAt,
            state: state, running: running, paused: paused, restarting: restarting, dead: dead);

    private static HttpResponseMessage InspectReply(string id, string startedAt = "2026-01-01T00:00:00.123456781Z",
        string state = "running", bool running = true, bool paused = false, bool restarting = false, bool dead = false) =>
        new(HttpStatusCode.OK) { Content = Json(InspectBody(id, startedAt, state, running, paused, restarting, dead)) };

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private sealed class Barrier
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task HoldAsync()
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(Limit);
        }
    }

    private sealed class ObservedResponse(TaskCompletionSource disposed) : HttpResponseMessage(HttpStatusCode.OK)
    {
        private int _count;
        public int Disposals => Volatile.Read(ref _count);
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            Interlocked.Increment(ref _count);
            disposed.TrySetResult();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request, ct);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Handler _daemon;
        private readonly Handler _app;
        private string? _resident;
        private string? _model;
        private readonly EngineDefinition _definition;
        private readonly ConcurrentQueue<string> _requests = new();
        private readonly SemaphoreSlim _healthRounds = new(0);
        private int _dispatchNudges;
        public LoopClock Clock { get; } = new();
        public EventRing Feed { get; } = new();
        public DockerLifecycleProvider Provider { get; }
        public EngineRuntime Engine { get; }
        public ConcurrentQueue<Task> Cleanups { get; } = new();
        public Func<string, Task<HttpResponseMessage>>? Inspect { get; set; }
        public Func<string, Task<HttpResponseMessage>>? Probe { get; set; }
        public Func<Task<HttpResponseMessage>>? StopReply { get; set; }
        public bool RefusePreflight { get; set; }
        public HttpStatusCode StartStatus { get; set; } = HttpStatusCode.NoContent;
        public NativeLaunchWatch Watch => Assert.IsType<NativeLaunchWatch>(Engine.Capability.NativeWatch);
        public int DispatchNudges => Volatile.Read(ref _dispatchNudges);
        public IReadOnlyList<string> Writes => _requests.Where(request => !request.StartsWith("GET ", StringComparison.Ordinal)).ToArray();
        public int InspectCount(string id) => _requests.Count(request => request == $"GET /v1.44/containers/{id}/json");
        public Task<bool> FirstTick => _healthRounds.WaitAsync(Limit);

        public Fixture(bool proveHealth = true, int callTimeoutMs = 1_000, int waitTimeoutMs = 1_000)
        {
            var docker = DockerLifecycleProviderTests.Cfg(waitHealth: proveHealth, removeStopped: false,
                callTimeoutMs: callTimeoutMs, waitTimeoutMs: waitTimeoutMs);
            _definition = new EngineDefinition
            {
                Name = "strata",
                Endpoint = new Uri("http://127.0.0.1:9/v1"),
                ModelsEndpoint = "/v1/models",
                Lifecycle = EngineLifecycleKind.Docker,
                Docker = docker,
                Catalog = new Dictionary<string, EngineModelDefinition>
                {
                    ["a"] = new() { Name = "a" },
                    ["b"] = new() { Name = "b" },
                },
            };
            _daemon = new Handler(DaemonResponseAsync);
            _app = new Handler((request, _) => Probe?.Invoke(request.RequestUri!.AbsolutePath) ??
                Task.FromResult(AppResponse(request.RequestUri!.AbsolutePath)));
            Provider = new DockerLifecycleProvider(docker, "strata", _daemon)
            {
                ObservationClock = Clock,
                ObservationCleanupForTests = Cleanups.Enqueue,
            };
            Engine = new EngineRuntime(_definition, new RouterConfig(), Feed, _app, Provider,
                dispatchHint: () => Interlocked.Increment(ref _dispatchNudges), hostEnrollment: null, clock: Clock)
                { ReadinessClockForTests = Clock };
            Feed.Subscribe(entry =>
            {
                if (entry.Kind == "engine.health") _healthRounds.Release();
            });
        }

        public async Task TickAsync()
        {
            Clock.Tick();
            Assert.True(await _healthRounds.WaitAsync(Limit));
        }

        public Task<EngineRuntime.SwitchResult> SwitchAsync(string model) =>
            Engine.WaitForModelAsync(model, Limit, CancellationToken.None);

        public Task<LifecycleResult> StopAsync() =>
            Provider.StopAsync(new("strata", "a", null, _definition.Catalog["a"]),
                [new(A, _definition.Docker!.Image, "a", true)], CancellationToken.None).AsTask();

        public void AssertUnreadyOnce()
        {
            Assert.False(Engine.Healthy);
            Assert.Null(Engine.Capability.SwitchProof);
            Assert.Single(Feed.Replay(0), entry => entry.Kind == "engine.switch_failed");
            Assert.DoesNotContain(Feed.Replay(0), entry => entry.Kind == "engine.switch_completed");
        }

        public HttpResponseMessage AppResponse(string path) => new(HttpStatusCode.OK)
        {
            Content = Json(path switch
            {
                "/v1/models" => $$"""{"data":[{"id":"{{_model}}"}]}""",
                "/props" => """{"n_ctx":1024,"total_slots":1}""",
                _ => throw new InvalidOperationException($"Unexpected application request {path}."),
            }),
        };

        private async Task<HttpResponseMessage> DaemonResponseAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            _requests.Enqueue($"{request.Method} {path}");
            if (path == "/v1.44/version") return Reply(HttpStatusCode.OK, """{"ApiVersion":"1.44"}""");
            if (path.Contains("/images/", StringComparison.Ordinal))
                return RefusePreflight ? Reply(HttpStatusCode.NotFound) : Reply(HttpStatusCode.OK, DockerLifecycleProviderTests.Inspect());
            if (path.StartsWith("/v1.44/containers/json?", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, _resident is { } id
                    ? $$$"""[{"Id":"{{{id}}}","Labels":{"arboreal.router":"r1","arboreal.engine":"strata","arboreal.model":"a"}}]"""
                    : "[]");
            if (path == "/v1.44/containers/create")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct));
                var model = json.RootElement.GetProperty("Labels").GetProperty("arboreal.model").GetString();
                return Reply(HttpStatusCode.Created, $$"""{"Id":"{{(model == "a" ? A : B)}}"}""");
            }
            if (path.EndsWith("/start", StringComparison.Ordinal))
            {
                _resident = path.Contains(A, StringComparison.Ordinal) ? A : B;
                _model = _resident == A ? "a" : "b";
                return Reply(StartStatus);
            }
            if (path.Contains("/stop?", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                if (StopReply is { } scripted) return await scripted();
                return Reply(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/json", StringComparison.Ordinal))
            {
                var observed = path.Contains(A, StringComparison.Ordinal) ? A : B;
                if (Inspect is { } scripted) return await scripted(observed);
                return InspectReply(observed);
            }
            throw new InvalidOperationException($"Unexpected fake daemon request {path}.");
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string value = "") =>
            new(status) { Content = Json(value) };

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Provider.Dispose();
            _daemon.Dispose();
            _app.Dispose();
            _healthRounds.Dispose();
        }
    }

    private sealed class LoopClock : TimeProvider
    {
        private readonly ReadinessDeadlineTests.ManualClock _single = new();
        private Action? _tick;
        public override long TimestampFrequency => _single.TimestampFrequency;
        public override long GetTimestamp() => _single.GetTimestamp();
        public void AdvanceTo(int milliseconds) => _single.AdvanceTo(milliseconds);
        public void Tick() => Assert.IsType<Action>(Volatile.Read(ref _tick)).Invoke();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period == Timeout.InfiniteTimeSpan) return _single.CreateTimer(callback, state, dueTime, period);
            Action tick = () => callback(state);
            Volatile.Write(ref _tick, tick);
            return new TickTimer(() => Interlocked.CompareExchange(ref _tick, null, tick));
        }

        private sealed class TickTimer(Action stop) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => stop();
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
