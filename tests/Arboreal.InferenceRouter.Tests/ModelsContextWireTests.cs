using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed class ModelsContextWireTests
{
    private static readonly string[] EnvelopeMembers = ["object", "data"];
    private static readonly string[] StandardMembers = ["id", "object", "created", "owned_by"];
    private static readonly string[] EnrichedMembers = ["id", "object", "created", "owned_by", "context_window"];
    private static readonly string[] ListedIds = ["shared", "distinct", "unknown", "shared"];
    private static readonly string[] ListedEngines = ["first", "first", "first", "second"];
    private static readonly string[] MixedEvidenceIds =
    [
        "duplicate", "DUPLICATE", "duplicate", "backend-alias", "route-alias", "unloaded",
        "empty", "text", "object", "fraction", "exponent", "overflow", "zero", "negative", "missing", "valid",
    ];

    [Fact(Timeout = 60_000)]
    public async Task OrdinaryGetReportsEngineSpecificWindowsAndPreservesTheOpenAiModelList()
    {
        using var first = new ModelsHandler("""
            {"data":[
              {"id":"shared","status":{"value":"loaded"},"meta":{"n_ctx":32768}},
              {"id":"distinct","meta":{"n_ctx":9223372036854775807}},
              {"id":"unknown"}
            ]}
            """);
        using var second = new ModelsHandler("""{"data":[{"id":"shared","meta":{"n_ctx":131072}}]}""");
        var (app, address) = await StartAsync(first, second);
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = address };

        foreach (var query in new[]
        {
            "", "?include_context_window=true", "?include_context_window=false",
            "?include_context_window=true&include_context_window=false", "?unrelated=1",
        })
        {
            using var response = await http.GetAsync("/v1/models" + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            Assert.Equal(EnvelopeMembers, root.EnumerateObject().Select(p => p.Name));
            Assert.Equal("list", root.GetProperty("object").GetString());

            var rows = root.GetProperty("data").EnumerateArray().ToArray();
            Assert.Equal(ListedIds, rows.Select(row => row.GetProperty("id").GetString()));
            Assert.Equal(ListedEngines, rows.Select(row => row.GetProperty("owned_by").GetString()));
            Assert.Collection(rows,
                row => AssertRow(row, 32_768),
                row => AssertRow(row, long.MaxValue),
                row => AssertRow(row, null),
                row => AssertRow(row, 131_072));

            var legacy = JsonSerializer.Deserialize(bytes, ModelListClientJsonContext.Default.OpenAiList);
            Assert.NotNull(legacy);
            Assert.Equal("list", legacy.Object);
            Assert.Equal(ListedIds, legacy.Data.Select(m => m.Id));
            Assert.Equal(ListedEngines, legacy.Data.Select(m => m.OwnedBy));
            Assert.All(legacy.Data, model =>
            {
                Assert.Equal("model", model.Object);
                Assert.True(model.Created > 0);
            });

            var enriched = JsonSerializer.Deserialize(bytes, ModelListClientJsonContext.Default.ContextList);
            Assert.NotNull(enriched);
            Assert.Equal(new long?[] { 32_768, long.MaxValue, null, 131_072 },
                enriched.Data.Select(m => m.ContextWindow));
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FilteredCaseEquivalentRowsWithdrawOnlyTheConflictingWindow(bool alias, bool filteredFirst)
    {
        const string loaded = """{"id":"m","status":{"value":"loaded"},"meta":{"n_ctx":32768}}""";
        const string unrelated = """{"id":"valid","status":{"value":"loaded"},"meta":{"n_ctx":49152}}""";
        var filtered = alias
            ? """{"id":"M","status":{"value":"loaded"},"alias_of":"valid","meta":{"n_ctx":131072}}"""
            : """{"id":"M","status":{"value":"unloaded"},"meta":{"n_ctx":131072}}""";
        var models = filteredFirst ? $"{filtered},{loaded},{unrelated}" : $"{loaded},{filtered},{unrelated}";
        using var engine = new ModelsHandler("{\"data\":[" + models + "]}");
        var (app, address) = await StartAsync(new RouterConfig
        {
            Engines = new Dictionary<string, EngineDefinition>
            {
                ["first"] = Engine("first", loadedRequiresStatusLoaded: true),
            },
            HttpHandlerFactory = _ => engine,
        });
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = address };

        using var response = await http.GetAsync("/v1/models");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Collection(doc.RootElement.GetProperty("data").EnumerateArray(),
            row =>
            {
                Assert.Equal("m", row.GetProperty("id").GetString());
                AssertRow(row, null);
            },
            row =>
            {
                Assert.Equal("valid", row.GetProperty("id").GetString());
                AssertRow(row, 49_152);
            });
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalCollisionKeepsRowsAndOmitsWindowsForRemappedRoutes(bool aliasFirst)
    {
        using var canonical = new ModelsHandler("""{"data":[{"id":"collision","meta":{"n_ctx":32768}}]}""");
        using var alias = new ModelsHandler("""{"data":[{"id":"collision","meta":{"n_ctx":131072}}]}""");
        var canonicalEngine = Engine("canonical", additionalModel: DeclaredModel("collision"));
        var aliasEngine = Engine("alias", additionalModel: DeclaredModel("other", "collision"));
        var first = aliasFirst ? aliasEngine : canonicalEngine;
        var second = aliasFirst ? canonicalEngine : aliasEngine;
        var (app, address) = await StartAsync(new RouterConfig
        {
            Engines = new Dictionary<string, EngineDefinition> { [first.Name] = first, [second.Name] = second },
            HttpHandlerFactory = def => def.Name == "canonical" ? canonical : alias,
        });
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = address };

        using var response = await http.GetAsync("/v1/models");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Collection(doc.RootElement.GetProperty("data").EnumerateArray(),
            row => AssertCollisionRow(row, first.Name, aliasFirst ? null : 32_768),
            row => AssertCollisionRow(row, second.Name, null));
    }

    private static void AssertCollisionRow(JsonElement row, string engine, long? window)
    {
        Assert.Equal("collision", row.GetProperty("id").GetString());
        Assert.Equal(engine, row.GetProperty("owned_by").GetString());
        AssertRow(row, window);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("qwen-3.8-flash-next", false)]
    [InlineData("qwen3.8-flash-next-iq3_s", true)]
    public async Task ReportedQwenRowNeedsTheMatchingCatalogKeyForServingCapacity(string catalogKey, bool bound)
    {
        const string reportedModel = "qwen3.8-flash-next-iq3_s";
        const string completionPath = "/v1/chat/completions";
        var endpoint = new Uri("http://127.0.0.1:9/v1");
        var upstreamUri = new Uri(endpoint, completionPath);
        var submittedPayload = """
            {
              "model": "qwen3.8-flash-next-iq3_s",
              "messages": [{"role": "user", "content": "ping  with spaces"}]
            }
            """u8.ToArray();
        // The router rewrites the top-level model and emits compact JSON even when the ID already matches.
        var expectedUpstreamPayload = """
            {"model":"qwen3.8-flash-next-iq3_s","messages":[{"role":"user","content":"ping  with spaces"}]}
            """u8.ToArray();
        var forwarded = new ConcurrentQueue<ForwardedCompletion>();
        using var engine = new ModelsHandler(
            """{"data":[{"id":"qwen3.8-flash-next-iq3_s","status":{"value":"loaded"},"meta":{"n_ctx":196608}}]}""",
            servingState: """{"n_ctx":256000,"total_slots":4}""",
            completion: async (request, ct) =>
            {
                if (request.Content is not { } content) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                var seen = new ForwardedCompletion(request.Method, request.RequestUri!,
                    content.Headers.ContentType?.ToString(), await content.ReadAsByteArrayAsync(ct));
                forwarded.Enqueue(seen);
                if (seen.Method != HttpMethod.Post || seen.Uri != upstreamUri ||
                    seen.ContentType != "application/json" || !seen.Body.AsSpan().SequenceEqual(expectedUpstreamPayload))
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"choices":[{"message":{"role":"assistant","content":"pong"}}]}""",
                        Encoding.UTF8, "application/json"),
                };
            });
        var definition = new EngineDefinition
        {
            Name = "first",
            Endpoint = endpoint,
            Remote = true,
            Health = new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 },
            LoadedRequiresStatusLoaded = true,
            ModelsContextWindowPath = "meta.n_ctx",
            Capabilities = new EngineCapabilities { MaxContext = 131_072 },
            Catalog = new Dictionary<string, EngineModelDefinition> { [catalogKey] = new() { Name = catalogKey } },
        };
        var (app, address) = await StartAsync(new RouterConfig
        {
            Engines = new Dictionary<string, EngineDefinition> { [definition.Name] = definition },
            HttpHandlerFactory = _ => engine,
        });
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = address };
        var runtime = app.Services.GetRequiredService<RouterCore>().Engines[definition.Name];
        var observed = runtime.Capability;
        Assert.True(observed.Healthy);
        Assert.Equal(4, observed.Serving.TotalSlots);
        Assert.Equal(bound ? 256_000 : 0, observed.Serving.WindowFor(reportedModel));
        Assert.Equal(bound, runtime.IsReadyFor(reportedModel, new ServingProfile { Name = "wide", MaxContext = 256_000 }));

        using var list = await http.GetAsync("/v1/models");
        list.EnsureSuccessStatusCode();
        using var models = JsonDocument.Parse(await list.Content.ReadAsByteArrayAsync());
        var row = Assert.Single(models.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(reportedModel, row.GetProperty("id").GetString());
        AssertRow(row, 196_608);

        using var request = new ByteArrayContent(submittedPayload);
        request.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var completed = await http.PostAsync(completionPath, request);
        var sent = Assert.Single(forwarded);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(upstreamUri, sent.Uri);
        Assert.Equal("application/json", sent.ContentType);
        using var forwardedJson = JsonDocument.Parse(sent.Body);
        Assert.Equal(reportedModel, forwardedJson.RootElement.GetProperty("model").GetString());
        Assert.Equal(expectedUpstreamPayload, sent.Body);
        completed.EnsureSuccessStatusCode();
        Assert.Contains("pong", await completed.Content.ReadAsStringAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task AmbiguousOrInvalidEvidenceNeverChangesTheRosterOrBorrowsTheGlobalGauge()
    {
        using var engine = new ModelsHandler("""
            {"data":[
              {"id":"duplicate","meta":{"n_ctx":32768}},
              {"id":"DUPLICATE","meta":{"n_ctx":131072}},
              {"id":"duplicate","meta":{"n_ctx":65536}},
              {"id":"backend-alias","alias_of":"real","meta":{"n_ctx":32768}},
              {"id":"route-alias","meta":{"n_ctx":32768}},
              {"id":"unloaded","status":{"value":"unloaded"},"meta":{"n_ctx":32768}},
              {"id":"empty","meta":{"n_ctx":null}},
              {"id":"text","meta":{"n_ctx":"32768"}},
              {"id":"object","meta":{"n_ctx":{}}},
              {"id":"fraction","meta":{"n_ctx":32768.5}},
              {"id":"exponent","meta":{"n_ctx":32768e0}},
              {"id":"overflow","meta":{"n_ctx":9223372036854775808}},
              {"id":"zero","meta":{"n_ctx":0}},
              {"id":"negative","meta":{"n_ctx":-1}},
              {"id":"missing"},
              {"id":"valid","status":{"value":"loaded"},"meta":{"n_ctx":49152}}
            ]}
            """, servingState: """{"n_ctx":65536,"total_slots":4}""");
        var (app, address) = await StartAsync(engine);
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = address };
        var runtime = app.Services.GetRequiredService<RouterCore>().Engines["first"];
        Assert.Equal(4, runtime.ActiveSlots);
        Assert.Equal(0, runtime.ActiveContextWindow);

        using var response = await http.GetAsync("/v1/models");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var rows = doc.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(MixedEvidenceIds, rows.Select(row => row.GetProperty("id").GetString()));
        Assert.All(rows[..^1], row => AssertRow(row, null));
        AssertRow(rows[^1], 49_152);
        var client = await response.Content.ReadFromJsonAsync(ModelListClientJsonContext.Default.ContextList);
        Assert.NotNull(client);
        Assert.All(client.Data[..^1], row => Assert.Null(row.ContextWindow));
        Assert.Equal(49_152, client.Data[^1].ContextWindow);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoveryUsesTheConfiguredRowPathAndOmitsWindowsWhenItIsUnset(bool configured)
    {
        const string toml = """
            [engine.first]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:9/v1"
            remote = true
            """;
        var text = configured ? toml + "\nmodels_context_window_path = \"runtime.context.tokens\"" : toml;
        using var engine = new ModelsHandler("""
            {"data":[{"id":"resident","meta":{"n_ctx":131072},"runtime":{"context":{"tokens":32768}}}]}
            """);
        var config = RouterConfigLoader.Load(text, "models-context.toml");
        config.HttpHandlerFactory = _ => engine;
        var (app, address) = await StartAsync(config);
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = address };

        var row = await GetFirstRowAsync(http);
        Assert.Equal("resident", row.GetProperty("id").GetString());
        AssertRow(row, configured ? 32_768 : null);
    }

    [Fact(Timeout = 60_000)]
    public async Task FailedObservationWithdrawsContextButKeepsTheHistoricalRowUntilRecovery()
    {
        using var engine = new ModelsHandler("""{"data":[{"id":"resident","meta":{"n_ctx":131072}}]}""");
        var (app, address) = await StartAsync(engine);
        await using var _ = app;
        using var http = new HttpClient { BaseAddress = address };
        var runtime = app.Services.GetRequiredService<RouterCore>().Engines["first"];
        AssertRow(await GetFirstRowAsync(http), 131_072);

        engine.Failed = true;
        await WaitForAsync(() => !runtime.Capability.Healthy);
        var historical = await GetFirstRowAsync(http);
        Assert.Equal("resident", historical.GetProperty("id").GetString());
        AssertRow(historical, null);

        engine.Models = """{"data":[{"id":"resident","meta":{"n_ctx":32768}}]}""";
        engine.Failed = false;
        await WaitForAsync(() => runtime.Capability.Healthy);
        AssertRow(await GetFirstRowAsync(http), 32_768);
    }

    private static async Task<JsonElement> GetFirstRowAsync(HttpClient http)
    {
        using var response = await http.GetAsync("/v1/models");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return doc.RootElement.GetProperty("data")[0].Clone();
    }

    private static void AssertRow(JsonElement row, long? window)
    {
        Assert.Equal("model", row.GetProperty("object").GetString());
        Assert.True(row.GetProperty("created").GetInt64() > 0);
        Assert.Equal(window is null ? StandardMembers : EnrichedMembers, row.EnumerateObject().Select(p => p.Name));
        if (window is { } tokens)
            Assert.Equal(tokens, row.GetProperty("context_window").GetInt64());
    }

    private static Task<(WebApplication App, Uri Address)> StartAsync(
        ModelsHandler first, ModelsHandler? second = null)
    {
        var engines = new Dictionary<string, EngineDefinition> { ["first"] = Engine("first") };
        if (second is not null) engines.Add("second", Engine("second"));
        return StartAsync(new RouterConfig
        {
            Engines = engines,
            HttpHandlerFactory = def => def.Name == "first" ? first : second!,
        });
    }

    private static async Task<(WebApplication App, Uri Address)> StartAsync(RouterConfig cfg)
    {
        var app = ServerApp.Build(["--urls", "http://127.0.0.1:0"], configOverride: cfg);
        await app.StartAsync();
        var router = app.Services.GetRequiredService<RouterCore>();
        await WaitForAsync(() => router.Engines.Values.All(e => e.Capability.Healthy));
        return (app, new Uri(app.Urls.Single()));
    }

    private static EngineDefinition Engine(string name, bool loadedRequiresStatusLoaded = false,
        EngineModelDefinition? additionalModel = null)
    {
        var catalog = new Dictionary<string, EngineModelDefinition>
        {
            ["shared"] = DeclaredModel("shared"),
            ["unknown"] = DeclaredModel("unknown"),
            ["catalog-only"] = DeclaredModel("catalog-only", "route-alias"),
        };
        if (additionalModel is not null) catalog.Add(additionalModel.Name, additionalModel);
        return new EngineDefinition
        {
            Name = name,
            Endpoint = new Uri("http://127.0.0.1:9/v1"),
            Remote = true,
            Health = new HealthProbeDefinition { Path = "/props", IntervalMs = 20 },
            LoadedRequiresStatusLoaded = loadedRequiresStatusLoaded,
            ModelsContextWindowPath = "meta.n_ctx",
            Capabilities = new EngineCapabilities { MaxContext = 524_288 },
            Catalog = catalog,
        };
    }

    private static EngineModelDefinition DeclaredModel(string id, params string[] aliases) => new()
    {
        Name = id,
        Aliases = aliases,
        Profile = "declared",
        ServingProfiles = [new ServingProfile { Name = "declared", MaxContext = 262_144 }],
    };

    private static async Task WaitForAsync(Func<bool> observed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!observed()) await Task.Delay(10, timeout.Token);
    }

    private sealed record ForwardedCompletion(HttpMethod Method, Uri Uri, string? ContentType, byte[] Body);

    private sealed class ModelsHandler(string models, string servingState = """{"n_ctx":65536}""",
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? completion = null) : HttpMessageHandler
    {
        private string _models = models;
        private bool _failed;
        public string Models
        {
            set => Volatile.Write(ref _models, value);
        }
        public bool Failed
        {
            set => Volatile.Write(ref _failed, value);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/chat/completions" && completion is not null)
                return completion(request, cancellationToken);
            if (request.Method != HttpMethod.Get || path is not ("/models" or "/props"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var isModels = path == "/models";
            var status = isModels && Volatile.Read(ref _failed) ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            var body = isModels ? Volatile.Read(ref _models) : servingState;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(OpenAiList))]
[JsonSerializable(typeof(ContextList))]
internal sealed partial class ModelListClientJsonContext : JsonSerializerContext;

internal sealed record OpenAiList(string Object, OpenAiRow[] Data);
internal sealed record OpenAiRow(string Id, string Object, long Created, string OwnedBy);
internal sealed record ContextList(ContextRow[] Data);
internal sealed record ContextRow(string Id, long? ContextWindow);
