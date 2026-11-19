using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerTargetProjectionTests
{
    private const string GlmModel = "glm-5.3-flash";
    private const string ProbePath = "engine.strata.catalog.qwen.serving[0].docker.healthcheck";
    private const string LiteralProbe = """  check "{{profile}}" '$tier'  """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HealthcheckProjectsAllThreeGlmServingPayloads(bool isJson)
    {
        var engine = LoadGlm(isJson);
        var model = engine.Catalog[GlmModel];
        Assert.Equal("1m", model.Profile);
        Assert.Collection(model.ServingProfiles,
            tier => AssertTier(tier, "32k", 32_768),
            tier => AssertTier(tier, "256k", 262_144),
            tier => AssertTier(tier, "1m", 1_048_576));

        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        foreach (var tier in model.ServingProfiles)
            AssertGlmPayload(Project(provider, engine, GlmModel, tier.Name), tier.Name);
        AssertGlmPayload(Project(provider, engine, GlmModel, "32k"), "32k");
        AssertGlmPayload(Project(provider, engine, "glm"), "1m");
        Assert.Empty(daemon.Requests);
    }

    private static void AssertTier(ServingProfile tier, string name, long context)
    {
        Assert.Equal(name, tier.Name);
        Assert.Equal(context, tier.MaxContext);
        Assert.Equal(1, tier.MaxSlots);
    }

    private static void AssertGlmPayload(byte[] body, string tier)
    {
        using var actual = JsonDocument.Parse(body);
        using var expected = JsonDocument.Parse($$"""
            {
              "Image": "{{ImageId}}",
              "Entrypoint": ["/opt/maya-python/bin/python"],
              "Cmd": ["/selector.py", "serve", "{{tier}}"],
              "Env": ["PYTHONPATH=/work:/work/tools:/policy", "MAYA_UPDATE_CHECK=0"],
              "Healthcheck": {
                "Test": ["CMD-SHELL", "/opt/maya-python/bin/python /selector.py health {{tier}}"],
                "Interval": 30000000000,
                "StartPeriod": 3600000000000
              },
              "Labels": {
                "arboreal.router": "test-router",
                "arboreal.engine": "glm",
                "arboreal.model": "glm-5.3-flash",
                "arboreal.profile": "{{tier}}"
              },
              "StopTimeout": 120,
              "ExposedPorts": {"8080/tcp": {} },
              "HostConfig": {
                "IpcMode": "host",
                "PortBindings": {"8080/tcp": [{"HostIp": "127.0.0.1", "HostPort": "18060"}]},
                "DeviceRequests": [{"Driver": "", "Count": -1, "Capabilities": [["gpu"]]}],
                "Mounts": [
                  {"Type":"bind","Source":"/srv/example/maya","Target":"/work","ReadOnly":true},
                  {"Type":"bind","Source":"/srv/example/qualification","Target":"/qualification","ReadOnly":true},
                  {"Type":"bind","Source":"/srv/example/credential","Target":"/api-key","ReadOnly":true},
                  {"Type":"bind","Source":"/srv/example/glm_selector.py","Target":"/selector.py","ReadOnly":true},
                  {"Type":"bind","Source":"/srv/example/serve-candidate.py","Target":"/policy/serve_candidate.py","ReadOnly":true},
                  {"Type":"bind","Source":"/srv/example/glm-state","Target":"/serving","ReadOnly":false}
                ],
                "NanoCpus": 20000000000,
                "Memory": 57982058496,
                "MemorySwap": 60129542144
              }
            }
            """);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), actual.RootElement.GetRawText());
    }

    [Fact]
    public void HealthcheckOnlyKeepsTheCommandLiteralAndOmitsOptionalFields()
    {
        var engine = LoadToml("""
            [engine.strata.catalog.qwen]
            default_profile = "checked"
            serving = [
                { name = "checked", max_context = 100, docker = { healthcheck = { command = "  check \"{{profile}}\" '$tier'  " } } },
                { name = "plain", max_context = 200 },
            ]
            [engine.strata.catalog.other]
            """);
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        using var actual = JsonDocument.Parse(Project(provider, engine, "qwen"));
        Assert.False(actual.RootElement.TryGetProperty("HostConfig", out _));
        var healthcheck = actual.RootElement.GetProperty("Healthcheck");
        var test = Assert.Single(healthcheck.EnumerateObject());
        Assert.Equal("Test", test.Name);
        Assert.Collection(test.Value.EnumerateArray(), mode => Assert.Equal("CMD-SHELL", mode.GetString()),
            command => Assert.Equal(LiteralProbe, command.GetString()));
        using var plain = JsonDocument.Parse(Project(provider, engine, "qwen", "plain"));
        using var other = JsonDocument.Parse(Project(provider, engine, "other"));
        Assert.False(plain.RootElement.TryGetProperty("Healthcheck", out _));
        Assert.False(other.RootElement.TryGetProperty("Healthcheck", out _));
        Assert.Empty(daemon.Requests);
    }

    [Fact]
    public void HealthcheckAcceptsTheExactDurationBounds()
    {
        var engine = LoadToml("""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, docker = { healthcheck = { command = "probe", interval_ms = 1, start_period_ms = 9223372036854 } } }]
            """);
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        using var actual = JsonDocument.Parse(Project(provider, engine, "qwen"));
        var healthcheck = actual.RootElement.GetProperty("Healthcheck");
        Assert.Equal(1_000_000L, healthcheck.GetProperty("Interval").GetInt64());
        Assert.Equal(9_223_372_036_854_000_000L, healthcheck.GetProperty("StartPeriod").GetInt64());
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData("""{"healthcheck":null}""", "")]
    [InlineData("""{"healthcheck":[]}""", "")]
    [InlineData("""{"Healthcheck":{"command":"probe"}}""", "")]
    [InlineData("""{"healthcheck":{"command":"probe"},"Healthcheck":null}""", "")]
    [InlineData("""{"healthcheck":{}}""", ".command")]
    [InlineData("""{"healthcheck":{"command":null}}""", ".command")]
    [InlineData("""{"healthcheck":{"command":7}}""", ".command")]
    [InlineData("""{"healthcheck":{"command":" "}}""", ".command")]
    [InlineData("""{"healthcheck":{"command":"probe\n"}}""", ".command")]
    [InlineData("""{"healthcheck":{"command":"probe","Command":"shadow"}}""", ".command")]
    [InlineData("""{"healthcheck":{"command":"probe","start_interval_ms":3}}""", ".start_interval_ms")]
    [InlineData("""{"healthcheck":{"command":"probe","interval_ms":null}}""", ".interval_ms")]
    [InlineData("""{"healthcheck":{"command":"probe","interval_ms":1.5}}""", ".interval_ms")]
    [InlineData("""{"healthcheck":{"command":"probe","interval_ms":0}}""", ".interval_ms")]
    [InlineData("""{"healthcheck":{"command":"probe","start_period_ms":-1}}""", ".start_period_ms")]
    [InlineData("""{"healthcheck":{"command":"probe","start_period_ms":9223372036855}}""", ".start_period_ms")]
    public void HealthcheckJsonRefusesMalformedProbeAtItsPath(string docker, string member)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadJson($$$$"""
            "qwen":{"serving":[{"name":"tier","max_context":100,"docker":{{{{docker}}}}}]}
            """));
        Assert.StartsWith(ProbePath + member + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("command = \"probe\", Command = \"shadow\"", "command")]
    [InlineData("""command = "probe", interval_ms = 1, Interval_Ms = 2""", "interval_ms")]
    [InlineData("""command = "probe", interval_ms = 0""", "interval_ms")]
    [InlineData("""command = "probe", start_period_ms = 9223372036855""", "start_period_ms")]
    [InlineData("""command = "probe", start_interval_ms = 3""", "start_interval_ms")]
    public void HealthcheckTomlRefusesMalformedProbe(string probe, string member)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadToml($$$"""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, docker = { healthcheck = { {{{probe}}} } } }]
            """));
        Assert.Contains(member, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthcheckTomlRefusesANonintegerDuration()
    {
        Assert.Throws<RouterConfigException>(() => LoadToml("""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, docker = { healthcheck = { command = "probe", interval_ms = 1.5 } } }]
            """));
    }

    [Fact]
    public void HealthcheckDirectDtoRefusesAnOverflowingDuration()
    {
        var dto = new ConfigDto
        {
            Engines = new()
            {
                ["strata"] = new EngineDto
                {
                    Kind = "openai-compatible",
                    Endpoint = "http://127.0.0.1:18021",
                    LifecycleOwner = "router",
                    Docker = new DockerDto { Image = ImageId, RouterId = "test-router" },
                    Catalog = new()
                    {
                        ["qwen"] = new ModelDto
                        {
                            Serving = [new ServingDto
                            {
                                Name = "tier", MaxContext = 100,
                                Docker = new DockerTargetDto
                                {
                                    Healthcheck = new DockerHealthcheckDto { Command = "probe", IntervalMs = 9_223_372_036_855L },
                                },
                            }],
                        },
                    },
                },
            },
        };
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.FromDto(dto, "fixture"));
        Assert.StartsWith(ProbePath + ".interval_ms ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"engine":{"strata":{"docker":{"healthcheck":{"command":"probe"}}}}}""")]
    [InlineData("""{"engine":{"strata":{"docker":{"Healthcheck":{"command":"probe"}},"Docker":{}}}}""")]
    [InlineData("""{"engine":{"strata":{"docker":{"Healthcheck":null,"healthcheck":{"command":"probe"}}}}}""")]
    public void HealthcheckJsonRefusesMisplacedCommonDeclarations(string config)
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(config, isJson: true));
        Assert.StartsWith("engine.strata.docker.healthcheck ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""healthcheck = { command = "probe" }""")]
    [InlineData("""Healthcheck = { command = "probe" }""")]
    [InlineData("""healthcheck = { command = "probe" }""" + "\n" + """Healthcheck = { command = "shadow" }""")]
    public void HealthcheckTomlRefusesMisplacedCommonDeclarations(string fields)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadToml("", fields));
        Assert.Contains("healthcheck", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthcheckTomlRefusesAMisplacedProbeInAShadowedDockerTable()
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load("""
            [engine.strata]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:18021"
            lifecycle_owner = "router"
            [engine.strata.docker]
            image = "fixture"
            router_id = "test-router"
            [engine.strata.docker.Healthcheck]
            command = "early"
            [engine.strata.Docker]
            image = "replacement"
            router_id = "test-router"
            """));
        Assert.Contains("docker", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthcheckTomlPreservesUnrelatedLegacyCommonKeys()
    {
        var engine = LoadToml("[engine.strata.catalog.qwen]", """legacy_extra = { value = "ignored" }""");
        Assert.NotNull(engine.Docker);
        Assert.False(engine.Docker.HasUnqualifiedTargetDocker);
    }

    private static EngineDefinition LoadGlm(bool isJson) => RouterConfigLoader.Load(isJson ? $$$$"""
        {"engine":{"glm":{
          "kind":"openai-compatible","endpoint":"http://127.0.0.1:18060","lifecycle_owner":"router",
          "docker":{
            "image":"{{{{ImageId}}}}","image_id":"{{{{ImageId}}}}","image_repo_digest":"{{{{RepoDigest}}}}",
            "image_platform":"linux/amd64","router_id":"test-router",
            "entrypoint":"/opt/maya-python/bin/python","ipc_mode":"host",
            "command":["/selector.py","serve","{{profile}}"],"env":["PYTHONPATH=/work:/work/tools:/policy","MAYA_UPDATE_CHECK=0"],
            "host_port":18060,"container_port":8080,"stop_timeout_ms":120000,
            "resources":{"gpu":{"all":true},"nano_cpus":20000000000,"memory_bytes":57982058496,"memory_swap_bytes":60129542144,
              "bind_mounts":[
                {"source":"/srv/example/maya","target":"/work","read_only":true},
                {"source":"/srv/example/qualification","target":"/qualification","read_only":true},
                {"source":"/srv/example/credential","target":"/api-key","read_only":true},
                {"source":"/srv/example/glm_selector.py","target":"/selector.py","read_only":true},
                {"source":"/srv/example/serve-candidate.py","target":"/policy/serve_candidate.py","read_only":true},
                {"source":"/srv/example/glm-state","target":"/serving","read_only":false}
              ]}
          },
          "catalog":{"glm-5.3-flash":{"aliases":["glm","glm-flash"],"default_profile":"1m","serving":[
            {"name":"32k","max_context":32768,"max_slots":1,"docker":{"healthcheck":{
              "command":"/opt/maya-python/bin/python /selector.py health 32k","interval_ms":30000,"start_period_ms":3600000}}},
            {"name":"256k","max_context":262144,"max_slots":1,"docker":{"healthcheck":{
              "command":"/opt/maya-python/bin/python /selector.py health 256k","interval_ms":30000,"start_period_ms":3600000}}},
            {"name":"1m","max_context":1048576,"max_slots":1,"docker":{"healthcheck":{
              "command":"/opt/maya-python/bin/python /selector.py health 1m","interval_ms":30000,"start_period_ms":3600000}}}
          ]}}
        }}}
        """ : $$$"""
        [engine.glm]
        kind = "openai-compatible"
        endpoint = "http://127.0.0.1:18060"
        lifecycle_owner = "router"
        [engine.glm.docker]
        image = "{{{ImageId}}}"
        image_id = "{{{ImageId}}}"
        image_repo_digest = "{{{RepoDigest}}}"
        image_platform = "linux/amd64"
        router_id = "test-router"
        entrypoint = "/opt/maya-python/bin/python"
        ipc_mode = "host"
        command = ["/selector.py", "serve", "{{profile}}"]
        env = ["PYTHONPATH=/work:/work/tools:/policy", "MAYA_UPDATE_CHECK=0"]
        host_port = 18060
        container_port = 8080
        stop_timeout_ms = 120000
        [engine.glm.docker.resources]
        gpu = { all = true }
        nano_cpus = 20000000000
        memory_bytes = 57982058496
        memory_swap_bytes = 60129542144
        bind_mounts = [
          { source = "/srv/example/maya", target = "/work", read_only = true },
          { source = "/srv/example/qualification", target = "/qualification", read_only = true },
          { source = "/srv/example/credential", target = "/api-key", read_only = true },
          { source = "/srv/example/glm_selector.py", target = "/selector.py", read_only = true },
          { source = "/srv/example/serve-candidate.py", target = "/policy/serve_candidate.py", read_only = true },
          { source = "/srv/example/glm-state", target = "/serving", read_only = false },
        ]
        [engine.glm.catalog."glm-5.3-flash"]
        aliases = ["glm", "glm-flash"]
        default_profile = "1m"
        serving = [
          { name = "32k", max_context = 32768, max_slots = 1, docker = { healthcheck = { command = "/opt/maya-python/bin/python /selector.py health 32k", interval_ms = 30000, start_period_ms = 3600000 } } },
          { name = "256k", max_context = 262144, max_slots = 1, docker = { healthcheck = { command = "/opt/maya-python/bin/python /selector.py health 256k", interval_ms = 30000, start_period_ms = 3600000 } } },
          { name = "1m", max_context = 1048576, max_slots = 1, docker = { healthcheck = { command = "/opt/maya-python/bin/python /selector.py health 1m", interval_ms = 30000, start_period_ms = 3600000 } } },
        ]
        """, isJson: isJson).Engines["glm"];
}
