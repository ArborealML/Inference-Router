using System.Security.Cryptography;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

// The store is the broker's independently anchored CAS. No local admission or issuer lock is held
// across these calls. A disputed write leaves the durable record untouched by this caller.
internal sealed class ServingBrokerLedger(HostReservationScope scope, Guid issuerEpoch, IServingBrokerStore store)
{
    private readonly Lock _mutate = new();

    internal ServingBrokerUse? Reserve(ServingBrokerBinding binding, ServingBrokerUseKind kind, Guid exchangeId,
        VerifiedInstanceInvocation? invocation)
    {
        using var mutation = _mutate.EnterScope();
        if (!Enum.IsDefined(kind) || exchangeId == Guid.Empty || ReadCurrent() is not { } current ||
            current.Record.IssuerEpoch != issuerEpoch || current.Record.Binding != binding || current.Record.Close is not null ||
            !PermitsUse(current.Record, kind, exchangeId, invocation))
            return null;

        var replay = current.Record.ApplicationReplay;
        if (invocation is { } application &&
            (replay = (replay ?? ServingBrokerApplicationReplay.Empty).Record(application.Sequence)) is null)
            return null;

        var use = new ServingBrokerUse(Guid.NewGuid(), binding.Route, kind, exchangeId, invocation,
            ServingBrokerUsePhase.Reserved);
        var uses = kind == ServingBrokerUseKind.Models ? current.Record.Uses.Clear() : current.Record.Uses;
        return Write(current, current.Record with { Uses = uses.Add(use), ApplicationReplay = replay }) ? use : null;
    }

    internal ServingBrokerUse? Issue(ServingBrokerUse expected)
    {
        using var mutation = _mutate.EnterScope();
        if (expected.Phase != ServingBrokerUsePhase.Reserved || ReadCurrent() is not { } current ||
            current.Record.IssuerEpoch != issuerEpoch || current.Record.Close is not null)
            return null;

        var issued = expected with { Phase = ServingBrokerUsePhase.Issued };
        return ReplaceUse(current, expected, issued) ? issued : null;
    }

    internal ServingBrokerUse? BindAccepted(ServingBrokerUse expected, ServingHostAcceptedSocket accepted)
    {
        using var mutation = _mutate.EnterScope();
        if (expected is not { Phase: ServingBrokerUsePhase.Issued, Accepted: null } ||
            ReadCurrent() is not { } current || current.Record.IssuerEpoch != issuerEpoch ||
            current.Record.Close is { Conclusive: true } || accepted.AcceptedInode == 0 || accepted.AcceptedCookie is 0 or ulong.MaxValue ||
            !HostMutation.IsPlanDigest(accepted.ExclusionDigest) ||
            accepted.Request.UseId != expected.Id || accepted.Request.Kind != expected.Kind ||
            accepted.Request.Invocation != expected.Invocation ||
            accepted.Request.Binding != ServingBrokerIssuer.ServingIdentity(current.Record.Binding) ||
            current.Record.Uses.Any(use => use.Accepted?.AcceptedCookie == accepted.AcceptedCookie) ||
            expected.Kind == ServingBrokerUseKind.State && !current.Record.Uses.Any(use =>
                use.ExchangeId == expected.ExchangeId && use.Kind == ServingBrokerUseKind.Models &&
                use.Accepted?.ExclusionDigest == accepted.ExclusionDigest))
            return null;

        var bound = expected with { Accepted = accepted };
        return ReplaceUse(current, expected, bound) ? bound : null;
    }

    // Only the original caller may assert it never entered the connector. An inherited
    // issued use is unresolved even if it has no recorded accepted FD.
    internal bool CancelBeforeConnect(ServingBrokerUse expected)
    {
        using var mutation = _mutate.EnterScope();
        return expected.Accepted is null && IsOutstanding(expected) && ReadCurrent() is { } current &&
            current.Record.IssuerEpoch == issuerEpoch &&
            ReplaceUse(current, expected, expected with { Phase = ServingBrokerUsePhase.Retired });
    }

    internal bool Retire(ServingBrokerUse expected, ServingHostTerminalProof terminal)
    {
        using var mutation = _mutate.EnterScope();
        if (ReadCurrent() is not { } current || expected is not
            { Phase: ServingBrokerUsePhase.Issued, Accepted: { } accepted } || !terminal.Binds(accepted))
            return false;

        // A confirmed retirement may reconcile an old epoch without reviving that epoch's use.
        return ReplaceUse(current, expected, expected with { Phase = ServingBrokerUsePhase.Retired, Terminal = terminal });
    }

    internal bool CompleteProbe(ServingBrokerUse expected, ServingHostTerminalProof terminal)
    {
        using var mutation = _mutate.EnterScope();
        return expected is { Phase: ServingBrokerUsePhase.Issued, Accepted: { } accepted,
                Kind: ServingBrokerUseKind.Models or ServingBrokerUseKind.State } &&
            terminal.Binds(accepted) && ReadCurrent() is { } current &&
            ReplaceUse(current, expected, expected with { Phase = ServingBrokerUsePhase.ProbeCompleted, Terminal = terminal });
    }

    internal (ServingHostAcceptedSocket Socket, ServingBrokerHead Head)? ReadIssuedSocket(ulong inode, ulong cookie)
    {
        if (inode == 0 || cookie is 0 or ulong.MaxValue || ReadCurrent() is not { } current ||
            current.Record.IssuerEpoch != issuerEpoch)
            return null;

        ServingHostAcceptedSocket? match = null;
        var binding = ServingBrokerIssuer.ServingIdentity(current.Record.Binding);
        foreach (var use in current.Record.Uses)
        {
            if (use.Accepted is not { } accepted || accepted.AcceptedInode != inode || accepted.AcceptedCookie != cookie)
                continue;
            if (match is not null || use.Phase != ServingBrokerUsePhase.Issued || use.Terminal is not null ||
                accepted.Request.UseId != use.Id || accepted.Request.Kind != use.Kind ||
                accepted.Request.Invocation != use.Invocation || use.Route != binding.Route || accepted.Request.Binding != binding ||
                !HostMutation.IsPlanDigest(accepted.ExclusionDigest))
                return null;
            match = accepted;
        }

        return match is null ? null : (match, new(scope, current.Record.Revision, current.Digest));
    }

    internal bool IsCurrentClosed(ServingBrokerBinding binding) =>
        ReadCurrent()?.Record is { Open: false, Close: null } record &&
        record.IssuerEpoch == issuerEpoch && record.Binding == binding && !HasOutstandingUses(record);

    internal Guid? BeginInitialProof(ServingBrokerBinding binding, bool observedOpen)
    {
        using var mutation = _mutate.EnterScope();
        if (ReadCurrent() is not { Record: { Close: null } record } current ||
            record.IssuerEpoch != issuerEpoch || record.Binding != binding || record.Open != observedOpen ||
            binding.Applied.State.Epoch != 1 || binding.AppliedJournalDigest is not null ||
            HasOutstandingUses(record) || record.ApplicationReplay?.HighWater == long.MaxValue)
            return null;

        // A retained Open refers to the same physically checked incumbent. Close its issuance
        // before new readiness probes. A successor cutoff is never eligible for this restart.
        var writer = Guid.NewGuid();
        if (!Write(current, record with { Open = false, Uses = [], ApplicationWriter = writer })) return null;
        VerifiedInstanceInvocation.ContinueAfter(record.ApplicationReplay?.HighWater ?? 0);
        return writer;
    }

    internal bool OwnsClosedInitial(ServingBrokerBinding binding, Guid commissionedEpoch) =>
        commissionedEpoch != Guid.Empty && commissionedEpoch == issuerEpoch &&
        binding.Applied.State.Epoch == 1 && binding.AppliedJournalDigest is null && IsCurrentClosed(binding);

    // Startup consumes a delegation earned by ordered probes in this exact issuer epoch.
    internal bool CanAttachInitial(ServingBrokerBinding binding) =>
        ReadCurrent()?.Record is { Open: true, Close: null } record &&
        record.IssuerEpoch == issuerEpoch && record.Binding == binding && !HasOutstandingUses(record) &&
        binding.Applied.State.Epoch == 1 && binding.AppliedJournalDigest is null;

    internal bool InstallClosedSuccessor(ServingBrokerBinding binding, EnrolledSuccessorApplied applied,
        EnrolledSuccessorAppliedWitness witness)
    {
        using var mutation = _mutate.EnterScope();
        var prior = applied.Retirement;
        var journal = witness.Journal;
        if (!binding.IsValid() || binding.Scope != scope || binding.Scope != applied.Scope.Reservation ||
            binding.CatalogHead != applied.Scope.Commission.Head || binding.Applied != witness.Checkpoint ||
            binding.Applied.State.AppliedLaunch != applied.Launch || binding.AppliedJournalDigest != journal.Digest ||
            journal.Revision <= 0 || !HostMutation.IsPlanDigest(journal.Digest) ||
            journal.Scope != scope || journal.Commission != binding.CatalogHead ||
            journal.Current is not { Phase: SuccessorIssuePhase.Applied } terminal ||
            terminal.Binding.Attempt != applied.Installed.Attempt ||
            terminal.Binding.Retirement != prior || terminal.Binding.Command.Operation != applied.Launch.Operation ||
            terminal.Binding.Successor != applied.Installed.Offer.Successor.Intent ||
            terminal.Start != new SupervisedStartIssue(terminal.Binding.Command, applied.Launch.InstanceId,
                terminal.Binding.Successor.CreateDigest) ||
            terminal.Outcome != new HostMutationOutcome(applied.Launch.Operation, HostMutationStatus.Applied,
                applied.Launch.InstanceId) ||
            binding.Route.RouteIncarnation <= prior.SealedRoute.RouteIncarnation ||
            ReadCurrent() is not { } current || current.Record is not { Open: false, Close: { Conclusive: true } close } record ||
            record.IssuerEpoch != issuerEpoch || close.Retirement != prior || HasOutstandingUses(record))
            return false;

        return Write(current, record with { Binding = binding, Close = null, Uses = [] });
    }

    // This protected delegation enables durable checkout only after the separate local
    // admission/issuer Open. It never exposes a route or mints a grant by itself.
    internal bool TryDelegateOpen(ServingBrokerBinding binding, Guid completedExchange)
    {
        using var mutation = _mutate.EnterScope();
        if (completedExchange == Guid.Empty || ReadCurrent() is not { } current ||
            current.Record is not { Open: false, Close: null } record || record.IssuerEpoch != issuerEpoch ||
            record.Binding != binding || HasOutstandingUses(record) ||
            !record.Uses.Any(use => use.ExchangeId == completedExchange &&
                use is { Kind: ServingBrokerUseKind.Models, Phase: ServingBrokerUsePhase.ProbeCompleted, Terminal: not null }) ||
            !record.Uses.Any(use => use.ExchangeId == completedExchange &&
                use is { Kind: ServingBrokerUseKind.State, Phase: ServingBrokerUsePhase.ProbeCompleted, Terminal: not null }))
            return false;

        return Write(current, record with { Open = true });
    }

    internal ServingBrokerClose? Seal(EnrolledSuccessorQuery query, HostEffectCheckpointCommitment checkpoint,
        long ownerGeneration, long ingressGeneration)
    {
        using var mutation = _mutate.EnterScope();
        if (ownerGeneration <= 0 || ingressGeneration <= 0 || query.Id == Guid.Empty || !query.Scope.Approves(query.Successor) ||
            query.Scope.Reservation != scope || checkpoint.State != query.Checkpoint ||
            checkpoint.State.AppliedLaunch is not { } incumbent || query.ProposedStop is not { } stop ||
            !HostEffectCheckpoint.BindsProposedStop(checkpoint.State, stop) || query.ExpectedIncumbentRoute is not { } route ||
            !EnrolledSuccessorScope.BindsResidentRoute(scope, route, incumbent) ||
            ReadCurrent() is not { } current || current.Record.IssuerEpoch != issuerEpoch ||
            current.Record.Binding.Route != route ||
            current.Record.Binding.CatalogHead != query.Scope.Commission.Head || current.Record.Binding.Applied != checkpoint)
            return null;

        if (current.Record.Close is { } prior)
            return prior.Retirement.OwnerGeneration == ownerGeneration && prior.Retirement.IngressGeneration == ingressGeneration &&
                prior.Matches(query, checkpoint) ? prior : null;

        if (!current.Record.Open) return null;
        var retirement = new EnrolledSuccessorRetirement(query.Id, Guid.NewGuid(), incumbent, stop, ownerGeneration,
            ingressGeneration, route, query.Successor.Key, query.Successor.PlanDigest, query.Successor.Intent.CreateDigest);
        var close = new ServingBrokerClose(scope, query.Scope.Commission.Head, checkpoint.State.Revision,
            checkpoint.Digest, retirement, Conclusive: false);
        return Write(current, current.Record with { Open = false, Close = close }) ? close : null;
    }

    // Broker completion proves there are no outstanding physical exchanges for the closed route.
    // The operation owner also requires zero holds in the corresponding local admission generation.
    internal EnrolledSuccessorRetirement? CompleteClose(ServingBrokerClose expected, VerifiedInstanceRoute localZeroRoute)
    {
        using var mutation = _mutate.EnterScope();
        if (expected.Retirement.SealedRoute != localZeroRoute || ReadCurrent() is not { } current ||
            current.Record.IssuerEpoch != issuerEpoch || current.Record.Open || current.Record.Close is not { } close ||
            close != (expected with { Conclusive = close.Conclusive }) || HasOutstandingUses(current.Record))
            return null;

        if (!close.Conclusive && !Write(current, current.Record with { Close = close with { Conclusive = true } }))
            return null;

        return close.Retirement;
    }

    // For the commissioned independent observer. The handle only resolves against a current sealed
    // durable record covering the exact scope, predecessor and operation.
    internal EnrolledSuccessorRetirement? ReadRetirement(Guid closeId) =>
        ReadRetirement(scope, store, closeId);

    internal static EnrolledSuccessorRetirement? ReadRetirement(HostReservationScope scope, IServingBrokerStore store,
        Guid closeId) =>
        closeId != Guid.Empty && ReadCurrent(scope, store)?.Record is { Open: false, Close: { Conclusive: true } close } record &&
        close.Retirement.CloseId == closeId && !HasOutstandingUses(record) ? close.Retirement : null;

    private ServingBrokerSnapshot? ReadCurrent() => ReadCurrent(scope, store);

    private static ServingBrokerSnapshot? ReadCurrent(HostReservationScope scope, IServingBrokerStore store)
    {
        var current = store.Read();
        return current is not null && current.Record.FormatVersion == ServingBrokerRecord.CurrentFormat &&
            current.Record.Scope == scope && current.Record.Binding.Scope == scope &&
            current.Record.Revision >= 0 && current.Record.IssuerEpoch != Guid.Empty && current.Record.Binding.IsValid() &&
            !current.Record.Uses.IsDefault && current.Record.ApplicationReplay?.IsValid() != false &&
            HostMutation.IsPlanDigest(current.Digest) ? current : null;
    }

    private bool ReplaceUse(ServingBrokerSnapshot current, ServingBrokerUse expected, ServingBrokerUse next)
    {
        var index = current.Record.Uses.IndexOf(expected);
        if (index < 0) return false;
        var uses = next.Phase == ServingBrokerUsePhase.Retired
            ? current.Record.Uses.RemoveAt(index) : current.Record.Uses.SetItem(index, next);
        return Write(current, current.Record with { Uses = uses });
    }

    private bool Write(ServingBrokerSnapshot prior, ServingBrokerRecord proposed)
    {
        var next = proposed with { Revision = checked(prior.Record.Revision + 1), Predecessor = prior.Digest };
        var payload = JsonSerializer.SerializeToUtf8Bytes(next, ServingBrokerRecordJsonContext.Default.ServingBrokerRecord);
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        return store.TryAdvance(prior, next) && ReadCurrent() is { } confirmed &&
            confirmed.Record.Revision == next.Revision && confirmed.Digest == digest;
    }

    private static bool HasOutstandingUses(ServingBrokerRecord record) =>
        record.Uses.Any(IsOutstanding);

    private static bool IsOutstanding(ServingBrokerUse use) =>
        use.Phase is not (ServingBrokerUsePhase.Retired or ServingBrokerUsePhase.ProbeCompleted);

    private static bool PermitsUse(ServingBrokerRecord record, ServingBrokerUseKind kind, Guid exchangeId,
        VerifiedInstanceInvocation? invocation)
    {
        if (kind == ServingBrokerUseKind.Application)
            return record.Open && invocation is { } application && application.Id == exchangeId &&
                record.ApplicationWriter != Guid.Empty && application.CommissionedWriter == record.ApplicationWriter &&
                application.Route == record.Binding.Route &&
                !record.Uses.Any(use => use.Invocation?.Id == application.Id) &&
                record.Uses.Count(use => use.Kind == ServingBrokerUseKind.Application &&
                    IsOutstanding(use)) < record.Binding.TotalSlots;

        if (record.Open || invocation is not null || record.Uses.Any(use => use.ExchangeId == exchangeId && use.Kind == kind))
            return false;

        return kind switch
        {
            ServingBrokerUseKind.Models => !HasOutstandingUses(record),
            ServingBrokerUseKind.State => record.Uses.Any(use => use.ExchangeId == exchangeId &&
                use is { Kind: ServingBrokerUseKind.Models, Phase: ServingBrokerUsePhase.ProbeCompleted, Terminal: not null }) &&
                !HasOutstandingUses(record),
            _ => false,
        };
    }
}
