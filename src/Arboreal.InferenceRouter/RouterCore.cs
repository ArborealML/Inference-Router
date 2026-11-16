using System.Text.Json;
using System.Threading.Channels;

namespace Arboreal.InferenceRouter;

/// <summary>Router orchestration: threads, per-model priority queues, dispatch loop,
/// engine execution, events, stats. Implements <see cref="IInferenceRouter"/>.</summary>
public sealed class RouterCore : IInferenceRouter
{
    private readonly RouterConfig _config;
    private readonly IRequestScheduler _scheduler;
    private readonly EventRing _events;
    private readonly Dictionary<string, EngineRuntime> _engines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RouterThread> _threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RouterTicket> _tickets = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _seq;
    private readonly CancellationTokenSource _shutdown = new();

    public RouterCore(RouterConfig config, IRequestScheduler? scheduler = null, EventRing? events = null)
    {
        _config = config;
        _scheduler = scheduler ?? new PrefixAwareScheduler();
        _events = events ?? new EventRing();
        foreach (var (name, def) in config.Engines)
            _engines[name] = new EngineRuntime(def, config, _events, config.HttpHandlerFactory?.Invoke(def));
        foreach (var (id, td) in config.Threads)
            _threads[id] = new RouterThread(id, td.Priority, td.Weight, td.ModelAffinity);
    }

    public EventRing Events => _events;
    public RouterConfig Config => _config;
    public IReadOnlyDictionary<string, EngineRuntime> Engines => _engines;

    public void Start()
    {
        foreach (var e in _engines.Values)
        {
            e.StartHealthLoop(_shutdown.Token);
            _events.Append(new RouterEvent { Kind = "engine.registered", Engine = e.Name });
        }
        _ = Task.Run(DispatchLoop, _shutdown.Token);
    }

    // ── IInferenceRouter ────────────────────────────────────────────────────

    public RouterThread RegisterThread(string threadId, ThreadPriority priority, RouterThreadOptions? options = null)
    {
        lock (_gate)
        {
            if (_threads.TryGetValue(threadId, out var existing))
            {
                existing.SetPriority(priority);
                return existing;
            }
            var t = new RouterThread(threadId, priority, options?.Weight ?? 1.0, options?.ModelAffinity);
            _threads[threadId] = t;
            _events.Append(new RouterEvent { Kind = "thread.registered", ThreadId = threadId, Value = priority.Class });
            return t;
        }
    }

    public void SetThreadPriority(string threadId, ThreadPriority priority)
    {
        lock (_gate)
        {
            if (!_threads.TryGetValue(threadId, out var t))
            {
                t = new RouterThread(threadId, priority, 1.0, null);
                _threads[threadId] = t;
                _events.Append(new RouterEvent { Kind = "thread.registered", ThreadId = threadId, Value = priority.Class });
                return;
            }
            t.SetPriority(priority);
            _events.Append(new RouterEvent { Kind = "thread.priority_changed", ThreadId = threadId, Value = priority.Class });
            foreach (var q in _queues.Values) q.Resort();
        }
    }

    public ValueTask<RouterTicket> EnqueueAsync(string threadId, InferenceRequest request, CancellationToken ct = default)
    {
        var opts = request.Options ?? new RouterRequestOptions();
        RouterThread thread;
        lock (_gate)
        {
            // A newly-encountered thread adopts the first request's explicit priority, if any.
            if (!_threads.TryGetValue(threadId, out var t))
                t = RegisterThread(threadId, opts.Priority ?? _config.DefaultPriority);
            thread = t;
        }

        var requestedModel = request.Model ?? thread.ModelAffinity ?? _config.DefaultModel;
        var canonical = ResolveCanonical(requestedModel);
        var ticket = new RouterTicket(NewTicketId(), threadId, canonical, DateTimeOffset.UtcNow);
        var prio = opts.Priority ?? thread.Priority;
        var node = new PrioritizedRequest(ticket, prio, Interlocked.Increment(ref _seq));
        var queued = new QueuedRequest
        {
            Ticket = ticket,
            Request = request,
            PriorityNode = node,
            Thread = thread,
            EnqueuedSeq = node.Sequence,
            PromptChars = PromptCharsProbe(request.Body),
        };
        ticket.State = TicketState.Queued;
        lock (_gate) _tickets[ticket.Id] = ticket;
        GetOrCreateQueue(canonical).TryEnqueue(queued);
        _events.Append(new RouterEvent
        {
            Kind = "request.enqueued",
            ThreadId = threadId,
            TicketId = ticket.Id,
            Model = canonical,
            Value = queued.PromptChars,
        });
        SignalDispatch();
        return ValueTask.FromResult(ticket);
    }

    public bool TryCancel(string ticketId)
    {
        RouterTicket? ticket;
        lock (_gate)
        {
            ticket = _tickets.GetValueOrDefault(ticketId);
            if (ticket is null) return false;
        }
        foreach (var q in _queues.Values)
        {
            if (q.TryRemoveById(ticketId))
            {
                Fail(ticket, "cancelled", "cancelled while queued");
                return true;
            }
        }
        var cts = ticket.ExecCts;
        if (cts is not null)
        {
            try { cts.Cancel(); return true; }
            catch (ObjectDisposedException) { }
        }
        return false;
    }

    public RouterStats GetStats()
    {
        lock (_gate)
        {
            var engines = _engines.Values.Select(e => new EngineStats
            {
                Engine = e.Name,
                Healthy = e.Healthy,
                LoadedModels = e.LoadedModels,
                Inflight = e.Inflight,
                SwitchingToward = e.SwitchingToward,
            }).ToArray();
            var queues = _queues.Values.ToDictionary(q => q.Model, q => q.Depth);
            var threads = _threads.Values.Select(t => new ThreadStats
            {
                ThreadId = t.ThreadId,
                Priority = t.Priority,
                Weight = t.Weight,
                Enqueued = Volatile.Read(ref t.Enqueued),
                Completed = Volatile.Read(ref t.Completed),
            }).ToArray();
            return new RouterStats(Engines: engines, QueueDepths: queues, Threads: threads);
        }
    }

    public TicketSnapshot? TryGetTicket(string ticketId)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(ticketId, out var t)) return null;
            return new TicketSnapshot
            {
                Id = t.Id,
                ThreadId = t.ThreadId,
                Model = t.Model,
                QueuedAt = t.QueuedAt,
                State = t.State.ToString(),
                Engine = t.Engine,
                ErrorCode = t.ErrorCode,
                Error = t.Error,
            };
        }
    }

    public IAsyncEnumerable<RouterEvent> SubscribeEvents(RouterEventFilter? filter = null, CancellationToken ct = default)
        => new EventEnumerable(_events, filter ?? new RouterEventFilter(), ct);

    private sealed class EventEnumerable(EventRing ring, RouterEventFilter filter, CancellationToken ct) : IAsyncEnumerable<RouterEvent>
    {
        public async IAsyncEnumerator<RouterEvent> GetAsyncEnumerator(CancellationToken token = default)
        {
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
            foreach (var e in ring.Replay(filter.SinceSeq))
                yield return e;
            var waiter = new TaskCompletionSource<RouterEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(RouterEvent e)
            {
                if (filter.Matches(e)) waiter.TrySetResult(e);
            }
            ring.Subscribe(Handler);
            try
            {
                while (!link.IsCancellationRequested)
                {
                    var e = await waiter.Task.WaitAsync(link.Token).ConfigureAwait(false);
                    waiter = new TaskCompletionSource<RouterEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                    yield return e;
                }
            }
            finally
            {
                ring.Unsubscribe(Handler);
            }
        }
    }

    // ── dispatch loop ───────────────────────────────────────────────────────

    private readonly SemaphoreSlim _dispatchSignal = new(0, int.MaxValue);

    private void SignalDispatch() => _dispatchSignal.Release();

    private async Task DispatchLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _dispatchSignal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            while (await TryDispatchOnce().ConfigureAwait(false))
            {
                // drain
            }
        }
    }

    private async Task<bool> TryDispatchOnce()
    {
        QueuedRequest? candidate = null;
        EngineRuntime? engine = null;
        bool switchRequired = false;

        lock (_gate)
        {
            foreach (var q in _queues.Values.OrderBy(q => q.Depth))
            {
                var head = q.PeekHead();
                if (head is null) continue;
                var infos = BuildEngineInfos(head);
                var decision = _scheduler.SelectNext(head, new SchedulerView(infos, DateTimeOffset.UtcNow), _config);
                if (decision.Engine is null)
                {
                    MaybeFailDeadline(head, decision.RejectReason ?? "no_available_engine");
                    continue;
                }
                var e = _engines[decision.Engine];
                if (!e.HasCapacity) continue;
                var popped = q.TryPopHead(t => t.Inflight < t.MaxInflight);
                if (popped is null) continue;
                candidate = popped;
                engine = e;
                switchRequired = decision.SwitchRequired;
                popped.Thread.IncrementInflight();
                engine.BeginRequest(popped.PromptChars);   // reserve engine capacity now:
                break;                                     // the drain loop must not over-dispatch
            }
        }

        if (candidate is null || engine is null) return false;

        var ticket = candidate.Ticket;
        ticket.State = switchRequired ? TicketState.WaitingForModel : TicketState.Executing;
        ticket.Engine = engine.Name;
        _events.Append(new RouterEvent
        {
            Kind = "request.selected",
            ThreadId = ticket.ThreadId,
            TicketId = ticket.Id,
            Engine = engine.Name,
            Model = ticket.Model,
        });
        _ = Task.Run(() => ExecuteAsync(candidate, engine, switchRequired), _shutdown.Token);
        return true;
    }

    private EngineRuntimeInfo[] BuildEngineInfos(QueuedRequest head)
    {
        var messages = head.Messages();
        var list = new List<EngineRuntimeInfo>(_engines.Count);
        foreach (var e in _engines.Values)
        {
            list.Add(new EngineRuntimeInfo
            {
                Name = e.Name,
                LoadedModels = e.LoadedModels,
                CatalogModels = e.Definition.Catalog.Keys.ToArray(),
                SwitchingToward = e.SwitchingToward is null ? [] : [e.SwitchingToward],
                Inflight = e.Inflight,
                InflightTokenProxy = e.InflightTokenProxy,
                Healthy = e.Healthy,
                Switching = e.Switching,
                MatchedPrefixChars = e.MatchedPrefixChars(messages),
            });
        }
        return list.ToArray();
    }

    private void MaybeFailDeadline(QueuedRequest head, string reason)
    {
        var maxQueue = head.Request.Options.MaxQueueTime;
        if (maxQueue is { } window && DateTimeOffset.UtcNow - head.Ticket.QueuedAt > window)
        {
            if (GetQueue(head.Ticket.Model)?.TryRemoveById(head.Ticket.Id) == true)
                Fail(head.Ticket, "deadline_exceeded", $"queue wait exceeded {window}");
        }
    }

    // ── execution ───────────────────────────────────────────────────────────

    private async Task ExecuteAsync(QueuedRequest candidate, EngineRuntime engine, bool switchRequired)
    {
        var ticket = candidate.Ticket;
        var thread = candidate.Thread;
        ticket.ExecCts = new CancellationTokenSource();
        var decremented = false;
        var ended = false;
        void DecrementOnce()
        {
            if (decremented) return;
            decremented = true;
            thread.DecrementInflight();
        }
        void EndEngineOnce()
        {
            if (ended) return;
            ended = true;
            engine.EndRequest(candidate.PromptChars);
        }
        try
        {

            if (switchRequired && _config.AutoSwitch)
            {
                ticket.State = TicketState.WaitingForModel;
                var loaded = await engine.WaitForModelAsync(ticket.Model,
                    Timeout.InfiniteTimeSpan, ticket.ExecCts.Token).ConfigureAwait(false);
                if (!loaded)
                {
                    Fail(ticket, "switch_failed", $"engine {engine.Name} could not load {ticket.Model}");
                    return;
                }
                ticket.State = TicketState.Executing;
            }

            Interlocked.Increment(ref thread.Enqueued);
            ticket.StreamChannel = candidate.Request.Stream
                ? Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = true })
                : null;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, ticket.ExecCts.Token);
                var body = ModelRewriter.Rewrite(candidate.Request.Body, engine.CanonicalModel(ticket.Model) ?? ticket.Model);
                var path = LooksLikeCompletion(candidate.Request.Body) ? "/completions" : "/chat/completions";

                using var res = await SendAsync(engine, body, path, cts.Token).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    var errBody = await res.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                    Fail(ticket, "engine_error",
                        $"{(int)res.StatusCode}: {System.Text.Encoding.UTF8.GetString(errBody.AsSpan(0, Math.Min(errBody.Length, 256)))}");
                    return;
                }

                if (candidate.Request.Stream)
                {
                    var channel = ticket.StreamChannel!;
                    await PumpStreamAsync(engine, ticket, res.Content, channel, cts.Token).ConfigureAwait(false);
                    Complete(ticket);
                }
                else
                {
                    var bytes = await res.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                    Complete(ticket, bytes);
                }
            }
            finally
            {
                engine.RecordPrefix(candidate.Messages());
            }
        }
        catch (OperationCanceledException)
        {
            Fail(ticket, "cancelled", "cancelled");
        }
        catch (Exception ex)
        {
            Fail(ticket, "engine_unreachable", ex.Message);
        }
        finally
        {
            EndEngineOnce();
            SignalDispatch();          // wake dispatch AFTER capacity is released
            ticket.ExecCts?.Dispose();
            ticket.ExecCts = null;
            DecrementOnce();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(EngineRuntime engine, byte[] body, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        if (engine.ApiKey is not null)
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {engine.ApiKey}");
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return await engine.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private async Task PumpStreamAsync(EngineRuntime engine, RouterTicket ticket, HttpContent content,
        Channel<ReadOnlyMemory<byte>> channel, CancellationToken ct)
    {
        var firstToken = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var buffer = new byte[16384];
            using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (read == 0) break;
                if (!firstToken)
                {
                    firstToken = true;
                    _events.Append(new RouterEvent
                    {
                        Kind = "request.streaming_first_token",
                        ThreadId = ticket.ThreadId,
                        TicketId = ticket.Id,
                        Engine = engine.Name,
                        Value = sw.ElapsedMilliseconds,
                    });
                }
                var chunk = new byte[read];
                buffer.AsSpan(0, read).CopyTo(chunk);
                await channel.Writer.WriteAsync(chunk, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled mid-stream: reader will observe terminal completion below
        }
        finally
        {
            channel.Writer.Complete();
        }
    }

    private void Complete(RouterTicket ticket, byte[]? body = null)
    {
        ticket.State = TicketState.Completed;
        ticket.CompletionTcs.TrySetResult(body ?? []);
        var thread = TicketThread(ticket);
        if (thread is not null) Interlocked.Increment(ref thread.Completed);
        _events.Append(new RouterEvent
        {
            Kind = "request.completed",
            ThreadId = ticket.ThreadId,
            TicketId = ticket.Id,
            Engine = ticket.Engine,
            Model = ticket.Model,
        });
        SignalDispatch();
    }

    private void Fail(RouterTicket ticket, string code, string message)
    {
        ticket.State = code is "cancelled" ? TicketState.Cancelled : TicketState.Failed;
        ticket.ErrorCode = code;
        ticket.Error = message;
        ticket.CompletionTcs.TrySetException(new RouterRequestException(code, message));
        _events.Append(new RouterEvent
        {
            Kind = code is "cancelled" ? "request.cancelled" : "request.failed",
            ThreadId = ticket.ThreadId,
            TicketId = ticket.Id,
            Engine = ticket.Engine,
            Model = ticket.Model,
            Reason = message,
        });
        SignalDispatch();
    }

    private RouterThread? TicketThread(RouterTicket ticket)
    {
        lock (_gate) return _threads.GetValueOrDefault(ticket.ThreadId);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private string NewTicketId() => $"t{Interlocked.Increment(ref _seq):x8}-{Random.Shared.Next(0xffff):x4}";

    private string ResolveCanonical(string requested)
    {
        foreach (var e in _engines.Values)
        {
            var canonical = e.CanonicalModel(requested);
            if (canonical is not null) return canonical;
        }
        return requested;
    }

    private ModelQueue? GetQueue(string canonicalModel)
    {
        lock (_gate) return _queues.GetValueOrDefault(canonicalModel);
    }

    private ModelQueue GetOrCreateQueue(string canonicalModel)
    {
        lock (_gate)
        {
            if (!_queues.TryGetValue(canonicalModel, out var q))
            {
                q = new ModelQueue(canonicalModel, _config.Scheduler.MaxQueuePerModel, OnQueueRejected);
                _queues[canonicalModel] = q;
            }
            return q;
        }
    }

    private void OnQueueRejected(QueuedRequest item, string reason)
    {
        Interlocked.Increment(ref item.Thread.Rejected);
        item.Ticket.State = TicketState.Failed;
        item.Ticket.ErrorCode = reason;
        item.Ticket.Error = $"queue for {item.Ticket.Model} is full";
        item.Ticket.CompletionTcs.TrySetException(new RouterRequestException(reason, item.Ticket.Error));
        _events.Append(new RouterEvent
        {
            Kind = "queue.rejected",
            ThreadId = item.Thread.ThreadId,
            TicketId = item.Ticket.Id,
            Model = item.Ticket.Model,
            Reason = reason,
        });
    }

    /// <summary>Token-proxy: total prompt chars (messages content or legacy prompt), from the raw body.</summary>
    internal static long PromptCharsProbe(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            long total = 0;
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                    total += SumContent(msgs);
                if (doc.RootElement.TryGetProperty("prompt", out var prompt))
                {
                    if (prompt.ValueKind == JsonValueKind.String) total += prompt.GetString()?.Length ?? 0;
                    else if (prompt.ValueKind == JsonValueKind.Array) total += SumContent(prompt);
                }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static long SumContent(JsonElement array)
    {
        long total = 0;
        foreach (var m in array.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            if (m.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                    total += content.GetString()?.Length ?? 0;
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.Object &&
                            part.TryGetProperty("text", out var text) &&
                            text.ValueKind == JsonValueKind.String)
                            total += text.GetString()?.Length ?? 0;
                    }
                }
            }
        }
        return total;
    }

    internal static bool LooksLikeCompletion(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("prompt", out _);
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        foreach (var e in _engines.Values) await e.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class RouterRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record EngineStats
{
    public required string Engine { get; init; }
    public required bool Healthy { get; init; }
    public required string[] LoadedModels { get; init; }
    public required int Inflight { get; init; }
    public required string? SwitchingToward { get; init; }
}

public sealed record ThreadStats
{
    public required string ThreadId { get; init; }
    public required ThreadPriority Priority { get; init; }
    public required double Weight { get; init; }
    public required long Enqueued { get; init; }
    public required long Completed { get; init; }
}

public sealed record RouterStats(
    EngineStats[] Engines,
    IReadOnlyDictionary<string, int> QueueDepths,
    ThreadStats[] Threads);

/// <summary>Immutable projection of one ticket for the admin API (AOT-safe DTO).</summary>
public sealed record TicketSnapshot
{
    public required string Id { get; init; }
    public required string ThreadId { get; init; }
    public required string Model { get; init; }
    public required DateTimeOffset QueuedAt { get; init; }
    /// <summary>TicketState as string (avoids enum source-gen pitfalls in metadata mode).</summary>
    public required string State { get; init; }
    public string? Engine { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}

public interface IInferenceRouter : IAsyncDisposable
{
    RouterThread RegisterThread(string threadId, ThreadPriority priority, RouterThreadOptions? options = null);
    void SetThreadPriority(string threadId, ThreadPriority priority);
    ValueTask<RouterTicket> EnqueueAsync(string threadId, InferenceRequest request, CancellationToken ct = default);
    bool TryCancel(string ticketId);
    RouterStats GetStats();
    IAsyncEnumerable<RouterEvent> SubscribeEvents(RouterEventFilter? filter = null, CancellationToken ct = default);
}

/// <summary>Top-level "model" rewrite in a raw JSON body without reflection: parse, re-serialize
/// substituting the model property. Used to map aliases onto the engine's canonical id.</summary>
public static class ModelRewriter
{
    public static byte[] Rewrite(ReadOnlyMemory<byte> body, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            using var ms = new MemoryStream(body.Length + 16);
            using (var writer = new Utf8JsonWriter(ms))
            {
                WriteWithModel(doc.RootElement, writer, model);
            }
            return ms.ToArray();
        }
        catch
        {
            return body.ToArray();
        }
    }

    private static void WriteWithModel(JsonElement element, Utf8JsonWriter writer, string model)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            element.WriteTo(writer);
            return;
        }
        writer.WriteStartObject();
        var wrote = false;
        foreach (var p in element.EnumerateObject())
        {
            if (p.Name == "model" && p.Value.ValueKind == JsonValueKind.String)
            {
                writer.WritePropertyName(p.Name);
                writer.WriteStringValue(model);
                wrote = true;
            }
            else
            {
                p.WriteTo(writer);
            }
        }
        if (!wrote)
        {
            writer.WritePropertyName("model");
            writer.WriteStringValue(model);
        }
        writer.WriteEndObject();
    }
}

file static class QueuedRequestExtensions
{
    public static IReadOnlyList<string> Messages(this QueuedRequest q)
    {
        try
        {
            using var doc = JsonDocument.Parse(q.Request.Body);
            var messages = new List<string>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("messages", out var msgs) &&
                msgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in msgs.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.Object &&
                        m.TryGetProperty("content", out var c) &&
                        c.ValueKind == JsonValueKind.String)
                        messages.Add(c.GetString() ?? "");
                }
            }
            return messages;
        }
        catch
        {
            return [];
        }
    }
}
