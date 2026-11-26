using System.Collections.Immutable;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal enum NativeLaunchHealth { Pending, Eligible, Unknown, Invalid }

internal sealed record NativeLaunchWatch(SwitchAttemptId Attempt, NativeLaunch Launch, NativeContainerStamp? Baseline,
    NativeLaunchHealth Health, VllmProcessStamp? ServingProcess = null);

internal readonly record struct ServingCapacity(string? Model, long ContextWindow, int TotalSlots, string? Profile)
{
    /// <summary>Does this engine's measured serving state already satisfy the declared tier? The proof is the
    /// window measured for this exact model: it must equal the window the tier declares. The recorded label is
    /// not evidence — §7.6 makes it a display inference, it is absent whenever more than one declared tier
    /// matches the observed pair, and it goes stale across a reload or a hand change. Comparing it could stop
    /// and recreate a container that already serves the requested window, which is the outage this contains.
    /// The direction stays one-way: a window measured narrower or wider than the tier declares is not that tier,
    /// and a measured capacity never rewrites a named profile. Slot count is a floor, not an identity: an
    /// eight-lane resident satisfies a four-lane tier of the same window, while fewer reported lanes than the
    /// tier declares is a mismatch, so a named tier is never silently run short of the concurrency it promises
    /// (and the same gauge caps admission — §7.6 Admission). This is the question *"does an engine that is
    /// already up owe a switch?"*, asked by the scheduler's escalation rule, the local group's cold/warm
    /// classification and the named-take gates on the dispatch path; the answer for what a launch may publish
    /// is <see cref="MatchesExactly"/>. <c>MaxContext &gt; 0</c> is what rules out a
    /// splice: an unread gauge or a window bound to another model row reads as zero through
    /// <see cref="WindowFor"/>, and zero must never satisfy a tier that declares no window.</summary>
    internal bool Matches(string model, ServingProfile profile) =>
        profile.MaxContext > 0 && WindowFor(model) == profile.MaxContext && TotalSlots >= profile.MaxSlots;

    /// <summary>Is the observed pair exactly the tier that was asked for? This is the launch publication proof
    /// (§5.3.1): an instance may only be certified as serving a named tier when it measures that tier's declared
    /// window AND its declared lane count. Extra lanes are as much a mismatch as missing ones, because the
    /// instance that came up is not the one the launch specified — certifying it as such would publish a Ready
    /// verdict, a completion event and a dispatch proof for a capacity nobody asked the provider to start.
    /// Satisfaction (<see cref="Matches"/>) deliberately reads the same observation the other way: extra lanes
    /// are enough of a tier that is already running, which is what keeps a resident from being stopped and
    /// recreated. So the two predicates differ only in the lane comparison, and only in who asks: the launch
    /// publication gates use this one; the switch-necessity, admission and readiness-observation gates use the
    /// other.</summary>
    internal bool MatchesExactly(string model, ServingProfile profile) =>
        profile.MaxContext > 0 && WindowFor(model) == profile.MaxContext && TotalSlots == profile.MaxSlots;

    public long WindowFor(string model) =>
        string.Equals(Model, model, StringComparison.OrdinalIgnoreCase) ? ContextWindow : 0;
}

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
/// <param name="LoadedModels">Models the engine reported serving. The snapshot copies this array.</param>
public sealed record EngineCapability(
    long Version,
    bool Healthy,
    string? SwitchingToward,
    string[] LoadedModels)
{
    private readonly ImmutableList<string> _modelRoster = ImmutableList.CreateRange(LoadedModels);

    /// <summary>A copy of the roster in the original public record shape.</summary>
    public string[] LoadedModels
    {
        get => _modelRoster.ToArray();
        init
        {
            _modelRoster = ImmutableList.CreateRange(value);
            ModelContextWindows = NoModelContextWindows;
            Serving = default;
            SwitchProof = null;
        }
    }

    internal ImmutableList<string> ModelRoster
    {
        get => _modelRoster;
        init => _modelRoster = value;
    }

    internal static readonly ImmutableDictionary<string, long> NoModelContextWindows =
        ImmutableDictionary.Create<string, long>(StringComparer.OrdinalIgnoreCase);

    // Each value came from its identified model row in the observation that published this roster.
    internal ImmutableDictionary<string, long> ModelContextWindows { get; init; } = NoModelContextWindows;

    internal ServingCapacity Serving { get; init; }
    internal SwitchServingProof? SwitchProof { get; init; }
    internal NativeLaunchWatch? NativeWatch { get; init; }

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
                foreach (var name in cap.ModelRoster)
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
