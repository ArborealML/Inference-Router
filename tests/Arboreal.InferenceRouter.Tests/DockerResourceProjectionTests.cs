using System.Net;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class DockerResourceProjectionTests
{
    private const string PinnedId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Manifest = "example/hip@sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string HipEnv =
        """["LD_LIBRARY_PATH=/opt/dax-hip/lib/python3.12/site-packages/_rocm_sdk_devel/lib:/opt/dax-hip/lib/python3.12/site-packages/_rocm_sdk_libraries_gfx103X_all/lib"]""";

    // In-container arguments are exact. Use example host bind sources, registry pins and labels.
    private const string HipResources = """
        device_paths = ["/dev/kfd", "/dev/dri/renderD129"]
        bind_mounts = [
            { source = "/srv/example/workspace", target = "/work", read_only = true },
            { source = "/srv/example/models", target = "/models", read_only = true },
        ]
        nano_cpus = 3000000000
        memory_bytes = 17179869184
        memory_swap_bytes = 17179869184
        ulimits = [{ name = "core", soft = 0, hard = 0 }]
        """;

    private const string HipHostConfig = """
        {
          "NetworkMode": "bridge",
          "PortBindings": {"8080/tcp":[{"HostIp":"127.0.0.1","HostPort":"18066"}]},
          "Devices": [
            {"PathOnHost":"/dev/kfd","PathInContainer":"/dev/kfd","CgroupPermissions":"rwm"},
            {"PathOnHost":"/dev/dri/renderD129","PathInContainer":"/dev/dri/renderD129","CgroupPermissions":"rwm"}
          ],
          "Mounts": [
            {"Type":"bind","Source":"/srv/example/workspace","Target":"/work","ReadOnly":true},
            {"Type":"bind","Source":"/srv/example/models","Target":"/models","ReadOnly":true}
          ],
          "NanoCpus": 3000000000,
          "Memory": 17179869184,
          "MemorySwap": 17179869184,
          "Ulimits": [{"Name":"core","Soft":0,"Hard":0}]
        }
        """;

    [Fact]
    public void MeasuredQwenHipProjectsTheExactArchivedResourceRequest()
    {
        // SHA-256 of archived container inspect:
        // 756ae62519c505a4c1624e635311f70ea645d5c5a35e8e4ed437fbcded813af3
        // The 4096 workload exited 0. The container actually launched with context 32768.
        const string command = """
            ["/work/build-hip/bin/llama-server","-m","/models/Qwen3.5-2B-Q4_K_M.gguf",
             "-ngl","99","-fa","on","-c","32768","-np","1","-t","3","-b","512","-ub","512",
             "--cache-ram","0","--host","0.0.0.0","--port","8080"]
            """;
        AssertHipProjection(command, """{"source":"measured_container","observed_exit":0,"workload_tokens":4096}""", 0);
    }

    [Fact]
    public void UnqualifiedBonsaiQ8ProjectsTheScriptRequestWithoutAnObservedExit()
    {
        // SHA-256 of point-in-time Q8 serving script:
        // d278d21bb5faa5e122161b25600e28d4a3809218413902bc5c4fb8a9f3c4f3a9
        // Project the script with a proposed context argument. The 8192 archive has no exit result.
        const string command = """
            ["/work/build-bonsai-hip/bin/llama-server","-m","/models/Ternary-Bonsai-2-27B-PQ2_0.gguf",
             "-ngl","99","-ctk","q8_0","-ctv","q8_0","-fa","on","-c","8192","-np","1","-t","3",
             "--host","0.0.0.0","--port","8080"]
            """;
        AssertHipProjection(command, """{"source":"unqualified_script","observed_exit":null}""", null);
    }

    private static void AssertHipProjection(string command, string provenance, int? observedExit)
    {
        using var evidence = JsonDocument.Parse(provenance);
        var exit = evidence.RootElement.GetProperty("observed_exit");
        if (observedExit is { } code) Assert.Equal(code, exit.GetInt32());
        else Assert.Equal(JsonValueKind.Null, exit.ValueKind);

        var cfg = LoadToml(HipResources, $"""
            network_mode = "bridge"
            host_port = 18066
            command = {command}
            env = {HipEnv}
            """);
        using var actual = Project(cfg);
        JsonEqual(HipHostConfig, actual.RootElement.GetProperty("HostConfig"));
        JsonEqual(command, actual.RootElement.GetProperty("Cmd"));
        JsonEqual(HipEnv, actual.RootElement.GetProperty("Env"));
        Assert.Equal(PinnedId, actual.RootElement.GetProperty("Image").GetString());
    }

    [Theory]
    [InlineData("""{"device_ids":["1","0"]}""", """{"Driver":"","Count":0,"DeviceIDs":["1","0"],"Capabilities":[["gpu"]]}""")]
    [InlineData("""{"device_ids":["0"]}""", """{"Driver":"","Count":0,"DeviceIDs":["0"],"Capabilities":[["gpu"]]}""")]
    [InlineData("""{"all":true}""", """{"Driver":"","Count":-1,"Capabilities":[["gpu"]]}""")]
    public void NvidiaAllAndOrderedIdsAreDistinctDeviceRequests(string gpu, string expected)
    {
        var cfg = LoadJson($$$"""
            "resources": {
                "gpu": {{{gpu}}},
                "bind_mounts": [{"source":"/srv/example/cache","target":"/cache","read_only":false}],
                "shm_size_bytes": 2147483648,
                "ulimits": [{"name":"memlock","soft":-1,"hard":-1}]
            }
            """);
        using var actual = Project(cfg);
        var host = actual.RootElement.GetProperty("HostConfig");
        JsonEqual($$$"""
            {
              "DeviceRequests": [{{{expected}}}],
              "Mounts": [{"Type":"bind","Source":"/srv/example/cache","Target":"/cache","ReadOnly":false}],
              "ShmSize": 2147483648,
              "Ulimits": [{"Name":"memlock","Soft":-1,"Hard":-1}]
            }
            """, host);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("""network_mode = "bridge" """, """{"NetworkMode":"bridge"}""")]
    [InlineData("host_port = 18066", """{"PortBindings":{"8080/tcp":[{"HostIp":"127.0.0.1","HostPort":"18066"}]}}""")]
    public void OmittedResourcesPreserveTheOriginalHostConfig(string dockerFields, string? expected)
    {
        using var actual = Project(LoadToml(resources: null, dockerFields: dockerFields));
        var hasHostConfig = actual.RootElement.TryGetProperty("HostConfig", out var host);
        Assert.Equal(expected is not null, hasHostConfig);
        if (expected is not null) JsonEqual(expected, host);
    }

    [Fact]
    public async Task UnqualifiedResourcePlanRefusesDirectAndCoordinatedMutation()
    {
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = _ => (HttpStatusCode.OK,
                """[{"Id":"incumbent","Labels":{"arboreal.router":"fixture-router","arboreal.engine":"fixture","arboreal.model":"old"}}]"""),
        };
        using var provider = new DockerLifecycleProvider(LoadToml(HipResources), "fixture", daemon);
        using var coordinator = new EngineLifecycleCoordinator(provider, ownsProvider: false);
        var target = Target();

        var preflight = await provider.PreflightAsync(target, CancellationToken.None);
        var launch = await provider.LaunchAsync(target, CancellationToken.None);
        var stop = await provider.StopAsync(target, [new EnginePeer("incumbent", "example/hip:1", "old", OwnedByUs: true)],
            CancellationToken.None);
        foreach (var result in new[] { preflight, launch, stop })
        {
            Assert.False(result.Succeeded);
            Assert.Equal("docker_resources_not_qualified", result.Reason);
            Assert.False(string.IsNullOrWhiteSpace(result.Action));
        }
        Assert.Empty(daemon.Requests);

        var report = await coordinator.SwitchWithReportAsync(target, CancellationToken.None);
        Assert.True(report.RefusedBeforeMutation);
        Assert.Equal("docker_resources_not_qualified", report.Result.Reason);
        Assert.StartsWith("GET /v1.44/containers/json?", Assert.Single(daemon.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResourceFreeInjectedProviderCannotActivateAResourceBearingEngine()
    {
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(LoadToml(resources: null), "fixture", daemon);
        var definition = new EngineDefinition
        {
            Name = "fixture",
            Endpoint = new Uri("http://127.0.0.1:18066"),
            LifecycleOwner = EngineLifecycleOwner.Router,
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = LoadToml(HipResources),
        };

        var failure = await Assert.ThrowsAsync<RouterConfigException>(async () =>
        {
            await using var runtime = new EngineRuntime(definition, new RouterConfig(), new EventRing(), lifecycle: provider);
        });
        Assert.Contains("Docker resources require host qualification", failure.Message, StringComparison.Ordinal);
        Assert.Empty(daemon.Requests);
    }

    [Theory]
    [InlineData("", "resources")]
    [InlineData("cpu = 3", "cpu")]
    [InlineData("gpu = { all = true, typo = false }", "typo")]
    [InlineData("gpu = { all = false }", "gpu")]
    [InlineData("""gpu = { all = true, device_ids = ["0"] }""", "gpu")]
    [InlineData("""gpu = { device_ids = ["01"] }""", "device_ids")]
    [InlineData("""gpu = { device_ids = ["0", "0"] }""", "device_ids")]
    [InlineData("""device_paths = ["/dev/dri/renderD129"]""", "device_paths")]
    [InlineData("""device_paths = ["/dev/kfd", "/dev/kfd"]""", "device_paths")]
    [InlineData("""device_paths = ["/dev/kfd", "/dev/dri/renderD129", "/dev/mem"]""", "device_paths")]
    [InlineData("gpu = { all = true }\ndevice_paths = [\"/dev/kfd\", \"/dev/dri/renderD129\"]", "gpu")]
    [InlineData("nano_cpus = 0", "nano_cpus")]
    [InlineData("memory_bytes = 6291455", "memory_bytes")]
    [InlineData("memory_swap_bytes = 6291456", "memory_swap_bytes")]
    [InlineData("memory_bytes = 7000000\nmemory_swap_bytes = 6291456", "memory_swap_bytes")]
    [InlineData("shm_size_bytes = -1", "shm_size_bytes")]
    [InlineData("bind_mounts = []", "bind_mounts")]
    [InlineData("""bind_mounts = [{ source = "/srv/../models", target = "/models", read_only = true }]""", "source")]
    [InlineData("""bind_mounts = [{ source = "/var/run/docker.sock", target = "/socket", read_only = true }]""", "source")]
    [InlineData("""bind_mounts = [{ source = "/srv/model", target = "/dev/dri", read_only = true }]""", "target")]
    [InlineData("""bind_mounts = [{ source = "/srv/model", target = "/models" }]""", "read_only")]
    [InlineData("""bind_mounts = [{ source = "/srv/m", target = "/models", read_only = true, unsafe = true }]""", "unsafe")]
    [InlineData("""bind_mounts = [{ source = "/srv/a", target = "/models", read_only = true }, { source = "/srv/b", target = "/models/b", read_only = false }]""", "target")]
    [InlineData("""ulimits = [{ name = "nofile", soft = 1, hard = 2 }]""", "name")]
    [InlineData("""ulimits = [{ name = "core", soft = -1, hard = 2 }]""", "soft")]
    [InlineData("""ulimits = [{ name = "core", soft = -2, hard = -1 }]""", "soft")]
    [InlineData("""ulimits = [{ name = "core", soft = 0, hard = 0 }, { name = "core", soft = 0, hard = 0 }]""", "name")]
    public void TomlRejectsMalformedOrContradictoryResources(string resources, string field)
    {
        var failure = Assert.Throws<RouterConfigException>(() => LoadToml(resources));
        Assert.Contains(field, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nano_cpus = 1\nnano_cpus = 2")]
    [InlineData("gpu = { all = true, all = true }")]
    public void TomlRejectsDuplicateResourceKeys(string resources) =>
        Assert.Throws<RouterConfigException>(() => LoadToml(resources));

    [Theory]
    [InlineData("null", "resources")]
    [InlineData("{}", "resources")]
    [InlineData("""{"nano_cpus":1, "gpu":null}""", "gpu")]
    [InlineData("""{"gpu":{"all":true,"all":false}}""", "all")]
    [InlineData("""{"gpu":{"all":true,"typo":true}}""", "typo")]
    [InlineData("""{"device_paths":[null]}""", "device_paths")]
    [InlineData("""{"bind_mounts":[null]}""", "bind_mounts")]
    [InlineData("""{"ulimits":[{"name":"core","soft":null,"hard":0}]}""", "soft")]
    public void JsonRejectsNullDuplicateAndUnknownResourceFields(string value, string field)
    {
        var failure = Assert.Throws<RouterConfigException>(() => LoadJson($"\"resources\":{value}"));
        Assert.Contains(field, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonDuplicateResourceRequestCannotBeShadowed()
    {
        var failure = Assert.Throws<RouterConfigException>(() => LoadJson("""
            "resources":{"nano_cpus":3000000000},
            "resources":{}
            """));
        Assert.Contains("resources appears more than once", failure.Message, StringComparison.Ordinal);
    }

    private static DockerLifecycleConfig LoadToml(string? resources, string dockerFields = "")
    {
        var root = $$"""
            [engine.fixture]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:18066"
            lifecycle_owner = "router"

            [engine.fixture.docker]
            image = "example/hip:1"
            image_id = "{{PinnedId}}"
            image_repo_digest = "{{Manifest}}"
            image_platform = "linux/amd64"
            router_id = "fixture-router"
            {{dockerFields}}
            """;
        if (resources is not null) root += $"\n[engine.fixture.docker.resources]\n{resources}";
        return RouterConfigLoader.Load(root).Engines["fixture"].Docker!;
    }

    private static DockerLifecycleConfig LoadJson(string dockerFields)
    {
        var config = $$$"""
            {"engine":{"fixture":{
              "kind":"openai-compatible","endpoint":"http://127.0.0.1:18066","lifecycle_owner":"router",
              "docker":{
                "image":"example/hip:1","router_id":"fixture-router",
                "image_id":"{{{PinnedId}}}","image_repo_digest":"{{{Manifest}}}","image_platform":"linux/amd64",
                {{{dockerFields}}}
              }
            }
            }
            }
            """;
        return RouterConfigLoader.Load(config, isJson: true).Engines["fixture"].Docker!;
    }

    private static JsonDocument Project(DockerLifecycleConfig cfg)
    {
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(cfg, "fixture", daemon);
        var request = provider.BuildCreateRequest(Target(), cfg.ImageIdentity!);
        Assert.Empty(daemon.Requests);
        return JsonSerializer.SerializeToDocument(request, DockerJsonContext.Default.DockerCreateRequest);
    }

    private static LifecycleTarget Target() =>
        new("fixture", "model", null, new EngineModelDefinition { Name = "model" });

    private static void JsonEqual(string expectedBody, JsonElement actual)
    {
        using var expected = JsonDocument.Parse(expectedBody);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual), actual.GetRawText());
    }
}
