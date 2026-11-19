using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed partial class HostEffectLedgerProbe
{
    private static readonly PartitionScope EnrolledScope = new(Group, Enrollment, 4, 9, 12);
    private string PartitionPath => Path.Combine(_root, "partition-facts.json");

    private string InstallObservationFacts(string scenario)
    {
        if (scenario is not ("owned-retired" or "owned-unretired" or "owned-stale-retirement" or "vacant-retired" or
            "vacant-old-enrollment" or "vacant-old-controller" or "vacant-old-worker" or "vacant-partial" or
            "vacant-unavailable" or "vacant-unknown-owner" or "vacant-foreign"))
            throw new ArgumentException("Unknown partition facts scenario.", nameof(scenario));

        // This fixture actor sets partition facts independently of both the checkpoint and daemon state.
        using var fence = OpenFence();
        var prior = Read(PartitionPath, Json.PartitionFacts);
        var ownerGeneration = (prior?.OwnerGeneration ?? 0) + 1;
        var coverageGeneration = (prior?.CoverageGeneration ?? 0) + 1;
        var scope = scenario switch
        {
            "vacant-old-enrollment" => EnrolledScope with { EnrollmentGeneration = EnrolledScope.EnrollmentGeneration - 1 },
            "vacant-old-controller" => EnrolledScope with { ControllerGeneration = EnrolledScope.ControllerGeneration - 1 },
            "vacant-old-worker" => EnrolledScope with { WorkerGeneration = EnrolledScope.WorkerGeneration - 1 },
            _ => EnrolledScope,
        };
        var incumbent = new PartitionResident("a", "partition-a-1", 1);
        const long ingressGeneration = 7;
        var retirement = scenario == "owned-unretired" ? null : new PartitionRetirement(incumbent, scope, ownerGeneration,
            scenario == "owned-stale-retirement" ? ingressGeneration - 1 : ingressGeneration);
        var coverage = scenario == "vacant-partial" ? PartitionCoverage.Devices : PartitionCoverage.Complete;
        var facts = new PartitionFacts(scope, coverageGeneration, ownerGeneration, ingressGeneration, coverage,
            Available: scenario != "vacant-unavailable", OwnerKnown: scenario != "vacant-unknown-owner",
            Foreign: scenario == "vacant-foreign", Resident: scenario.StartsWith("owned-", StringComparison.Ordinal) ? incumbent : null,
            Retirement: retirement);
        WritePartitionFacts(facts);
        return "observed-facts-installed";
    }

    private PartitionProof? ObservePartition(HostEffectCheckpointState checkpoint, bool vacant, string mode)
    {
        var incumbent = checkpoint.AppliedLaunch!;
        var transition = checkpoint.Transition!;
        var query = new PartitionQuery(Guid.NewGuid(), EnrolledScope, checkpoint.Epoch, transition.Stop!, transition.Plan,
            new PartitionResident(incumbent.Operation.Engine, incumbent.InstanceId, incumbent.Operation.Epoch), vacant);

        using var fence = OpenFence();
        WriteOperation("partition-queries", query.Nonce, query, Json.PartitionQuery, mode);
        var lastPath = Path.Combine(_root, vacant ? "last-vacancy-proof.json" : "last-owner-proof.json");
        var facts = Read(PartitionPath, Json.PartitionFacts);
        var proof = mode == "replay-vacancy" && vacant
            ? Read(lastPath, Json.PartitionProof)
            : facts is { Available: true } ? new PartitionProof(query, facts) : null;

        if (proof is null || proof.Query != query || !ProvesPartition(query, proof.Facts)) return null;
        Publish(lastPath, proof, Json.PartitionProof, "partition-proof", mode, replace: true);
        return proof;
    }

    private bool PartitionProofStillCurrent(LedgerCommand command, PartitionProof? proof)
    {
        // Submit holds the same worker fence used by every modeled writer through daemon entry.
        if (proof is null || ReadOperation("partition-queries", proof.Query.Nonce, Json.PartitionQuery) != proof.Query ||
            Read(PartitionPath, Json.PartitionFacts) != proof.Facts || !ProvesPartition(proof.Query, proof.Facts)) return false;

        var query = proof.Query;
        if (command.Group != query.Scope.Group || command.Enrollment != query.Scope.Enrollment ||
            command.Operation.Epoch != query.Epoch) return false;

        if (!query.Vacant)
            return command.Operation == query.Stop && command.Incumbent is { } incumbent &&
                query.Previous == new PartitionResident(incumbent.Operation.Engine, incumbent.InstanceId, incumbent.Operation.Epoch);

        var effect = ReadOperation("effects", query.Stop.Id, Json.LedgerEffect);
        return command.Plan == query.Successor && query.Successor.Matches(command.Operation, query.Epoch) &&
            effect is { Stop: { } stopped } && effect.Command.Group == query.Scope.Group &&
            effect.Command.Enrollment == query.Scope.Enrollment && effect.Command.Operation == query.Stop && stopped.Operation == query.Stop &&
            query.Previous == new PartitionResident(stopped.IncumbentLaunch.Engine, stopped.InstanceId, stopped.IncumbentLaunch.Epoch);
    }

    private static bool ProvesPartition(PartitionQuery query, PartitionFacts facts) =>
        query.Nonce != Guid.Empty && query.Stop.Kind == HostMutationKind.Stop && query.Stop.Id != Guid.Empty &&
        query.Stop.Epoch == query.Epoch && query.Previous.LaunchEpoch < query.Epoch && query.Stop.Engine == query.Previous.Engine &&
        query.Stop.Target == query.Previous.InstanceId && query.Successor.IsValid() &&
        query.Scope == EnrolledScope && facts.Scope == query.Scope && facts.Available && facts.OwnerKnown && !facts.Foreign &&
        facts.Coverage == PartitionCoverage.Complete && facts.CoverageGeneration > 0 && facts.OwnerGeneration > 0 &&
        facts.IngressGeneration > 0 && (query.Vacant ? facts.Resident is null : facts.Resident == query.Previous) &&
        facts.Retirement == new PartitionRetirement(query.Previous, query.Scope, facts.OwnerGeneration, facts.IngressGeneration);

    private void RotatePartitionBeforeIssue(string mode)
    {
        if (mode is not ("coverage-aba" or "owner-aba")) return;

        using var fence = OpenFence();
        var facts = Read(PartitionPath, Json.PartitionFacts)!;
        if (mode == "coverage-aba")
        {
            WritePartitionFacts(facts with { Coverage = PartitionCoverage.Devices, CoverageGeneration = facts.CoverageGeneration + 1 });
            WritePartitionFacts(facts with { CoverageGeneration = facts.CoverageGeneration + 2 });
            return;
        }

        var owner = facts.OwnerGeneration + 2;
        WritePartitionFacts(facts with
        {
            Foreign = true, OwnerGeneration = owner - 1, Resident = new PartitionResident("other", "foreign-instance", 1),
        });
        WritePartitionFacts(facts with { OwnerGeneration = owner, Retirement = facts.Retirement! with { OwnerGeneration = owner } });
    }

    private void WritePartitionFacts(PartitionFacts facts) =>
        Publish(PartitionPath, facts, Json.PartitionFacts, "partition-facts", "partition", replace: true);
}

[Flags]
internal enum PartitionCoverage { Devices = 1, Daemon = 2, Ingress = 4, Complete = Devices | Daemon | Ingress }

internal sealed record PartitionScope(string Group, string Enrollment, long EnrollmentGeneration, long ControllerGeneration,
    long WorkerGeneration);
internal sealed record PartitionResident(string Engine, string InstanceId, long LaunchEpoch);
internal sealed record PartitionRetirement(PartitionResident Resident, PartitionScope Scope, long OwnerGeneration, long IngressGeneration);
internal sealed record PartitionFacts(PartitionScope Scope, long CoverageGeneration, long OwnerGeneration, long IngressGeneration,
    PartitionCoverage Coverage, bool Available, bool OwnerKnown, bool Foreign, PartitionResident? Resident, PartitionRetirement? Retirement);
internal sealed record PartitionQuery(Guid Nonce, PartitionScope Scope, long Epoch, HostMutation Stop, HostCheckpointPlan Successor,
    PartitionResident Previous, bool Vacant);
internal sealed record PartitionProof(PartitionQuery Query, PartitionFacts Facts);
