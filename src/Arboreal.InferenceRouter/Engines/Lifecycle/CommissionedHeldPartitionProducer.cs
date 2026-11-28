using System.Collections.Immutable;

namespace Arboreal.InferenceRouter.Lifecycle;

// The host supervisor provisions this policy in its protected exclusion domain. It governs
// coverage and the published serving generation. Occupancy and daemon effects are never fields
// in this policy. A policy record is usable only on the separately enforced dedicated partition.
internal sealed record CommissionedPartitionPolicy(HostReservationScope Scope, EnrolledPairHead Head,
    long Revision, long OwnerGeneration, long CoverageGeneration, long IngressGeneration,
    ImmutableArray<string> Devices, ImmutableArray<EnrolledPlanKey> PreflightTargets, HeldServingGeneration? Serving,
    CommissionedPartitionKernelPolicy Kernel,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<CommissionedServingRecipe> ServingRecipes = default);

internal sealed record CommissionedServingProcessRecipe(string ExecutableSha256, ImmutableArray<string> Arguments, uint Uid);

internal sealed record CommissionedServingRecipe(EnrolledPlanKey Target, string PlanDigest, string PolicyDigest,
    ServingBackendProfile Profile, ServingHostAddress Endpoint, ServingHostAddress Listener,
    CommissionedServingProcessRecipe Handler, CommissionedServingProcessRecipe Worker);

internal sealed record CommissionedPartitionKernelPolicy(Guid BootId, ServingHostLifetime Enforcer,
    ServingHostNamespace NetworkNamespace, string Cgroup, ServingHostNamespace CgroupIdentity,
    ImmutableArray<uint> DevicePrograms, ImmutableArray<uint> IngressPrograms, ImmutableArray<uint> EgressPrograms);

internal sealed record CommissionedPartitionPolicyRead(CommissionedPartitionPolicy Policy, string Digest);

internal sealed class CommissionedHeldPartitionProducer(EnrolledSuccessorScope scope, string checkpointLock,
    DockerLifecycleProvider.SuccessorCensus census, Func<ServingBrokerSnapshot?> readBroker,
    Func<CommissionedPartitionPolicyRead> readPolicy, Func<HeldServingGeneration, bool> isServingCurrent,
    Func<CommissionedPartitionPolicyRead, HostEffectCheckpointCommitment, ServingBrokerRecord,
        HostCheckpointLaunchEffect, DockerImageIdentity, CancellationToken, ValueTask<HeldServingGeneration?>> captureGeneration)
{
    private long _lastPolicyRevision;
    private string? _lastPolicyDigest;

    // The executable has one read loop. Re-read every authority after the bounded daemon
    // reads and refuse crossed observations. No process can publish a newer policy from Docker.
    internal async ValueTask<SuccessorHeldPartition?> ReadAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var installed = HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock);
            var policyRead = readPolicy();
            var policy = policyRead.Policy;
            if (!MatchesPolicy(policyRead) || readBroker() is not { } broker ||
                !MatchesBroker(broker, installed, policy)) return null;

            var state = installed.State;
            var candidateEngine = state.Phase == HostCheckpointPhase.LaunchDispatching
                ? state.Transition?.Plan.Engine : state.AppliedLaunch?.Plan.Engine;
            if (await census.ReadHeldCandidateAsync(scope.Commission.Head.RouterId, candidateEngine, ct).ConfigureAwait(false)
                    is not { } candidate ||
                !TryBindResident(state, candidate, out var resident))
                return null;

            var approval = resident is null ? null : scope.Commission.Approvals.SingleOrDefault(
                approval => approval.Launch.Intent.Plan == resident.Plan);
            if ((resident is not null && approval is null) ||
                !await census.CorroborateAsync(scope.Commission.Head.RouterId, resident, candidate.Running,
                    approval?.Launch.Image, stopped: null, ct).ConfigureAwait(false))
                return null;

            var sealedClose = broker.Record.Close;
            VerifiedInstanceRoute? route = resident is not null && broker.Record.Binding.Applied.State.AppliedLaunch == resident
                ? broker.Record.Binding.Route : null;
            HeldServingGeneration? serving = null;
            if (candidate.Running && resident is not null && approval is not null)
            {
                serving = policy.Serving is { } published && MatchesGeneration(published, installed, resident, route)
                    ? published
                    : await captureGeneration(policyRead, installed, broker.Record, resident, approval.Launch.Image, ct)
                        .ConfigureAwait(false);
                if (serving is not null && !MatchesGeneration(serving, installed, resident, route))
                    return null;
            }
            route ??= serving?.Exclusion.Binding.Route;

            // An open broker must still be serving this one certified resident. A newly
            // created or running successor can be observed while its generation is still closed.
            if (broker.Record.Open && (route is null || serving is null || !candidate.Running))
                return null;

            ct.ThrowIfCancellationRequested();
            if (HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock) != installed ||
                readPolicy().Digest != policyRead.Digest || readBroker()?.Digest != broker.Digest)
                return null;

            return new(scope.Reservation, scope.Commission.Head, policy.Revision, policy.OwnerGeneration,
                policy.CoverageGeneration, sealedClose?.Retirement.IngressGeneration ?? OpenIngressGeneration(policy, broker.Record.Binding.Route),
                IngressClosed: !broker.Record.Open, policy.Devices, resident, route, approval?.Launch.Image,
                candidate.Running, resident?.Plan.BackendRoute, approval?.Plan.Endpoint, PreflightTarget: null,
                serving, policy.PreflightTargets);
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error) || error is HttpRequestException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    private bool MatchesPolicy(CommissionedPartitionPolicyRead read)
    {
        var policy = read.Policy;
        if (policy.Scope != scope.Reservation || policy.Head != scope.Commission.Head ||
            policy.Revision <= 0 || policy.OwnerGeneration <= 0 || policy.CoverageGeneration <= 0 ||
            policy.IngressGeneration <= 0 || policy.IngressGeneration == long.MaxValue ||
            !OperationHeadProtocol.IsCanonicalDigest(read.Digest) ||
            !SupervisedLaunchCommission.SameDevices(scope.Commission.Devices, policy.Devices) ||
            policy.PreflightTargets.IsDefault ||
            policy.PreflightTargets.Distinct().Count() != policy.PreflightTargets.Length ||
            !policy.PreflightTargets.All(target => scope.Commission.Approvals.Any(approval => approval.Plan.Key == target)) ||
            policy.Revision < _lastPolicyRevision ||
            (policy.Revision == _lastPolicyRevision && read.Digest != _lastPolicyDigest))
            return false;

        _lastPolicyRevision = policy.Revision;
        _lastPolicyDigest = read.Digest;
        return true;
    }

    private bool MatchesBroker(ServingBrokerSnapshot snapshot, HostEffectCheckpointCommitment installed,
        CommissionedPartitionPolicy policy)
    {
        var checkpoint = installed.State;
        var broker = snapshot.Record;
        if (broker.Scope != scope.Reservation || broker.Binding.Scope != scope.Reservation ||
            broker.Binding.CatalogHead != scope.Commission.Head || !broker.Binding.IsValid() ||
            !OperationHeadProtocol.IsCanonicalDigest(snapshot.Digest) ||
            broker.IssuerEpoch == Guid.Empty || broker.Binding.Applied.State.AppliedLaunch is not { } prior ||
            OpenIngressGeneration(policy, broker.Binding.Route) <= 0)
            return false;

        if (broker.Close is not { } close)
            return prior == checkpoint.AppliedLaunch && checkpoint.Phase is HostCheckpointPhase.Resident or HostCheckpointPhase.StopPrepared;

        var retirement = close.Retirement;
        var beforeStop = checkpoint.Revision == close.CheckpointRevision && installed.Digest == close.CheckpointDigest &&
            HostEffectCheckpoint.BindsProposedStop(checkpoint, retirement.Stop) && checkpoint.AppliedLaunch == prior;
        return !broker.Open && (close.Conclusive || beforeStop) &&
            close.Scope == scope.Reservation && close.CatalogHead == scope.Commission.Head &&
            scope.Commission.Approvals.Any(approval => approval.Plan.Key == retirement.Successor &&
                approval.Plan.PlanDigest == retirement.PlanDigest && approval.Launch.Intent.CreateDigest == retirement.CreateDigest) &&
            retirement.Incumbent == prior && retirement.SealedRoute == broker.Binding.Route &&
            retirement.OwnerGeneration == policy.OwnerGeneration && retirement.IngressGeneration == OpenIngressGeneration(policy, broker.Binding.Route) + 1 &&
            (beforeStop || checkpoint.Transition?.Stop == retirement.Stop && checkpoint.AppliedLaunch == prior ||
                checkpoint.LastStoppedResident is { } stopped && stopped.Incumbent == prior &&
                    stopped.Effect.Matches(retirement.Stop, prior));
    }

    private static long OpenIngressGeneration(CommissionedPartitionPolicy policy, VerifiedInstanceRoute route) =>
        route.RouteIncarnation <= 0 || route.RouteIncarnation > long.MaxValue - policy.IngressGeneration
            ? 0 : policy.IngressGeneration + route.RouteIncarnation - 1;

    private static bool TryBindResident(HostEffectCheckpointState state, SuccessorCensusCandidate observed,
        out HostCheckpointLaunchEffect? resident)
    {
        resident = null;
        if (state.Phase == HostCheckpointPhase.LaunchDispatching && state.Transition is { Launch: { } operation } transition)
        {
            if (observed.InstanceId is not { } id) return true;
            resident = new(operation, transition.Plan, id);
            return resident.IsValid();
        }

        if (state.Phase is HostCheckpointPhase.StopApplied or HostCheckpointPhase.LaunchPrepared)
            return observed.InstanceId is null;

        if (state.Phase == HostCheckpointPhase.StopDispatching && observed.InstanceId is null) return true;
        if (state.Phase is not (HostCheckpointPhase.Resident or HostCheckpointPhase.StopPrepared or HostCheckpointPhase.StopDispatching) ||
            !observed.Running || state.AppliedLaunch is not { } expected || observed.InstanceId != expected.InstanceId)
            return false;

        resident = expected;
        return true;
    }

    private bool MatchesGeneration(HeldServingGeneration held, HostEffectCheckpointCommitment installed,
        HostCheckpointLaunchEffect resident, VerifiedInstanceRoute? route)
    {
        var identity = held.Exclusion.Binding;
        if (identity.Scope != scope.Reservation || identity.Head != scope.Commission.Head ||
            identity.Launch != resident.Operation || route is not null && identity.Route != route ||
            !EnrolledSuccessorScope.BindsResidentRoute(scope.Reservation, identity.Route, resident) ||
            (installed.State.Phase == HostCheckpointPhase.Resident && identity.AppliedDigest != installed.Digest) ||
            held.Profile.CanonicalModel != resident.Plan.Model || held.Profile.Profile != resident.Plan.Tier ||
            held.Profile.Context <= 0 || held.Profile.Slots <= 0) return false;

        using var pins = CommissionedServingPins.TryOpen(held.Exclusion);
        return pins is not null && isServingCurrent(held);
    }
}
