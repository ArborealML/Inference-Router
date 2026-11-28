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

// Within the bounded window, admissions can reach the broker out of scheduling order.
// Any sequence outside the window is already too old to authorize a new physical use.
internal sealed record ServingBrokerApplicationReplay(long HighWater, ImmutableArray<ulong> Used)
{
    private const int Window = 1024;

    private const int WordBits = 64;
    private const int Words = Window / WordBits;

    internal static readonly ServingBrokerApplicationReplay Empty = new(0, [.. new ulong[Words]]);

    internal bool IsValid() => HighWater >= 0 && !Used.IsDefault && Used.Length == Words &&
        (HighWater == 0 || Contains(HighWater));

    internal ServingBrokerApplicationReplay? Record(long sequence)
    {
        if (sequence <= Math.Max(HighWater - Window, 0) || sequence <= HighWater && Contains(sequence))
            return null;
        var high = Math.Max(sequence, HighWater);
        var words = new ulong[Words];
        if (high - HighWater < Window)
        {
            Used.CopyTo(words);
            for (var offset = 1L; offset <= high - HighWater; offset++)
                Set(words, HighWater + offset, set: false);
        }

        Set(words, sequence, set: true);
        return new(high, [.. words]);
    }

    private bool Contains(long sequence) =>
        (Used[Word(sequence)] & Bit(sequence)) != 0;

    private static void Set(ulong[] words, long sequence, bool set)
    {
        var word = Word(sequence);
        var bit = Bit(sequence);
        if (set) words[word] |= bit;
        else words[word] &= ~bit;
    }

    private static int Word(long sequence) => (int)((sequence % Window) / WordBits);
    private static ulong Bit(long sequence) => 1UL << (int)(sequence % WordBits);
}

internal sealed record ServingBrokerRecord(int FormatVersion, HostReservationScope Scope, long Revision, string? Predecessor,
    Guid IssuerEpoch, ServingBrokerBinding Binding, bool Open, ServingBrokerClose? Close, ImmutableArray<ServingBrokerUse> Uses,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ServingBrokerApplicationReplay? ApplicationReplay = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Guid ApplicationWriter = default)
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
