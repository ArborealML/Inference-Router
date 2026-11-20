using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerTargetProjectionTests
{
    private const string Hy4Model = ConfigLoaderTests.Hy4Canonical;
    private static readonly string[] Hy4ModelSpellings = [Hy4Model, "hy4", "hy4-flash-maya-xs"];

    private const string Hy4NativeConfig = """
        default_model = "hy4-flash"
        [engine.maya]
        kind = "openai-compatible"
        endpoint = "http://127.0.0.1:28060/v1"
        lifecycle_owner = "router"

        [engine.maya.docker]
        image = "registry.invalid/hy4/maya:offline"
        image_id = "sha256:2222222222222222222222222222222222222222222222222222222222222222"
        image_repo_digest = "registry.invalid/hy4/maya@sha256:3333333333333333333333333333333333333333333333333333333333333333"
        image_platform = "linux/amd64"
        router_id = "hy4-fixture"
        entrypoint = "/opt/maya-python/bin/python"
        ipc_mode = "host"
        host_ip = "127.0.0.1"
        host_port = 28060
        container_port = 8080
        stop_timeout_ms = 120000
        env = ["PYTHONPATH=/work:/work/tools", "MAYA_UPDATE_CHECK=0"]

        [engine.maya.docker.resources]
        gpu = { all = true }
        nano_cpus = 20000000000
        memory_bytes = 57982058496
        memory_swap_bytes = 60129542144
        bind_mounts = [
          { source = "/fixture/maya-source", target = "/work", read_only = true },
          { source = "/fixture/hy4-assets", target = "/assets", read_only = true },
          { source = "/fixture/key-role", target = "/api-key", read_only = true },
          { source = "/fixture/hy4_selector.py", target = "/selector.py", read_only = true },
          { source = "/fixture/glm_selector.py", target = "/glm_selector.py", read_only = true },
          { source = "/fixture/hy4-assets/models", target = "/fixture/hy4-assets/models", read_only = true },
          { source = "/fixture/hy4-logs", target = "/serving", read_only = false },
        ]

        [engine.maya.catalog."hy4-flash"]
        aliases = ["hy4", "hy4-flash-maya-xs"]
        """;

    private const string Hy4ServingGolden = """
        {
          "Image":"sha256:2222222222222222222222222222222222222222222222222222222222222222",
          "Entrypoint":["/opt/maya-python/bin/python"],
          "Cmd":["/selector.py","serve","TIER"],
          "Env":["PYTHONPATH=/work:/work/tools","MAYA_UPDATE_CHECK=0"],
          "Labels":{"arboreal.router":"hy4-fixture","arboreal.engine":"maya",
            "arboreal.model":"hy4-flash","arboreal.profile":"TIER"},
          "StopTimeout":120,
          "ExposedPorts":{"8080/tcp":{}},
          "HostConfig":{
            "IpcMode":"host",
            "PortBindings":{"8080/tcp":[{"HostIp":"127.0.0.1","HostPort":"28060"}]},
            "DeviceRequests":[{"Driver":"","Count":-1,"Capabilities":[["gpu"]]}],
            "Mounts":[
              {"Type":"bind","Source":"/fixture/maya-source","Target":"/work","ReadOnly":true},
              {"Type":"bind","Source":"/fixture/hy4-assets","Target":"/assets","ReadOnly":true},
              {"Type":"bind","Source":"/fixture/key-role","Target":"/api-key","ReadOnly":true},
              {"Type":"bind","Source":"/fixture/hy4_selector.py","Target":"/selector.py","ReadOnly":true},
              {"Type":"bind","Source":"/fixture/glm_selector.py","Target":"/glm_selector.py","ReadOnly":true},
              {"Type":"bind","Source":"/fixture/hy4-assets/models","Target":"/fixture/hy4-assets/models","ReadOnly":true},
              {"Type":"bind","Source":"/fixture/hy4-logs","Target":"/serving","ReadOnly":false}
            ],
            "NanoCpus":20000000000,"Memory":57982058496,"MemorySwap":60129542144
          }
        }
        """;

    [Theory(Timeout = 15_000)]
    [InlineData("32k", 32_768L)]
    [InlineData("64k", 65_536L)]
    [InlineData("96k", 98_304L)]
    [InlineData("128k", 131_072L)]
    [InlineData("256k", 262_144L)]
    [InlineData("512k", 524_288L)]
    [InlineData("1024k", 1_048_576L)]
    public async Task Hy4FlashProjectsSelectorServingPayload(string name, long window)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = watchdog.Token;
        var sample = ConfigLoaderTests.ReadMayaEngineForHy4();
        var sampleModel = sample.Catalog[Hy4Model];
        var native = LoadNativeHy4(sampleModel);
        var nativeModel = native.Catalog[Hy4Model];
        var tier = Assert.Single(nativeModel.ServingProfiles, profile => profile.Name == name);
        Assert.Equal(window, tier.MaxContext);
        Assert.Equal(1, tier.MaxSlots);
        var docker = Assert.IsType<DockerLifecycleConfig>(native.Docker);
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var provider = new DockerLifecycleProvider(docker, native.Name, daemon);
        var resolver = new VerifiedDispatchTargetResolver(native, provider);
        using var expected = JsonDocument.Parse(Hy4ServingGolden.Replace("TIER", name, StringComparison.Ordinal));
        var expectedTarget = resolver.Resolve(Hy4Model, name, null, "hy4-fixture-config", "hy4-fixture-route");
        foreach (var spelling in Hy4ModelSpellings)
        {
            var resolved = resolver.Resolve(spelling, name, null, "hy4-fixture-config", "hy4-fixture-route");
            Assert.Equal(Hy4Model, resolved.Model);
            Assert.Equal(name, resolved.Tier);
            Assert.Equal(expectedTarget, resolved);
            using var create = JsonDocument.Parse(Project(provider, native, spelling, name));
            Assert.True(JsonElement.DeepEquals(expected.RootElement, create.RootElement), create.RootElement.GetRawText());
        }

        if (name == "96k")
        {
            foreach (var spelling in Hy4ModelSpellings)
            {
                var omitted = resolver.Resolve(spelling, null, null, "hy4-fixture-config", "hy4-fixture-route");
                Assert.Equal(expectedTarget, omitted);
                Assert.Equal("96k", omitted.Tier);
                Assert.Equal(Project(provider, native, spelling, name), Project(provider, native, spelling));

                var explicitSmall = resolver.Resolve(spelling, "32k", null, "hy4-fixture-config", "hy4-fixture-route");
                Assert.Equal("32k", explicitSmall.Tier);
                Assert.NotEqual(omitted.PlanDigest, explicitSmall.PlanDigest);
            }
            var digests = nativeModel.ServingProfiles.Select(profile =>
                resolver.Resolve("hy4", profile.Name, null, "hy4-fixture-config", "hy4-fixture-route").PlanDigest);
            Assert.Equal(7, digests.Distinct(StringComparer.Ordinal).Count());
            var unsupported = Assert.Throws<VerifiedDispatchBindingException>(() =>
                resolver.Resolve("hy4", "1m", null, "hy4-fixture-config", "hy4-fixture-route"));
            Assert.Equal(VerifiedDispatchRefusal.Target, unsupported.Refusal);
        }

        if (name is "32k" or "96k")
        {
            var target = new LifecycleTarget(native.Name, Hy4Model, tier, nativeModel);
            AssertHy4ProviderRefusal(await provider.PreflightAsync(target, ct));
            AssertHy4ProviderRefusal(await provider.StopAsync(target,
                [new EnginePeer("incumbent", docker.ImageIdentity!.Id, Hy4Model, true)], ct));
            AssertHy4ProviderRefusal(await provider.LaunchAsync(target, ct));
        }
        Assert.Empty(daemon.Requests);
        await ConfigLoaderTests.CheckHy4SampleProbeAsync(sample, name, window, ct);
    }

    private static void AssertHy4ProviderRefusal(LifecycleResult result)
    {
        Assert.False(result.Succeeded);
        Assert.Equal("docker_target_not_qualified", result.Reason);
    }

    private static EngineDefinition LoadNativeHy4(EngineModelDefinition sample)
    {
        // Project each exact catalog row as future launch intent. The installed selector
        // accepts only 32k, and the independent golden does not model that selector gate.
        var catalog = $"""default_profile = "{sample.Profile}" """;
        var tiers = string.Join('\n', sample.ServingProfiles.Select(profile => FormattableString.Invariant($"""
            [[engine.maya.catalog."hy4-flash".serving]]
            name = "{profile.Name}"
            max_context = {profile.MaxContext}
            max_slots = {profile.MaxSlots}
            [engine.maya.catalog."hy4-flash".serving.docker]
            command = ["/selector.py", "serve", "{profile.Name}"]
            """)));
        return RouterConfigLoader.Load(Hy4NativeConfig + "\n" + catalog + "\n" + tiers).Engines["maya"];
    }
}
