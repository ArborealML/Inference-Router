using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerTargetProjectionTests
{
    private const long ParkingShm = 38_654_705_664L;
    private const string TierShmPath = "engine.strata.catalog.qwen.serving[0].docker.shm_size_bytes";
    private const string CommonShmPath = "engine.strata.docker.resources.shm_size_bytes";
    private const string TierShmToml = """
        [engine.strata.catalog.qwen]
        serving = [{ name = "parked", max_context = 100, docker = { shm_size_bytes = 38654705664 } }]
        """;
    private const string TierShmJson = """
        "qwen":{"serving":[{"name":"parked","max_context":100,"docker":{"shm_size_bytes":38654705664}}]}
        """;

    [Fact]
    public void HyperQwenParkingShmProjectsShmOnlyWithoutCommonResourcesAndFreezesDto()
    {
        var source = new DockerTargetDto { ShmSizeBytes = ParkingShm };
        var engine = DeepSeekFromDto(source);
        Assert.Throws<RouterConfigException>(() => source.ShmSizeBytes = 1);

        using var projected = ProjectDeepSeekTest(engine);
        var host = projected.RootElement.GetProperty("HostConfig");
        Assert.Equal(ParkingShm, host.GetProperty("ShmSize").GetInt64());
        Assert.Single(host.EnumerateObject());

        var omission = new DockerTargetDto { Env = ["TARGET=kept"] };
        Assert.Null(omission.ShmSizeBytes);
        Assert.Throws<RouterConfigException>(() => omission.ShmSizeBytes = null);
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekFromDto(new DockerTargetDto { ShmSizeBytes = 0 }));
        Assert.StartsWith(TierShmPath + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HyperQwenParkingShmInheritsForOrdinaryAndUnplannedModels(bool hasCommon)
    {
        var common = hasCommon ? "[engine.strata.docker.resources]\nShm_Size_Bytes = 2147483648" : "";
        var engine = LoadToml("""
            [engine.strata.catalog.qwen]
            default_profile = "ordinary"
            serving = [
              { name = "ordinary", max_context = 100, docker = { env = ["TARGET=ordinary"] } },
              { name = "parked", max_context = 100, docker = { Shm_Size_Bytes = 38654705664 } }
            ]
            [engine.strata.catalog.other]
            """, common);
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        foreach (var model in new[] { "qwen", "other" })
        {
            var create = provider.BuildCreateRequest(Selection(engine, model), engine.Docker!.ImageIdentity!);
            Assert.Equal(hasCommon ? 2_147_483_648L : (long?)null, create.HostConfig?.ShmSize);
            Assert.Equal(hasCommon, create.HostConfig is not null);
        }
        var parked = provider.BuildCreateRequest(Selection(engine, "qwen", "parked"), engine.Docker!.ImageIdentity!);
        Assert.Equal(ParkingShm, parked.HostConfig!.ShmSize);
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    [InlineData("\"38654705664\"")]
    public void HyperQwenParkingShmRejectsInvalidJsonTierSize(string size)
    {
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekTierJson($"\"shm_size_bytes\":{size}"));
        Assert.StartsWith(TierShmPath + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shm_size_bytes = 0")]
    [InlineData("shm_size_bytes = 1.5")]
    [InlineData("shm_size_bytes = \"38654705664\"")]
    [InlineData("shm_size_bytes = 1, Shm_Size_Bytes = 2")]
    public void HyperQwenParkingShmRejectsInvalidOrShadowedTomlTierSize(string fields)
    {
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekTierToml(fields));
        Assert.StartsWith(TierShmPath + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""" "shm_size_bytes":1,"shm_size_bytes":2 """)]
    [InlineData(""" "shm_size_bytes":1,"Shm_Size_Bytes":2 """)]
    [InlineData(""" "Shm_Size_Bytes":2 """)]
    public void HyperQwenParkingShmRejectsDuplicateOrNoncanonicalJsonTierSize(string fields)
    {
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekTierJson(fields));
        Assert.StartsWith(TierShmPath + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""" "shm_size_bytes":0 """)]
    [InlineData(""" "shm_size_bytes":null """)]
    [InlineData(""" "shm_size_bytes":9223372036854775808 """)]
    [InlineData(""" "shm_size_bytes":1,"Shm_Size_Bytes":2 """)]
    public void HyperQwenParkingShmStillValidatesSupersededJsonCommonSize(string fields)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadJson(TierShmJson, ",\"resources\":{" + fields + "}"));
        Assert.StartsWith(CommonShmPath + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shm_size_bytes = -1")]
    [InlineData("shm_size_bytes = 1\nShm_Size_Bytes = 2")]
    public void HyperQwenParkingShmStillValidatesSupersededTomlCommonSize(string fields)
    {
        var error = Assert.Throws<RouterConfigException>(() =>
            LoadToml(TierShmToml, "[engine.strata.docker.resources]\n" + fields));
        Assert.StartsWith(CommonShmPath + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"Engine":{"strata":{"docker":{"resources":{"shm_size_bytes":1}}}},"engine":{}}""", "engine")]
    [InlineData("""{"engine":{"strata":{"docker":{"resources":{"shm_size_bytes":1}}},"strata":{}}}""", "engine.strata")]
    [InlineData("""{"engine":{"strata":{"Docker":{"resources":{"shm_size_bytes":1}},"docker":{}}}}""", "engine.strata.docker")]
    [InlineData("""{"engine":{"strata":{"docker":{"Resources":{"shm_size_bytes":1},"resources":{}}}}}""", "engine.strata.docker.resources")]
    [InlineData("""{"engine":{"strata":{"catalog":{"qwen":{"serving":[{"docker":{"shm_size_bytes":1}}],"serving":[]}}}}}""",
        "engine.strata.catalog.qwen.serving")]
    public void HyperQwenParkingShmDoesNotDisappearUnderShadowedJsonAncestors(string config, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(config, isJson: true));
        Assert.StartsWith(path + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Engine.strata.docker.resources]\nshm_size_bytes = 1\n[engine.strata.docker.resources]\n", "engine")]
    [InlineData("[engine.Strata.docker.resources]\nshm_size_bytes = 1\n[engine.strata.docker.resources]\n", "engine.Strata")]
    [InlineData("[engine.strata.Docker.resources]\nshm_size_bytes = 1\n[engine.strata.docker]\n", "engine.strata.docker")]
    [InlineData("[engine.strata.docker.Resources]\nshm_size_bytes = 1\n[engine.strata.docker.resources]\n", "engine.strata.docker.resources")]
    public void HyperQwenParkingShmDoesNotDisappearUnderShadowedTomlAncestors(string config, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(config));
        Assert.StartsWith(path + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "[engine.strata]\nshm_size_bytes = 1", "engine.strata.shm_size_bytes")]
    [InlineData(true, """{"engine":{"strata":{"shm_size_bytes":1}}}""", "engine.strata.shm_size_bytes")]
    [InlineData(false, "[engine.strata.docker]\nshm_size_bytes = 1", "engine.strata.docker.shm_size_bytes")]
    [InlineData(true, """{"engine":{"strata":{"docker":{"shm_size_bytes":1}}}}""", "engine.strata.docker.shm_size_bytes")]
    [InlineData(false, "[engine.strata.catalog.qwen]\nshm_size_bytes = 1", "engine.strata.catalog.qwen.shm_size_bytes")]
    [InlineData(true, """{"engine":{"strata":{"catalog":{"qwen":{"shm_size_bytes":1}}}}}""", "engine.strata.catalog.qwen.shm_size_bytes")]
    [InlineData(false, "[engine.strata.catalog.qwen.Docker]\nshm_size_bytes = 1", "engine.strata.catalog.qwen.docker.shm_size_bytes")]
    [InlineData(true, """{"engine":{"strata":{"catalog":{"qwen":{"Docker":{"SHM_SIZE_BYTES":1},"docker":{}}}}}}""",
        "engine.strata.catalog.qwen.docker.shm_size_bytes")]
    [InlineData(false, "[[engine.strata.catalog.qwen.serving]]\nshm_size_bytes = 1", "engine.strata.catalog.qwen.serving[0].shm_size_bytes")]
    [InlineData(true, """{"engine":{"strata":{"catalog":{"qwen":{"serving":[{"shm_size_bytes":1}]}}}}}""",
        "engine.strata.catalog.qwen.serving[0].shm_size_bytes")]
    public void HyperQwenParkingShmRejectsMisplacedDeclarationsEvenWithoutTierOptIn(bool isJson, string config, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(config, isJson: isJson));
        Assert.StartsWith(path + " ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperQwenParkingShmPreservesUnrelatedLegacyModelDockerAndCommonSize()
    {
        var engine = LoadJson(""" "qwen":{"docker":{"network_mode":"legacy"}} """,
            ""","resources":{"shm_size_bytes":2147483648} """);
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        using var projected = JsonDocument.Parse(Project(provider, engine, "qwen"));
        Assert.Equal(2_147_483_648L, projected.RootElement.GetProperty("HostConfig").GetProperty("ShmSize").GetInt64());
        Assert.Empty(daemon.Requests);
    }
}
