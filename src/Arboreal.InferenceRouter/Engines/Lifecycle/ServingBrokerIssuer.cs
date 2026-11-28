namespace Arboreal.InferenceRouter.Lifecycle;

// A delegation is local to this issuer epoch. The durable broker lease never opens the
// admission route. The route predicates are state-only under the same typed issuer lock.
internal sealed class ServingBrokerIssuer(ServingBrokerLedger ledger, ServingBrokerBinding binding,
    ServingBackendEndpoint backend, Uri logicalEndpoint, Lock issuerGate,
    Func<VerifiedInstanceRoute, bool> canMint, Func<VerifiedInstanceInvocation, bool> firstWriteAllowed,
    TimeSpan cleanupTimeout) : IVerifiedDispatchIssuer
{
    private readonly Dictionary<VerifiedInstanceGrant, Transfer> _transfers = new();
    private readonly Uri _chat = EngineRuntime.Combine(logicalEndpoint, "chat/completions");
    private readonly Uri _completion = EngineRuntime.Combine(logicalEndpoint, "completions");

    public ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken ct) =>
        ValueTask.FromException<VerifiedInstanceGrant?>(new InvalidOperationException("Certified checkout is required."));

    public async ValueTask<VerifiedCheckout> CheckoutVerifiedAsync(VerifiedInstanceInvocation invocation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (issuerGate)
        {
            if (invocation.Route != binding.Route || invocation.Id == Guid.Empty || logicalEndpoint.Scheme != "http" ||
                (!VerifiedDispatchEnvelope.SameEffectiveUri(_chat, invocation.LogicalUri) &&
                 !VerifiedDispatchEnvelope.SameEffectiveUri(_completion, invocation.LogicalUri)) ||
                !canMint(invocation.Route) || !firstWriteAllowed(invocation))
                return new(VerifiedCheckoutDisposition.NoTransfer);
        }

        if (ledger.Reserve(binding, ServingBrokerUseKind.Application, invocation.Id, invocation) is not { } reserved)
            return new(VerifiedCheckoutDisposition.Unknown);
        if (ledger.Issue(reserved) is not { } issued)
            return new(ledger.CancelBeforeConnect(reserved) ? VerifiedCheckoutDisposition.ConfirmedRetired : VerifiedCheckoutDisposition.Unknown);

        // A failed accepted-socket join leaves the issued use fenced. Neither cancellation nor
        // the local close proves the handler has retired that exact use.
        var identity = ServingIdentity(binding);
        var connection = await ServingBackendConnection.OpenAsync(backend, identity, issued, deadline: null, ct).ConfigureAwait(false);
        var ownsConnection = true;
        try
        {
            if (ledger.BindAccepted(issued, connection.Accepted) is not { } bound)
                return new(VerifiedCheckoutDisposition.Unknown);

            var transfer = new Transfer(bound, connection);
            var grant = new VerifiedInstanceGrant(invocation, connection.TakeApplicationStream(), () => CloseAsync(transfer));
            transfer.Grant = grant;
            lock (issuerGate)
            {
                if (!ct.IsCancellationRequested && canMint(invocation.Route) && firstWriteAllowed(invocation) &&
                    backend.Evidence.IsLive(connection.Accepted))
                {
                    _transfers.Add(grant, transfer);
                    ownsConnection = false;
                    return new(VerifiedCheckoutDisposition.Transferred, grant);
                }
            }

            ownsConnection = false;
            await CloseAsync(transfer).ConfigureAwait(false);
            return new(VerifiedCheckoutDisposition.ConfirmedRetired);
        }
        finally
        {
            if (ownsConnection) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
    {
        lock (issuerGate)
        {
            if (!_transfers.TryGetValue(grant, out var transfer) || transfer.Authorised || transfer.Closing ||
                !canMint(grant.Invocation.Route) || !firstWriteAllowed(grant.Invocation) ||
                !backend.Evidence.IsLive(transfer.Connection.Accepted))
                return null;

            transfer.Authorised = true;
            return new Permit(issuerGate, transfer);
        }
    }

    private async ValueTask CloseAsync(Transfer transfer)
    {
        lock (issuerGate)
        {
            if (transfer.Closing)
                throw new InvalidOperationException("The verified transfer is already closing.");
            transfer.Closing = true;
        }

        if (await transfer.Connection.CloseAndConfirmTerminalAsync(backend.Evidence, cleanupTimeout)
                .ConfigureAwait(false) is not { } terminal || !ledger.Retire(transfer.Use, terminal))
            throw new IOException("The broker could not prove retirement of the exact application connection.");

        lock (issuerGate)
        {
            transfer.Retired = true;
            _transfers.Remove(transfer.Grant!);
        }
    }

    internal static ServingBackendIdentity ServingIdentity(ServingBrokerBinding binding) =>
        new(binding.Scope, binding.CatalogHead, binding.Applied.State.AppliedLaunch!.Operation, binding.Applied.Digest,
            binding.Route, binding.PolicyDigest, binding.AppliedJournalDigest);

    private sealed class Transfer(ServingBrokerUse use, ServingBackendConnection connection)
    {
        internal ServingBrokerUse Use { get; } = use;
        internal ServingBackendConnection Connection { get; } = connection;
        internal VerifiedInstanceGrant? Grant;
        internal bool Authorised;
        internal bool Closing;
        internal bool Retired;
    }

    private sealed class Permit(Lock gate, Transfer transfer) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (gate)
                return transfer.Retired ? ValueTask.CompletedTask :
                    ValueTask.FromException(new IOException("The physical use remains unconfirmed."));
        }
    }
}
