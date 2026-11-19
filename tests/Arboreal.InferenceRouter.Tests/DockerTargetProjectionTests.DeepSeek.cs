using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerTargetProjectionTests
{
    private const string MayaXs = "deepseek-v4.1-flash-1017-maya-xs-v2";
    private const string DeepSeekTierPath = "engine.strata.catalog.qwen.serving[0].docker";
    private const string MayaXsPayload = """
        {
          "Image":"sha256:1111111111111111111111111111111111111111111111111111111111111111",
          "Entrypoint":["/opt/maya-python/bin/python"],
          "Cmd":["/selector.py","serve","32768"],
          "Env":["STRATA_NO_ACQUIRE=1","PYTHONPATH=/source:/source/tools:/policy","DEFIANT_DEEPSEEK_XS=1","DEFIANT_DEEPSEEK_XS_ASSETS=/srv/example/xs-assets"],
          "Healthcheck":{
            "Test":["CMD-SHELL","/opt/maya-python/bin/python /selector.py health 32768"],
            "Interval":30000000000,"StartPeriod":3600000000000,"Timeout":10000000000,"Retries":3
          },
          "Labels":{"arboreal.router":"test-router","arboreal.engine":"strata",
            "arboreal.model":"deepseek-v4.1-flash-1017-maya-xs-v2","arboreal.profile":"32k"},
          "StopTimeout":120,
          "ExposedPorts":{"8080/tcp":{}},
          "HostConfig":{
            "IpcMode":"host",
            "PortBindings":{"8080/tcp":[{"HostIp":"127.0.0.1","HostPort":"18060"}]},
            "DeviceRequests":[{"Driver":"","Count":-1,"Capabilities":[["gpu"]]}],
            "Mounts":[
              {"Type":"bind","Source":"/srv/example/vision","Target":"/vision","ReadOnly":true},
              {"Type":"bind","Source":"/srv/example/experiment","Target":"/legacy","ReadOnly":true},
              {"Type":"bind","Source":"/srv/example/maya","Target":"/work","ReadOnly":true},
              {"Type":"bind","Source":"/srv/example/maya","Target":"/source","ReadOnly":true},
              {"Type":"bind","Source":"/srv/example/experiment/models","Target":"/work/models","ReadOnly":true},
              {"Type":"bind","Source":"/srv/example/pack","Target":"/work/data/pack","ReadOnly":true},
              {"Type":"bind","Source":"/srv/example/serve-candidate.py","Target":"/policy/serve_candidate.py","ReadOnly":true},
              {"Type":"bind","Source":"/srv/example/deepseek_selector.py","Target":"/selector.py","ReadOnly":true},
              {"Type":"bind","Source":"/srv/example/vision","Target":"/srv/example/vision","ReadOnly":true},
              {"Type":"bind","Source":"/srv/example/xs-assets","Target":"/srv/example/xs-assets","ReadOnly":true},
              {"Type":"bind","Source":"/srv/example/experiment/logs","Target":"/serving-logs","ReadOnly":false}
            ],
            "NanoCpus":20000000000,"Memory":57982058496,"MemorySwap":60129542144
          }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepSeekMayaXs32KProjectsTheExactOfflinePayload(bool isJson)
    {
        var engine = LoadMayaXs(isJson);
        var model = engine.Catalog[MayaXs];
        var tier = Assert.Single(model.ServingProfiles);
        Assert.Equal("32k", tier.Name);
        Assert.Equal(32_768L, tier.MaxContext);
        Assert.Equal(1, tier.MaxSlots);
        Assert.Collection(model.Aliases, alias => Assert.Equal("deepseek-maya-xs", alias),
            alias => Assert.Equal("deepseek-xs", alias));

        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        using var expected = JsonDocument.Parse(MayaXsPayload);
        foreach (var spelling in model.Aliases.Prepend(MayaXs))
        {
            using var omitted = JsonDocument.Parse(Project(provider, engine, spelling));
            using var explicitTier = JsonDocument.Parse(Project(provider, engine, spelling, "32k"));
            Assert.True(JsonElement.DeepEquals(expected.RootElement, omitted.RootElement), omitted.RootElement.GetRawText());
            Assert.True(JsonElement.DeepEquals(expected.RootElement, explicitTier.RootElement), explicitTier.RootElement.GetRawText());
        }
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepSeekNestedBindsPreserveTheirEffectiveOrder(bool commonParent)
    {
        var root = Bind("/work");
        var children = new[] { Bind("/work/models", "/work"), Bind("/work/data/pack", "/work"), Bind("/workbench") };
        var engine = DeepSeekFromDto(new DockerTargetDto { BindMounts = commonParent ? children : [root, .. children] },
            commonParent ? new DockerResourcesDto { BindMounts = [root] } : null);
        using var body = ProjectDeepSeekTest(engine);
        var mounts = body.RootElement.GetProperty("HostConfig").GetProperty("Mounts");
        Assert.Collection(mounts.EnumerateArray(), mount => AssertReadOnlyMount(mount, "/work"),
            mount => AssertReadOnlyMount(mount, "/work/models"), mount => AssertReadOnlyMount(mount, "/work/data/pack"),
            mount => AssertReadOnlyMount(mount, "/workbench"));
    }

    private static void AssertReadOnlyMount(JsonElement mount, string target)
    {
        Assert.Equal(target, mount.GetProperty("Target").GetString());
        Assert.True(mount.GetProperty("ReadOnly").GetBoolean());
        Assert.False(mount.TryGetProperty("NestedUnder", out _));
        Assert.False(mount.TryGetProperty("nested_under", out _));
    }

    [Theory]
    [InlineData("reverse", "earlier")]
    [InlineData("missing", "earlier")]
    [InlineData("wrong", "overlaps")]
    [InlineData("same", "overlaps")]
    [InlineData("unmarked", "overlaps")]
    [InlineData("writable-child", "overlaps")]
    [InlineData("writable-parent", "overlaps")]
    [InlineData("chain", "overlaps")]
    [InlineData("overlapping-children", "overlaps")]
    [InlineData("component-sibling", "earlier")]
    [InlineData("protected-source", "outside control")]
    [InlineData("protected-target", "outside control")]
    [InlineData("control-ancestor-source", "parent of a control")]
    [InlineData("control-ancestor-target", "parent of a control")]
    public void DeepSeekNestedBindsRejectUnqualifiedPairs(string shape, string expected)
    {
        var parent = Bind("/work");
        var child = Bind("/work/models", "/work");
        DockerBindDto[] mounts = shape switch
        {
            "reverse" => [child, parent],
            "missing" => [child],
            "wrong" => [parent, Bind("/work/models", "/other")],
            "same" => [parent, Bind("/work", "/work")],
            "unmarked" => [parent, Bind("/work/models")],
            "writable-child" => [parent, Bind("/work/models", "/work", readOnly: false)],
            "writable-parent" => [Bind("/work", readOnly: false), child],
            "chain" => [parent, child, Bind("/work/models/pack", "/work/models")],
            "overlapping-children" => [parent, child, Bind("/work/models/pack", "/work")],
            "component-sibling" => [parent, Bind("/workbench", "/work")],
            "protected-source" => [parent, Bind("/work/models", "/work", source: "/run/host")],
            "protected-target" => [Bind("/run/host")],
            "control-ancestor-source" => [parent, Bind("/work/models", "/work", source: "/var/lib")],
            "control-ancestor-target" => [Bind("/var/lib")],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var error = Assert.Throws<RouterConfigException>(() =>
            DeepSeekFromDto(new DockerTargetDto { BindMounts = mounts }));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSeekNestedBindsRejectCommonAnnotationsEvenWithAParent()
    {
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekFromDto(
            new DockerTargetDto { Env = ["TARGET=test"] },
            new DockerResourcesDto { BindMounts = [Bind("/work"), Bind("/work/models", "/work")] }));
        Assert.StartsWith("engine.strata.docker.resources.bind_mounts[1].nested_under ", error.Message, StringComparison.Ordinal);
    }

    private static DockerBindDto Bind(string target, string? parent = null, bool readOnly = true,
        string source = "/srv/example/content")
    {
        var bind = new DockerBindDto { Source = source, Target = target, ReadOnly = readOnly };
        if (parent is not null) bind.NestedUnder = parent;
        return bind;
    }

    [Theory]
    [InlineData("timeout_ms", "0")]
    [InlineData("timeout_ms", "-1")]
    [InlineData("timeout_ms", "9223372036855")]
    [InlineData("timeout_ms", "1.5")]
    [InlineData("timeout_ms", "\"10\"")]
    [InlineData("retries", "0")]
    [InlineData("retries", "-1")]
    [InlineData("retries", "2147483648")]
    [InlineData("retries", "1.5")]
    [InlineData("retries", "\"3\"")]
    public void DeepSeekHealthRejectsInvalidValuesInBothFormats(string field, string value)
    {
        var json = Assert.Throws<RouterConfigException>(() =>
            DeepSeekTierJson($"\"healthcheck\":{{\"command\":\"probe\",\"{field}\":{value}}}"));
        var toml = Assert.Throws<RouterConfigException>(() =>
            DeepSeekTierToml($"healthcheck = {{ command = \"probe\", {field} = {value} }}"));
        Assert.StartsWith($"{DeepSeekTierPath}.healthcheck.{field} ", json.Message, StringComparison.Ordinal);
        Assert.StartsWith($"{DeepSeekTierPath}.healthcheck.{field} ", toml.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1, 1_000_000L)]
    [InlineData(9_223_372_036_854L, int.MaxValue, 9_223_372_036_854_000_000L)]
    public void DeepSeekHealthProjectsExactDurationAndRetryBounds(long timeout, int retries, long timeoutNs)
    {
        var values = System.FormattableString.Invariant($"timeout_ms = {timeout}, retries = {retries}");
        var engine = DeepSeekTierToml($"healthcheck = {{ command = \"probe\", {values} }}");
        using var body = ProjectDeepSeekTest(engine);
        var probe = body.RootElement.GetProperty("Healthcheck");
        Assert.Equal(timeoutNs, probe.GetProperty("Timeout").GetInt64());
        Assert.Equal(retries, probe.GetProperty("Retries").GetInt32());
        Assert.False(probe.TryGetProperty("StartInterval", out _));
        Assert.False(probe.TryGetProperty("Interval", out _));
        Assert.False(probe.TryGetProperty("StartPeriod", out _));
    }

    [Fact]
    public void DeepSeekHealthKeepsUnsetFieldsOmitted()
    {
        var engine = DeepSeekTierToml("""healthcheck = { command = "probe" }""");
        using var body = ProjectDeepSeekTest(engine);
        var only = Assert.Single(body.RootElement.GetProperty("Healthcheck").EnumerateObject());
        Assert.Equal("Test", only.Name);
        Assert.False(body.RootElement.TryGetProperty("HostConfig", out _));
    }

    [Theory]
    [InlineData(0, 3, "timeout_ms")]
    [InlineData(9_223_372_036_855L, 3, "timeout_ms")]
    [InlineData(10000, 0, "retries")]
    [InlineData(10000, -1, "retries")]
    public void DeepSeekHealthValidatesRepresentableDirectDtoValues(long timeout, int retries, string field)
    {
        var probe = new DockerHealthcheckDto { Command = "probe", TimeoutMs = timeout, Retries = retries };
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekFromDto(new DockerTargetDto { Healthcheck = probe }));
        Assert.StartsWith($"{DeepSeekTierPath}.healthcheck.{field} ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSeekNewDtoFieldsRejectExplicitNull()
    {
        var probe = new DockerHealthcheckDto();
        var mount = new DockerBindDto();
        Assert.Contains("timeout_ms", Assert.Throws<RouterConfigException>(() => probe.TimeoutMs = null).Message,
            StringComparison.Ordinal);
        Assert.Contains("retries", Assert.Throws<RouterConfigException>(() => probe.Retries = null).Message,
            StringComparison.Ordinal);
        Assert.Contains("nested_under", Assert.Throws<RouterConfigException>(() => mount.NestedUnder = null).Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"healthcheck\":{\"command\":\"probe\",\"timeout_ms\":null}", ".healthcheck.timeout_ms")]
    [InlineData("\"healthcheck\":{\"command\":\"probe\",\"retries\":null}", ".healthcheck.retries")]
    [InlineData("\"healthcheck\":{\"command\":\"probe\",\"timeout_ms\":10000,\"TIMEOUT_MS\":10}", ".healthcheck.timeout_ms")]
    [InlineData("\"healthcheck\":{\"command\":\"probe\",\"retries\":3,\"retries\":1}", ".healthcheck.retries")]
    [InlineData("\"healthcheck\":{\"command\":\"probe\",\"retries\":3},\"Healthcheck\":{\"command\":\"replacement\"}", ".healthcheck")]
    [InlineData("\"bind_mounts\":[{\"source\":\"/srv/data\",\"target\":\"/work/models\",\"read_only\":true,\"nested_under\":null}]", ".bind_mounts[0].nested_under")]
    [InlineData("\"bind_mounts\":[{\"source\":\"/srv/data\",\"target\":\"/work/models\",\"read_only\":true,\"nested_under\":17}]", ".bind_mounts[0].nested_under")]
    [InlineData("\"bind_mounts\":[{\"source\":\"/srv/data\",\"target\":\"/work/models\",\"read_only\":true,\"nested_under\":\"/work\",\"Nested_Under\":\"/replacement\"}]", ".bind_mounts[0].nested_under")]
    [InlineData("\"bind_mounts\":[{\"source\":\"/srv/data\",\"target\":\"/work/models\",\"Target\":\"/replacement\",\"read_only\":true,\"nested_under\":\"/work\"}]", ".bind_mounts[0].target")]
    [InlineData("\"bind_mounts\":[{\"source\":\"/srv/data\",\"target\":\"/work/models\",\"read_only\":true,\"nested_under\":\"/work\"}],\"Bind_Mounts\":[]", ".bind_mounts")]
    public void DeepSeekJsonRejectsShadowedOrMalformedTargetFields(string fields, string suffix)
    {
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekTierJson(fields));
        Assert.StartsWith(DeepSeekTierPath + suffix + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("healthcheck = { command = \"probe\", timeout_ms = 10000, Timeout_Ms = 10 }", ".healthcheck.timeout_ms")]
    [InlineData("healthcheck = { command = \"probe\", retries = 3, Retries = 1 }", ".healthcheck.retries")]
    [InlineData("healthcheck = { command = \"probe\", retries = 3 }, Healthcheck = { command = \"replacement\" }", ".healthcheck")]
    [InlineData("bind_mounts = [{ source = \"/srv/data\", target = \"/work/models\", read_only = true, nested_under = 17 }]", ".bind_mounts[0].nested_under")]
    [InlineData("bind_mounts = [{ source = \"/srv/data\", target = \"/work/models\", read_only = true, nested_under = \"/work\", Nested_Under = \"/replacement\" }]", ".bind_mounts[0].nested_under")]
    [InlineData("bind_mounts = [{ source = \"/srv/data\", target = \"/work/models\", Target = \"/replacement\", read_only = true, nested_under = \"/work\" }]", ".bind_mounts[0].target")]
    [InlineData("bind_mounts = [{ source = \"/srv/data\", target = \"/work/models\", read_only = true, nested_under = \"/work\" }], Bind_Mounts = []", ".bind_mounts")]
    public void DeepSeekTomlRejectsShadowedOrMalformedTargetFields(string fields, string suffix)
    {
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekTierToml(fields));
        Assert.StartsWith(DeepSeekTierPath + suffix + " ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSeekTargetDeclarationCannotBeHiddenByLaterServing()
    {
        var json = Assert.Throws<RouterConfigException>(() => LoadJson("""
            "qwen":{"serving":[{"name":"tier","max_context":100,"docker":{
              "healthcheck":{"command":"probe","timeout_ms":10000,"retries":3},
              "bind_mounts":[{"source":"/srv/root","target":"/work","read_only":true},
                {"source":"/srv/data","target":"/work/models","read_only":true,"nested_under":"/work"}]
            }}],"serving":[]}
            """));
        var toml = Assert.Throws<RouterConfigException>(() => LoadToml("""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, docker = { healthcheck = { command = "probe", timeout_ms = 10000, retries = 3 }, bind_mounts = [{ source = "/srv/root", target = "/work", read_only = true }, { source = "/srv/data", target = "/work/models", read_only = true, nested_under = "/work" }] } }]
            Serving = []
            """));
        Assert.StartsWith("engine.strata.catalog.qwen.serving is duplicated", json.Message, StringComparison.Ordinal);
        Assert.StartsWith("engine.strata.catalog.qwen.serving is duplicated", toml.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepSeekCommonAnnotationCannotBeHiddenByLaterResources(bool isJson)
    {
        var error = Assert.Throws<RouterConfigException>(() => isJson ? LoadJson("\"qwen\":{}", """
            ,"Resources":{"bind_mounts":[{"source":"/srv/data","target":"/work/models","read_only":true,"Nested_Under":"/work"}]},
             "resources":{"memory_bytes":6291456}
            """) : LoadToml("[engine.strata.catalog.qwen]", """
            resources = { bind_mounts = [{ source = "/srv/data", target = "/work/models", read_only = true, Nested_Under = "/work" }] }
            Resources = { memory_bytes = 6291456 }
            """));
        Assert.StartsWith("engine.strata.docker.resources.bind_mounts[0].nested_under ", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSeekHealthcheckIsRefusedOutsideATierEvenWhenShadowed()
    {
        var json = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load("""
            {"engine":{"strata":{"Docker":{"healthcheck":{"command":"probe","timeout_ms":10000,"retries":3}},
             "docker":{"image":"fixture","router_id":"test-router"}}}}
            """, isJson: true));
        var toml = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load("""
            [engine.strata]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:18021"
            [engine.strata.Docker.Healthcheck]
            command = "probe"
            timeout_ms = 10000
            retries = 3
            [engine.strata.docker]
            image = "fixture"
            router_id = "test-router"
            """));
        Assert.StartsWith("engine.strata.docker.healthcheck ", json.Message, StringComparison.Ordinal);
        Assert.StartsWith("engine.strata.docker.healthcheck ", toml.Message, StringComparison.Ordinal);
    }

    private static EngineDefinition DeepSeekFromDto(DockerTargetDto target, DockerResourcesDto? resources = null,
        string[]? commonCommand = null, string lifecycleOwner = "router") =>
        RouterConfigLoader.FromDto(new ConfigDto
        {
            Engines = new()
            {
                ["strata"] = new()
                {
                    Kind = "openai-compatible", Endpoint = "http://127.0.0.1:18021", LifecycleOwner = lifecycleOwner,
                    Docker = new()
                    {
                        Image = ImageId, ImageId = ImageId, ImageRepoDigest = RepoDigest, ImagePlatform = "linux/amd64",
                        RouterId = "test-router", Resources = resources, Command = commonCommand,
                    },
                    Catalog = new() { ["qwen"] = new() { Serving = [new() { Name = "tier", MaxContext = 100, Docker = target }] } },
                },
            },
        }, "fixture").Engines["strata"];

    private static EngineDefinition DeepSeekTierJson(string fields) => LoadJson($$$$"""
        "qwen":{"serving":[{"name":"tier","max_context":100,"docker":{ {{{{fields}}}} }}]}
        """);

    private static EngineDefinition DeepSeekTierToml(string fields) => LoadToml($$$"""
        [engine.strata.catalog.qwen]
        serving = [{ name = "tier", max_context = 100, docker = { {{{fields}}} } }]
        """);

    private static JsonDocument ProjectDeepSeekTest(EngineDefinition engine)
    {
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        var bytes = Project(provider, engine, "qwen");
        Assert.Empty(daemon.Requests);
        return JsonDocument.Parse(bytes);
    }

    private static EngineDefinition LoadMayaXs(bool isJson) => isJson ? LoadJson(
        """
        "deepseek-v4.1-flash-1017-maya-xs-v2":{
          "aliases":["deepseek-maya-xs","deepseek-xs"],"default_profile":"32k",
          "serving":[{"name":"32k","max_context":32768,"max_slots":1,"docker":{
            "env":["DEFIANT_DEEPSEEK_XS=1","DEFIANT_DEEPSEEK_XS_ASSETS=/srv/example/xs-assets"],
            "healthcheck":{"command":"/opt/maya-python/bin/python /selector.py health 32768",
              "interval_ms":30000,"start_period_ms":3600000,"timeout_ms":10000,"retries":3},
            "bind_mounts":[
              {"source":"/srv/example/experiment/models","target":"/work/models","read_only":true,"nested_under":"/work"},
              {"source":"/srv/example/pack","target":"/work/data/pack","read_only":true,"nested_under":"/work"},
              {"source":"/srv/example/serve-candidate.py","target":"/policy/serve_candidate.py","read_only":true},
              {"source":"/srv/example/deepseek_selector.py","target":"/selector.py","read_only":true},
              {"source":"/srv/example/vision","target":"/srv/example/vision","read_only":true},
              {"source":"/srv/example/xs-assets","target":"/srv/example/xs-assets","read_only":true},
              {"source":"/srv/example/experiment/logs","target":"/serving-logs","read_only":false}
            ]
          }}]
        }
        """,
        """
        ,"entrypoint":"/opt/maya-python/bin/python","ipc_mode":"host",
        "command":["/selector.py","serve","32768"],
        "env":["STRATA_NO_ACQUIRE=1","PYTHONPATH=/source:/source/tools:/policy"],
        "host_port":18060,"container_port":8080,"stop_timeout_ms":120000,
        "resources":{
          "gpu":{"all":true},"nano_cpus":20000000000,"memory_bytes":57982058496,"memory_swap_bytes":60129542144,
          "bind_mounts":[
            {"source":"/srv/example/vision","target":"/vision","read_only":true},
            {"source":"/srv/example/experiment","target":"/legacy","read_only":true},
            {"source":"/srv/example/maya","target":"/work","read_only":true},
            {"source":"/srv/example/maya","target":"/source","read_only":true}
          ]
        }
        """) : LoadToml(
        """
        [engine.strata.catalog."deepseek-v4.1-flash-1017-maya-xs-v2"]
        aliases = ["deepseek-maya-xs", "deepseek-xs"]
        default_profile = "32k"
        [[engine.strata.catalog."deepseek-v4.1-flash-1017-maya-xs-v2".serving]]
        name = "32k"
        max_context = 32768
        max_slots = 1
        [engine.strata.catalog."deepseek-v4.1-flash-1017-maya-xs-v2".serving.docker]
        env = ["DEFIANT_DEEPSEEK_XS=1", "DEFIANT_DEEPSEEK_XS_ASSETS=/srv/example/xs-assets"]
        bind_mounts = [
          { source = "/srv/example/experiment/models", target = "/work/models", read_only = true, nested_under = "/work" },
          { source = "/srv/example/pack", target = "/work/data/pack", read_only = true, nested_under = "/work" },
          { source = "/srv/example/serve-candidate.py", target = "/policy/serve_candidate.py", read_only = true },
          { source = "/srv/example/deepseek_selector.py", target = "/selector.py", read_only = true },
          { source = "/srv/example/vision", target = "/srv/example/vision", read_only = true },
          { source = "/srv/example/xs-assets", target = "/srv/example/xs-assets", read_only = true },
          { source = "/srv/example/experiment/logs", target = "/serving-logs", read_only = false },
        ]
        [engine.strata.catalog."deepseek-v4.1-flash-1017-maya-xs-v2".serving.docker.healthcheck]
        command = "/opt/maya-python/bin/python /selector.py health 32768"
        interval_ms = 30000
        start_period_ms = 3600000
        timeout_ms = 10000
        retries = 3
        """,
        """
        entrypoint = "/opt/maya-python/bin/python"
        ipc_mode = "host"
        command = ["/selector.py", "serve", "32768"]
        env = ["STRATA_NO_ACQUIRE=1", "PYTHONPATH=/source:/source/tools:/policy"]
        host_port = 18060
        container_port = 8080
        stop_timeout_ms = 120000
        [engine.strata.docker.resources]
        gpu = { all = true }
        nano_cpus = 20000000000
        memory_bytes = 57982058496
        memory_swap_bytes = 60129542144
        bind_mounts = [
          { source = "/srv/example/vision", target = "/vision", read_only = true },
          { source = "/srv/example/experiment", target = "/legacy", read_only = true },
          { source = "/srv/example/maya", target = "/work", read_only = true },
          { source = "/srv/example/maya", target = "/source", read_only = true },
        ]
        """);
}
