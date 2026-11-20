using System.Net;
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

    [Theory(Timeout = 40_000)]
    [InlineData("untouched")]
    [InlineData("acknowledged")]
    [InlineData("held")]
    [InlineData("recovered")]
    [InlineData("shutdown")]
    [InlineData("contradiction")]
    public async Task NativeRefusalRevalidatesIncumbentAtCommit(string disposition)
    {
        const string id = DockerLifecycleProviderTests.FullPeerId;
        var submitted = disposition == "acknowledged" ? id.ToUpperInvariant() : id;
        var stop = $"/v1.44/containers/{submitted}/stop?t=30";
        var loaded = false;
        var absent = false;
        var cfg = DockerLifecycleProviderTests.Cfg(waitHealth: false, removeStopped: false);
        using var inference = new HeldRefusalProbe { LoadedModels = [] };
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path =>
            {
                if (path.StartsWith("/v1.44/containers/json?", StringComparison.Ordinal))
                    return (HttpStatusCode.OK, loaded && !absent
                        ? $$$"""[{"Id":"{{{id}}}","Labels":{"arboreal.router":"r1","arboreal.engine":"strata","arboreal.model":"incumbent"}}]"""
                        : "[]");
                if (path == "/v1.44/version") return (HttpStatusCode.OK, """{"ApiVersion":"1.44"}""");
                if (path.Contains("/images/", StringComparison.Ordinal))
                    return loaded ? (HttpStatusCode.NotFound, "") : (HttpStatusCode.OK, DockerLifecycleProviderTests.Inspect());
                if (path == "/v1.44/containers/create" && !loaded)
                    return (HttpStatusCode.Created, $$$"""{"Id":"{{{id}}}"}""");
                if (path == $"/v1.44/containers/{id}/start" && !loaded)
                {
                    loaded = true;
                    inference.LoadedModels = ["incumbent"];
                    return (HttpStatusCode.NoContent, "");
                }
                if (path == stop)
                    return disposition == "acknowledged" ? (HttpStatusCode.NoContent, "") : throw new HttpRequestException("lost stop reply");
                if (path == $"/v1.44/containers/{submitted}/json")
                    return (absent ? HttpStatusCode.NotFound : HttpStatusCode.OK, "{}");
                throw new InvalidOperationException($"Unexpected fake daemon request {path}");
            },
        };
        using var provider = new DockerLifecycleProvider(cfg, "strata", daemon);
        var feed = new EventRing();
        var definition = new EngineDefinition
        {
            Name = "strata",
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = cfg,
            Catalog = new Dictionary<string, EngineModelDefinition>
            {
                ["incumbent"] = new() { Name = "incumbent" },
                ["target"] = new() { Name = "target" },
            },
        };
        await using var engine = new EngineRuntime(definition, new RouterConfig(), feed, inference, lifecycle: provider);
        Assert.True((await engine.WaitForModelAsync("incumbent", Limit, CancellationToken.None)).IsReady);
        var incumbent = Assert.IsType<SwitchServingProof>(engine.Capability.SwitchProof);
        Assert.Equal(id, incumbent.NativeLaunch?.ContainerId);
        var nativeTarget = new LifecycleTarget(definition.Name, "incumbent", null, definition.Catalog["incumbent"]);
        EnginePeer[] peers = [new(submitted, cfg.Image, "incumbent", OwnedByUs: true)];
        var initialRequests = daemon.Requests.Count;
        if (disposition is "held" or "recovered")
            Assert.False((await provider.StopAsync(nativeTarget, peers, CancellationToken.None)).Succeeded);
        if (disposition == "recovered")
        {
            absent = true;
            Assert.Empty(await provider.OwnedPeersAsync(nativeTarget, CancellationToken.None));
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preflightEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreflight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (disposition is "shutdown" or "contradiction")
        {
            daemon.AsyncResponder = async (path, ct) =>
            {
                if (path.Contains("/images/", StringComparison.Ordinal))
                {
                    preflightEntered.TrySetResult();
                    await releasePreflight.Task.WaitAsync(Limit, CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                }
                return daemon.Responder(path);
            };
        }
        if (disposition == "contradiction")
        {
            engine.BeforeNativeFailureEventForTests = () =>
            {
                entered.TrySetResult();
                release.Task.WaitAsync(Limit).GetAwaiter().GetResult();
            };
        }
        var failuresAtWake = 0;
        EngineCapability? unknown = null;
        engine.AfterFailedMutationUnknownForTests = snapshot => unknown = snapshot;
        engine.AfterEndCommitForTests = transitionHeld =>
        {
            using var probe = new CancellationTokenSource();
            var entry = provider.EnterOperationAsync(probe.Token);
            var providerFree = entry.IsCompletedSuccessfully;
            if (providerFree) provider.ExitOperation();
            else probe.Cancel();
            ended.TrySetResult(!transitionHeld && providerFree);
        };
        engine.AfterReadyWaitForTests = _ => failuresAtWake = feed.Replay(0).Count(e => e.Kind == "engine.switch_failed");
        var holdFeed = disposition is "untouched" or "acknowledged";
        feed.Subscribe(e =>
        {
            if (!holdFeed || e.Kind != "engine.switch_failed" || e.Model != "target") return;
            entered.TrySetResult();
            release.Task.WaitAsync(Limit).GetAwaiter().GetResult();
        });

        var refusal = engine.WaitForModelAsync("target", Limit, CancellationToken.None);
        try
        {
            if (holdFeed) await entered.Task.WaitAsync(Limit);
            if (disposition == "acknowledged")
                Assert.True((await provider.StopAsync(nativeTarget, peers, CancellationToken.None)).Succeeded);
            if (disposition == "shutdown")
            {
                await preflightEntered.Task.WaitAsync(Limit);
                await engine.DisposeAsync();
            }
            if (disposition == "contradiction")
            {
                await preflightEntered.Task.WaitAsync(Limit);
                var marker = engine.Capability;
                Assert.Equal("target", marker.SwitchingToward);
                inference.LoadedModels = ["different"];
                inference.HoldModels = true;
                var probe = engine.ProbeHealthOnceAsync(CancellationToken.None);
                await inference.Entered.Task.WaitAsync(Limit);
                releasePreflight.TrySetResult();
                await entered.Task.WaitAsync(Limit);
                Assert.DoesNotContain(feed.Replay(0), entry => entry.Kind == "engine.switch_failed");
                Assert.False(refusal.IsCompleted);
                inference.Release.TrySetResult();
                Assert.True(await probe.WaitAsync(Limit));
                Assert.Same(marker, engine.Capability);
            }
        }
        finally
        {
            releasePreflight.TrySetResult();
            inference.Release.TrySetResult();
            release.TrySetResult();
        }

        var result = await refusal;
        Assert.True(await ended.Task.WaitAsync(Limit));
        Assert.False(result.IsReady);
        Assert.Equal(disposition == "shutdown" ? 0 : 1,
            feed.Replay(0).Count(e => e.Kind == "engine.switch_failed"));
        Assert.Null(engine.SwitchingToward);
        Assert.False(engine.Capability.HealthFromClaim);
        Assert.Equal(["incumbent"], engine.LoadedModels);
        if (disposition == "untouched")
        {
            Assert.True(engine.Healthy);
            Assert.Same(incumbent, engine.Capability.SwitchProof);
            Assert.Null(unknown);
        }
        else
        {
            Assert.False(engine.Healthy);
            Assert.Null(engine.Capability.SwitchProof);
            if (disposition == "contradiction")
            {
                Assert.Empty(engine.Capability.ModelContextWindows);
                Assert.Equal(default, engine.Capability.Serving);
                Assert.Null(unknown);
            }
            else if (disposition != "shutdown")
            {
                var captured = Assert.IsType<EngineCapability>(unknown);
                Assert.Equal("target", captured.SwitchingToward);
                Assert.False(captured.HealthFromClaim);
                Assert.Null(captured.SwitchProof);
            }
        }
        if (disposition != "shutdown")
        {
            Assert.Equal("launch_failed", result.Detail);
            Assert.Equal(1, failuresAtWake);
        }
        else Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, result.Outcome);
        var writes = daemon.Requests.Skip(initialRequests)
            .Where(request => !request.StartsWith("GET ", StringComparison.Ordinal));
        string[] expectedWrites = disposition is "untouched" or "shutdown" or "contradiction" ? [] : [$"POST {stop}"];
        Assert.Equal(expectedWrites, writes);

        if (disposition == "held")
        {
            var beforeReset = engine.Capability;
            var priorCalls = daemon.Requests.Count;
            lock (engine.ReloadTransitionGate)
                Assert.True(engine.ResetNativeTargetUnderGate("target", ""));
            Assert.Same(beforeReset, engine.Capability);
            Assert.Equal(priorCalls, daemon.Requests.Count);
            var fenced = await engine.WaitForModelAsync("target", Limit, CancellationToken.None);
            Assert.Equal(SwitchReason.LaunchFailed, fenced.Detail);
            Assert.Equal($"GET /v1.44/containers/{submitted}/json", Assert.Single(daemon.Requests.Skip(priorCalls)));
            Assert.False(engine.Healthy);
        }
    }

    private sealed class HeldRefusalProbe : FakeEngineHandler
    {
        internal bool HoldModels { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (HoldModels && request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await base.SendAsync(request, ct);
        }
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
