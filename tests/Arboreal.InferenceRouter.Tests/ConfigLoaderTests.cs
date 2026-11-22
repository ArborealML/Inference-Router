using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

public partial class ConfigLoaderTests
{
    private const string TomlConfig = """
        listen = "http://127.0.0.1:9901"
        default_model = "m1"
        auto_switch = true

        [default_priority]
        class = "high"
        sub = 100

        [scheduler]
        max_queue_per_model = 7
        prefix_affinity_threshold_chars = 256
        switch_wait_timeout_ms = 60000

        [threads.chat-ui]
        priority = { class = "interactive", sub = 10 }
        model_affinity = "m1"

        [engine.fake]
        kind = "openai-compatible"
        endpoint = "http://127.0.0.1:9/v1"
        api_key = "secret"
        loaded_requires_status_loaded = true
        response_timeout_ms = 90000

        [engine.fake.health_probe]
        path = "/props"
        expect = "total_slots>0"
        interval_ms = 500
        context_path = "n_ctx"
        slots_path = "slots.active"

        [engine.fake.catalog.m1]
        aliases = ["one"]
        profiles = ["max"]
        default_profile = "small"

        [[engine.fake.catalog.m1.serving]]
        name = "small"
        max_context = 131072
        max_slots = 2

        [[engine.fake.catalog.m1.serving]]
        name = "big"
        max_context = 1048576
        max_slots = 1

        [engine.fake.catalog.m2]
        select.exec = "/bin/echo"
        select.args = ["{{model}}"]
        select.wait_health = false
        select.wait_timeout_ms = 30000

        [engine.fake.capabilities]
        vision = true
        max_context = 4096
        """;

    private const string JsonConfig = """
        // JSON twin of the TOML config — both must load identically.
        {
          "listen": "http://127.0.0.1:9901",
          "default_model": "m1",
          "auto_switch": true,
          "default_priority": { "class": "high", "sub": 100 },
          "scheduler": { "max_queue_per_model": 7, "prefix_affinity_threshold_chars": 256, "switch_wait_timeout_ms": 60000 },
          "threads": {
            "chat-ui": { "priority": { "class": "interactive", "sub": 10 }, "model_affinity": "m1" }
          },
          "engine": {
            "fake": {
              "kind": "openai-compatible",
              "endpoint": "http://127.0.0.1:9/v1",
              "api_key": "secret",
              "loaded_requires_status_loaded": true,
              "response_timeout_ms": 90000,
              "health_probe": { "path": "/props", "expect": "total_slots>0", "interval_ms": 500, "context_path": "n_ctx", "slots_path": "slots.active" },
              "catalog": {
                "m1": {
                  "aliases": ["one"],
                  "profiles": ["max"],
                  "default_profile": "small",
                  "serving": [
                    { "name": "small", "max_context": 131072, "max_slots": 2 },
                    { "name": "big", "max_context": 1048576, "max_slots": 1 }
                  ]
                },
                "m2": { "select": { "exec": "/bin/echo", "args": ["{{model}}"], "wait_health": false, "wait_timeout_ms": 30000 } }
              },
              "capabilities": { "vision": true, "max_context": 4096 }
            }
          }
        }
        """;

    [Fact]
    public void Parses_Toml()
    {
        var cfg = RouterConfigLoader.Load(TomlConfig, "test.toml");
        Assert.Equal("http://127.0.0.1:9901", cfg.Listen);
        Assert.Equal("m1", cfg.DefaultModel);
        Assert.True(cfg.AutoSwitch);
        Assert.Equal(ThreadPriorityClass.High, cfg.DefaultPriority.Class);
        Assert.Equal(100, cfg.DefaultPriority.Sub);
        Assert.Equal(7, cfg.Scheduler.MaxQueuePerModel);
        Assert.Equal(60_000, cfg.Scheduler.SwitchWaitTimeoutMs);
        Assert.Equal(256, cfg.Scheduler.PrefixAffinityThresholdChars);

        var e = cfg.Engines["fake"];
        Assert.Equal(EngineKind.OpenAiCompatible, e.Kind);
        Assert.Equal("http://127.0.0.1:9/v1", e.Endpoint.ToString());
        Assert.Equal("secret", e.ApiKey);
        Assert.True(e.LoadedRequiresStatusLoaded);
        Assert.Equal(90_000, e.ResponseTimeoutMs);
        Assert.Equal("/props", e.Health!.Path);
        Assert.Equal("total_slots>0", e.Health.Expect);
        Assert.Equal(500, e.Health.IntervalMs);
        Assert.Equal("n_ctx", e.Health.ContextPath);
        Assert.Equal("slots.active", e.Health.SlotsPath);

        var m1 = e.Catalog["m1"];
        Assert.Equal(["one"], m1.Aliases);
        Assert.Equal(["max"], m1.Profiles);
        Assert.Null(m1.Select);
        Assert.Equal("small", m1.Profile);
        Assert.Equal(2, m1.ServingProfiles.Count);
        Assert.Equal("small", m1.ServingProfiles[0].Name);
        Assert.Equal(131072, m1.ServingProfiles[0].MaxContext);
        Assert.Equal(2, m1.ServingProfiles[0].MaxSlots);
        Assert.Equal("big", m1.ServingProfiles[1].Name);
        Assert.Equal(1048576, m1.ServingProfiles[1].MaxContext);
        Assert.Equal(1, m1.ServingProfiles[1].MaxSlots);

        var m2 = e.Catalog["m2"];
        Assert.NotNull(m2.Select);
        Assert.Equal("/bin/echo", m2.Select!.Exec);
        Assert.Equal(["{{model}}"], m2.Select.Args);
        Assert.False(m2.Select.WaitHealth);
        Assert.Equal(30_000, m2.Select.WaitTimeoutMs);

        Assert.True(e.Capabilities.Vision);
        Assert.Equal(4096, e.Capabilities.MaxContext);

        var thread = cfg.Threads["chat-ui"];
        Assert.Equal(ThreadPriorityClass.Interactive, thread.Priority.Class);
        Assert.Equal(10, thread.Priority.Sub);
        Assert.Equal("m1", thread.ModelAffinity);
    }

    [Fact]
    public void Parses_Json_Twin_Identically()
    {
        var toml = RouterConfigLoader.Load(TomlConfig, "test.toml");
        var json = RouterConfigLoader.Load(JsonConfig, "test.json", isJson: true);
        Assert.Equal(toml.Listen, json.Listen);
        Assert.Equal(toml.DefaultModel, json.DefaultModel);
        Assert.Equal(toml.DefaultPriority, json.DefaultPriority);
        Assert.Equal(toml.Scheduler.MaxQueuePerModel, json.Scheduler.MaxQueuePerModel);
        Assert.Equal(toml.Scheduler.SwitchWaitTimeoutMs, json.Scheduler.SwitchWaitTimeoutMs);
        Assert.Equal(toml.Threads["chat-ui"].Priority, json.Threads["chat-ui"].Priority);
        var te = toml.Engines["fake"];
        var je = json.Engines["fake"];
        Assert.Equal(te.Endpoint, je.Endpoint);
        Assert.Equal(te.ApiKey, je.ApiKey);
        Assert.Equal(te.LoadedRequiresStatusLoaded, je.LoadedRequiresStatusLoaded);
        Assert.Equal(te.ResponseTimeoutMs, je.ResponseTimeoutMs);
        Assert.Equal(te.Health!.Path, je.Health!.Path);
        Assert.Equal(te.Health.ContextPath, je.Health.ContextPath);
        Assert.Equal(te.Health.SlotsPath, je.Health.SlotsPath);
        Assert.Equal(te.Catalog["m1"].Profile, je.Catalog["m1"].Profile);
        Assert.Equal(
            te.Catalog["m1"].ServingProfiles.Select(p => (p.Name, p.MaxContext, p.MaxSlots)),
            je.Catalog["m1"].ServingProfiles.Select(p => (p.Name, p.MaxContext, p.MaxSlots)));
        Assert.Equal(te.Catalog.Keys, je.Catalog.Keys);
        Assert.Equal(te.Capabilities.MaxContext, je.Capabilities.MaxContext);
        Assert.Equal(te.Capabilities.Vision, je.Capabilities.Vision);
    }

    /// <summary>The catalogue probe path is per-engine: llama.cpp-style servers answer `/models` at the
    /// root, OpenAI-only servers answer `/v1/models`. Point the probe at the wrong one and a perfectly
    /// healthy engine looks unhealthy, so the knob is defaulted, overridable and sample-pinned.</summary>
    [Fact]
    public void Catalogue_Probe_Path_Is_Configurable()
    {
        const string Text = """
            listen = "http://127.0.0.1:9901"
            default_model = "m1"
            [engine.plain]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"
            [engine.plain.catalog."m1"]
            [engine.openai_only]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:10/v1"
            models_endpoint = "/v1/models"
            models_context_window_path = "meta.n_ctx"
            loaded_requires_status_loaded = false
            [engine.openai_only.catalog."m1"]
            """;

        var cfg = RouterConfigLoader.Load(Text, "test.toml");
        Assert.Equal("/models", cfg.Engines["plain"].ModelsEndpoint);
        Assert.Null(cfg.Engines["plain"].ModelsContextWindowPath);
        Assert.Equal("/v1/models", cfg.Engines["openai_only"].ModelsEndpoint);
        Assert.Equal("meta.n_ctx", cfg.Engines["openai_only"].ModelsContextWindowPath);
        Assert.False(cfg.Engines["openai_only"].LoadedRequiresStatusLoaded);
    }

    [Theory]
    [InlineData("n_ctx", true)]
    [InlineData("meta.n_ctx", true)]
    [InlineData("runtime.context.tokens", true)]
    [InlineData("", false)]
    [InlineData(".meta", false)]
    [InlineData("meta.", false)]
    [InlineData("meta..n_ctx", false)]
    [InlineData("meta. n_ctx", false)]
    public void ModelContextPathsAreValidatedForTomlAndJson(string path, bool valid)
    {
        var toml = $$"""
            [engine.fake]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"
            models_context_window_path = "{{path}}"
            """;
        var json = $$"""
            {"engine":{"fake":{"kind":"openai-compatible","endpoint":"http://127.0.0.1:9/v1",
            "models_context_window_path":"{{path}}" } } }
            """;
        if (valid)
        {
            Assert.Equal(path, RouterConfigLoader.Load(toml, "test.toml").Engines["fake"].ModelsContextWindowPath);
            Assert.Equal(path, RouterConfigLoader.Load(json, "test.json", isJson: true).Engines["fake"].ModelsContextWindowPath);
        }
        else
        {
            var tomlError = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(toml, "test.toml"));
            var jsonError = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(json, "test.json", isJson: true));
            Assert.Contains("models_context_window_path", tomlError.Message, StringComparison.Ordinal);
            Assert.Contains("models_context_window_path", jsonError.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Native Docker lifecycle is opt-in per engine, and a half-configured one is a load-time
    /// error: an engine that says "docker" with no image, or a model that keeps a select command while the
    /// router owns the container, is two owners claiming one lifecycle.</summary>
    [Fact]
    public void Native_Docker_Lifecycle_Is_Configurable_And_Exclusive()
    {
        const string Text = """
            listen = "http://127.0.0.1:9901"
            default_model = "m1"
            [engine.native]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"
            lifecycle = "docker"
            [engine.native.docker]
            image = "registry.local/strata:2026-11"
            router_id = "r-e2e"
            socket = "unix:///run/docker.sock"
            api_version = "v1.44"
            command = ["--model", "{{model}}"]
            host_port = 18021
            container_port = 8080
            [engine.native.catalog."m1"]
            """;

        var cfg = RouterConfigLoader.Load(Text, "test.toml");
        var engine = cfg.Engines["native"];
        Assert.Equal(EngineLifecycleKind.Docker, engine.Lifecycle);
        Assert.NotNull(engine.Docker);
        Assert.Equal("registry.local/strata:2026-11", engine.Docker!.Image);
        Assert.Equal("r-e2e", engine.Docker.RouterId);
        Assert.Equal("unix:///run/docker.sock", engine.Docker.Socket);
        Assert.Equal("v1.44", engine.Docker.ApiVersion);
        Assert.Equal(["--model", "{{model}}"], engine.Docker.Command);
        Assert.Equal(18021, engine.Docker.HostPort);
        Assert.Equal("127.0.0.1", engine.Docker.HostIp);   // a published port is loopback unless named otherwise
        Assert.True(engine.Docker.RemoveStopped);           // a switch does not leave stopped containers behind
        Assert.True(engine.Docker.WaitHealth);          // readiness is proved unless the operator says otherwise
        Assert.Equal(30_000, engine.Docker.StopTimeoutMs);
        Assert.Equal(600_000, engine.Docker.WaitTimeoutMs);
        Assert.Equal(300_000, engine.Docker.CallTimeoutMs);
        Assert.Equal(300_000, new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" }.CallTimeoutMs);
        var overridden = Text.Replace("container_port = 8080", "container_port = 8080\ncall_timeout_ms = 19000", StringComparison.Ordinal);
        Assert.Equal(19_000, RouterConfigLoader.Load(overridden).Engines["native"].Docker!.CallTimeoutMs);
    }

    [Fact]
    public void Native_Docker_Lifecycle_RejectsAnIncompleteOrContradictoryConfig()
    {
        const string Base = """
            listen = "http://127.0.0.1:9901"
            default_model = "m1"
            [engine.native]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"
            """;

        // "docker" with no block: the router would have nothing to start.
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            Base + "\nlifecycle = \"docker\"\n[engine.native.catalog.\"m1\"]\n", "test.toml"));

        // A block with no image: the router never pulls, so an unpinned image is not a default, it is a bug.
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            Base + "\n[engine.native.docker]\nsocket = \"unix:///run/docker.sock\"\n[engine.native.catalog.\"m1\"]\n",
            "test.toml"));

        // A block with no router_id: the label is the ownership proof, and a shared default would entitle
        // two routers on one daemon to stop each other's containers.
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            Base + "\n[engine.native.docker]\nimage = \"img:1\"\n[engine.native.catalog.\"m1\"]\n",
            "test.toml"));

        // Two owners for one model: the selector program and the router cannot both prove ownership.
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            Base + """
            [engine.native.docker]
            image = "img:1"
            router_id = "r1"
            [engine.native.catalog."m1"]
            select = { exec = "/bin/true" }
            """, "test.toml"));

        // A made-up lifecycle kind is a typo, not a future extension.
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            Base + "\nlifecycle = \"podman\"\n[engine.native.catalog.\"m1\"]\n", "test.toml"));

        // Delegated stays the default, so every existing config keeps working unchanged.
        Assert.Equal(EngineLifecycleKind.Delegated,
            RouterConfigLoader.Load(Base + "\n[engine.native.catalog.\"m1\"]\n", "test.toml").Engines["native"].Lifecycle);
    }

    private static readonly string[] DockerIdentityFields = ["image_id", "image_repo_digest", "image_platform"];
    private static readonly string?[] DockerIdentityValues =
    [
        "sha256:" + new string('1', 64),
        "example/inference-backend@sha256:" + new string('2', 64),
        "linux/arm64/v8",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_Docker_Image_Identity_RoundTrips_And_Rejects_Partial_Pins(bool json)
    {
        var value = Assert.IsType<DockerImageIdentity>(LoadDockerPins(json, DockerIdentityFields.Zip(DockerIdentityValues)).ImageIdentity);
        Assert.Equal(DockerIdentityValues[0], value.Id);
        Assert.Equal(DockerIdentityValues[1], value.RepoDigest);
        Assert.Equal(DockerIdentityValues[2], value.Platform);
        Assert.Null(LoadDockerPins(json, []).ImageIdentity);

        // Every strict nonempty subset must fail rather than silently loading as a legacy config.
        for (var mask = 1; mask < 7; mask++)
        {
            var partial = DockerIdentityFields.Zip(DockerIdentityValues).Where((_, index) => (mask & (1 << index)) != 0);
            var ex = Assert.Throws<RouterConfigException>(() => LoadDockerPins(json, partial));
            Assert.Contains("engine.native.docker.image_", ex.Message);
        }
    }

    [Theory]
    [InlineData(false, 0, "")]
    [InlineData(true, 0, "")]
    [InlineData(false, 0, "sha256:1111")]
    [InlineData(true, 0, "sha256:1111")]
    [InlineData(false, 0, "sha256:111111111111111111111111111111111111111111111111111111111111111g")]
    [InlineData(true, 0, "sha256:111111111111111111111111111111111111111111111111111111111111111g")]
    [InlineData(false, 1, "")]
    [InlineData(true, 1, "")]
    [InlineData(false, 1, "sha256:2222222222222222222222222222222222222222222222222222222222222222")]
    [InlineData(true, 1, "sha256:2222222222222222222222222222222222222222222222222222222222222222")]
    [InlineData(false, 1, "image@sha256:2222")]
    [InlineData(true, 1, "image@sha256:2222")]
    [InlineData(false, 2, "")]
    [InlineData(true, 2, "")]
    [InlineData(false, 2, "linux/arm64/")]
    [InlineData(true, 2, "linux/arm64/")]
    [InlineData(false, 2, "Linux/arm64")]
    [InlineData(true, 2, "Linux/arm64")]
    [InlineData(true, 0, null)]
    [InlineData(true, 1, null)]
    [InlineData(true, 2, null)]
    public void Native_Docker_Image_Identity_Rejects_Malformed_Fields(bool json, int fieldIndex, string? badValue)
    {
        var values = DockerIdentityValues.Select((value, index) => index == fieldIndex ? badValue : value);
        var ex = Assert.Throws<RouterConfigException>(() => LoadDockerPins(json, DockerIdentityFields.Zip(values)));
        Assert.Contains($"engine.native.docker.{DockerIdentityFields[fieldIndex]} is missing or malformed", ex.Message);
    }

    [Theory]
    [InlineData("image_id")]
    [InlineData("image_repo_digest")]
    [InlineData("image_platform")]
    public void Native_Docker_Image_Identity_Rejects_A_Lone_Explicit_Json_Null(string field) =>
        Assert.Throws<RouterConfigException>(() => LoadDockerPins(json: true, [(field, null)]));

    private static DockerLifecycleConfig LoadDockerPins(bool json, IEnumerable<(string Field, string? Value)> pins)
    {
        const string Toml = """
            default_model = "m1"
            [engine.native]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"
            [engine.native.catalog."m1"]
            [engine.native.docker]
            image = "example/inference-backend:1"
            router_id = "r1"
            """;
        const string JsonStart = """
            {"default_model":"m1","engine":{"native":{"kind":"openai-compatible","endpoint":"http://127.0.0.1:9/v1",
            "catalog":{"m1":{}},"docker":{"image":"example/inference-backend:1","router_id":"r1"
            """;
        var text = json
            ? JsonStart + string.Concat(pins.Select(pin => $",\"{pin.Field}\":{(pin.Value is null ? "null" : $"\"{pin.Value}\"")}")) + "}}}}"
            : Toml + string.Concat(pins.Select(pin => $"\n{pin.Field} = \"{pin.Value}\""));
        return RouterConfigLoader.Load(text, isJson: json).Engines["native"].Docker!;
    }

    /// <summary>The shipped samples must load as written: dotted model ids quoted (TOML table
    /// separators otherwise silently create bogus catalog entries) and the smoke config inert.</summary>
    [Fact]
    public async Task Shipped_Sample_Configs_Load_As_Written()
    {
        var path = RepoFile("samples/router.toml");
        var smoke = RepoFile("samples/router.safe-smoke.toml");
        Assert.NotNull(path);
        Assert.NotNull(smoke);

        var cfg = RouterConfigLoader.LoadFile(path!);
        var strata = cfg.Engines["strata"];
        Assert.Equal(EngineLifecycleOwner.Delegated, strata.LifecycleOwner);
        Assert.Equal(EngineLifecycleOwner.External, cfg.Engines["hyperqwen"].LifecycleOwner);
        AssertMayaSample(cfg.Engines["glm"]);
        Assert.Contains("qwen-3.8-flash-next", strata.Catalog.Keys);
        Assert.DoesNotContain("qwen-3", strata.Catalog.Keys);
        var model = strata.Catalog["qwen-3.8-flash-next"];
        Assert.Equal(4, model.ServingProfiles.Count);
        var measured = model.ServingProfiles.Single(p => p.Name == "512k-1");
        Assert.Equal(524_288, measured.MaxContext);
        Assert.Equal(1, measured.MaxSlots);
        Assert.Equal("1m", model.ServingProfiles[^1].Name);
        Assert.Equal(1_048_576, model.ServingProfiles[^1].MaxContext);
        Assert.Equal("128k-2", model.Profile);
        Assert.NotNull(model.Select);
        Assert.Equal("n_ctx", strata.Health!.ContextPath);

        var nativePath = RepoFile("samples/router.native-docker.toml");
        Assert.NotNull(nativePath);
        var native = Assert.Single(RouterConfigLoader.LoadFile(nativePath!).Engines).Value;
        Assert.Equal(EngineLifecycleKind.Docker, native.Lifecycle);
        Assert.Equal<EngineLifecycleOwner?>(EngineLifecycleOwner.Router, native.LifecycleOwner);
        Assert.NotNull(native.Docker?.ImageIdentity);
        Assert.Equal(300_000, native.Docker!.CallTimeoutMs);
        Assert.NotEmpty(native.Catalog);
        Assert.All(native.Catalog.Values, entry => Assert.Null(entry.Select));

        var smokeCfg = RouterConfigLoader.LoadFile(smoke!);
        Assert.Contains("qwen-3.8-flash-next", smokeCfg.Engines["strata"].Catalog.Keys);
        Assert.All(smokeCfg.Engines.Values, e => Assert.True(e.Remote));
        Assert.All(smokeCfg.Engines.Values, e => Assert.Equal(EngineLifecycleOwner.External, e.LifecycleOwner));
        Assert.All(smokeCfg.Engines.Values, e => Assert.All(e.Catalog.Values, m => Assert.Null(m.Select)));
        var safe = smokeCfg.Engines["strata"];
        using var handler = new FakeEngineHandler();
        var provider = new RecordingLifecycleProvider { Kind = "docker" };
        await using var runtime = new EngineRuntime(safe, smokeCfg, new EventRing(), handler, lifecycle: provider);
        var result = await runtime.WaitForModelAsync("qwen-3.8-flash-next", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("no_lifecycle_owner", result.Detail);
        Assert.Empty(provider.Calls);

        // The CI smoke config pins where its catalogue probe looks, matching the fake engine.
        var ciSmoke = RepoFile("samples/router.ci-smoke.toml");
        Assert.NotNull(ciSmoke);
        var ciCfg = RouterConfigLoader.LoadFile(ciSmoke!);
        Assert.Equal(EngineLifecycleOwner.External, ciCfg.Engines["fixture"].LifecycleOwner);
        Assert.Equal("/v1/models", ciCfg.Engines["fixture"].ModelsEndpoint);
        Assert.Equal("/models", strata.ModelsEndpoint);
        Assert.Equal("/v1/models", cfg.Engines["hyperqwen"].ModelsEndpoint);
    }

    /// <summary>Walk up from the test assembly to the repo root and return the file, else null.</summary>
    private static string? RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void RequestBodyCap_IsConfigurableAndHasADefault()
    {
        Assert.Equal(16 * 1024 * 1024, RouterConfigLoader.Load(TomlConfig, "test.toml").MaxRequestBodyBytes);

        var cfg = RouterConfigLoader.Load(
            TomlConfig.Replace("auto_switch = true", "auto_switch = true\nmax_request_body_bytes = 4096"), "test.toml");
        Assert.Equal(4096, cfg.MaxRequestBodyBytes);

        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            TomlConfig.Replace("auto_switch = true", "auto_switch = true\nmax_request_body_bytes = 10"), "test.toml"));
    }

    [Fact]
    public void EventFeedCap_IsConfigurableAndMustBePositive()
    {
        var cfg = RouterConfigLoader.Load(
            TomlConfig.Replace("auto_switch = true", "auto_switch = true\nmax_event_subscribers = 7"), "test.toml");
        Assert.Equal(7, cfg.MaxEventSubscribers);

        // Zero or negative would refuse every feed, including the router's own dispatcher wake, so it
        // is a config error rather than a way to turn observability off.
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            TomlConfig.Replace("auto_switch = true", "auto_switch = true\nmax_event_subscribers = 0"), "test.toml"));
        Assert.Equal(256, RouterConfigLoader.Load(TomlConfig, "test.toml").MaxEventSubscribers);
    }

    [Fact]
    public void Rejects_Missing_Engine_Kind()
    {
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            """
            [engine.fake]
            endpoint = "http://127.0.0.1:9/v1"
            """, "bad.toml"));
    }

    [Fact]
    public void Rejects_Bad_Endpoint()
    {
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            """
            [engine.fake]
            kind = "openai-compatible"
            endpoint = "not-a-uri"
            """, "bad.toml"));
    }

    [Fact]
    public void Rejects_Serving_Default_Not_In_Ladder()
    {
        var ex = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            """
            [engine.fake]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"

            [engine.fake.catalog.m1]
            default_profile = "huge"

            [[engine.fake.catalog.m1.serving]]
            name = "small"
            max_context = 131072
            """, "bad.toml"));
        Assert.Contains("huge", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_Serving_Missing_MaxContext()
    {
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            """
            [engine.fake]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"

            [[engine.fake.catalog.m1.serving]]
            name = "small"
            """, "bad.toml"));
    }

    [Fact]
    public void Rejects_Unknown_Priority_Class()
    {
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            """
            [default_priority]
            class = "urgent"
            """, "bad.toml"));
    }

    [Fact]
    public void Rejects_No_Engines()
    {
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            "listen = \"http://127.0.0.1:9901\"", "bad.toml"));
    }

    [Fact]
    public void Rejects_NegativeRequestPhaseTimeouts()
    {
        // Both knobs are allowed to be 0 (no ceiling, the historical behaviour); a negative one would
        // turn into an instant cancellation, so it is a typo and not a setting.
        const string Base = """
            listen = "http://127.0.0.1:9901"
            default_model = "m1"
            [engine.e]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"
            """;

        var engineCase = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            Base + "\nresponse_timeout_ms = -1\n", "bad.toml"));
        Assert.Contains("response_timeout_ms", engineCase.Message);

        var schedulerCase = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(
            Base + "\n[scheduler]\nswitch_wait_timeout_ms = -5\n", "bad.toml"));
        Assert.Contains("switch_wait_timeout_ms", schedulerCase.Message);
    }
}
