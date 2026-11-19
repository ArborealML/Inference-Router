using System.Globalization;
using System.Net;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerResourceProjectionTests
{
    // Synthetic values shared by the dual-format fixture and the private config-only specimen.
    private const string HyperQwenLocator = "registry.invalid/fixtures/hyperqwen:offline";
    private const string HyperQwenLocalId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string HyperQwenRepoDigest = "registry.invalid/fixtures/hyperqwen@sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string HyperQwenPlatform = "linux/amd64";
    private const string HyperQwenInit = "init = true";
    private const string HyperQwenRestart = """restart = "unless-stopped" """;
    private const string HyperQwenRotation = """log_rotation = { max_size = "20m", max_files = 3 }""";

    private const string Tp2 = "--tensor-parallel-size 2 --disable-custom-all-reduce";
    private const string LimitedTp2 = Tp2 + " --max-num-batched-tokens 2048";
    private const string VisionAndTemplate = """ --limit-mm-per-prompt {"image":{"count":8}} --chat-template /app/managed-chat-template.jinja""";
    private static readonly (string Name, long Context, int Sequences, string Args)[] HyperQwenTiers =
    [
        ("max", 262144, 1, Tp2),
        ("max-2", 262144, 2, LimitedTp2),
        ("128k-2", 131072, 2, LimitedTp2),
        ("32k", 32768, 4, "--data-parallel-size 2"),
    ];

    private static readonly (string Model, string Alias, string Family)[] HyperQwenModels =
    [
        ("swift", "swift-27b", "swift"),
        ("qwen-3.8-27b", "classic", "classic"),
    ];

    private const string HyperQwenSwiftMaxPayload = $$$"""
        {
          "Image":"{{{HyperQwenLocalId}}}",
          "Cmd":["single"],
          "Env":["HOME=/cache","MODEL=/app/models","PREPARE=0","VERIFY=0","HOST=0.0.0.0","PORT=8000",
            "CTX=long","SPEC=mtp","DRAFT_TOKENS=3","MAX_LEN=262144","MAX_SEQS=1","GPU_UTIL=0.90",
            "TOOLS=1","PREFIX_CACHE=1","VISION=1","API_SERVERS=1","HF_HUB_OFFLINE=1","TRANSFORMERS_OFFLINE=1",
            "PYTORCH_CUDA_ALLOC_CONF=expandable_segments:False","CUDA_DEVICE_ORDER=PCI_BUS_ID","NCCL_IB_DISABLE=1",
            "EXTRA_ARGS=--tensor-parallel-size 2 --disable-custom-all-reduce --limit-mm-per-prompt {\"image\":{\"count\":8}} --chat-template /app/managed-chat-template.jinja"],
          "Labels":{"arboreal.router":"fixture-router","arboreal.engine":"hyperqwen","arboreal.model":"swift","arboreal.profile":"max"},
          "StopTimeout":60,
          "ExposedPorts":{"8000/tcp":{}},
          "HostConfig":{
            "PortBindings":{"8000/tcp":[{"HostIp":"127.0.0.1","HostPort":"18020"}]},
            "Init":true,
            "RestartPolicy":{"Name":"unless-stopped","MaximumRetryCount":0},
            "LogConfig":{"Type":"","Config":{"max-size":"20m","max-file":"3"}},
            "DeviceRequests":[{"Driver":"","Count":0,"DeviceIDs":["0","1"],"Capabilities":[["gpu"]]}],
            "Mounts":[
              {"Type":"bind","Source":"/srv/hyperqwen/checkpoints/swift","Target":"/app/models","ReadOnly":true},
              {"Type":"bind","Source":"/srv/hyperqwen/chat-template.jinja","Target":"/app/managed-chat-template.jinja","ReadOnly":true},
              {"Type":"bind","Source":"/srv/hyperqwen/cache/swift/max-context","Target":"/cache","ReadOnly":false},
              {"Type":"bind","Source":"/srv/hyperqwen/credential","Target":"/app/api_key.txt","ReadOnly":true}
            ],
            "ShmSize":2147483648
          }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HyperQwenProjectsEightPairedTargetsFromBothFormats(bool isJson)
    {
        var engine = LoadHyperQwen(isJson);
        var docker = engine.Docker!;
        Assert.Equal(HyperQwenLocator, docker.Image);
        Assert.Equal(new DockerImageIdentity(HyperQwenLocalId, HyperQwenRepoDigest, HyperQwenPlatform), docker.ImageIdentity);
        Assert.Equal(2, engine.Catalog.Count);
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(docker, engine.Name, daemon);
        using var golden = JsonDocument.Parse(HyperQwenSwiftMaxPayload);
        var expected = golden.RootElement;
        var expectedEnv = expected.GetProperty("Env").EnumerateArray().Select(value => value.GetString()).ToArray();
        var expectedHost = expected.GetProperty("HostConfig");
        var expectedMounts = expectedHost.GetProperty("Mounts");
        foreach (var (canonical, alias, family) in HyperQwenModels)
        {
            var model = engine.Catalog[canonical];
            Assert.Equal("max", model.Profile);
            Assert.Equal(alias, Assert.Single(model.Aliases));
            Assert.Equal(HyperQwenTiers.Select(tier => tier.Name), model.ServingProfiles.Select(tier => tier.Name));
            var defaultRequest = provider.BuildCreateRequest(new LifecycleTarget(engine.Name, alias, null, model), docker.ImageIdentity!);

            foreach (var (name, context, sequences, args) in HyperQwenTiers)
            {
                var profile = model.ServingProfiles.Single(p => p.Name == name);
                Assert.Equal(context, profile.MaxContext);
                // Four configured sequences per DP replica are not four global admission slots.
                Assert.Equal(name == "32k" ? 1 : sequences, profile.MaxSlots);
                var target = new LifecycleTarget(engine.Name, canonical, profile, model);
                var request = provider.BuildCreateRequest(target, docker.ImageIdentity!);
                using var projected = JsonSerializer.SerializeToDocument(request, DockerJsonContext.Default.DockerCreateRequest);
                using var aliased = JsonSerializer.SerializeToDocument(
                    provider.BuildCreateRequest(target with { Model = alias }, docker.ImageIdentity!),
                    DockerJsonContext.Default.DockerCreateRequest);
                Assert.Equal(projected.RootElement.GetRawText(), aliased.RootElement.GetRawText());
                if (name == "max")
                {
                    using var omitted = JsonSerializer.SerializeToDocument(defaultRequest, DockerJsonContext.Default.DockerCreateRequest);
                    Assert.Equal(projected.RootElement.GetRawText(), omitted.RootElement.GetRawText());
                    if (canonical == "swift") JsonEqual(HyperQwenSwiftMaxPayload, omitted.RootElement);
                }

                Assert.Equal(HyperQwenLocalId, request.Image);
                Assert.Null(request.Entrypoint);
                Assert.Null(request.Healthcheck);
                Assert.Equal("single", Assert.Single(request.Cmd!));
                Assert.Equal(60, request.StopTimeoutSeconds);
                Assert.Equal(canonical, request.Labels[DockerLifecycleProvider.LabelModel]);
                Assert.Equal(name, request.Labels[DockerLifecycleProvider.LabelProfile]);
                Assert.True(JsonElement.DeepEquals(expected.GetProperty("ExposedPorts"), projected.RootElement.GetProperty("ExposedPorts")));

                expectedEnv[9] = FormattableString.Invariant($"MAX_LEN={context}");
                expectedEnv[10] = FormattableString.Invariant($"MAX_SEQS={sequences}");
                expectedEnv[^1] = $"EXTRA_ARGS={args}{VisionAndTemplate}";
                Assert.Equal(expectedEnv, request.Env!);
                var host = projected.RootElement.GetProperty("HostConfig");
                Assert.Equal(expectedHost.EnumerateObject().Count(), host.EnumerateObject().Count());
                foreach (var part in expectedHost.EnumerateObject())
                    if (!part.NameEquals("Mounts"))
                        Assert.True(JsonElement.DeepEquals(part.Value, host.GetProperty(part.Name)), part.Name);
                var mounts = host.GetProperty("Mounts");
                Assert.Equal(4, mounts.GetArrayLength());
                AssertHyperQwenMount(mounts[0], expectedMounts[0], $"/srv/hyperqwen/checkpoints/{family}");
                Assert.True(JsonElement.DeepEquals(expectedMounts[1], mounts[1]));
                AssertHyperQwenMount(mounts[2], expectedMounts[2], HyperQwenCache(family, name));
                Assert.True(JsonElement.DeepEquals(expectedMounts[3], mounts[3]));
            }
        }
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData(HyperQwenInit, """{"Init":true}""")]
    [InlineData(HyperQwenRestart, """{"RestartPolicy":{"Name":"unless-stopped","MaximumRetryCount":0}}""")]
    [InlineData(HyperQwenRotation, """{"LogConfig":{"Type":"","Config":{"max-size":"20m","max-file":"3"}}}""")]
    public async Task HyperQwenPolicyAloneProjectsAndRefusesEveryLivePath(string fields, string hostPayload)
    {
        var config = LoadToml(resources: null, fields);
        using var request = Project(config);
        Assert.False(request.RootElement.TryGetProperty("ExposedPorts", out _));
        JsonEqual(hostPayload, request.RootElement.GetProperty("HostConfig"));

        var definition = new EngineDefinition
        {
            Name = "fixture",
            Endpoint = new Uri("http://127.0.0.1:18066"),
            LifecycleOwner = EngineLifecycleOwner.Router,
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = config,
        };
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = _ => (HttpStatusCode.OK,
                """[{"Id":"incumbent","Labels":{"arboreal.router":"fixture-router","arboreal.engine":"fixture","arboreal.model":"other"}}]"""),
        };
        using var provider = new DockerLifecycleProvider(config, definition.Name, daemon);
        using var plainProvider = new DockerLifecycleProvider(LoadToml(resources: null), definition.Name, daemon);
        foreach (var injected in new[] { null, plainProvider })
        {
            var error = await Assert.ThrowsAsync<RouterConfigException>(async () =>
            {
                await using var runtime = new EngineRuntime(definition, new RouterConfig(), new EventRing(),
                    handler: daemon, lifecycle: injected);
            });
            Assert.Contains("container policy options require host qualification", error.Message, StringComparison.Ordinal);
        }
        var unrelated = new LifecycleTarget("fixture", "other", null, new EngineModelDefinition { Name = "other" });
        foreach (var result in new[]
        {
            await provider.PreflightAsync(unrelated, CancellationToken.None),
            await provider.StopAsync(unrelated, [new EnginePeer("incumbent", HyperQwenLocalId, "other", true)], CancellationToken.None),
            await provider.LaunchAsync(unrelated, CancellationToken.None),
        })
        {
            Assert.False(result.Succeeded);
            Assert.Equal("docker_launch_options_not_qualified", result.Reason);
        }
        Assert.Empty(daemon.Requests);

        using var coordinator = new EngineLifecycleCoordinator(provider);
        var coordinated = await coordinator.SwitchWithReportAsync(unrelated, CancellationToken.None);
        Assert.True(coordinated.RefusedBeforeMutation);
        Assert.Equal("docker_launch_options_not_qualified", coordinated.Result.Reason);
        Assert.StartsWith("GET /v1.44/containers/json?", Assert.Single(daemon.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public void HyperQwenPolicyWithPortProjectsExposureWithoutResources()
    {
        using var projection = Project(LoadToml(null, $"{HyperQwenInit}\nhost_port = 18020\ncontainer_port = 8000"));
        var root = projection.RootElement;
        JsonEqual("""{"8000/tcp":{}}""", root.GetProperty("ExposedPorts"));
        JsonEqual("""{"PortBindings":{"8000/tcp":[{"HostIp":"127.0.0.1","HostPort":"18020"}]},"Init":true}""",
            root.GetProperty("HostConfig"));
    }

    [Fact]
    public void HyperQwenLegacyPortOnlyProjectionRetainsItsCreateBytes()
    {
        using var projection = Project(LoadToml(null, "host_port = 18020\ncontainer_port = 8000"));
        const string expected = $$$$"""{"Image":"{{{{PinnedId}}}}","Labels":{"arboreal.router":"fixture-router","arboreal.engine":"fixture","arboreal.model":"model","arboreal.profile":""},"StopTimeout":30,"HostConfig":{"PortBindings":{"8000/tcp":[{"HostIp":"127.0.0.1","HostPort":"18020"}]}}}""";
        Assert.Equal(expected, projection.RootElement.GetRawText());
    }

    private static void AssertHyperQwenMount(JsonElement actual, JsonElement expected, string source)
    {
        Assert.Equal(source, actual.GetProperty("Source").GetString());
        Assert.Equal(expected.EnumerateObject().Count(), actual.EnumerateObject().Count());
        foreach (var part in expected.EnumerateObject())
            if (!part.NameEquals("Source"))
                Assert.True(JsonElement.DeepEquals(part.Value, actual.GetProperty(part.Name)), part.Name);
    }

    private static string HyperQwenCache(string family, string tier)
    {
        if (family == "swift" && tier is "max-2" or "128k-2")
            return "/srv/hyperqwen/tuning/cache/tp-128k-c2";
        if (tier is "single-128k-2" or "single-max")
            return $"/srv/hyperqwen/single-cache/{family}/{tier}";
        var selectorProfile = tier == "max" ? "max-context" : tier;
        return $"/srv/hyperqwen/cache/{family}/{selectorProfile}";
    }

    private static EngineDefinition LoadHyperQwen(bool isJson, string? singleGpu = null)
    {
        const string commonEnv = """
            "HOME=/cache","MODEL=/app/models","PREPARE=0","VERIFY=0","HOST=0.0.0.0","PORT=8000"
            """;
        if (isJson)
        {
            var catalog = string.Join(',', HyperQwenModels.Select(model => $$$$"""
                "{{{{model.Model}}}}":{
                  "aliases":["{{{{model.Alias}}}}"],"default_profile":"max",
                  "serving":[{{{{HyperQwenRows(model.Model, model.Family, isJson: true, singleGpu: singleGpu)}}}}]
                }
                """));
            return RouterConfigLoader.Load($$$$"""
                {"engine":{"hyperqwen":{
                  "kind":"openai-compatible","endpoint":"http://127.0.0.1:18020/v1","lifecycle_owner":"router",
                  "docker":{
                    "image":"{{{{HyperQwenLocator}}}}","image_id":"{{{{HyperQwenLocalId}}}}","image_repo_digest":"{{{{HyperQwenRepoDigest}}}}",
                    "image_platform":"{{{{HyperQwenPlatform}}}}","router_id":"fixture-router","command":["single"],
                    "host_ip":"127.0.0.1","host_port":18020,"container_port":8000,"stop_timeout_ms":60000,
                    "init":true,"restart":"unless-stopped","log_rotation":{"max_size":"20m","max_files":3},
                    "env":[{{{{commonEnv}}}}],
                    "resources":{"gpu":{"device_ids":["0","1"]},"shm_size_bytes":2147483648}
                  },
                  "catalog":{ {{{{catalog}}}} }
                }}}
                """, isJson: true).Engines["hyperqwen"];
        }

        var models = string.Join('\n', HyperQwenModels.Select(model => $$"""
            [engine.hyperqwen.catalog."{{model.Model}}"]
            aliases = ["{{model.Alias}}"]
            default_profile = "max"
            {{HyperQwenRows(model.Model, model.Family, isJson: false, singleGpu: singleGpu)}}
            """));
        return RouterConfigLoader.Load($$"""
            [engine.hyperqwen]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:18020/v1"
            lifecycle_owner = "router"
            [engine.hyperqwen.docker]
            image = "{{HyperQwenLocator}}"
            image_id = "{{HyperQwenLocalId}}"
            image_repo_digest = "{{HyperQwenRepoDigest}}"
            image_platform = "{{HyperQwenPlatform}}"
            router_id = "fixture-router"
            command = ["single"]
            host_ip = "127.0.0.1"
            host_port = 18020
            container_port = 8000
            stop_timeout_ms = 60000
            {{HyperQwenInit}}
            {{HyperQwenRestart}}
            {{HyperQwenRotation}}
            env = [{{commonEnv}}]
            [engine.hyperqwen.docker.resources]
            gpu = { device_ids = ["0", "1"] }
            shm_size_bytes = 2147483648
            {{models}}
            """).Engines["hyperqwen"];
    }

    private static string HyperQwenRows(string model, string family, bool isJson, string? singleGpu)
    {
        var tiers = singleGpu is null ? HyperQwenTiers : HyperQwenTiers.Concat(HyperQwenSingleGpuTiers);
        return string.Join(isJson ? ",\n" : "\n", tiers.Select(tier =>
        {
            var context = tier.Context.ToString(CultureInfo.InvariantCulture);
            var sequences = tier.Sequences.ToString(CultureInfo.InvariantCulture);
            var args = JsonEncodedText.Encode(tier.Args + VisionAndTemplate);
            var cache = HyperQwenCache(family, tier.Name);
            var fast = tier.Name == "single-64k";
            var targetGpu = tier.Name.StartsWith("single-", StringComparison.Ordinal) ? singleGpu : null;
            var env = $$"""
                ["CTX={{(fast ? "fast" : "long")}}","SPEC=mtp","DRAFT_TOKENS={{(fast ? "4" : "3")}}",
                 "MAX_LEN={{context}}","MAX_SEQS={{sequences}}","GPU_UTIL=0.90",
                 "TOOLS=1","PREFIX_CACHE=1","VISION=1","API_SERVERS=1","HF_HUB_OFFLINE=1","TRANSFORMERS_OFFLINE=1",
                 "PYTORCH_CUDA_ALLOC_CONF=expandable_segments:False","CUDA_DEVICE_ORDER=PCI_BUS_ID","NCCL_IB_DISABLE=1",
                 "EXTRA_ARGS={{args}}"]
                """;
            if (isJson)
            {
                var slots = tier.Name == "32k" ? "" : $"\"max_slots\":{sequences},";
                var gpu = targetGpu is null ? "" : $$""" "gpu":{"device_ids":["{{targetGpu}}"]}, """;
                return $$$"""
                    {"name":"{{{tier.Name}}}","max_context":{{{context}}},{{{slots}}}"docker":{
                      {{{gpu}}}
                      "env":{{{env}}},
                      "bind_mounts":[
                        {"source":"/srv/hyperqwen/checkpoints/{{{family}}}","target":"/app/models","read_only":true},
                        {"source":"/srv/hyperqwen/chat-template.jinja","target":"/app/managed-chat-template.jinja","read_only":true},
                        {"source":"{{{cache}}}","target":"/cache","read_only":false},
                        {"source":"/srv/hyperqwen/credential","target":"/app/api_key.txt","read_only":true}
                      ]
                    }}
                    """;
            }
            var tomlSlots = tier.Name == "32k" ? "" : $"max_slots = {sequences}";
            var tomlGpu = targetGpu is null ? "" : $$"""gpu = { device_ids = ["{{targetGpu}}"] }""";
            var table = $"engine.hyperqwen.catalog.\"{model}\".serving";
            return $$"""
                [[{{table}}]]
                name = "{{tier.Name}}"
                max_context = {{context}}
                {{tomlSlots}}
                [{{table}}.docker]
                {{tomlGpu}}
                env = {{env}}
                bind_mounts = [
                  { source = "/srv/hyperqwen/checkpoints/{{family}}", target = "/app/models", read_only = true },
                  { source = "/srv/hyperqwen/chat-template.jinja", target = "/app/managed-chat-template.jinja", read_only = true },
                  { source = "{{cache}}", target = "/cache", read_only = false },
                  { source = "/srv/hyperqwen/credential", target = "/app/api_key.txt", read_only = true }
                ]
                """;
        }));
    }
}
