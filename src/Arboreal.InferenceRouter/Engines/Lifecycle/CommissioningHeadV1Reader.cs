using System.Net.Sockets;

namespace Arboreal.InferenceRouter.Lifecycle;

/// <summary>Uses the independent topology authority with the intake's original monotonic cutoff.</summary>
internal sealed class CommissioningHeadV1Reader(string commissionedSlot, UnixDomainSocketEndPoint endpoint,
    string leafSha256) : ICommissioningHeadReader
{
    public async ValueTask<SupervisedLaunchCommission?> ReadCurrentAsync(string trustedSlot, CommissionReadBudget budget,
        CancellationToken ct)
    {
        if (trustedSlot != commissionedSlot) return null;

        var value = await HeadReadV1Endpoint.ReadAsync(endpoint, leafSha256, HeadReadScope.Topology(commissionedSlot),
            budget.Clock, budget.StartedAt, budget.Duration, ct).ConfigureAwait(false);
        return value.Topology;
    }
}
