using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed partial class HostEffectLedgerProbe
{
    private static readonly PartitionScope EnrolledScope = new(Group, Enrollment, 4, 9, 12);
    private string PartitionPath => Path.Combine(_root, "partition-facts.json");
    private string PartitionSeedPath => Path.Combine(_root, "partition-seed.json");

    private string InstallPartitionSeed(Guid operationId)
    {
        // The parent gives the independent fixture actor the prepared A identity before daemon I/O.
        // The actor subsequently reads only this seed and its own partition record.
        using var fence = OpenFence();
        Publish(PartitionSeedPath, FixtureLaunch("a", operationId), Json.HostCheckpointLaunchEffect, "partition-seed", "partition");
        return "partition-seed-installed";
    }

    private string WriteObservationFacts(string mode, string scenario)
    {
        var result = InstallObservationFacts(scenario);
        if (mode != "pause-writer-blocked" || result != "worker-busy") return result;

        Park(mode, "writer-blocked");
        return InstallObservationFacts(scenario);
    }

    private string InstallObservationFacts(string scenario)
    {
        if (scenario is not ("owned-retired" or "owned-unretired" or "owned-stale-retirement" or "vacant-retired" or
            "owned-wrong-launch" or "vacant-wrong-launch" or
            "vacant-old-enrollment" or "vacant-old-controller" or "vacant-old-worker" or "vacant-partial" or
            "vacant-unavailable" or "vacant-unknown-owner" or "vacant-foreign"))
            throw new ArgumentException("Unknown partition facts scenario.", nameof(scenario));

        // This fixture actor sets partition facts independently of both the checkpoint and daemon state.
        using var fence = TryOpenFence();
        if (fence is null) return "worker-busy";
        var prior = Read(PartitionPath, Json.PartitionFacts);
        if (_requireCausalDrain && scenario == "vacant-retired")
        {
            if (prior is null) throw new IOException("The partition actor has no established scope.");
            WritePartitionFacts(prior with { Resident = null, Retirement = null });
            return "observed-facts-installed";
        }
        var ownerGeneration = (prior?.OwnerGeneration ?? 0) + 1;
        var coverageGeneration = (prior?.CoverageGeneration ?? 0) + 1;
        var scope = scenario switch
        {
            "vacant-old-enrollment" => EnrolledScope with { EnrollmentGeneration = EnrolledScope.EnrollmentGeneration - 1 },
            "vacant-old-controller" => EnrolledScope with { ControllerGeneration = EnrolledScope.ControllerGeneration - 1 },
            "vacant-old-worker" => EnrolledScope with { WorkerGeneration = EnrolledScope.WorkerGeneration - 1 },
            _ => EnrolledScope,
        };
        var incumbent = Read(PartitionSeedPath, Json.HostCheckpointLaunchEffect)
            ?? throw new IOException("The independent partition actor has no launch seed.");
        if (scenario is "owned-wrong-launch" or "vacant-wrong-launch")
            incumbent = incumbent with { Operation = incumbent.Operation with { Id = Guid.NewGuid() } };
        const long ingressGeneration = 7;
        var retirement = _requireCausalDrain || scenario == "owned-unretired" ? null : new PartitionRetirement(incumbent, scope, ownerGeneration,
            scenario == "owned-stale-retirement" ? ingressGeneration - 1 : ingressGeneration);
        var coverage = scenario == "vacant-partial" ? PartitionCoverage.Devices : PartitionCoverage.Complete;
        var facts = new PartitionFacts(scope, coverageGeneration, ownerGeneration, ingressGeneration, coverage,
            Available: scenario != "vacant-unavailable", OwnerKnown: scenario != "vacant-unknown-owner",
            Foreign: scenario == "vacant-foreign", Resident: scenario.StartsWith("owned-", StringComparison.Ordinal) ? incumbent : null,
            Retirement: retirement);
        WritePartitionFacts(facts);
        return "observed-facts-installed";
    }

    private PartitionProof? ObservePartition(HostEffectCheckpointState checkpoint, bool vacant, string mode,
        CausalRetirementWitness? requestedRetirement = null)
    {
        var incumbent = checkpoint.AppliedLaunch!;
        var transition = checkpoint.Transition!;
        var query = new PartitionQuery(Guid.NewGuid(), EnrolledScope, checkpoint.Epoch, transition.Stop!, transition.Plan,
            incumbent, transition.StopEffect, vacant);

        using var fence = OpenFence();
        WriteOperation("partition-queries", query.Nonce, query, Json.PartitionQuery, mode);
        var lastPath = Path.Combine(_root, vacant ? "last-vacancy-proof.json" : "last-owner-proof.json");
        var facts = Read(PartitionPath, Json.PartitionFacts);
        var frontier = _requireCausalDrain ? Read(CurrentFrontierPath, Json.CausalRetirementWitness) : null;
        if (_requireCausalDrain && (frontier is null || (!vacant && frontier != requestedRetirement))) return null;
        var replay = vacant ? mode == "replay-vacancy" : mode == "replay-owner";
        var proof = replay
            ? Read(lastPath, Json.PartitionProof)
            : facts is { Available: true } ? new PartitionProof(query, facts, frontier) : null;

        if (proof is null || proof.Query != query || !ProvesPartition(proof)) return null;
        Publish(lastPath, proof, Json.PartitionProof, "partition-proof", mode, replace: true);
        return proof;
    }

    private bool PartitionProofStillCurrent(LedgerCommand command, PartitionProof? proof)
    {
        // Submit holds the same worker fence used by every modeled writer through daemon entry.
        if (proof is null || ReadOperation("partition-queries", proof.Query.Nonce, Json.PartitionQuery) != proof.Query ||
            Read(PartitionPath, Json.PartitionFacts) != proof.Facts || !ProvesPartition(proof)) return false;
        if (_requireCausalDrain && Read(CurrentFrontierPath, Json.CausalRetirementWitness) != proof.Frontier) return false;

        var query = proof.Query;
        if (command.Group != query.Scope.Group || command.Enrollment != query.Scope.Enrollment ||
            command.Operation.Epoch != query.Epoch) return false;

        if (!query.Vacant) return command.Operation == query.Stop && command.Incumbent == query.Previous;

        var effect = ReadOperation("effects", query.Stop.Id, Json.LedgerEffect);
        return command.Plan == query.Successor && query.Successor.Matches(command.Operation, query.Epoch) &&
            effect is { Stop: { } stopped } && stopped == query.StopEffect && effect.Command.Group == query.Scope.Group &&
            effect.Command.Enrollment == query.Scope.Enrollment && effect.Command.Operation == query.Stop &&
            effect.Command.Incumbent == query.Previous && effect.Command.Plan == query.Previous.Plan;
    }

    private bool ProvesPartition(PartitionProof proof)
    {
        var (query, facts, frontier) = proof;
        return query.Nonce != Guid.Empty && query.Stop.Kind == HostMutationKind.Stop && query.Stop.Id != Guid.Empty && query.Previous.IsValid() &&
            query.Stop.Epoch == query.Epoch && query.Previous.Operation.Epoch < query.Epoch && query.Stop.Engine == query.Previous.Operation.Engine &&
            query.Stop.Target == query.Previous.InstanceId && query.Successor.IsValid() &&
            (query.Vacant ? query.StopEffect?.Matches(query.Stop, query.Previous) == true : query.StopEffect is null) &&
            query.Scope == EnrolledScope && facts.Scope == query.Scope && facts.Available && facts.OwnerKnown && !facts.Foreign &&
            facts.Coverage == PartitionCoverage.Complete && facts.CoverageGeneration > 0 && facts.OwnerGeneration > 0 &&
            facts.IngressGeneration > 0 && (query.Vacant ? facts.Resident is null : facts.Resident == query.Previous) &&
            (_requireCausalDrain ? WitnessMatches(query, facts, frontier)
                : facts.Retirement == new PartitionRetirement(query.Previous, query.Scope, facts.OwnerGeneration, facts.IngressGeneration));
    }

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
            Foreign = true, OwnerGeneration = owner - 1, Resident = FixtureLaunch("other", Guid.NewGuid()),
        });
        WritePartitionFacts(facts with { OwnerGeneration = owner, Retirement = facts.Retirement! with { OwnerGeneration = owner } });
    }

    private void WritePartitionFacts(PartitionFacts facts) =>
        Publish(PartitionPath, facts, Json.PartitionFacts, "partition-facts", "partition", replace: true);

    private string PoisonStoppedLaunch(Guid stopId)
    {
        // Deliberately contradict the already validated checkpoint effect without changing the observed partition.
        using var fence = OpenFence();
        var effect = ReadOperation("effects", stopId, Json.LedgerEffect);
        if (effect is not { Stop: { } stopped }) throw new InvalidOperationException("The fixture has no applied stop to poison.");
        var changed = stopped with { IncumbentLaunch = stopped.IncumbentLaunch with { Id = Guid.NewGuid() } };
        Publish(Path.Combine(_root, "effects", $"{stopId:N}.json"), effect with { Stop = changed }, Json.LedgerEffect,
            "effects", "partition", replace: true);
        return "stopped-launch-poisoned";
    }

    private static HostCheckpointLaunchEffect FixtureLaunch(string engine, Guid operationId)
    {
        var plan = Plan(engine);
        return new HostCheckpointLaunchEffect(
            new HostMutation(1, operationId, HostMutationKind.Launch, engine, plan.Model, plan.Tier, plan.Digest()), plan, $"partition-{engine}-1");
    }
}

[Flags]
internal enum PartitionCoverage { Devices = 1, Daemon = 2, Ingress = 4, Complete = Devices | Daemon | Ingress }

internal sealed record PartitionScope(string Group, string Enrollment, long EnrollmentGeneration, long ControllerGeneration,
    long WorkerGeneration);
internal sealed record PartitionRetirement(HostCheckpointLaunchEffect Resident, PartitionScope Scope, long OwnerGeneration, long IngressGeneration);
internal sealed record PartitionFacts(PartitionScope Scope, long CoverageGeneration, long OwnerGeneration, long IngressGeneration,
    PartitionCoverage Coverage, bool Available, bool OwnerKnown, bool Foreign, HostCheckpointLaunchEffect? Resident, PartitionRetirement? Retirement);
internal sealed record PartitionQuery(Guid Nonce, PartitionScope Scope, long Epoch, HostMutation Stop, HostCheckpointPlan Successor,
    HostCheckpointLaunchEffect Previous, HostCheckpointStopEffect? StopEffect, bool Vacant);
internal sealed record PartitionProof(PartitionQuery Query, PartitionFacts Facts, CausalRetirementWitness? Frontier = null);
