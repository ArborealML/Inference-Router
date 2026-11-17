using System.Globalization;
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
    /// <summary>The same engines as the capability scan sees, fixed once at construction. A stable list is
    /// what lets one scan prove that no engine published while it was deciding (§5.5).</summary>
    private ICapabilitySource[] _capabilitySources = [];
    private readonly Dictionary<string, ModelQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RouterThread> _threads = new(StringComparer.Ordinal);
    // Ticket lookup has its own lock. A full queue rejects a ticket from inside the queue's lock,
    // and taking _gate there would invert the _gate → queue order the dispatcher uses.
    private readonly Dictionary<string, RouterTicket> _tickets = new(StringComparer.Ordinal);
    private readonly object _ticketGate = new();
    private readonly Queue<string> _terminalTickets = new();
    /// <summary>How many finished tickets stay answerable to the admin surface. Same order of
    /// magnitude as the event ring (4096), so ticket lookups and event replay cover a comparable
    /// window instead of the dictionary growing with traffic forever.</summary>
    private const int TicketRetention = 4096;
    // Held so Subscribe and Unsubscribe pass the same delegate instance.
    private readonly Action<RouterEvent> _wakeOnEngineState;
    private readonly object _gate = new();
    private long _seq;
    private readonly CancellationTokenSource _shutdown = new();

    public RouterCore(RouterConfig config, IRequestScheduler? scheduler = null, EventRing? events = null)
    {
        _config = config;
        _wakeOnEngineState = WakeOnEngineState;
        _scheduler = scheduler ?? new PrefixAwareScheduler();
        _events = events ?? new EventRing(maxLiveSubscribers: config.MaxEventSubscribers);
        foreach (var (name, def) in config.Engines)
            _engines[name] = new EngineRuntime(def, config, _events, config.HttpHandlerFactory?.Invoke(def));
        foreach (var (id, td) in config.Threads)
            _threads[id] = new RouterThread(id, td.Priority, td.Weight, td.ModelAffinity);
        _capabilitySources = [.. _engines.Values];
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
        _events.Subscribe(_wakeOnEngineState);
        _ = Task.Run(DispatchLoop, _shutdown.Token);
    }

    /// <summary>Dispatch must be level-triggered on engine state, not only edge-triggered by a new
    /// request: a request that arrived while no engine was healthy yet (router starting up, a health
    /// poll in flight, a model switch finishing) has to be reconsidered when the engine state changes.
    /// Otherwise it sits beside a free engine slot until something unrelated happens to wake the loop.</summary>
    private void WakeOnEngineState(RouterEvent e)
    {
        if (e.Kind is "engine.health" or "engine.profile" or "engine.switch_started"
                     or "engine.switch_completed" or "engine.switch_failed")
            SignalDispatch();
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
        var promptChars = PromptCharsProbe(request.Body);
        var queued = new QueuedRequest
        {
            Ticket = ticket,
            Request = request,
            PriorityNode = node,
            Thread = thread,
            EnqueuedSeq = node.Sequence,
            PromptChars = promptChars,
            RequiredContextTokens = opts.RequiredContext is { } rc && rc > 0
                ? rc
                : EstimateContextTokens(promptChars, _config.Scheduler.CharsPerTokenEstimate),
        };
        ticket.State = TicketState.Queued;
        // The cancel signal exists the moment the ticket is findable: if it were created only when
        // execution starts, an abort in the window between pop and execution would find nothing to
        // cancel and the request would run to completion for a caller that already hung up. A ticket
        // that never dispatches leaves it to the GC — it holds no unmanaged resource, and disposing
        // it from the cancel path would race a dispatch that is concurrently starting.
        ticket.ExecCts = new CancellationTokenSource();
        lock (_ticketGate) _tickets[ticket.Id] = ticket;
        // A model that nothing in this configuration can serve is refused before it ever reaches a queue
        // (§5.5). The check sits ahead of the cap on purpose: for a model no engine could take, "come back
        // later" (429) is a worse answer than "nobody here serves this" (503), and refusing puts nothing in
        // that queue, so the cap stays free for traffic that could actually run.
        if (CapabilityScan.DefinitelyHopeless(_capabilitySources, canonical))
        {
            RejectAtAdmission(queued, "model_unavailable",
                $"no engine in this configuration declares or currently reports model '{canonical}'");
            return ValueTask.FromResult(ticket);
        }
        var accepted = GetOrCreateQueue(canonical).TryEnqueue(queued);
        if (accepted && ct.CanBeCanceled)
        {
            // A caller that hangs up is a cancellation, wired to the same path an explicit cancel
            // takes from the moment the ticket can be found. Registered after the enqueue because a
            // full queue rejects the ticket inside TryEnqueue, and re-checked in case the abort
            // landed while we were getting here.
            ticket.CallerLink = ct.Register(() => TryCancel(ticket.Id));
            if (ct.IsCancellationRequested) TryCancel(ticket.Id);
        }
        if (accepted && opts.MaxQueueTime is { } window && window > TimeSpan.Zero)
        {
            // The queue bound has to be a timer rather than a check at dispatch time: a busy engine
            // raises no dispatch signal, so a waiter whose deadline passed would keep its place in the
            // queue until something else happened to wake the dispatcher.
            ticket.QueueTimer = new System.Threading.Timer(
                _ => FailExpiredQueuedWait(ticket, window), null, window, Timeout.InfiniteTimeSpan);
        }
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
        lock (_ticketGate)
        {
            ticket = _tickets.GetValueOrDefault(ticketId);
            if (ticket is null) return false;
        }
        var snapshot = new List<ModelQueue>();
        lock (_gate) snapshot.AddRange(_queues.Values);
        foreach (var q in snapshot)
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
                ActiveProfile = e.ActiveProfile,
                ActiveContextWindow = e.ActiveContextWindow,
                ActiveSlots = e.ActiveSlots,
            }).ToArray();
            var queues = _queues.Values.ToDictionary(q => q.Model, q => q.Depth, StringComparer.Ordinal);
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
        lock (_ticketGate)
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
        => new EventEnumerable(_events, filter ?? new RouterEventFilter(), ct, _shutdown.Token);

    private sealed class EventEnumerable(EventRing ring, RouterEventFilter filter, CancellationToken caller, CancellationToken shutdown)
        : IAsyncEnumerable<RouterEvent>
    {
        // A slow subscriber must not become an unbounded queue. Silent pruning is not acceptable: with
        // DropWrite a *filtered* reader sees no Seq gap at all, so a dropped terminal event would look
        // like a stream that simply has nothing more coming. Instead the first overflow closes the feed.
        // That is the strongest signal available without a new event kind — it says "this stream is over,
        // reconnect from the Seq you last saw" — but it does not say how much was missed, and it looks
        // the same as a feed that legitimately ended. Counting the loss on the wire is an open decision
        // (SPEC §10, `stream.gap`).
        private const int SubscriberBuffer = 256;

        public async IAsyncEnumerator<RouterEvent> GetAsyncEnumerator(CancellationToken token = default)
        {
            // A cursor from a previous router lifetime must not silence the live tail: every event this
            // router will ever emit has a smaller Seq than that cursor, so an unreconciled `since`
            // subscribes a feed that can never deliver anything. Clamp it to the current head — the
            // honest reading of "I have seen everything up to here" is "start from now".
            var sinceSeq = Math.Min(filter.SinceSeq, ring.CurrentSeq);
            var live = sinceSeq == filter.SinceSeq ? filter : filter with { SinceSeq = sinceSeq };

            using var link = CancellationTokenSource.CreateLinkedTokenSource(caller, token, shutdown);
            var queue = Channel.CreateBounded<RouterEvent>(new BoundedChannelOptions(SubscriberBuffer)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            var dropped = false;
            void Handler(RouterEvent e)
            {
                if (dropped || !live.Matches(e)) return;
                // Never block inside the ring's lock. If the buffer is full the reader is too slow:
                // close the feed instead of dropping, so the loss is visible as an ended stream.
                if (!queue.Writer.TryWrite(e))
                {
                    dropped = true;
                    queue.Writer.TryComplete();
                }
            }

            // Snapshot and subscribe are one atomic step: subscribing first would double-count the
            // events appended in between, replaying first would lose them. A full ring refuses, and the
            // refusal is a return value so the HTTP layer can answer 503 before it commits to SSE
            // headers; a reader that gets here anyway sees an empty feed rather than an exception.
            if (!ring.TrySubscribeReplay(sinceSeq, Handler, out var replayed))
            {
                queue.Writer.TryComplete();
                yield break;
            }
            try
            {
                foreach (var e in replayed)
                {
                    if (link.Token.IsCancellationRequested) break;
                    if (live.Matches(e)) yield return e;
                }

                while (true)
                {
                    bool more;
                    try { more = await queue.Reader.WaitToReadAsync(link.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }   // caller hung up, or router stopping
                    if (!more) break;
                    // Stop pulling as soon as the caller is gone: a reader that hung up must not be
                    // fed the rest of its buffer while the router keeps producing.
                    while (!link.Token.IsCancellationRequested && queue.Reader.TryRead(out var e)) yield return e;
                }
            }
            finally
            {
                ring.Unsubscribe(Handler);
                queue.Writer.TryComplete();
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
        string? targetProfile = null;

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
                    // The scheduler's reason (no_engines / no_available_engine) is internal: read by the
                    // scheduler's own tests, never emitted to the feed and never the caller's answer, whose
                    // wait ends as deadline_exceeded if it ends at all (§6.1). A request that nothing here
                    // could ever take is refused before it is queued (§5.5), so what reaches this line is a
                    // wait that could still resolve.
                    MaybeFailDeadline(head);
                    continue;
                }
                var e = _engines[decision.Engine];
                if (!e.HasCapacity) continue;
                var popped = q.TryPopHead(t => t.Inflight < t.MaxInflight);
                if (popped is null) continue;
                candidate = popped;
                engine = e;
                switchRequired = decision.SwitchRequired;
                targetProfile = decision.TargetProfile;
                popped.Thread.IncrementInflight();
                engine.BeginRequest(popped.PromptChars);   // reserve engine capacity now:
                break;                                     // the drain loop must not over-dispatch
            }
        }

        if (candidate is null || engine is null) return false;

        var ticket = candidate.Ticket;
        if (candidate.Request.Stream && ticket.StreamChannel is null)
        {
            // Publish the channel with the state change: a listener that sees Executing must be
            // able to read the stream from that moment, not only after the engine call starts.
            ticket.StreamChannel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        }

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
        try
        {
            // No cancellation token on Task.Run here: with an already-cancelled token Task.Run hands
            // back a task that never runs the delegate, which would strand the thread slot and the
            // engine slot reserved at pop. ExecuteAsync links its own CTS to _shutdown.
            _ = Task.Run(() => ExecuteAsync(candidate, engine, switchRequired, targetProfile));
        }
        catch (Exception)
        {
            engine.EndRequest(candidate.PromptChars);
            candidate.Thread.DecrementInflight();
            Fail(ticket, "cancelled", "router is shutting down");
            SignalDispatch();
        }
        return true;
    }

    private EngineRuntimeInfo[] BuildEngineInfos(QueuedRequest head)
    {
        var messages = head.Messages();
        var list = new List<EngineRuntimeInfo>(_engines.Count);
        foreach (var e in _engines.Values)
        {
            // One snapshot per engine, and every capability-shaped field derived from that one read: the
            // loaded list, the switch marker and the eligibility bit cannot come from different instants and
            // then disagree inside one scheduling decision (§5.5).
            var cap = e.Capability;
            list.Add(new EngineRuntimeInfo
            {
                Name = e.Name,
                LoadedModels = cap.LoadedModels,
                CatalogModels = e.Definition.Catalog.Keys.ToArray(),
                SwitchingToward = cap.SwitchingToward is null ? [] : [cap.SwitchingToward],
                CanServeModel = e.CanServeModel(head.Ticket.Model, cap),
                Inflight = e.Inflight,
                InflightTokenProxy = e.InflightTokenProxy,
                Healthy = cap.Healthy,
                Switching = cap.SwitchingToward is not null,
                MatchedPrefixChars = e.MatchedPrefixChars(messages),
                ActiveContextWindow = e.ActiveContextWindow,
                ServingProfiles = e.ProfilesForModel(head.Ticket.Model),
            });
        }
        return list.ToArray();
    }

    /// <summary>Fails a request whose queue wait outlived the deadline it declared. If the ticket is
    /// no longer in a queue it was already dispatched — the deadline bounds the wait, not the
    /// execution — or it is terminal, so there is nothing to do.</summary>
    private void FailExpiredQueuedWait(RouterTicket ticket, TimeSpan window)
    {
        var snapshot = new List<ModelQueue>();
        lock (_gate) snapshot.AddRange(_queues.Values);
        foreach (var q in snapshot)
        {
            if (q.TryRemoveById(ticket.Id))
            {
                Fail(ticket, "deadline_exceeded", $"queue wait exceeded {window}");
                return;
            }
        }
    }

    private void MaybeFailDeadline(QueuedRequest head)
    {
        var maxQueue = head.Request.Options.MaxQueueTime;
        if (maxQueue is { } window && DateTimeOffset.UtcNow - head.Ticket.QueuedAt > window)
        {
            if (GetQueue(head.Ticket.Model)?.TryRemoveById(head.Ticket.Id) == true)
                Fail(head.Ticket, "deadline_exceeded", $"queue wait exceeded {window}");
        }
    }

    // ── execution ───────────────────────────────────────────────────────────

    private async Task ExecuteAsync(QueuedRequest candidate, EngineRuntime engine, bool switchRequired, string? targetProfileName)
    {
        var ticket = candidate.Ticket;
        var thread = candidate.Thread;
        var targetProfile = engine.GetProfile(ticket.Model, targetProfileName);
        var execCts = ticket.ExecCts ??= new CancellationTokenSource();
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

            if ((switchRequired || targetProfile is not null) && _config.AutoSwitch)
            {
                ticket.State = TicketState.WaitingForModel;
                // A ticket's own deadline bounds only its time in the queue, so the wait for a model
                // switch needs its own ceiling: without one a request is parked through a model load as
                // long as the engine's lifecycle wait allows (two hours for a strata select). 0 = no
                // ceiling, which is the old behaviour.
                var ceilingMs = _config.Scheduler.SwitchWaitTimeoutMs;
                var switchWait = ceilingMs <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(ceilingMs);
                // The caller's token bounds only its own wait: the switch transaction runs on the engine's
                // token, so giving up here never leaves the engine half-switched with nothing loaded.
                var switchResult = targetProfile is null
                    ? await engine.WaitForModelAsync(ticket.Model, switchWait, execCts.Token).ConfigureAwait(false)
                    : await engine.WaitForProfileAsync(ticket.Model, targetProfile, switchWait, execCts.Token).ConfigureAwait(false);
                if (!switchResult.IsReady)
                {
                    var what = targetProfile is null ? ticket.Model : $"{ticket.Model} @ {targetProfile.Name}";
                    // Failed and TimedOut answer the only question the caller has — retry now and hit the
                    // same wall, or retry later and probably get through — so the message keeps them apart.
                    var detail = switchResult.Detail is { Length: > 0 } d ? $" ({d})" : "";
                    Fail(ticket, "switch_failed", switchResult.Outcome == EngineRuntime.SwitchOutcome.TimedOut
                        ? $"engine {engine.Name} did not become ready to serve {what} within {ceilingMs} ms — " +
                          $"the switch is still running, retry once it completes{detail}"
                        : $"engine {engine.Name} could not serve {what}{detail}");
                    return;
                }
                ticket.State = TicketState.Executing;
            }

            Interlocked.Increment(ref thread.Enqueued);
            if (candidate.Request.Stream)
            {
                ticket.StreamChannel ??= Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            }
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, execCts.Token);
                var body = ModelRewriter.Rewrite(candidate.Request.Body, engine.CanonicalModel(ticket.Model) ?? ticket.Model);
                var suffix = LooksLikeCompletion(candidate.Request.Body) ? "completions" : "chat/completions";

                // The cap bounds the wait for the engine to *answer*: the whole response body for a
                // non-stream call, the first byte of the body for a stream. Response headers are not an
                // answer — an engine may send 200 and then go quiet, and holding a slot, a ticket and a
                // caller through that is what this cap exists to prevent. Once the owed bytes are in hand
                // the gate is disarmed and a long generation runs to its end: one gate per request, not
                // per token.
                var responseTimeout = engine.ResponseTimeout;
                if (responseTimeout is null)
                {
                    using var res = await SendAsync(engine, body, suffix, cts.Token).ConfigureAwait(false);
                    await FinishWithResponseAsync(engine, ticket, res, candidate, gate: null, cts.Token).ConfigureAwait(false);
                }
                else
                {
                    // The gate must NOT share the caller's token: cancelling that token would complete the
                    // delay, Task.WhenAny would report the delay as the winner, and a caller who hung up
                    // would be answered `engine_timeout` instead of `cancelled`. It runs on its own source,
                    // which is disarmed when the request ends or when the engine delivers what it owed.
                    using var gate = new ResponseGate(responseTimeout.Value);
                    var delay = Task.Delay(Timeout.Infinite, gate.Token);
                    var send = SendAsync(engine, body, suffix, cts.Token);
                    HttpResponseMessage? res = null;
                    bool silent;
                    try
                    {
                        if (await Task.WhenAny(send, delay).ConfigureAwait(false) == send)
                        {
                            res = await send.ConfigureAwait(false);
                            try
                            {
                                await FinishWithResponseAsync(engine, ticket, res, candidate, gate, cts.Token).ConfigureAwait(false);
                                silent = false;
                            }
                            catch (Exception) when (gate.Tripped && !cts.IsCancellationRequested)
                            {
                                // The gate tripped while the body was still owed. The caller did not go
                                // away and the engine is not unreachable: it answered and then stalled.
                                // Whether the read reports that as cancellation or as a broken connection
                                // depends on the handler, so the ceiling — not the exception type — decides.
                                silent = true;
                            }
                        }
                        else
                        {
                            silent = true;
                        }
                    }
                    finally
                    {
                        gate.Dispose();      // always disarm: no pending timer outlives the request
                        _ = delay.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                        res?.Dispose();
                        if (res is null)
                        {
                            _ = send.ContinueWith(static t =>
                            {
                                if (t.Status == TaskStatus.RanToCompletion) { t.Result.Dispose(); }
                                else { _ = t.Exception; }
                            }, TaskScheduler.Default);
                        }
                    }

                    if (silent)
                    {
                        cts.Cancel();          // release the connection to the engine that never answered
                        if (execCts.IsCancellationRequested || _shutdown.IsCancellationRequested)
                        {
                            // Somebody upstream gave up before the ceiling did, and their reason is the
                            // honest one: a request the caller abandoned stays `cancelled` even when the
                            // engine's handler ignored the cancellation we sent and the ceiling fired first.
                            Fail(ticket, "cancelled", "cancelled");
                            return;
                        }

                        Fail(ticket, "engine_timeout",
                            $"engine {engine.Name} sent no response to {ticket.Model} within " +
                            $"{responseTimeout.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
                        return;
                    }
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

    private static async Task<HttpResponseMessage> SendAsync(EngineRuntime engine, byte[] body, string suffix, CancellationToken ct)
    {
        // The suffix is composed against the endpoint path (EngineRuntime.Combine), so an endpoint
        // mounted under /v1 keeps it.
        using var req = new HttpRequestMessage(HttpMethod.Post, engine.CompletionUri(suffix));
        if (engine.ApiKey is not null)
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {engine.ApiKey}");
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return await engine.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>What happens once the engine has answered: an error status becomes the router's own
    /// error contract, a stream is relayed frame by frame, a normal completion is returned as bytes.
    /// Split out so the capped and uncapped send paths cannot drift apart. `gate` is the response cap;
    /// for a non-streaming call it stays armed across the body read, and for a stream the pump disarms it
    /// at the first byte so a generation in progress is never truncated.</summary>
    private async Task FinishWithResponseAsync(EngineRuntime engine, RouterTicket ticket,
        HttpResponseMessage res, QueuedRequest candidate, ResponseGate? gate, CancellationToken ct)
    {
        // The body is the answer, so the cap covers the body: linked with the caller's token so an abort
        // still ends as cancelled rather than as a timeout.
        using var bodyLink = gate is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, gate.Token);
        var bodyCt = bodyLink?.Token ?? ct;

        if (!res.IsSuccessStatusCode)
        {
            var errBody = await res.Content.ReadAsByteArrayAsync(bodyCt).ConfigureAwait(false);
            // The body is in hand, so the ceiling is spent: pass it *before* the terminal call, otherwise a
            // failure inside Fail/event work could be caught by the ceiling filter and blamed on the engine.
            gate?.Pass();
            Fail(ticket, "engine_error",
                $"{(int)res.StatusCode}: {System.Text.Encoding.UTF8.GetString(errBody.AsSpan(0, Math.Min(errBody.Length, 256)))}");
            return;
        }

        if (candidate.Request.Stream)
        {
            var channel = ticket.StreamChannel!;
            await PumpStreamAsync(engine, ticket, res.Content, channel, gate, ct).ConfigureAwait(false);
            Complete(ticket);
        }
        else
        {
            var bytes = await res.Content.ReadAsByteArrayAsync(bodyCt).ConfigureAwait(false);
            gate?.Pass();      // owed bytes delivered: the ceiling is spent before any terminal work
            Complete(ticket, bytes);
        }
    }

    /// <summary>The response cap as a gate on the bytes the engine owes. Armed with the configured
    /// ceiling; <see cref="Pass"/> disarms it once those bytes are in hand. Disarming is a cancel, so
    /// <see cref="Tripped"/> is what tells "the engine went quiet" apart from "we let the timer go" —
    /// and a caller's own cancellation lives on a different token entirely, which is what keeps a client
    /// that hangs up reported as cancelled.</summary>
    private sealed class ResponseGate : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private int _passed;

        public ResponseGate(TimeSpan dueIn) => _cts = new CancellationTokenSource(dueIn);
        public CancellationToken Token => _cts.Token;
        public void Pass() { Interlocked.Exchange(ref _passed, 1); _cts.Cancel(); }
        public bool Tripped => Volatile.Read(ref _passed) == 0 && _cts.IsCancellationRequested;
        public void Dispose() => _cts.Dispose();
    }

    private async Task PumpStreamAsync(EngineRuntime engine, RouterTicket ticket, HttpContent content,
        Channel<ReadOnlyMemory<byte>> channel, ResponseGate? gate, CancellationToken ct)
    {
        var firstToken = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Only the first read is bounded: a stream that has begun is relayed to its end whatever the
        // engine's ceiling is, because cutting a generation mid-token is worse than a slow one.
        using var firstReadLink = gate is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, gate.Token);
        var firstReadCt = firstReadLink?.Token ?? ct;
        var buffer = new byte[16384];
        // Stream acquisition is inside the ceiling too: with ResponseHeadersRead the content object exists
        // before any byte does, and a handler that builds the stream lazily must not be able to stall the
        // request before the first gated read happens.
        using var stream = await content.ReadAsStreamAsync(firstReadCt).ConfigureAwait(false);
        while (true)
        {
            int read;
            try
            {
                read = firstToken
                    ? await stream.ReadAsync(buffer, ct).ConfigureAwait(false)
                    : await stream.ReadAsync(buffer, firstReadCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A cancelled first read is only the caller going away if the gate did not trip.
                if (!firstToken && gate is not null && gate.Tripped) throw;
                break;
            }
            if (read == 0) break;
            if (!firstToken)
            {
                firstToken = true;
                gate?.Pass();
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

        // Reaching the end of the loop because the caller went away is not the same as reaching
        // the end of the stream: a truncated stream must not be counted as a finished one.
        ct.ThrowIfCancellationRequested();
        // The channel is *not* closed here. The terminal transition (Complete / Fail) owns it, so the
        // relay is never let go before the ticket knows its own outcome: close the channel first and the
        // HTTP side reports a stream that merely stopped instead of the failure that stopped it.
    }

    private void Complete(RouterTicket ticket, byte[]? body = null)
    {
        if (!ticket.TryEnterTerminal()) return;
        ticket.DetachWaitHooks();
        DisposeQueueTimer(ticket);
        RetainTerminalTicket(ticket);
        ticket.State = TicketState.Completed;
        // Count the ticket finished BEFORE waking the caller: a caller that awaits its own ticket and
        // then reads /admin/status must see its own request accounted for, not a stale snapshot.
        var thread = TicketThread(ticket);
        if (thread is not null) Interlocked.Increment(ref thread.Completed);
        // And the same rule for the feed (§10): the terminal event is appended before the caller is
        // woken, so a client that awaits its own ticket and then replays its history (or tails
        // /admin/events) finds its ending already there instead of racing it.
        _events.Append(new RouterEvent
        {
            Kind = "request.completed",
            ThreadId = ticket.ThreadId,
            TicketId = ticket.Id,
            Engine = ticket.Engine,
            Model = ticket.Model,
        });
        ticket.StreamChannel?.Writer.TryComplete();
        ticket.CompletionTcs.TrySetResult(body ?? []);
        SignalDispatch();
    }

    private void Fail(RouterTicket ticket, string code, string message)
    {
        if (!ticket.TryEnterTerminal()) return;
        ticket.DetachWaitHooks();
        DisposeQueueTimer(ticket);
        RetainTerminalTicket(ticket);
        ticket.State = code is "cancelled" ? TicketState.Cancelled : TicketState.Failed;
        ticket.ErrorCode = code;
        ticket.Error = message;
        // A stream that will never produce another byte has to be closed. The HTTP side pumps the
        // channel until its writer completes, so a failure that leaves the channel open parks the
        // caller on a 200 response that can no longer say anything — not even its own error frame.
        ticket.StreamChannel?.Writer.TryComplete();
        _events.Append(new RouterEvent
        {
            Kind = code is "cancelled" ? "request.cancelled" : "request.failed",
            ThreadId = ticket.ThreadId,
            TicketId = ticket.Id,
            Engine = ticket.Engine,
            Model = ticket.Model,
            Reason = message,
        });
        ticket.CompletionTcs.TrySetException(new RouterRequestException(code, message));
        SignalDispatch();
    }

    /// <summary>The queue-wait timer is created here, so it is released here: the ticket only carries
    /// the reference. Disposing does not wait for a callback that is already running, and that
    /// callback is guarded by the ticket's single terminal transition.</summary>
    private static void DisposeQueueTimer(RouterTicket ticket)
    {
        var timer = ticket.QueueTimer;
        ticket.QueueTimer = null;
#pragma warning disable IDISP007 // created in EnqueueAsync and only parked on the ticket: this type owns it
        if (timer is not null) timer.Dispose();
#pragma warning restore IDISP007
    }

    /// <summary>Terminal tickets stay lookups-able for a bounded window (the admin surface reads
    /// them after the fact), then the oldest are evicted so the dictionary cannot grow with traffic.</summary>
    private void RetainTerminalTicket(RouterTicket ticket)
    {
        lock (_ticketGate)
        {
            _terminalTickets.Enqueue(ticket.Id);
            while (_terminalTickets.Count > TicketRetention)
                _tickets.Remove(_terminalTickets.Dequeue());
        }
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
        => RejectAtAdmission(item, reason, $"queue for {item.Ticket.Model} is full");

    /// <summary>One shape for every request refused before it joins a queue: terminal once, thread counter
    /// moved, caller answered with the code, and exactly one <c>queue.rejected</c> event — no
    /// <c>request.enqueued</c>, because this ticket never entered a queue and the feed does not pretend
    /// otherwise (§10). The queue-cap rejection and the unservable-model refusal (§5.5) are the two callers, so
    /// their shapes cannot drift apart.</summary>
    private void RejectAtAdmission(QueuedRequest item, string reason, string message)
    {
        Interlocked.Increment(ref item.Thread.Rejected);
        if (!item.Ticket.TryEnterTerminal()) return;
        item.Ticket.DetachWaitHooks();
        DisposeQueueTimer(item.Ticket);
        RetainTerminalTicket(item.Ticket);
        item.Ticket.State = TicketState.Failed;
        item.Ticket.ErrorCode = reason;
        item.Ticket.Error = message;
        item.Ticket.CompletionTcs.TrySetException(new RouterRequestException(reason, message));
        _events.Append(new RouterEvent
        {
            Kind = "queue.rejected",
            ThreadId = item.Thread.ThreadId,
            TicketId = item.Ticket.Id,
            Model = item.Ticket.Model,
            Reason = reason,
        });
    }

    /// <summary>Cheap context-demand estimate when the client does not declare one:
    /// prompt chars / chars_per_token_estimate, rounded up (excludes requested output; the
    /// scheduler ladder decision is deliberately conservative on the prompt side).</summary>
    internal static long EstimateContextTokens(long promptChars, double charsPerToken)
    {
        if (promptChars <= 0) return 0;
        var divisor = charsPerToken < 1.0 ? 1.0 : charsPerToken;
        var estimate = promptChars / divisor;
        if (estimate >= long.MaxValue) return long.MaxValue;
        return (long)Math.Ceiling(estimate);
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
        // A queued ticket has no execution to cancel, so it needs an explicit terminal transition;
        // without this its relays sit open until the process exits.
        var pending = new List<QueuedRequest>();
        lock (_gate)
        {
            foreach (var q in _queues.Values) pending.AddRange(q.DrainAll());
            _queues.Clear();
        }
        foreach (var p in pending) Fail(p.Ticket, "cancelled", "router shutting down");
        _events.Unsubscribe(_wakeOnEngineState);
        foreach (var e in _engines.Values) await e.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
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
    /// <summary>Serving profile inferred from the live context window (null = unknown).</summary>
    public string? ActiveProfile { get; init; }
    /// <summary>Context window the engine currently serves in tokens (0 = unreported).</summary>
    public long ActiveContextWindow { get; init; }
    /// <summary>Slots the engine advertises (0 = fall back to max_inflight_per_engine).</summary>
    public int ActiveSlots { get; init; }
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
