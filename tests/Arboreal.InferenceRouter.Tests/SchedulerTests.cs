using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

public class SchedulerTests
{
    private static QueuedRequest Make(string model, ThreadPriority priority, long seq)
    {
        var ticket = new RouterTicket($"t{seq}", "th", model, DateTimeOffset.UtcNow);
        var thread = new RouterThread("th", priority, 1.0, null);
        return new QueuedRequest
        {
            Ticket = ticket,
            Request = new InferenceRequest
            {
                Model = model,
                Stream = false,
                Body = "{}"u8.ToArray(),
                Options = new RouterRequestOptions(),
            },
            PriorityNode = new PrioritizedRequest(ticket, priority, seq),
            Thread = thread,
            EnqueuedSeq = seq,
            PromptChars = 100,
        };
    }

    private static EngineRuntimeInfo Info(string name, string[] loaded, string[] catalog,
        int inflight = 0, bool healthy = true, long matched = 0, bool switching = false)
        => new()
        {
            Name = name,
            LoadedModels = loaded,
            CatalogModels = catalog,
            SwitchingToward = [],
            Inflight = inflight,
            InflightTokenProxy = 0,
            Healthy = healthy,
            Switching = switching,
            MatchedPrefixChars = matched,
        };

    private static RouterConfig Config(bool autoSwitch = true)
        => new()
        {
            AutoSwitch = autoSwitch,
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 8 },
        };

    [Fact]
    public void Prefers_Engine_With_Model_Loaded()
    {
        var view = new SchedulerView(new[]
        {
            Info("a", [], ["m1"]),
            Info("b", ["m1"], ["m1"]),
        }, DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(Make("m1", ThreadPriority.Normal(), 1), view, Config());
        Assert.Equal("b", decision.Engine);
        Assert.False(decision.SwitchRequired);
    }

    [Fact]
    public void Falls_Back_To_Catalog_With_Switch_When_AutoSwitch()
    {
        var view = new SchedulerView(new[] { Info("a", [], ["m1"]) }, DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(Make("m1", ThreadPriority.Normal(), 1), view, Config(autoSwitch: true));
        Assert.Equal("a", decision.Engine);
        Assert.True(decision.SwitchRequired);
    }

    [Fact]
    public void Rejects_When_No_Catalog_Match_And_No_AutoSwitch()
    {
        var view = new SchedulerView(new[] { Info("a", [], ["other"]) }, DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(Make("m1", ThreadPriority.Normal(), 1), view, Config(autoSwitch: false));
        Assert.Null(decision.Engine);
        Assert.NotNull(decision.RejectReason);
    }

    [Fact]
    public void Loaded_Engine_Beats_Busier_Affinity_Match_At_Threshold()
    {
        // both engines have the model; a has big prefix match but is fully busy
        var view = new SchedulerView(new[]
        {
            Info("a", ["m1"], ["m1"], inflight: 8, matched: 4000),
            Info("b", ["m1"], ["m1"], inflight: 0, matched: 0),
        }, DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(Make("m1", ThreadPriority.Normal(), 1), view, Config());
        Assert.Equal("b", decision.Engine);
    }

    [Fact]
    public void Affinity_Match_Wins_Among_Idle_Engines()
    {
        var view = new SchedulerView(new[]
        {
            Info("a", ["m1"], ["m1"], inflight: 0, matched: 4000),
            Info("b", ["m1"], ["m1"], inflight: 0, matched: 0),
        }, DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(Make("m1", ThreadPriority.Normal(), 1), view, Config());
        Assert.Equal("a", decision.Engine);
    }

    [Fact]
    public void Skips_Unhealthy_Engines()
    {
        var view = new SchedulerView(new[] { Info("a", ["m1"], ["m1"], healthy: false) }, DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(Make("m1", ThreadPriority.Normal(), 1), view, Config());
        Assert.Null(decision.Engine);
    }

    [Fact]
    public void Skips_Switching_Engines()
    {
        var view = new SchedulerView(new[] { Info("a", ["m1"], ["m1"], switching: true) }, DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(Make("m1", ThreadPriority.Normal(), 1), view, Config());
        Assert.Null(decision.Engine);
    }
}
