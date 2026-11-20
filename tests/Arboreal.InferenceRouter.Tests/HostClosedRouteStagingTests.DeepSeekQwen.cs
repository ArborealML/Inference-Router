using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Tomlyn;
using Tomlyn.Model;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    private const string DeepSeekEngineName = "deepseek";
    private const string QwenEngineName = "strata";
    private const string DeepSeekPairModel = "deepseek-v4.1-flash-1017-iq2-xxs";
    private const string QwenPairModel = "qwen3.8-flash-next-iq3_s";
    private const string PairProbeKey = "deepseek-qwen-fixture-key";

    public enum DeepSeekQwenObservation { Intended, WrongImage, WrongModel, UnderWindow }

    private sealed record DeepSeekQwenTarget(EngineDefinition Engine, string Alias, LifecycleTarget Launch,
        VerifiedDispatchTarget Frozen, HostCheckpointPlan Plan, JsonElement Create, JsonElement ReferenceCreate);

    [Fact(Timeout = 30_000)]
    public async Task DeepSeekQwenPairFakeDedicatedInputsResolveButExactNativePairIsRefused()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = watchdog.Token;
        var deepSeek = ReadDeepSeekQwenConfig("deepseek.toml");
        var corrected = CorrectDeepSeekEndpoint(deepSeek);
        var original = LoadDeepSeekQwenEngine(deepSeek, DeepSeekEngineName);
        var maya = LoadDeepSeekQwenEngine(corrected, DeepSeekEngineName);
        var strata = ReadDeepSeekQwenConfig("strata.toml");
        var qwen = LoadDeepSeekQwenEngine(strata, QwenEngineName);
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var referenceStream = OpenPairResource("selector-reference.json");
        using var reference = JsonDocument.Parse(referenceStream);
        var target = CaptureDeepSeekQwen(maya, reference.RootElement, daemon);
        var uncorrected = CaptureDeepSeekQwen(original, reference.RootElement, daemon);
        Assert.True(JsonElement.DeepEquals(uncorrected.Create, target.Create));
        var endpointOnlyControl = ReadDeepSeekQwenDocument(corrected);
        DeepSeekQwenEngineTable(endpointOnlyControl, DeepSeekEngineName)["endpoint"] = original.Endpoint.AbsoluteUri;
        var root = LoadDeepSeekQwenEngine(WriteDeepSeekQwenDocument(endpointOnlyControl), DeepSeekEngineName);
        var rootTarget = CaptureDeepSeekQwen(root, reference.RootElement, daemon);
        Assert.Equal(uncorrected.Plan, rootTarget.Plan);
        Assert.Equal(rootTarget.Plan with { LaunchSettingsDigest = target.Plan.LaunchSettingsDigest }, target.Plan);
        Assert.NotEqual(rootTarget.Plan.LaunchSettingsDigest, target.Plan.LaunchSettingsDigest);
        Assert.NotEqual(rootTarget.Frozen.PlanDigest, target.Frozen.PlanDigest);
        Assert.Equal("/chat/completions", EngineRuntime.Combine(root.Endpoint, "chat/completions").AbsolutePath);
        Assert.Equal("/v1/chat/completions", EngineRuntime.Combine(maya.Endpoint, "chat/completions").AbsolutePath);
        await CheckDeepSeekPairProbesAsync(maya, ct);
        _ = CaptureDeepSeekQwen(qwen, reference.RootElement, daemon);
        Assert.Empty(daemon.Requests);

        var combined = ReadDeepSeekQwenDocument(strata);
        combined["auto_switch"] = false;
        var engines = Assert.IsType<TomlTable>(combined["engine"]);
        engines.Add(DeepSeekEngineName, DeepSeekQwenEngineTable(ReadDeepSeekQwenDocument(corrected), DeepSeekEngineName));
        var refused = Assert.Throws<RouterConfigException>(() =>
            RouterConfigLoader.Load(WriteDeepSeekQwenDocument(combined)));
        Assert.Equal("engine.strata.lifecycle_owner = router requires a dedicated single-engine configuration until managed peer fencing is available",
            refused.Message);

        using var unused = new UnusedSharedReadiness();
        var activation = await Assert.ThrowsAsync<RouterConfigException>(async () =>
        {
            await using var runtime = new EngineRuntime(maya, new RouterConfig(), new EventRing(), unused);
        });
        Assert.Equal(0, unused.Requests);
        Assert.Equal("engine.deepseek: serving-tier Docker targets require host qualification before activation", activation.Message);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(true, DeepSeekQwenObservation.Intended)]
    [InlineData(false, DeepSeekQwenObservation.Intended)]
    [InlineData(true, DeepSeekQwenObservation.WrongImage)]
    [InlineData(true, DeepSeekQwenObservation.WrongModel)]
    [InlineData(true, DeepSeekQwenObservation.UnderWindow)]
    public async Task DeepSeekQwenPairFakeStagesExactTargetAndSendsOnlyForCurrentRoute(
        bool toQwen, DeepSeekQwenObservation observation)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = watchdog.Token;
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var referenceStream = OpenPairResource("selector-reference.json");
        using var reference = JsonDocument.Parse(referenceStream);
        var deepSeek = LoadDeepSeekQwenEngine(CorrectDeepSeekEndpoint(ReadDeepSeekQwenConfig("deepseek.toml")), DeepSeekEngineName);
        var strata = LoadDeepSeekQwenEngine(ReadDeepSeekQwenConfig("strata.toml"), QwenEngineName);
        var maya = CaptureDeepSeekQwen(deepSeek, reference.RootElement, daemon);
        var qwen = CaptureDeepSeekQwen(strata, reference.RootElement, daemon);
        var (incumbent, successor) = toQwen ? (maya, qwen) : (qwen, maya);
        var observedPlan = observation == DeepSeekQwenObservation.WrongImage ? OtherDeepSeekQwenImage(successor) : successor.Plan;

        var host = new FakeHost
        {
            FrozenPlan = target =>
            {
                // The runtime rebuilds LifecycleTarget from its catalog. Select the capture by tuple.
                var plan = target.EngineName == incumbent.Plan.Engine ? incumbent.Plan : observedPlan;
                Assert.Equal(plan.Engine, target.EngineName);
                Assert.Equal(plan.Model, target.Model);
                Assert.Equal(plan.Tier, target.EffectiveProfile);
                return plan;
            },
        };
        var oldRoute = SeedDeepSeekQwenIncumbent(host, incumbent);
        var staging = new HostClosedRouteStaging(host.FindLaunch, DeepSeekQwenRoute, oldRoute);
        var issuer = new CertifiedPairIssuer(staging);
        var controller = new HostLifecycleCoordinator(ReservationPath, host, staging);
        var old = FreezePairChat(incumbent, oldRoute);
        await SendCertifiedPairChatAsync(incumbent, old, issuer, ct);
        var oldBytes = issuer.Bytes(old.Invocation);
        var incumbentTraffic = issuer.Traffic;

        var first = await RunDeepSeekQwenStageAsync(controller, staging, host, incumbent, successor, observation, ct);
        var operation = first.Launch.Operation;
        Assert.Equal(HostMutationKind.Launch, operation.Kind);
        Assert.NotEqual(Guid.Empty, operation.Id);
        Assert.Equal(oldRoute.LaunchEpoch + 1, operation.Epoch);
        Assert.Equal(successor.Frozen.Engine, operation.Engine);
        Assert.Equal(successor.Frozen.Model, operation.Target);
        Assert.Equal(successor.Frozen.Tier, operation.Profile);
        Assert.Equal(observedPlan, first.Launch.Plan);
        Assert.Equal(observedPlan.Digest(), operation.PlanDigest);
        Assert.True(first.Launch.IsValid());
        Assert.Equal(first.Launch, host.FindLaunch(operation));
        Assert.Equal(first.Launch.InstanceId, first.Route.ContainerId);
        Assert.NotEqual(oldRoute.ContainerId, first.Route.ContainerId);
        var retained = new HostReservationState(operation.Epoch, operation.Engine, operation,
            HostMutationPhase.LaunchApplied, first.Launch.InstanceId);
        Assert.Equal(retained, ReadReservation());
        Assert.Equal(1, host.Stops);
        Assert.Equal(1, host.Creates);
        // Initial occupant, pre-stop revalidation, post-stop vacancy and exact successor.
        Assert.Equal(4, host.Observations);
        Assert.Equal(incumbentTraffic, issuer.Traffic);

        if (observation != DeepSeekQwenObservation.Intended)
        {
            await AssertCertifiedPairRefusedAsync(FreezePairChat(incumbent, oldRoute), issuer, ct);
            var intendedRoute = first.Route with { EffectiveTarget = successor.Frozen.PlanDigest };
            await AssertCertifiedPairRefusedAsync(FreezePairChat(successor, intendedRoute), issuer, ct);
            Assert.Equal(incumbentTraffic, issuer.Traffic);
            Assert.Equal(2, issuer.NoTransfers);
            Assert.Equal(1, issuer.FirstWriteAttempts);
        }
        else
        {
            async Task<VerifiedInstanceRoute> RecoverAsync()
            {
                var recovered = await RunDeepSeekQwenStageAsync(controller, staging, host, incumbent, successor, observation, ct);
                Assert.Equal(first.Launch, recovered.Launch);
                Assert.Equal(retained, ReadReservation());
                Assert.Equal(1, host.Stops);
                Assert.Equal(1, host.Creates);
                return recovered.Route;
            }

            await CheckDeepSeekQwenCertifiedDispatchAsync(incumbent, successor, first.Route, issuer, RecoverAsync, ct);
        }

        Assert.Equal(retained, ReadReservation());
        Assert.Equal(oldBytes, issuer.Bytes(old.Invocation));
        Assert.Empty(daemon.Requests);
    }

    private async Task<HostClosedRouteStage> RunDeepSeekQwenStageAsync(
        HostLifecycleCoordinator controller, HostClosedRouteStaging staging, FakeHost host,
        DeepSeekQwenTarget incumbent, DeepSeekQwenTarget successor, DeepSeekQwenObservation observation,
        CancellationToken ct)
    {
        var tier = successor.Launch.Profile!;
        var fits = observation == DeepSeekQwenObservation.Intended;
        var install = fits || observation == DeepSeekQwenObservation.UnderWindow;
        var clock = new ReadinessDeadlineTests.ManualClock();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new EventRing();
        HostClosedRouteStage? read = null;
        HostStagedSwitchResult? hostResult = null;
        var installs = 0;
        var completions = 0;
        var fixture = new HostStagedRuntimeFixture((stage, _) =>
        {
            Assert.Null(read);
            Assert.True(host.Draining);
            Assert.False(staging.CanMint(stage.Route));
            read = stage;
            var digestMatches = successor.Frozen.Matches(stage.Route);
            Assert.Equal(observation != DeepSeekQwenObservation.WrongImage, digestMatches);
            if (!digestMatches) return Task.FromResult<HostStagedReadiness?>(null);

            var canonical = observation == DeepSeekQwenObservation.WrongModel ? incumbent.Frozen.Model : successor.Frozen.Model;
            var window = tier.MaxContext;
            if (observation == DeepSeekQwenObservation.UnderWindow)
            {
                Assert.Equal("256k-4", tier.Name);
                Assert.Equal(4, tier.MaxSlots);
                Assert.Equal(256_000L, EngineRuntime.RequiredWindow(tier, explicitContextMinimum: 0));
                window = 255_999;
            }
            return Task.FromResult<HostStagedReadiness?>(new(stage, canonical,
                new ServingCapacity(successor.Frozen.Model, window, tier.MaxSlots, tier.Name)));
        },
        AfterStageInstalled: () =>
        {
            installs++;
            Assert.Equal(read, HostClosedRouteStageFile.Read(ReservationPath));
            Assert.False(staging.CanMint(Assert.IsType<HostClosedRouteStage>(read).Route));
        },
        AfterComplete: () =>
        {
            completions++;
            Assert.True(host.Draining);
            Assert.False(staging.CanMint(Assert.IsType<HostClosedRouteStage>(read).Route));
        },
        AfterRun: () => complete.TrySetResult(),
        AfterHostResult: result => hostResult = result);

        // The fake runtime exercises the real settlement seam. The fake host carries the full
        // independently captured plan from the gated parsed config, not these fixture settings.
        var docker = successor.Engine.Docker!;
        var definition = new EngineDefinition
        {
            Name = successor.Engine.Name, Endpoint = successor.Engine.Endpoint,
            Catalog = successor.Engine.Catalog, LifecycleOwner = EngineLifecycleOwner.Router,
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = new DockerLifecycleConfig { Image = docker.Image, RouterId = docker.RouterId, WaitTimeoutMs = docker.WaitTimeoutMs },
        };
        var enrollment = new HostEngineEnrollment(docker.RouterId, definition.Name, controller, fixture);
        using var unused = new UnusedSharedReadiness();
        await using var runtime = new EngineRuntime(definition, new RouterConfig(), feed, unused, lifecycle: null,
            dispatchHint: null, hostEnrollment: enrollment) { ReadinessClockForTests = clock };
        var result = await runtime.WaitForProfileAsync(successor.Frozen.Model, tier, TimeSpan.FromSeconds(10), ct);
        await complete.Task.WaitAsync(ct);

        var stage = Assert.IsType<HostClosedRouteStage>(read);
        var staged = Assert.IsType<HostStagedSwitchResult>(hostResult);
        Assert.Equal(0, unused.Requests);
        Assert.Null(staged.DeliveryFailure);
        Assert.Equal(install ? 1 : 0, installs);
        Assert.Equal(fits ? 1 : 0, completions);
        Assert.Equal(fits, result.IsReady);
        Assert.Equal(fits, staged.Opened);
        Assert.Equal(fits, staging.CanMint(stage.Route));
        Assert.Equal(fits ? HostStagedVerdict.Committed : HostStagedVerdict.Refused, staged.Decision.Verdict);
        var terminal = Assert.Single(feed.Replay(0), entry => entry.Kind is "engine.switch_completed" or "engine.switch_failed");
        if (fits)
        {
            Assert.True(staged.Result.Succeeded);
            Assert.Equal("engine.switch_completed", terminal.Kind);
            Assert.Same(Assert.IsType<SwitchServingProof>(staged.Decision.Proof), runtime.Capability.SwitchProof);
            Assert.Equal(tier.MaxContext, runtime.Capability.Serving.WindowFor(successor.Frozen.Model));
            Assert.Equal(tier.MaxSlots, runtime.Capability.Serving.TotalSlots);
        }
        else
        {
            Assert.Equal("engine.switch_failed", terminal.Kind);
            Assert.Equal("host_publication_refused", staged.Result.Reason);
            Assert.Equal(SwitchReason.LaunchFailed, result.Detail);
            Assert.Null(staged.Decision.Proof);
            Assert.Null(runtime.Capability.SwitchProof);
            if (install) Assert.Equal(stage, HostClosedRouteStageFile.Read(ReservationPath));
            else Assert.Null(HostClosedRouteStageFile.Read(ReservationPath));
        }
        return stage;
    }

    private VerifiedInstanceRoute SeedDeepSeekQwenIncumbent(FakeHost host, DeepSeekQwenTarget incumbent)
    {
        HostReservation.ProvisionNew(ReservationPath);
        using var reservation = HostReservation.Open(ReservationPath);
        var epoch = reservation.Grant(incumbent.Plan.Engine);
        var operation = reservation.Begin(epoch, HostMutationKind.Launch, incumbent.Plan.Engine,
            incumbent.Plan.Model, incumbent.Plan.Tier, incumbent.Plan.Digest());
        const string instance = "deepseek-qwen-incumbent";
        reservation.MarkDispatching(operation);
        host.SetAppliedLaunch(operation, incumbent.Launch, instance);
        reservation.RecordAppliedLaunch(operation, instance);
        reservation.Settle(operation);
        return DeepSeekQwenRoute(new HostCheckpointLaunchEffect(operation, incumbent.Plan, instance), generation: 1);
    }

    private static VerifiedInstanceRoute DeepSeekQwenRoute(HostCheckpointLaunchEffect launch, long generation) =>
        new("deepseek-qwen-host", 1, launch.Operation.Engine, launch.Operation.PlanDigest!, launch.InstanceId,
            launch.InstanceId + "-serving", launch.Operation.Epoch, generation);

    private static DeepSeekQwenTarget CaptureDeepSeekQwen(EngineDefinition engine, JsonElement reference,
        FakeDockerDaemonHandler daemon)
    {
        var isMaya = engine.Name == DeepSeekEngineName;
        var (model, alias, spelling, tier, window, slots, port) = isMaya
            ? (DeepSeekPairModel, "deepseek", "32k", "32k", 32_768L, 1, "28060")
            : (QwenPairModel, "flash-next", "max-4", "256k-4", 256_000L, 4, "28021");
        var definition = engine.Catalog[model];
        Assert.Equal(isMaya ? "max" : "512k", definition.Profile);
        var profile = Assert.Single(definition.ServingProfiles, entry => entry.Name == tier);
        Assert.Equal(window, profile.MaxContext);
        Assert.Equal(slots, profile.MaxSlots);
        var launch = new LifecycleTarget(engine.Name, model, profile, definition);
        var docker = engine.Docker!;
        var image = Assert.IsType<DockerImageIdentity>(docker.ImageIdentity);
        using var provider = new DockerLifecycleProvider(docker, engine.Name, daemon);
        var resolver = new VerifiedDispatchTargetResolver(engine, provider);
        var frozen = resolver.Resolve(alias, spelling, resident: null, engine.Name + "-fixture-config", engine.Name + "-fixture-route");
        Assert.Equal(engine.Name, frozen.Engine);
        Assert.Equal(model, frozen.Model);
        Assert.Equal(tier, frozen.Tier);
        Assert.Equal(engine.Endpoint, frozen.Endpoint);

        var oracle = Assert.Single(reference.EnumerateArray(), row =>
            row.GetProperty("engine").GetString() == engine.Name &&
            row.GetProperty("model").GetString() == model && row.GetProperty("tier").GetString() == tier).GetProperty("create");
        var expected = ReadPairCreate(oracle);
        var actual = JsonSerializer.SerializeToElement(provider.BuildCreateRequest(launch, image), DockerJsonContext.Default.DockerCreateRequest);
        Assert.True(JsonElement.DeepEquals(oracle, actual), actual.GetRawText());
        Assert.Equal("8080/tcp", Assert.Single(expected.ExposedPorts!).Key);
        var binding = Assert.Single(expected.HostConfig!.PortBindings!);
        Assert.Equal("8080/tcp", binding.Key);
        var loopback = Assert.Single(binding.Value);
        Assert.Equal("127.0.0.1", loopback.HostIp);
        Assert.Equal(port, loopback.HostPort);
        var plan = DeepSeekQwenCheckpoint(engine, launch, image, expected);
        Assert.Equal(plan.Digest(), frozen.PlanDigest);
        return new(engine, alias, launch, frozen, plan, actual, oracle);
    }

    private static HostCheckpointPlan OtherDeepSeekQwenImage(DeepSeekQwenTarget target)
    {
        var original = target.Engine.Docker!.ImageIdentity!;
        var image = new DockerImageIdentity("sha256:" + new string('a', 64),
            "registry.invalid/pair/alternate@sha256:" + new string('b', 64), original.Platform);
        Assert.NotEqual(image.Id, original.Id);
        Assert.NotEqual(image.RepoDigest, original.RepoDigest);
        var create = ReadPairCreate(target.ReferenceCreate);
        Assert.Equal(original.Id, create.Image);
        create.Image = image.Id;
        var plan = DeepSeekQwenCheckpoint(target.Engine, target.Launch, image, create);
        Assert.NotEqual(plan.Digest(), target.Frozen.PlanDigest);
        return plan;
    }

    private static HostCheckpointPlan DeepSeekQwenCheckpoint(EngineDefinition engine, LifecycleTarget target,
        DockerImageIdentity image, DockerCreateRequest expected)
    {
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartArray();
            JsonSerializer.Serialize(writer, expected, DockerJsonContext.Default.DockerCreateRequest);
            writer.WriteStringValue(image.RepoDigest);
            writer.WriteStringValue(image.Platform);
            writer.WriteStringValue(engine.Endpoint.AbsoluteUri);
            writer.WriteEndArray();
        }
        return new(engine.Name, target.Model, target.EffectiveProfile, engine.Name + "-fixture-config",
            engine.Docker!.WaitTimeoutMs, engine.Name + "-fixture-route", Convert.ToHexString(SHA256.HashData(bytes.WrittenSpan)));
    }

    private static string ReadDeepSeekQwenConfig(string name)
    {
        using var stream = OpenPairResource(name);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static EngineDefinition LoadDeepSeekQwenEngine(string specimen, string engine)
    {
        var only = Assert.Single(RouterConfigLoader.Load(specimen).Engines);
        Assert.Equal(engine, only.Key);
        Assert.Equal<EngineLifecycleOwner?>(EngineLifecycleOwner.Router, only.Value.LifecycleOwner);
        return only.Value;
    }

    private static TomlTable ReadDeepSeekQwenDocument(string specimen) =>
        Assert.IsType<TomlTable>(TomlSerializer.Deserialize(specimen, ConfigTomlDocumentContext.Default.TomlTable));

    private static string WriteDeepSeekQwenDocument(TomlTable document) =>
        TomlSerializer.Serialize(document, ConfigTomlDocumentContext.Default.TomlTable);

    private static TomlTable DeepSeekQwenEngineTable(TomlTable document, string name) =>
        Assert.IsType<TomlTable>(Assert.IsType<TomlTable>(document["engine"])[name]);

    private static string CorrectDeepSeekEndpoint(string specimen)
    {
        var document = ReadDeepSeekQwenDocument(specimen);
        var engine = DeepSeekQwenEngineTable(document, DeepSeekEngineName);
        var root = Assert.IsType<string>(engine["endpoint"]);
        engine["endpoint"] = root + "/v1";
        engine["models_endpoint"] = "/v1/models";
        engine["loaded_requires_status_loaded"] = true;
        engine["models_context_window_path"] = "meta.n_ctx";
        engine["health_probe"] = new TomlTable
        {
            ["path"] = "/props",
            ["expect"] = "total_slots>0",
            ["context_path"] = "default_generation_settings.n_ctx",
            ["slots_path"] = "total_slots",
        };
        return WriteDeepSeekQwenDocument(document);
    }
}
