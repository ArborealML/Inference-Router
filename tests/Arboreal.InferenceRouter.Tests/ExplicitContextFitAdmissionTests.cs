using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed class ExplicitContextFitAdmissionTests
{
    private const string Model = "fit-model";
    private const string Alias = "fit-alias";
    private const string AlternateCase = "FIT-MODEL";
    private const long SmallWindow = 32_768;
    private const long LargeWindow = 65_536;
    private const int OverFloor = 65_537;
    private const long WideWindow = 131_072;
    private const string Settings = """
        default_model = "fit-model"
        auto_switch = false
        [scheduler]
        chars_per_token_estimate = 1.0
        """ + "\n";

    private static readonly string SmallEngine = External("small", Model, "32k", SmallWindow, alias: true);
    private static readonly string LargeEngine = External("large", Model, "64k", LargeWindow);
    private static readonly string NarrowConfig = Settings + SmallEngine + LargeEngine;

    [Fact(Timeout = 30_000)]
    public async Task ExplicitHeaderRefusesBeforeQueueWhileTheSameEstimatedDemandAndEqualityEnqueue()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = bound.Token;
        using var small = new FakeEngineHandler { ModelsStatus = 503 };
        using var large = new FakeEngineHandler { ModelsStatus = 503 };
        var config = RouterConfigLoader.Load(NarrowConfig);
        config.HttpHandlerFactory = engine => engine.Name == "small" ? small : large;
        await using var app = ServerApp.Build(["--urls", "http://127.0.0.1:0"], configOverride: config);
        await app.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var router = app.Services.GetRequiredService<RouterCore>();

        var body = Encoding.UTF8.GetBytes($"{{\"model\":\"{Alias}\",\"prompt\":\"{new string('a', OverFloor)}\"}}");
        var promptChars = RouterCore.PromptCharsProbe(body);
        Assert.Equal((long)OverFloor, promptChars);
        Assert.Equal((long)OverFloor, RouterCore.EstimateContextTokens(promptChars, config.Scheduler.CharsPerTokenEstimate));

        using (var request = WireRequest(body, "explicit-over", OverFloor))
        using (var response = await http.SendAsync(request, ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            var error = Assert.Single(document.RootElement.EnumerateObject());
            Assert.Equal("error", error.Name);
            Assert.Equal("air_error", error.Value.GetProperty("type").GetString());
            Assert.Equal("model_unavailable", error.Value.GetProperty("code").GetString());
            var message = error.Value.GetProperty("message").GetString();
            Assert.NotNull(message);
            Assert.InRange(message.Length, 1, WireText.ReasonChars);
            Assert.Contains("model 'fit-model' needs at least 65537 tokens", message, StringComparison.Ordinal);
            Assert.Contains("configured tier '64k' on engine 'large' has 65536 tokens", message, StringComparison.Ordinal);
            AssertRejectedBeforeQueue(router, TicketHeader(response));
        }

        await AssertWireEnqueueAsync(http, router, body, "over-estimate", floor: null, ct);
        await AssertWireEnqueueAsync(http, router, body, "equal-floor", LargeWindow, ct);
        Assert.Equal(0, small.CompletionCalls);
        Assert.Equal(0, large.CompletionCalls);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("one-empty")]
    [InlineData("case-only")]
    [InlineData("all-empty")]
    [InlineData("custom")]
    public async Task UnqualifiedLaddersAndCustomSchedulerKeepOrdinaryAdmission(string scenario)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fake = new FakeEngineHandler();
        var text = Settings + (scenario switch
        {
            "one-empty" => SmallEngine + External("large", Model, null, 0),
            "case-only" => SmallEngine + External("large", AlternateCase, "64k", LargeWindow),
            "all-empty" => External("small", Model, null, 0, alias: true) + External("large", Model, null, 0),
            _ => SmallEngine + LargeEngine,
        });
        var config = RouterConfigLoader.Load(text);
        config.HttpHandlerFactory = _ => fake;
        await using var router = new RouterCore(config, scenario == "custom" ? new WaitingScheduler() : null);
        var smaller = router.Engines["small"];
        var larger = router.Engines["large"];
        Assert.True(smaller.DeclaresModel(Model));
        Assert.True(larger.DeclaresModel(Model));
        if (scenario == "all-empty")
            Assert.Empty(smaller.ProfilesForModel(Model));
        else
            Assert.Equal(SmallWindow, Assert.Single(smaller.ProfilesForModel(Model)).MaxContext);

        if (scenario == "custom")
            Assert.Equal(LargeWindow, Assert.Single(larger.ProfilesForModel(Model)).MaxContext);
        else
            Assert.Empty(larger.ProfilesForModel(Model));
        if (scenario == "case-only")
            Assert.Equal(LargeWindow, Assert.Single(larger.ProfilesForModel(AlternateCase)).MaxContext);
        Assert.False(larger.Healthy);
        Assert.Equal(0L, larger.Capability.Serving.WindowFor(Model));

        var ticket = await router.EnqueueAsync("guard", Request(), bound.Token);
        AssertQueued(router, ticket);
        Assert.True(router.TryCancel(ticket.Id));
        var failure = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(bound.Token));
        Assert.Equal("cancelled", failure.Code);
        Assert.Equal(0, fake.CompletionCalls);
    }

    [Fact(Timeout = 20_000)]
    public async Task WideningTopologyReloadDiscardsTheHeldOldNoFitDecision()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fake = new FakeEngineHandler { ModelsStatus = 503 };
        var initial = RouterConfigLoader.LoadForReload(NarrowConfig, "fixture.toml");
        initial.Config.HttpHandlerFactory = _ => fake;
        await using var router = new RouterCore(initial.Config, null, null, TimeProvider.System, reload: initial);
        var prior = router.CurrentGeneration;
        Assert.Equal(SmallWindow, Assert.Single(prior.Engines["small"].ProfilesForModel(Model)).MaxContext);
        Assert.Equal(LargeWindow, Assert.Single(prior.Engines["large"].ProfilesForModel(Model)).MaxContext);

        using var cut = new ConfigReloadRaceTests.Cut();
        var visits = 0;
        router.BeforeEnqueueCommitForTests = () =>
        {
            if (Interlocked.Increment(ref visits) == 1)
                cut.Hold();
        };
        const string threadId = "widening";
        var pending = Task.Run(async () => await router.EnqueueAsync(threadId, Request(), bound.Token), bound.Token);
        RouterTicket ticket;
        try
        {
            await cut.Entered.Task.WaitAsync(bound.Token);
            Assert.Same(prior, router.CurrentGeneration);
            Assert.Equal(1, Volatile.Read(ref visits));
            var stats = router.GetStats();
            Assert.Empty(stats.QueueDepths);
            Assert.DoesNotContain(stats.Threads, thread => thread.ThreadId == threadId);
            Assert.DoesNotContain(router.Events.Replay(0), entry => entry.ThreadId == threadId);

            var amended = RouterConfigLoader.LoadForReload(
                NarrowConfig + External("wider", Model, "128k", WideWindow), "fixture.toml");
            var applied = await router.ApplyConfigurationAsync(amended, prior.Number, bound.Token);
            Assert.Equal((ConfigReloadStatus.Applied, prior.Number + 1), applied);
            var current = router.CurrentGeneration;
            Assert.Same(prior.Engines["small"], current.Engines["small"]);
            Assert.Same(prior.Engines["large"], current.Engines["large"]);
            Assert.Equal(Model, current.Settings.DefaultModel);
            Assert.Equal(WideWindow, Assert.Single(current.Engines["wider"].ProfilesForModel(Model)).MaxContext);
        }
        finally
        {
            cut.Release();
            ticket = await pending.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        Assert.Equal(2, Volatile.Read(ref visits));
        AssertQueued(router, ticket);
        var enqueued = Assert.Single(router.Events.Replay(0), entry => entry.ThreadId == threadId && entry.TicketId is not null);
        Assert.Equal(ticket.Id, enqueued.TicketId);
        Assert.Equal("request.enqueued", enqueued.Kind);
        Assert.True(router.TryCancel(ticket.Id));
        await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(bound.Token));
        Assert.Equal(0, fake.CompletionCalls);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameReportedWideWindowOnlyAdmitsWithoutTheNonfittingLadder(bool withLadder)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var fake = new FakeEngineHandler { LoadedModels = [Model], NCtx = WideWindow, TotalSlots = 1 };
        var config = RouterConfigLoader.Load(Settings + External("large", Model, withLadder ? "64k" : null, LargeWindow, alias: true));
        config.HttpHandlerFactory = _ => fake;
        await using var router = new RouterCore(config);
        var engine = router.Engines["large"];
        Assert.True(await engine.ProbeHealthOnceAsync(bound.Token));
        var observed = engine.Capability;
        Assert.True(observed.Healthy);
        Assert.Equal(Model, Assert.Single(observed.ModelRoster));
        Assert.Equal(WideWindow, observed.Serving.WindowFor(Model));
        Assert.Equal(1, observed.Serving.TotalSlots);

        var ticket = await router.EnqueueAsync("reported-wide", Request(), bound.Token);
        if (withLadder)
        {
            Assert.Equal(LargeWindow, Assert.Single(engine.ProfilesForModel(Model)).MaxContext);
            AssertRejectedBeforeQueue(router, ticket.Id);
            var failure = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(bound.Token));
            Assert.Equal("model_unavailable", failure.Code);
            Assert.Equal(0, fake.CompletionCalls);
        }
        else
        {
            Assert.Empty(engine.ProfilesForModel(Model));
            AssertQueued(router, ticket);
            Assert.True(router.TryDispatchOnce());
            await ticket.CompletionTask.WaitAsync(bound.Token);
            Assert.Equal(TicketState.Completed, ticket.State);
            Assert.Single(router.Events.Replay(0), entry => entry.Kind == "request.selected" && entry.TicketId == ticket.Id);
            Assert.Equal(1, fake.CompletionCalls);
        }
    }

    private static async Task AssertWireEnqueueAsync(HttpClient http, RouterCore router, byte[] body, string threadId,
        long? floor, CancellationToken ct)
    {
        var observed = new TaskCompletionSource<(TicketSnapshot? Ticket, int Depth, bool Cancelled)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeforeEnqueueEligibilityForTests = ticket =>
        {
            if (ticket.ThreadId != threadId) return;
            var snapshot = router.TryGetTicket(ticket.Id);
            var depth = router.GetStats().QueueDepths.GetValueOrDefault(ticket.Model);
            observed.TrySetResult((snapshot, depth, router.TryCancel(ticket.Id)));
        };

        try
        {
            using var request = WireRequest(body, threadId, floor);
            using var response = await http.SendAsync(request, ct);
            Assert.Equal(499, (int)response.StatusCode);
            var id = TicketHeader(response);
            var (ticket, depth, cancelled) = await observed.Task.WaitAsync(ct);
            Assert.NotNull(ticket);
            Assert.Equal(id, ticket.Id);
            Assert.Equal(Model, ticket.Model);
            Assert.Equal("Queued", ticket.State);
            Assert.Equal(1, depth);
            Assert.True(cancelled);
            var feed = router.Events.Replay(0).Where(entry => entry.TicketId == id);
            Assert.Collection(feed,
                entry =>
                {
                    Assert.Equal("request.enqueued", entry.Kind);
                    Assert.Equal((double)OverFloor, entry.Value);
                },
                entry => Assert.Equal("request.cancelled", entry.Kind));
        }
        finally
        {
            router.BeforeEnqueueEligibilityForTests = null;
        }
    }

    private static void AssertRejectedBeforeQueue(RouterCore router, string id)
    {
        var ticket = router.TryGetTicket(id);
        Assert.NotNull(ticket);
        Assert.Equal(Model, ticket.Model);
        Assert.Equal("Failed", ticket.State);
        Assert.Equal("model_unavailable", ticket.ErrorCode);
        Assert.DoesNotContain(Model, router.GetStats().QueueDepths.Keys);
        var rejection = Assert.Single(router.Events.Replay(0), entry => entry.TicketId == id);
        Assert.Equal("queue.rejected", rejection.Kind);
        Assert.Equal("model_unavailable", rejection.Reason);
    }

    private static void AssertQueued(RouterCore router, RouterTicket ticket)
    {
        Assert.Equal(Model, ticket.Model);
        Assert.Equal(TicketState.Queued, ticket.State);
        Assert.Equal("Queued", router.TryGetTicket(ticket.Id)?.State);
        Assert.Equal(1, router.GetStats().QueueDepths[Model]);
        var item = Assert.Single(router.Events.Replay(0), entry => entry.TicketId == ticket.Id);
        Assert.Equal("request.enqueued", item.Kind);
    }

    private static HttpRequestMessage WireRequest(byte[] body, string threadId, long? floor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/completions")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        request.Headers.Add("X-Inference-Thread", threadId);
        if (floor is { } value)
            request.Headers.Add("X-Inference-Required-Context", value.ToString(CultureInfo.InvariantCulture));
        return request;
    }

    private static string TicketHeader(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("X-Inference-Ticket", out var ids));
        return Assert.Single(ids);
    }

    private static InferenceRequest Request() => new()
    {
        Model = Alias,
        Body = """{"model":"fit-alias","prompt":"fixture request"}"""u8.ToArray(),
        Options = new RouterRequestOptions { RequiredContext = OverFloor },
    };

    private static string External(string engine, string model, string? tier, long window, bool alias = false) =>
        $"""
        [engine.{engine}]
        kind = "openai-compatible"
        endpoint = "http://{engine}.invalid/v1/"
        lifecycle_owner = "external"
        [engine.{engine}.health_probe]
        path = "/props"
        interval_ms = 3600000
        [engine.{engine}.catalog."{model}"]
        """ + "\n" + (alias ? "aliases = [\"fit-alias\"]\n" : "") +
        (tier is null ? "" : FormattableString.Invariant($"serving = [{{ name = \"{tier}\", max_context = {window}, max_slots = 1 }}]\n"));

    private sealed class WaitingScheduler : IRequestScheduler
    {
        public SchedulerDecision SelectNext(QueuedRequest request, SchedulerView view, RouterConfig config) =>
            SchedulerDecision.Wait("fixture");
    }
}
