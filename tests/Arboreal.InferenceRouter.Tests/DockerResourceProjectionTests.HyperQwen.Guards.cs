using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerResourceProjectionTests
{
    [Theory]
    [InlineData("init = false", """ "init":false """, "init")]
    [InlineData("""init = "true" """, """ "init":"true" """, "init")]
    [InlineData("""restart = "always" """, """ "restart":"always" """, "restart")]
    [InlineData("restart = 1", """ "restart":1 """, "restart")]
    [InlineData("log_rotation = []", """ "log_rotation":[] """, "log_rotation")]
    [InlineData("log_rotation = { max_files = 3 }", """ "log_rotation":{"max_files":3} """, "log_rotation.max_size")]
    [InlineData("""log_rotation = { max_size = "20m" }""", """ "log_rotation":{"max_size":"20m"} """, "log_rotation.max_files")]
    [InlineData("""log_rotation = { max_size = "01m", max_files = 3 }""", """ "log_rotation":{"max_size":"01m","max_files":3} """, "log_rotation.max_size")]
    [InlineData("""log_rotation = { max_size = "20M", max_files = 3 }""", """ "log_rotation":{"max_size":"20M","max_files":3} """, "log_rotation.max_size")]
    [InlineData("""log_rotation = { max_size = "2147483648m", max_files = 3 }""", """ "log_rotation":{"max_size":"2147483648m","max_files":3} """, "log_rotation.max_size")]
    [InlineData("""log_rotation = { max_size = "20m", max_files = 2147483648 }""", """ "log_rotation":{"max_size":"20m","max_files":2147483648} """, "log_rotation.max_files")]
    [InlineData("""log_rotation = { max_size = "20m", max_files = 0 }""", """ "log_rotation":{"max_size":"20m","max_files":0} """, "log_rotation.max_files")]
    [InlineData("""log_rotation = { max_size = "20m", max_files = "3" }""", """ "log_rotation":{"max_size":"20m","max_files":"3"} """, "log_rotation.max_files")]
    [InlineData("""log_rotation = { max_size = "20m", max_files = 3, driver = "json-file" }""",
        """ "log_rotation":{"max_size":"20m","max_files":3,"driver":"json-file"} """, "log_rotation.driver")]
    public void HyperQwenRejectsInvalidPolicyInBothFormats(string toml, string json, string path)
    {
        var tomlError = Assert.Throws<RouterConfigException>(() => LoadToml(null, toml));
        var jsonError = Assert.Throws<RouterConfigException>(() => LoadJson(json));
        Assert.StartsWith($"engine.fixture.docker.{path} ", tomlError.Message, StringComparison.Ordinal);
        Assert.StartsWith($"engine.fixture.docker.{path} ", jsonError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""" "init":null """, "init")]
    [InlineData(""" "restart":null """, "restart")]
    [InlineData(""" "log_rotation":null """, "log_rotation")]
    [InlineData(""" "log_rotation":{"max_size":"20m","max_files":null} """, "log_rotation.max_files")]
    [InlineData(""" "INIT":true """, "init")]
    [InlineData(""" "init":true,"init":false """, "init")]
    [InlineData(""" "restart":"unless-stopped","Restart":"always" """, "restart")]
    [InlineData(""" "log_rotation":{"max_size":"20m","Max_Size":"30m","max_files":3} """, "log_rotation.max_size")]
    public void HyperQwenRejectsNullOrDuplicateJsonPolicy(string fields, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadJson(fields));
        Assert.StartsWith($"engine.fixture.docker.{path} ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperQwenTomlAcceptsSingleMixedCasePolicyAndRefusesTwins()
    {
        using var projection = Project(LoadToml(null, """
            Init = true
            Restart = "unless-stopped"
            Log_Rotation = { Max_Size = "20m", Max_Files = 3 }
            """));
        JsonEqual("""
            {"Init":true,"RestartPolicy":{"Name":"unless-stopped","MaximumRetryCount":0},
             "LogConfig":{"Type":"","Config":{"max-size":"20m","max-file":"3"}}}
            """, projection.RootElement.GetProperty("HostConfig"));
        var topLevel = Assert.Throws<RouterConfigException>(() => LoadToml(null, "Init = true\ninit = false"));
        Assert.StartsWith("engine.fixture.docker.init ", topLevel.Message, StringComparison.Ordinal);
        var nested = Assert.Throws<RouterConfigException>(() => LoadToml(null, """
            log_rotation = { max_size = "20m", Max_Size = "21m", max_files = 3 }
            """));
        Assert.StartsWith("engine.fixture.docker.log_rotation.max_size ", nested.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"Engine":{"fixture":{"docker":{"init":true}}},"engine":{}}""", "engine")]
    [InlineData("""{"engine":{"fixture":{"docker":{"init":true}},"fixture":{}}}""", "engine.fixture")]
    [InlineData("""{"engine":{"fixture":{"Docker":{"init":true},"docker":{}}}}""", "engine.fixture.docker")]
    [InlineData("""{"engine":{"fixture":{"docker":{"init":true},"lifecycle_owner":"external","Lifecycle_Owner":"router"}}}""",
        "engine.fixture.lifecycle_owner")]
    [InlineData("""{"engine":{"fixture":{"docker":{"init":true},"catalog":{"other":{"select":{"exec":"selector"},"select":null}}}}}""",
        "engine.fixture.catalog.other.select")]
    public void HyperQwenJsonRefusesHiddenOptInOrAuthority(string config, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(config, isJson: true));
        Assert.StartsWith(path + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Engine.fixture.docker]\ninit = true\n[engine.fixture.docker]\nimage = 'legacy'", "engine")]
    [InlineData("[engine.Fixture.docker]\ninit = true\n[engine.fixture.docker]\nimage = 'legacy'", "engine.Fixture")]
    [InlineData("[engine.fixture.Docker]\ninit = true\n[engine.fixture.docker]\nimage = 'legacy'", "engine.fixture.docker")]
    [InlineData("[engine.fixture]\nlifecycle_owner = 'router'\nLifecycle_Owner = 'external'\n[engine.fixture.docker]\ninit = true",
        "engine.fixture.lifecycle_owner")]
    [InlineData("[engine.fixture.docker]\ninit = true\n[engine.fixture.catalog.other]\nselect = { exec = 'selector' }\nSelect = {}",
        "engine.fixture.catalog.other.select")]
    public void HyperQwenTomlRefusesHiddenOptInOrAuthority(string config, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(config));
        Assert.StartsWith(path + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""" "resources":{"init":true} """, "resources.init")]
    [InlineData(""" "resources":{"log_rotation":{}} """, "resources.log_rotation")]
    public void HyperQwenCommonResourcesCannotCarryPolicy(string json, string path)
    {
        var toml = Assert.Throws<RouterConfigException>(() => LoadToml("init = true"));
        Assert.StartsWith("engine.fixture.docker.resources.init ", toml.Message, StringComparison.Ordinal);
        var jsonError = Assert.Throws<RouterConfigException>(() => LoadJson(json));
        Assert.StartsWith($"engine.fixture.docker.{path} ", jsonError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"engine":{"fixture":{"catalog":{"other":{"serving":[{"docker":{"init":true},"Docker":{}}]}}}}}""",
        "engine.fixture.catalog.other.serving[0].docker.init")]
    [InlineData("""{"engine":{"fixture":{"catalog":{"other":{"serving":[{"log_rotation":{}}]}}}}}""",
        "engine.fixture.catalog.other.serving[0].log_rotation")]
    public void HyperQwenJsonRefusesMisplacedTierPolicy(string config, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(config, isJson: true));
        Assert.StartsWith(path + " ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperQwenTomlRefusesMisplacedTierPolicy()
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load("""
            [[engine.fixture.catalog.other.serving]]
            name = "tier"
            [engine.fixture.catalog.other.serving.Docker]
            init = true
            [engine.fixture.catalog.other.serving.docker]
            env = ["TARGET=legacy"]
            """));
        Assert.StartsWith("engine.fixture.catalog.other.serving[0].docker.init ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "init = true", "init")]
    [InlineData(true, """ "init":true """, "init")]
    [InlineData(false, """restart = "unless-stopped" """, "restart")]
    [InlineData(true, """ "restart":"unless-stopped" """, "restart")]
    [InlineData(false, """log_rotation = { max_size = "20m", max_files = 3 }""", "log_rotation")]
    [InlineData(true, """ "log_rotation":{"max_size":"20m","max_files":3} """, "log_rotation")]
    public void HyperQwenModelDockerPolicyPlacementRejectsBothFormats(bool isJson, string option, string field)
    {
        var input = isJson ? $$$$$"""
            {"engine":{"fixture":{
              "kind":"openai-compatible","endpoint":"http://127.0.0.1:18020/v1",
              "catalog":{"model":{"docker":{ {{{{{option}}}}} }}}
            }}}
            """ : $$"""
            [engine.fixture]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:18020/v1"
            [engine.fixture.catalog.model.docker]
            {{option}}
            """;
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(input, isJson: isJson));
        Assert.StartsWith($"engine.fixture.catalog.model.docker.{field} ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""" "Docker":{"INIT":true} """)]
    [InlineData(""" "docker":{"init":true},"docker":{} """)]
    public void HyperQwenModelDockerPolicyPlacementChecksJsonCaseAndShadowedOccurrences(string modelFields)
    {
        var json = $$$$$"""
            {"engine":{"fixture":{
              "kind":"openai-compatible","endpoint":"http://127.0.0.1:18020/v1",
              "catalog":{"model":{ {{{{{modelFields}}}}} }}
            }}}
            """;
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(json, isJson: true));
        Assert.StartsWith("engine.fixture.catalog.model.docker.init ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HyperQwenModelDockerPolicyPlacementPreservesLegacyModelSettingsAndEnginePolicy(bool init)
    {
        var tomlInit = init ? "init = true" : "";
        var jsonInit = init ? ""","init":true""" : "";
        var toml = $$"""
            [engine.fixture]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:18020/v1"
            [engine.fixture.docker]
            image = "{{HyperQwenLocator}}"
            router_id = "fixture-router"
            {{tomlInit}}
            [engine.fixture.catalog.model]
            aliases = ["legacy"]
            [engine.fixture.catalog.model.docker]
            network_mode = "legacy"
            """;
        var json = $$$$$"""
            {"engine":{"fixture":{
              "kind":"openai-compatible","endpoint":"http://127.0.0.1:18020/v1",
              "docker":{"image":"{{{{{HyperQwenLocator}}}}}","router_id":"fixture-router"{{{{{jsonInit}}}}} },
              "catalog":{"model":{"aliases":["legacy"],"docker":{"network_mode":"legacy"}}}
            }}}
            """;
        foreach (var config in new[] { RouterConfigLoader.Load(toml), RouterConfigLoader.Load(json, isJson: true) })
        {
            var engine = config.Engines["fixture"];
            var docker = engine.Docker!;
            Assert.Equal("legacy", Assert.Single(engine.Catalog["model"].Aliases));
            Assert.Equal(init, docker.HasUnqualifiedLaunchOptions);
            Assert.Equal(init ? true : (bool?)null, docker.ContainerPolicy?.Init);
        }
    }

    [Theory]
    [InlineData("init", "router", "docker.init")]
    [InlineData("restart", "router", "docker.restart")]
    [InlineData("size", "router", "docker.log_rotation.max_size")]
    [InlineData("files", "router", "docker.log_rotation.max_files")]
    [InlineData("valid", "external", "requires a native Docker engine")]
    public void HyperQwenDirectDtoValidatesPolicyAndOwnership(string setting, string owner, string message)
    {
        var docker = new DockerDto { Image = HyperQwenLocator, RouterId = "fixture-router" };
        switch (setting)
        {
            case "init": docker.Init = false; break;
            case "restart": docker.Restart = "always"; break;
            case "size": docker.LogRotation = new DockerLogRotationDto { MaxSize = "01m", MaxFiles = 3 }; break;
            case "files": docker.LogRotation = new DockerLogRotationDto { MaxSize = "20m", MaxFiles = 0 }; break;
            default: docker.Init = true; break;
        }
        var error = Assert.Throws<RouterConfigException>(() => LoadHyperQwenDto(docker, owner));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HyperQwenProjectionFreezesRotationAndCopiesLogConfig()
    {
        var rotation = new DockerLogRotationDto { MaxSize = "20m", MaxFiles = 3 };
        var docker = new DockerDto
        {
            Image = HyperQwenLocator,
            ImageId = HyperQwenLocalId,
            ImageRepoDigest = HyperQwenRepoDigest,
            ImagePlatform = HyperQwenPlatform,
            RouterId = "fixture-router",
            LogRotation = rotation,
        };
        var config = LoadHyperQwenDto(docker, "router");
        rotation.MaxSize = "30m";
        rotation.MaxFiles = 5;
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(config, "fixture", daemon);
        var first = provider.BuildCreateRequest(Target(), config.ImageIdentity!);
        first.HostConfig!.LogConfig!.Config["max-file"] = "9";

        var second = provider.BuildCreateRequest(Target(), config.ImageIdentity!);
        var projected = second.HostConfig!.LogConfig!;
        Assert.Equal("", projected.Type);
        Assert.Equal("20m", projected.Config["max-size"]);
        Assert.Equal("3", projected.Config["max-file"]);
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData(true, "docker_target_not_qualified")]
    [InlineData(false, "docker_launch_options_not_qualified")]
    public async Task HyperQwenRefusalPreservesTargetAndResourcePrecedence(bool hasTierTargets, string expected)
    {
        var config = hasTierTargets ? LoadHyperQwen(isJson: true).Docker! : LoadToml("nano_cpus = 3000000000", HyperQwenInit);
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(config, hasTierTargets ? "hyperqwen" : "fixture", daemon);
        var result = await provider.PreflightAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Reason);
        Assert.Empty(daemon.Requests);
    }

    private static DockerLifecycleConfig LoadHyperQwenDto(DockerDto docker, string owner) =>
        RouterConfigLoader.FromDto(new ConfigDto
        {
            Engines = new()
            {
                ["fixture"] = new EngineDto
                {
                    Kind = "openai-compatible",
                    Endpoint = "http://127.0.0.1:18020/v1",
                    LifecycleOwner = owner,
                    Docker = docker,
                },
            },
        }, "fixture").Engines["fixture"].Docker!;
}
