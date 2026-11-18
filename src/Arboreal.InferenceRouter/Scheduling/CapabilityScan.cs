namespace Arboreal.InferenceRouter;

/// <summary>
/// Everything the router is entitled to claim about one engine's capability, as a single immutable value
/// (§5.5, "a model nothing here can serve"). Health, the set of models the engine reports serving, and the
/// model a switch is in flight toward are not three independent readings: taken separately they can be
/// spliced across an update and produce a capability answer that was true at no single instant — which,
/// for a refusal, means telling a caller that nothing here could ever serve a model something here was
/// serving throughout. One snapshot per engine makes each engine's contribution atomic; <see cref="Version"/>
/// rides inside it so a reader can prove nothing was published while it was deciding.
/// </summary>
/// <param name="Version">Monotonic per engine: every publication of any field below increments it, so an
/// unchanged version across a scan means that engine said the same thing the whole time.</param>
/// <param name="Healthy">The last model probe answered. False means "we do not know", never "it cannot".</param>
/// <param name="SwitchingToward">Model a switch is in flight toward, or null. A switch that has started but
/// not landed is not an absence: the model will be there.</param>
/// <param name="LoadedModels">Models the engine itself reported serving at <see cref="Version"/>. Never
/// mutated after publication — a snapshot owns its array.</param>
public sealed record EngineCapability(
    long Version,
    bool Healthy,
    string? SwitchingToward,
    string[] LoadedModels)
{
    /// <summary>Did the switch that raised the current marker author this engine's unknown health? One
    /// <see cref="Healthy"/> flag cannot say both "we do not know" and "who made us not know", and the
    /// difference decides whether a waiter keeps a proof its own switch published: a marker raised by a later
    /// claim withdrew the health as the <i>price of starting a transaction</i>, which contradicts nothing about
    /// what the engine serves, while a probe negative or a switch exit is an <i>observation</i> that does
    /// (§5.5, TASK-61 AC #7-#8). True only for the claim that set <see cref="SwitchingToward"/> and only when
    /// health was true when that claim started; every other author of a health change — a positive probe, a
    /// switch proof, a probe negative that was allowed to publish, a switch exit, a marker clear — clears it.
    /// Internal, and on no wire surface (decision-41).</summary>
    internal bool HealthFromClaim { get; init; }

    /// <summary>An engine before its first probe: nothing known, nothing refused.</summary>
    public static readonly EngineCapability Initial = new(0, false, null, []);
}

/// <summary>The capability question as the scan needs it: one engine's snapshot, plus what its configured
/// catalog declares (static, so it does not belong in the snapshot). Implemented by
/// <see cref="EngineRuntime"/>; faked in tests so the scan's coherence rule can be pinned deterministically
/// instead of by racing a real probe.</summary>
internal interface ICapabilitySource
{
    EngineCapability Capability { get; }
    bool DeclaresModel(string model);
}

/// <summary>Is this model hopeless here — could no engine in the configuration ever take it? That is a
/// refusal, so the rule is deliberately one-way: anything unknown (an engine that has never answered the
/// model probe, a switch still running) or any doubt about the snapshot answers "not hopeless" and the
/// request waits. Refusing on a moving picture is the failure this class exists to prevent (§5.5).</summary>
internal static class CapabilityScan
{
    /// <summary>A scan that catches a publication mid-pass retries once; a second unstable scan is doubt,
    /// and doubt never refuses.</summary>
    private const int MaxAttempts = 2;

    /// <summary>True only when every engine has answered its probe, none declares the model in its catalog,
    /// none reports it, none is switching toward it, and no engine published anything while the scan was
    /// running. Callers that act on true must still claim the ticket (remove it from its queue) before
    /// failing it: this is a statement about capability, not about queue ownership.</summary>
    public static bool DefinitelyHopeless(IReadOnlyList<ICapabilitySource> engines, string model)
    {
        // Vacuous truth is still a lie: with no engines there is nothing to have answered, so nothing is
        // hopeless. Config validation forbids zero engines, and this stays as the guard rather than a
        // caller-side assumption.
        if (engines.Count == 0) return false;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var stampBefore = Stamp(engines);
            foreach (var e in engines)
            {
                // One read of one snapshot per engine: this engine's whole answer is from a single instant.
                var cap = e.Capability;
                if (!cap.Healthy) return false;
                if (cap.SwitchingToward is { } toward
                    && string.Equals(toward, model, StringComparison.OrdinalIgnoreCase)) return false;
                if (e.DeclaresModel(model)) return false;
                foreach (var name in cap.LoadedModels)
                {
                    // Case-insensitive to match the scheduler, which has always compared model names this
                    // way; a catalog-only fast path that were ordinal would answer a different question.
                    if (string.Equals(name, model, StringComparison.OrdinalIgnoreCase)) return false;
                }
            }

            // Every version unchanged across the whole pass ⇒ no engine published during it (versions only
            // ever increment), so "all healthy, none can serve" is one state, not a splice of two.
            if (Stamp(engines) == stampBefore) return true;
        }

        return false;
    }

    /// <summary>Sum of the per-engine versions. Monotone non-negative addends make an unchanged sum an exact
    /// "no publication happened" test — a sum can only stay the same if every addend did. Overflow would
    /// defeat it, and would need ~2.9e11 publications per second for a century: not a live risk, and the
    /// failure direction is a retry, then a wait, never a refusal.</summary>
    private static long Stamp(IReadOnlyList<ICapabilitySource> engines)
    {
        var sum = 0L;
        for (var i = 0; i < engines.Count; i++) sum += engines[i].Capability.Version;
        return sum;
    }
}
