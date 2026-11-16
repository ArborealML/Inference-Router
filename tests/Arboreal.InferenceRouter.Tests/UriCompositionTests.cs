using System.Text;
using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>
/// TASK-10 AC #1: how a configured engine endpoint composes with every path the router requests
/// (completion suffix, models probe, health probe, query strings, trailing slashes). The shipped
/// samples mount engines under `/v1`, so a composition that drops the base path 404s every
/// completion against a real engine while the path-insensitive in-process fake stays green — these
/// tests pin the composition itself.
/// </summary>
public class UriCompositionTests
{
    [Theory]
    // completion suffixes are relative to the endpoint path
    [InlineData("http://127.0.0.1:18021/v1", "chat/completions", "http://127.0.0.1:18021/v1/chat/completions")]
    [InlineData("http://127.0.0.1:18021/v1/", "chat/completions", "http://127.0.0.1:18021/v1/chat/completions")]
    [InlineData("http://127.0.0.1:18021", "chat/completions", "http://127.0.0.1:18021/chat/completions")]
    [InlineData("https://gw.internal:8443/gateway/v1", "chat/completions", "https://gw.internal:8443/gateway/v1/chat/completions")]
    [InlineData("http://127.0.0.1:18021/v1", "completions", "http://127.0.0.1:18021/v1/completions")]
    // a leading '/' is absolute from the server root, which is how llama.cpp serves /models, /props
    [InlineData("http://127.0.0.1:18021/v1", "/models", "http://127.0.0.1:18021/models")]
    [InlineData("http://127.0.0.1:18021/v1", "/v1/models", "http://127.0.0.1:18021/v1/models")]
    [InlineData("http://127.0.0.1:18021/v1", "models", "http://127.0.0.1:18021/v1/models")]
    [InlineData("http://127.0.0.1:18021/v1/", "health", "http://127.0.0.1:18021/v1/health")]
    // query strings: the value wins, otherwise the endpoint's own query survives
    [InlineData("http://127.0.0.1:18021/v1", "/health?verbose=1", "http://127.0.0.1:18021/health?verbose=1")]
    [InlineData("http://127.0.0.1:18021/v1", "props?format=json", "http://127.0.0.1:18021/v1/props?format=json")]
    [InlineData("http://127.0.0.1:18021/v1?key=abc", "/health", "http://127.0.0.1:18021/health?key=abc")]
    [InlineData("http://127.0.0.1:18021/v1?key=abc", "health", "http://127.0.0.1:18021/v1/health?key=abc")]
    [InlineData("http://127.0.0.1:18021/v1?key=abc", "/health?x=1", "http://127.0.0.1:18021/health?x=1")]
    public void Path_composes_against_the_endpoint(string endpoint, string path, string expected)
        => Assert.Equal(expected, EngineRuntime.Combine(new Uri(endpoint), path).ToString());

    [Fact]
    public async Task Completion_calls_keep_the_endpoint_path_end_to_end()
    {
        using var fake = new FakeEngineHandler();
        var cfg = new RouterConfig
        {
            DefaultModel = "test-model",
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 4, MaxQueuePerModel = 4 },
            HttpHandlerFactory = _ => fake,
        };
        cfg.Engines = new Dictionary<string, EngineDefinition>
        {
            ["fake"] = new EngineDefinition
            {
                Name = "fake",
                Kind = EngineKind.OpenAiCompatible,
                Endpoint = new Uri("http://127.0.0.1:9/v1"),
                Catalog = new Dictionary<string, EngineModelDefinition>
                {
                    ["test-model"] = new EngineModelDefinition { Name = "test-model" },
                },
            },
        };

        await using var router = new RouterCore(cfg, events: new EventRing());
        router.Start();

        var chat = await router.EnqueueAsync("t-chat", Body(
            """{"model":"test-model","messages":[{"role":"user","content":"chat-path"}]}"""));
        _ = await chat.CompletionTask;

        var legacy = await router.EnqueueAsync("t-legacy", Body(
            """{"model":"test-model","prompt":"completion-path"}"""));
        _ = await legacy.CompletionTask;

        Assert.Contains("/v1/chat/completions", fake.SeenRoutes);
        Assert.Contains("/v1/completions", fake.SeenRoutes);
        // the default models probe is root-absolute: llama.cpp serves /models, not /v1-only
        Assert.Contains("/models", fake.SeenRoutes);
        Assert.DoesNotContain("/chat/completions", fake.SeenRoutes);
    }

    private static InferenceRequest Body(string json) => new()
    {
        Model = "test-model",
        Stream = false,
        Body = Encoding.UTF8.GetBytes(json),
        Options = new RouterRequestOptions(),
    };
}
