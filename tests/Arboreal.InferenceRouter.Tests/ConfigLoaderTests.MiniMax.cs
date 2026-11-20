using System.Text;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public partial class ConfigLoaderTests
{
    private const string MiniMaxCanonical = "minimax-m3.1-flash";
    private static readonly string[] MiniMaxSpellings = [MiniMaxCanonical, "minimax", "minimax-maya-xs"];

    private static void AssertMiniMaxSample(EngineDefinition maya)
    {
        var model = maya.Catalog[MiniMaxCanonical];
        Assert.Equal(MiniMaxCanonical, model.Name);
        Assert.Collection(model.Aliases, alias => Assert.Equal("minimax", alias),
            alias => Assert.Equal("minimax-maya-xs", alias));
        Assert.Equal("96k", model.Profile);
        Assert.Null(model.Select);
        Assert.Equal(Hy4SampleTiers, model.ServingProfiles.Select(tier => (tier.Name, tier.MaxContext, tier.MaxSlots)));
    }

    [Fact(Timeout = 40_000)]
    public async Task MiniMaxM31FlashSharedMayaRoutesOnlyWithMeasuredCapacity()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = watchdog.Token;
        var config = ReadMayaSample();
        var maya = config.Engines["glm"];
        AssertMayaSample(maya);
        Assert.Null(maya.ApiKey);
        Assert.True(config.AutoSwitch);
        // Use the shipped Maya definition and isolate the peers from credential or lifecycle use.
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
        Assert.Equal("none", runtime.LifecycleProvider);

        foreach (var spelling in MiniMaxSpellings)
        {
            var canonical = Assert.IsType<string>(runtime.CanonicalModel(spelling));
            Assert.Equal(MiniMaxCanonical, canonical);
            var definition = maya.Catalog[canonical];
            var omitted = new LifecycleTarget(maya.Name, canonical, null, definition);
            var normal = Assert.IsType<ServingProfile>(runtime.GetProfile(canonical, "96k"));
            var fallback = Assert.IsType<ServingProfile>(runtime.GetProfile(canonical, "32k"));
            var minimumNormal = Assert.IsType<ServingProfile>(runtime.GetProfile(canonical, "64k"));
            Assert.Equal("96k", omitted.EffectiveProfile);
            Assert.Equal(omitted.EffectiveProfile, (omitted with { Profile = normal }).EffectiveProfile);
            Assert.Equal("32k", (omitted with { Profile = fallback }).EffectiveProfile);
            Assert.Equal("64k", (omitted with { Profile = minimumNormal }).EffectiveProfile);
        }

        var glmLoaded = await PublishMayaPhaseAsync(runtime, mayaHttp, events, new(GlmCanonical, true, 262_144), router.Start, ct);
        Assert.Equal(0L, glmLoaded.Serving.WindowFor(MiniMaxCanonical));
        await RouteAsync(glmLoaded, "minimax", forwards: false);

        var safe = await PublishMayaPhaseAsync(runtime, mayaHttp, events, new(MiniMaxCanonical, true, 32_768), null, ct);
        AssertLoadedMiniMax(safe, 32_768, "32k");
        await RouteAsync(safe, "minimax-maya-xs", forwards: true);
        await RouteAsync(safe, "minimax", forwards: false, minimum: 65_536);

        var normalWindow = await PublishMayaPhaseAsync(runtime, mayaHttp, events, new(MiniMaxCanonical, true, 98_304), null, ct);
        AssertLoadedMiniMax(normalWindow, 98_304, "96k");
        await RouteAsync(normalWindow, MiniMaxCanonical, forwards: true, minimum: 98_304);
        await RouteAsync(normalWindow, MiniMaxCanonical, forwards: false, minimum: 98_305);

        var unloaded = await PublishMayaPhaseAsync(runtime, mayaHttp, events, new(MiniMaxCanonical, false, 98_304), null, ct);
        Assert.True(unloaded.Healthy);
        Assert.Equal(1, unloaded.Serving.TotalSlots);
        Assert.Empty(unloaded.ModelRoster);
        Assert.Equal(0L, unloaded.Serving.WindowFor(MiniMaxCanonical));
        await RouteAsync(unloaded, "minimax", forwards: false);

        Assert.Equal(2, mayaHttp.Forwards.Count);
        Assert.Equal(0, inert.CompletionCalls);

        async Task RouteAsync(EngineCapability verified, string spelling, bool forwards, long? minimum = null)
        {
            Assert.Same(verified, runtime.Capability);
            Assert.Null(runtime.SwitchingToward);
            var before = mayaHttp.Forwards.ToArray();
            var payload = $$$"""{"model":"{{{spelling}}}","messages":[{"role":"user","content":"ping"}]}""";
            var ticket = await router.EnqueueAsync("maya-minimax-fixture", new InferenceRequest
            {
                Model = spelling, Body = Encoding.UTF8.GetBytes(payload),
                Options = new RouterRequestOptions { RequiredContext = minimum },
            }, ct);
            Assert.Equal(MiniMaxCanonical, ticket.Model);

            if (forwards)
            {
                var result = await ticket.CompletionTask.WaitAsync(ct);
                Assert.Equal("""{"choices":[{"message":{"role":"assistant","content":"pong"}}]}""", Encoding.UTF8.GetString(result));
                Assert.Equal(TicketState.Completed, ticket.State);
                Assert.Equal(maya.Name, ticket.Engine);
                var sent = Assert.Single(mayaHttp.Forwards.Skip(before.Length));
                Assert.Equal(HttpMethod.Post, sent.Method);
                Assert.Equal(new Uri(maya.Endpoint, "/v1/chat/completions"), sent.Uri);
                Assert.Equal("""{"model":"minimax-m3.1-flash","messages":[{"role":"user","content":"ping"}]}"""u8.ToArray(), sent.Body);
            }
            else
            {
                var error = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
                Assert.Equal(TicketState.Failed, ticket.State);
                Assert.Equal("switch_failed", error.Code);
                Assert.EndsWith("(no_lifecycle_owner)", error.Message, StringComparison.Ordinal);
                Assert.Equal("none", runtime.LifecycleProvider);
                Assert.DoesNotContain(events.Replay(0), item => item.Kind == "engine.switch_started");
                Assert.Equal(before, mayaHttp.Forwards.ToArray());
            }

            Assert.Null(runtime.SwitchingToward);
            Assert.Same(verified, runtime.Capability);
        }
    }

    private static void AssertLoadedMiniMax(EngineCapability snapshot, long window, string profile)
    {
        Assert.True(snapshot.Healthy);
        Assert.Equal(1, snapshot.Serving.TotalSlots);
        Assert.Equal(MiniMaxCanonical, Assert.Single(snapshot.ModelRoster));
        Assert.Equal(window, snapshot.Serving.WindowFor(MiniMaxCanonical));
        Assert.Equal(profile, snapshot.Serving.Profile);
    }
}
