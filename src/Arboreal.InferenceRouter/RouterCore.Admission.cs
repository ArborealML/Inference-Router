using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Arboreal.InferenceRouter;

public sealed partial class RouterCore
{
    // An owner stays registered while it publishes outside the router gate. Concurrent or
    // reentrant setters append to that owner rather than publish a newer priority first.
    private sealed class ThreadPublication
    {
        internal List<QueuedRequest>? Enqueues;
        internal List<ThreadPriority>? PriorityChanges;
    }

    private readonly Dictionary<string, ThreadPublication> _threadPublications = new(StringComparer.Ordinal);
    internal Action<string>? BeforeThreadRegistrationFeedForTests;
    internal Action<ThreadPriority>? BeforeThreadPriorityFeedForTests;
    internal Action<RouterTicket>? BeforeEnqueueFeedForTests;
    internal Action? BeforeEnqueueCommitForTests;
    internal Action<RouterTicket>? BeforeEnqueueEligibilityForTests;
    internal bool SuppressQueueHooksForTests;
    internal long QueueTimestampForTests;

    private long _pendingPriorityRevision;

    public RouterThread RegisterThread(string threadId, ThreadPriority priority, RouterThreadOptions? options = null)
    {
        if (OwnedPolicyEnabled) throw ScopeRequired();
        return ApplyThreadPriority(threadId, priority, options, expected: null)!;
    }

    public void SetThreadPriority(string threadId, ThreadPriority priority)
    {
        if (OwnedPolicyEnabled) throw ScopeRequired();
        ApplyThreadPriority(threadId, priority, options: null, expected: null);
    }

    internal bool ChangeOwnedThreadPriority(RouterThread expected, ThreadPriority priority) =>
        ApplyThreadPriority(expected.ThreadId, priority, options: null, expected) is not null;

    private RouterThread? ApplyThreadPriority(string threadId, ThreadPriority priority, RouterThreadOptions? options,
                                               RouterThread? expected, PolicyGroupKey? owner = null, string? displayId = null)
    {
        RouterThread thread;
        bool created;
        lock (_gate)
        {
            var current = _threads.GetValueOrDefault(threadId);
            if (expected is not null && !ReferenceEquals(expected, current)) return null;
            created = current is null;
            thread = current ?? AddThread(threadId, priority, options?.Weight ?? 1.0, options?.ModelAffinity, owner, displayId);
            if (!created)
            {
                thread.SetPriorityValue(priority);
                var changed = false;
                foreach (var queue in _queues.Values)
                    changed |= queue.RefreshInheritedPriority(thread, priority);
                if (changed) _pendingPriorityRevision++;

                if (_threadPublications.TryGetValue(threadId, out var pending))
                {
                    (pending.PriorityChanges ??= []).Add(priority);
                    return thread;
                }
                _threadPublications.Add(threadId, new ThreadPublication());
            }
        }
        if (created) PublishThreadRegistration(thread, priority);
        else
        {
            try { PublishThreadPriorityChange(thread, priority); }
            finally { DrainThreadPriorityChanges(thread); }
        }
        return thread;
    }

    private void PublishThreadPriorityChange(RouterThread thread, ThreadPriority priority)
    {
        BeforeThreadPriorityFeedForTests?.Invoke(priority);
        try { thread.NotifyPriorityChanged(priority); }
        finally
        {
            _events.Append(new RouterEvent { Kind = "thread.priority_changed", ThreadId = thread.DisplayId ?? thread.ThreadId,
                OwnerGroup = thread.OwnerGroup, Value = priority.Class });
            SignalDispatch();
        }
    }

    private RouterThread AddThread(string id, ThreadPriority priority, double weight, string? affinity,
        PolicyGroupKey? owner = null, string? displayId = null)
    {
        if (owner is not null && _threads.Count >= 4096)
            throw new RouterRequestException("owned_thread_limit", "the router's owned named thread limit is reached");
        var thread = new RouterThread(id, priority, weight, affinity, ChangeOwnedThreadPriority) { OwnerGroup = owner, DisplayId = displayId };
        _threads.Add(id, thread);
        _threadPublications.Add(id, new ThreadPublication { Enqueues = [] });
        return thread;
    }

    private void PublishThreadRegistration(RouterThread thread, ThreadPriority priority)
    {
        BeforeThreadRegistrationFeedForTests?.Invoke(thread.ThreadId);
        _events.Append(new RouterEvent { Kind = "thread.registered", ThreadId = thread.DisplayId ?? thread.ThreadId,
            OwnerGroup = thread.OwnerGroup, Value = priority.Class });
        List<QueuedRequest> enqueues;
        lock (_gate)
        {
            var pending = _threadPublications[thread.ThreadId];
            enqueues = pending.Enqueues!;
            pending.Enqueues = null;
        }

        DrainThreadPriorityChanges(thread);
        foreach (var item in enqueues) PublishEnqueue(item);
    }

    private void DrainThreadPriorityChanges(RouterThread thread)
    {
        while (true)
        {
            List<ThreadPriority> changes;
            lock (_gate)
            {
                var pending = _threadPublications[thread.ThreadId];
                if (pending.PriorityChanges is not { } batch)
                {
                    _threadPublications.Remove(thread.ThreadId);
                    return;
                }

                // Keep ownership while the detached batch is published. New changes go to
                // the next batch, including changes made from a synchronous feed observer.
                changes = batch;
                pending.PriorityChanges = null;
            }
            foreach (var change in changes)
            {
                // The setter has already returned. A deferred priority callback's exception
                // cannot propagate back to it or strand the remaining notifications.
                try { PublishThreadPriorityChange(thread, change); }
                catch (Exception) { }
            }
        }
    }

    private static (string Engine, ServingProfile Tier)? LargestInsufficientConfiguredTier(
        ConfigurationGeneration generation, string model, long requiredContext)
    {
        (string Engine, ServingProfile Tier)? largest = null;
        foreach (var engine in generation.Engines.Values)
        {
            if (!engine.DeclaresModel(model)) continue;

            // Dispatch uses the ordinal ladder for the resolved ticket model. With no ladder,
            // a declaring engine can satisfy the floor with a matching serving observation.
            var tiers = engine.ProfilesForModel(model);
            if (tiers.Count == 0) return null;

            for (var i = 0; i < tiers.Count; i++)
            {
                var tier = tiers[i];
                if (tier.MaxContext >= requiredContext) return null;

                if (largest is { } other)
                {
                    var contextOrder = tier.MaxContext.CompareTo(other.Tier.MaxContext);
                    if (contextOrder < 0) continue;
                    if (contextOrder == 0)
                    {
                        var engineOrder = string.CompareOrdinal(engine.Name, other.Engine);
                        if (engineOrder > 0 || (engineOrder == 0 && string.CompareOrdinal(tier.Name, other.Tier.Name) >= 0))
                            continue;
                    }
                }
                largest = (engine.Name, tier);
            }
        }
        return largest;
    }

    public ValueTask<RouterTicket> EnqueueAsync(string threadId, InferenceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        return EnqueueAsync(threadId, request, httpStream: false, ct);
    }

    internal ValueTask<RouterTicket> EnqueueHttpAsync(string? threadId, InferenceRequest request, CancellationToken ct) =>
        EnqueueAsync(threadId, request, request.Stream, ct);

    private ValueTask<RouterTicket> EnqueueAsync(string? threadId, InferenceRequest request, bool httpStream, CancellationToken ct,
        OwnedTicketPolicy? policy = null)
    {
        if (OwnedPolicyEnabled != (policy is not null)) throw ScopeRequired();
        var displayId = threadId;
        if (policy is not null && threadId is not null) threadId = ScopedThreadKey(policy.Key, threadId);
        var opts = request.Options;
        var explicitContext = opts?.RequiredContext ?? 0;
        var promptChars = PromptCharsProbe(request.Body);
        var createdAt = _clock.GetUtcNow();
        var timestamp = ReadQueueTimestamp();
        while (true)
        {
            ConfigurationGeneration generation;
            RouterThread? observedThread;
            string? affinity;
            lock (_gate)
            {
                if (_closing) throw new RouterRequestException("cancelled", "router shutting down");
                generation = CurrentGeneration;
                observedThread = threadId is null ? null : _threads.GetValueOrDefault(threadId);
                affinity = observedThread?.ModelAffinity;
            }

            var requested = request.Model ?? affinity ?? generation.Settings.DefaultModel;
            if (generation.Engines.Count == 0 && generation.Settings.CommercialProviders.Count != 0)
                throw ControlledDirectChat.Unavailable();
            var model = policy is null ? ResolveCanonical(requested, generation) : ResolveOwnedModel(requested, generation, policy);
            var estimated = EstimateContextTokens(promptChars, generation.Settings.Scheduler.CharsPerTokenEstimate);
            var required = opts?.Profile is not null ? Math.Max(estimated, explicitContext)
                : explicitContext > 0 ? explicitContext : estimated;
            var profileFits = opts?.Profile is not { } profile || generation.Engines.Values.Any(engine =>
                (policy is null || AllowsOwnedRoute(policy, engine.Name)) &&
                engine.GetProfile(model, profile) is { } tier && tier.MaxContext >= required && engine.SupportsNamedProfile(model, tier));
            var hopeless = CapabilityScan.DefinitelyHopeless(generation.Capabilities, model);
            var noFit = policy is null && !hopeless && explicitContext > 0 && _scheduler is PrefixAwareScheduler
                ? LargestInsufficientConfiguredTier(generation, model, explicitContext)
                : null;
            BeforeEnqueueCommitForTests?.Invoke();
            QueuedRequest queued;
            bool publishRegistration;
            bool publishItem;
            ThreadPriority priority;
            lock (_gate)
            {
                if (_closing) throw new RouterRequestException("cancelled", "router shutting down");
                if (policy is not null && _ownedPolicy?.IsCurrent(policy) != true)
                    throw new RouterRequestException("policy_group_unavailable", "the policy changed before the request was admitted");
                var thread = threadId is null ? null : _threads.GetValueOrDefault(threadId);
                if (!ReferenceEquals(generation, CurrentGeneration) ||
                    !ReferenceEquals(thread, observedThread) ||
                    !string.Equals(affinity, thread?.ModelAffinity, StringComparison.Ordinal)) continue;

                if (!profileFits)
                    throw new RouterRequestException("invalid_profile", FormattableString.Invariant(
                        $"Model '{WireText.Ident(model)}' has no serving profile '{WireText.Ident(opts!.Profile!)}' that fits {required} tokens."));

                var ticketId = NewTicketId();
                var diagnosticThreadId = displayId ?? $"request:{ticketId}";
                publishRegistration = threadId is not null && thread is null;
                thread ??= threadId is null
                    ? new RouterThread(diagnosticThreadId, generation.Settings.DefaultPriority, 1.0, null)
                    : AddThread(threadId, opts?.Priority ?? generation.Settings.DefaultPriority, 1.0, null, policy?.Key, displayId);
                priority = opts?.Priority ?? thread.Priority;
                var bufferLimit = generation.Settings.StreamBufferLimitBytes;
                if (policy is not null && bufferLimit <= 0) bufferLimit = OwnedDefaultStreamBufferLimitBytes;
                var ticket = new RouterTicket(ticketId, diagnosticThreadId, model, createdAt)
                {
                    IsStateless = threadId is null,
                    OwnedPolicy = policy,
                    ScopedThreadKey = policy is null ? null : threadId,
                    IsStreamingRequest = request.Stream,
                    IsHttpStream = httpStream,
                    HttpDelivery = httpStream && bufferLimit > 0 ? new HttpStreamDelivery(bufferLimit, ct, _shutdownToken) : null,
                };
                var node = new PrioritizedRequest(ticket, priority, Interlocked.Increment(ref _seq));
                queued = new QueuedRequest
                {
                    Ticket = ticket,
                    Request = request,
                    OriginalModel = requested,
                    PriorityNode = node,
                    Thread = thread,
                    EnqueuedSeq = node.Sequence,
                    EnqueuedAtTimestamp = timestamp,
                    CallerToken = ct,
                    PromptChars = promptChars,
                    RequiredContextTokens = required,
                };

                if (hopeless)
                {
                    IndexRejected(queued, "model_unavailable",
                        $"no engine in this configuration declares or currently reports model '{WireText.Ident(model)}'");
                }
                else if (noFit is { } limit)
                {
                    IndexRejected(queued, "model_unavailable", FormattableString.Invariant(
                        $"model '{WireText.Ident(model)}' needs at least {explicitContext} tokens. Largest configured tier '{WireText.Ident(limit.Tier.Name)}' on engine '{WireText.Ident(limit.Engine)}' has {limit.Tier.MaxContext} tokens."));
                }
                else
                {
                    var queue = GetOrCreateQueue(model, generation.Settings.Scheduler.MaxQueuePerModel, node.Sequence);
                    lock (queue.Gate)
                    {
                        ticket.PendingEnqueuePublication = true;
                        if (queue.TryInsert(queued))
                        {
                            ticket.ExecCts = new CancellationTokenSource();
                            lock (_ticketGate) _tickets.Add(ticket.Id, ticket);
                        }
                        else
                        {
                            ticket.PendingEnqueuePublication = false;
                            RetireQueueIfEmpty(queue);
                            IndexRejected(queued, "queue_overflow", $"queue for {WireText.Ident(model)} is full");
                        }
                    }
                }

                var publication = threadId is null ? null : _threadPublications.GetValueOrDefault(threadId)?.Enqueues;
                publishItem = publication is null;
                publication?.Add(queued);
            }

            if (publishRegistration) PublishThreadRegistration(queued.Thread, priority);
            else if (publishItem) PublishEnqueue(queued);
            return ValueTask.FromResult(queued.Ticket);
        }
    }

    private ModelQueue GetOrCreateQueue(string model, int capacity, long creationOrdinal)
    {
        if (_queues.TryGetValue(model, out var queue)) return queue;
        queue = new ModelQueue(model, capacity, static (_, _) => { }) { CreationOrdinal = creationOrdinal };
        _queues.Add(model, queue);
        return queue;
    }

    // Called under the router gate and queue gate in the same cut as the membership change.
    private void RetireQueueIfEmpty(ModelQueue queue)
    {
        if (queue.Depth == 0 && ReferenceEquals(_queues.GetValueOrDefault(queue.Model), queue))
            _queues.Remove(queue.Model);
    }

    private void IndexRejected(QueuedRequest queued, string reason, string message)
    {
        var ticket = queued.Ticket;
        Interlocked.Increment(ref queued.Thread.Rejected);
        ticket.TryEnterTerminal();
        ticket.ErrorCode = reason;
        ticket.Error = WireText.Bound(message, WireText.ReasonChars);
        ticket.StoreTerminalState(TicketState.Failed);
        lock (_ticketGate)
        {
            _tickets.Add(ticket.Id, ticket);
            RetainTerminalTicket(ticket);
        }
    }

    private void PublishEnqueue(QueuedRequest queued)
    {
        var ticket = queued.Ticket;
        if (ticket.IsTerminal)
        {
            _events.Append(new RouterEvent
            {
                Kind = "queue.rejected", ThreadId = ticket.ThreadId, OwnerGroup = ticket.OwnedPolicy?.Key, TicketId = ticket.Id,
                Model = ticket.Model, Reason = ticket.ErrorCode,
            });
            ticket.SignalTerminalStream();
            ticket.CompletionTcs.TrySetException(new RouterRequestException(ticket.ErrorCode!, ticket.Error!));
            return;
        }

        ArmQueueHooks(queued);
        BeforeEnqueueFeedForTests?.Invoke(ticket);
        _events.Append(new RouterEvent
        {
            Kind = "request.enqueued", ThreadId = ticket.ThreadId, OwnerGroup = ticket.OwnedPolicy?.Key, TicketId = ticket.Id,
            Model = ticket.Model, Value = queued.PromptChars,
        });
        BeforeEnqueueEligibilityForTests?.Invoke(ticket);
        string? end;
        lock (_gate)
        {
            end = ticket.DeferredQueueEnd ?? (_closing ? "cancelled" : ReadQueueEnd(queued));
            ticket.DeferredQueueEnd = end;
            // The router gate excludes exact take, queue cancellation and shutdown across the cut.
            if (_queues.TryGetValue(ticket.Model, out var queue))
            {
                lock (queue.Gate)
                {
                    ticket.PendingEnqueuePublication = false;
                    if (end is not null && queue.TryRemove(ticket))
                        RetireQueueIfEmpty(queue);
                }
            }
            else
            {
                ticket.PendingEnqueuePublication = false;
            }
        }

        if (end is not null) SettleQueueEnd(ticket, end, queued.Request.Options?.MaxQueueTime);
        else SignalDispatch();
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "InstallQueueTimer transfers ownership to the ticket or disposes the losing timer.")]
    private void ArmQueueHooks(QueuedRequest queued)
    {
        if (SuppressQueueHooksForTests) return;
        var ticket = queued.Ticket;
        if (queued.CallerToken.CanBeCanceled)
            ticket.InstallCallerLink(queued.CallerToken.UnsafeRegister(_ => CancelTicket(ticket), null));

        if (queued.Request.Options?.MaxQueueTime is { } window && window > TimeSpan.Zero)
        {
            var remaining = window - ReadQueueElapsed(queued);
            var due = remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
            // Timer accepts at most UInt32.MaxValue - 1 milliseconds; use the same absolute
            // deadline if a long window has to be checked and armed again.
            var maxDue = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
            if (due > maxDue) due = maxDue;
            var timer = new Timer(_ => OnQueueDeadline(queued, window), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            ticket.InstallQueueTimer(timer);
            ticket.ChangeQueueTimer(due);
        }
    }

    private void OnQueueDeadline(QueuedRequest queued, TimeSpan window)
    {
        if (ReadQueueElapsed(queued) < window)
        {
            ReArmQueueTimer(queued, window);
            return;
        }
        if (ClaimQueuedEnd(queued.Ticket, "deadline_exceeded") == QueueEndClaim.Removed)
            SettleQueueEnd(queued.Ticket, "deadline_exceeded", window);
    }

    private void ReArmQueueTimer(QueuedRequest queued, TimeSpan window)
    {
        var remaining = window - ReadQueueElapsed(queued);
        var maxDue = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        queued.Ticket.ChangeQueueTimer(remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining > maxDue ? maxDue : remaining);
    }

    public bool TryCancel(string ticketId) => !OwnedPolicyEnabled && TryCancel(ticketId, null);

    internal bool CancelTicket(RouterTicket ticket) => TryCancel(ticket.Id, ticket.OwnedPolicy?.Key);

    internal bool TryCancel(string ticketId, PolicyGroupKey? group)
    {
        RouterTicket? ticket;
        lock (_ticketGate) ticket = _tickets.GetValueOrDefault(ticketId);
        if (ticket is null || ticket.OwnedPolicy?.Key != group || ticket.IsTerminal) return false;
        switch (ClaimQueuedEnd(ticket, "cancelled"))
        {
            case QueueEndClaim.Deferred:
                return true;
            case QueueEndClaim.Competing:
                return false;
            case QueueEndClaim.Removed:
                SettleQueueEnd(ticket, "cancelled", null);
                return true;
        }

        var cts = ticket.ExecCts;
        if (cts is null) return false;
        var delivery = ticket.HttpDelivery;
        if (delivery is not null && delivery.Decide(HttpStreamOutcome.Cancelled).Code != "cancelled")
            return false;
        try { cts.Cancel(); return true; }
        catch (ObjectDisposedException) { return delivery is not null; }
    }

    private enum QueueEndClaim { Absent, Deferred, Competing, Removed }

    internal void FailSlowStream(RouterTicket ticket, CancellationToken caller)
    {
        if (ticket.IsTerminal || ticket.HttpDelivery is not { } delivery) return;
        var execution = ticket.ExecCts;
        BeforeSlowStreamDecisionForTests?.Invoke();
        if (delivery.Decide(HttpStreamOutcome.SlowConsumer, execution, caller).Code != "slow_consumer") return;

        switch (ClaimQueuedEnd(ticket, "slow_consumer"))
        {
            case QueueEndClaim.Deferred:
            case QueueEndClaim.Competing:
                return;
            case QueueEndClaim.Removed:
                SettleQueueEnd(ticket, "slow_consumer", null);
                return;
        }

        if (execution is null) return;
        // The dispatch owner settles an admitted ticket using the already selected outcome.
        try { _ = execution.CancelAsync(); }
        catch (ObjectDisposedException) { }
    }

    private QueueEndClaim ClaimQueuedEnd(RouterTicket ticket, string code)
    {
        lock (_gate)
        {
            if (ticket.DeferredQueueEnd is { } winner)
                return winner == code ? QueueEndClaim.Deferred : QueueEndClaim.Competing;
            if (ticket.PendingEnqueuePublication)
            {
                ticket.DeferredQueueEnd = code;
                return QueueEndClaim.Deferred;
            }
            if (!_queues.TryGetValue(ticket.Model, out var queue)) return QueueEndClaim.Absent;
            lock (queue.Gate)
            {
                if (!queue.TryRemove(ticket)) return QueueEndClaim.Absent;
                ClearQueuedScheduling(ticket);
                RetireQueueIfEmpty(queue);
                return QueueEndClaim.Removed;
            }
        }
    }

    private void MaybeFailDeadline(QueuedRequest head)
    {
        if (ReadQueueEnd(head) is { } end && ClaimQueuedEnd(head.Ticket, end) == QueueEndClaim.Removed)
            SettleQueueEnd(head.Ticket, end, head.Request.Options?.MaxQueueTime);
    }

    private void SettleQueueEnd(RouterTicket ticket, string code, TimeSpan? window, string? failureReason = null)
    {
        if (_admission?.Invalidate(ticket) == true) _ = CancelQueuedActivationAsync(ticket);
        var reason = failureReason ?? code switch
        {
            "cancelled" => "cancelled while queued",
            "slow_consumer" => HttpStreamOutcome.SlowConsumer.Message,
            "switch_failed" => "The enrolled host could not activate the selected recipe.",
            "policy_group_unavailable" => "the policy could not authorize the queued request",
            "policy_route_denied" => "the owned private route changed during preparation",
            "policy_journal_quarantined" => "the owned attempt journal is quarantined pending external reconciliation",
            _ => $"queue wait exceeded {window}",
        };
        Fail(ticket, code, reason);
        RetireOwnedPreparation(ticket, queued: true);
        ReleaseExecutionCancellation(ticket);
    }

    private static async Task CancelQueuedActivationAsync(RouterTicket ticket)
    {
        // The activation can already be waiting on a linked token. Keep its source alive
        // until cancellation has reached those registrations, independently of ticket settlement.
        using var execution = Interlocked.Exchange(ref ticket.ExecCts, null);
        if (execution is null) return;
        try { await execution.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { /* Callback faults cannot undo cancellation. */ }
    }

    private long ReadQueueTimestamp()
    {
        var fixture = Volatile.Read(ref QueueTimestampForTests);
        return fixture != 0 ? fixture : ReferenceEquals(_clock, TimeProvider.System) ? Stopwatch.GetTimestamp() : _clock.GetTimestamp();
    }

    private TimeSpan ReadQueueElapsed(QueuedRequest request)
    {
        var now = ReadQueueTimestamp();
        return ReferenceEquals(_clock, TimeProvider.System)
            ? Stopwatch.GetElapsedTime(request.EnqueuedAtTimestamp, now)
            : _clock.GetElapsedTime(request.EnqueuedAtTimestamp, now);
    }

    private string? ReadQueueEnd(QueuedRequest request)
    {
        if (request.CallerToken.IsCancellationRequested) return "cancelled";
        return request.Request.Options?.MaxQueueTime is { } window && ReadQueueElapsed(request) > window
            ? "deadline_exceeded" : null;
    }
}
