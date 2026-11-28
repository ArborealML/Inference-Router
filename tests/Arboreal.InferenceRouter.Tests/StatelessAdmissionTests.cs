using System.Net;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed class StatelessAdmissionTests
{
    private const string ThreadHeader = "X-Inference-Thread";

    [Fact(Timeout = 30_000)]
    public async Task OmittedHttpAndActualCliCallsReachIndependentBackendsBeforeRelease()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(22));
        var ct = bound.Token;
        var entered = NewBarrier();
        using var first = new FakeEngineHandler { LoadedModels = ["shared"], HoldCompletions = true };
        first.AfterHeldCompletionEnteredForTests = () => entered.TrySetResult();
        using var second = new FakeEngineHandler { LoadedModels = ["shared", "different"] };
        await using var app = await StartAsync(first, second, ct);
        var router = app.Services.GetRequiredService<RouterCore>();
        Assert.Throws<ArgumentNullException>(() => { _ = router.EnqueueAsync(null!, new InferenceRequest { Body = "{}"u8.ToArray() }); });
        using var http = Client(app);
        using var heldRequest = Request("shared", "held");
        var held = http.SendAsync(heldRequest, ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            using var sameRequest = Request("shared", "omitted HTTP");
            using var same = await http.SendAsync(sameRequest, ct);
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            var url = app.Urls.Single();
            var chat = CliCompletionProcessTests.RunCliAsync("chat",
                ["--server", url, "-m", "different", "--no-stream", "omitted chat"], null, ct);
            var complete = CliCompletionProcessTests.RunCliAsync("complete",
                ["--server", url, "-m", "shared", "--no-stream", "omitted complete"], null, ct);
            var cli = await Task.WhenAll(chat, complete).WaitAsync(ct);

            Assert.All(cli, result => Assert.Equal(0, result.ExitCode));
            Assert.Contains("omitted HTTP", second.ExecutionOrder);
            Assert.Contains("omitted chat", second.ExecutionOrder);
            Assert.Contains("omitted complete", second.ExecutionOrder);
            Assert.Equal(3, second.CompletionCalls);
            Assert.False(held.IsCompleted);
            Assert.Empty(router.GetStats().Threads);
            var feed = router.Events.Replay(0);
            Assert.DoesNotContain(feed, entry => entry.Kind == "thread.registered");
            var enqueued = feed.Where(entry => entry.Kind == "request.enqueued").ToArray();
            Assert.Equal(4, enqueued.Select(entry => entry.ThreadId).Distinct(StringComparer.Ordinal).Count());
            Assert.All(enqueued, entry => Assert.Equal($"request:{entry.TicketId}", entry.ThreadId));
        }
        finally { first.ReleaseGate(); }
        using var released = await held.WaitAsync(ct);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("named-session")]
    [InlineData("anon")]
    public async Task ExplicitNamesIncludingLegacyAnonKeepTheirSharedThreadLimit(string name)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var entered = NewBarrier();
        using var first = new FakeEngineHandler { LoadedModels = ["shared"], HoldCompletions = true };
        first.AfterHeldCompletionEnteredForTests = () => entered.TrySetResult();
        using var second = new FakeEngineHandler { LoadedModels = ["different"] };
        await using var app = await StartAsync(first, second, ct);
        var router = app.Services.GetRequiredService<RouterCore>();
        using var http = Client(app);
        var waiting = new TaskCompletionSource<RouterEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.Events.Subscribe(entry =>
        {
            if (entry.Kind == "request.waiting" && entry.Reason == $"thread_concurrency_limit ({name})")
                waiting.TrySetResult(entry);
        });
        using var heldRequest = Request("shared", "held", name);
        var held = http.SendAsync(heldRequest, ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            using var blockedRequest = Request("different", "limited", name);
            var blocked = http.SendAsync(blockedRequest, ct);
            var evt = await waiting.Task.WaitAsync(ct);
            var snapshot = Assert.IsType<TicketSnapshot>(router.TryGetTicket(evt.TicketId!));
            Assert.Equal(name, snapshot.ThreadId);
            Assert.Equal(name, snapshot.QueueWaitScope);
            Assert.Equal("thread_concurrency_limit", snapshot.QueueWaitReason);
            Assert.Equal(0, second.CompletionCalls);
            Assert.False(blocked.IsCompleted);
            Assert.Equal(name, Assert.Single(router.GetStats().Threads).ThreadId);
            first.ReleaseGate();
            using var completed = await blocked.WaitAsync(ct);
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            Assert.Equal("limited", Assert.Single(second.ExecutionOrder));
        }
        finally { first.ReleaseGate(); }
        using var released = await held.WaitAsync(ct);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
    }

    [Fact(Timeout = 20_000)]
    public async Task ADeliberateNamedThreadCanShareTheDiagnosticSpellingWithoutSharingAccounting()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var entered = NewBarrier();
        using var first = new FakeEngineHandler { LoadedModels = ["shared"], HoldCompletions = true };
        first.AfterHeldCompletionEnteredForTests = () => entered.TrySetResult();
        using var second = new FakeEngineHandler { LoadedModels = ["different"] };
        await using var app = await StartAsync(first, second, ct);
        var router = app.Services.GetRequiredService<RouterCore>();
        using var http = Client(app);
        using var heldRequest = Request(model: null, "diagnostic");
        var held = http.SendAsync(heldRequest, ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            Assert.Empty(router.GetStats().Threads);
            var firstSelection = Assert.Single(router.Events.Replay(0), entry => entry.Kind == "request.selected");
            var label = Assert.IsType<string>(firstSelection.ThreadId);
            var thread = router.RegisterThread(label, ThreadPriority.Low(), new() { ModelAffinity = "different" });
            thread.SetPriority(ThreadPriority.High());
            using var namedRequest = Request(model: null, "deliberately named", label);
            using var named = await http.SendAsync(namedRequest, ct);
            Assert.Equal(HttpStatusCode.OK, named.StatusCode);
            Assert.Equal("deliberately named", Assert.Single(second.ExecutionOrder));
            Assert.False(held.IsCompleted);
        }
        finally { first.ReleaseGate(); }
        using var released = await held.WaitAsync(ct);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Equal("diagnostic", Assert.Single(first.ExecutionOrder));
        var namedStats = Assert.Single(router.GetStats().Threads);
        Assert.Equal(1, namedStats.Enqueued);
        Assert.Equal(1, namedStats.Completed);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("empty", false)]
    [InlineData("space", false)]
    [InlineData("duplicated", false)]
    [InlineData("conflict", false)]
    [InlineData("comma", false)]
    [InlineData("conflict", true)]
    public async Task MalformedThreadHeadersRefuseBeforeTheBodyOrAnyQueueEffect(string shape, bool badPriority)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var first = new FakeEngineHandler { LoadedModels = ["shared"] };
        using var second = new FakeEngineHandler { LoadedModels = ["different"] };
        await using var app = await StartAsync(first, second, ct);
        var router = app.Services.GetRequiredService<RouterCore>();
        string[] values = shape switch
        {
            "empty" => [""],
            "space" => ["   "],
            "duplicated" => ["same", "same"],
            "conflict" => ["first", "second"],
            _ => ["first, second"],
        };
        using var http = Client(app);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };
        Assert.True(request.Headers.TryAddWithoutValidation(ThreadHeader, values));
        if (badPriority) Assert.True(request.Headers.TryAddWithoutValidation("X-Inference-Priority", "bogus"));
        using var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Inference-Ticket"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(badPriority ? "invalid_priority" : "invalid_request", error.GetProperty("code").GetString());
        Assert.Contains(badPriority ? "X-Inference-Priority" : ThreadHeader, error.GetProperty("message").GetString());
        AssertNoThreadsOrEnqueues(router);
        Assert.Equal(0, first.CompletionCalls + second.CompletionCalls);
    }

    [Fact(Timeout = 20_000)]
    public async Task StatelessOverflowRefusalAndQueuedAndActiveCancellationLeaveNoRegisteredThread()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var entered = NewBarrier();
        using var first = new FakeEngineHandler { LoadedModels = ["shared"], HoldCompletions = true };
        first.AfterHeldCompletionEnteredForTests = () => entered.TrySetResult();
        using var second = new FakeEngineHandler { LoadedModels = ["different"] };
        await using var app = await StartAsync(first, second, ct, maxQueuePerModel: 1);
        var router = app.Services.GetRequiredService<RouterCore>();
        using var http = Client(app);
        using var heldRequest = Request("shared", "held");
        var held = http.SendAsync(heldRequest, ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            var active = Assert.Single(router.Events.Replay(0), entry => entry.Kind == "request.selected").TicketId!;
            var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            router.Events.Subscribe(entry =>
            {
                if (entry.Kind == "request.enqueued" && entry.Model == "shared") waiting.TrySetResult(entry.TicketId!);
            });
            using var queuedRequest = Request("shared", "queued");
            var queued = http.SendAsync(queuedRequest, ct);
            var queuedId = await waiting.Task.WaitAsync(ct);
            using var overflowRequest = Request("shared", "overflow");
            using var overflow = await http.SendAsync(overflowRequest, ct);
            Assert.Equal(HttpStatusCode.TooManyRequests, overflow.StatusCode);
            using var unavailableRequest = Request("unknown", "unavailable");
            using var unavailable = await http.SendAsync(unavailableRequest, ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.True(router.TryCancel(queuedId));
            using var cancelledQueued = await queued.WaitAsync(ct);
            Assert.Equal(499, (int)cancelledQueued.StatusCode);
            Assert.True(router.TryCancel(active));
            using var cancelledActive = await held.WaitAsync(ct);
            Assert.Equal(499, (int)cancelledActive.StatusCode);
            Assert.Equal(1, first.CompletionCalls);
            Assert.Empty(router.GetStats().QueueDepths);
            Assert.Empty(router.GetStats().Threads);
            var feed = router.Events.Replay(0);
            Assert.DoesNotContain(feed, entry => entry.Kind == "thread.registered");
            Assert.Equal(2, feed.Count(entry => entry.Kind == "request.cancelled"));
            Assert.Equal(2, feed.Count(entry => entry.Kind == "queue.rejected"));
        }
        finally { first.ReleaseGate(); }
    }

    [Fact(Timeout = 30_000)]
    public async Task CliRejectsMissingBlankAndDuplicateExplicitThreadBeforePosting()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = bound.Token;
        using var first = new FakeEngineHandler { LoadedModels = ["shared"] };
        using var second = new FakeEngineHandler { LoadedModels = ["different"] };
        await using var app = await StartAsync(first, second, ct);
        var router = app.Services.GetRequiredService<RouterCore>();
        string[][] malformed = [["--thread"], ["-t", "  "], ["-t", "a,b"], ["-t", "name", "--thread", "name"], ["--thread", "--no-stream"]];
        foreach (var options in malformed)
        {
            var result = await CliCompletionProcessTests.RunCliAsync("chat",
                ["--server", app.Urls.Single(), "--no-stream", "prompt", .. options], null, ct);
            Assert.Equal(2, result.ExitCode);
            Assert.Contains("invalid thread", Encoding.UTF8.GetString(result.Stderr));
        }
        AssertNoThreadsOrEnqueues(router);
        Assert.Equal(0, first.CompletionCalls + second.CompletionCalls);
    }

    private static async Task<WebApplication> StartAsync(FakeEngineHandler first, FakeEngineHandler second, CancellationToken ct,
                                                         int maxQueuePerModel = 8)
    {
        var config = new RouterConfig
        {
            DefaultModel = "shared",
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 1, MaxQueuePerModel = maxQueuePerModel },
            Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal)
            {
                ["a"] = new() { Name = "a", Endpoint = new Uri("http://a.invalid/v1"), Remote = true },
                ["b"] = new() { Name = "b", Endpoint = new Uri("http://b.invalid/v1"), Remote = true },
            },
            HttpHandlerFactory = engine => engine.Name == "a" ? first : second,
        };
        var app = ServerApp.Build(["--urls", "http://127.0.0.1:0"], configOverride: config);
        try
        {
            await app.StartAsync(ct);
            var router = app.Services.GetRequiredService<RouterCore>();
            foreach (var engine in router.Engines.Values) Assert.True(await engine.ProbeHealthOnceAsync(ct));
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };

    private static HttpRequestMessage Request(string? model, string marker, string? thread = null)
    {
        var body = model is null ? $$"""{"messages":[{"role":"user","content":"{{marker}}"}]}""" :
            $$"""{"model":"{{model}}","messages":[{"role":"user","content":"{{marker}}"}]}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (thread is not null) request.Headers.Add(ThreadHeader, thread);
        return request;
    }

    private static void AssertNoThreadsOrEnqueues(RouterCore router)
    {
        Assert.Empty(router.GetStats().Threads);
        Assert.Empty(router.GetStats().QueueDepths);
        Assert.DoesNotContain(router.Events.Replay(0), entry => entry.Kind is "thread.registered" or "request.enqueued" or "queue.rejected");
    }

    private static TaskCompletionSource NewBarrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
