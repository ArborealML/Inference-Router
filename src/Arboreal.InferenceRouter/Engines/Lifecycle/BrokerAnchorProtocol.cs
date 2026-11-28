using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record BrokerAnchorHead(HostReservationScope Scope, long Revision, string Digest)
{
    internal bool IsValidFor(HostReservationScope scope) =>
        Scope == scope && Revision >= 0 && OperationHeadProtocol.IsCanonicalDigest(Digest);
}

internal sealed record BrokerAnchorRequest(int Version, string Purpose, string Operation, HostReservationScope Scope,
    string Nonce, BrokerAnchorHead? Predecessor, BrokerAnchorHead? Next)
{
    internal bool IsValidFor(HostReservationScope scope) =>
        Version == BrokerAnchorProtocol.Version && Purpose == BrokerAnchorProtocol.Purpose && Scope == scope &&
        OperationHeadProtocol.IsCanonicalDigest(Nonce) &&
        (Operation == OperationHeadProtocol.Read
            ? Predecessor is null && Next is null
            : Operation == OperationHeadProtocol.CompareAdvance &&
              Predecessor is { Revision: < long.MaxValue } predecessor && predecessor.IsValidFor(scope) &&
              Next is { } next && next.IsValidFor(scope) && next.Revision == predecessor.Revision + 1);
}

internal sealed record BrokerAnchorReply(BrokerAnchorRequest Request, string Outcome, BrokerAnchorHead? Head)
{
    internal bool Matches(BrokerAnchorRequest request) => Request == request &&
        (request.Operation == OperationHeadProtocol.Read
            ? Outcome == OperationHeadProtocol.Current && Head is not null && Head.IsValidFor(request.Scope)
            : Outcome == OperationHeadProtocol.Refused && Head is null ||
              Outcome == OperationHeadProtocol.Applied && Head is not null && Head == request.Next);
}

internal sealed record BrokerAnchorSnapshot(int Version, string Purpose, HostReservationScope Scope, long Revision, string Digest)
{
    internal BrokerAnchorHead Head => new(Scope, Revision, Digest);

    internal bool IsValidFor(HostReservationScope scope) =>
        Version == BrokerAnchorProtocol.Version && Purpose == BrokerAnchorProtocol.Purpose && Head.IsValidFor(scope);

    internal static BrokerAnchorSnapshot FromHead(BrokerAnchorHead head) =>
        new(BrokerAnchorProtocol.Version, BrokerAnchorProtocol.Purpose, head.Scope, head.Revision, head.Digest);
}

[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(BrokerAnchorRequest))]
[JsonSerializable(typeof(BrokerAnchorReply))]
[JsonSerializable(typeof(BrokerAnchorSnapshot))]
internal sealed partial class BrokerAnchorJsonContext : JsonSerializerContext;

internal static class BrokerAnchorProtocol
{
    internal const int Version = 1;
    internal const string Purpose = "ServingBroker";
}
