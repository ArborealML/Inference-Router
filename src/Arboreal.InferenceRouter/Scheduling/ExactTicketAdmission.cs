using System.Runtime.CompilerServices;

namespace Arboreal.InferenceRouter;

/// <summary>
/// Internal admission boundary for the injected sender. A serving-route publisher and physical
/// exchange owner must be supplied by the enrolled host before this can guard real upstream traffic.
/// </summary>
internal sealed class ExactTicketAdmission(
    Func<ExactTicketAdmission.Claim, CancellationToken, ValueTask<IAsyncDisposable?>> prepare,
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
{
    internal readonly record struct Route(string Engine, string Model, string? Profile);
    internal readonly record struct Claim(QueuedRequest Request, Route Target, long Generation, bool SwitchRequired,
                                          long ExplicitContext)
    {
        internal RouterTicket Ticket => Request.Ticket;
    }

    private readonly record struct Binding(Route Target, bool SwitchRequired, long ExplicitContext);
    private sealed class RouteState
    {
        internal long Generation;
        internal bool Open;
        internal long Window;
        internal int DetachedOwnerUses;
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
    private readonly Dictionary<RouterTicket, Preparation> _preparations = [];
    private readonly ConditionalWeakTable<RouterTicket, Dictionary<Binding, long>> _spent = new();
    private Action? _wake;
    private TimeProvider _clock = TimeProvider.System;
    private int _preparationLimit;
    private int _physicalUses;
    private int _releases;
    private bool _stopped;

    internal object Gate { get; } = new();
    internal Func<Claim, Task>? BeforeSubmitForTests { get; set; }
    internal Func<Invocation, Task>? BeforeSendForTests { get; set; }
    internal Action<Claim, Exception?>? AfterPreparationCleanupForTests { get; set; }
    internal Action<Claim, bool>? AfterPreparationForTests { get; set; }
    internal Action<Claim, Exception>? AfterPreparationFaultForTests { get; set; }
    internal Action? AfterReleaseForTests { get; set; }
    internal int PhysicalUses { get { lock (Gate) return _physicalUses; } }
    internal int Releases { get { lock (Gate) return _releases; } }
    internal int PreparationCount { get { lock (Gate) return _preparations.Count; } }

    internal void Attach(int engineCount, TimeProvider clock, Action wake)
    {
        _preparationLimit = Math.Max(2, engineCount);
        _clock = clock;
        _wake = wake;
    }

    internal void Publish(Route target, long generation, bool open, long window = 0)
    {
        List<Preparation> cleanup = [];
        lock (Gate)
        {
            if (_stopped) return;
            if (!_routes.TryGetValue(target, out var route))
                _routes.Add(target, route = new());
            if (generation < route.Generation) return;
            if (generation == route.Generation && route.Open == open && route.Window == window) return;
            if (generation != route.Generation) route.ClearOpeningOwner();
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

    // The dispatcher carries this immutable stamp across preparation and commit. It can never
    // authorize from a freshly looked-up stamp that it did not rank.
    internal Claim? Capture(QueuedRequest request, Route target, bool switchRequired, long explicitContext)
    {
        lock (Gate)
            return !_stopped && _routes.TryGetValue(target, out var state)
                ? new Claim(request, target, state.Generation, switchRequired, explicitContext)
                : null;
    }

    internal bool IsOpen(Claim claim) => !_stopped &&
        _routes.TryGetValue(claim.Target, out var route) && route.Open && route.Generation == claim.Generation;

    internal long Window(Claim claim) => _routes[claim.Target].Window;

    // Only the offline host fixture stages an owner. The production owner is not promoted
    // through this path. A reservation has no physical use until authorization at open.
    internal bool StageOpeningOwnerForTests(Claim owner)
    {
        lock (Gate)
        {
            if (_stopped || !_routes.TryGetValue(owner.Target, out var route) || route.Open ||
                route.Generation != owner.Generation || route.OpeningOwner is not null ||
                !CanPrepare(owner) || AttemptSpent(owner))
                return false;
            route.OpeningOwner = owner;
            return true;
        }
    }

    internal Invocation? AuthorizeOpeningOwnerForTests(Claim owner, int availableSlots, long window)
    {
        Invocation invocation;
        lock (Gate)
        {
            if (!IsOpen(owner) || _routes[owner.Target].OpeningOwner != owner ||
                !IsPrepared(owner) || !OpeningOwnerEligible(owner, window) || !CanTake(owner, availableSlots, window))
                return null;
            invocation = Authorize(owner, detachedOwner: true);
        }
        // The detached caller has no dispatch loop iteration to observe this freed preparation slot.
        _wake?.Invoke();
        return invocation;
    }

    // Called under Gate as part of the exact take. Holding aside a final unit for a live
    // owner does not prevent ordinary use of additional free units.
    internal bool CanTake(Claim claim, int availableSlots, long window)
    {
        var route = _routes[claim.Target];
        var free = availableSlots - route.DetachedOwnerUses;
        return free > 0 && (free > 1 || route.OpeningOwner is not { } owner || owner == claim ||
            !OpeningOwnerEligible(owner, window));
    }

    private bool OpeningOwnerEligible(Claim owner, long window) =>
        CanPrepare(owner) &&
        owner.Request.Thread.Inflight < owner.Request.Thread.MaxInflight && owner.ExplicitContext <= window;

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
            if (_stopped || !CanPrepare(claim)) return;
            var ownerRoute = _routes.TryGetValue(claim.Target, out var route) && route.OpeningOwner == claim
                ? route : null;
            if (_preparations.TryGetValue(claim.Ticket, out var current))
            {
                if (current.Claim != claim)
                {
                    if (ownerRoute is not null) ownerRoute.OwnerPreparationPending = true;
                    if (Retire(current)) cleanup = current;
                }
            }
            else if (_preparations.Count >= _preparationLimit)
            {
                if (ownerRoute is not null) ownerRoute.OwnerPreparationPending = true;
            }
            else if (ownerRoute is not null || (IsOpen(claim) && !HasRunnablePendingOwner()))
            {
                if (ownerRoute is not null) ownerRoute.OwnerPreparationPending = false;
                start = ReservePreparation(claim);
            }
        }
        cleanup?.RequestRetirement();
        if (start is not null) _ = Task.Run(() => PrepareAsync(start));
    }

    // Called under Gate. Retirement can free capacity after the dispatch pass has already
    // checked pending owners. Ordinary preparation must leave that capacity available.
    private bool HasRunnablePendingOwner() => _routes.Values.Any(route =>
        route.OwnerPreparationPending && route.OpeningOwner is { } owner &&
        CanPrepare(owner) && !_preparations.ContainsKey(owner.Ticket));

    // The detached owner has no queue head to rescan. Its exact request survives a full table
    // or a prior attempt until a dispatch pass can reserve capacity for it.
    internal void ResumePendingOpeningOwnerPreparations()
    {
        List<Preparation>? starts = null;
        lock (Gate)
        {
            if (_stopped) return;
            foreach (var route in _routes.Values)
            {
                if (_preparations.Count >= _preparationLimit) break;
                if (!route.OwnerPreparationPending || route.OpeningOwner is not { } owner ||
                    _preparations.ContainsKey(owner.Ticket))
                    continue;

                route.OwnerPreparationPending = false;
                if (CanPrepare(owner) && ReservePreparation(owner) is { } start)
                    (starts ??= []).Add(start);
                else
                    route.ClearOpeningOwner();
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
            lock (Gate)
            {
                published = resource is not null && !entry.Retiring && !_stopped &&
                    entry.Claim.Ticket.CanPrepareAdmission && !entry.Claim.Request.QueueExpired(_clock);
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
                            route.OpeningOwner == entry.Claim)
                            route.ClearOpeningOwner();
                    }
                }
                _wake?.Invoke();
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
            return await prepare(claim, ct).ConfigureAwait(false);
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
            foreach (var route in _routes.Values)
            {
                if (route.OpeningOwner is not { } owner || !ReferenceEquals(owner.Ticket, ticket)) continue;
                route.ClearOpeningOwner();
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
        _preparations.Remove(claim.Ticket);
        var route = _routes[claim.Target];
        if (route.OpeningOwner == claim) route.ClearOpeningOwner();
        if (detachedOwner) route.DetachedOwnerUses++;
        _physicalUses++;
        return new Invocation(this, claim, prepared.FinishAsync, detachedOwner);
    }

    private void Release(Claim claim, bool detachedOwner)
    {
        lock (Gate)
        {
            if (detachedOwner) _routes[claim.Target].DetachedOwnerUses--;
            _physicalUses--;
            _releases++;
        }
        _wake?.Invoke();
        AfterReleaseForTests?.Invoke();
    }

    internal sealed class Invocation(ExactTicketAdmission admission, Claim claim,
                                     Func<ValueTask> finishPreparation, bool detachedOwner)
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

        internal async Task RunAsync(HttpRequestMessage request, Func<HttpResponseMessage, Task> consume,
                                     Action<Exception>? reportFailure, CancellationToken ct)
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
            try
            {
                using var cancellation = ct.UnsafeRegister(static value => ((Invocation)value!).CancelBeforeSubmit(), this);
                if (admission.BeforeSubmitForTests is { } before)
                    await before(claim).ConfigureAwait(false);
                if (Interlocked.CompareExchange(ref _state, Submitting, Authorized) != Authorized)
                    throw new OperationCanceledException(ct);
                if (admission.BeforeSendForTests is { } beforeSend)
                    await beforeSend(this).ConfigureAwait(false);

                using var response = await admission.SendAsync(request, ct).ConfigureAwait(false);
                await consume(response).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The injected logical result may settle while physical cleanup is still held.
                reportFailure?.Invoke(ex);
                throw;
            }
            finally
            {
                await FinishOwnedAsync().ConfigureAwait(false);
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
                await FinishOwnedAsync().ConfigureAwait(false);
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
                admission.Release(claim, detachedOwner);
        }

        private async ValueTask FinishOwnedAsync()
        {
            // If submission never won (including a failure before dispatch starts), prevent
            // any future submission before releasing the count. A submitted use releases
            // only after the preparation worker reports completed cleanup.
            CancelBeforeSubmit();
            await finishPreparation().ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _state, Released, Submitting) == Submitting)
                admission.Release(claim, detachedOwner);
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}
