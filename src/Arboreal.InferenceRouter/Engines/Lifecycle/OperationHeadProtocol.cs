using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record OperationHeadRequest(int Version, string Operation, HostReservationScope Scope,
    string Nonce, SupervisedLaunchJournalHead? Predecessor, SupervisedLaunchJournalHead? Next)
{
    internal bool IsValidFor(HostReservationScope scope) =>
        Version == OperationHeadProtocol.Version && Scope == scope &&
        OperationHeadProtocol.IsCanonicalDigest(Nonce) &&
        (Operation == OperationHeadProtocol.Read
            ? Predecessor is null && Next is null
            : Operation == OperationHeadProtocol.CompareAdvance &&
              Next is not null && OperationHeadProtocol.IsHeadFor(Next, scope) &&
              (Predecessor is null || OperationHeadProtocol.IsHeadFor(Predecessor, scope)));
}

internal sealed record OperationHeadReply(OperationHeadRequest Request, string Outcome, SupervisedLaunchJournalHead? Head)
{
    internal bool Matches(OperationHeadRequest request) => Request == request &&
        (request.Operation == OperationHeadProtocol.Read
            ? Outcome == OperationHeadProtocol.Absent && Head is null ||
              Outcome == OperationHeadProtocol.Current && Head is not null && OperationHeadProtocol.IsHeadFor(Head, request.Scope)
            : Outcome == OperationHeadProtocol.Refused && Head is null ||
              Outcome == OperationHeadProtocol.Applied && Head is not null && Head == request.Next);
}

internal sealed record OperationHeadSnapshot(int Version, HostReservationScope Scope, string Kind, int? Revision, string? Digest)
{
    internal bool IsValidFor(HostReservationScope scope) => Version == OperationHeadProtocol.Version && Scope == scope &&
        (Kind == OperationHeadProtocol.Absent && Revision is null && Digest is null ||
         Kind == OperationHeadProtocol.Current && Revision is >= 0 and <= SupervisedLaunchJournal.LastRevision &&
         OperationHeadProtocol.IsCanonicalDigest(Digest));

    internal SupervisedLaunchJournalHead? Head => Kind == OperationHeadProtocol.Absent
        ? null : new(Scope, Revision!.Value, Digest!);

    internal static OperationHeadSnapshot Current(SupervisedLaunchJournalHead head) =>
        new(OperationHeadProtocol.Version, head.Scope, OperationHeadProtocol.Current, head.Revision, head.Digest);
}

[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(OperationHeadRequest))]
[JsonSerializable(typeof(OperationHeadReply))]
internal sealed partial class OperationHeadWireJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(OperationHeadSnapshot))]
internal sealed partial class OperationHeadJsonContext : JsonSerializerContext;

internal static class OperationHeadProtocol
{
    internal const int Version = 1;
    internal const int NonceBytes = 32;
    internal const int MaxPayload = 4 * 1024;
    internal const string Read = "Read";
    internal const string CompareAdvance = "CompareAdvance";
    internal const string Absent = "Absent";
    internal const string Current = "Current";
    internal const string Refused = "Refused";
    internal const string Applied = "Applied";
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static bool IsScopeValid(HostReservationScope scope) => HeadReadScope.ForJournal(scope).IsValid();

    internal static bool IsCanonicalDigest(string? value) => value is { Length: NonceBytes * 2 } &&
        value.All(static digit => digit is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static bool IsHeadFor(SupervisedLaunchJournalHead value, HostReservationScope scope) =>
        value.Scope == scope && value.Revision is >= 0 and <= SupervisedLaunchJournal.LastRevision &&
        IsCanonicalDigest(value.Digest);
}
