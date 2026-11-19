using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal enum HostCheckpointPhase
{
    Genesis, Resident, StopPrepared, StopDispatching, StopApplied, LaunchPrepared, LaunchDispatching,
    RetainedBeforeDispatch, RetainedAfterRejection,
}

internal sealed record HostCheckpointPlan(string Engine, string Model, string? Tier, string Configuration,
    int ReadinessTimeoutMs, string BackendRoute, string LaunchSettingsDigest)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Engine) && !string.IsNullOrWhiteSpace(Model) &&
        (Tier is null || !string.IsNullOrWhiteSpace(Tier)) && !string.IsNullOrWhiteSpace(Configuration) &&
        ReadinessTimeoutMs > 0 && !string.IsNullOrWhiteSpace(BackendRoute) && HostMutation.IsPlanDigest(LaunchSettingsDigest);

    public string Digest() => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(this, HostEffectCheckpointJsonContext.Default.HostCheckpointPlan)));

    public bool Matches(HostMutation operation, long epoch) => IsValid() &&
        operation is { Kind: HostMutationKind.Launch } && operation.Id != Guid.Empty && operation.Epoch == epoch &&
        operation.Engine == Engine && operation.Target == Model && operation.Profile == Tier && operation.PlanDigest == Digest();
}

internal sealed record HostCheckpointLaunchEffect(HostMutation Operation, HostCheckpointPlan Plan, string InstanceId)
{
    public bool IsValid() => Operation is { Epoch: > 0 } && Plan is not null &&
        Plan.Matches(Operation, Operation.Epoch) && !string.IsNullOrWhiteSpace(InstanceId);
}

internal sealed record HostCheckpointStopEffect(HostMutation Operation, HostMutation IncumbentLaunch, string InstanceId)
{
    public bool Matches(HostMutation stop, HostCheckpointLaunchEffect incumbent) =>
        Operation == stop && IncumbentLaunch == incumbent.Operation && InstanceId == incumbent.InstanceId;
}

// In the fixture this evidence comes from the authority that can exclude delayed operation delivery.
internal readonly record struct HostCheckpointBeforeIssueFence(HostMutation Operation);

internal sealed record HostCheckpointStoppedResident(HostCheckpointLaunchEffect Incumbent, HostCheckpointStopEffect Effect);

internal sealed record HostCheckpointTransition(HostCheckpointPlan Plan, HostMutation? Stop = null,
    HostCheckpointStopEffect? StopEffect = null, HostMutation? Launch = null);

internal sealed record HostEffectCheckpointState([property: JsonRequired] int Version, [property: JsonRequired] long Revision,
    [property: JsonRequired] long Epoch, [property: JsonRequired] HostCheckpointPhase Phase,
    HostCheckpointLaunchEffect? AppliedLaunch = null, HostCheckpointTransition? Transition = null,
    HostCheckpointStoppedResident? LastStoppedResident = null)
{
    public const int FormatVersion = 1;
    public static HostEffectCheckpointState Genesis { get; } = new(FormatVersion, 0, 0, HostCheckpointPhase.Genesis);

    public bool IsValid()
    {
        if (Phase == HostCheckpointPhase.Genesis) return this == Genesis;
        if (Version != FormatVersion || Epoch <= 0 || Revision < Epoch) return false;
        if (AppliedLaunch is { } resident && (!resident.IsValid() || resident.Operation.Epoch > Epoch)) return false;
        if (!HasValidStopHistory()) return false;
        if (Phase == HostCheckpointPhase.Resident) return AppliedLaunch?.Operation.Epoch == Epoch && Transition is null;
        if (Transition is not { Plan: { } plan } transition || !plan.IsValid()) return false;

        if (AppliedLaunch is not { } incumbent)
            return Epoch == 1 && transition is { Stop: null, StopEffect: null, Launch: { } initialLaunch } &&
                IsLaunchInProgress() && plan.Matches(initialLaunch, Epoch);

        if (transition.Stop is not { } stop || !MatchesStop(stop, incumbent, Epoch) || plan == incumbent.Plan) return false;
        if (Phase is HostCheckpointPhase.StopPrepared or HostCheckpointPhase.StopDispatching or
            HostCheckpointPhase.RetainedBeforeDispatch or HostCheckpointPhase.RetainedAfterRejection)
            return transition is { StopEffect: null, Launch: null };

        if (transition.StopEffect?.Matches(stop, incumbent) != true) return false;
        if (Phase == HostCheckpointPhase.StopApplied) return transition.Launch is null;
        return IsLaunchInProgress() && transition.Launch is { } launch && launch.Id != stop.Id && plan.Matches(launch, Epoch);
    }

    private bool IsLaunchInProgress() => Phase is HostCheckpointPhase.LaunchPrepared or HostCheckpointPhase.LaunchDispatching;

    private bool HasValidStopHistory()
    {
        if (AppliedLaunch is not { Operation.Epoch: > 1 } resident) return LastStoppedResident is null;

        return LastStoppedResident is { Incumbent: { } incumbent, Effect: { Operation: { } stop } effect } &&
            incumbent.IsValid() && MatchesStop(stop, incumbent, resident.Operation.Epoch) && effect.Matches(stop, incumbent) &&
            resident.Plan != incumbent.Plan && resident.Operation.Id != stop.Id && resident.Operation.Id != incumbent.Operation.Id;
    }

    private static bool MatchesStop(HostMutation stop, HostCheckpointLaunchEffect incumbent, long epoch) =>
        stop is { Kind: HostMutationKind.Stop, Profile: null, PlanDigest: null } && stop.Id != Guid.Empty &&
        stop.Id != incumbent.Operation.Id && stop.Epoch == epoch && stop.Epoch > incumbent.Operation.Epoch &&
        stop.Engine == incumbent.Operation.Engine && stop.Target == incumbent.InstanceId;
}

[JsonSerializable(typeof(HostEffectCheckpointState))]
[JsonSerializable(typeof(HostCheckpointPlan))]
internal sealed partial class HostEffectCheckpointJsonContext : JsonSerializerContext;
