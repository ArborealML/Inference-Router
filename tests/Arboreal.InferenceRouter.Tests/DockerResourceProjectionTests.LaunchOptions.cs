using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerResourceProjectionTests
{
    private const string EntrypointField = """entrypoint = "/opt/maya-python/bin/python" """;
    private const string HostIpcField = """ipc_mode = "host" """;
    private const string LaunchOptionCatalog = "\n[engine.fixture.catalog.intended]";
    private const string TargetCatalog = """

        [[engine.fixture.catalog.intended.serving]]
        name = "32k"
        max_context = 32768
        docker = { env = ["PLAN=32k"] }
        """;
    private const string MiniMaxBody = """
        {
          "Image": "sha256:1111111111111111111111111111111111111111111111111111111111111111",
          "Entrypoint": ["/opt/maya-python/bin/python"],
          "Cmd": ["/selector.py", "serve", "32k"],
          "Env": ["PYTHONPATH=/work:/work/tools", "MAYA_UPDATE_CHECK=0"],
          "Labels": {
            "arboreal.router": "example-router",
            "arboreal.engine": "minimax",
            "arboreal.model": "minimax-m3.1-flash",
            "arboreal.profile": "32k"
          },
          "StopTimeout": 120,
          "HostConfig": {
            "IpcMode": "host",
            "PortBindings": {"8080/tcp": [{"HostIp": "127.0.0.1", "HostPort": "18123"}]},
            "DeviceRequests": [{"Driver": "", "Count": -1, "Capabilities": [["gpu"]]}],
            "Mounts": [
              {"Type": "bind", "Source": "/srv/example/maya", "Target": "/work", "ReadOnly": true},
              {"Type": "bind", "Source": "/srv/example/minimax-assets", "Target": "/assets", "ReadOnly": true},
              {"Type": "bind", "Source": "/srv/example/credential", "Target": "/api-key", "ReadOnly": true},
              {"Type": "bind", "Source": "/srv/example/minimax_selector.py", "Target": "/selector.py", "ReadOnly": true},
              {"Type": "bind", "Source": "/srv/example/glm_selector.py", "Target": "/glm_selector.py", "ReadOnly": true},
              {"Type": "bind", "Source": "/srv/example/minimax-assets/models", "Target": "/srv/example/minimax-assets/models", "ReadOnly": true},
              {"Type": "bind", "Source": "/srv/example/minimax-state", "Target": "/serving", "ReadOnly": false}
            ],
            "NanoCpus": 20000000000,
            "Memory": 57982058496,
            "MemorySwap": 60129542144
          }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MiniMax32KServingOptionsProjectTheExactOfflinePayload(bool isJson)
    {
        var engine = LoadMiniMax(isJson).Engines["minimax"];
        var model = engine.Catalog["minimax-m3.1-flash"];
        var tier = Assert.Single(model.ServingProfiles);
        Assert.Equal("32k", model.Profile);
        Assert.Equal(model.Profile, tier.Name);
        Assert.Equal(32768L, tier.MaxContext);
        Assert.Equal(1, tier.MaxSlots);
        Assert.Collection(model.Aliases, item => Assert.Equal("minimax", item), item => Assert.Equal("minimax-maya-xs", item));
        foreach (var spelling in model.Aliases.Prepend(model.Name))
        {
            var canonical = Assert.Single(engine.Catalog.Values, candidate =>
                string.Equals(candidate.Name, spelling, StringComparison.OrdinalIgnoreCase) ||
                candidate.Aliases.Contains(spelling, StringComparer.OrdinalIgnoreCase));
            using var omitted = Project(engine.Docker!, new LifecycleTarget(engine.Name, canonical.Name, null, canonical));
            using var explicitTier = Project(engine.Docker!, new LifecycleTarget(engine.Name, canonical.Name, tier, canonical));
            JsonEqual(MiniMaxBody, omitted.RootElement);
            JsonEqual(MiniMaxBody, explicitTier.RootElement);
        }
    }

    [Theory]
    [InlineData(HostIpcField, null, """{"IpcMode":"host"}""")]
    [InlineData("""entrypoint = "/run/{{model}}/image-program" """, "/run/{{model}}/image-program", null)]
    public void IsolatedLaunchOptionsProjectWithoutAddingOtherFields(string fields, string? entrypoint, string? host)
    {
        using var actual = Project(LoadToml(resources: null, dockerFields: fields));
        var root = actual.RootElement;
        Assert.False(root.TryGetProperty("Cmd", out _));
        Assert.Equal(entrypoint is not null, root.TryGetProperty("Entrypoint", out var projectedEntrypoint));
        if (entrypoint is not null)
        {
            Assert.Equal(1, projectedEntrypoint.GetArrayLength());
            Assert.Equal(entrypoint, projectedEntrypoint[0].GetString());
        }
        Assert.Equal(host is not null, root.TryGetProperty("HostConfig", out var projectedHost));
        if (host is not null) JsonEqual(host, projectedHost);
    }

    [Theory]
    [InlineData("entrypoint", "\"\"")]
    [InlineData("entrypoint", "\"bin/serve\"")]
    [InlineData("entrypoint", "\"/opt/../serve\"")]
    [InlineData("entrypoint", "\"/opt/./serve\"")]
    [InlineData("entrypoint", "\"/opt//serve\"")]
    [InlineData("entrypoint", "\"/opt/serve/\"")]
    [InlineData("entrypoint", "\" /opt/serve\"")]
    [InlineData("entrypoint", "\"/opt/serve \"")]
    [InlineData("entrypoint", "\"/opt\\\\serve\"")]
    [InlineData("entrypoint", "\"/opt/serve\\n\"")]
    [InlineData("ipc_mode", "\"\"")]
    [InlineData("ipc_mode", "\"HOST\"")]
    [InlineData("ipc_mode", "\"container:other\"")]
    [InlineData("ipc_mode", "\"private\"")]
    public void LaunchOptionValuesAreValidatedInBothFormats(string field, string value)
    {
        var toml = Assert.Throws<RouterConfigException>(() => LoadToml(null, $"{field} = {value}"));
        var json = Assert.Throws<RouterConfigException>(() => LoadJson($"\"{field}\":{value}"));
        Assert.StartsWith($"engine.fixture.docker.{field} ", toml.Message, StringComparison.Ordinal);
        Assert.StartsWith($"engine.fixture.docker.{field} ", json.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("entrypoint = \"/opt/one\"\nEntrypoint = \"/opt/two\"", "entrypoint")]
    [InlineData("ipc_mode = \"host\"\nIPC_MODE = \"host\"", "ipc_mode")]
    public void LaunchOptionTomlCaseTwinsAreRefused(string fields, string field)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadToml(null, fields));
        Assert.Contains(field, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(""" "entrypoint":null """, "entrypoint")]
    [InlineData(""" "ipc_mode":null """, "ipc_mode")]
    [InlineData(""" "Entrypoint":"/opt/python" """, "entrypoint")]
    [InlineData(""" "IPC_MODE":"host" """, "ipc_mode")]
    [InlineData(""" "entrypoint":"/opt/one","entrypoint":"/opt/two" """, "entrypoint")]
    [InlineData(""" "ipc_mode":"host","IPC_MODE":"host" """, "ipc_mode")]
    public void LaunchOptionJsonRejectsNullDuplicatesAndNoncanonicalNames(string fields, string field)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadJson(fields));
        Assert.StartsWith($"engine.fixture.docker.{field} ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"engine":{"fixture":{"docker":{"entrypoint":"/opt/python"}}},"engine":null}""", "engine")]
    [InlineData("""{"Engine":{"fixture":{"docker":{"entrypoint":"/opt/python"}}},"engine":{}}""", "engine")]
    [InlineData("""{"engine":{"fixture":{"docker":{"ipc_mode":"host"}},"fixture":{}}}""", "engine.fixture")]
    [InlineData("""{"engine":{"fixture":{"docker":{"ipc_mode":"host"},"docker":null}}}""", "engine.fixture.docker")]
    [InlineData("""{"engine":{"fixture":{"Docker":{"entrypoint":"/opt/python"},"docker":{}}}}""", "engine.fixture.docker")]
    [InlineData("""{"engine":{"fixture":{"docker":{"ipc_mode":"host"},"lifecycle_owner":"external","lifecycle_owner":"router"}}}""",
        "engine.fixture.lifecycle_owner")]
    [InlineData("""{"engine":{"fixture":{"docker":{"entrypoint":"/opt/python"},"remote":true,"remote":null}}}""", "engine.fixture.remote")]
    [InlineData("""{"engine":{"fixture":{"docker":{"ipc_mode":"host"},"lifecycle":"exec","lifecycle":"docker"}}}""", "engine.fixture.lifecycle")]
    [InlineData("""{"engine":{"fixture":{"docker":{"ipc_mode":"host"},"catalog":{"model":{"select":{"exec":"/bin/selector"},"select":null}}}}}""",
        "engine.fixture.catalog.model.select")]
    public void LaunchOptionJsonCannotHideOptInOrAuthority(string json, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(json, isJson: true));
        Assert.StartsWith(path + " ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative", null, "router", "docker.entrypoint")]
    [InlineData(null, "container:other", "router", "docker.ipc_mode")]
    [InlineData("/opt/python", null, "external", "requires a native Docker engine")]
    [InlineData(null, "host", "delegated", "requires a native Docker engine")]
    public void LaunchOptionValuesAndOwnerAreValidatedForDirectDtos(string? entrypoint, string? ipcMode,
        string owner, string expected)
    {
        var docker = new DockerDto { Image = PinnedId, RouterId = "fixture-router" };
        if (entrypoint is not null) docker.Entrypoint = entrypoint;
        if (ipcMode is not null) docker.IpcMode = ipcMode;
        var dto = new ConfigDto { Engines = new()
        {
            ["fixture"] = new EngineDto { Kind = "openai-compatible", Endpoint = "http://127.0.0.1:18066",
                LifecycleOwner = owner, Docker = docker },
        } };
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.FromDto(dto, "fixture"));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EntrypointField)]
    [InlineData(HostIpcField)]
    public async Task LaunchOptionsRefuseDefaultAndInjectedStartup(string fields)
    {
        var definition = new EngineDefinition
        {
            Name = "fixture",
            Endpoint = new Uri("http://127.0.0.1:18066"),
            LifecycleOwner = EngineLifecycleOwner.Router,
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = LoadToml(null, fields),
        };
        using var daemon = new FakeDockerDaemonHandler();
        using var injected = new DockerLifecycleProvider(LoadToml(resources: null), "different-engine", daemon);
        foreach (var provider in new[] { null, injected })
        {
            var error = await Assert.ThrowsAsync<RouterConfigException>(async () =>
            {
                await using var runtime = new EngineRuntime(definition, new RouterConfig(), new EventRing(),
                    handler: daemon, lifecycle: provider);
            });
            Assert.Contains("docker.entrypoint and docker.ipc_mode require host qualification", error.Message, StringComparison.Ordinal);
        }
        Assert.Empty(daemon.Requests);
    }

    private static RouterConfig LoadMiniMax(bool isJson) => RouterConfigLoader.Load(isJson ? $$$$"""
        {"engine":{"minimax":{
          "kind":"openai-compatible","endpoint":"http://127.0.0.1:18123/v1","lifecycle_owner":"router",
          "docker":{
            "image":"{{{{PinnedId}}}}","image_id":"{{{{PinnedId}}}}","image_repo_digest":"{{{{Manifest}}}}",
            "image_platform":"linux/amd64","router_id":"example-router","stop_timeout_ms":120000,
            "host_port":18123,"entrypoint":"/opt/maya-python/bin/python","ipc_mode":"host",
            "command":["/selector.py","serve","{{profile}}"],"env":["PYTHONPATH=/work:/work/tools","MAYA_UPDATE_CHECK=0"],
            "resources":{"gpu":{"all":true},"nano_cpus":20000000000,"memory_bytes":57982058496,"memory_swap_bytes":60129542144,
              "bind_mounts":[
                {"source":"/srv/example/maya","target":"/work","read_only":true},
                {"source":"/srv/example/minimax-assets","target":"/assets","read_only":true},
                {"source":"/srv/example/credential","target":"/api-key","read_only":true},
                {"source":"/srv/example/minimax_selector.py","target":"/selector.py","read_only":true},
                {"source":"/srv/example/glm_selector.py","target":"/glm_selector.py","read_only":true},
                {"source":"/srv/example/minimax-assets/models","target":"/srv/example/minimax-assets/models","read_only":true},
                {"source":"/srv/example/minimax-state","target":"/serving","read_only":false}
              ]}
          },
          "catalog":{"minimax-m3.1-flash":{"aliases":["minimax","minimax-maya-xs"],"default_profile":"32k",
            "serving":[{"name":"32k","max_context":32768,"max_slots":1}]}}
        }}}
        """ : $$$"""
        [engine.minimax]
        kind = "openai-compatible"
        endpoint = "http://127.0.0.1:18123/v1"
        lifecycle_owner = "router"
        [engine.minimax.docker]
        image = "{{{PinnedId}}}"
        image_id = "{{{PinnedId}}}"
        image_repo_digest = "{{{Manifest}}}"
        image_platform = "linux/amd64"
        router_id = "example-router"
        stop_timeout_ms = 120000
        host_port = 18123
        entrypoint = "/opt/maya-python/bin/python"
        ipc_mode = "host"
        command = ["/selector.py", "serve", "{{profile}}"]
        env = ["PYTHONPATH=/work:/work/tools", "MAYA_UPDATE_CHECK=0"]
        [engine.minimax.docker.resources]
        gpu = { all = true }
        nano_cpus = 20000000000
        memory_bytes = 57982058496
        memory_swap_bytes = 60129542144
        bind_mounts = [
          { source = "/srv/example/maya", target = "/work", read_only = true },
          { source = "/srv/example/minimax-assets", target = "/assets", read_only = true },
          { source = "/srv/example/credential", target = "/api-key", read_only = true },
          { source = "/srv/example/minimax_selector.py", target = "/selector.py", read_only = true },
          { source = "/srv/example/glm_selector.py", target = "/glm_selector.py", read_only = true },
          { source = "/srv/example/minimax-assets/models", target = "/srv/example/minimax-assets/models", read_only = true },
          { source = "/srv/example/minimax-state", target = "/serving", read_only = false },
        ]
        [engine.minimax.catalog."minimax-m3.1-flash"]
        aliases = ["minimax", "minimax-maya-xs"]
        default_profile = "32k"
        serving = [{ name = "32k", max_context = 32768, max_slots = 1 }]
        """, isJson: isJson);
}
