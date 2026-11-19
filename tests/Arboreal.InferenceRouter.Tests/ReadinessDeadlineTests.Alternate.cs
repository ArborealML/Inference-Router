namespace Arboreal.InferenceRouter.Tests;

public sealed partial class ReadinessDeadlineTests
{
    [Fact(Timeout = 30_000)]
    public async Task HealthyAlternateServesWhileTheRetiredReadinessBodyIsStillHeld()
    {
        const string alternateModel = "alternate.model";
        var clock = new ManualClock();
        var feed = new EventRing();
        var held = new HeldRead<byte[]>("""{"data":[{"id":"model"}]}"""u8.ToArray());
        using var content = new HeldJsonContent(held);
        using var retiringHandler = new TrustReadinessHandler(content, holdModels: true);
        using var alternateHandler = new FakeEngineHandler { LoadedModels = [alternateModel] };
        var primary = TrustEngine(tier: null);
        var alternate = new EngineDefinition
        {
            Name = "alternate",
            Endpoint = new Uri("http://alternate.invalid/v1/"),
            Catalog = new Dictionary<string, EngineModelDefinition> { [alternateModel] = new() { Name = alternateModel } },
        };
        await using var router = new RouterCore(new RouterConfig
        {
            AutoSwitch = true,
            Engines = new Dictionary<string, EngineDefinition> { [primary.Name] = primary, [alternate.Name] = alternate },
            HttpHandlerFactory = definition => definition.Name == primary.Name ? retiringHandler : alternateHandler,
        }, scheduler: null, events: feed, clock: TimeProvider.System,
            lifecycleFactory: _ => new TrustReadinessProvider());
        var a = router.Engines[primary.Name];
        var b = router.Engines[alternate.Name];
        a.ReadinessClockForTests = clock;
        var proofs = 0;
        a.AfterSwitchProofForTests = () => Interlocked.Increment(ref proofs);

        try
        {
            Assert.True(await b.ProbeHealthOnceAsync(CancellationToken.None).WaitAsync(Watchdog));
            Assert.True(b.IsReadyFor(alternateModel, null));
            var attempt = a.WaitForModelAsync("model", Watchdog, CancellationToken.None);
            await held.Entered.Task.WaitAsync(Watchdog);
            clock.AdvanceTo(100);
            var result = await attempt.WaitAsync(Watchdog);
            Assert.Equal(EngineRuntime.SwitchOutcome.Failed, result.Outcome);
            Assert.Equal(SwitchReason.HealthTimeout, result.Detail);
            Assert.Null(a.SwitchingToward);
            var retired = a.Capability;

            var ticket = await router.EnqueueAsync("alternate-progress", new InferenceRequest
            {
                Model = alternateModel,
                Body = """{"model":"alternate.model","messages":[{"role":"user","content":"alternate request"}]}"""u8.ToArray(),
                Options = new RouterRequestOptions(),
            }, CancellationToken.None);
            Assert.True(router.TryDispatchOnce());
            Assert.NotEmpty(await ticket.CompletionTask.WaitAsync(Watchdog));
            Assert.Equal(TicketState.Completed, ticket.State);
            Assert.Equal(1, alternateHandler.CompletionCalls);
            Assert.Equal("alternate request", Assert.Single(alternateHandler.ExecutionOrder));
            Assert.Equal(b.Name, Assert.Single(feed.Replay(0), entry => entry.Kind == "request.selected").Engine);
            Assert.False(held.Released.Task.IsCompleted);
            Assert.False(content.Disposed.Task.IsCompleted);
            Assert.True(b.IsReadyFor(alternateModel, null));
            var serving = b.Capability;

            var events = feed.Replay(0).ToArray();
            Assert.Single(events, entry => entry.Engine == a.Name && entry.Kind == "engine.switch_failed");
            held.Release.TrySetResult();
            await content.Disposed.Task.WaitAsync(Watchdog);

            Assert.Same(retired, a.Capability);
            Assert.Same(serving, b.Capability);
            Assert.Equal(events, feed.Replay(0).ToArray());
            Assert.Equal(0, Volatile.Read(ref proofs));
            Assert.Equal(1, retiringHandler.ModelsCalls);
        }
        finally
        {
            held.Release.TrySetResult();
            if (held.Entered.Task.IsCompleted) await content.Disposed.Task.WaitAsync(Watchdog);
        }
    }
}
