using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerTargetProjectionTests
{
    private const string SingleGpuTomlCatalog = """
        [engine.strata.catalog.qwen]
        serving = [{ name = "tier", max_context = 100, gpu = { device_ids = ["1"] } }]
        """;

    [Theory]
    [InlineData("""gpu = { device_ids = ["1", "0"] }""", false)]
    [InlineData("gpu = { all = true }", true)]
    public void HyperQwenSingleGpuInheritsCommonAndKeepsNoPlanFallback(string commonGpu, bool all)
    {
        var engine = LoadToml("""
            [engine.strata.catalog.qwen]
            default_profile = "inherit"
            serving = [
              { name = "inherit", max_context = 100, docker = { env = ["TARGET=inherit"] } },
              { name = "single", max_context = 100, docker = { GPU = { Device_Ids = ["1"] } } }
            ]
            [engine.strata.catalog.other]
            """, "[engine.strata.docker.resources]\n" + commonGpu);
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        foreach (var target in new[] { Selection(engine, "qwen"), Selection(engine, "other") })
        {
            var request = provider.BuildCreateRequest(target, engine.Docker!.ImageIdentity!);
            var gpu = Assert.Single(request.HostConfig!.DeviceRequests!);
            Assert.Equal(all ? -1 : 0, gpu.Count);
            if (all)
                Assert.Null(gpu.DeviceIds);
            else
                Assert.Collection(gpu.DeviceIds!, id => Assert.Equal("1", id), id => Assert.Equal("0", id));
        }
        var selected = provider.BuildCreateRequest(Selection(engine, "qwen", "single"), engine.Docker!.ImageIdentity!);
        AssertSingleGpu(selected, "1");
        Assert.Empty(daemon.Requests);
    }

    [Fact]
    public void HyperQwenSingleGpuFreezesInputAndAllocatesOnlyForEffectiveResources()
    {
        string[] ids = ["1"];
        var engine = DeepSeekFromDto(new DockerTargetDto { Gpu = new DockerTargetGpuDto { DeviceIds = ids } });
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        var target = Selection(engine, "qwen");
        ids[0] = "0";
        var first = provider.BuildCreateRequest(target, engine.Docker!.ImageIdentity!);
        var gpu = AssertSingleGpu(first, "1");
        gpu.DeviceIds![0] = "0";
        AssertSingleGpu(provider.BuildCreateRequest(target, engine.Docker.ImageIdentity!), "1");

        var envOnly = DeepSeekFromDto(new DockerTargetDto { Env = ["TARGET=plain"] });
        using var plainProvider = new DockerLifecycleProvider(envOnly.Docker!, envOnly.Name, daemon);
        Assert.Null(plainProvider.BuildCreateRequest(Selection(envOnly, "qwen"), envOnly.Docker!.ImageIdentity!).HostConfig);
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData("""gpu = { all = false, device_ids = ["1"] }""", "all")]
    [InlineData("gpu = []", "gpu")]
    [InlineData("gpu = { device_ids = [] }", "device_ids")]
    [InlineData("""gpu = { device_ids = ["0", "1"] }""", "device_ids")]
    [InlineData("""gpu = { device_ids = ["+1"] }""", "device_ids")]
    [InlineData("""gpu = { device_ids = ["01"] }""", "device_ids")]
    [InlineData("""gpu = { device_ids = [" 1"] }""", "device_ids")]
    [InlineData("""gpu = { device_ids = ["1"], Device_Ids = ["0"] }""", "device_ids")]
    public void HyperQwenSingleGpuRejectsInvalidToml(string fields, string field)
    {
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekTierToml(fields));
        Assert.Contains($".docker.{(field == "gpu" ? field : $"gpu.{field}")} ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""" "gpu":null """, "gpu")]
    [InlineData(""" "gpu":{"device_ids":null} """, "gpu.device_ids")]
    [InlineData(""" "gpu":{"device_ids":[null]} """, "gpu.device_ids")]
    [InlineData(""" "gpu":{"device_ids":[1]} """, "gpu.device_ids")]
    [InlineData(""" "gpu":{"device_ids":["1"],"driver":"custom"} """, "gpu.driver")]
    [InlineData(""" "gpu":{"device_ids":["1"]},"Gpu":{} """, "gpu")]
    [InlineData(""" "gpu":{"device_ids":["1"],"Device_Ids":["0"]} """, "gpu.device_ids")]
    public void HyperQwenSingleGpuRejectsInvalidJson(string fields, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekTierJson(fields));
        Assert.StartsWith($"engine.strata.catalog.qwen.serving[0].docker.{path} ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[engine.strata.catalog.qwen]", """gpu = { device_ids = ["1"] }""", "engine.strata.docker.gpu")]
    [InlineData("[engine.strata.catalog.qwen]\nGPU = { device_ids = ['1'] }", "", "engine.strata.catalog.qwen.gpu")]
    [InlineData("[engine.strata.catalog.qwen.Docker]\ngpu = { device_ids = ['1'] }", "", "engine.strata.catalog.qwen.docker.gpu")]
    [InlineData(SingleGpuTomlCatalog, "", "engine.strata.catalog.qwen.serving[0].gpu")]
    public void HyperQwenSingleGpuRejectsMisplacedToml(string catalog, string common, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadToml(catalog, common));
        Assert.StartsWith(path + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""" "qwen":{} """, ""","Gpu":{"device_ids":["1"]},"gpu":{} """, "engine.strata.docker.gpu")]
    [InlineData(""" "qwen":{"GPU":{"device_ids":["1"]}} """, "", "engine.strata.catalog.qwen.gpu")]
    [InlineData(""" "qwen":{"Docker":{"GPU":{"device_ids":["1"]}},"docker":{}} """, "", "engine.strata.catalog.qwen.docker.gpu")]
    [InlineData(""" "qwen":{"serving":[{"gpu":{"device_ids":["1"]}}],"serving":[]} """, "", "engine.strata.catalog.qwen.serving[0].gpu")]
    public void HyperQwenSingleGpuRejectsMisplacedOrShadowedJson(string catalog, string common, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadJson(catalog, common));
        Assert.StartsWith(path + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, """gpu = { device_ids = ["1"] }""")]
    [InlineData(true, """ "gpu":{"device_ids":["1"]} """)]
    [InlineData(true, """ "GPU":{"device_ids":["1"]},"gpu":{} """)]
    public void HyperQwenSingleGpuRejectsEngineRootGpu(bool isJson, string fields)
    {
        var config = isJson ? $$$$"""
            {"engine":{"strata":{
              "kind":"openai-compatible","endpoint":"http://127.0.0.1:18021",
              {{{{fields}}}}
            }}}
            """ : $$"""
            [engine.strata]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:18021"
            {{fields}}
            """;
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(config, isJson: isJson));
        Assert.StartsWith("engine.strata.gpu ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperQwenSingleGpuCannotBeHiddenByLaterJsonServingOrConflictWithOwner()
    {
        const string tier = """{"name":"tier","max_context":100,"docker":{"gpu":{"device_ids":["1"]}}}""";
        var shadow = Assert.Throws<RouterConfigException>(() => LoadJson($$$"""
            "qwen":{"serving":[{{{tier}}}],"serving":[]}
            """));
        Assert.StartsWith("engine.strata.catalog.qwen.serving ", shadow.Message, StringComparison.Ordinal);
        var owner = Assert.Throws<RouterConfigException>(() => LoadJson($$$"""
            "qwen":{"serving":[{{{tier}}}]},"other":{"select":{"exec":"/bin/selector"}}
            """));
        Assert.Contains("still carries a select", owner.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""gpu = { device_ids = ["01"] }""", "engine.strata.docker.resources.gpu.device_ids[0]")]
    [InlineData("gpu = { all = false, All = true }", "engine.strata.docker.resources.gpu.all")]
    [InlineData("""device_paths = ["/dev/kfd", "/dev/dri/renderD128"]""", "engine.strata.catalog.qwen.serving[0].docker.gpu")]
    public void HyperQwenSingleGpuStillValidatesSupersededCommonResources(string common, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadToml("""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, docker = { gpu = { device_ids = ["1"] } } }]
            [engine.strata.catalog.other]
            """, "[engine.strata.docker.resources]\n" + common));
        Assert.StartsWith(path + " ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperQwenSingleGpuLegacyModelDockerSettingAndCommonGpuStillLoad()
    {
        var engine = LoadJson(""" "qwen":{"docker":{"network_mode":"legacy"}} """,
            ""","resources":{"gpu":{"device_ids":["1"]}} """);
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        AssertSingleGpu(provider.BuildCreateRequest(Selection(engine, "qwen"), engine.Docker!.ImageIdentity!), "1");
        Assert.Empty(daemon.Requests);
    }

    [Fact]
    public void HyperQwenSingleGpuDirectDtoRefusesInvalidShapeAndNull()
    {
        var malformed = new DockerTargetDto { Gpu = new DockerTargetGpuDto { DeviceIds = ["0", "1"] } };
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekFromDto(malformed));
        Assert.StartsWith("engine.strata.catalog.qwen.serving[0].docker.gpu.device_ids ", error.Message, StringComparison.Ordinal);
        Assert.Throws<RouterConfigException>(() => new DockerTargetDto { Gpu = null });

        var target = new DockerTargetDto { Gpu = new DockerTargetGpuDto { DeviceIds = ["1"] } };
        var owner = Assert.Throws<RouterConfigException>(() => DeepSeekFromDto(target, lifecycleOwner: "external"));
        Assert.Contains("requires a native Docker engine", owner.Message, StringComparison.Ordinal);
    }

    private static DockerDeviceRequest AssertSingleGpu(DockerCreateRequest request, string device)
    {
        var gpu = Assert.Single(request.HostConfig!.DeviceRequests!);
        Assert.Empty(gpu.Driver);
        Assert.Equal(0, gpu.Count);
        Assert.Equal(device, Assert.Single(gpu.DeviceIds!));
        Assert.Equal("gpu", Assert.Single(Assert.Single(gpu.Capabilities)));
        return gpu;
    }
}
