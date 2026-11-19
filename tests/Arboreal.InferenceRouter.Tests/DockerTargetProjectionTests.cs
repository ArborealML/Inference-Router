using System.Net;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerTargetProjectionTests
{
    private const string ImageId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string RepoDigest = "example/strata@sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string QwenOptions = "resident-budget-gib=36 conversation-cache-slots=8  rope-scaling=none";
    private const string SwiftOptions = "resident-budget-gib=40 conversation-cache-slots=4  rope-scaling=none";
    private const string QwenTier = """{ name = "256k-4", max_context = 256000, max_slots = 4, docker = { env = ["FAMILY=qwen"] } }""";
    private const string TargetJson = """{"name":"256k-4","max_context":256000,"max_slots":4,"docker":{"env":["FAMILY=qwen"]}}""";
    private const string HealthcheckTargetJson = """{"name":"tier","max_context":100,"docker":{"healthcheck":{"command":"probe"}}}""";
    private const string OptedCatalogJson = "\"qwen\":{\"serving\":[" + TargetJson + "]}";
    private const long WideMemory = 57_982_058_496;
    private const long WideMemoryAndSwap = 62_277_025_792;
    private const string CommonMemoryAndSwap = """
        [engine.strata.docker.resources]
        memory_bytes = 8589934592
        memory_swap_bytes = 10737418240
        """;
    private const string CommonMemoryOnly = """
        [engine.strata.docker.resources]
        memory_bytes = 8589934592
        """;

    private static readonly string[] ConcurrentEnv =
        ["COMMON=qwen:concurrent:strata", "STRATA_SET=conversation-cache-slots=4", "LITERAL={{profile}}", "EMPTY="];
    private static readonly string[] SixtyFourKEnv =
        ["COMMON=qwen:64k-4:strata", "STRATA_SET=conversation-cache-slots=8", "LITERAL={{profile}}", "EMPTY="];
    private static readonly string[] UnchangedPlanEnv = ["COMMON=tier", "TARGET=original"];

    [Fact]
    public void PairedTargetsProjectExactlyInBothOrdersAndFormats()
    {
        var toml = LoadToml($$$"""
            [engine.strata.catalog."qwen.model"]
            aliases = ["qwen"]
            default_profile = "256k-4"

            [[engine.strata.catalog."qwen.model".serving]]
            name = "256k-4"
            max_context = 256000
            max_slots = 4

            [engine.strata.catalog."qwen.model".serving.docker]
            env = ["FAMILY=qwen", "CONTEXT=256000", "PARALLEL=4", "PRESET=fixture-qwen-256k-4", "STRATA_SET={{{QwenOptions}}}"]
            bind_mounts = [
              { source = "/srv/qwen/data", target = "/data", read_only = false },
              { source = "/srv/qwen/model", target = "/models", read_only = true },
            ]

            [engine.strata.catalog."swift.model"]
            aliases = ["swift"]
            default_profile = "max"
            serving = [
              { name = "max", max_context = 262144, max_slots = 1, docker = { env = ["FAMILY=swift", "CONTEXT=256k", "PARALLEL=1", "PRESET=fixture-swift-max", "STRATA_SET={{{SwiftOptions}}}"], bind_mounts = [{ source = "/srv/swift/data", target = "/data", read_only = false }, { source = "/srv/swift/model", target = "/models", read_only = true }] } }
            ]

            [engine.strata.catalog.sibling]
            """, """
            host_port = 18021
            command = ["serve", "{{model}}", "{{profile}}", "{{endpoint}}"]
            env = ["MODEL=IQ3_S", "GPUS=0,1"]
            [engine.strata.docker.resources]
            gpu = { all = true }
            shm_size_bytes = 2147483648
            ulimits = [{ name = "memlock", soft = -1, hard = -1 }]
            bind_mounts = [
              { source = "/srv/shared/profile.py", target = "/profile-check.py", read_only = true },
              { source = "/srv/shared/runner.py", target = "/opt/strata/runner.py", read_only = true },
            ]
            """);
        var json = LoadJson($$$"""
            "qwen.model":{"aliases":["qwen"],"default_profile":"256k-4","serving":[{
              "name":"256k-4","max_context":256000,"max_slots":4,"docker":{
                "env":["FAMILY=qwen","CONTEXT=256000","PARALLEL=4","PRESET=fixture-qwen-256k-4","STRATA_SET={{{QwenOptions}}}"],
                "bind_mounts":[{"source":"/srv/qwen/data","target":"/data","read_only":false},
                               {"source":"/srv/qwen/model","target":"/models","read_only":true}]
              }}]},
            "swift.model":{"aliases":["swift"],"default_profile":"max","serving":[{
              "name":"max","max_context":262144,"max_slots":1,"docker":{
                "env":["FAMILY=swift","CONTEXT=256k","PARALLEL=1","PRESET=fixture-swift-max","STRATA_SET={{{SwiftOptions}}}"],
                "bind_mounts":[{"source":"/srv/swift/data","target":"/data","read_only":false},
                               {"source":"/srv/swift/model","target":"/models","read_only":true}]
              }}]},
            "sibling":{}
            """, """
            ,"host_port":18021,"command":["serve","{{model}}","{{profile}}","{{endpoint}}"],"env":["MODEL=IQ3_S","GPUS=0,1"],
            "resources":{"gpu":{"all":true},"shm_size_bytes":2147483648,"ulimits":[{"name":"memlock","soft":-1,"hard":-1}],
              "bind_mounts":[{"source":"/srv/shared/profile.py","target":"/profile-check.py","read_only":true},
                             {"source":"/srv/shared/runner.py","target":"/opt/strata/runner.py","read_only":true}]}
            """);
        using var daemon = new FakeDockerDaemonHandler();
        using var tomlProvider = new DockerLifecycleProvider(toml.Docker!, "strata", daemon);
        using var jsonProvider = new DockerLifecycleProvider(json.Docker!, "strata", daemon);
        var qwen = Project(tomlProvider, toml, "qwen.model");
        var swift = Project(tomlProvider, toml, "swift.model");
        Assert.Equal(swift, Project(jsonProvider, json, "SWIFT", "max"));
        Assert.Equal(qwen, Project(jsonProvider, json, "QWEN", "256k-4"));
        Assert.Equal(qwen, Project(tomlProvider, toml, "qwen", "256k-4"));
        Assert.Equal(swift, Project(tomlProvider, toml, "swift", "max"));
        AssertPair(qwen, "qwen", "256k-4", "256000", "4", QwenOptions);
        AssertPair(swift, "swift", "max", "256k", "1", SwiftOptions);

        Assert.Null(toml.Docker!.TargetPlans!.Resolve(Selection(toml, "sibling")));
        Assert.Null(json.Docker!.TargetPlans!.Resolve(Selection(json, "sibling")));
        var sibling = Project(tomlProvider, toml, "sibling");
        Assert.Equal(sibling, Project(jsonProvider, json, "sibling"));
        using var siblingRequest = JsonDocument.Parse(sibling);
        AssertStrataPublishedPort(siblingRequest.RootElement);
        Assert.Empty(daemon.Requests);
    }

    private static void AssertStrataPublishedPort(JsonElement request)
    {
        Assert.Equal("""{"8080/tcp":{}}""", request.GetProperty("ExposedPorts").GetRawText());
        Assert.Equal("""{"8080/tcp":[{"HostIp":"127.0.0.1","HostPort":"18021"}]}""",
            request.GetProperty("HostConfig").GetProperty("PortBindings").GetRawText());
    }

    private static void AssertPair(byte[] body, string family, string tier, string context, string slots, string options)
    {
        using var json = JsonDocument.Parse(body);
        var request = json.RootElement;
        var canonical = family + ".model";
        Assert.Equal(ImageId, request.GetProperty("Image").GetString());
        Assert.Equal(canonical, request.GetProperty("Labels").GetProperty(DockerLifecycleProvider.LabelModel).GetString());
        Assert.Equal(tier, request.GetProperty("Labels").GetProperty(DockerLifecycleProvider.LabelProfile).GetString());
        Assert.Equal(new[] { "serve", canonical, tier, "strata" }, Strings(request.GetProperty("Cmd")));
        Assert.Equal(new[] { "MODEL=IQ3_S", "GPUS=0,1", $"FAMILY={family}", $"CONTEXT={context}",
            $"PARALLEL={slots}", $"PRESET=fixture-{family}-{tier}", $"STRATA_SET={options}" }, Strings(request.GetProperty("Env")));
        var host = request.GetProperty("HostConfig");
        Assert.Equal(2147483648L, host.GetProperty("ShmSize").GetInt64());
        Assert.Equal(-1, host.GetProperty("DeviceRequests")[0].GetProperty("Count").GetInt32());
        AssertStrataPublishedPort(request);
        Assert.Equal(-1L, host.GetProperty("Ulimits")[0].GetProperty("Soft").GetInt64());
        var mounts = host.GetProperty("Mounts").EnumerateArray().Select(mount =>
            (mount.GetProperty("Source").GetString(), mount.GetProperty("Target").GetString(),
                mount.GetProperty("ReadOnly").GetBoolean())).ToArray();
        Assert.Equal(new (string?, string?, bool)[]
        {
            ("/srv/shared/profile.py", "/profile-check.py", true),
            ("/srv/shared/runner.py", "/opt/strata/runner.py", true),
            ($"/srv/{family}/data", "/data", false),
            ($"/srv/{family}/model", "/models", true),
        }, mounts);
    }

    [Fact]
    public void EqualSizeProfilesKeepDistinctLiteralSettingsAndUseTheCapturedDefault()
    {
        var engine = LoadToml("""
            [engine.strata.catalog.qwen]
            default_profile = "concurrent"
            serving = [
              { name = "concurrent", max_context = 65536, max_slots = 4, docker = { env = ["STRATA_SET=conversation-cache-slots=4", "LITERAL={{profile}}", "EMPTY="] } },
              { name = "64k-4", max_context = 65536, max_slots = 4, docker = { env = ["STRATA_SET=conversation-cache-slots=8", "LITERAL={{profile}}", "EMPTY="] } },
            ]
            """, """env = ["COMMON={{model}}:{{profile}}:{{endpoint}}"]""");
        using var provider = new DockerLifecycleProvider(engine.Docker!, "strata");
        var implicitDefault = Project(provider, engine, "qwen");
        Assert.Equal(implicitDefault, Project(provider, engine, "qwen", "concurrent"));
        using var concurrent = JsonDocument.Parse(implicitDefault);
        using var widerCache = JsonDocument.Parse(Project(provider, engine, "qwen", "64k-4"));
        Assert.Equal(ConcurrentEnv, Strings(concurrent.RootElement.GetProperty("Env")));
        Assert.Equal(SixtyFourKEnv, Strings(widerCache.RootElement.GetProperty("Env")));
        Assert.False(concurrent.RootElement.TryGetProperty("HostConfig", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QwenMemoryPairFollowsTheSelectedTierOnRepeatedProjection(bool json)
    {
        var engine = json
            ? LoadJson("""
                "qwen":{"aliases":["flash-next"],"default_profile":"512k","serving":[
                  {"name":"512k","max_context":524288,"docker":{"env":["CONTEXT=524288"]}},
                  {"name":"768k","max_context":786432,"docker":{"env":["CONTEXT=786432"],
                    "memory_bytes":57982058496,"memory_swap_bytes":62277025792}}
                ]}
                """, """
                ,"resources":{"gpu":{"all":true},"shm_size_bytes":2147483648,
                  "bind_mounts":[{"source":"/srv/example/weights","target":"/models","read_only":true}]}
                """)
            : LoadToml("""
                [engine.strata.catalog.qwen]
                aliases = ["flash-next"]
                default_profile = "512k"
                serving = [
                  { name = "512k", max_context = 524288, docker = { env = ["CONTEXT=524288"] } },
                  { name = "768k", max_context = 786432, docker = { env = ["CONTEXT=786432"], memory_bytes = 57982058496, memory_swap_bytes = 62277025792 } },
                ]
                """, """
                [engine.strata.docker.resources]
                gpu = { all = true }
                shm_size_bytes = 2147483648
                bind_mounts = [{ source = "/srv/example/weights", target = "/models", read_only = true }]
                """);
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, "strata", daemon);
        var uncapped = Project(provider, engine, "flash-next");
        var capped = Project(provider, engine, "qwen", "768k");
        Assert.Equal(uncapped, Project(provider, engine, "qwen", "512k"));
        Assert.Equal(capped, Project(provider, engine, "flash-next", "768k"));

        using var uncappedJson = JsonDocument.Parse(uncapped);
        using var cappedJson = JsonDocument.Parse(capped);
        var uncappedRequest = uncappedJson.RootElement;
        var cappedRequest = cappedJson.RootElement;
        var uncappedHost = uncappedRequest.GetProperty("HostConfig");
        var cappedHost = cappedRequest.GetProperty("HostConfig");
        Assert.False(uncappedHost.TryGetProperty("Memory", out _));
        Assert.False(uncappedHost.TryGetProperty("MemorySwap", out _));
        Assert.Equal(WideMemory, cappedHost.GetProperty("Memory").GetInt64());
        Assert.Equal(WideMemoryAndSwap, cappedHost.GetProperty("MemorySwap").GetInt64());
        Assert.Equal(2_147_483_648L, uncappedHost.GetProperty("ShmSize").GetInt64());
        Assert.Equal(-1, uncappedHost.GetProperty("DeviceRequests")[0].GetProperty("Count").GetInt32());
        Assert.True(uncappedHost.GetProperty("Mounts")[0].GetProperty("ReadOnly").GetBoolean());
        foreach (var property in uncappedHost.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, cappedHost.GetProperty(property.Name)), property.Name);
        Assert.Equal("512k", uncappedRequest.GetProperty("Labels").GetProperty(DockerLifecycleProvider.LabelProfile).GetString());
        Assert.Equal("768k", cappedRequest.GetProperty("Labels").GetProperty(DockerLifecycleProvider.LabelProfile).GetString());
        Assert.Equal("CONTEXT=524288", Assert.Single(Strings(uncappedRequest.GetProperty("Env"))));
        Assert.Equal("CONTEXT=786432", Assert.Single(Strings(cappedRequest.GetProperty("Env"))));
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData(CommonMemoryAndSwap, """env = ["TIER=inherited"]""", 8_589_934_592L, 10_737_418_240L)]
    [InlineData(CommonMemoryAndSwap, "memory_bytes = 57982058496, memory_swap_bytes = 62277025792", WideMemory, WideMemoryAndSwap)]
    [InlineData(CommonMemoryOnly, "memory_bytes = 6291456", 6_291_456L, null)]
    [InlineData("", "memory_bytes = 6291456", 6_291_456L, null)]
    [InlineData("", "memory_bytes = 6291456, memory_swap_bytes = 6291456", 6_291_456L, 6_291_456L)]
    public void TargetMemoryInheritsOrReplacesTheWholePair(string common, string tier, long memory, long? memorySwap)
    {
        var engine = LoadToml($$$"""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, docker = { {{{tier}}} } }]
            """, common);
        using var provider = new DockerLifecycleProvider(engine.Docker!, "strata");
        using var projected = JsonDocument.Parse(Project(provider, engine, "qwen"));
        var host = projected.RootElement.GetProperty("HostConfig");
        Assert.Equal(memory, host.GetProperty("Memory").GetInt64());
        if (memorySwap is { } total)
            Assert.Equal(total, host.GetProperty("MemorySwap").GetInt64());
        else
            Assert.False(host.TryGetProperty("MemorySwap", out _));
    }

    [Fact]
    public void LegacyEngineMemoryLimitStillLeavesTotalMemoryAndSwapAbsent()
    {
        var engine = LoadToml("[engine.strata.catalog.qwen]", CommonMemoryOnly);
        using var provider = new DockerLifecycleProvider(engine.Docker!, "strata");
        using var projected = JsonDocument.Parse(Project(provider, engine, "qwen"));
        var host = projected.RootElement.GetProperty("HostConfig");
        Assert.Equal(8_589_934_592L, host.GetProperty("Memory").GetInt64());
        Assert.False(host.TryGetProperty("MemorySwap", out _));
    }

    [Fact]
    public void TargetPlanFreezesTheDtoAndEachProjectionGetsItsOwnArrays()
    {
        var targetEnv = new[] { "TARGET=original" };
        var commonEnv = new[] { "COMMON={{profile}}" };
        var binds = new[] { new DockerBindDto { Source = "/srv/model", Target = "/models", ReadOnly = true } };
        var dto = new ConfigDto { Engines = new()
        {
            ["strata"] = new EngineDto
            {
                Kind = "openai-compatible", Endpoint = "http://127.0.0.1:18021", LifecycleOwner = "router",
                Docker = new DockerDto { Image = ImageId, ImageId = ImageId, ImageRepoDigest = RepoDigest,
                    ImagePlatform = "linux/amd64", RouterId = "test-router", Env = commonEnv },
                Catalog = new()
                {
                    ["qwen"] = new ModelDto { Serving = [new ServingDto { Name = "tier", MaxContext = 1024,
                        Docker = new DockerTargetDto { Env = targetEnv, BindMounts = binds,
                            MemoryBytes = WideMemory, MemorySwapBytes = WideMemoryAndSwap,
                            Healthcheck = new DockerHealthcheckDto { Command = "original check", IntervalMs = 1 } } }] },
                },
            },
        } };
        var engine = RouterConfigLoader.FromDto(dto, "fixture").Engines["strata"];
        targetEnv[0] = "TARGET=changed";
        commonEnv[0] = "COMMON=changed";
        binds[0].Source = "/srv/changed";
        dto.Engines!["strata"].Catalog!["qwen"].Serving![0] = new ServingDto
            { Name = "tier", MaxContext = 1024, Docker = new DockerTargetDto { MemoryBytes = 6_291_456 } };
        using var provider = new DockerLifecycleProvider(engine.Docker!, "strata");
        var target = Selection(engine, "qwen");
        var first = provider.BuildCreateRequest(target, engine.Docker!.ImageIdentity!);
        first.Env![0] = "COMMON=overwrite";
        var firstProbe = Assert.IsType<DockerCreateHealthcheck>(first.Healthcheck);
        firstProbe.Test[1] = "overwritten check";
        first.HostConfig!.Mounts![0] = new DockerMount("bind", "/srv/other", "/models", false);
        first.HostConfig.Memory = 0;
        first.HostConfig.MemorySwap = 0;
        var next = provider.BuildCreateRequest(target, engine.Docker.ImageIdentity!);
        Assert.Equal(UnchangedPlanEnv, next.Env);
        var nextProbe = Assert.IsType<DockerCreateHealthcheck>(next.Healthcheck);
        Assert.NotSame(firstProbe, nextProbe);
        Assert.NotSame(firstProbe.Test, nextProbe.Test);
        Assert.Collection(nextProbe.Test, mode => Assert.Equal("CMD-SHELL", mode),
            command => Assert.Equal("original check", command));
        Assert.Equal(1_000_000L, nextProbe.Interval);
        Assert.Equal("/srv/model", Assert.Single(next.HostConfig!.Mounts!).Source);
        Assert.Equal(WideMemory, next.HostConfig.Memory);
        Assert.Equal(WideMemoryAndSwap, next.HostConfig.MemorySwap);
    }

    [Theory]
    [InlineData("""env = ["FAMILY=qwen"]""", false, false, false, false)]
    [InlineData("""bind_mounts = [{ source = "/srv/model", target = "/models", read_only = true }]""", true, false, false, false)]
    [InlineData("memory_bytes = 57982058496, memory_swap_bytes = 62277025792", false, true, false, false)]
    [InlineData("""healthcheck = { command = "probe" }""", false, false, false, false)]
    [InlineData("""env = ["FAMILY=qwen"]""", false, false, true, false)]
    [InlineData("""command = ["serve", "32768"]""", false, false, true, false)]
    [InlineData("""gpu = { device_ids = ["1"] }""", false, false, true, false)]
    [InlineData("shm_size_bytes = 38654705664", false, false, true, false)]
    [InlineData("""bind_mounts = [{ source = "/srv/root", target = "/work", read_only = true }, { source = "/srv/model", target = "/work/models", read_only = true, nested_under = "/work" }], healthcheck = { command = "probe", timeout_ms = 10000, retries = 3 }""", false, false, false, true)]
    public async Task TargetDeclarationRefusesEveryLivePathEvenForAnotherModel(
        string fields, bool hasMount, bool hasMemory, bool hasAlias, bool hasNested)
    {
        var aliases = hasAlias ? """aliases = ["max-4"], """ : "";
        var engine = LoadToml($$$"""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 256000, {{{aliases}}}docker = { {{{fields}}} } }]
            [engine.strata.catalog.other]
            """);
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = _ => (HttpStatusCode.OK,
                """[{"Id":"incumbent","Labels":{"arboreal.router":"test-router","arboreal.engine":"strata","arboreal.model":"other"}}]"""),
        };
        using var provider = new DockerLifecycleProvider(engine.Docker!, "strata", daemon);
        using var coordinator = new EngineLifecycleCoordinator(provider);
        var selected = Selection(engine, "qwen", "tier");
        if (hasAlias) selected = selected with { Profile = selected.Profile! with { Name = "max-4" } };
        var projection = provider.BuildCreateRequest(selected, engine.Docker!.ImageIdentity!);
        Assert.Equal("tier", projection.Labels[DockerLifecycleProvider.LabelProfile]);
        Assert.Null(projection.ExposedPorts);
        var hasGpu = fields.StartsWith("gpu", StringComparison.Ordinal);
        var hasShm = fields.StartsWith("shm_size_bytes", StringComparison.Ordinal);
        Assert.Equal(hasMount || hasMemory || hasNested || hasGpu || hasShm, projection.HostConfig is not null);
        if (hasGpu) Assert.Equal("1", Assert.Single(Assert.Single(projection.HostConfig!.DeviceRequests!).DeviceIds!));
        if (hasShm) Assert.Equal(38_654_705_664L, projection.HostConfig!.ShmSize);
        if (hasMount) Assert.Equal("/models", Assert.Single(projection.HostConfig!.Mounts!).Target);
        if (hasNested)
            Assert.Collection(projection.HostConfig!.Mounts!, parent => Assert.Equal("/work", parent.Target),
                child => Assert.Equal("/work/models", child.Target));
        if (hasMemory)
        {
            Assert.Equal(WideMemory, projection.HostConfig!.Memory);
            Assert.Equal(WideMemoryAndSwap, projection.HostConfig.MemorySwap);
        }

        var other = Selection(engine, "other");
        foreach (var result in new[]
        {
            await provider.PreflightAsync(other, CancellationToken.None),
            await provider.StopAsync(other, [new EnginePeer("incumbent", ImageId, "other", true)], CancellationToken.None),
            await provider.LaunchAsync(other, CancellationToken.None),
        })
        {
            Assert.False(result.Succeeded);
            Assert.Equal("docker_target_not_qualified", result.Reason);
        }
        Assert.Empty(daemon.Requests);
        using var plainProvider = new DockerLifecycleProvider(new DockerLifecycleConfig
            { Image = ImageId, RouterId = "test-router" }, "strata", daemon);
        foreach (var injected in new[] { null, plainProvider })
        {
            var error = await Assert.ThrowsAsync<RouterConfigException>(async () =>
            {
                await using var runtime = new EngineRuntime(engine, new RouterConfig(), new EventRing(), lifecycle: injected);
            });
            Assert.Contains("require host qualification", error.Message, StringComparison.Ordinal);
        }
        Assert.Empty(daemon.Requests);

        var coordinated = await coordinator.SwitchWithReportAsync(other, CancellationToken.None);
        Assert.True(coordinated.RefusedBeforeMutation);
        Assert.Equal("docker_target_not_qualified", coordinated.Result.Reason);
        Assert.StartsWith("GET /v1.44/containers/json?", Assert.Single(daemon.Requests), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""env = ["BARE"]""", "", "KEY=value")]
    [InlineData("""env = ["=value"]""", "", "KEY=value")]
    [InlineData("""env = ["BAD.KEY=value"]""", "", "KEY=value")]
    [InlineData("""env = ["K=bad\nvalue"]""", "", "KEY=value")]
    [InlineData("""env = ["K=one", "K=two"]""", "", "repeats")]
    [InlineData("""env = ["K=three"]""", """env = ["J=one", "J=two"]""", "repeats")]
    [InlineData("""env = ["qwen=target"]""", """env = ["{{model}}=common"]""", "repeats")]
    [InlineData("""env = ["K=one"], Env = ["L=two"]""", "", "duplicate")]
    [InlineData("""image = "other" """, "", "image")]
    [InlineData("", "", "must contain")]
    [InlineData("""bind_mounts = [{ source = "/var/run/docker.sock", target = "/socket", read_only = true }]""", "", "source")]
    [InlineData("""bind_mounts = [{ source = "/srv/model", target = "/run/secret", read_only = true }]""", "", "target")]
    [InlineData("""bind_mounts = [{ source = "/var", target = "/socket", read_only = true }]""", "", "parent")]
    [InlineData("""bind_mounts = [{ source = "/srv/../model", target = "/models", read_only = true }]""", "", "source")]
    [InlineData("""bind_mounts = [{ source = "/srv/model", target = "/models" }]""", "", "read_only")]
    [InlineData("memory_bytes = 6291455", "", "memory_bytes")]
    [InlineData("memory_bytes = 6291456, memory_swap_bytes = 6291455", "", "memory_swap_bytes")]
    [InlineData("memory_swap_bytes = 62277025792", "", "memory_swap_bytes")]
    [InlineData("memory_swap_bytes = 62277025792", CommonMemoryAndSwap, "memory_swap_bytes")]
    [InlineData("memory_bytes = 57982058496", CommonMemoryAndSwap, "memory_swap_bytes")]
    [InlineData("memory_bytes = 6291456, Memory_Bytes = 6291457", "", "memory_bytes")]
    public void TomlRejectsUnsafeOrAmbiguousLaunchFields(string tier, string common, string field)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadToml($$$"""
            [engine.strata.catalog.qwen]
            serving = [{ name = "256k-4", max_context = 256000, docker = { {{{tier}}} } }]
            """, common));
        Assert.Contains(field, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/models", "/models")]
    [InlineData("/models/child", "/models")]
    [InlineData("/models", "/models/child")]
    public void CommonAndTargetDestinationsCannotOverlap(string common, string tier)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadToml($$$"""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, docker = { bind_mounts = [{ source = "/srv/target", target = "{{{tier}}}", read_only = true }] } }]
            """, $$$"""
            [engine.strata.docker.resources]
            bind_mounts = [{ source = "/srv/common", target = "{{{common}}}", read_only = true }]
            """));
        Assert.Contains("overlaps", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""aliases = ["SWIFT"]""", QwenTier, "shared")]
    [InlineData("""profile = "64k-4" """ + "\n" + """default_profile = "256k-4" """,
        QwenTier + """, { name = "64k-4", max_context = 65536 }""", "conflicts")]
    [InlineData("""default_profile = "256k-4" """, QwenTier + """, { name = "256k-4", max_context = 1 }""", "duplicates")]
    [InlineData("", QwenTier + """, { name = "64k-4", max_context = 65536 }""", "default_profile")]
    public void AmbiguousModelOrTierSelectionIsRejected(string model, string tiers, string field)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadToml($$$"""
            [engine.strata.catalog.qwen]
            {{{model}}}
            serving = [{{{tiers}}}]
            [engine.strata.catalog.swift]
            """));
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null", "docker")]
    [InlineData("[]", "docker")]
    [InlineData("""{"env":null}""", "env")]
    [InlineData("""{"env":[null]}""", "env")]
    [InlineData("""{"env":"FAMILY=qwen"}""", "env")]
    [InlineData("""{"env":["A=1"],"env":["B=2"]}""", "env")]
    [InlineData("""{"memory_bytes":null,"memory_swap_bytes":62277025792}""", "memory_bytes")]
    [InlineData("""{"memory_bytes":57982058496,"memory_swap_bytes":null}""", "memory_swap_bytes")]
    [InlineData("""{"memory_bytes":"57982058496","memory_swap_bytes":62277025792}""", "memory_bytes")]
    [InlineData("""{"memory_bytes":57982058496,"memory_bytes":6291456,"memory_swap_bytes":62277025792}""", "memory_bytes")]
    [InlineData("""{"Memory_Bytes":57982058496,"memory_swap_bytes":62277025792}""", "memory_bytes")]
    [InlineData("""{"image":"other"}""", "image")]
    [InlineData("""{"bind_mounts":[{"source":"/srv/model","target":"/models","target":"/other","read_only":true}]}""", "target")]
    public void JsonRejectsMalformedTierDockerBeforeOrDuringMapping(string docker, string field)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadJson($$$"""
            "qwen":{"serving":[{"name":"tier","max_context":100,"docker":{{{docker}}}}]}
            """));
        Assert.Contains(field, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"engine":{"strata":{"catalog":{"qwen":{"serving":[""" + TargetJson + """]}}}},"engine":{}}""", "engine")]
    [InlineData("""{"engine":{"strata":{"catalog":{"qwen":{"serving":[""" + TargetJson + """]}}},"strata":null}}""", "strata")]
    [InlineData("""{"engine":{"strata":{"catalog":{"qwen":{"serving":[""" + TargetJson + """]}},"catalog":{}}}}""", "catalog")]
    [InlineData("""{"engine":{"strata":{"catalog":{"qwen":{"serving":[""" + HealthcheckTargetJson + """]}},"Catalog":{}}}}""", "catalog")]
    [InlineData("""{"engine":{"strata":{"catalog":{"qwen":{"serving":[""" + TargetJson + """]},"qwen":null}}}}""", "qwen")]
    [InlineData("""{"engine":{"strata":{"catalog":{"qwen":{"serving":[""" + TargetJson + """],"serving":[]}}}}}""", "serving")]
    [InlineData("""{"engine":{"strata":{"catalog":{"qwen":{"serving":[{"docker":null,"docker":{}}]}}}}}""", "docker")]
    [InlineData("""{"engine":{"strata":{"catalog":{"qwen":{"serving":[{"Docker":{}}]}}}}}""", "casing")]
    [InlineData("""{"engine":{"strata":{"catalog":{"qwen":{"serving":{"docker":{}},"serving":[]}}}}}""", "array")]
    public void JsonCannotEraseEarlierTargetOptIn(string json, string field)
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(json, isJson: true));
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
        Assert.StartsWith("engine", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""" "lifecycle_owner":"external", """, "lifecycle_owner")]
    [InlineData(""" "Lifecycle_Owner":"external", """, "lifecycle_owner")]
    [InlineData(""" "remote":true,"remote":null, """, "remote")]
    [InlineData(""" "Remote":true, """, "remote")]
    [InlineData(""" "lifecycle":"exec","lifecycle":"docker", """, "lifecycle")]
    [InlineData(""" "Lifecycle":"exec", """, "lifecycle")]
    public void OptedJsonRefusesShadowedLifecycleAuthority(string authority, string field)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadJson(OptedCatalogJson, authority: authority));
        Assert.StartsWith($"engine.strata.{field} ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qwen")]
    [InlineData("other")]
    public void OptedJsonRefusesASelectErasedFromAnyCatalogModel(string model)
    {
        const string shadowed = """ "select":{"exec":"/usr/bin/fixture-selector"},"select":null """;
        var catalog = model == "qwen"
            ? $$$""" "qwen":{ {{{shadowed}}},"serving":[{{{TargetJson}}}]} """
            : OptedCatalogJson + $$""","other":{ {{shadowed}} }""";
        var error = Assert.Throws<RouterConfigException>(() => LoadJson(catalog));
        Assert.StartsWith($"engine.strata.catalog.{model}.select ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(""","Env":["COMMON=ignored"]""", "env")]
    [InlineData(""","env":["COMMON=valid"],"Env":["COMMON=twin"]""", "env")]
    [InlineData(""","Host_Port":18021""", "host_port")]
    public void OptedJsonRefusesNoncanonicalCommonDockerMembers(string common, string field)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadJson(OptedCatalogJson, common));
        Assert.StartsWith($"engine.strata.docker.{field} ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyJsonKeepsExistingAuthorityAndCommonDockerParsing()
    {
        var engine = LoadJson(""" "qwen":{"select":{"exec":"/usr/bin/fixture-selector"},"select":null} """,
            ""","env":["COMMON=retained"],"Env":["COMMON=ignored"]""",
            """ "lifecycle_owner":"external","remote":true,"remote":null,"lifecycle":"exec","lifecycle":"docker", """);
        Assert.Equal(EngineLifecycleOwner.Router, engine.LifecycleOwner);
        Assert.Null(engine.Catalog["qwen"].Select);
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, "strata", daemon);
        var request = provider.BuildCreateRequest(Selection(engine, "qwen"), engine.Docker!.ImageIdentity!);
        Assert.Equal("COMMON=retained", Assert.Single(request.Env!));
        using var payload = JsonSerializer.SerializeToDocument(request, DockerJsonContext.Default.DockerCreateRequest);
        Assert.False(payload.RootElement.TryGetProperty("Healthcheck", out _));
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData("x{{profile}}", "tier")]
    [InlineData("qwen{{profile}}", "tier{{endpoint}}")]
    public void OptedCommonTemplatesKeepPlaceholderSyntaxInResolvedIdentifiers(string model, string profile)
    {
        var engine = LoadJson($$$""" "{{{model}}}":{"serving":[{"name":"{{{profile}}}","max_context":100,"docker":{"env":["TARGET={{profile}}"]}}]} """,
            ""","command":["{{model}}","{{profile}}","{{endpoint}}","{{unknown}}"],"env":["MODEL={{model}}"]""");
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(engine.Docker!, "strata", daemon);
        var request = provider.BuildCreateRequest(Selection(engine, model), engine.Docker!.ImageIdentity!);
        Assert.Equal(model, request.Labels[DockerLifecycleProvider.LabelModel]);
        Assert.Equal(profile, request.Labels[DockerLifecycleProvider.LabelProfile]);
        Assert.Collection(request.Cmd!, item => Assert.Equal(model, item), item => Assert.Equal(profile, item),
            item => Assert.Equal("strata", item), item => Assert.Equal("{{unknown}}", item));
        Assert.Collection(request.Env!, item => Assert.Equal($"MODEL={model}", item),
            item => Assert.Equal("TARGET={{profile}}", item));
        Assert.Empty(daemon.Requests);
    }

    [Fact]
    public void ATargetRequiresAUsableEngineDockerDeclaration()
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load($$$"""
            [engine.strata]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:18021"
            [engine.strata.catalog.qwen]
            serving = [{{{QwenTier}}}]
            """));
        Assert.Contains("requires a native Docker engine", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TomlTargetMembersUseTheExistingPropertyCasingRules()
    {
        var engine = LoadToml("""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, Docker = { Env = ["TARGET=literal"] } }]
            """);
        using var provider = new DockerLifecycleProvider(engine.Docker!, "strata");
        var request = provider.BuildCreateRequest(Selection(engine, "qwen"), engine.Docker!.ImageIdentity!);
        Assert.Equal("TARGET=literal", Assert.Single(request.Env!));
    }

    private static EngineDefinition LoadToml(string catalog, string common = "") =>
        RouterConfigLoader.Load($$$"""
            [engine.strata]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:18021"
            lifecycle_owner = "router"
            [engine.strata.docker]
            image = "{{{ImageId}}}"
            image_id = "{{{ImageId}}}"
            image_repo_digest = "{{{RepoDigest}}}"
            image_platform = "linux/amd64"
            router_id = "test-router"
            {{{common}}}
            {{{catalog}}}
            """).Engines["strata"];

    private static EngineDefinition LoadJson(string catalog, string common = "", string authority = "") =>
        RouterConfigLoader.Load($$$$"""
            {"engine":{"strata":{
              "kind":"openai-compatible","endpoint":"http://127.0.0.1:18021",{{{{authority}}}}"lifecycle_owner":"router",
              "docker":{"image":"{{{{ImageId}}}}","image_id":"{{{{ImageId}}}}","image_repo_digest":"{{{{RepoDigest}}}}",
                        "image_platform":"linux/amd64","router_id":"test-router"{{{{common}}}} },
              "catalog":{ {{{{catalog}}}} }
            }}}
            """, isJson: true).Engines["strata"];

    private static LifecycleTarget Selection(EngineDefinition engine, string requested, string? tier = null)
    {
        var model = engine.Catalog.Values.Single(entry => entry.Name.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
            entry.Aliases.Contains(requested, StringComparer.OrdinalIgnoreCase));
        var profile = tier is null ? null : model.ServingProfiles.Single(p => p.Name == tier);
        return new LifecycleTarget(engine.Name, requested, profile, model);
    }

    private static byte[] Project(DockerLifecycleProvider provider, EngineDefinition engine, string requested, string? profile = null) =>
        JsonSerializer.SerializeToUtf8Bytes(provider.BuildCreateRequest(Selection(engine, requested, profile),
            engine.Docker!.ImageIdentity!), DockerJsonContext.Default.DockerCreateRequest);

    private static string?[] Strings(JsonElement array) => array.EnumerateArray().Select(element => element.GetString()).ToArray();
}
