namespace Arboreal.InferenceRouter;

/// <summary>Router observability event. Monotonic Seq; replayable from the ring buffer.
/// Wire names are PascalCase, like the rest of the admin surface (SPEC §6): the casing is a live
/// contract, so `EventSurfaceTests` pins it rather than leaving it implicit. Unifying the admin
/// surface on lowerCamel is an open owner decision (SPEC §10 and the tool manifest already describe
/// the fields in lowerCamel), not something to change as a side effect of other work.</summary>
public sealed record RouterEvent
{
    public long Seq { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public required string Kind { get; init; }
    public string? ThreadId { get; init; }
    public string? TicketId { get; init; }
    public string? Engine { get; init; }
    public string? Model { get; init; }
    public string? Reason { get; init; }
    /// <summary>Structured detail (numbers: affinity/busy scores, latency ms, token counts…).</summary>
    public double? Value { get; init; }
}

public sealed record RouterEventFilter
{
    public long SinceSeq { get; init; }
    public string[]? Kinds { get; init; }
    public string? ThreadId { get; init; }
    public string? Engine { get; init; }
    /// <summary>Set to see exactly one request's event history — the contract an agent harness uses
    /// to follow its own ticket without reading other sessions' traffic.</summary>
    public string? TicketId { get; init; }

    public bool Matches(RouterEvent e) =>
        e.Seq > SinceSeq
        && (Kinds is null || Kinds.Contains(e.Kind, StringComparer.Ordinal))
        && (ThreadId is null || e.ThreadId == ThreadId)
        && (Engine is null || e.Engine == Engine)
        && (TicketId is null || e.TicketId == TicketId);
}

/// <summary>Bounded, thread-safe, replayable event ring (the Blazor/CLI live feed contract).</summary>
public sealed class EventRing
{
    private readonly object _lock = new();
    private readonly RouterEvent[] _ring;
    private long _head;      // total events appended
    private readonly List<Action<RouterEvent>> _subscribers = [];
    private readonly TimeSpan _retention = TimeSpan.FromMinutes(30);

    public EventRing(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 16);
        _ring = new RouterEvent[capacity];
    }

    public long Append(RouterEvent @event)
    {
        lock (_lock)
        {
            @event = @event with { Seq = _head + 1, Timestamp = DateTimeOffset.UtcNow };
            _ring[(int)(_head % _ring.Length)] = @event;
            _head++;
            // drop stale subscribers silently; SSE layer reconnects by seq
            foreach (var s in _subscribers.ToArray()) s(@event);
            return @event.Seq;
        }
    }

    public long CurrentSeq => Volatile.Read(ref _head);

    /// <summary>Oldest Seq still replayable (1 once anything has been appended).</summary>
    public long OldestSeq
    {
        get { lock (_lock) return Math.Max(1, _head + 1 - _ring.Length); }
    }

    /// <summary>Replay everything after <paramref name="sinceSeq"/> that the ring still holds.
    /// A cursor ahead of the ring (a client that reconnects with a seq from a previous router
    /// lifetime) yields nothing rather than throwing; a cursor older than the ring yields the whole
    /// retained window. <c>max</c> defaults to the ring capacity so a within-window reconnect is
    /// never silently truncated — the retention boundary is the only place events disappear.</summary>
    public IReadOnlyList<RouterEvent> Replay(long sinceSeq, int? max = null)
    {
        lock (_lock) return ReplayLocked(sinceSeq, max ?? _ring.Length);
    }

    private IReadOnlyList<RouterEvent> ReplayLocked(long sinceSeq, int max)
    {
        // SinceSeq is exclusive, the same way RouterEventFilter reads it: the caller has already seen
        // that Seq and is asking for what came after it.
        var first = Math.Max(1, _head + 1 - _ring.Length);
        var start = Math.Max(sinceSeq + 1, first);
        var n = (int)Math.Max(0, Math.Min(_head - start + 1, max));
        var list = new List<RouterEvent>(n);
        for (var i = 0; i < n; i++) list.Add(_ring[(int)((start + i - 1) % _ring.Length)]);
        return list;
    }

    /// <summary>Snapshot and subscribe under one lock. Doing the two separately leaves a window in
    /// which an appended event is in neither the snapshot nor the live tail — the handover hole.
    /// The handler therefore sees exactly the events appended after the snapshot.</summary>
    public IReadOnlyList<RouterEvent> SubscribeReplay(long sinceSeq, Action<RouterEvent> handler, int? max = null)
    {
        lock (_lock)
        {
            if (_subscribers.Count >= MaxSubscribers)
                throw new InvalidOperationException($"Event ring already has {MaxSubscribers} live subscribers.");
            var replayed = ReplayLocked(sinceSeq, max ?? _ring.Length);
            _subscribers.Add(handler);
            return replayed;
        }
    }

    public const int MaxSubscribers = 256;

    public void Subscribe(Action<RouterEvent> handler)
    {
        lock (_lock) _subscribers.Add(handler);
    }

    public void Unsubscribe(Action<RouterEvent> handler)
    {
        lock (_lock) _subscribers.Remove(handler);
    }
}
