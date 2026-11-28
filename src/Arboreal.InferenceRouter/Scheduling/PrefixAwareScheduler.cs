namespace Arboreal.InferenceRouter;

/// <summary>Runtime view of one engine for the scheduler.</summary>
public sealed class EngineRuntimeInfo
{
    public required string Name { get; init; }
    /// <summary>Models currently loaded and serving.</summary>
    public required IReadOnlyList<string> LoadedModels { get; init; }
    /// <summary>Models this engine's catalog could serve (may require a switch).</summary>
    public required IReadOnlyList<string> CatalogModels { get; init; }
    /// <summary>Models queued elsewhere but this engine is mid-switch toward (approximate).</summary>
    public required IReadOnlyList<string> SwitchingToward { get; init; }
    /// <summary>Does this engine declare the candidate request's model in its configured catalog, or report
    /// it loaded right now? Precomputed by the view builder from the engine itself, so the scheduler and
    /// the admission path ask one question of one implementation (§7, "a model nothing here can serve").
    /// It says nothing about health or switching, which the scheduler weighs separately.</summary>
    public required bool CanServeModel { get; init; }
    /// <summary>The local lifecycle coordinator can start a configured cold target.</summary>
    internal bool CanLoad { get; init; }
    internal bool CanSwitchProfile { get; init; }
    public required int Inflight { get; init; }
    /// <summary>Estimated token-proxy load: sum of prompt chars + expected output of in-flight.</summary>
    public required long InflightTokenProxy { get; init; }
    public required bool Healthy { get; init; }
    public required bool Switching { get; init; }
    /// <summary>Matched-prefix extent (normalized chars) this engine holds for the candidate request.</summary>
    public required long MatchedPrefixChars { get; init; }
    /// <summary>Measured serving window for this model in tokens. Zero means unknown.</summary>
    public long ActiveContextWindow { get; init; }
    /// <summary>Serving-profile ladder declared for the candidate request's model (empty = no ladder).</summary>
    public IReadOnlyList<ServingProfile> ServingProfiles { get; init; } = Array.Empty<ServingProfile>();
    internal EngineRuntime.DispatchObservation DispatchObservation { get; init; }
    internal LocalLifecycleGroup? DispatchGroup { get; init; }
    internal long DispatchGroupRevision { get; init; }
    internal bool NamedPhysicalUse { get; init; }
    internal int DispatchInferenceUses { get; init; }
    internal PrefixAwareScheduler.NamedRoute? NamedProfile { get; init; }
    internal VerifiedDispatchResident? ManagedResident { get; init; }
    internal IReadOnlyList<ServingProfile>? ManagedColdProfiles { get; init; }
    internal string? DefaultProfile { get; init; }
}

/// <summary>Decision: engine name (null = wait) or rejection.</summary>
public sealed record SchedulerDecision
{
    public string? Engine { get; init; }
    public string? RejectReason { get; init; }
    public bool SwitchRequired { get; init; }
    /// <summary>Serving profile the switch must land on (context-window escalation); null = model default.</summary>
    public string? TargetProfile { get; init; }
    public long EstimatedWaitMs { get; init; }
    public static SchedulerDecision Wait(string reason) => new() { Engine = null, RejectReason = reason };
}

/// <summary>Pluggable scheduler. Must be deterministic given the view + config for testability.</summary>
public interface IRequestScheduler
{
    SchedulerDecision SelectNext(QueuedRequest request, SchedulerView view, RouterConfig config);
}

public sealed record SchedulerView(IReadOnlyList<EngineRuntimeInfo> Engines, DateTimeOffset Now);

/// <summary>
/// v1 policy: model-affinity gate → prefix-affinity bonus → busy-ness penalty.
/// Uncached requests (short matched prefix) prefer the least-busy healthy engine
/// that already has the model loaded. Prefix affinity dominates only past
/// PrefixAffinityThreshold. Engines mid-switch are excluded; engines that could
/// serve the model after a switch are considered only when auto_switch allows and
/// no eligible resident has capacity.
/// </summary>
public sealed class PrefixAwareScheduler : IRequestScheduler
{
    internal readonly record struct Target(bool SwitchRequired, string? Profile);
    internal readonly record struct NamedRoute(ServingProfile? Profile, bool FitsResident);
    internal readonly record struct RouteFit(bool Healthy, bool Switching, bool CanServeModel, bool CanLoad,
        bool CanSwitchProfile, IReadOnlyList<string> LoadedModels, long ActiveContextWindow,
        IReadOnlyList<ServingProfile> ServingProfiles, EngineRuntime.DispatchObservation Observation, NamedRoute? NamedProfile = null,
        VerifiedDispatchResident? ManagedResident = null, IReadOnlyList<ServingProfile>? ManagedColdProfiles = null,
        string? DefaultProfile = null);
    internal readonly record struct RankedRoute(EngineRuntimeInfo Engine, Target Target, double Score)
    {
        internal SchedulerDecision Decision => new()
        {
            Engine = Engine.Name,
            SwitchRequired = Target.SwitchRequired,
            TargetProfile = Target.Profile,
        };
    }

    public SchedulerDecision SelectNext(QueuedRequest request, SchedulerView view, RouterConfig config)
    {
        foreach (var route in RankRoutes(request, view.Engines, config))
            if (route.Engine.DispatchObservation.SlotLimit == 0 || route.Engine.DispatchObservation.HasCapacity)
                return route.Decision;
        return SchedulerDecision.Wait(view.Engines.Count == 0 ? "no_engines" : "no_available_engine");
    }

    internal static List<RankedRoute> RankRoutes(QueuedRequest request, IReadOnlyList<EngineRuntimeInfo> engines,
                                                 RouterConfig config)
    {
        var routes = new List<RankedRoute>(engines.Count);
        foreach (var engine in engines)
            if (DescribeTarget(request, new(engine.Healthy, engine.Switching, engine.CanServeModel, engine.CanLoad,
                engine.CanSwitchProfile, engine.LoadedModels, engine.ActiveContextWindow, engine.ServingProfiles,
                engine.DispatchObservation, engine.NamedProfile, engine.ManagedResident, engine.ManagedColdProfiles,
                engine.DefaultProfile), config) is { } target)
                routes.Add(new(engine, target, AffinityScore(engine) - BusyScore(engine, request, config)));
        routes.Sort(static (a, b) =>
        {
            var order = a.Target.SwitchRequired.CompareTo(b.Target.SwitchRequired);
            if (order != 0) return order;
            order = b.Score.CompareTo(a.Score);
            return order != 0 ? order : string.CompareOrdinal(a.Engine.Name, b.Engine.Name);
        });
        return routes;
    }

    // No affinity score, queue mutation, or resource acquisition is needed to check fit for one route.
    internal static Target? DescribeTarget(QueuedRequest request, RouteFit engine, RouterConfig config)
    {
        if ((!engine.Healthy && !engine.CanLoad) || engine.Switching || !engine.CanServeModel) return null;
        var model = request.Ticket.Model;
        var loaded = engine.Healthy && engine.LoadedModels.Contains(model, StringComparer.OrdinalIgnoreCase);
        var named = request.Request.Options?.Profile;
        ServingProfile? target = null;
        if (named is not null)
        {
            if (engine.Observation.SlotLimit == 0) return null;
            target = engine.NamedProfile is { } resolved ? resolved.Profile : EngineRuntime.UniqueNamedProfile(engine.ServingProfiles, named);
            if (target is null || target.MaxContext < request.RequiredContextTokens) return null;
        }
        else if (request.RequiredContextTokens > 0 && engine.ServingProfiles.Count > 0)
        {
            target = SmallestFittingProfile(engine.ServingProfiles, request.RequiredContextTokens);
            if (target is null) return null;
        }
        if (named is null && loaded && target is not null && engine.ActiveContextWindow <= 0) return null;
        if (loaded && target is null && request.ExplicitContextMinimum > 0 && engine.ActiveContextWindow < request.ExplicitContextMinimum) return null;

        var needsEscalation = target is not null && (named is null ? engine.ActiveContextWindow < target.MaxContext
            : !(engine.NamedProfile?.FitsResident ?? engine.Observation.Capability.Serving.Matches(model, target)));
        if (loaded && !needsEscalation) return new(false, named is null ? null : target?.Name);
        if (!config.AutoSwitch || named is not null && !(engine.CanSwitchProfile && (engine.CanLoad || engine.Observation.Inflight == 0)))
            return null;
        if (named is null && engine.ManagedColdProfiles is { } supported)
        {
            target = request.RequiredContextTokens > 0 ? SmallestFittingProfile(supported, request.RequiredContextTokens) :
                engine.DefaultProfile is { } defaultTier ? EngineRuntime.UniqueNamedProfile(supported, defaultTier) : null;
            if (target is null) return null;
        }
        return new(true, target?.Name);
    }

    internal static ServingProfile? SmallestFittingProfile(IReadOnlyList<ServingProfile> profiles, long requiredTokens)
    {
        ServingProfile? best = null;
        foreach (var p in profiles)
        {
            if (p.MaxContext < requiredTokens) continue;
            if (best is null || p.MaxContext < best.MaxContext) best = p;
        }
        return best;
    }

    internal static double AffinityScore(EngineRuntimeInfo e)
    {
        // Affinity bonus: normalized matched-prefix chars over threshold, 0 otherwise.
        var matched = e.MatchedPrefixChars;
        return matched >= AffinityThresholdChars
            ? Math.Min(matched, 4096.0) / 4096.0 * AffinityWeight
            : 0.0;
    }

    internal static double BusyScore(EngineRuntimeInfo e, QueuedRequest request, RouterConfig config)
    {
        // Prefill impact: uncached prompt chars this request would add to the engine's work.
        var prefillMiss = Math.Max(0, request.PromptChars - e.MatchedPrefixChars);
        return e.Inflight * 32.0
               + e.InflightTokenProxy * config.Scheduler.PrefillCostAlpha
               + prefillMiss;
    }

    internal const int AffinityThresholdChars = 512;
    internal const double AffinityWeight = 1.5;
}
