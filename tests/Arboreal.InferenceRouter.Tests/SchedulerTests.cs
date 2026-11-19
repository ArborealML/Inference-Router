using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

public class SchedulerTests
{
    private static QueuedRequest Make(string model, ThreadPriority priority, long seq, long requiredContext = 0)
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
            RequiredContextTokens = requiredContext,
        };
    }

    private static readonly ServingProfile[] Ladder =
    [
        new ServingProfile { Name = "small", MaxContext = 131_072, MaxSlots = 2 },
        new ServingProfile { Name = "mid", MaxContext = 262_144, MaxSlots = 2 },
        new ServingProfile { Name = "big", MaxContext = 1_048_576, MaxSlots = 1 },
    ];

    private static EngineRuntimeInfo Info(string name, string[] loaded, string[] catalog,
        int inflight = 0, bool healthy = true, long matched = 0, bool switching = false,
        long activeContextWindow = 0, IReadOnlyList<ServingProfile>? profiles = null, string? model = null)
        => new()
        {
            Name = name,
            LoadedModels = loaded,
            CatalogModels = catalog,
            // Production computes this on the engine itself (EngineRuntime.CanServeModel); the fixture
            // derives it from the same two lists so no test can pose a combination no engine could produce.
            // Pass `model` in the cases that are about the capability gate; leaving it out means "yes",
            // which is what every other case here assumes.
            CanServeModel = model is null
                || loaded.Contains(model, StringComparer.OrdinalIgnoreCase)
                || catalog.Contains(model, StringComparer.OrdinalIgnoreCase),
            SwitchingToward = [],
            Inflight = inflight,
            InflightTokenProxy = 0,
            Healthy = healthy,
            Switching = switching,
            MatchedPrefixChars = matched,
            ActiveContextWindow = activeContextWindow,
            ServingProfiles = profiles ?? Array.Empty<ServingProfile>(),
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
        Assert.Equal("no_available_engine", decision.RejectReason);
    }

    /// <summary>A model the engine reports but no catalog names is still eligible. This is the case the
    /// admission refusal must not swallow: a model loaded out-of-band has no config entry, and the
    /// scheduler has always served it (§7, TASK-35 review blocker).</summary>
    [Fact]
    public void Serves_A_Reported_Model_That_No_Catalog_Entry_Names()
    {
        var view = new SchedulerView(
            new[] { Info("a", ["out-of-band"], [], model: "out-of-band") }, DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(
            Make("out-of-band", ThreadPriority.Normal(), 1), view, Config());
        Assert.Equal("a", decision.Engine);
        Assert.False(decision.SwitchRequired);
    }

    // ── serving-profile escalation (§7.6) ────────────────────────────────────

    [Fact]
    public void Escalates_Loaded_Engine_When_Context_Exceeds_Window()
    {
        var view = new SchedulerView(
            new[] { Info("a", ["m1"], ["m1"], activeContextWindow: 131_072, profiles: Ladder) },
            DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(
            Make("m1", ThreadPriority.Normal(), 1, requiredContext: 200_000), view, Config());
        Assert.Equal("a", decision.Engine);
        Assert.True(decision.SwitchRequired);
        Assert.Equal("mid", decision.TargetProfile);
    }

    [Fact]
    public void Prefers_Idle_Window_Match_Over_Engine_Needing_Escalation()
    {
        var view = new SchedulerView(new[]
        {
            Info("busy", ["m1"], ["m1"], inflight: 1, activeContextWindow: 1_048_576, profiles: Ladder),
            Info("small-window", ["m1"], ["m1"], activeContextWindow: 131_072, profiles: Ladder),
        }, DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(
            Make("m1", ThreadPriority.Normal(), 1, requiredContext: 200_000), view, Config());
        Assert.Equal("busy", decision.Engine);
        Assert.False(decision.SwitchRequired);
        Assert.Null(decision.TargetProfile);
    }

    [Fact]
    public void Chooses_Smallest_Fitting_Profile_On_Escalation()
    {
        var view = new SchedulerView(
            new[] { Info("a", ["m1"], ["m1"], activeContextWindow: 131_072, profiles: Ladder) },
            DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(
            Make("m1", ThreadPriority.Normal(), 1, requiredContext: 131_073), view, Config());
        Assert.Equal("mid", decision.TargetProfile);
    }

    [Fact]
    public void WaitsForWindowEvidenceWhenAProfileIsRequired()
    {
        var view = new SchedulerView(
            new[] { Info("a", ["m1"], ["m1"], activeContextWindow: 0, profiles: Ladder) },
            DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(
            Make("m1", ThreadPriority.Normal(), 1, requiredContext: 200_000), view, Config());
        Assert.Null(decision.Engine);
        Assert.False(decision.SwitchRequired);
    }

    [Fact]
    public void RoutesLoadedModelsWithoutARequiredProfileWhenWindowIsUnreported()
    {
        var view = new SchedulerView(
            new[] { Info("a", ["m1"], ["m1"], activeContextWindow: 0, profiles: Ladder) },
            DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(
            Make("m1", ThreadPriority.Normal(), 1, requiredContext: 0), view, Config());
        Assert.Equal("a", decision.Engine);
        Assert.False(decision.SwitchRequired);
    }

    [Fact]
    public void Skips_Engine_Whose_Ladder_Cannot_Fit()
    {
        var view = new SchedulerView(new[]
        {
            Info("small-only", ["m1"], ["m1"], activeContextWindow: 131_072, profiles: [Ladder[0]]),
            Info("ladderless", ["m1"], ["m1"]),
        }, DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(
            Make("m1", ThreadPriority.Normal(), 1, requiredContext: 200_000), view, Config());
        Assert.Equal("ladderless", decision.Engine);
        Assert.False(decision.SwitchRequired);
    }

    [Fact]
    public void No_Escalation_When_AutoSwitch_Off()
    {
        var view = new SchedulerView(
            new[] { Info("a", ["m1"], ["m1"], activeContextWindow: 131_072, profiles: Ladder) },
            DateTimeOffset.UtcNow);
        var decision = new PrefixAwareScheduler().SelectNext(
            Make("m1", ThreadPriority.Normal(), 1, requiredContext: 200_000), view, Config(autoSwitch: false));
        Assert.Null(decision.Engine);
        Assert.Equal("no_available_engine", decision.RejectReason);
    }
}
