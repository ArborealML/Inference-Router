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
    public required int Inflight { get; init; }
    /// <summary>Estimated token-proxy load: sum of prompt chars + expected output of in-flight.</summary>
    public required long InflightTokenProxy { get; init; }
    public required bool Healthy { get; init; }
    public required bool Switching { get; init; }
    /// <summary>Matched-prefix extent (normalized chars) this engine holds for the candidate request.</summary>
    public required long MatchedPrefixChars { get; init; }
}

/// <summary>Decision: engine name (null = wait) or rejection.</summary>
public sealed record SchedulerDecision
{
    public string? Engine { get; init; }
    public string? RejectReason { get; init; }
    public bool SwitchRequired { get; init; }
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
/// nothing healthy has it loaded.
/// </summary>
public sealed class PrefixAwareScheduler : IRequestScheduler
{
    public SchedulerDecision SelectNext(QueuedRequest request, SchedulerView view, RouterConfig config)
    {
        if (view.Engines.Count == 0)
            return SchedulerDecision.Wait("no_engines");

        var scored = new List<(EngineRuntimeInfo E, double Score, bool Loaded)>();
        var loadable = new List<(EngineRuntimeInfo E, double Score)>();
        foreach (var e in view.Engines)
        {
            if (!e.Healthy || e.Switching) continue;
            var loaded = e.LoadedModels.Contains(request.Request.Model, StringComparer.OrdinalIgnoreCase);
            var catalog = e.CatalogModels.Contains(request.Request.Model, StringComparer.OrdinalIgnoreCase);
            if (!loaded && !catalog) continue;

            var affinity = AffinityScore(e);
            var busy = BusyScore(e, request, config);
            var score = affinity - busy;
            if (loaded) scored.Add((e, score, true));
            else if (config.AutoSwitch) loadable.Add((e, score));
        }

        if (scored.Count > 0)
        {
            var best = Best(scored.Select(s => (s.E, s.Score)));
            return new SchedulerDecision { Engine = best.Name, SwitchRequired = false };
        }
        if (loadable.Count > 0)
        {
            var best = Best(loadable.Select(s => (s.E, s.Score)));
            return new SchedulerDecision { Engine = best.Name, SwitchRequired = true };
        }
        // Everything healthy is mid-switch or unhealthy: wait.
        return SchedulerDecision.Wait("no_available_engine");

        static EngineRuntimeInfo Best(IEnumerable<(EngineRuntimeInfo E, double Score)> seq)
        {
            EngineRuntimeInfo? best = null;
            var bestScore = double.NegativeInfinity;
            foreach (var s in seq)
            {
                if (best is null || s.Item2 > bestScore || (s.Item2 == bestScore &&
                    string.CompareOrdinal(s.E.Name, best.Name) < 0))
                {
                    best = s.E;
                    bestScore = s.Item2;
                }
            }
            return best ?? throw new InvalidOperationException("no candidates");
        }
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
