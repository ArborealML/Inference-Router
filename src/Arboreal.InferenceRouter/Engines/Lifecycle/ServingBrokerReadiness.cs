using System.Collections.Immutable;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record ServingBrokerProbe(Uri Uri, int ResponseLimit)
{
    internal bool IsValid() => VerifiedDispatchEnvelope.IsHttpEndpoint(Uri) && Uri.Scheme == "http" &&
        ResponseLimit is > 0 and <= ServingBackendConnection.MaxResponse;
}

internal sealed record ServingBrokerProbePolicy(string Digest, VerifiedDispatchCredential Credential,
    ServingBrokerProbe Models, ServingBrokerProbe State);
internal sealed record ServingBrokerProbePair(ServingBrokerBinding Binding, Guid ExchangeId, ServingBackendProfile Profile,
    ImmutableArray<byte> Models, ImmutableArray<byte> State);

internal sealed class ServingBrokerReadiness(ServingBrokerLedger ledger, Lock issuerGate, TimeSpan cleanupTimeout)
{
    // The owning stage supplies the original launch deadline. The detached worker owns its
    // exact connections and durable uses and has no publication or checkpoint authority.
    internal ValueTask<ReadinessStep<ServingBrokerProbePair?>> ReadAsync(ServingBrokerBinding binding,
        ServingBackendEndpoint endpoint, ServingBrokerProbePolicy policy, ReadinessDeadline originalDeadline,
        Func<ServingBrokerUseKind, ReadOnlyMemory<byte>, bool> validateReply) =>
        originalDeadline.ObserveAsync(token => ReadWithinAsync(binding, endpoint, policy, originalDeadline, validateReply, token));

    // A staged caller already owns the ObserveAsync race and supplies its exact token.
    internal async Task<ServingBrokerProbePair?> ReadWithinAsync(ServingBrokerBinding binding, ServingBackendEndpoint endpoint,
        ServingBrokerProbePolicy policy, ReadinessDeadline deadline,
        Func<ServingBrokerUseKind, ReadOnlyMemory<byte>, bool> validateReply, CancellationToken ct)
    {
        var plan = binding.Applied.State.AppliedLaunch?.Plan;
        if (plan is null || !binding.IsValid() || policy.Digest != binding.PolicyDigest ||
            !policy.Models.IsValid() || !policy.State.IsValid() ||
            endpoint.Profile.CanonicalModel != plan.Model || endpoint.Profile.Profile != plan.Tier ||
            endpoint.Profile.Context <= 0 || endpoint.Profile.Slots < binding.TotalSlots)
            return null;

        var exchange = Guid.NewGuid();
        var models = await ReadOneAsync(ServingBrokerUseKind.Models, policy.Models).ConfigureAwait(false);
        if (models is null) return null;
        var state = await ReadOneAsync(ServingBrokerUseKind.State, policy.State).ConfigureAwait(false);
        return state is not null && ledger.IsCurrentClosed(binding) &&
            deadline.OutcomeForSettlement == ReadinessStepOutcome.Completed
            ? new(binding, exchange, endpoint.Profile, [.. models], [.. state]) : null;

        async ValueTask<byte[]?> ReadOneAsync(ServingBrokerUseKind kind, ServingBrokerProbe probe)
        {
            if (deadline.OutcomeForSettlement != ReadinessStepOutcome.Completed ||
                ledger.Reserve(binding, kind, exchange, invocation: null) is not { } reserved)
                return null;

            if (deadline.OutcomeForSettlement != ReadinessStepOutcome.Completed)
            {
                _ = ledger.CancelBeforeConnect(reserved);
                return null;
            }

            if (ledger.Issue(reserved) is not { } issued) return null;
            if (deadline.OutcomeForSettlement != ReadinessStepOutcome.Completed)
            {
                _ = ledger.CancelBeforeConnect(issued);
                return null;
            }

            var connection = await ServingBackendConnection.OpenAsync(endpoint, ServingBrokerIssuer.ServingIdentity(binding),
                issued, deadline, ct).ConfigureAwait(false);
            ServingBrokerUse? bound = null;
            byte[]? accepted = null;
            var completed = false;
            try
            {
                bound = ledger.BindAccepted(issued, connection.Accepted);
                if (bound is null || deadline.OutcomeForSettlement != ReadinessStepOutcome.Completed) return null;
                lock (issuerGate)
                {
                    if (ct.IsCancellationRequested || deadline.OutcomeForSettlement != ReadinessStepOutcome.Completed ||
                        !endpoint.Evidence.IsLive(connection.Accepted))
                        return null;
                }

                var bytes = await connection.ReadProbeAsync(probe, policy.Credential, ct).ConfigureAwait(false);
                if (validateReply(kind, bytes)) accepted = bytes;
            }
            finally
            {
                if (bound is null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                else if (await connection.CloseAndConfirmTerminalAsync(endpoint.Evidence, cleanupTimeout).ConfigureAwait(false)
                    is { } terminal)
                {
                    if (accepted is null) _ = ledger.Retire(bound, terminal);
                    else completed = ledger.CompleteProbe(bound, terminal);
                }
            }

            return completed ? accepted : null;
        }
    }
}
