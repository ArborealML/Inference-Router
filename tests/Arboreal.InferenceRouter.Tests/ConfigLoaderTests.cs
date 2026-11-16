using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

public class ConfigLoaderTests
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

        [threads.chat-ui]
        priority = { class = "interactive", sub = 10 }
        model_affinity = "m1"

        [engine.fake]
        kind = "openai-compatible"
        endpoint = "http://127.0.0.1:9/v1"
        api_key = "secret"
        loaded_requires_status_loaded = true

        [engine.fake.health_probe]
        path = "/props"
        expect = "total_slots>0"
        interval_ms = 500

        [engine.fake.catalog.m1]
        aliases = ["one"]
        profiles = ["max"]

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
          "scheduler": { "max_queue_per_model": 7, "prefix_affinity_threshold_chars": 256 },
          "threads": {
            "chat-ui": { "priority": { "class": "interactive", "sub": 10 }, "model_affinity": "m1" }
          },
          "engine": {
            "fake": {
              "kind": "openai-compatible",
              "endpoint": "http://127.0.0.1:9/v1",
              "api_key": "secret",
              "loaded_requires_status_loaded": true,
              "health_probe": { "path": "/props", "expect": "total_slots>0", "interval_ms": 500 },
              "catalog": {
                "m1": { "aliases": ["one"], "profiles": ["max"] },
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
        Assert.Equal(256, cfg.Scheduler.PrefixAffinityThresholdChars);

        var e = cfg.Engines["fake"];
        Assert.Equal(EngineKind.OpenAiCompatible, e.Kind);
        Assert.Equal("http://127.0.0.1:9/v1", e.Endpoint.ToString());
        Assert.Equal("secret", e.ApiKey);
        Assert.True(e.LoadedRequiresStatusLoaded);
        Assert.Equal("/props", e.Health!.Path);
        Assert.Equal("total_slots>0", e.Health.Expect);
        Assert.Equal(500, e.Health.IntervalMs);

        var m1 = e.Catalog["m1"];
        Assert.Equal(["one"], m1.Aliases);
        Assert.Equal(["max"], m1.Profiles);
        Assert.Null(m1.Select);

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
        Assert.Equal(toml.Threads["chat-ui"].Priority, json.Threads["chat-ui"].Priority);
        var te = toml.Engines["fake"];
        var je = json.Engines["fake"];
        Assert.Equal(te.Endpoint, je.Endpoint);
        Assert.Equal(te.ApiKey, je.ApiKey);
        Assert.Equal(te.LoadedRequiresStatusLoaded, je.LoadedRequiresStatusLoaded);
        Assert.Equal(te.Health!.Path, je.Health!.Path);
        Assert.Equal(te.Catalog.Keys, je.Catalog.Keys);
        Assert.Equal(te.Capabilities.MaxContext, je.Capabilities.MaxContext);
        Assert.Equal(te.Capabilities.Vision, je.Capabilities.Vision);
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
}
