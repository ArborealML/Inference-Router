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

    public RouterThread RegisterThread(string threadId, ThreadPriority priority, RouterThreadOptions? options = null)
    {
        RouterThread thread;
        bool created;
        lock (_gate)
        {
            created = !_threads.TryGetValue(threadId, out var current);
            thread = current ?? AddThread(threadId, priority, options?.Weight ?? 1.0, options?.ModelAffinity);
            if (!created) thread.SetPriorityValue(priority);
        }
        if (created) PublishThreadRegistration(thread, priority);
        else thread.NotifyPriorityChanged(priority);
        return thread;
    }

    public void SetThreadPriority(string threadId, ThreadPriority priority)
    {
        RouterThread thread;
        bool created;
        lock (_gate)
        {
            created = !_threads.TryGetValue(threadId, out var current);
            thread = current ?? AddThread(threadId, priority, 1.0, null);
            if (!created)
            {
                thread.SetPriorityValue(priority);
                foreach (var queue in _queues.Values) queue.Resort();
                if (_threadPublications.TryGetValue(threadId, out var pending))
                {
                    (pending.PriorityChanges ??= []).Add(priority);
                    return;
                }
                _threadPublications.Add(threadId, new ThreadPublication());
            }
        }
        if (created)
        {
            PublishThreadRegistration(thread, priority);
            return;
        }
        try { PublishThreadPriorityChange(thread, priority); }
        finally { DrainThreadPriorityChanges(thread); }
    }

    private void PublishThreadPriorityChange(RouterThread thread, ThreadPriority priority)
    {
        BeforeThreadPriorityFeedForTests?.Invoke(priority);
        thread.NotifyPriorityChanged(priority);
        _events.Append(new RouterEvent { Kind = "thread.priority_changed", ThreadId = thread.ThreadId, Value = priority.Class });
        SignalDispatch();
    }

    private RouterThread AddThread(string id, ThreadPriority priority, double weight, string? affinity)
    {
        var thread = new RouterThread(id, priority, weight, affinity);
        _threads.Add(id, thread);
        _threadPublications.Add(id, new ThreadPublication { Enqueues = [] });
        return thread;
    }

    private void PublishThreadRegistration(RouterThread thread, ThreadPriority priority)
    {
        BeforeThreadRegistrationFeedForTests?.Invoke(thread.ThreadId);
        _events.Append(new RouterEvent { Kind = "thread.registered", ThreadId = thread.ThreadId, Value = priority.Class });
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

    public ValueTask<RouterTicket> EnqueueAsync(string threadId, InferenceRequest request, CancellationToken ct = default)
    {
        var opts = request.Options;
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
                observedThread = _threads.GetValueOrDefault(threadId);
                affinity = observedThread?.ModelAffinity;
            }

            var requested = request.Model ?? affinity ?? generation.Settings.DefaultModel;
            var model = ResolveCanonical(requested, generation);
            var hopeless = CapabilityScan.DefinitelyHopeless(generation.Capabilities, model);
            BeforeEnqueueCommitForTests?.Invoke();
            QueuedRequest queued;
            bool publishRegistration;
            bool publishItem;
            ThreadPriority priority;
            lock (_gate)
            {
                if (_closing) throw new RouterRequestException("cancelled", "router shutting down");
                var thread = _threads.GetValueOrDefault(threadId);
                if (!ReferenceEquals(generation, CurrentGeneration) ||
                    !ReferenceEquals(thread, observedThread) ||
                    !string.Equals(affinity, thread?.ModelAffinity, StringComparison.Ordinal)) continue;

                publishRegistration = thread is null;
                thread ??= AddThread(threadId, opts?.Priority ?? generation.Settings.DefaultPriority, 1.0, null);
                priority = opts?.Priority ?? thread.Priority;
                var ticket = new RouterTicket(NewTicketId(), threadId, model, createdAt);
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
                    RequiredContextTokens = opts?.RequiredContext is { } context && context > 0
                        ? context : EstimateContextTokens(promptChars, generation.Settings.Scheduler.CharsPerTokenEstimate),
                };

                if (hopeless)
                {
                    IndexRejected(queued, "model_unavailable",
                        $"no engine in this configuration declares or currently reports model '{WireText.Ident(model)}'");
                }
                else
                {
                    var queue = GetOrCreateQueue(model, generation.Settings.Scheduler.MaxQueuePerModel);
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
                            IndexRejected(queued, "queue_overflow", $"queue for {WireText.Ident(model)} is full");
                        }
                    }
                }

                var publication = _threadPublications.GetValueOrDefault(threadId)?.Enqueues;
                publishItem = publication is null;
                publication?.Add(queued);
            }

            if (publishRegistration) PublishThreadRegistration(queued.Thread, priority);
            else if (publishItem) PublishEnqueue(queued);
            return ValueTask.FromResult(queued.Ticket);
        }
    }

    private ModelQueue GetOrCreateQueue(string model, int capacity)
    {
        if (_queues.TryGetValue(model, out var queue)) return queue;
        queue = new ModelQueue(model, capacity, static (_, _) => { });
        _queues.Add(model, queue);
        return queue;
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
                Kind = "queue.rejected", ThreadId = ticket.ThreadId, TicketId = ticket.Id,
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
            Kind = "request.enqueued", ThreadId = ticket.ThreadId, TicketId = ticket.Id,
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
                    if (end is not null) queue.TryRemove(ticket);
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
            ticket.InstallCallerLink(queued.CallerToken.UnsafeRegister(_ => TryCancel(ticket.Id), null));

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

    public bool TryCancel(string ticketId)
    {
        RouterTicket? ticket;
        lock (_ticketGate) ticket = _tickets.GetValueOrDefault(ticketId);
        if (ticket is null || ticket.IsTerminal) return false;
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
        try { cts.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    private enum QueueEndClaim { Absent, Deferred, Competing, Removed }

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
            return _queues.TryGetValue(ticket.Model, out var queue) && queue.TryRemove(ticket)
                ? QueueEndClaim.Removed : QueueEndClaim.Absent;
        }
    }

    private void MaybeFailDeadline(QueuedRequest head)
    {
        if (ReadQueueEnd(head) is { } end && ClaimQueuedEnd(head.Ticket, end) == QueueEndClaim.Removed)
            SettleQueueEnd(head.Ticket, end, head.Request.Options?.MaxQueueTime);
    }

    private void SettleQueueEnd(RouterTicket ticket, string code, TimeSpan? window)
    {
        _admission?.Invalidate(ticket);
        Fail(ticket, code, code == "cancelled" ? "cancelled while queued" : $"queue wait exceeded {window}");
        ReleaseExecutionCancellation(ticket);
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
