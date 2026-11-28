using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter;

/// <summary>Router observability event. Monotonic Seq; replayable from the ring buffer.
/// Wire names are PascalCase, like the rest of the admin surface (SPEC §6): the casing is a live
/// contract, so `EventSurfaceTests` pins it rather than leaving it implicit. Unifying the admin
/// surface on lowerCamel is an open owner decision (SPEC §10 and the tool manifest already describe
/// the fields in lowerCamel), not something to change as a side effect of other work.</summary>
public sealed record RouterEvent
{
    internal PolicyGroupKey? OwnerGroup { get; init; }
    public long Seq { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public required string Kind { get; init; }
    public string? ThreadId { get; init; }
    public string? TicketId { get; init; }
    public string? Engine { get; init; }
    public string? Model { get; init; }
    public string? Reason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpstreamOutcome { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpstreamEndReason { get; init; }
    /// <summary>Structured detail (numbers: affinity/busy scores, latency ms, token counts…).</summary>
    public double? Value { get; init; }
}

public sealed record RouterEventFilter
{
    internal PolicyGroupKey? OwnerGroup { get; init; }
    public long SinceSeq { get; init; }
    public string[]? Kinds { get; init; }
    public string? ThreadId { get; init; }
    public string? Engine { get; init; }
    /// <summary>Set to see exactly one request's event history — the contract an agent harness uses
    /// to follow its own ticket without reading other sessions' traffic.</summary>
    public string? TicketId { get; init; }

    // Stored values are capped at reporting length by the ring's funnel, so the filter compares at the same
    // length: a caller whose thread id is longer than the reporting budget still finds their own records. The
    // trade is stated rather than hidden — two filter values that share a head match the same records, which is
    // an operator looking at the wrong thread, never a lookup that misses the right one (§6.1).
    public bool Matches(RouterEvent e) =>
        e.Seq > SinceSeq
        && (OwnerGroup is null || e.OwnerGroup == OwnerGroup)
        && (Kinds is null || Kinds.Contains(e.Kind, StringComparer.Ordinal))
        && (ThreadId is null || e.ThreadId == WireText.Ident(ThreadId))
        && (Engine is null || e.Engine == WireText.Ident(Engine))
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
    private readonly int _maxSubscribers;

    // Faults counted, never rethrown: one observer's exception is not allowed to reach the code that produced the
    // event (decision-34: a claimed switch is completed, not rolled back, and a readiness task stranded by a
    // stranger's bug is a stranded request). Read by /admin/status as `FeedSubscriberFaults` so the failure is
    // visible to an operator instead of silent (§6.1).
    private long _subscriberFaults;

    public EventRing(int capacity = 4096, int maxLiveSubscribers = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLiveSubscribers, 1);
        _ring = new RouterEvent[capacity];
        _maxSubscribers = maxLiveSubscribers;
    }

    public long Append(RouterEvent @event)
    {
        lock (_lock)
        {
            // Identifiers and reason text are stored at reporting length. A model id, an engine name, a thread id
            // and a reason arrive here from several producers, and more than one of them starts in a request body,
            // so the caps belong at the single funnel rather than in each writer: the ring stops being a way for a
            // caller to choose how much of their own text the feed carries, and the ceiling SPEC §10 states for one
            // feed reason holds for the request path's failure producer as well as for the engine's emitter. An
            // identifier keeps its head plus a marker that names the true length and fingerprints the whole value,
            // so two ids that cut to the same head remain distinguishable in the record (§6.1, §10).
            @event = @event with
            {
                Seq = _head + 1,
                Timestamp = DateTimeOffset.UtcNow,
                Model = @event.Model is { } m ? WireText.Ident(m) : null,
                Engine = @event.Engine is { } e ? WireText.Ident(e) : null,
                ThreadId = @event.ThreadId is { } t ? WireText.Ident(t) : null,
                Reason = @event.Reason is { } r ? WireText.Bound(r, WireText.ReasonChars) : null,
                UpstreamOutcome = @event.UpstreamOutcome is "complete" or "incomplete" or "failed" or "unknown"
                    ? @event.UpstreamOutcome : null,
                UpstreamEndReason = @event.UpstreamEndReason is "complete" or "upstream_closed_without_terminator"
                    or "stream_incomplete" or "failed" or "unknown" ? @event.UpstreamEndReason : null,
            };
            _ring[(int)(_head % _ring.Length)] = @event;
            _head++;
            // drop stale subscribers silently; SSE layer reconnects by seq
            // **Isolated, not dropped.** Every subscriber is invoked inside the append, on the emitter's thread, so
            // an exception here lands in the producer: the engine that was publishing a capability, the switch that
            // was ending an attempt, the request path that was recording a terminal outcome. One observer's failure
            // must cost that observer its own event and nothing else — the append has already happened, the rest of
            // the subscribers still get it, and the emitter finishes the work it came to do. The fault is counted,
            // not logged here: a feed callback runs under the ring lock, so this path may not do I/O (§6.1).
            foreach (var s in _subscribers.ToArray())
            {
                try
                {
                    s(@event);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _subscriberFaults);
                }
            }
            return @event.Seq;
        }
    }

    public long CurrentSeq => Volatile.Read(ref _head);

    internal void AppendIf(RouterEvent @event, Func<bool> isCurrent)
    {
        lock (_lock)
            if (isCurrent()) Append(@event);
    }

    /// <summary>How many subscriber invocations have thrown since startup. This is the only sign a feed observer
    /// is broken: the exception is swallowed by design, so without a counter a permanently failing observer — a
    /// socket handler that throws on every write, say — would be invisible and would silently stop delivering
    /// (§6.1).</summary>
    public long SubscriberFaults => Volatile.Read(ref _subscriberFaults);

    /// <summary>Oldest Seq still replayable (1 once anything has been appended).</summary>
    public long OldestSeq
    {
        get { lock (_lock) return Math.Max(1, _head + 1 - _ring.Length); }
    }

    /// <summary>Replay everything after <paramref name="sinceSeq"/> that the ring still holds.
    /// A cursor at or ahead of the ring (a client that reconnects with a seq from a previous router
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
        // that Seq and asking for what came after it. A cursor at or ahead of the head yields nothing,
        // and it must be checked before the addition: the build is checked-arithmetic, and a client can
        // ask for long.MaxValue.
        if (sinceSeq >= _head) return [];
        var first = Math.Max(1, _head + 1 - _ring.Length);
        var start = Math.Max(sinceSeq + 1, first);
        var n = (int)Math.Max(0, Math.Min(_head - start + 1, max));
        var list = new List<RouterEvent>(n);
        for (var i = 0; i < n; i++) list.Add(_ring[(int)((start + i - 1) % _ring.Length)]);
        return list;
    }

    /// <summary>Snapshot and subscribe under one lock, refusing the subscription if the feed is full.
    /// Doing the two separately leaves a window in which an appended event is in neither the snapshot
    /// nor the live tail — the handover hole. The handler therefore sees exactly the events appended
    /// after the snapshot. Refusal is a return value rather than an exception because the HTTP layer
    /// has already decided how to answer a full feed (503 before any SSE headers), and an exception
    /// thrown after the headers are out looks to the client like a truncated success.</summary>
    public bool TrySubscribeReplay(long sinceSeq, Action<RouterEvent> handler, out IReadOnlyList<RouterEvent> replayed, int? max = null)
    {
        lock (_lock)
        {
            if (_subscribers.Count >= _maxSubscribers)
            {
                replayed = Array.Empty<RouterEvent>();
                return false;
            }
            replayed = ReplayLocked(sinceSeq, max ?? _ring.Length);
            _subscribers.Add(handler);
            return true;
        }
    }

    /// <summary>How many live subscriptions the ring is willing to hold.</summary>
    public int MaxLiveSubscribers => _maxSubscribers;

    /// <summary>Live subscriptions currently attached. The admin feed checks this before it commits to
    /// a 200 + SSE headers.</summary>
    public int LiveSubscribers { get { lock (_lock) return _subscribers.Count; } }

    public void Subscribe(Action<RouterEvent> handler)
    {
        lock (_lock) _subscribers.Add(handler);
    }

    public void Unsubscribe(Action<RouterEvent> handler)
    {
        lock (_lock) _subscribers.Remove(handler);
    }
}
