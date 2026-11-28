using System.Net.Sockets;
using System.Security.Cryptography;

namespace Arboreal.InferenceRouter.Lifecycle;

// The broker uses a single fixed purpose on the existing OperationHead authority.
// A reader can never issue CAS, even if its local process can reach the writer socket.
internal sealed class OperationHeadServingBrokerAnchor : IServingBrokerAnchor
{
    private readonly HostReservationScope _scope;
    private readonly UnixDomainSocketEndPoint _endpoint;
    private readonly string _pinnedLeaf;
    private readonly bool _canAdvance;

    private OperationHeadServingBrokerAnchor(HostReservationScope scope, UnixDomainSocketEndPoint endpoint,
        string pinnedLeaf, bool canAdvance)
    {
        if (!OperatingSystem.IsLinux() || !OperationHeadProtocol.IsScopeValid(scope) ||
            !OperationHeadProtocol.IsCanonicalDigest(pinnedLeaf))
            throw new ArgumentException("The broker anchor requires the commissioned scope and authority pin.", nameof(scope));
        _scope = scope;
        _endpoint = endpoint;
        _pinnedLeaf = pinnedLeaf;
        _canAdvance = canAdvance;
    }

    internal static OperationHeadServingBrokerAnchor ForReader(HostReservationScope scope,
        UnixDomainSocketEndPoint endpoint, string pinnedLeaf) => new(scope, endpoint, pinnedLeaf, canAdvance: false);

    internal static OperationHeadServingBrokerAnchor ForWriter(HostReservationScope scope,
        UnixDomainSocketEndPoint endpoint, string pinnedLeaf) => new(scope, endpoint, pinnedLeaf, canAdvance: true);

    public ServingBrokerHead? Read(HostReservationScope scope)
    {
        if (scope != _scope) return null;
        var head = Exchange(Request(OperationHeadProtocol.Read)).Head;
        return head is not null ? new(head.Scope, head.Revision, head.Digest) : null;
    }

    public bool TryAdvance(ServingBrokerHead predecessor, ServingBrokerHead next)
    {
        if (!_canAdvance) return false;
        var request = Request(OperationHeadProtocol.CompareAdvance,
            new(predecessor.Scope, predecessor.Revision, predecessor.Digest), new(next.Scope, next.Revision, next.Digest));
        return request.IsValidFor(_scope) && Exchange(request).Outcome == OperationHeadProtocol.Applied;
    }

    private BrokerAnchorRequest Request(string operation, BrokerAnchorHead? predecessor = null, BrokerAnchorHead? next = null) =>
        new(BrokerAnchorProtocol.Version, BrokerAnchorProtocol.Purpose, operation, _scope,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(OperationHeadProtocol.NonceBytes)), predecessor, next);

    // The synchronous append fence stays installed through the bounded, authenticated
    // exchange. Unavailable replies propagate independently of a proved CAS refusal.
    private BrokerAnchorReply Exchange(BrokerAnchorRequest request) =>
        OperationHeadTransport.ExchangeBrokerAsync(_endpoint, _pinnedLeaf, request).GetAwaiter().GetResult();
}
