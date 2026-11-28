namespace Arboreal.InferenceRouter;

/// <summary>Chat thread: a logical conversation/agent whose priority drives request scheduling.
/// Requests enqueue on threads; the scheduler consults the thread's priority (and weight,
/// which caps concurrent in-flight requests to enforce fairness).</summary>
public sealed class RouterThread
{
    private int _priorityCode;   // (class<<8)|sub — accessed via Volatile.Read/Write
    private double _weight;
    private readonly Func<RouterThread, ThreadPriority, bool>? _setOwnedPriority;

    internal RouterThread(string threadId, ThreadPriority priority, double weight, string? modelAffinity,
                          Func<RouterThread, ThreadPriority, bool>? setOwnedPriority = null)
    {
        ThreadId = threadId;
        _priorityCode = Encode(priority);
        _setOwnedPriority = setOwnedPriority;
        _weight = Math.Clamp(weight, 0.25, 8.0);
        ModelAffinity = modelAffinity;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string ThreadId { get; }
    internal string? DisplayId { get; init; }
    internal PolicyGroupKey? OwnerGroup { get; init; }
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
        if (_setOwnedPriority?.Invoke(this, priority) == true) return;
        SetPriorityValue(priority);
        NotifyPriorityChanged(priority);
    }

    internal void SetPriorityValue(ThreadPriority priority) => Volatile.Write(ref _priorityCode, Encode(priority));
    internal void NotifyPriorityChanged(ThreadPriority priority) => PriorityChanged?.Invoke(this, priority);

    /// <summary>Raised when the priority changes. An owning router updates its queues before notification.</summary>
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
    /// <summary>Context tokens this request needs. When absent the router estimates from prompt
    /// chars (scheduler.chars_per_token_estimate); a value above the active window escalates.</summary>
    public long? RequiredContext { get; init; }
    /// <summary>The exact configured serving profile requested by this caller.</summary>
    public string? Profile { get; init; }
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
    internal string? ScopedThreadKey { get; init; }
    internal OwnedTicketPolicy? OwnedPolicy { get; init; }
    internal OwnedPrivateAttempt? PrivateAttempt;
    internal int PrivateAttemptDisposition;
    public string Model { get; }
    public DateTimeOffset QueuedAt { get; }
    public TicketState State
    {
        get => _state;
        internal set
        {
            _state = value;
            if (value is TicketState.Completed or TicketState.Cancelled or TicketState.Failed)
            {
                // A streaming listener must not wait forever on a request that ended before it had
                // a channel to read.
                _streamReady.TrySetResult(null);
            }
        }
    }

    private TicketState _state;
    public string? Engine { get; internal set; }
    public string? Error { get; internal set; }
    public string? ErrorCode { get; internal set; }
    /// <summary>Frozen upstream evidence when a streaming ticket settles.</summary>
    public SemanticStreamObservation? StreamObservation { get; private set; }

    /// <summary>Resolves when a terminal state is reached; value is the raw engine response body
    /// (JSON), or the ticket fails with the router error.</summary>
    public Task<byte[]> CompletionTask => CompletionTcs.Task;
    internal readonly TaskCompletionSource<byte[]> CompletionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationTokenSource? ExecCts;
    internal bool IsStateless { get; init; }
    internal bool IsStreamingRequest { get; init; }
    internal bool IsHttpStream { get; init; }
    internal HttpStreamDelivery? HttpDelivery { get; init; }
    internal bool SuppressTerminalFrame { get; set; }
    internal bool DefaultStreamReadShutdown { get; set; }
    /// <summary>Wiring from the caller's cancellation token to this ticket's cancel path. Dropped
    /// when the ticket reaches a terminal state, because the registration is rooted in the caller's
    /// token — for a server request that is the connection's abort token, which outlives the request.</summary>
    private CancellationTokenRegistration _callerLink;
    private Timer? _queueTimer;
    private readonly object _hooks = new();
    private bool _queueWaitEnded;
    private SemanticStreamObserver? _streamObserver;
    private bool _streamRefused;
    internal bool PendingEnqueuePublication;
    internal string? DeferredQueueEnd;
    internal LocalLifecycleGroup? LocalDrain; // router gate
    internal DispatchWaitInfo? DispatchWait;
    private int _terminal;
    private int _admissionRevoked;
    internal bool CanPrepareAdmission => Volatile.Read(ref _terminal) == 0 &&
        Volatile.Read(ref _admissionRevoked) == 0;

    internal void RevokeAdmission() => Volatile.Write(ref _admissionRevoked, 1);

    /// <summary>Claims the ticket's one terminal transition (complete, fail, cancel or queue
    /// rejection). Every sink goes through this, so a ticket cannot be reported both completed and
    /// failed — which is a real race in streaming, where a late pump cancellation meets a write that
    /// already finished.</summary>
    internal bool TryEnterTerminal()
    {
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) != 0) return false;
        if (IsStreamingRequest)
        {
            lock (_hooks)
            {
                StreamObservation = _streamObserver?.Snapshot() ??
                    (_streamRefused ? SemanticStreamObservation.Failed : SemanticStreamObservation.Unknown);
                _streamObserver = null;
            }
        }
        return true;
    }

    internal void AttachStreamObserver(SemanticStreamObserver observer)
    {
        lock (_hooks)
            if (!IsTerminal) _streamObserver = observer;
    }

    internal void ObserveStreamRefusal()
    {
        if (!IsStreamingRequest) return;
        lock (_hooks)
            if (!IsTerminal) _streamRefused = true;
    }

    internal bool IsTerminal => Volatile.Read(ref _terminal) != 0;

    internal void StoreTerminalState(TicketState state) => _state = state;
    internal void SignalTerminalStream() => _streamReady.TrySetResult(null);

    internal void InstallCallerLink(CancellationTokenRegistration registration)
    {
        lock (_hooks)
        {
            if (!IsTerminal)
            {
                _callerLink = registration;
                return;
            }
        }
        registration.Unregister();
    }

    internal void InstallQueueTimer(Timer timer)
    {
        lock (_hooks)
        {
            if (!_queueWaitEnded && !IsTerminal)
            {
                _queueTimer = timer;
                return;
            }
        }
#pragma warning disable IDISP007 // Caller transfers the newly created timer even when the queue wait has already ended.
        timer.Dispose();
#pragma warning restore IDISP007
    }

    internal void EndQueueWait()
    {
        Timer? timer;
        lock (_hooks)
        {
            _queueWaitEnded = true;
            timer = _queueTimer;
            _queueTimer = null;
        }
        timer?.Dispose();
    }

    internal void ChangeQueueTimer(TimeSpan due)
    {
        Timer? timer;
        lock (_hooks) timer = _queueTimer;
        try { timer?.Change(due, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    internal void DetachWaitHooks()
    {
        CancellationTokenRegistration registration;
        lock (_hooks)
        {
            registration = _callerLink;
            _callerLink = default;
        }
        registration.Unregister();
        EndQueueWait();
    }
    /// <summary>Streaming responses: ordered upstream byte chunks (SSE frames as received).
    /// Null until admission creates the channel for a streaming request.</summary>
    public System.Threading.Channels.Channel<ReadOnlyMemory<byte>>? StreamChannel
    {
        get => _streamChannel;
        internal set
        {
            _streamChannel = value;
            if (value is not null)
            {
                _streamReady.TrySetResult(value);
            }
        }
    }

    private System.Threading.Channels.Channel<ReadOnlyMemory<byte>>? _streamChannel;

    /// <summary>Resolves with the stream channel as soon as a streaming request has one, or with
    /// null if the ticket reaches a terminal state without one. Lets an SSE relay start the moment
    /// the request is admitted instead of polling — a poll interval would otherwise be paid by every
    /// stream as time-to-first-token.</summary>
    public Task<System.Threading.Channels.Channel<ReadOnlyMemory<byte>>?> StreamReady => _streamReady.Task;

    private readonly TaskCompletionSource<System.Threading.Channels.Channel<ReadOnlyMemory<byte>>?> _streamReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record DispatchWaitInfo(string Reason, string? Scope);

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
