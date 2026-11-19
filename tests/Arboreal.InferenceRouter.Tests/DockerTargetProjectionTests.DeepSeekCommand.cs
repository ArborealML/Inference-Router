using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerTargetProjectionTests
{
    private const string DeepSeekFlash = "deepseek-v4.1-flash-1017-iq2-xxs";
    private const string TierCommandPath = "engine.strata.catalog.qwen.serving[0].docker.command";
    private static readonly (string Name, string Context)[] DeepSeekCommandTiers =
    [
        ("max", "1048576"), ("32k", "32768"), ("pilot", "65536"), ("128k", "131072"),
        ("256k", "262144"), ("320k", "327680"), ("512k", "524288"), ("768k", "786432"),
    ];

    private const string DeepSeekCommandMaxPayload = """
        {
          "Image":"sha256:1111111111111111111111111111111111111111111111111111111111111111",
          "Entrypoint":["/opt/maya-python/bin/python"],
          "Cmd":["/selector.py","serve","1048576"],
          "Env":["STRATA_NO_ACQUIRE=1","PYTHONPATH=/source:/source/tools:/policy"],
          "Healthcheck":{
            "Test":["CMD-SHELL","/opt/maya-python/bin/python /selector.py health 1048576"],
            "Interval":30000000000,"StartPeriod":3600000000000,"Timeout":10000000000,"Retries":3
          },
          "Labels":{"arboreal.router":"test-router","arboreal.engine":"strata",
            "arboreal.model":"deepseek-v4.1-flash-1017-iq2-xxs","arboreal.profile":"max"},
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
              {"Type":"bind","Source":"/srv/example/experiment/logs","Target":"/serving-logs","ReadOnly":false}
            ],
            "NanoCpus":20000000000,"Memory":57982058496,"MemorySwap":60129542144
          }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepSeekCommandProjectsAllEightCanonicalPayloadsAndReturnsToMax(bool isJson)
    {
        var engine = LoadGeneralDeepSeek(isJson);
        var model = engine.Catalog[DeepSeekFlash];
        Assert.Equal("max", model.Profile);
        Assert.Equal(DeepSeekCommandTiers.Select(tier => tier.Name), model.ServingProfiles.Select(tier => tier.Name));
        Assert.Collection(model.Aliases, name => Assert.Equal("deepseek-v4.1-flash", name),
            name => Assert.Equal("deepseek", name), name => Assert.Equal("deepseek-flash", name));

        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        foreach (var (name, context) in DeepSeekCommandTiers)
        {
            var tier = model.ServingProfiles.Single(profile => profile.Name == name);
            Assert.Equal(long.Parse(context, System.Globalization.CultureInfo.InvariantCulture), tier.MaxContext);
            Assert.Equal(1, tier.MaxSlots);
            using var expected = JsonDocument.Parse(DeepSeekCommandMaxPayload
                .Replace("1048576", context, StringComparison.Ordinal)
                .Replace("\"max\"", $"\"{name}\"", StringComparison.Ordinal));
            foreach (var spelling in model.Aliases.Prepend(DeepSeekFlash))
            {
                using var actual = JsonDocument.Parse(Project(provider, engine, spelling, name));
                Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), actual.RootElement.GetRawText());
            }
        }

        var max = Project(provider, engine, DeepSeekFlash, "max");
        Assert.Equal(max, Project(provider, engine, "deepseek"));
        var resolver = new VerifiedDispatchTargetResolver(engine, provider);
        var initial = resolver.Resolve("deepseek", "max", null, "fixture-config", "fixture-route");
        var small = resolver.Resolve("deepseek-flash", "32k", initial, "fixture-config", "fixture-route");
        Assert.Equal("32k", small.Tier);
        Assert.Equal(small, resolver.Resolve("deepseek", null, small, "fixture-config", "fixture-route"));
        Assert.Equal(initial, resolver.Resolve("deepseek", "max", small, "fixture-config", "fixture-route"));
        Assert.Equal(max, Project(provider, engine, "deepseek", "max"));
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepSeekCommandReplacesOrInheritsAndKeepsLiteralArguments(bool isJson)
    {
        var engine = isJson ? LoadJson("""
            "qwen":{"default_profile":"literal","serving":[
              {"name":"literal","max_context":100,"aliases":["launch"],"docker":{
                "command":["","  ","{{profile}}","{{model}}","one two"]}},
              {"name":"inherited","max_context":200}
            ]}
            """, ""","command":["serve","{{model}}","{{profile}}"]""")
            : LoadToml("""
                [engine.strata.catalog.qwen]
                default_profile = "literal"
                serving = [
                  { name = "literal", max_context = 100, aliases = ["launch"], docker = { Command = ["", "  ", "{{profile}}", "{{model}}", "one two"] } },
                  { name = "inherited", max_context = 200 },
                ]
                """, """command = ["serve", "{{model}}", "{{profile}}"]""");
        var plans = engine.Docker!.TargetPlans!;
        Assert.Same(plans.Resolve(engine.Name, "qwen", "literal"), plans.Resolve(engine.Name, "qwen", "launch"));
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        using var literal = JsonDocument.Parse(Project(provider, engine, "qwen"));
        using var inherited = JsonDocument.Parse(Project(provider, engine, "qwen", "inherited"));
        Assert.Collection(literal.RootElement.GetProperty("Cmd").EnumerateArray(), item => Assert.Equal("", item.GetString()),
            item => Assert.Equal("  ", item.GetString()), item => Assert.Equal("{{profile}}", item.GetString()),
            item => Assert.Equal("{{model}}", item.GetString()), item => Assert.Equal("one two", item.GetString()));
        Assert.Collection(inherited.RootElement.GetProperty("Cmd").EnumerateArray(), item => Assert.Equal("serve", item.GetString()),
            item => Assert.Equal("qwen", item.GetString()), item => Assert.Equal("inherited", item.GetString()));
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("\"serve\"")]
    [InlineData("[\"bad\\u0000arg\"]")]
    public void DeepSeekCommandRefusesInvalidArraysInBothFormats(string value)
    {
        var json = Assert.Throws<RouterConfigException>(() =>
            DeepSeekTierJson($"\"env\":[\"TARGET=kept\"],\"command\":{value}"));
        Assert.StartsWith(TierCommandPath, json.Message, StringComparison.Ordinal);
        var toml = Assert.Throws<RouterConfigException>(() =>
            DeepSeekTierToml($"env = [\"TARGET=kept\"], command = {value}"));
        Assert.StartsWith(TierCommandPath, toml.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"command\":null")]
    [InlineData("\"command\":[null]")]
    [InlineData("\"Command\":[\"serve\"]")]
    [InlineData("\"command\":[\"first\"],\"command\":[\"second\"]")]
    [InlineData("\"command\":[\"first\"],\"Command\":[\"second\"]")]
    public void DeepSeekCommandJsonRefusesNullsAndShadowedKeys(string fields)
    {
        var error = Assert.Throws<RouterConfigException>(() => DeepSeekTierJson(fields));
        Assert.StartsWith(TierCommandPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSeekCommandTomlRefusesEffectiveCasingTwins()
    {
        var error = Assert.Throws<RouterConfigException>(() =>
            DeepSeekTierToml("""command = ["first"], Command = ["second"]"""));
        Assert.StartsWith(TierCommandPath + " is duplicated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSeekCommandCannotBeHiddenByLaterServing()
    {
        var json = Assert.Throws<RouterConfigException>(() => LoadJson("""
            "qwen":{"serving":[{"name":"tier","max_context":100,"docker":{"command":["probe"]}}],"serving":[]}
            """));
        var toml = Assert.Throws<RouterConfigException>(() => LoadToml("""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, docker = { command = ["probe"] } }]
            Serving = []
            """));
        Assert.StartsWith("engine.strata.catalog.qwen.serving is duplicated", json.Message, StringComparison.Ordinal);
        Assert.StartsWith("engine.strata.catalog.qwen.serving is duplicated", toml.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSeekCommandRefusesMisplacedCommandBeforeOptIn()
    {
        var json = Assert.Throws<RouterConfigException>(() => LoadJson("""
            "qwen":{"serving":[{"name":"tier","max_context":100,"command":["first"]}],"serving":[]}
            """));
        var toml = Assert.Throws<RouterConfigException>(() => LoadToml("""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, Command = ["first"] }]
            Serving = []
            """));
        const string boundary = "engine.strata.catalog.qwen.serving[0].command belongs inside serving-tier docker";
        Assert.Equal(boundary, json.Message);
        Assert.Equal(boundary, toml.Message);
    }

    [Fact]
    public void DeepSeekCommandStillValidatesOverriddenCommonInputInBothFormats()
    {
        var json = Assert.Throws<RouterConfigException>(() => LoadJson("""
            "qwen":{"serving":[{"name":"tier","max_context":100,"docker":{"command":["override"]}}]}
            """, ""","command":[null]"""));
        var toml = Assert.Throws<RouterConfigException>(() => LoadToml("""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, docker = { command = ["override"] } }]
            """, """command = [1]"""));
        Assert.StartsWith("engine.strata.docker.command[0] ", json.Message, StringComparison.Ordinal);
        Assert.StartsWith("engine.strata.docker.command[0] ", toml.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSeekCommandFreezesDirectDtoAndCopiesEachProjectedVector()
    {
        var arguments = new[] { "serve", "32768" };
        var engine = DeepSeekFromDto(new DockerTargetDto { Command = arguments });
        arguments[1] = "modified DTO";
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        var target = Selection(engine, "qwen");
        var first = provider.BuildCreateRequest(target, engine.Docker!.ImageIdentity!).Cmd!;
        first[1] = "modified request";
        var second = provider.BuildCreateRequest(target, engine.Docker.ImageIdentity!).Cmd!;
        Assert.NotSame(first, second);
        Assert.Collection(second, item => Assert.Equal("serve", item), item => Assert.Equal("32768", item));
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("nul")]
    public void DeepSeekCommandRefusesInvalidDirectDtoArrays(string kind)
    {
        string[] args = kind switch
        {
            "empty" => [],
            "null" => [null!],
            _ => ["bad\0arg"],
        };
        var error = Assert.Throws<RouterConfigException>(() =>
            DeepSeekFromDto(new DockerTargetDto { Command = args, Env = ["TARGET=kept"] }));
        Assert.StartsWith(TierCommandPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSeekCommandDirectDtoRefusesExplicitNullAndDuplicateAssignments()
    {
        var tier = new DockerTargetDto();
        Assert.Throws<RouterConfigException>(() => tier.Command = null);
        tier.Command = ["serve"];
        Assert.Throws<RouterConfigException>(() => tier.Command = ["replace"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepSeekCommandDirectDtoStillValidatesOverriddenCommonInput(bool embeddedNul)
    {
        var tier = new DockerTargetDto { Command = ["override"] };
        var argument = embeddedNul ? "bad\0arg" : null;
        var error = Assert.Throws<RouterConfigException>(() =>
            DeepSeekFromDto(tier, commonCommand: [argument!]));
        Assert.StartsWith("engine.strata.docker.command[0] ", error.Message, StringComparison.Ordinal);
    }

    private static EngineDefinition LoadGeneralDeepSeek(bool isJson)
    {
        if (isJson)
        {
            var rows = DeepSeekCommandTiers.Select(tier => $$$$"""
                {"name":"{{{{tier.Name}}}}","max_context":{{{{tier.Context}}}},"max_slots":1,"docker":{
                  "command":["/selector.py","serve","{{{{tier.Context}}}}"],
                  "healthcheck":{"command":"/opt/maya-python/bin/python /selector.py health {{{{tier.Context}}}}",
                    "interval_ms":30000,"start_period_ms":3600000,"timeout_ms":10000,"retries":3},
                  "bind_mounts":[
                    {"source":"/srv/example/experiment/models","target":"/work/models","read_only":true,"nested_under":"/work"},
                    {"source":"/srv/example/pack","target":"/work/data/pack","read_only":true,"nested_under":"/work"},
                    {"source":"/srv/example/serve-candidate.py","target":"/policy/serve_candidate.py","read_only":true},
                    {"source":"/srv/example/deepseek_selector.py","target":"/selector.py","read_only":true},
                    {"source":"/srv/example/experiment/logs","target":"/serving-logs","read_only":false}
                  ]
                }}
                """);
            return LoadJson($$$"""
                "{{{DeepSeekFlash}}}":{"aliases":["deepseek-v4.1-flash","deepseek","deepseek-flash"],
                  "default_profile":"max","serving":[{{{string.Join(",", rows)}}}]}
                """,
                """
                ,"entrypoint":"/opt/maya-python/bin/python","ipc_mode":"host",
                "command":["unused-common","{{profile}}"],
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
                """);
        }

        var table = $"engine.strata.catalog.\"{DeepSeekFlash}\"";
        var tiers = DeepSeekCommandTiers.Select(tier => $$$"""
            [[{{{table}}}.serving]]
            name = "{{{tier.Name}}}"
            max_context = {{{tier.Context}}}
            max_slots = 1
            [{{{table}}}.serving.docker]
            command = ["/selector.py", "serve", "{{{tier.Context}}}"]
            bind_mounts = [
              { source = "/srv/example/experiment/models", target = "/work/models", read_only = true, nested_under = "/work" },
              { source = "/srv/example/pack", target = "/work/data/pack", read_only = true, nested_under = "/work" },
              { source = "/srv/example/serve-candidate.py", target = "/policy/serve_candidate.py", read_only = true },
              { source = "/srv/example/deepseek_selector.py", target = "/selector.py", read_only = true },
              { source = "/srv/example/experiment/logs", target = "/serving-logs", read_only = false },
            ]
            [{{{table}}}.serving.docker.healthcheck]
            command = "/opt/maya-python/bin/python /selector.py health {{{tier.Context}}}"
            interval_ms = 30000
            start_period_ms = 3600000
            timeout_ms = 10000
            retries = 3
            """);
        return LoadToml($"""
            [{table}]
            aliases = ["deepseek-v4.1-flash", "deepseek", "deepseek-flash"]
            default_profile = "max"
            {string.Join("\n", tiers)}
            """,
            """
            entrypoint = "/opt/maya-python/bin/python"
            ipc_mode = "host"
            command = ["unused-common", "{{profile}}"]
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
}
