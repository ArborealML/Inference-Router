using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    private const string PairConfiguration = "strata-pair-fixture";
    private const string PairBackendRoute = "strata-pair-serving-route";
    private const string PairHost = "strata-pair-host";
    private const string OtherPairImageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public enum NativePairObservation { Intended, WrongImage, WrongModel, IncompatibleTier }

    private sealed record FrozenPairTarget(LifecycleTarget Launch, VerifiedDispatchTarget Frozen,
        HostCheckpointPlan ExpectedPlan, HostCheckpointPlan PrePortPlan, JsonElement ReferenceCreate);

    [Theory(Timeout = 30_000)]
    [InlineData(true, NativePairObservation.Intended)]
    [InlineData(false, NativePairObservation.Intended)]
    [InlineData(true, NativePairObservation.WrongImage)]
    [InlineData(true, NativePairObservation.WrongModel)]
    [InlineData(true, NativePairObservation.IncompatibleTier)]
    public async Task NativePairFakeHandoffMatchesFrozenTarget(bool toSwift, NativePairObservation observation)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = watchdog.Token;
        var engine = LoadPairEngine();
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var projection = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        using var referenceStream = OpenPairResource("selector-reference.json");
        using var reference = JsonDocument.Parse(referenceStream);
        var resolver = new VerifiedDispatchTargetResolver(engine, projection);
        var qwen = FreezePairTarget(engine, projection, resolver, reference.RootElement, "flash-next", "256k-4",
            "qwen3.8-flash-next-iq3_s", "256k-4", 256_000, 4);
        var swift = FreezePairTarget(engine, projection, resolver, reference.RootElement, "swift-flash-next", null,
            "swift-1.5-iq3_s", "max", 262_144, 1);
        var (incumbent, successor) = toSwift ? (qwen, swift) : (swift, qwen);
        var observedPlan = successor.ExpectedPlan;
        if (observation == NativePairObservation.WrongImage)
        {
            // This observation has a complete, different image-backed digest. The fake applies
            // a self-consistent operation for it so only the originally frozen target can veto it.
            var observedCreate = ReadPairCreate(successor.ReferenceCreate);
            Assert.NotEqual(observedCreate.Image, OtherPairImageId);
            observedCreate.Image = OtherPairImageId;
            observedPlan = PairCheckpoint(engine, successor.Launch, observedCreate);
            Assert.NotEqual(successor.Frozen.PlanDigest, observedPlan.Digest());
        }

        var observedTier = successor.Launch.Profile!;
        if (observation == NativePairObservation.IncompatibleTier)
        {
            observedTier = Assert.Single(swift.Launch.Definition.ServingProfiles, tier => tier.Name == "max-2");
            var intendedTier = swift.Launch.Profile!;
            Assert.Equal(intendedTier.MaxContext, observedTier.MaxContext);
            Assert.True(observedTier.MaxSlots > intendedTier.MaxSlots);
        }

        var host = new FakeHost { FrozenPlan = target => target == incumbent.Launch ? incumbent.ExpectedPlan : observedPlan };
        var oldRoute = SeedPairIncumbent(host, incumbent);
        var staging = new HostClosedRouteStaging(host.FindLaunch,
            (launch, generation) => PairRoute(launch.Operation, launch.InstanceId, generation), oldRoute);
        var intendedRoute = oldRoute;
        var routeChecks = new List<VerifiedInstanceRoute>();

        // This additional issuer gate is fixture-supplied. It checks the independently intended
        // route at both checkout (Mint) and first-write authorization.
        var issuer = new FixtureIssuer(staging)
        {
            AllowsRoute = route =>
            {
                routeChecks.Add(route);
                return route == intendedRoute;
            },
        };
        var oldInvocation = Invocation(oldRoute);
        var oldBody = PairBody(incumbent);
        await SendAsync(issuer, oldInvocation, oldBody, ct);
        AssertBody(oldBody, issuer.Bytes(oldInvocation));
        var grantsBefore = issuer.Grants;
        var permitsBefore = issuer.Permits;
        var routeChecksBefore = routeChecks.Count;

        var attempt = new SwitchAttemptId(Guid.NewGuid(), 1);
        var targetKey = new SwitchSampleKey(attempt.RuntimeEpoch, DockerLifecycleProvider.ProviderKind,
            successor.Frozen.Model, successor.Frozen.Tier, 1, SwitchObservationClass.FreshContainer, null);
        var opened = 0;
        var delivered = 0;
        var publications = 0;
        bool? digestMatched = null;
        bool? receivedEvidence = null;
        HostClosedRouteStage? readStage = null;

        var publication = new HostStagedDeadlinePublication(Guid.NewGuid(), attempt, targetKey,
            new ReadinessDeadlineTests.ManualClock(),
            (stage, _) =>
            {
                Assert.Null(readStage);
                Assert.False(staging.CanMint(stage.Route));
                readStage = stage;
                var model = observation == NativePairObservation.WrongModel ? incumbent.Frozen.Model : successor.Frozen.Model;
                var serving = new ServingCapacity(successor.Frozen.Model, observedTier.MaxContext,
                    observedTier.MaxSlots, observedTier.Name);
                return Task.FromResult<HostStagedReadiness?>(new(stage, model, serving));
            },
            (stage, evidence, deadline, commitClosed) =>
            {
                publications++;
                digestMatched = successor.Frozen.Matches(stage.Route);
                receivedEvidence = evidence is not null;
                // Fixture-supplied publication gate. The host verifies the observed operation
                // internally; it does not know the caller's original image-backed target.
                if (digestMatched != true || evidence is null || deadline.IsExpired ||
                    evidence.Serving.Profile != successor.Frozen.Tier) return default;

                intendedRoute = PairRoute(stage.Launch.Operation with { PlanDigest = successor.Frozen.PlanDigest },
                    stage.Launch.InstanceId, oldRoute.RouteIncarnation + 1);
                if (stage.Route != intendedRoute) return default;
                var proof = new SwitchServingProof(attempt, 1, targetKey, nativeLaunch: null);
                commitClosed(proof);
                return new(HostStagedVerdict.Committed, proof);
            },
            decision =>
            {
                Assert.Equal(HostStagedVerdict.Committed, decision.Verdict);
                Assert.False(staging.CanMint(intendedRoute));
                delivered++;
            },
            (stage, proof, open) => proof.Target == targetKey && stage.Route == intendedRoute && open(),
            () => opened++);

        var result = await new HostLifecycleCoordinator(ReservationPath, host, staging)
            .SwitchStagedAsync(successor.Launch, publication, ct);
        var stage = Assert.IsType<HostClosedRouteStage>(readStage);
        var operation = stage.Launch.Operation;
        Assert.Equal(HostMutationKind.Launch, operation.Kind);
        Assert.NotEqual(Guid.Empty, operation.Id);
        Assert.Equal(oldRoute.LaunchEpoch + 1, operation.Epoch);
        Assert.Equal(successor.Launch.EngineName, operation.Engine);
        Assert.Equal(successor.Frozen.Model, operation.Target);
        Assert.Equal(successor.Frozen.Tier, operation.Profile);
        Assert.Equal(observedPlan, stage.Launch.Plan);
        Assert.Equal(observedPlan.Digest(), operation.PlanDigest);
        Assert.True(stage.Launch.IsValid());
        Assert.Equal(stage.Launch, host.FindLaunch(operation));
        Assert.NotEqual(oldRoute.ContainerId, stage.Launch.InstanceId);
        Assert.Equal(stage.Launch.InstanceId, stage.Route.ContainerId);
        Assert.Equal(new HostReservationState(operation.Epoch, operation.Engine, operation,
            HostMutationPhase.LaunchApplied, stage.Launch.InstanceId), ReadReservation());
        Assert.Equal(1, host.Stops);
        Assert.Equal(1, host.Creates);
        Assert.Equal(1, publications);
        Assert.Equal(observation != NativePairObservation.WrongImage, digestMatched);
        Assert.Equal(observation != NativePairObservation.WrongModel, receivedEvidence);
        Assert.Null(result.DeliveryFailure);
        Assert.Empty(daemon.Requests);

        var body = PairBody(successor);
        await AssertDeniedAsync(issuer, Invocation(oldRoute), oldBody, ct);
        var current = PairRoute(operation with { PlanDigest = successor.Frozen.PlanDigest },
            stage.Launch.InstanceId, oldRoute.RouteIncarnation + 1);
        if (observation != NativePairObservation.Intended)
        {
            Assert.Equal("host_publication_refused", result.Result.Reason);
            Assert.Equal(HostStagedVerdict.Refused, result.Decision.Verdict);
            Assert.False(result.Opened);
            Assert.Equal(0, opened);
            Assert.Equal(0, delivered);
            await AssertDeniedAsync(issuer, Invocation(stage.Route), body, ct);
            await AssertDeniedAsync(issuer, Invocation(current), body, ct);
            Assert.Equal(grantsBefore, issuer.Grants);
            Assert.Equal(permitsBefore, issuer.Permits);
            Assert.Equal(routeChecksBefore, routeChecks.Count);
            AssertBody(oldBody, issuer.Bytes(oldInvocation));
            return;
        }

        Assert.True(result.Result.Succeeded);
        Assert.Equal(HostStagedVerdict.Committed, result.Decision.Verdict);
        Assert.True(result.Opened);
        Assert.Equal(1, opened);
        Assert.Equal(1, delivered);
        Assert.Equal(current, intendedRoute);
        Assert.Equal(stage.Route, intendedRoute);
        var stalePortRoute = current with { EffectiveTarget = successor.PrePortPlan.Digest() };
        Assert.False(successor.Frozen.Matches(stalePortRoute));
        await AssertDeniedAsync(issuer, Invocation(stalePortRoute), body, ct);

        var invocation = Invocation(current);
        await SendAsync(issuer, invocation, body, ct);
        AssertBody(body, issuer.Bytes(invocation));
        Assert.Equal(grantsBefore + 1, issuer.Grants);
        Assert.Equal(permitsBefore + 1, issuer.Permits);
        Assert.Collection(routeChecks.Skip(routeChecksBefore),
            route => Assert.Equal(current, route), route => Assert.Equal(current, route));
    }

    private VerifiedInstanceRoute SeedPairIncumbent(FakeHost host, FrozenPairTarget incumbent)
    {
        HostReservation.ProvisionNew(ReservationPath);
        using var reservation = HostReservation.Open(ReservationPath);
        var epoch = reservation.Grant(incumbent.Launch.EngineName);
        var operation = reservation.Begin(epoch, HostMutationKind.Launch, incumbent.Launch.EngineName,
            incumbent.Frozen.Model, incumbent.Frozen.Tier, incumbent.Frozen.PlanDigest);
        const string instance = "strata-pair-incumbent";
        reservation.MarkDispatching(operation);
        host.SetAppliedLaunch(operation, incumbent.Launch, instance);
        reservation.RecordAppliedLaunch(operation, instance);
        // The seed stands for an already settled incumbent. Each direction owns a fresh reservation.
        reservation.Settle(operation);
        return PairRoute(operation, instance, 1);
    }

    private static VerifiedInstanceRoute PairRoute(HostMutation operation, string instance, long generation) =>
        new(PairHost, 1, operation.Engine, operation.PlanDigest!, instance, instance + "-serving", operation.Epoch, generation);

    private static byte[] PairBody(FrozenPairTarget target) =>
        Encoding.UTF8.GetBytes($$"""{"model":"{{target.Frozen.Model}}","messages":[{"role":"user","content":"fixture"}]}""");

    private static Stream OpenPairResource(string name) =>
        typeof(HostClosedRouteStagingTests).Assembly.GetManifestResourceStream("SelectorFixtures/" + name)
        ?? throw new InvalidOperationException($"Missing native pair fixture {name}.");

    private static EngineDefinition LoadPairEngine()
    {
        using var stream = OpenPairResource("strata.toml");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return RouterConfigLoader.Load(reader.ReadToEnd()).Engines["strata"];
    }

    private static FrozenPairTarget FreezePairTarget(EngineDefinition engine, DockerLifecycleProvider projection,
        VerifiedDispatchTargetResolver resolver, JsonElement reference, string spelling, string? requestedTier,
        string model, string tier, long context, int slots)
    {
        var selected = engine.Docker!.TargetPlans!.Resolve(engine.Name, spelling, requestedTier);
        Assert.NotNull(selected);
        Assert.Equal(model, selected.Model);
        Assert.Equal(tier, selected.Profile);
        var frozen = resolver.Resolve(spelling, requestedTier, resident: null, PairConfiguration, PairBackendRoute);
        Assert.Equal(model, frozen.Model);
        Assert.Equal(tier, frozen.Tier);
        var definition = engine.Catalog[model];
        var profile = Assert.Single(definition.ServingProfiles, p => p.Name == tier);
        Assert.Equal(context, profile.MaxContext);
        Assert.Equal(slots, profile.MaxSlots);

        var expectedCreate = Assert.Single(reference.EnumerateArray(), row =>
            row.GetProperty("engine").GetString() == engine.Name &&
            row.GetProperty("model").GetString() == model && row.GetProperty("tier").GetString() == tier).GetProperty("create");
        var launch = new LifecycleTarget(engine.Name, model, profile, definition);
        var actual = projection.BuildCreateRequest(launch, engine.Docker.ImageIdentity!);
        Assert.True(JsonElement.DeepEquals(expectedCreate,
            JsonSerializer.SerializeToElement(actual, DockerJsonContext.Default.DockerCreateRequest)));
        var expected = ReadPairCreate(expectedCreate);
        Assert.Equal("8080/tcp", Assert.Single(expected.ExposedPorts!).Key);
        var binding = Assert.Single(expected.HostConfig!.PortBindings!);
        Assert.Equal("8080/tcp", binding.Key);
        var loopback = Assert.Single(binding.Value);
        Assert.Equal("127.0.0.1", loopback.HostIp);
        Assert.Equal("28021", loopback.HostPort);

        // The expected settings are decoded from the archived, redacted oracle, independently of
        // the provider output. The published checkpoint digest must agree with the typed witness.
        var expectedPlan = PairCheckpoint(engine, launch, expected);
        Assert.Equal(expectedPlan.Digest(), frozen.PlanDigest);
        expected.ExposedPorts = null;
        var prePortCreate = JsonSerializer.SerializeToElement(expected, DockerJsonContext.Default.DockerCreateRequest);
        Assert.False(prePortCreate.TryGetProperty("ExposedPorts", out _));
        Assert.Equal(expectedCreate.GetPropertyCount() - 1, prePortCreate.GetPropertyCount());
        foreach (var field in expectedCreate.EnumerateObject())
            if (!field.NameEquals("ExposedPorts"))
                Assert.True(JsonElement.DeepEquals(field.Value, prePortCreate.GetProperty(field.Name)), field.Name);
        var prePortPlan = PairCheckpoint(engine, launch, expected);
        Assert.NotEqual(expectedPlan.LaunchSettingsDigest, prePortPlan.LaunchSettingsDigest);
        Assert.NotEqual(frozen.PlanDigest, prePortPlan.Digest());
        return new(launch, frozen, expectedPlan, prePortPlan, expectedCreate);
    }

    private static DockerCreateRequest ReadPairCreate(JsonElement oracle) =>
        JsonSerializer.Deserialize(oracle, DockerJsonContext.Default.DockerCreateRequest)
        ?? throw new InvalidOperationException("The native pair oracle has no create document.");

    private static HostCheckpointPlan PairCheckpoint(EngineDefinition engine, LifecycleTarget target, DockerCreateRequest expected)
    {
        var docker = engine.Docker!;
        var identity = docker.ImageIdentity!;
        var bytes = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartArray();
            JsonSerializer.Serialize(writer, expected, DockerJsonContext.Default.DockerCreateRequest);
            writer.WriteStringValue(identity.RepoDigest);
            writer.WriteStringValue(identity.Platform);
            writer.WriteStringValue(engine.Endpoint.AbsoluteUri);
            writer.WriteEndArray();
        }
        var digest = Convert.ToHexString(SHA256.HashData(bytes.WrittenSpan));
        return new(target.EngineName, target.Model, target.EffectiveProfile, PairConfiguration,
            docker.WaitTimeoutMs, PairBackendRoute, digest);
    }
}
