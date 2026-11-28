using System.Runtime.CompilerServices;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

/// <summary>
/// Internal admission boundary for the injected sender. A serving-route publisher and physical
/// exchange owner must be supplied by the enrolled host before this can guard real upstream traffic.
/// </summary>
internal sealed class ExactTicketAdmission(
    Func<ExactTicketAdmission.Claim, CancellationToken, ValueTask<IAsyncDisposable?>> prepare,
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
    Func<ExactTicketAdmission.Claim, CancellationToken, ValueTask<VerifiedPreparedSender?>>? prepareVerified = null)
{
    internal readonly record struct DomainIdentity(string Name, long Generation);
    internal readonly record struct DomainSnapshot(DomainIdentity Identity, int PreparationLimit, int Preparations,
        int PhysicalUses, bool Transitioning, long RouteGeneration);
    internal readonly record struct Route(string Engine, string Model, string? Profile, VerifiedDispatchTarget? Recipe = null);
    internal readonly record struct Claim(QueuedRequest Request, Route Target, long Generation, bool SwitchRequired,
                                          long ExplicitContext, VerifiedDispatchResident? Resident = null)
    {
        internal RouterTicket Ticket => Request.Ticket;
    }

    private readonly record struct Binding(Route Target, bool SwitchRequired, long ExplicitContext);
    private sealed class RouteState(EngineAdmissionState engine)
    {
        internal EngineAdmissionState Engine { get; } = engine;
        internal long Generation;
        internal bool Open;
        internal long Window;
        internal VerifiedDispatchResident? Resident;
        internal CommissionedRouteUses? Uses;
    }

    private sealed class DomainState(DomainIdentity identity)
    {
        internal DomainIdentity Identity { get; } = identity;
        internal List<EngineAdmissionState> Engines { get; } = [];
        internal int Limit => Math.Max(2, Engines.Count);
        internal int Preparations;
        internal bool Transitioning;
        internal long RouteGeneration;
        internal VerifiedDispatchResident? ActiveResident;
        internal RouterTicket? ActivationOwner;
        internal RouteState? CommissionedIncumbent;
    }

    private sealed class EngineAdmissionState(DomainState domain)
    {
        internal DomainState Domain { get; } = domain;
        internal int PhysicalUses;
        internal int LogicalOverlap;
        internal Claim? OpeningOwner;
        internal bool OwnerPreparationPending;

        internal void ClearOpeningOwner()
        {
            OpeningOwner = null;
            OwnerPreparationPending = false;
        }
    }

    internal sealed class CommissionedRouteUses(CommissionedServingStamp stamp)
    {
        internal CommissionedServingStamp Stamp => stamp;
        internal int Count;
        internal TaskCompletionSource Zero { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Sealed;
    }

    private sealed class Preparation(Claim claim, DomainState domain)
    {
        internal Claim Claim { get; } = claim;
        internal DomainState Domain { get; } = domain;
        internal bool Ready { get; set; }
        internal VerifiedPreparedSender? VerifiedSender { get; set; }
        internal bool Borrowed { get; set; }
        internal bool Retiring { get; set; }
        internal TaskCompletionSource CleanupRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RetirementRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WorkFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Exception?> CleanupFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void RequestRetirement()
        {
            RetirementRequested.TrySetResult();
            CleanupRequested.TrySetResult();
        }

        internal async ValueTask FinishAsync()
        {
            CleanupRequested.TrySetResult();
            if (await CleanupFinished.Task.ConfigureAwait(false) is { } error)
                throw new InvalidOperationException("The admitted preparation did not clean up.", error);
        }
    }

    private readonly Dictionary<Route, RouteState> _routes = [];
    private readonly Dictionary<string, RouteState> _verifiedRoutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EngineAdmissionState> _engines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RouterTicket, Preparation> _preparations = [];
    private readonly ConditionalWeakTable<RouterTicket, Dictionary<Binding, long>> _spent = new();
    private Action? _wake;
    private Action<Claim, Exception>? _reportCleanupFailure;
    private Action<Claim, VerifiedDispatchRefusal>? _rejectBinding;
    private TimeProvider _clock = TimeProvider.System;
    private int _releases;
    private bool _stopped;

    internal object Gate { get; } = new();
    internal Func<Claim, Task>? BeforeSubmitForTests { get; set; }
    internal Func<Claim, CancellationToken, ValueTask>? BeforePrepareForTests { get; set; }
    internal Func<Invocation, Task>? BeforeSendForTests { get; set; }
    internal Action<HttpRequestMessage>? BeforeVerifiedOfferForTests { get; set; }
    internal Action<HttpResponseMessage>? AfterVerifiedConsumeForTests { get; set; }
    internal Action<Claim, Exception?>? AfterPreparationCleanupForTests { get; set; }
    internal Action<Claim, bool>? AfterPreparationForTests { get; set; }
    internal Action<Claim, Exception>? AfterPreparationFaultForTests { get; set; }
    internal Action? AfterReleaseForTests { get; set; }
    internal int PhysicalUses { get { lock (Gate) return _engines.Values.Sum(static state => state.PhysicalUses); } }
    internal int Releases { get { lock (Gate) return _releases; } }
    internal int PreparationCount { get { lock (Gate) return _preparations.Count; } }

    internal bool UsesVerifiedDispatch => prepareVerified is not null;

    internal void Attach(IReadOnlyDictionary<string, DomainIdentity> engines, TimeProvider clock,
                         Action wake, Action<Claim, Exception> reportCleanupFailure,
                         Action<Claim, VerifiedDispatchRefusal>? rejectBinding = null)
    {
        if (_wake is not null) throw new InvalidOperationException("An admission owner is already attached.");
        foreach (var (engine, identity) in engines)
        {
            if (!_domains.TryGetValue(identity, out var domain)) _domains.Add(identity, domain = new(identity));
            var state = new EngineAdmissionState(domain);
            _engines.Add(engine, state);
            domain.Engines.Add(state);
        }
        _clock = clock;
        _wake = wake;
        _reportCleanupFailure = reportCleanupFailure;
        _rejectBinding = rejectBinding;
    }

    private readonly Dictionary<DomainIdentity, DomainState> _domains = [];

    // Called under the admission gate at the queue acceptance cut. No provider work starts here.
    internal bool TryBeginTransition(DomainIdentity identity, RouterTicket? ticket, CommissionedRouteIssuer? issuer,
        TimeSpan drainBudget, out CommissionedHostCut? cut)
    {
        lock (Gate)
        {
            cut = null;
            if (_stopped || !_domains.TryGetValue(identity, out var domain) || domain.Transitioning ||
                domain.ActivationOwner is not null || ticket is not null &&
                (_preparations.ContainsKey(ticket) || HasActivationOwner(ticket))) return false;
            if (issuer is null)
            {
                if (domain.Preparations != 0 || domain.Engines.Exists(static engine => engine.PhysicalUses != 0)) return false;
            }
            else
            {
                var incumbent = domain.CommissionedIncumbent;
                var uses = incumbent?.Uses;
                var authority = issuer.Authority;
                var epoch = authority.ResourceEpoch;
                if (!issuer.IsCurrentScope || epoch <= 0 || (uses is null ? !authority.IsKnownVacant(epoch) :
                    uses.Stamp.ResourceEpoch != epoch)) return false;

                List<Preparation> cleanup = [];
                cut = new(issuer, epoch, uses?.Stamp, ReserveStop, Seal, AwaitZeroAsync, _clock, drainBudget);

                bool Seal()
                {
                    lock (Gate)
                    lock (issuer.Gate)
                    {
                        if (_stopped || !domain.Transitioning || !ReferenceEquals(domain.CommissionedIncumbent, incumbent) ||
                            uses is null || uses.Count < 0 || !issuer.Seal(uses.Stamp)) return false;
                        incumbent!.Open = false;
                        Volatile.Write(ref domain.ActiveResident, null);
                        uses.Sealed = true;
                        if (uses.Count == 0) uses.Zero.TrySetResult();
                        foreach (var entry in _preparations.Values)
                        {
                            if (!ReferenceEquals(incumbent?.Resident, entry.Claim.Resident)) continue;
                            _ = Retire(entry);
                            cleanup.Add(entry);
                        }
                    }
                    foreach (var entry in cleanup) entry.RequestRetirement();
                    return true;
                }

                ICommissionedStopPermit? ReserveStop(CommissionedServingRetirement retirement)
                {
                    lock (Gate)
                    lock (issuer.Gate)
                        return !_stopped && domain.Transitioning && ReferenceEquals(domain.CommissionedIncumbent, incumbent) &&
                            uses is { Sealed: true, Count: 0 } && issuer.IsSealed(uses.Stamp) &&
                            authority.ResourceEpoch == epoch ? authority.TryReserveStop(retirement) : null;
                }

                async Task<bool> AwaitZeroAsync(CancellationToken ct)
                {
                    foreach (var entry in cleanup)
                        if (await entry.CleanupFinished.Task.WaitAsync(ct).ConfigureAwait(false) is not null) return false;
                    await uses!.Zero.Task.WaitAsync(ct).ConfigureAwait(false);
                    return true;
                }
            }
            domain.Transitioning = true;
            domain.ActivationOwner = ticket;
            if (issuer is null)
            {
                Volatile.Write(ref domain.ActiveResident, null);
                foreach (var route in _verifiedRoutes.Values)
                    if (ReferenceEquals(route.Engine.Domain, domain)) route.Open = false;
            }
        }
        return true;
    }

    internal bool EndTransition(DomainIdentity identity)
    {
        VerifiedDispatchResident? resident;
        lock (Gate)
        {
            var domain = _domains[identity];
            domain.Transitioning = false;
            var active = _verifiedRoutes.Values.FirstOrDefault(route => route.Open && ReferenceEquals(route.Engine.Domain, domain));
            resident = _stopped ? null : active?.Resident;
            Volatile.Write(ref domain.ActiveResident, resident);
        }
        _wake?.Invoke();
        return resident is not null;
    }

    private bool HasActivationOwner(RouterTicket ticket) => _domains.Values.Any(domain => ReferenceEquals(domain.ActivationOwner, ticket));

    internal VerifiedDispatchResident? ReadManagedResident(string engine) => _engines.TryGetValue(engine, out var state) &&
        Volatile.Read(ref state.Domain.ActiveResident) is { } resident && resident.Route.Engine == engine ? resident : null;

    internal bool IsManagedResident(string engine) => ReadManagedResident(engine) is not null;

    internal bool HasOutstanding(RouterTicket ticket)
    {
        lock (Gate) return _preparations.ContainsKey(ticket) || HasActivationOwner(ticket);
    }

    internal DomainSnapshot Snapshot(DomainIdentity identity)
    {
        lock (Gate)
        {
            var domain = _domains[identity];
            return new(identity, domain.Limit, domain.Preparations, domain.Engines.Sum(static engine => engine.PhysicalUses),
                domain.Transitioning, domain.RouteGeneration);
        }
    }

    internal void Publish(Route target, long generation, bool open, long window = 0)
    {
        List<Preparation> cleanup = [];
        lock (Gate)
        {
            if (_stopped) return;
            if (!_routes.TryGetValue(target, out var route))
            {
                if (!_engines.TryGetValue(target.Engine, out var engine) || engine.Domain.Identity.Generation > 0) return;
                _routes.Add(target, route = new(engine));
            }
            if (generation < route.Generation) return;
            if (generation == route.Generation && route.Open == open && route.Window == window) return;
            if (generation != route.Generation && route.Engine.OpeningOwner is { } owner && owner.Target == target)
                route.Engine.ClearOpeningOwner();
            route.Generation = generation;
            route.Open = open;
            route.Window = window;
            foreach (var entry in _preparations.Values)
                if (entry.Claim.Target == target && entry.Claim.Generation != generation &&
                    Retire(entry))
                    cleanup.Add(entry);
        }
        foreach (var entry in cleanup) entry.RequestRetirement();
        _wake?.Invoke();
    }

    // Only the offline issuer adapter publishes these proofs. A replacement/availability event
    // advances the generation and fences the old preparation without rearming a spent generation.
    internal bool PublishVerified(VerifiedDispatchResident resident, CommissionedServingStamp? stamp = null)
    {
        List<Preparation> cleanup = [];
        lock (Gate)
        {
            if (_stopped || !UsesVerifiedDispatch || !resident.Target.Matches(resident.Route)) return false;
            var engineName = resident.Route.Engine;
            if (!_engines.TryGetValue(engineName, out var engine)) return false;
            var domain = engine.Domain;
            if (domain.Identity.Generation > 0 && (!domain.Transitioning || resident.Route.HostGroup != domain.Identity.Name ||
                resident.Route.EnrollmentGeneration != domain.Identity.Generation ||
                resident.Route.RouteIncarnation <= domain.RouteGeneration)) return false;
            if (!_verifiedRoutes.TryGetValue(engineName, out var route))
            {
                _verifiedRoutes.Add(engineName, route = new(engine));
            }
            if (resident.Route.RouteIncarnation <= route.Generation) return false;

            if (stamp is not null)
            {
                if (stamp.Route != resident.Route || (domain.CommissionedIncumbent is { Uses: { } previous } &&
                    (!previous.Sealed || previous.Count != 0))) return false;
                _verifiedRoutes[engineName] = route = new(engine) { Uses = new(stamp) };
                domain.CommissionedIncumbent = route;
            }
            route.Generation = resident.Route.RouteIncarnation;
            route.Resident = resident;
            route.Open = true;
            domain.RouteGeneration = resident.Route.RouteIncarnation;
            foreach (var entry in _preparations.Values)
                if (entry.Claim.Resident is { } old &&
                    string.Equals(old.Route.Engine, engineName, StringComparison.Ordinal) && Retire(entry))
                    cleanup.Add(entry);
        }
        foreach (var entry in cleanup) entry.RequestRetirement();
        _wake?.Invoke();
        return true;
    }

    internal bool CloseVerified(VerifiedDispatchResident resident)
    {
        List<Preparation> cleanup = [];
        lock (Gate)
        {
            if (_stopped || !_verifiedRoutes.TryGetValue(resident.Route.Engine, out var route) ||
                !ReferenceEquals(route.Resident, resident) || !route.Open) return false;
            route.Open = false;
            var domain = route.Engine.Domain;
            if (ReferenceEquals(domain.ActiveResident, resident))
            {
                Volatile.Write(ref domain.ActiveResident, null);
                domain.ActivationOwner = null;
            }
            foreach (var entry in _preparations.Values)
                if (ReferenceEquals(entry.Claim.Resident, resident) && Retire(entry)) cleanup.Add(entry);
        }
        foreach (var entry in cleanup) entry.RequestRetirement();
        _wake?.Invoke();
        return true;
    }

    // The dispatcher carries this immutable stamp across preparation and commit. It can never
    // authorize from a freshly looked-up stamp that it did not rank.
    internal Claim? Capture(QueuedRequest request, Route target, bool switchRequired, long explicitContext)
    {
        lock (Gate)
        {
            if (_stopped) return null;
            if (UsesVerifiedDispatch)
            {
                return _verifiedRoutes.TryGetValue(target.Engine, out var verified) &&
                    verified.Resident is { } resident &&
                    (verified.Engine.Domain.Identity.Generation == 0 || !switchRequired &&
                        (target.Profile is null || target.Profile == resident.Target.Tier && target.Recipe == resident.Target)) &&
                    string.Equals(target.Model, resident.Target.Model, StringComparison.Ordinal)
                    ? new Claim(request, target, verified.Generation, switchRequired, explicitContext, resident) : null;
            }
            return _routes.TryGetValue(target, out var state)
                ? new Claim(request, target, state.Generation, switchRequired, explicitContext) : null;
        }
    }

    internal bool IsOpen(Claim claim) => !_stopped && StateFor(claim) is { Open: true } route &&
        !route.Engine.Domain.Transitioning &&
        route.Generation == claim.Generation && ReferenceEquals(route.Resident, claim.Resident);

    private bool IsCurrentManagedOffer(Claim claim)
    {
        lock (Gate) return StateFor(claim)?.Engine.Domain.Identity.Generation == 0 || IsOpen(claim);
    }

    internal bool IsOtherDomain(RouterTicket ticket, string engine) =>
        (_preparations.TryGetValue(ticket, out var entry) && _engines.TryGetValue(engine, out var state) &&
        !ReferenceEquals(entry.Domain, state.Domain)) || _domains.Values.Any(domain =>
            ReferenceEquals(domain.ActivationOwner, ticket) && _engines.GetValueOrDefault(engine)?.Domain != domain);

    internal bool SharesDomain(string firstEngine, string secondEngine) =>
        _engines.TryGetValue(firstEngine, out var first) && _engines.TryGetValue(secondEngine, out var second) &&
        ReferenceEquals(first.Domain, second.Domain);

    // Advisory only. The dispatch round uses this to choose one preparation after trying
    // every ready alternative. Prepare makes the final decision under the same gate.
    internal bool CanStartPreparation(Claim claim) => IsOpen(claim) && CanPrepare(claim) &&
        !_preparations.ContainsKey(claim.Ticket) && !IsOtherDomain(claim.Ticket, claim.Target.Engine) && !AttemptSpent(claim) &&
        StateFor(claim)!.Engine.Domain is { } domain && domain.Preparations < domain.Limit &&
        !HasRunnablePendingOwner(domain);

    private RouteState? StateFor(Claim claim) => claim.Resident is null
        ? _routes.GetValueOrDefault(claim.Target) : _verifiedRoutes.GetValueOrDefault(claim.Target.Engine);

    internal bool IsCurrentBindingRefusal(Claim claim) => IsOpen(claim) && claim.Ticket.CanPrepareAdmission &&
        _preparations.TryGetValue(claim.Ticket, out var current) && !current.Retiring && current.Claim == claim;

    internal long Window(Claim claim) => _routes[claim.Target].Window;

    // Only the offline host fixture stages an owner. The production owner is not promoted
    // through this path. A reservation has no physical use until authorization at open.
    internal bool StageOpeningOwnerForTests(Claim owner)
    {
        lock (Gate)
        {
            if (_stopped || !_routes.TryGetValue(owner.Target, out var route) || route.Open ||
                route.Generation != owner.Generation ||
                !CanPrepare(owner) || AttemptSpent(owner))
                return false;
            var state = route.Engine;
            if (state.OpeningOwner is { } current && CanPrepare(current)) return false;
            state.ClearOpeningOwner();
            state.OpeningOwner = owner;
            return true;
        }
    }

    internal Invocation? AuthorizeOpeningOwnerForTests(Claim owner, EngineRuntime engine)
    {
        Invocation invocation;
        lock (Gate)
        {
            if (!string.Equals(engine.Name, owner.Target.Engine, StringComparison.OrdinalIgnoreCase) ||
                !IsOpen(owner) || _routes[owner.Target].Engine.OpeningOwner != owner || !IsPrepared(owner))
                return null;
            var observation = engine.ReadDispatchObservation();
            if (!OpeningOwnerEligible(owner, observation.Capability) || !CanTake(owner, observation))
                return null;
            invocation = Authorize(owner, engine, detachedOwner: true);
        }
        // The detached caller has no dispatch loop iteration to observe this freed preparation slot.
        _wake?.Invoke();
        return invocation;
    }

    // Called under Gate as part of the exact take. Holding aside a final unit for a live
    // owner does not prevent ordinary use of additional free units.
    internal bool CanTake(Claim claim, EngineRuntime.DispatchObservation observation)
    {
        var state = StateFor(claim)!.Engine;
        // Ordinary requests may occupy both counters. Charge the overlap only once.
        var slots = claim.Resident is { } resident ? Math.Min(observation.SlotLimit, resident.TotalSlots) : observation.SlotLimit;
        var free = slots - observation.Inflight - (state.PhysicalUses - state.LogicalOverlap);
        return free > 0 && (free > 1 || state.OpeningOwner is not { } owner || owner == claim ||
            !IsOpen(owner) || !OpeningOwnerEligible(owner, observation.Capability)) &&
            (state.Domain.ActivationOwner is not { } activation || ReferenceEquals(activation, claim.Ticket));
    }

    private bool OpeningOwnerEligible(Claim owner, EngineCapability capability) =>
        CanPrepare(owner) &&
        owner.Request.Thread.Inflight < owner.Request.Thread.MaxInflight &&
        owner.ExplicitContext <= (owner.SwitchRequired ? Window(owner) : capability.Serving.WindowFor(owner.Target.Model));

    private bool CanPrepare(Claim claim) => claim.Ticket.CanPrepareAdmission && !claim.Request.QueueExpired(_clock);

    private bool AttemptSpent(Claim claim)
    {
        var binding = new Binding(claim.Target, claim.SwitchRequired, claim.ExplicitContext);
        return _spent.TryGetValue(claim.Ticket, out var spent) &&
            spent.TryGetValue(binding, out var generation) && generation >= claim.Generation;
    }

    internal void Prepare(Claim claim)
    {
        Action? work;
        lock (Gate) work = ReservePreparationWork(claim);
        work?.Invoke();
    }

    // Called under Gate, and under the router and queue gates for an ordinary pending ticket.
    // Run the resulting work after releasing all three gates.
    internal Action? ReservePreparationWork(Claim claim)
    {
        if (_stopped || !CanPrepare(claim) || StateFor(claim) is not { } state ||
            state.Generation != claim.Generation || (claim.Resident is not null && !IsOpen(claim)) ||
            IsOtherDomain(claim.Ticket, claim.Target.Engine)) return null;

        var domain = state.Engine.Domain;
        var ownerState = _routes.TryGetValue(claim.Target, out var route) && route.Engine.OpeningOwner == claim
            ? route.Engine : null;
        if (_preparations.TryGetValue(claim.Ticket, out var current))
        {
            if (current.Claim == claim) return null;
            if (ownerState is not null) ownerState.OwnerPreparationPending = true;
            // A different viable route cannot replace an active attempt. The old domain
            // keeps the charge until a superseded preparation actually finishes cleanup.
            return !IsOpen(current.Claim) && Retire(current) ? current.RequestRetirement : null;
        }

        if (domain.Preparations >= domain.Limit || ownerState is not null && !HasThreadRoom(claim))
        {
            if (ownerState is not null) ownerState.OwnerPreparationPending = true;
            return null;
        }
        if (ownerState is null && (!IsOpen(claim) || HasRunnablePendingOwner(domain))) return null;

        if (ownerState is not null) ownerState.OwnerPreparationPending = false;
        return ReservePreparation(claim, domain) is { } start ? () => _ = Task.Run(() => PrepareAsync(start)) : null;
    }

    // Called under Gate. Retirement can free capacity after the dispatch pass has already
    // checked pending owners. Ordinary preparation must leave that capacity available.
    private static bool HasThreadRoom(Claim owner) => owner.Request.Thread.Inflight < owner.Request.Thread.MaxInflight;

    private bool HasRunnablePendingOwner(DomainState domain) => domain.Engines.Any(state =>
        state.OwnerPreparationPending && state.OpeningOwner is { } owner &&
        CanPrepare(owner) && HasThreadRoom(owner) && !_preparations.ContainsKey(owner.Ticket));

    // The detached owner has no queue head to rescan. Its exact request survives a full table
    // or a prior attempt until a dispatch pass can reserve capacity for it.
    internal void ResumePendingOpeningOwnerPreparations()
    {
        List<Preparation>? starts = null;
        lock (Gate)
        {
            if (_stopped) return;
            foreach (var state in _engines.Values)
            {
                var domain = state.Domain;
                if (domain.Preparations >= domain.Limit) continue;
                if (!state.OwnerPreparationPending || state.OpeningOwner is not { } owner ||
                    _preparations.ContainsKey(owner.Ticket) || !HasThreadRoom(owner))
                    continue;

                state.OwnerPreparationPending = false;
                if (CanPrepare(owner) && ReservePreparation(owner, domain) is { } start)
                    (starts ??= []).Add(start);
                else
                    state.ClearOpeningOwner();
            }
        }
        if (starts is not null)
            foreach (var start in starts) _ = Task.Run(() => PrepareAsync(start));
    }

    // Called under Gate after checking the preparation limit and the ticket's outstanding entry.
    private Preparation? ReservePreparation(Claim claim, DomainState domain)
    {
        var binding = new Binding(claim.Target, claim.SwitchRequired, claim.ExplicitContext);
        var spent = _spent.GetOrCreateValue(claim.Ticket);
        if (spent.TryGetValue(binding, out var generation) && generation >= claim.Generation) return null;

        spent[binding] = claim.Generation;
        var start = new Preparation(claim, domain);
        _preparations.Add(claim.Ticket, start);
        domain.Preparations++;
        return start;
    }

    private void RemovePreparation(Preparation entry)
    {
        _preparations.Remove(entry.Claim.Ticket);
        entry.Domain.Preparations--;
    }

    private async Task PrepareAsync(Preparation entry)
    {
        var published = false;
        Exception? cleanupError = null;
        using var retirement = new CancellationTokenSource();
        var cancellation = ObserveRetirementAsync(entry, retirement);
        try
        {
            // The worker receives and owns the prepared resource until retirement or execution
            // signals cleanup. Dispatch only transfers the counted invocation, not this resource.
            await using var resource = await AcquirePreparationAsync(entry.Claim, retirement.Token).ConfigureAwait(false);
            var sender = resource as VerifiedPreparedSender;
            var bindingMatches = entry.Claim.Resident is null || sender?.Matches(entry.Claim) == true;
            if (!bindingMatches && resource is not null)
                _rejectBinding?.Invoke(entry.Claim, VerifiedDispatchRefusal.Target);
            lock (Gate)
            {
                published = resource is not null && bindingMatches && !entry.Retiring && !_stopped &&
                    entry.Claim.Ticket.CanPrepareAdmission && !entry.Claim.Request.QueueExpired(_clock);
                entry.VerifiedSender = published ? sender : null;
                entry.Ready = published;
                entry.Retiring = !published;
            }
            if (published)
            {
                _wake?.Invoke();
                AfterPreparationForTests?.Invoke(entry.Claim, true);
                await entry.CleanupRequested.Task.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            cleanupError = ex;
        }
        finally
        {
            entry.WorkFinished.TrySetResult();
            await cancellation.ConfigureAwait(false);
            if (cleanupError is null)
            {
                lock (Gate)
                {
                    if (_preparations.TryGetValue(entry.Claim.Ticket, out var current) && ReferenceEquals(current, entry))
                    {
                        RemovePreparation(entry);
                        if (!published && _routes.TryGetValue(entry.Claim.Target, out var route) &&
                            route.Engine.OpeningOwner == entry.Claim)
                            route.Engine.ClearOpeningOwner();
                    }
                }
                _wake?.Invoke();
            }
            else
            {
                if (!entry.Borrowed || entry.VerifiedSender is null)
                    _reportCleanupFailure?.Invoke(entry.Claim, entry.Claim.Resident is null ? cleanupError :
                        new InvalidOperationException("Verified preparation cleanup could not be confirmed."));
            }

            // A fault does not establish retirement. Keep a retiring entry or, after
            // authorization, the counted physical use until terminal proof is available.
            entry.CleanupFinished.TrySetResult(cleanupError);
            AfterPreparationCleanupForTests?.Invoke(entry.Claim, cleanupError);
            if (!published) AfterPreparationForTests?.Invoke(entry.Claim, false);
        }
    }

    private static async Task ObserveRetirementAsync(Preparation entry, CancellationTokenSource source)
    {
        await Task.WhenAny(entry.RetirementRequested.Task, entry.WorkFinished.Task).ConfigureAwait(false);
        if (!entry.RetirementRequested.Task.IsCompleted) return;
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { /* A throwing registration cannot revoke the cancellation signal. */ }
    }

    private async ValueTask<IAsyncDisposable?> AcquirePreparationAsync(Claim claim, CancellationToken ct)
    {
        try
        {
            if (BeforePrepareForTests is { } before) await before(claim, ct).ConfigureAwait(false);
            return claim.Resident is null
                ? await prepare(claim, ct).ConfigureAwait(false)
                : await prepareVerified!(claim, ct).ConfigureAwait(false);
        }
        catch (VerifiedDispatchBindingException error) when (claim.Resident is not null)
        {
            _rejectBinding?.Invoke(claim, error.Refusal);
            return null;
        }
        catch (Exception ex)
        {
            // An unsuccessful attempt spends this revision. Only new claim inputs can rearm it.
            AfterPreparationFaultForTests?.Invoke(claim, ex);
            return null;
        }
    }

    internal bool Invalidate(RouterTicket ticket)
    {
        Preparation? cleanup = null;
        var ownerReleased = false;
        var cancelledActivation = false;
        lock (Gate)
        {
            ticket.RevokeAdmission();
            if (_preparations.TryGetValue(ticket, out var entry) && Retire(entry)) cleanup = entry;
            foreach (var domain in _domains.Values)
            {
                if (!ReferenceEquals(domain.ActivationOwner, ticket)) continue;
                domain.ActivationOwner = null;
                cancelledActivation = domain.Transitioning;
                ownerReleased = true;
            }
            foreach (var state in _engines.Values)
            {
                if (state.OpeningOwner is not { } owner || !ReferenceEquals(owner.Ticket, ticket)) continue;
                state.ClearOpeningOwner();
                ownerReleased = true;
            }
        }
        cleanup?.RequestRetirement();
        if (ownerReleased) _wake?.Invoke();
        return cancelledActivation;
    }

    internal void Stop()
    {
        List<Preparation> cleanup = [];
        lock (Gate)
        {
            _stopped = true;
            foreach (var entry in _preparations.Values)
                if (Retire(entry)) cleanup.Add(entry);
        }
        foreach (var entry in cleanup) entry.RequestRetirement();
    }

    private static bool Retire(Preparation entry)
    {
        if (entry.Retiring) return false;
        entry.Retiring = true;
        return true;
    }

    // Only called inside Gate -> router -> queue. No callbacks or I/O here.
    internal bool IsPrepared(Claim claim) => _preparations.TryGetValue(claim.Ticket, out var entry) &&
        claim.Ticket.CanPrepareAdmission && !entry.Retiring && entry.Claim == claim && entry.Ready;

    internal Invocation Authorize(Claim claim, EngineRuntime engine, bool detachedOwner = false)
    {
        var prepared = _preparations[claim.Ticket];
        prepared.Borrowed = true;
        RemovePreparation(prepared);
        var route = StateFor(claim)!;
        var state = route.Engine;
        var uses = route.Uses;
        if (uses is not null) uses.Count++;
        if (ReferenceEquals(state.Domain.ActivationOwner, claim.Ticket)) state.Domain.ActivationOwner = null;
        if (state.OpeningOwner == claim) state.ClearOpeningOwner();
        state.PhysicalUses++;
        if (!detachedOwner) state.LogicalOverlap++;
        if (claim.Request.Request.Options?.Profile is not null) engine.RetainNamedUse();
        return new Invocation(this, claim, engine, prepared.FinishAsync, detachedOwner, prepared.VerifiedSender, uses);
    }

    internal sealed class Invocation(ExactTicketAdmission admission, Claim claim, EngineRuntime runtime,
                                     Func<ValueTask> finishPreparation, bool detachedOwner,
                                     VerifiedPreparedSender? verifiedSender, CommissionedRouteUses? uses)
    {
        private const int Authorized = 0;
        private const int Submitting = 1;
        private const int Released = 2;
        private const int Unclaimed = 0;
        private const int ExecutionOwner = 1;
        private const int AbortOwner = 2;
        private readonly TaskCompletionSource _abortFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;
        private int _owner;
        private int _physicalReleased;
        private bool _overlapsLogical = !detachedOwner; // guarded by the admission gate

        internal bool IsCommissioned => uses is not null;

        internal long VerifiedCredentialEpoch => claim.Resident?.Credential.Epoch ?? 0;

        internal void EndOrdinaryUse(EngineRuntime engine, long promptChars)
        {
            lock (admission.Gate)
            {
                engine.EndRequest(promptChars);
                RemoveOverlap(admission._engines[claim.Target.Engine]);
            }
        }

        private void RemoveOverlap(EngineAdmissionState state)
        {
            if (!_overlapsLogical) return;
            _overlapsLogical = false;
            state.LogicalOverlap--;
        }

        private void ReleasePhysicalUse()
        {
            if (Interlocked.Exchange(ref _physicalReleased, 1) != 0) return;
            lock (admission.Gate)
            {
                var state = admission._engines[claim.Target.Engine];
                RemoveOverlap(state);
                state.PhysicalUses--;
                if (uses is not null && --uses.Count == 0 && uses.Sealed) uses.Zero.TrySetResult();
                if (claim.Request.Request.Options?.Profile is not null) runtime.ReleaseNamedUse();
                admission._releases++;
            }
            admission._wake?.Invoke();
            admission.AfterReleaseForTests?.Invoke();
        }

        internal async Task RunAsync(HttpRequestMessage request, Func<HttpResponseMessage, Task> consume,
                                     Action<Exception>? reportFailure, CancellationToken ct, long credentialEpoch = 0)
        {
            var owner = Interlocked.CompareExchange(ref _owner, ExecutionOwner, Unclaimed);
            if (owner == AbortOwner)
            {
                // Abort callers observe any cleanup fault. A late execution still lost before submission.
                await _abortFinished.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                throw new OperationCanceledException("The counted invocation was aborted before execution.", ct);
            }
            if (owner == ExecutionOwner)
                throw new InvalidOperationException("The counted invocation is already owned by execution.");
            VerifiedTransportOperation? operation = null;
            try
            {
                using var cancellation = ct.UnsafeRegister(static value => ((Invocation)value!).CancelBeforeSubmit(), this);
                if (admission.BeforeSubmitForTests is { } before)
                    await before(claim).ConfigureAwait(false);
                if (Interlocked.CompareExchange(ref _state, Submitting, Authorized) != Authorized)
                    throw new OperationCanceledException(ct);
                if (admission.BeforeSendForTests is { } beforeSend)
                    await beforeSend(this).ConfigureAwait(false);

                if (verifiedSender is null)
                {
                    await runtime.VerifyRequestedProfileAsync(claim.Request, ct).ConfigureAwait(false);
                    using var response = await admission.SendAsync(request, ct).ConfigureAwait(false);
                    await consume(response).ConfigureAwait(false);
                }
                else
                {
                    admission.BeforeVerifiedOfferForTests?.Invoke(request);
                    operation = await verifiedSender.StartIfProfileReadyAsync(runtime, request, credentialEpoch,
                        () => admission.IsCurrentManagedOffer(claim), ct)
                        .ConfigureAwait(false);
                    if (operation is null)
                    {
                        reportFailure?.Invoke(EngineRuntime.ProfileNoLongerReady());
                        return;
                    }
                    var response = await operation.Primary.ConfigureAwait(false);
                    await consume(response).ConfigureAwait(false);
                    admission.AfterVerifiedConsumeForTests?.Invoke(response);
                }
            }
            catch (Exception ex)
            {
                // The injected logical result may settle while physical cleanup is still held.
                if (verifiedSender is not null)
                {
                    // The transport's raw exception can include request or credential information.
                    Exception safe = ex is OperationCanceledException
                        ? new OperationCanceledException("The verified request was cancelled.", ct)
                        : new HttpRequestException("The verified request could not be delivered.");
                    reportFailure?.Invoke(safe);
                    return;
                }
                reportFailure?.Invoke(ex);
                throw;
            }
            finally
            {
                if (verifiedSender is null) await FinishOwnedAsync().ConfigureAwait(false);
                else await FinishVerifiedAsync(operation).ConfigureAwait(false);
            }
        }

        // Execution has its own task through response cleanup. Abort owns a shared completion only
        // when it prevents execution from starting.
        internal ValueTask AbortAsync() => Interlocked.CompareExchange(ref _owner, AbortOwner, Unclaimed) switch
        {
            Unclaimed => FinishAbortAsync(),
            AbortOwner => new ValueTask(_abortFinished.Task),
            _ => ValueTask.CompletedTask,
        };

        private async ValueTask FinishAbortAsync()
        {
            try
            {
                if (verifiedSender is null) await FinishOwnedAsync().ConfigureAwait(false);
                else await FinishVerifiedAsync(operation: null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _abortFinished.SetException(ex);
                await _abortFinished.Task.ConfigureAwait(false);
                return;
            }
            _abortFinished.SetResult();
        }

        private void CancelBeforeSubmit()
        {
            if (Interlocked.CompareExchange(ref _state, Released, Authorized) == Authorized && uses is null)
                ReleasePhysicalUse();
        }

        private async ValueTask FinishVerifiedAsync(VerifiedTransportOperation? operation)
        {
            operation?.EndBorrow();
            CancelBeforeSubmit();
            Exception? preparationFailure = null;
            // Start preparation cleanup even while the issuer's terminal result is held.
            try { await finishPreparation().ConfigureAwait(false); }
            catch (Exception error) { preparationFailure = error; }
            var terminal = operation is null ? new VerifiedTerminal(VerifiedRetirement.NoUse)
                : await operation.Terminal.ConfigureAwait(false);

            if (terminal.Conclusive && preparationFailure is null)
            {
                if (Interlocked.CompareExchange(ref _state, Released, Submitting) == Submitting || uses is not null)
                    ReleasePhysicalUse();
            }
            if (!terminal.Conclusive || preparationFailure is not null || terminal.ResponseCleanupFailure is not null)
            {
                List<string> faults = [];
                if (terminal.StreamCloseFailure is not null) faults.Add("stream close");
                if (terminal.PermitFailure is not null) faults.Add("issuer permit retirement");
                if (terminal.ResponseCleanupFailure is not null) faults.Add("response cleanup");
                if (preparationFailure is not null) faults.Add("preparation cleanup");
                if (faults.Count == 0) faults.Add("issuer outcome");
                admission._reportCleanupFailure?.Invoke(claim,
                    new InvalidOperationException($"Verified request cleanup failed: {string.Join(", ", faults)}."));
            }
        }

        private async ValueTask FinishOwnedAsync()
        {
            // If submission never won (including a failure before dispatch starts), prevent
            // any future submission before releasing the count. A submitted use releases
            // only after the preparation worker reports completed cleanup.
            CancelBeforeSubmit();
            await finishPreparation().ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _state, Released, Submitting) == Submitting)
                ReleasePhysicalUse();
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}
