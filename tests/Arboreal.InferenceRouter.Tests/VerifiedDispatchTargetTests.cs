using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class VerifiedDispatchTargetTests
{
    private const string ImageId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string RepoDigest = "example/fixture@sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string OtherRepoDigest = "example/fixture@sha256:3333333333333333333333333333333333333333333333333333333333333333";
    private const string Configuration = "fixture-config";
    private const string BackendRoute = "fixture-route";
    private const string DefaultEndpoint = "http://127.0.0.1:18021/v1";

    [Fact]
    public void CanonicalAliasAndExplicitDefaultShareAPlanWhileResidentReuseKeepsItsTier()
    {
        var engine = Load();
        using var daemon = new FakeDockerDaemonHandler();
        using var projection = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        var resolver = new VerifiedDispatchTargetResolver(engine, projection);
        var canonical = resolver.Resolve("model.canonical", null, null, Configuration, BackendRoute);
        Assert.Equal("model.canonical", canonical.Model);
        Assert.Equal("wide", canonical.Tier);
        Assert.Equal(canonical, resolver.Resolve("ALIAS", null, null, Configuration, BackendRoute));
        Assert.Equal(canonical, resolver.Resolve("alias", "wide", null, Configuration, BackendRoute));
        var resident = resolver.Resolve("model.canonical", "resident", null, Configuration, BackendRoute);
        Assert.Equal(canonical, resolver.Resolve("alias", "wide", resident, Configuration, BackendRoute));
        Assert.Equal("resident", resident.Tier);
        Assert.Equal(resident, resolver.Resolve("alias", null, resident, Configuration, BackendRoute));
        Assert.NotEqual(canonical.PlanDigest, resident.PlanDigest);
        Assert.Empty(daemon.Requests);
    }

    [Fact]
    public void SingletonTierIsTheSameEffectivePlanWithOrWithoutAnOverride()
    {
        var engine = Load(singleton: true);
        using var daemon = new FakeDockerDaemonHandler();
        using var projection = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        var resolver = new VerifiedDispatchTargetResolver(engine, projection);
        var implicitTier = resolver.Resolve("alias", null, null, Configuration, BackendRoute);
        Assert.Equal("wide", implicitTier.Tier);
        Assert.Equal(implicitTier, resolver.Resolve("model.canonical", "wide", null, Configuration, BackendRoute));
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData("unknown-model", null)]
    [InlineData("model.unprojected", null)]
    [InlineData("alias", "WIDE")]
    [InlineData("alias", "unknown-profile")]
    public void RefusesUnknownExplicitOrUnprojectedTargets(string requested, string? selectedTier)
    {
        var engine = Load();
        using var daemon = new FakeDockerDaemonHandler();
        using var projection = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        var resolver = new VerifiedDispatchTargetResolver(engine, projection);

        var error = Assert.Throws<VerifiedDispatchBindingException>(
            () => resolver.Resolve(requested, selectedTier, null, Configuration, BackendRoute));

        Assert.Equal(VerifiedDispatchRefusal.Target, error.Refusal);
        Assert.Empty(daemon.Requests);
    }

    [Fact]
    public void RejectsAliasShadowingAnotherCanonicalSpellingBeforeProjection()
    {
        var error = Assert.Throws<RouterConfigException>(() => Load(alias: "MODEL.UNPROJECTED"));
        Assert.Contains("shared with another model", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("env")]
    [InlineData("mount")]
    [InlineData("memory")]
    [InlineData("manifest")]
    [InlineData("platform")]
    [InlineData("configuration")]
    [InlineData("route")]
    [InlineData("endpoint")]
    public void DifferentEffectiveInputsChangeTheDigestWithIdenticalTierAndCapacity(string change)
    {
        var baseline = Load();
        var changed = Load(
            mode: change == "env" ? "changed" : "normal",
            mount: change == "mount" ? "/fixture/other" : "/fixture/weights",
            memory: change == "memory" ? 8_388_608 : 6_291_456,
            digest: change == "manifest" ? OtherRepoDigest : RepoDigest,
            platform: change == "platform" ? "linux/arm64" : "linux/amd64",
            endpoint: change == "endpoint" ? "http://127.0.0.1:18021/other/v1" : DefaultEndpoint);
        using var daemon = new FakeDockerDaemonHandler();
        using var originalProjection = new DockerLifecycleProvider(baseline.Docker!, baseline.Name, daemon);
        using var changedProjection = new DockerLifecycleProvider(changed.Docker!, changed.Name, daemon);
        var oldTier = baseline.Catalog["model.canonical"].ServingProfiles.Single(p => p.Name == "wide");
        var newTier = changed.Catalog["model.canonical"].ServingProfiles.Single(p => p.Name == "wide");
        Assert.Equal(oldTier, newTier);
        var originalResolver = new VerifiedDispatchTargetResolver(baseline, originalProjection);
        var changedResolver = new VerifiedDispatchTargetResolver(changed, changedProjection);
        var original = originalResolver.Resolve("alias", "wide", null, Configuration, BackendRoute);
        var changedConfig = change == "configuration" ? "new-config" : Configuration;
        var changedRoute = change == "route" ? "new-route" : BackendRoute;
        var candidate = changedResolver.Resolve("alias", "wide", null, changedConfig, changedRoute);
        Assert.Equal(original.Model, candidate.Model);
        Assert.Equal(original.Tier, candidate.Tier);
        Assert.NotEqual(original.PlanDigest, candidate.PlanDigest);

        // Independently observe the projection and the equal-JSON image pin controls.
        var oldCreate = CreateBytes(originalProjection, baseline, oldTier);
        var newCreate = CreateBytes(changedProjection, changed, newTier);
        if (change is "manifest" or "platform" or "configuration" or "route" or "endpoint")
            Assert.Equal(oldCreate, newCreate);
        else
            Assert.False(oldCreate.AsSpan().SequenceEqual(newCreate));

        var oldProof = new VerifiedInstanceRoute("host", 1, original.Engine, original.PlanDigest,
            "fixture-container", "fixture-incarnation", 2, 3);
        Assert.True(original.Matches(oldProof));
        Assert.False(candidate.Matches(oldProof));
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("model")]
    public void RefusesForeignResidentReuseEvenWhenTheTierNameMatches(string foreign)
    {
        var engine = Load();
        using var daemon = new FakeDockerDaemonHandler();
        using var projection = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        var resolver = new VerifiedDispatchTargetResolver(engine, projection);
        var resident = resolver.Resolve("alias", "resident", null, Configuration, BackendRoute);
        var wrong = foreign == "engine" ? resident with { Engine = "other-engine" } : resident with { Model = "other-model" };

        var error = Assert.Throws<VerifiedDispatchBindingException>(
            () => resolver.Resolve("alias", null, wrong, Configuration, BackendRoute));

        Assert.Equal(VerifiedDispatchRefusal.Target, error.Refusal);
        Assert.Equal("wide", resolver.Resolve("alias", null, null, Configuration, BackendRoute).Tier);
        Assert.Empty(daemon.Requests);
    }

    [Fact]
    public void RefusesProjectionFromADifferentConfigWithTheSameLabelsAndImage()
    {
        var captured = Load();
        var other = Load(mode: "different");
        using var daemon = new FakeDockerDaemonHandler();
        using var expected = new DockerLifecycleProvider(captured.Docker!, captured.Name, daemon);
        using var foreign = new DockerLifecycleProvider(other.Docker!, other.Name, daemon);
        using var expectedJson = JsonDocument.Parse(CreateBytes(expected, captured,
            captured.Catalog["model.canonical"].ServingProfiles[0]));
        using var foreignJson = JsonDocument.Parse(CreateBytes(foreign, other,
            other.Catalog["model.canonical"].ServingProfiles[0]));
        Assert.True(JsonElement.DeepEquals(expectedJson.RootElement.GetProperty("Labels"), foreignJson.RootElement.GetProperty("Labels")));
        Assert.True(JsonElement.DeepEquals(expectedJson.RootElement.GetProperty("Image"), foreignJson.RootElement.GetProperty("Image")));
        Assert.False(JsonElement.DeepEquals(expectedJson.RootElement.GetProperty("Env"), foreignJson.RootElement.GetProperty("Env")));

        var error = Assert.Throws<VerifiedDispatchBindingException>(() => new VerifiedDispatchTargetResolver(captured, foreign));

        Assert.Equal(VerifiedDispatchRefusal.Target, error.Refusal);
        Assert.Empty(daemon.Requests);
    }

    private static byte[] CreateBytes(DockerLifecycleProvider projection, EngineDefinition engine, ServingProfile tier)
    {
        var target = new LifecycleTarget(engine.Name, "alias", tier, engine.Catalog["model.canonical"]);
        return JsonSerializer.SerializeToUtf8Bytes(projection.BuildCreateRequest(target, engine.Docker!.ImageIdentity!),
            DockerJsonContext.Default.DockerCreateRequest);
    }

    internal static EngineDefinition Load(
        string mode = "normal", string mount = "/fixture/weights", long memory = 6_291_456, string digest = RepoDigest,
        string platform = "linux/amd64", string alias = "alias", bool singleton = false, string endpoint = DefaultEndpoint)
    {
        const string residentTier = """
            [[engine.fixture.catalog."model.canonical".serving]]
            name = "resident"
            max_context = 32768
            max_slots = 2
            [engine.fixture.catalog."model.canonical".serving.docker]
            env = ["SETTING=resident"]
            """;
        var memoryLiteral = memory.ToString(System.Globalization.CultureInfo.InvariantCulture);
        const string command = """command = ["serve", "{{model}}", "{{profile}}"]""";
        var defaultField = singleton ? "" : "default_profile = \"wide\"";
        return RouterConfigLoader.Load($$"""
            [engine.fixture]
            kind = "openai-compatible"
            endpoint = "{{endpoint}}"
            lifecycle_owner = "router"
            [engine.fixture.docker]
            socket = "unix:///var/run/docker.sock"
            image = "{{ImageId}}"
            image_id = "{{ImageId}}"
            image_repo_digest = "{{digest}}"
            image_platform = "{{platform}}"
            router_id = "fixture-router"
            {{command}}
            [engine.fixture.catalog."model.canonical"]
            aliases = ["{{alias}}"]
            {{defaultField}}
            [[engine.fixture.catalog."model.canonical".serving]]
            name = "wide"
            max_context = 32768
            max_slots = 2
            [engine.fixture.catalog."model.canonical".serving.docker]
            env = ["SETTING={{mode}}"]
            bind_mounts = [{ source = "{{mount}}", target = "/weights", read_only = true }]
            memory_bytes = {{memoryLiteral}}
            {{(singleton ? "" : residentTier)}}
            [engine.fixture.catalog."model.unprojected"]
            """).Engines["fixture"];
    }
}
