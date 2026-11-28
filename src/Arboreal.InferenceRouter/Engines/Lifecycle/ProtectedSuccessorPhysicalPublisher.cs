using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

// This is the scope guard's local readback of the actual held partition. It carries no
// writer receipts or Docker request bytes. Effects come from the separate ingress audit.
internal sealed record SuccessorHeldPartition(HostReservationScope Scope, EnrolledPairHead Catalog,
    long Revision, long OwnerGeneration, long CoverageGeneration, long IngressGeneration, bool IngressClosed,
    ImmutableArray<string> CoveredDevices, HostCheckpointLaunchEffect? Resident, VerifiedInstanceRoute? ResidentRoute,
    DockerImageIdentity? ResidentImage, bool ResidentRunning, string? ResidentBackendRoute, Uri? ResidentEndpoint,
    EnrolledPlanKey? PreflightTarget, HeldServingGeneration? Serving = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<EnrolledPlanKey> PreflightTargets = default);

// Produced alongside the held partition, for an exact commissioned runtime generation.
// Serving state is useful only while that same generation remains under supervisor exclusion.
internal sealed record HeldServingGeneration(ServingHostExclusion Exclusion, ServingBackendProfile Profile)
{
    internal bool Matches(EnrolledSuccessorScope scope, CheckedEnrolledTarget target, ServingBackendIdentity identity) =>
        Exclusion is { } exclusion && Profile is { } profile && exclusion.IsValid() &&
        exclusion.Binding == identity && identity.Scope == scope.Reservation &&
        identity.Head == scope.Commission.Head && identity.Launch.PlanDigest == target.PlanDigest &&
        target.Target.Matches(identity.Route) && scope.Approves(target) &&
        profile.CanonicalModel == target.Key.Model && profile.Profile == target.Key.Profile &&
        profile.Context > 0 && profile.Slots > 0;
}

internal sealed class ProtectedSuccessorPhysicalPublisher(EnrolledSuccessorScope scope, string checkpointLock,
    ProtectedSuccessorDaemonAudit audit, Func<CancellationToken, ValueTask<SuccessorHeldPartition?>> readHeldPartition)
{
    internal async ValueTask<SuccessorPhysicalSnapshot?> ReadAsync(CancellationToken ct)
    {
        try
        {
            var installed = HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock);
            var partition = await readHeldPartition(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (partition is null || partition.Scope != scope.Reservation || partition.Catalog != scope.Commission.Head ||
                partition.Revision <= 0 || partition.OwnerGeneration <= 0 || partition.CoverageGeneration <= 0 ||
                partition.IngressGeneration <= 0 ||
                !SupervisedLaunchCommission.SameDevices(scope.Commission.Devices, partition.CoveredDevices)) return null;

            var stop = ReadStop(installed.State);
            var (create, start) = ReadLaunch(installed.State, partition);
            ct.ThrowIfCancellationRequested();
            if (HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock) != installed) return null;
            var sample = new SuccessorPhysicalSnapshot(partition.Scope, partition.Catalog, partition.Revision,
                Digest: string.Empty, partition.OwnerGeneration, partition.CoverageGeneration, partition.IngressGeneration,
                Exclusive: true, partition.IngressClosed, partition.CoveredDevices, partition.Resident,
                partition.ResidentRoute, partition.ResidentImage, partition.ResidentRunning,
                partition.PreflightTarget, create, start, stop,
                partition.PreflightTargets.IsDefault ? [] : partition.PreflightTargets);
            var payload = JsonSerializer.SerializeToUtf8Bytes(sample, SuccessorPhysicalWitnessJson.Default.SuccessorPhysicalSnapshot);
            return sample with { Digest = Convert.ToHexString(SHA256.HashData(payload)) };
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    private HostCheckpointStopEffect? ReadStop(HostEffectCheckpointState installed)
    {
        if (installed is not { AppliedLaunch: { } incumbent, Transition.Stop: { } operation } ||
            audit.ReadAccepted(operation, SuccessorDaemonIngressKind.Stop) is not { } accepted ||
            !Matches(accepted, operation, incumbent.InstanceId)) return null;
        return new(operation, incumbent.Operation, accepted.InstanceId);
    }

    private (SupervisedObservedCreate? Create, SupervisedObservedStart? Start) ReadLaunch(
        HostEffectCheckpointState installed, SuccessorHeldPartition partition)
    {
        if (installed is not { Phase: HostCheckpointPhase.LaunchDispatching, Transition.Launch: { } operation } ||
            partition is not { Resident: { } resident, ResidentImage: { } image, ResidentBackendRoute: { } route,
                ResidentEndpoint: { } endpoint } || resident.Operation != operation ||
            audit.ReadAccepted(operation, SuccessorDaemonIngressKind.Create) is not { } created ||
            !Matches(created, operation, resident.InstanceId)) return default;

        var create = new SupervisedObservedCreate(created.Intent.Command, created.InstanceId, created.Intent.Body, image,
            EffectiveSettings: null, route, endpoint, partition.CoveredDevices);
        var started = partition.ResidentRunning ? audit.ReadAccepted(operation, SuccessorDaemonIngressKind.Start) : null;
        return (create, started is not null && Matches(started, operation, resident.InstanceId) &&
            started.Intent.Command == created.Intent.Command
                ? new SupervisedObservedStart(started.Intent.Command, started.InstanceId, Running: true) : null);
    }

    private bool Matches(SuccessorDaemonIngressEffect accepted, HostMutation operation, string instance) =>
        accepted.Intent.Command.Scope == scope.Reservation && accepted.Intent.Command.Head == scope.Commission.Head &&
        accepted.Intent.Command.Operation == operation && accepted.InstanceId == instance;
}
