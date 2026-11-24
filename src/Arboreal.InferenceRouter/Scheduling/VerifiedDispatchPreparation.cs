using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

/// <summary>One independently observed resident, issued for one offline route generation.</summary>
internal sealed record VerifiedDispatchResident(
    VerifiedDispatchTarget Target, VerifiedInstanceRoute Route, long TierWindow,
    long MeasuredWindow, int TotalSlots, Uri Endpoint, VerifiedDispatchCredential Credential,
    IVerifiedDispatchIssuer Issuer)
{
    internal bool Fits(long requiredWindow) =>
        TierWindow > 0 && MeasuredWindow >= Math.Max(TierWindow, requiredWindow) && TotalSlots > 0;
}

/// <summary>One immutable sender on a Ready preparation. The preparation worker owns disposal.</summary>
internal sealed class VerifiedPreparedSender : IAsyncDisposable
{
    private readonly ExactTicketAdmission.Claim _claim;
    private readonly VerifiedDispatchEnvelope _envelope;
    private readonly VerifiedInstanceTransport _transport;
    private readonly Func<ValueTask>? _cleanup;

    private VerifiedPreparedSender(ExactTicketAdmission.Claim claim, VerifiedDispatchEnvelope envelope,
                                   IVerifiedDispatchIssuer issuer, Func<ValueTask>? cleanup)
    {
        _claim = claim;
        _envelope = envelope;
        _transport = new VerifiedInstanceTransport(envelope.Invocation, issuer);
        _cleanup = cleanup;
    }

    internal static VerifiedPreparedSender Create(
        ExactTicketAdmission.Claim claim, VerifiedDispatchTargetResolver resolver, string configuration,
        string backendRoute, Func<ValueTask>? cleanup = null)
    {
        var resident = claim.Resident ?? throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);
        var requestedSpelling = claim.Request.OriginalModel ?? claim.Ticket.Model;
        var selected = resolver.Resolve(requestedSpelling, claim.Target.Profile,
            claim.SwitchRequired ? null : resident.Target, configuration, backendRoute);
        if (selected != resident.Target)
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);

        var frozen = VerifiedDispatchEnvelope.Freeze(selected, resident.Route, Guid.NewGuid(), resident.Endpoint,
            requestedSpelling, claim.Request.Request.Body, resident.Credential);
        return new VerifiedPreparedSender(claim, frozen, resident.Issuer, cleanup);
    }

    internal bool Matches(ExactTicketAdmission.Claim claim) =>
        _claim == claim && ReferenceEquals(_claim.Resident, claim.Resident);

    internal Func<Task>? AfterOfferValidatedForTests { get; set; }

    // A null operation means the local serving observation changed before checkout. Offer
    // bytes and credentials have already been validated. Issuer errors stay on the fault path.
    internal async ValueTask<VerifiedTransportOperation?> StartIfProfileReadyAsync(
        EngineRuntime runtime, HttpRequestMessage request, long credentialEpoch, CancellationToken ct)
    {
        await _envelope.CheckOfferAsync(_envelope.Invocation, request, credentialEpoch, ct).ConfigureAwait(false);
        if (AfterOfferValidatedForTests is { } after) await after().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!runtime.RequestedProfileReady(_claim.Request)) return null;
        var operation = _transport.SendVerified(_envelope, ct);
        _claim.Ticket.DispatchedProfile = _claim.Resident?.Target.Tier;
        return operation;
    }

    public ValueTask DisposeAsync() => _cleanup?.Invoke() ?? ValueTask.CompletedTask;
}
