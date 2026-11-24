namespace Arboreal.InferenceRouter;

/// <summary>One queued request with scheduler metadata.</summary>
public sealed class QueuedRequest
{
    internal bool HasProfileSelection => Request.Options?.Profile is not null || Request.Options?.PreferredProfile is not null;
    internal bool ReservedSwitch { get; set; }
    internal LocalLifecycleGroup.Lease? LocalLease { get; set; }
    public required RouterTicket Ticket { get; init; }
    public required InferenceRequest Request { get; init; }
    internal string? OriginalModel { get; init; }
    public required PrioritizedRequest PriorityNode { get; init; }
    public required RouterThread Thread { get; init; }
    public required long EnqueuedSeq { get; init; }
    internal long EnqueuedAtTimestamp { get; init; }
    internal CancellationToken CallerToken { get; init; }
    internal bool QueueExpired(TimeProvider clock) => Request.Options?.MaxQueueTime is { } window &&
        clock.GetElapsedTime(EnqueuedAtTimestamp) > window;
    public required long PromptChars { get; init; }
    /// <summary>Context demand for tier selection, from the client minimum or the prompt estimate (§7.6).</summary>
    public long RequiredContextTokens { get; init; }
    internal long ExplicitContextMinimum => Math.Max(0L, Request.Options?.RequiredContext ?? 0);
}

internal readonly record struct QueueDispatchCandidate(int Depth, long BaselineOrdinal, QueuedRequest? Request);

/// <summary>Per-model FIFO priority queue. Ordering: thread class, then sub, then seq.
/// Reorders in place when a thread's priority changes while queued.</summary>
public sealed class ModelQueue
{
    private readonly object _lock = new();
    private readonly List<QueuedRequest> _heap = [];
    private readonly int _capacity;
    private readonly Action<QueuedRequest, string> _onReject;

    public ModelQueue(string model, int capacity, Action<QueuedRequest, string> onReject)
    {
        Model = model;
        _capacity = capacity;
        _onReject = onReject;
    }

    // Router dispatch holds this only after the router lock (and admission, when injected).
    // The outer hold keeps exact removal and physical accounting in one critical section.
    internal object Gate => _lock;

    internal long CreationOrdinal { get; init; }
    public string Model { get; }
    public int Depth { get { lock (_lock) return _heap.Count; } }

    public bool TryEnqueue(QueuedRequest item)
    {
        if (TryInsert(item)) return true;
        _onReject(item, "queue_overflow");
        return false;
    }

    internal bool TryInsert(QueuedRequest item)
    {
        lock (_lock)
        {
            if (_heap.Count >= _capacity) return false;
            _heap.Add(item);
            _heap.Sort(HeadFirst);
            return true;
        }
    }

    /// <summary>Pop the head if it passes the admission predicate (thread inflight cap).</summary>
    public QueuedRequest? TryPopHead(Func<RouterThread, bool> admission)
    {
        lock (_lock)
        {
            var index = FirstAdmissibleIndex(admission);
            if (index < 0) return null;
            var item = _heap[index];
            _heap.RemoveAt(index);
            return item;
        }
    }

    internal QueueDispatchCandidate SnapshotCandidate(long baselineOrdinal, Func<RouterThread, bool> admission)
    {
        lock (_lock)
        {
            var index = FirstAdmissibleIndex(admission);
            return new QueueDispatchCandidate(_heap.Count, baselineOrdinal, index < 0 ? null : _heap[index]);
        }
    }

    /// <summary>Take only the request the scheduler evaluated, while it is still first admissible.</summary>
    internal bool TryTakeExact(QueueDispatchCandidate candidate, Func<RouterThread, bool> admission)
    {
        lock (_lock)
        {
            var index = FindExactTakeIndexUnderGate(candidate, admission);
            if (index < 0) return false;
            RemoveExactTakeUnderGate(index);
            return true;
        }
    }

    // The router holds Gate across validation, the deadline check and removal.
    internal int FindExactTakeIndexUnderGate(QueueDispatchCandidate candidate, Func<RouterThread, bool> admission)
    {
        var index = FirstAdmissibleIndex(admission);
        return index >= 0 && ReferenceEquals(_heap[index], candidate.Request) ? index : -1;
    }

    internal void RemoveExactTakeUnderGate(int index) => _heap.RemoveAt(index);

    private int FirstAdmissibleIndex(Func<RouterThread, bool> admission)
    {
        for (var i = 0; i < _heap.Count; i++)
            if (!_heap[i].Ticket.PendingEnqueuePublication && admission(_heap[i].Thread)) return i;
        return -1;
    }

    public bool TryRemove(RouterTicket ticket)
    {
        lock (_lock)
        {
            var idx = _heap.FindIndex(q => ReferenceEquals(q.Ticket, ticket));
            if (idx < 0) return false;
            _heap.RemoveAt(idx);
            return true;
        }
    }

    public bool TryRemoveById(string ticketId)
    {
        lock (_lock)
        {
            var idx = _heap.FindIndex(q => q.Ticket.Id == ticketId);
            if (idx < 0) return false;
            _heap.RemoveAt(idx);
            return true;
        }
    }

    /// <summary>Re-sort in place after a thread priority change; stable on enqueue sequence.</summary>
    /// <summary>Takes everything still waiting (used at shutdown, where each queued ticket needs an
    /// explicit terminal transition because it has no execution to cancel).</summary>
    public List<QueuedRequest> DrainAll()
    {
        lock (_lock)
        {
            var all = new List<QueuedRequest>(_heap);
            _heap.Clear();
            return all;
        }
    }

    public void Resort()
    {
        lock (_lock) _heap.Sort(HeadFirst);
    }

    /// <summary>Head-of-queue ordering: higher priority first, FIFO (enqueue sequence) within
    /// a priority. Index 0 is always the next request to dispatch.</summary>
    private static int HeadFirst(QueuedRequest a, QueuedRequest b)
    {
        var c = b.PriorityNode.Priority.CompareTo(a.PriorityNode.Priority); // descending priority
        return c != 0 ? c : a.PriorityNode.Sequence.CompareTo(b.PriorityNode.Sequence); // ascending FIFO
    }

    public QueuedRequest? PeekHead()
    {
        lock (_lock)
        {
            return _heap.Count > 0 ? _heap[0] : null;
        }
    }

    /// <summary>Queue depth for a specific thread (used by stats + wait estimation).</summary>
    public int DepthOfThread(string threadId)
    {
        lock (_lock)
        {
            var n = 0;
            foreach (var q in _heap) if (q.Thread.ThreadId == threadId) n++;
            return n;
        }
    }
}
