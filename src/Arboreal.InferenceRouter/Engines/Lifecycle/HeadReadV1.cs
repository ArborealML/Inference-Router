using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal enum HeadReadKind { Topology = 1, Journal }

internal sealed record HeadReadScope(HeadReadKind Kind, string? Slot, HostReservationScope? Journal)
{
    internal static HeadReadScope Topology(string slot) => new(HeadReadKind.Topology, slot, null);
    internal static HeadReadScope ForJournal(HostReservationScope scope) => new(HeadReadKind.Journal, null, scope);

    internal bool IsValid() => Kind switch
    {
        HeadReadKind.Topology => IsName(Slot) && Journal is null,
        HeadReadKind.Journal => Slot is null && Journal is { } journal && IsName(journal.Slot) &&
            journal.Identity != Guid.Empty && journal.Enrollment is { Generation: > 0 } enrollment &&
            IsName(enrollment.Group),
        _ => false,
    };

    internal static bool IsName(string? value) => value is { Length: <= 256 } && !string.IsNullOrWhiteSpace(value);
}

internal enum HeadReadOutcome { Unavailable, Current, AuthenticatedAbsent }

internal sealed record HeadReadValue(HeadReadOutcome Outcome, SupervisedLaunchCommission? Topology = null,
    SupervisedLaunchJournalHead? Journal = null)
{
    internal static readonly HeadReadValue Unavailable = new(HeadReadOutcome.Unavailable);
    internal static readonly HeadReadValue Absent = new(HeadReadOutcome.AuthenticatedAbsent);

    internal bool IsEvidenceFor(HeadReadScope scope) => scope.Kind switch
    {
        HeadReadKind.Topology => IsTopologyEvidenceFor(scope),
        HeadReadKind.Journal => Topology is null &&
            (Outcome == HeadReadOutcome.AuthenticatedAbsent && Journal is null ||
             Outcome == HeadReadOutcome.Current && Journal is { Revision: >= 0 } journal &&
             journal.Scope == scope.Journal && HostMutation.IsPlanDigest(journal.Digest)),
        _ => false,
    };

    private bool IsTopologyEvidenceFor(HeadReadScope scope)
    {
        if (Outcome != HeadReadOutcome.Current || Journal is not null || scope.Slot is not { } slot ||
            Topology is not { Scope: { Enrollment.IsValid: true } reservation,
                Topology.Head: { Generation: > 0, Revision: >= 0 } head } commission ||
            reservation.Identity == Guid.Empty || string.IsNullOrWhiteSpace(reservation.Slot) ||
            !commission.AuthorizesScope(reservation, slot) || !HeadReadScope.IsName(head.Partition) ||
            !HeadReadScope.IsName(head.Daemon) || !HeadReadScope.IsName(head.RouterId) ||
            !HostMutation.IsPlanDigest(head.Digest)) return false;

        return !commission.Topology.Members.IsDefault && commission.Topology.Members.Length == 2 &&
            commission.Topology.Members.All(grant => grant is { Member: not null, Endpoint.IsAbsoluteUri: true } &&
                !string.IsNullOrWhiteSpace(grant.SocketPath) && HostMutation.IsPlanDigest(grant.PlanDigest)) &&
            !commission.Topology.Devices.IsDefault && commission.Topology.Devices.Length == 2 &&
            commission.Topology.Devices.All(device => !string.IsNullOrWhiteSpace(device)) &&
            !commission.Approvals.IsDefault && commission.Approvals.Length == 2 &&
            commission.Approvals.All(approval => approval is { Intent.Plan: not null, Image: not null } &&
                approval.HasPinnedApiVersion());
    }

    internal bool Extends(HeadReadValue previous)
    {
        // The chain's frame index is contiguous. Commissioned topology heads can skip revisions
        // when the authority persists a newer current snapshot without observing intermediate heads.
        if (Topology is { Topology.Head: { } topology } && previous.Topology is { Topology.Head: { } prior })
            return topology.Slot == prior.Slot && topology.Partition == prior.Partition &&
                topology.Daemon == prior.Daemon && topology.RouterId == prior.RouterId &&
                topology.Generation == prior.Generation && topology.Revision > prior.Revision;
        if (Journal is { } journal)
            return previous.Outcome == HeadReadOutcome.AuthenticatedAbsent ? journal.Revision == 0 :
                previous.Journal is { } priorJournal && journal.Revision > 0 &&
                journal.Revision - 1 == priorJournal.Revision;
        return false;
    }
}

internal sealed record HeadReadV1Request(string Operation, int Version, HeadReadScope Scope, string Nonce)
{
    internal bool IsValid() => Operation == HeadReadV1.Operation && Version == HeadReadV1.Version &&
        Scope is not null && Scope.IsValid() && HostMutation.IsPlanDigest(Nonce);
}

internal sealed record HeadReadV1Reply(string Operation, int Version, HeadReadScope Scope, string Nonce,
    HeadReadValue Value)
{
    internal bool Matches(HeadReadV1Request request) => Operation == request.Operation && Version == request.Version &&
        Scope == request.Scope && Nonce == request.Nonce && Value is not null && Value.IsEvidenceFor(request.Scope);
}

internal sealed record HeadReadFrame(HeadReadScope Scope, int Revision, string? Predecessor, HeadReadValue Value);

internal sealed record HeadReadFloor(HeadReadScope Scope, int Revision, string Digest);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(HeadReadV1Request))]
[JsonSerializable(typeof(HeadReadV1Reply))]
[JsonSerializable(typeof(HeadReadFrame))]
[JsonSerializable(typeof(HeadReadFloor))]
internal sealed partial class HeadReadJsonContext : JsonSerializerContext;

internal static class HeadReadV1
{
    internal const string Operation = "HeadReadV1";
    internal const int Version = 1;
    internal const int NonceBytes = 32;
    internal const int MaxRequest = 1024;
    internal const int MaxReply = 512 * 1024;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static ValueTask WriteReplyAsync(Stream stream, HeadReadV1Reply reply, CancellationToken ct) =>
        BoundedJsonFrame.WriteAsync(stream, reply, HeadReadJsonContext.Default.HeadReadV1Reply, MaxReply, ct);
}
