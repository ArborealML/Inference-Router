using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal enum ServingBrokerUseKind { Models = 2, State, Application }
internal enum ServingBrokerUsePhase { Reserved = 1, Issued, Retired, ProbeCompleted }

internal sealed record ServingBrokerBinding(
    HostReservationScope Scope,
    EnrolledPairHead CatalogHead,
    HostEffectCheckpointCommitment Applied,
    VerifiedInstanceRoute Route,
    string PolicyDigest,
    int TotalSlots,
    string? AppliedJournalDigest = null)
{
    internal bool IsValid() =>
        Scope.Enrollment is { } enrollment && enrollment.IsValid &&
        CatalogHead.Partition == enrollment.Group && CatalogHead.Generation == enrollment.Generation &&
        HostMutation.IsPlanDigest(CatalogHead.Digest) && HostMutation.IsPlanDigest(PolicyDigest) && TotalSlots > 0 &&
        Applied.State is { Phase: HostCheckpointPhase.Resident, AppliedLaunch: { } launch } &&
        HostMutation.IsPlanDigest(Applied.Digest) &&
        (AppliedJournalDigest is null || HostMutation.IsPlanDigest(AppliedJournalDigest)) &&
        EnrolledSuccessorScope.BindsResidentRoute(Scope, Route, launch);
}

internal sealed record ServingBrokerUse(Guid Id, VerifiedInstanceRoute Route, ServingBrokerUseKind Kind,
    Guid ExchangeId, VerifiedInstanceInvocation? Invocation, ServingBrokerUsePhase Phase,
    ServingHostAcceptedSocket? Accepted = null, ServingHostTerminalProof? Terminal = null);

internal sealed record ServingBrokerClose(
    HostReservationScope Scope,
    EnrolledPairHead CatalogHead,
    long CheckpointRevision,
    string CheckpointDigest,
    EnrolledSuccessorRetirement Retirement,
    bool Conclusive)
{
    internal bool Matches(EnrolledSuccessorQuery query, HostEffectCheckpointCommitment checkpoint) =>
        Scope == query.Scope.Reservation && CatalogHead == query.Scope.Commission.Head &&
        checkpoint.State == query.Checkpoint && checkpoint.State.Revision == CheckpointRevision &&
        checkpoint.Digest == CheckpointDigest && checkpoint.State.AppliedLaunch == Retirement.Incumbent &&
        query.ProposedStop == Retirement.Stop && HostEffectCheckpoint.BindsProposedStop(checkpoint.State, query.ProposedStop) &&
        Retirement.Approves(query.Successor) &&
        query.ExpectedIncumbentRoute == Retirement.SealedRoute && query.Id == Retirement.QueryId;
}

internal sealed record ServingBrokerRecord(int FormatVersion, HostReservationScope Scope, long Revision, string? Predecessor,
    Guid IssuerEpoch, ServingBrokerBinding Binding, bool Open, ServingBrokerClose? Close, ImmutableArray<ServingBrokerUse> Uses)
{
    internal const int CurrentFormat = 1;
}

internal sealed record ServingBrokerSnapshot(ServingBrokerRecord Record, string Digest);

// The same commissioned broker serializes access and uses an independently protected anchor.
// Unavailable or uncertain exchange is distinct from a proved CAS refusal.
internal interface IServingBrokerStore
{
    ServingBrokerSnapshot? Read();
    bool TryAdvance(ServingBrokerSnapshot predecessor, ServingBrokerRecord next);
}

[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ServingBrokerRecord))]
internal sealed partial class ServingBrokerRecordJsonContext : JsonSerializerContext;
