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
    internal readonly record struct Route(string Engine, string Model, string? Profile);
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
    }

    private sealed class EngineAdmissionState
    {
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

    private sealed class Preparation(Claim claim)
    {
        internal Claim Claim { get; } = claim;
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
    private int _preparationLimit;
    private int _releases;
    private bool _stopped;

    internal object Gate { get; } = new();
    internal Func<Claim, Task>? BeforeSubmitForTests { get; set; }
    internal Func<Invocation, Task>? BeforeSendForTests { get; set; }
    internal Action<HttpRequestMessage>? BeforeVerifiedOfferForTests { get; set; }
    internal Action<Claim, Exception?>? AfterPreparationCleanupForTests { get; set; }
    internal Action<Claim, bool>? AfterPreparationForTests { get; set; }
    internal Action<Claim, Exception>? AfterPreparationFaultForTests { get; set; }
    internal Action? AfterReleaseForTests { get; set; }
    internal int PhysicalUses { get { lock (Gate) return _engines.Values.Sum(static state => state.PhysicalUses); } }
    internal int Releases { get { lock (Gate) return _releases; } }
    internal int PreparationCount { get { lock (Gate) return _preparations.Count; } }

    internal bool UsesVerifiedDispatch => prepareVerified is not null;

    internal void Attach(int engineCount, TimeProvider clock, Action wake, Action<Claim, Exception> reportCleanupFailure,
                         Action<Claim, VerifiedDispatchRefusal>? rejectBinding = null)
    {
        _preparationLimit = Math.Max(2, engineCount);
        _clock = clock;
        _wake = wake;
        _reportCleanupFailure = reportCleanupFailure;
        _rejectBinding = rejectBinding;
    }

    internal void Publish(Route target, long generation, bool open, long window = 0)
    {
        List<Preparation> cleanup = [];
        lock (Gate)
        {
            if (_stopped) return;
            if (!_routes.TryGetValue(target, out var route))
            {
                if (!_engines.TryGetValue(target.Engine, out var engine))
                    _engines.Add(target.Engine, engine = new());
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
    internal bool PublishVerified(VerifiedDispatchResident resident)
    {
        List<Preparation> cleanup = [];
        lock (Gate)
        {
            if (_stopped || !UsesVerifiedDispatch || !resident.Target.Matches(resident.Route)) return false;
            var engineName = resident.Route.Engine;
            if (!_verifiedRoutes.TryGetValue(engineName, out var route))
            {
                if (!_engines.TryGetValue(engineName, out var engine))
                    _engines.Add(engineName, engine = new());
                _verifiedRoutes.Add(engineName, route = new(engine));
            }
            if (resident.Route.RouteIncarnation <= route.Generation) return false;

            route.Generation = resident.Route.RouteIncarnation;
            route.Resident = resident;
            route.Open = true;
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
                    string.Equals(target.Model, resident.Target.Model, StringComparison.Ordinal)
                    ? new Claim(request, target, verified.Generation, switchRequired, explicitContext, resident) : null;
            }
            return _routes.TryGetValue(target, out var state)
                ? new Claim(request, target, state.Generation, switchRequired, explicitContext) : null;
        }
    }

    internal bool IsOpen(Claim claim) => !_stopped && StateFor(claim) is { Open: true } route &&
        route.Generation == claim.Generation && ReferenceEquals(route.Resident, claim.Resident);

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
            invocation = Authorize(owner, detachedOwner: true);
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
            !IsOpen(owner) || !OpeningOwnerEligible(owner, observation.Capability));
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
        Preparation? start = null;
        Preparation? cleanup = null;
        lock (Gate)
        {
            if (_stopped || !CanPrepare(claim) || (claim.Resident is not null && !IsOpen(claim))) return;
            var ownerState = _routes.TryGetValue(claim.Target, out var route) && route.Engine.OpeningOwner == claim
                ? route.Engine : null;
            if (_preparations.TryGetValue(claim.Ticket, out var current))
            {
                if (current.Claim != claim)
                {
                    if (ownerState is not null) ownerState.OwnerPreparationPending = true;
                    if (Retire(current)) cleanup = current;
                }
            }
            else if (_preparations.Count >= _preparationLimit)
            {
                if (ownerState is not null) ownerState.OwnerPreparationPending = true;
            }
            else if (ownerState is not null || (IsOpen(claim) && !HasRunnablePendingOwner()))
            {
                if (ownerState is not null) ownerState.OwnerPreparationPending = false;
                start = ReservePreparation(claim);
            }
        }
        cleanup?.RequestRetirement();
        if (start is not null) _ = Task.Run(() => PrepareAsync(start));
    }

    // Called under Gate. Retirement can free capacity after the dispatch pass has already
    // checked pending owners. Ordinary preparation must leave that capacity available.
    private bool HasRunnablePendingOwner() => _engines.Values.Any(state =>
        state.OwnerPreparationPending && state.OpeningOwner is { } owner &&
        CanPrepare(owner) && !_preparations.ContainsKey(owner.Ticket));

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
                if (_preparations.Count >= _preparationLimit) break;
                if (!state.OwnerPreparationPending || state.OpeningOwner is not { } owner ||
                    _preparations.ContainsKey(owner.Ticket))
                    continue;

                state.OwnerPreparationPending = false;
                if (CanPrepare(owner) && ReservePreparation(owner) is { } start)
                    (starts ??= []).Add(start);
                else
                    state.ClearOpeningOwner();
            }
        }
        if (starts is not null)
            foreach (var start in starts) _ = Task.Run(() => PrepareAsync(start));
    }

    // Called under Gate after checking the preparation limit and the ticket's outstanding entry.
    private Preparation? ReservePreparation(Claim claim)
    {
        var binding = new Binding(claim.Target, claim.SwitchRequired, claim.ExplicitContext);
        var spent = _spent.GetOrCreateValue(claim.Ticket);
        if (spent.TryGetValue(binding, out var generation) && generation >= claim.Generation) return null;

        spent[binding] = claim.Generation;
        var start = new Preparation(claim);
        _preparations.Add(claim.Ticket, start);
        return start;
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
                        _preparations.Remove(entry.Claim.Ticket);
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

    internal void Invalidate(RouterTicket ticket)
    {
        Preparation? cleanup = null;
        var ownerReleased = false;
        lock (Gate)
        {
            ticket.RevokeAdmission();
            if (_preparations.TryGetValue(ticket, out var entry) && Retire(entry)) cleanup = entry;
            foreach (var state in _engines.Values)
            {
                if (state.OpeningOwner is not { } owner || !ReferenceEquals(owner.Ticket, ticket)) continue;
                state.ClearOpeningOwner();
                ownerReleased = true;
            }
        }
        cleanup?.RequestRetirement();
        if (ownerReleased) _wake?.Invoke();
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

    internal Invocation Authorize(Claim claim, bool detachedOwner = false)
    {
        var prepared = _preparations[claim.Ticket];
        prepared.Borrowed = true;
        _preparations.Remove(claim.Ticket);
        var state = StateFor(claim)!.Engine;
        if (state.OpeningOwner == claim) state.ClearOpeningOwner();
        state.PhysicalUses++;
        if (!detachedOwner) state.LogicalOverlap++;
        return new Invocation(this, claim, prepared.FinishAsync, detachedOwner, prepared.VerifiedSender);
    }

    internal sealed class Invocation(ExactTicketAdmission admission, Claim claim,
                                     Func<ValueTask> finishPreparation, bool detachedOwner,
                                     VerifiedPreparedSender? verifiedSender)
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
        private bool _overlapsLogical = !detachedOwner; // guarded by the admission gate

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
            lock (admission.Gate)
            {
                var state = admission._engines[claim.Target.Engine];
                RemoveOverlap(state);
                state.PhysicalUses--;
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
                    using var response = await admission.SendAsync(request, ct).ConfigureAwait(false);
                    await consume(response).ConfigureAwait(false);
                }
                else
                {
                    admission.BeforeVerifiedOfferForTests?.Invoke(request);
                    operation = await verifiedSender.StartAsync(request, credentialEpoch, ct).ConfigureAwait(false);
                    var response = await operation.Primary.ConfigureAwait(false);
                    await consume(response).ConfigureAwait(false);
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
            if (Interlocked.CompareExchange(ref _state, Released, Authorized) == Authorized)
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
                if (Interlocked.CompareExchange(ref _state, Released, Submitting) == Submitting)
                    ReleasePhysicalUse();
            }
            else
            {
                List<string> faults = [];
                if (terminal.StreamCloseFailure is not null) faults.Add("stream close");
                if (terminal.PermitFailure is not null) faults.Add("issuer permit retirement");
                if (terminal.ResponseCleanupFailure is not null) faults.Add("response cleanup");
                if (preparationFailure is not null) faults.Add("preparation cleanup");
                if (faults.Count == 0) faults.Add("issuer outcome");
                admission._reportCleanupFailure?.Invoke(claim,
                    new InvalidOperationException($"Verified request cleanup could not be confirmed: {string.Join(", ", faults)}."));
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
