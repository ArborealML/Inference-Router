namespace Arboreal.InferenceRouter;

/// <summary>Chat thread: a logical conversation/agent whose priority drives request scheduling.
/// Requests enqueue on threads; the scheduler consults the thread's priority (and weight,
/// which caps concurrent in-flight requests to enforce fairness).</summary>
public sealed class RouterThread
{
    private int _priorityCode;   // (class<<8)|sub — accessed via Volatile.Read/Write
    private double _weight;

    internal RouterThread(string threadId, ThreadPriority priority, double weight, string? modelAffinity)
    {
        ThreadId = threadId;
        _priorityCode = Encode(priority);
        _weight = Math.Clamp(weight, 0.25, 8.0);
        ModelAffinity = modelAffinity;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string ThreadId { get; }
    public DateTimeOffset CreatedAt { get; }

    private static int Encode(ThreadPriority p) => (p.Class << 8) | p.Sub;
    private static ThreadPriority Decode(int code) => new((byte)(code >> 8), (byte)(code & 0xFF));

    public ThreadPriority Priority => Decode(Volatile.Read(ref _priorityCode));
    public double Weight => _weight;
    public string? ModelAffinity { get; set; }

    /// <summary>How many requests this thread may hold in-flight concurrently (fairness cap).</summary>
    public int MaxInflight => (int)Math.Ceiling(_weight);

    public void SetPriority(ThreadPriority priority)
    {
        Volatile.Write(ref _priorityCode, Encode(priority));
        PriorityChanged?.Invoke(this, priority);
    }

    /// <summary>Raised when the priority changes; the queue reorders in response.</summary>
    public event EventHandler<ThreadPriority>? PriorityChanged;

    internal int Inflight => Volatile.Read(ref _inflight);
    private int _inflight;
    internal void IncrementInflight() => Interlocked.Increment(ref _inflight);
    internal void DecrementInflight() => Interlocked.Decrement(ref _inflight);
    internal long Enqueued, Completed;
    internal long Rejected;
}

public sealed record RouterThreadOptions
{
    public double Weight { get; init; } = 1.0;
    public string? ModelAffinity { get; init; }
}

/// <summary>Per-request scheduling knobs. Inherit from the thread when unset.</summary>
public sealed record RouterRequestOptions
{
    public ThreadPriority? Priority { get; init; }
    public string? Model { get; init; }
    /// <summary>Soft deadline: used for metrics + admission; not enforced after execution starts.</summary>
    public TimeSpan? Deadline { get; init; }
    /// <summary>If not dispatched within this window the request fails with deadline_exceeded.</summary>
    public TimeSpan? MaxQueueTime { get; init; }
    /// <summary>Client-supplied affinity hint (normalized prefix hash) for sticky routing.</summary>
    public string? CacheAffinityHash { get; init; }
}

public enum TicketState : byte { Queued, WaitingForModel, Executing, Completed, Cancelled, Failed }

/// <summary>Handle for one enqueued chat/completion request.</summary>
public sealed class RouterTicket
{
    internal RouterTicket(string id, string threadId, string model, DateTimeOffset queuedAt)
    {
        Id = id;
        ThreadId = threadId;
        Model = model;
        QueuedAt = queuedAt;
    }

    public string Id { get; }
    public string ThreadId { get; }
    public string Model { get; }
    public DateTimeOffset QueuedAt { get; }
    public TicketState State { get; internal set; }
    public string? Engine { get; internal set; }
    public string? Error { get; internal set; }
    public string? ErrorCode { get; internal set; }

    /// <summary>Resolves when a terminal state is reached; value is the raw engine response body
    /// (JSON), or the ticket fails with the router error.</summary>
    public Task<byte[]> CompletionTask => CompletionTcs.Task;
    internal readonly TaskCompletionSource<byte[]> CompletionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationTokenSource? ExecCts;
    /// <summary>Streaming responses: ordered upstream byte chunks (SSE frames as received).
    /// Null for non-streaming requests until the engine begins streaming.</summary>
    public System.Threading.Channels.Channel<ReadOnlyMemory<byte>>? StreamChannel { get; internal set; }
}

/// <summary>Raw request envelope: the OpenAI-compatible JSON body plus the router's parsed
/// scheduling probe. Bodies are forwarded verbatim to engines (full tools/multimodal passthrough).</summary>
public sealed class InferenceRequest
{
    /// <summary>Requested model (OpenAI alias or canonical id); null falls back to thread
    /// affinity, then the router default.</summary>
    public string? Model { get; init; }
    /// <summary>Whether the client wants SSE streaming deltas.</summary>
    public bool Stream { get; init; }
    /// <summary>UTF-8 JSON body (OpenAI chat/completions shape).</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }
    public required RouterRequestOptions Options { get; init; }
}
