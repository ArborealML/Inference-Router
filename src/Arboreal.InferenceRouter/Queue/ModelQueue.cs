namespace Arboreal.InferenceRouter;

/// <summary>One queued request with scheduler metadata.</summary>
public sealed class QueuedRequest
{
    public required RouterTicket Ticket { get; init; }
    public required InferenceRequest Request { get; init; }
    public required PrioritizedRequest PriorityNode { get; init; }
    public required RouterThread Thread { get; init; }
    public required long EnqueuedSeq { get; init; }
    public required long PromptChars { get; init; }
    /// <summary>Context tokens the request must be served under (client hint or char estimate).
    /// Drives serving-profile escalation in the scheduler (§7.6).</summary>
    public long RequiredContextTokens { get; init; }
}

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

    public string Model { get; }
    public int Depth { get { lock (_lock) return _heap.Count; } }

    public bool TryEnqueue(QueuedRequest item)
    {
        lock (_lock)
        {
            if (_heap.Count >= _capacity)
            {
                _onReject(item, "queue_overflow");
                return false;
            }
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
            for (var i = 0; i < _heap.Count; i++)
            {
                if (!admission(_heap[i].Thread)) continue;
                var item = _heap[i];
                _heap.RemoveAt(i);
                return item;
            }
            return null;
        }
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
