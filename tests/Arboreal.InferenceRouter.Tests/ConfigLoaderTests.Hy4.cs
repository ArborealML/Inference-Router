using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Tests;

public partial class ConfigLoaderTests
{
    internal const string Hy4Canonical = "hy4-flash";
    private const string GlmCanonical = "glm-5.3-flash";
    private static readonly (string Tier, long Window, int Slots)[] Hy4SampleTiers =
    [
        ("32k", 32_768, 1),
        ("64k", 65_536, 1),
        ("96k", 98_304, 1),
        ("128k", 131_072, 1),
        ("256k", 262_144, 1),
        ("512k", 524_288, 1),
        ("1024k", 1_048_576, 1),
    ];

    private static void AssertMayaSample(EngineDefinition maya)
    {
        Assert.Equal<EngineLifecycleOwner?>(EngineLifecycleOwner.External, maya.LifecycleOwner);
        Assert.True(maya.Remote);
        Assert.Null(maya.ApiKeyFile);
        Assert.Null(maya.Docker);
        Assert.Equal(new Uri("http://127.0.0.1:18060/v1"), maya.Endpoint);
        Assert.Equal("/v1/models", maya.ModelsEndpoint);
        Assert.True(maya.LoadedRequiresStatusLoaded);
        var probe = Assert.IsType<HealthProbeDefinition>(maya.Health);
        Assert.Equal("/props", probe.Path);
        Assert.Equal("total_slots>0", probe.Expect);
        Assert.Equal("default_generation_settings.n_ctx", probe.ContextPath);
        Assert.Equal("total_slots", probe.SlotsPath);
        Assert.Equal(3, maya.Catalog.Count);
        Assert.Equal("glm-flash", Assert.Single(maya.Catalog[GlmCanonical].Aliases));

        var model = maya.Catalog[Hy4Canonical];
        Assert.Collection(model.Aliases, alias => Assert.Equal("hy4", alias),
            alias => Assert.Equal("hy4-flash-maya-xs", alias));
        Assert.Equal("96k", model.Profile);
        Assert.Null(model.Select);
        Assert.Null(maya.Catalog[GlmCanonical].Select);
        Assert.Equal(Hy4SampleTiers, model.ServingProfiles.Select(tier => (tier.Name, tier.MaxContext, tier.MaxSlots)));
        AssertMiniMaxSample(maya);
    }

    private static RouterConfig ReadMayaSample()
    {
        var path = RepoFile("samples/router.toml");
        Assert.NotNull(path);
        return RouterConfigLoader.LoadFile(path!);
    }

    internal static EngineDefinition ReadMayaEngineForHy4() => ReadMayaSample().Engines["glm"];

    [Fact(Timeout = 40_000)]
    public async Task Hy4FlashSharedMayaRoutesOnlyTheReportedResident()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = watchdog.Token;
        var config = ReadMayaSample();
        var maya = config.Engines["glm"];
        AssertMayaSample(maya);
        Assert.True(config.AutoSwitch);
        // Keep the actual Maya definition. No peer in this fixture can read a key or run a selector.
        config.Engines = config.Engines.ToDictionary(entry => entry.Key, entry =>
            entry.Key == maya.Name ? maya : new EngineDefinition
            {
                Name = entry.Key, Endpoint = entry.Value.Endpoint, LifecycleOwner = EngineLifecycleOwner.External,
            }, StringComparer.Ordinal);
        using var mayaHttp = new SharedMayaHandler(maya.Endpoint);
        using var inert = new FakeEngineHandler { LoadedModels = [] };
        config.HttpHandlerFactory = engine => engine.Name == maya.Name ? mayaHttp : inert;
        var events = new EventRing();
        await using var router = new RouterCore(config, events: events);
        var runtime = router.Engines[maya.Name];
        Assert.Same(maya, runtime.Definition);
        Assert.Equal(Hy4Canonical, runtime.CanonicalModel(Hy4Canonical));
        Assert.Equal(Hy4Canonical, runtime.CanonicalModel("hy4"));
        Assert.Equal(Hy4Canonical, runtime.CanonicalModel("hy4-flash-maya-xs"));

        var glmLoaded = await PublishPhaseAsync(new(GlmCanonical, true, 262_144), start: true);
        await RouteAsync(glmLoaded, "glm-flash", GlmCanonical, forwards: true);
        await RouteAsync(glmLoaded, "hy4", Hy4Canonical, forwards: false);

        var hy4Loaded = await PublishPhaseAsync(new(Hy4Canonical, true, 32_768));
        await RouteAsync(hy4Loaded, "hy4-flash-maya-xs", Hy4Canonical, forwards: true);
        await RouteAsync(hy4Loaded, "glm-flash", GlmCanonical, forwards: false);
        await RouteAsync(hy4Loaded, "hy4", Hy4Canonical, forwards: false, minimum: 65_536);

        var unloaded = await PublishPhaseAsync(new(Hy4Canonical, false, 32_768));
        await RouteAsync(unloaded, "hy4", Hy4Canonical, forwards: false);
        Assert.Equal(2, mayaHttp.Forwards.Count);
        Assert.Equal(0, inert.CompletionCalls);

        Task<EngineCapability> PublishPhaseAsync(MayaPhase phase, bool start = false) =>
            PublishMayaPhaseAsync(runtime, mayaHttp, events, phase, start ? router.Start : null, ct);

        async Task RouteAsync(EngineCapability verified, string spelling, string canonical, bool forwards,
            long? minimum = null)
        {
            // Equivalent polls preserve the current capability. Tickets in the same phase reuse it.
            Assert.Same(verified, runtime.Capability);
            var before = mayaHttp.Forwards.ToArray();
            var payload = $$$"""{"model":"{{{spelling}}}","messages":[{"role":"user","content":"ping"}]}""";
            var ticket = await router.EnqueueAsync("maya-hy4-fixture", new InferenceRequest
            {
                Model = spelling, Body = Encoding.UTF8.GetBytes(payload),
                Options = new RouterRequestOptions { RequiredContext = minimum },
            }, ct);
            Assert.Equal(canonical, ticket.Model);
            if (forwards)
            {
                var result = await ticket.CompletionTask.WaitAsync(ct);
                Assert.Equal("""{"choices":[{"message":{"role":"assistant","content":"pong"}}]}""", Encoding.UTF8.GetString(result));
                Assert.Equal(maya.Name, ticket.Engine);
                var sent = Assert.Single(mayaHttp.Forwards.Skip(before.Length));
                Assert.Equal(HttpMethod.Post, sent.Method);
                Assert.Equal(new Uri(maya.Endpoint, "/v1/chat/completions"), sent.Uri);
                var expectedBody = $$$"""{"model":"{{{canonical}}}","messages":[{"role":"user","content":"ping"}]}""";
                Assert.Equal(Encoding.UTF8.GetBytes(expectedBody), sent.Body);
            }
            else
            {
                var error = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
                Assert.Equal("switch_failed", error.Code);
                Assert.EndsWith("(no_lifecycle_owner)", error.Message, StringComparison.Ordinal);
                Assert.Equal(before, mayaHttp.Forwards.ToArray());
            }
            Assert.Same(verified, runtime.Capability);
        }
    }

    private static async Task<EngineCapability> PublishMayaPhaseAsync(EngineRuntime runtime, SharedMayaHandler mayaHttp,
        EventRing events, MayaPhase phase, Action? start, CancellationToken ct)
    {
        var previousVersion = runtime.Capability.Version;
        var previousPair = mayaHttp.LastPair?.Number ?? 0;
        var published = new TaskCompletionSource<(EngineCapability Capability, MayaProbePair Pair)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(RouterEvent item)
        {
            if (item.Kind != "engine.health" || item.Engine != runtime.Name ||
                mayaHttp.LastPair is not { } pair || !ReferenceEquals(phase, pair.Phase) || pair.Number <= previousPair)
                return;

            var observed = runtime.Capability;
            if (observed.Version > previousVersion && MatchesMayaPhase(observed, phase))
                published.TrySetResult((observed, pair));
        }

        events.Subscribe(OnEvent);
        try
        {
            mayaHttp.Select(phase);
            start?.Invoke();
            var (snapshot, pair) = await published.Task.WaitAsync(ct);
            AssertMayaReadback(runtime.Definition, pair, phase);
            Assert.True(snapshot.Version > previousVersion);
            Assert.True(MatchesMayaPhase(snapshot, phase));
            return snapshot;
        }
        finally { events.Unsubscribe(OnEvent); }
    }

    internal static async Task CheckHy4SampleProbeAsync(EngineDefinition maya, string tier, long expectedWindow, CancellationToken ct)
    {
        Assert.Null(maya.ApiKeyFile);
        using var handler = new SharedMayaHandler(maya.Endpoint);
        await using var runtime = new EngineRuntime(maya, new RouterConfig(), new EventRing(), handler);
        var selected = Assert.IsType<ServingProfile>(runtime.GetProfile(Hy4Canonical, tier));
        Assert.Equal(expectedWindow, selected.MaxContext);
        Assert.Equal(1, selected.MaxSlots);
        var positive = new MayaPhase(Hy4Canonical, true, expectedWindow);
        handler.Select(positive);
        var version = runtime.Capability.Version;
        Assert.True(await runtime.ProbeHealthOnceAsync(ct));
        var snapshot = runtime.Capability;
        Assert.True(snapshot.Version > version);
        AssertMayaReadback(maya, Assert.IsType<MayaProbePair>(handler.LastPair), positive);
        Assert.True(MatchesMayaPhase(snapshot, positive));
        Assert.Equal(tier, snapshot.Serving.Profile);
        Assert.True(runtime.IsReadyFor(Hy4Canonical, selected));

        if (tier is "96k" or "1024k")
        {
            var underfloor = new MayaPhase(Hy4Canonical, true, tier == "96k" ? 32_768 : 1_048_575);
            handler.Select(underfloor);
            Assert.True(await runtime.ProbeHealthOnceAsync(ct));
            var narrow = runtime.Capability;
            Assert.True(narrow.Version > snapshot.Version);
            AssertMayaReadback(maya, Assert.IsType<MayaProbePair>(handler.LastPair), underfloor);
            Assert.True(MatchesMayaPhase(narrow, underfloor));
            Assert.Equal(tier == "96k" ? "32k" : null, narrow.Serving.Profile);
            Assert.False(runtime.IsReadyFor(Hy4Canonical, selected));
        }
        Assert.Empty(handler.Forwards);
    }

    private static bool MatchesMayaPhase(EngineCapability snapshot, MayaPhase phase) =>
        snapshot.Healthy && snapshot.Serving.TotalSlots == 1 &&
        snapshot.Serving.WindowFor(phase.Model) == (phase.Loaded ? phase.Window : 0) &&
        (phase.Loaded ? snapshot.ModelRoster.Count == 1 && snapshot.ModelRoster[0] == phase.Model : snapshot.ModelRoster.Count == 0);

    private static void AssertMayaReadback(EngineDefinition maya, MayaProbePair pair, MayaPhase phase)
    {
        Assert.Same(phase, pair.Phase);
        Assert.Equal(new Uri(maya.Endpoint, "/v1/models"), pair.Discovery);
        Assert.Equal(new Uri(maya.Endpoint, "/props"), pair.Props);
        using var models = JsonDocument.Parse(pair.DiscoveryBody);
        var row = Assert.Single(models.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(phase.Model, row.GetProperty("id").GetString());
        Assert.Equal(phase.Loaded ? "loaded" : "unloaded", row.GetProperty("status").GetProperty("value").GetString());
        using var props = JsonDocument.Parse(pair.PropsBody);
        Assert.Equal(phase.Window, props.RootElement.GetProperty("default_generation_settings").GetProperty("n_ctx").GetInt64());
        Assert.Equal(1, props.RootElement.GetProperty("total_slots").GetInt32());
    }

    private sealed record MayaPhase(string Model, bool Loaded, long Window);
    private sealed record MayaProbePair(MayaPhase Phase, Uri Discovery, string DiscoveryBody, Uri Props, string PropsBody, int Number);
    private sealed record MayaForward(HttpMethod Method, Uri Uri, byte[] Body);

    private sealed class SharedMayaHandler(Uri endpoint) : HttpMessageHandler
    {
        private MayaPhase? _selected;
        private (MayaPhase Phase, Uri Discovery, string Body)? _discovery;
        private MayaProbePair? _lastPair;
        private int _numbers;

        internal MayaProbePair? LastPair => Volatile.Read(ref _lastPair);
        internal ConcurrentQueue<MayaForward> Forwards { get; } = new();
        internal void Select(MayaPhase phase) => Volatile.Write(ref _selected, phase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = Assert.IsType<Uri>(request.RequestUri);
            if (request.Method != HttpMethod.Get)
            {
                var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Forwards.Enqueue(new(request.Method, uri, body));
                return Json("""{"choices":[{"message":{"role":"assistant","content":"pong"}}]}""");
            }

            if (uri == new Uri(endpoint, "/v1/models"))
            {
                Assert.Null(_discovery);
                var phase = Assert.IsType<MayaPhase>(Volatile.Read(ref _selected));
                var status = phase.Loaded ? "loaded" : "unloaded";
                var body = $$$"""{"data":[{"id":"{{{phase.Model}}}","status":{"value":"{{{status}}}"}}]}""";
                _discovery = (phase, uri, body);
                return Json(body);
            }

            Assert.Equal(new Uri(endpoint, "/props"), uri);
            var (paired, modelsUri, modelsBody) = _discovery ?? throw new InvalidOperationException("No matching models GET preceded props.");
            _discovery = null;
            var propsBody = FormattableString.Invariant(
                $$$"""{"default_generation_settings":{"n_ctx":{{{paired.Window}}} },"total_slots":1}""");
            Volatile.Write(ref _lastPair, new(paired, modelsUri, modelsBody, uri, propsBody, ++_numbers));
            return Json(propsBody);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
