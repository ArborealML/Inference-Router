using System.Text;

namespace Arboreal.InferenceRouter.Tests;

public sealed class DomainPreparationTests
{
    [Theory(Timeout = 20_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IndependentDomainSendsWhileOtherDomainIsAtItsPreparationLimit(bool reverse, bool sameModel)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var held = new AdmissionFixtureBarrier();
        var ct = watchdog.Token;
        var first = reverse ? "z" : "a";
        var second = reverse ? "a" : "z";
        var follower = sameModel ? "shared" : "independent";
        using var discoveryA = new FakeEngineHandler { LoadedModels = ["shared", "fill"] };
        using var discoveryB = new FakeEngineHandler { LoadedModels = ["shared", "independent"] };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var destinationA = new AdmissionFixtureSink();
        var destinationB = new AdmissionFixtureSink();
        var admission = new ExactTicketAdmission(async (claim, _) =>
        {
            if (claim.Target.Engine == first)
            {
                if (Interlocked.Increment(ref started) == 2) entered.TrySetResult();
                await held.HoldAsync(ct); // An ignored retirement still holds its originating budget.
            }
            return new Preparation();
        }, (request, _) => (request.RequestUri!.Host == first + ".invalid" ? destinationA : destinationB).AcceptAsync(request));
        await using var router = new RouterCore(new RouterConfig
        {
            AutoSwitch = false,
            Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal)
                { ["a"] = External("a"), ["z"] = External("z") },
            HttpHandlerFactory = engine => engine.Name == first ? discoveryA : discoveryB,
        }, null, null, TimeProvider.System, admission: admission);
        foreach (var engine in router.Engines.Values) Assert.True(await engine.ProbeHealthOnceAsync(ct));

        admission.Publish(new(first, "shared", null), 1, open: true);
        admission.Publish(new(first, "fill", null), 1, open: true);
        var head = await router.EnqueueAsync("head", Request("shared", "a holds the first preparation"), ct);
        var fill = await router.EnqueueAsync("fill", Request("fill", "a holds the second preparation"), ct);
        router.Start();
        try
        {
            await entered.Task.WaitAsync(ct);
            Assert.True(router.TryCancel(fill.Id));
            await Assert.ThrowsAsync<RouterRequestException>(() => fill.CompletionTask.WaitAsync(ct));
            admission.Publish(new(second, follower, null), 1, open: true);
            var independent = await router.EnqueueAsync("independent", Request(follower, "b reaches its backend"), ct);
            await independent.CompletionTask.WaitAsync(ct);
            Assert.Equal(second, independent.Engine);
            Assert.Equal(1, destinationB.Calls);
            Assert.Contains("b reaches its backend", Encoding.UTF8.GetString(Assert.Single(destinationB.ReceivedBodies)), StringComparison.Ordinal);
            Assert.Equal(0, destinationA.Calls);
            Assert.False(head.CompletionTask.IsCompleted);
            Assert.Equal(2, admission.PreparationCount);
        }
        finally { held.Release(); }
        await head.CompletionTask.WaitAsync(ct);
        Assert.Equal(first, head.Engine);
        Assert.Equal(1, destinationA.Calls);
        Assert.Equal(1, destinationB.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task SharedThreadCapBlocksAnIndependentDomainUntilExecutionReleasesIt()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = new AdmissionFixtureBarrier();
        var ct = watchdog.Token;
        using var discoveryA = new FakeEngineHandler { LoadedModels = ["first"] };
        using var discoveryB = new FakeEngineHandler { LoadedModels = ["second"] };
        var independent = new AdmissionFixtureSink();
        var held = new AdmissionFixtureSink(async () =>
        {
            await response.HoldAsync(ct);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var admission = new ExactTicketAdmission(static (_, _) => ValueTask.FromResult<IAsyncDisposable?>(new Preparation()),
            (request, _) => (request.RequestUri!.Host == "a.invalid" ? held : independent).AcceptAsync(request));
        await using var router = new RouterCore(new RouterConfig
        {
            AutoSwitch = false,
            Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal) { ["a"] = External("a"), ["z"] = External("z") },
            HttpHandlerFactory = engine => engine.Name == "a" ? discoveryA : discoveryB,
        }, null, null, TimeProvider.System, admission: admission);
        foreach (var engine in router.Engines.Values) Assert.True(await engine.ProbeHealthOnceAsync(ct));
        admission.Publish(new("a", "first", null), 1, open: true);
        admission.Publish(new("z", "second", null), 1, open: true);
        router.Start();
        var first = await router.EnqueueAsync("shared", Request("first", "holds named thread credit"), ct);
        RouterTicket blocked;
        try
        {
            await response.Reached.WaitAsync(ct);
            blocked = await router.EnqueueAsync("shared", Request("second", "requires shared thread credit"), ct);
            var allowed = await router.EnqueueAsync("another", Request("second", "has separate thread credit"), ct);
            await allowed.CompletionTask.WaitAsync(ct);
            Assert.Equal(TicketState.Queued, blocked.State);
            Assert.Equal("thread_concurrency_limit", router.TryGetTicket(blocked.Id)?.QueueWaitReason);
            Assert.Equal(1, independent.Calls);
        }
        finally { response.Release(); }

        await Task.WhenAll(first.CompletionTask, blocked.CompletionTask).WaitAsync(ct);
        Assert.Equal(2, independent.Calls);
    }

    private static EngineDefinition External(string name) => new() { Name = name, Endpoint = new($"http://{name}.invalid/v1"), Remote = true };

    private static InferenceRequest Request(string model, string text) => new()
    {
        Model = model,
        Body = Encoding.UTF8.GetBytes($$"""{"model":"{{model}}","messages":[{"role":"user","content":"{{text}}"}]}"""),
        Options = new RouterRequestOptions(),
    };

    private sealed class Preparation : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
