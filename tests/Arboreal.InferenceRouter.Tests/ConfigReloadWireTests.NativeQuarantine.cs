using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Arboreal.InferenceRouter.Lifecycle;
using Arboreal.InferenceRouter.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class ConfigReloadWireTests
{
    private const string TargetResetPath = "/admin/config/target-reset";
    private const string ExactSmallReset = """{"engine":"native","model":"candidate","profile":"small"}""";
    private const string QuarantineDefinition = """

        auto_switch = true
        switch_cooldown_ms = 1
        [scheduler]
        max_inflight_per_engine = 2
        switch_wait_timeout_ms = 20000
        [engine.native]
        kind = "openai-compatible"
        endpoint = "http://127.0.0.1:9/v1"
        lifecycle_owner = "router"
        lifecycle = "docker"
        [engine.native.docker]
        image = "offline-image"
        router_id = "offline-owner"
        [engine.native.health_probe]
        interval_ms = 3600000
        [engine.native.catalog.a]
        [engine.native.catalog.b]
        [engine.native.catalog.candidate]
        aliases = ["candidate-alias"]
        profile = "small"
        serving = [
          { name = "small", max_context = 4096, max_slots = 2 },
          { name = "large", max_context = 8192, max_slots = 2 },
        ]
        """;
    private static readonly TimeSpan QuarantineLimit = TimeSpan.FromSeconds(10);

    [Theory(Timeout = 50_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFailureQuarantineSurvivesRealReloadAndRequiresTheExactOptedInReset(bool enabled)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = bound.Token;
        var path = Path.Combine(_dir.FullName, "quarantine.toml");
        using var upstream = new CapturingEngine { LoadedModels = ["a", "b"], NCtx = 4_096 };
        var provider = new QuarantineProvider(upstream);
        await File.WriteAllTextAsync(path, Initial + QuarantineDefinition, ct);
        await using var app = ServerApp.Build(["--urls", "http://127.0.0.1:0"], path, null, TimeProvider.System,
            _ => provider, _ => upstream, Token, readTargetResetOptInForTests: () => enabled ? "true" : null);
        await app.StartAsync(ct);
        using var http = Client(app);
        var router = app.Services.GetRequiredService<RouterCore>();
        var engine = router.Engines["native"];
        await WaitUntilAsync(() => engine.Healthy, ct);
        var incumbent = engine.Capability;
        var (originalTag, _) = await ReadGenerationAsync(http, ct);

        await FailedTicketAsync(4_096);
        await FailedTicketAsync(8_192);
        Assert.Equal(["small", "large"], provider.Ownership.ToArray());
        Assert.Equal(["small", "large"], provider.Preflight.ToArray());
        Assert.Equal(2, SwitchFailures());
        Assert.Equal(2, SwitchStarts());
        Assert.True(engine.Healthy);
        Assert.Same(incumbent.ModelRoster, engine.Capability.ModelRoster);
        Assert.Equal(incumbent.Serving, engine.Capability.Serving);

        // There is no runtime cooldown timer. Subsequent real requests wake the scheduler after the interval.
        await Task.Delay(TimeSpan.FromMilliseconds(router.Config.SwitchCooldownMs + 1), ct);
        Assert.Equal(SwitchReason.LaunchFailed, (await engine.WaitForModelAsync("candidate-alias", QuarantineLimit, ct)).Detail);
        await StillLatchedAsync();
        if (!enabled)
        {
            using var unavailable = await PostTargetResetAsync(http, originalTag, ExactSmallReset, Token, ct);
            Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
            await StillLatchedAsync();
        }

        await File.WriteAllTextAsync(path, "default_model = \"b\"\n" + QuarantineDefinition, ct);
        using var reload = await ApplyAsync(http, originalTag, ct);
        Assert.Equal(HttpStatusCode.OK, reload.StatusCode);
        Assert.Equal("{\"Generation\":2}", await reload.Content.ReadAsStringAsync(ct));
        var currentTag = reload.Headers.ETag!.Tag;
        Assert.NotEqual(originalTag, currentTag);
        Assert.Same(engine, router.Engines["native"]);
        using (var prompt = new StringContent("""{"prompt":"new default"}""", Encoding.UTF8, "application/json"))
        using (var forwarded = await http.PostAsync("/v1/completions", prompt, ct))
        {
            Assert.Equal(HttpStatusCode.OK, forwarded.StatusCode);
            Assert.Equal("""{"prompt":"new default","model":"b"}"""u8.ToArray(), Assert.Single(upstream.Forwarded));
        }
        await StillLatchedAsync();
        if (!enabled)
        {
            using var unavailable = await PostTargetResetAsync(http, currentTag, ExactSmallReset, Token, ct);
            Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
            await StillLatchedAsync();
            return;
        }

        var foreignTag = $"\"reload-{Guid.NewGuid():N}-2\"";
        using (var unauthorized = await PostTargetResetAsync(http, currentTag, "[]", "wrong-token", ct))
            await AssertErrorAsync(unauthorized, HttpStatusCode.Unauthorized, "reload_unauthorized", ct);
        using (var badTag = await PostTargetResetAsync(http, "bad-tag", "[]", Token, ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badTag.StatusCode);
            Assert.Equal("""{"error":{"message":"Send one current reload ETag in If-Match.","code":"invalid_target_reset","type":"air_error"}}"""u8.ToArray(),
                await badTag.Content.ReadAsByteArrayAsync(ct));
        }
        foreach (var badBody in new[] { "[]", "\"reset\"", "42", "true", "false", "null",
            """{"engine":"native","model":"candidate","profile":4}""" })
        {
            using var malformed = await PostTargetResetAsync(http, currentTag, badBody, Token, ct);
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            Assert.Equal("""{"error":{"message":"Send the exact engine, canonical model and effective profile.","code":"invalid_target_reset","type":"air_error"}}"""u8.ToArray(),
                await malformed.Content.ReadAsByteArrayAsync(ct));
        }
        using (var stale = await PostTargetResetAsync(http, originalTag, ExactSmallReset, Token, ct))
            await AssertErrorAsync(stale, HttpStatusCode.PreconditionFailed, "reload_stale", ct);
        using (var foreign = await PostTargetResetAsync(http, foreignTag, ExactSmallReset, Token, ct))
            await AssertErrorAsync(foreign, HttpStatusCode.PreconditionFailed, "reload_stale", ct);
        var wrongTarget = ExactSmallReset.Replace("small", "unknown", StringComparison.Ordinal);
        using (var wrong = await PostTargetResetAsync(http, currentTag, wrongTarget, Token, ct))
            await AssertErrorAsync(wrong, HttpStatusCode.Conflict, "target_reset_unavailable", ct);
        await StillLatchedAsync();

        var resetWake = Gate();
        router.AfterDispatchParkForTests = _ => resetWake.TrySetResult();
        using var reset = await PostTargetResetAsync(http, currentTag, ExactSmallReset, Token, ct);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        await resetWake.Task.WaitAsync(ct);
        Assert.Equal(2, SwitchStarts());
        Assert.Equal(2, provider.Ownership.Count);
        Assert.Equal(0, provider.StopCalls);
        Assert.Empty(provider.Launches);
        Assert.Equal((currentTag, 2L), await ReadGenerationAsync(http, ct));

        provider.AllowSmall = true;
        using (var fresh = await SendQuarantineTicketAsync(http, 4_096, ct))
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(["small", "large", "small"], provider.Ownership.ToArray());
        Assert.Equal(["small"], provider.Launches.ToArray());
        Assert.Equal("""{"model":"candidate","prompt":"target"}"""u8.ToArray(), upstream.Forwarded.Last());
        await FailedTicketAsync(8_192);
        Assert.Equal(3, SwitchStarts());
        Assert.Equal(2, SwitchFailures());
        Assert.Equal(3, provider.Ownership.Count);

        async Task FailedTicketAsync(long context)
        {
            using var response = await SendQuarantineTicketAsync(http, context, ct);
            await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "switch_failed", ct);
            var ticket = Assert.Single(response.Headers.GetValues("X-Inference-Ticket"));
            Assert.Single(router.Events.Replay(0), entry => entry.TicketId == ticket && entry.Kind == "request.selected");
            Assert.Single(router.Events.Replay(0), entry => entry.TicketId == ticket && entry.Kind == "request.failed");
            await WaitUntilAsync(() => engine.Inflight == 0, ct);
        }

        async Task StillLatchedAsync()
        {
            await FailedTicketAsync(4_096);
            await FailedTicketAsync(8_192);
            Assert.Equal(2, SwitchFailures());
            Assert.Equal(2, SwitchStarts());
            Assert.Equal(2, provider.Ownership.Count);
            Assert.Equal(2, provider.Preflight.Count);
            Assert.Equal(0, provider.StopCalls);
            Assert.Empty(provider.Launches);
        }

        int SwitchStarts() => router.Events.Replay(0).Count(entry => entry.Kind == "engine.switch_started");
        int SwitchFailures() => router.Events.Replay(0).Count(entry => entry.Kind == "engine.switch_failed");
    }

    [Fact(Timeout = 40_000)]
    public async Task NativeFailureQuarantineKeepsAnOldJoinedTicketFailedAfterResetAndNewReady()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var starterCancellation = new CancellationTokenSource();
        using var upstream = new CapturingEngine { LoadedModels = ["a"], NCtx = 4_096 };
        var provider = new QuarantineProvider(upstream);
        var preflight = Gate();
        var releasePreflight = Gate();
        provider.BeforePreflight = async ct =>
        {
            preflight.TrySetResult();
            await releasePreflight.Task.WaitAsync(ct);
        };
        const string startupInstance = "fixture-startup";
        var config = RouterConfigLoader.Load(Initial + QuarantineDefinition);
        config.HttpHandlerFactory = _ => upstream;
        await using var router = new RouterCore(config, new QuarantineSwitchScheduler(), null, TimeProvider.System,
            _ => provider, nativeResetStartupInstance: startupInstance);
        var engine = router.Engines["native"];
        var profile = Assert.IsType<ServingProfile>(engine.GetProfile("candidate", "small"));
        var ct = watchdog.Token;
        Assert.True(await engine.ProbeHealthOnceAsync(ct));

        var joined = Gate();
        var woke = Gate();
        var releaseOld = Gate();
        var normalWakes = 0;
        var forwardClaims = 0;
        engine.AfterForwardAdmissionForTests = () =>
        {
            Interlocked.Increment(ref forwardClaims);
            return Task.CompletedTask;
        };
        engine.SwitchWaitTimeForTests = deadline =>
        {
            joined.TrySetResult();
            return deadline - QuarantineLimit;
        };
        engine.AfterReadyWaitForTests = fromCancellation =>
        {
            if (fromCancellation || Interlocked.Increment(ref normalWakes) != 1) return;
            woke.TrySetResult();
            releaseOld.Task.WaitAsync(ct).GetAwaiter().GetResult();
        };

        var oldBody = """{"model":"candidate","prompt":"old waiter"}"""u8.ToArray();
        var newBody = """{"model":"candidate","prompt":"fresh waiter"}"""u8.ToArray();
        var starter = engine.WaitForProfileAsync("candidate", profile, Timeout.InfiniteTimeSpan, starterCancellation.Token);
        try
        {
            await preflight.Task.WaitAsync(ct);
            var old = await EnqueueAsync("old", oldBody);
            Assert.True(router.TryDispatchOnce());
            await joined.Task.WaitAsync(ct);
            await starterCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter.WaitAsync(ct));
            releasePreflight.TrySetResult();
            await woke.Task.WaitAsync(ct);

            Assert.False(old.CompletionTask.IsCompleted);
            Assert.Null(engine.SwitchingToward);
            Assert.Single(router.Events.Replay(0), entry => entry.Kind == "engine.switch_failed");
            Assert.Equal(RouterCore.NativeTargetResetStatus.Reset,
                router.ResetNativeTarget(startupInstance, 1, "native", "candidate", "small", ct));
            provider.AllowSmall = true;
            var fresh = await EnqueueAsync("new", newBody);
            Assert.True(router.TryDispatchOnce());
            await fresh.CompletionTask.WaitAsync(ct);
            Assert.Equal(newBody, Assert.Single(upstream.Forwarded));
            var proved = engine.Capability;
            Assert.True(proved.Healthy);
            Assert.False(old.CompletionTask.IsCompleted);

            releaseOld.TrySetResult();
            var failure = await Assert.ThrowsAsync<RouterRequestException>(() => old.CompletionTask.WaitAsync(ct));
            Assert.Equal("switch_failed", failure.Code);
            Assert.Contains("(launch_failed)", failure.Message, StringComparison.Ordinal);
            Assert.Single(router.Events.Replay(0), entry => entry.Kind == "request.failed" && entry.TicketId == old.Id);
            Assert.Equal(newBody, Assert.Single(upstream.Forwarded));
            Assert.Equal(1, upstream.CompletionCalls);
            Assert.Equal(1, Volatile.Read(ref forwardClaims));
            Assert.Same(proved, engine.Capability);
        }
        finally
        {
            releasePreflight.TrySetResult();
            releaseOld.TrySetResult();
        }

        ValueTask<RouterTicket> EnqueueAsync(string thread, byte[] body) => router.EnqueueAsync(thread,
            new InferenceRequest { Model = "candidate", Body = body, Options = new() { RequiredContext = 4_096 } }, ct);
    }

    private static async Task<HttpResponseMessage> PostTargetResetAsync(
        HttpClient client, string tag, string body, string bearer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TargetResetPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.TryAddWithoutValidation("If-Match", tag);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request, ct);
    }

    private static async Task<HttpResponseMessage> SendQuarantineTicketAsync(HttpClient client, long context, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/completions");
        request.Headers.TryAddWithoutValidation("X-Inference-Required-Context", context.ToString(CultureInfo.InvariantCulture));
        request.Content = new StringContent("""{"model":"candidate","prompt":"target"}""", Encoding.UTF8, "application/json");
        return await client.SendAsync(request, ct);
    }

    private sealed class QuarantineSwitchScheduler : IRequestScheduler
    {
        public SchedulerDecision SelectNext(QueuedRequest request, SchedulerView view, RouterConfig config) =>
            new() { Engine = "native", SwitchRequired = true, TargetProfile = "small" };
    }

    private sealed class QuarantineProvider(FakeEngineHandler upstream) : IEngineLifecycleProvider
    {
        private int _stops;
        public string Kind => DockerLifecycleProvider.ProviderKind;
        internal ConcurrentQueue<string> Ownership { get; } = new();
        internal ConcurrentQueue<string> Preflight { get; } = new();
        internal ConcurrentQueue<string> Launches { get; } = new();
        internal bool AllowSmall { get; set; }
        internal Func<CancellationToken, Task>? BeforePreflight { get; set; }
        internal int StopCalls => Volatile.Read(ref _stops);

        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
        {
            Ownership.Enqueue(target.EffectiveProfile);
            return ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);
        }

        public async ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
        {
            Preflight.Enqueue(target.EffectiveProfile);
            if (BeforePreflight is { } before) await before(ct);
            return AllowSmall && target.EffectiveProfile == "small"
                ? LifecycleResult.Ok() : LifecycleResult.Fail("fixture_refusal", "The fixture refused this target.");
        }

        public ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
        {
            Interlocked.Increment(ref _stops);
            return ValueTask.FromResult(LifecycleResult.Ok());
        }

        public ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            Launches.Enqueue(target.EffectiveProfile);
            upstream.LoadedModels = [target.Model];
            return ValueTask.FromResult(LifecycleResult.Ok());
        }

        public ReadinessPolicy Readiness(LifecycleTarget target) => new(false, TimeSpan.FromSeconds(5));
    }
}
