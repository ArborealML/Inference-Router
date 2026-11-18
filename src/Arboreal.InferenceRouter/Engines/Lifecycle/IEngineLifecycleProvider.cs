namespace Arboreal.InferenceRouter.Lifecycle;

/// <summary>One model on one engine, as a lifecycle provider sees it. Deliberately narrow: a provider
/// is told what to make true, never how the router chose it.</summary>
public sealed record LifecycleTarget(
    string EngineName,
    string Model,
    ServingProfile? Profile,
    EngineModelDefinition Definition);

/// <summary>A container (or equivalent unit) a provider found for this engine. <see cref="OwnedByUs"/>
/// is the ownership proof: it is true only when the unit carries this launcher's labels, and the
/// coordinator will never hand an unowned unit to <see cref="IEngineLifecycleProvider.StopAsync"/>.</summary>
public sealed record EnginePeer(string Id, string Image, string? Model, bool OwnedByUs, bool QueryFailed = false);

/// <summary>Outcome of a lifecycle step. <see cref="Reason"/> is the short machine-readable form that
/// goes on the event feed; <see cref="Action"/> is one sentence telling the operator what to do, and a
/// failure must carry exactly one of them rather than a stack of near-duplicates.</summary>
public sealed class LifecycleResult
{
    public required bool Succeeded { get; init; }
    public string? Reason { get; init; }
    public string? Action { get; init; }

    public static LifecycleResult Ok(string? reason = null) => new() { Succeeded = true, Reason = reason };

    public static LifecycleResult Fail(string reason, string action) =>
        new() { Succeeded = false, Reason = reason, Action = action };

    public override string ToString() =>
        Succeeded ? $"ok{(Reason is null ? "" : $" {Reason}")}" : $"{Reason} — {Action}";
}

/// <summary>The seam between the router and whatever can put a model into an engine. Every method is a
/// question or a mutation; the coordinator's ordering is what makes the mutations safe, so providers
/// must not do ordering work of their own.</summary>
public interface IEngineLifecycleProvider
{
    /// <summary>"exec" or "docker". Logged, and used to reject a config that asks for two providers on
    /// one model.</summary>
    string Kind { get; }

    /// <summary>Validate only: starts nothing, stops nothing, writes nothing. Runs before any mutation
    /// so a refusal costs the engine nothing.</summary>
    ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct);

    /// <summary>What already exists for this engine, with the ownership proof attached.</summary>
    ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct);

    /// <summary>Stop the listed units, all of which the coordinator proved we own.</summary>
    ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct);

    /// <summary>Start the exact target: this model, this profile, this pinned image. A provider may
    /// never substitute a different image or pull one that is not already present.</summary>
    ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct);

    /// <summary>How the router proves the model is actually serving after a launch. Pure — no I/O — and
    /// provider-specific, because "launched" means different things to a selector program and to an
    /// image that has to cold-start a loader.</summary>
    ReadinessPolicy Readiness(LifecycleTarget target);
}

/// <summary>Whether the router must see the model serving before it calls a switch good, and how long
/// it will wait. A false <see cref="ProveHealth"/> skips the readiness *rule*; it never removes the
/// requirement that the engine's own <c>/models</c> answer name the model, because a switch the engine
/// cannot confirm is refused rather than served (§5.5). The setting is a promise the operator made in
/// config about how long the router waits, not a fallback the router may choose for itself.</summary>
public sealed record ReadinessPolicy(bool ProveHealth, TimeSpan Timeout);
