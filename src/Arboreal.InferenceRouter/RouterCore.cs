using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

/// <summary>Router orchestration: threads, per-model priority queues, dispatch loop,
/// engine execution, events, stats. Implements <see cref="IInferenceRouter"/>.</summary>
public sealed partial class RouterCore : IInferenceRouter
{
    private readonly RouterConfig _config;
    private readonly IRequestScheduler _scheduler;
    private readonly EventRing _events;
    private readonly TimeProvider _clock;
    private readonly ExactTicketAdmission? _admission;
    private readonly InProcessHostManager? _hostManager;
    private readonly OwnedPrivatePolicy? _ownedPolicy;
    // An exact caller may share a proven resident. Keep a conflicting ordinary switch
    // from entering between that caller's queue removal and its first transport use.
    private readonly Dictionary<EngineRuntime, (int Named, int Switching)> _profileUsers = [];
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
    private TaskCompletionSource? _shutdownCompletion;
    // Consumers can resume after shutdown cancels and disposes its source. Keep the token
    // for their waits while retaining cancellation and source ownership in the router.
    private readonly CancellationToken _shutdownToken;
    internal Action? BeforeCostCommitForTests;
    internal Action<string, EngineRuntime.SwitchOutcome>? AfterModelReadinessForTests;
    internal Action<RouterTicket>? BeforeAdmissionCommitForTests;
    internal Action<RouterTicket>? BeforeAdmissionPreparationForTests;
    internal Action<bool>? BeforeDispatchParkForTests;
    internal Action<Task>? AfterDispatchParkForTests;
    internal Action? BeforeShutdownCancellationForTests;
    internal Action? BeforeShutdownUnsubscribeForTests;
    internal TimeProvider ResponseTimeoutClockForTests = TimeProvider.System;

    private readonly Func<EngineDefinition, IEngineLifecycleProvider>? _lifecycleFactory;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LocalLifecycleGroup> _localGroups = new(StringComparer.Ordinal);

    public RouterCore(RouterConfig config, IRequestScheduler? scheduler = null, EventRing? events = null)
        : this(config, scheduler, events, TimeProvider.System)
    {
    }

    internal RouterCore(RouterConfig config, IRequestScheduler? scheduler, EventRing? events, TimeProvider clock,
                        Func<EngineDefinition, IEngineLifecycleProvider>? lifecycleFactory = null,
                        ExactTicketAdmission? admission = null, ReloadConfiguration? reload = null, string? nativeResetStartupInstance = null,
                        InProcessHostManager? hostManager = null, OwnedPrivatePolicy? ownedPolicy = null)
    {
        if (config.OwnedPrivatePolicy != (ownedPolicy is not null))
            throw new RouterConfigException("Owned private policy requires a verified external composition.");
        ownedPolicy?.ValidateTopology(config, scheduler is not null || lifecycleFactory is not null ||
            admission is not null || hostManager is not null || nativeResetStartupInstance is not null);
        _ownedPolicy = ownedPolicy;
        config.ValidateLifecycleTopology();
        _shutdownToken = _shutdown.Token;
        _config = config;
        _lifecycleFactory = lifecycleFactory;
        _hostManager = hostManager;
        _nativeResetStartupInstance = nativeResetStartupInstance;
        _clock = clock;
        _wakeOnEngineState = WakeOnEngineState;
        _scheduler = scheduler ?? new PrefixAwareScheduler();
        _events = events ?? new EventRing(maxLiveSubscribers: config.MaxEventSubscribers);
        reload?.ValidateNames();
        if (admission is not null && (hostManager is not null ||
            config.Engines.Values.Any(static definition => definition.LifecycleGroup is not null)))
            throw new RouterConfigException("Local lifecycle groups require ordinary queue admission.");
        var managedDomains = hostManager?.Validate(config);
        var engines = new Dictionary<string, EngineRuntime>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, def) in config.Engines)
            // The dispatch hint closes the one wake gap the event feed leaves: switch outcome events are
            // emitted before the capability transition commits, so a dispatcher woken by them still sees the
            // engine marked as switching. Committing the transition nudges the loop again (§5.5).
            engines[name] = CreateOrdinaryEngine(def, config, reload);
        var groups = new Dictionary<string, LocalLifecycleGroup>(StringComparer.Ordinal);
        foreach (var engine in engines.Values)
        {
            if (engine.Definition.LifecycleGroup is not { } groupName || hostManager?.Owns(engine.Name) == true) continue;
            if (!groups.TryGetValue(groupName, out var group))
                groups.Add(groupName, group = new(groupName, SignalDispatch));
            group.Attach(engine);
            _localGroups[engine.Name] = group;
        }
        foreach (var (id, td) in config.Threads)
            _threads[id] = new RouterThread(id, td.Priority, td.Weight, td.ModelAffinity, ChangeOwnedThreadPriority);
        _generation = new ConfigurationGeneration(1, config, reload, engines);
        _admission = hostManager?.Admission ?? admission;
        _admission?.Attach(managedDomains ?? engines.ToDictionary(static entry => entry.Key,
            static entry => new ExactTicketAdmission.DomainIdentity(entry.Key, Generation: 0), StringComparer.OrdinalIgnoreCase),
            _clock, SignalDispatch, ReportAdmissionCleanupFailure, RejectVerifiedBinding);
        hostManager?.Attach(_generation.Engines);
    }

    private EngineRuntime CreateOrdinaryEngine(EngineDefinition definition, RouterConfig settings, ReloadConfiguration? source)
    {
        var enrollment = _hostManager?.Bind(definition);
        return new(definition, settings, _events, _config.HttpHandlerFactory?.Invoke(definition),
            enrollment is null ? _lifecycleFactory?.Invoke(definition) : null, SignalDispatch, enrollment, _clock, source,
            localManaged: definition.LifecycleGroup is not null, ownedPolicy: _ownedPolicy);
    }

    private bool UsesExactAdmission(EngineRuntime engine) => _admission is not null && (_hostManager?.Owns(engine.Name) ?? true);

    public EventRing Events => !OwnedPolicyEnabled ? _events : throw ScopeRequired();
    public RouterConfig Config => CurrentGeneration.File?.Observe() ?? _config;
    public IReadOnlyDictionary<string, EngineRuntime> Engines => !OwnedPolicyEnabled ? CurrentGeneration.Engines : throw ScopeRequired();
    internal int MaxRequestBodyBytes => _config.MaxRequestBodyBytes;
    internal ThreadPriority DefaultPriority => _config.DefaultPriority;

    public void Start()
    {
        var shutdownToken = _shutdown.Token;
        foreach (var e in CurrentGeneration.Engines.Values)
        {
            e.StartHealthLoop(shutdownToken);
            _events.Append(new RouterEvent { Kind = "engine.registered", Engine = e.Name });
        }
        _events.Subscribe(_wakeOnEngineState);
        _ = Task.Run(DispatchLoop, shutdownToken);
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

    public RouterStats GetStats()
    {
        if (OwnedPolicyEnabled) throw ScopeRequired();
        lock (_gate)
        {
            var engines = CurrentGeneration.Engines.Values.Select(e =>
            {
                var cap = e.Capability;
                return new EngineStats
                {
                    Engine = e.Name,
                    Healthy = cap.Healthy,
                    LoadedModels = cap.LoadedModels,
                    Inflight = e.Inflight,
                    SwitchingToward = cap.SwitchingToward,
                    ActiveProfile = cap.Serving.Profile,
                    ActiveContextWindow = cap.Serving.ContextWindow,
                    ActiveSlots = cap.Serving.TotalSlots,
                };
            }).ToArray();
            var queues = _queues.Values.ToDictionary(q => q.Model, q => q.Depth, StringComparer.Ordinal);
            var threads = _threads.Values.Select(ReadThreadStats).ToArray();
            return new RouterStats(Engines: engines, QueueDepths: queues, Threads: threads)
            {
                FeedSubscriberFaults = _events.SubscriberFaults,
            };
        }
    }

    private static ThreadStats ReadThreadStats(RouterThread thread) => new()
    {
        ThreadId = thread.DisplayId ?? thread.ThreadId,
        Priority = thread.Priority,
        Weight = thread.Weight,
        Enqueued = Volatile.Read(ref thread.Enqueued),
        Completed = Volatile.Read(ref thread.Completed),
    };

    /// <summary>One ticket's state for the admin surface (§6). The identifiers here are <b>whole</b>, not the
    /// reporting-length excerpt the feed carries: an operator who reads a thread id or a model id off this response
    /// follows it up — a query, a retry, a cancel — and a cut id is a value that looks valid and matches nothing.
    /// What this response cannot inherit from a request body is an unbounded <b>sentence</b>: <c>Error</c> is capped
    /// where the ticket became terminal, so it arrives here already at reporting length (§10).</summary>
    public TicketSnapshot? TryGetTicket(string ticketId) => !OwnedPolicyEnabled ? TryGetTicket(ticketId, null) : null;

    internal TicketSnapshot? TryGetTicket(string ticketId, PolicyGroupKey? group)
    {
        lock (_ticketGate)
        {
            if (!_tickets.TryGetValue(ticketId, out var t) || t.OwnedPolicy?.Key != group) return null;
            var state = t.State;
            var wait = Volatile.Read(ref t.DispatchWait);
            var upstream = state is TicketState.Completed or TicketState.Cancelled or TicketState.Failed
                ? t.StreamObservation : null;
            var effective = t.OwnedPolicy is { } admitted ? _ownedPolicy?.Store.ReadEffectivePolicy(admitted.Key) : null;
            return new TicketSnapshot
            {
                Id = t.Id,
                ThreadId = t.ThreadId,
                Model = t.Model,
                QueuedAt = t.QueuedAt,
                State = state.ToString(),
                Engine = t.Engine,
                ErrorCode = t.ErrorCode,
                Error = t.Error,
                QueueWaitReason = wait?.Reason,
                QueueWaitScope = wait?.Scope,
                QueueDispatch = wait,
                UpstreamOutcome = upstream?.OutcomeName,
                UpstreamEndReason = upstream?.EndReasonName,
                AttemptPolicyRevision = t.OwnedPolicy?.Group.Revision,
                CurrentPolicyRevision = effective?.Revision ??
                    (t.OwnedPolicy is { } policy && _ownedPolicy?.IsCurrent(policy) == true ? policy.Group.Revision : null),
                Sensitive = t.OwnedPolicy?.Group.Sensitive,
                CurrentSensitivity = effective?.Sensitive,
                PolicyAdmissionState = effective?.AdmissionState,
                PrivateAttemptMayHaveSent = Volatile.Read(ref t.PrivateAttemptDisposition) switch
                {
                    1 => false,
                    2 => true,
                    _ => null,
                },
                DestinationTrust = t.OwnedPolicy is not null && t.Engine is not null ? "private" : null,
            };
        }
    }

    public IAsyncEnumerable<RouterEvent> SubscribeEvents(RouterEventFilter? filter = null, CancellationToken ct = default)
        => !OwnedPolicyEnabled ? new EventEnumerable(_events, filter ?? new RouterEventFilter(), ct, _shutdown.Token) : throw ScopeRequired();

    private int OwnedInflight(PolicyGroupKey key, string engine)
    {
        lock (_ticketGate)
            return _tickets.Values.Count(t => t.OwnedPolicy?.Key == key && t.State == TicketState.Executing && t.Engine == engine);
    }

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

    private readonly DispatchWake _dispatchWake = new();

    private void SignalDispatch() => _dispatchWake.Signal();

    private enum DispatchResult { Dispatched, StateChanged, Parked }

    internal async Task DispatchLoop()
    {
        while (!_shutdownToken.IsCancellationRequested)
        {
            var revision = _dispatchWake.Revision;
            if (DispatchRound() == DispatchResult.Dispatched) continue;

            BeforeDispatchParkForTests?.Invoke(false);
            var wake = _dispatchWake.Arm();
            BeforeDispatchParkForTests?.Invoke(true);
            if (_dispatchWake.Revision != revision) continue;
            AfterDispatchParkForTests?.Invoke(wake);
            try
            {
                await wake.WaitAsync(_shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static bool HasThreadCapacity(RouterThread thread) => thread.Inflight < thread.MaxInflight;

    private readonly record struct SwitchComparison(
        EngineRuntime Engine, EngineRuntime.DispatchObservation Observation, SwitchSampleKey Incumbent,
        long Timestamp, DateTimeOffset UtcNow, long PendingPriorityRevision);

    private SwitchComparison? ReadSwitchComparison(ConfigurationGeneration generation, long pendingPriorityRevision)
    {
        if (_scheduler is not PrefixAwareScheduler || !generation.Settings.AutoSwitch || generation.Engines.Count != 1)
            return null;

        var engine = generation.Engines.Values.Single();
        var observation = engine.ReadDispatchObservation();
        if (observation.Inflight != 0 || !observation.HasCapacity ||
            engine.ReadVerifiedSwitchIncumbent(observation.Capability) is not { } incumbent)
            return null;

        return new SwitchComparison(engine, observation, incumbent, _clock.GetTimestamp(), _clock.GetUtcNow(), pendingPriorityRevision);
    }

    private readonly record struct QueueRound(ModelQueue Queue, ModelQueueRound Snapshot);
    private readonly record struct Selection(int QueueIndex, QueuedRequest Request, SchedulerDecision Decision,
                                             long? PendingPriorityRevision = null);
    private readonly record struct Blocker(string Reason, string? Scope = null);
    private readonly record struct RouteProbe(ServingProfile? Profile, LocalLifecycleGroup? Group, Blocker? Blocker,
                                              bool Drain = false);
    private readonly record struct PredecessorRoute(EngineRuntime.DispatchObservation Observation,
        LocalLifecycleGroup? Group, long GroupRevision, bool HasNamedUse, int InferenceUses)
    {
        internal bool IsCurrent(EngineRuntime engine) => engine.IsDispatchObservationCurrent(Observation) &&
            GroupRevision == (Group?.Revision ?? 0) && HasNamedUse == engine.HasNamedUse && InferenceUses == engine.InferenceUses;
    }
    private enum TakeResult { Taken, QueueChanged, RouteChanged, Ineligible, Terminal, Preparing, PreparationExhausted }
    private readonly record struct AdmissionTake(TakeResult Result, ExactTicketAdmission.Invocation? Invocation = null,
        string? TerminalCode = null);
    private readonly record struct PreparationOffer(Selection Selection, EngineRuntime Engine, ExactTicketAdmission.Claim Claim);
    private readonly record struct ManagedOffer(Selection Selection, EngineRuntime Engine, VerifiedDispatchTarget Recipe);

    // Existing fixtures step one admission transition without starting the background dispatcher.
    internal bool TryDispatchOnce() => DispatchRound(stopAfterTransition: true) != DispatchResult.Parked;
    internal bool TryDispatchRound() => DispatchRound() == DispatchResult.Dispatched;

    private DispatchResult DispatchRound(bool stopAfterTransition = false)
    {
        _admission?.ResumePendingOpeningOwnerPreparations();
        QueueRound[] round;
        ConfigurationGeneration generation;
        long pendingPriorityRevision;
        lock (_gate)
        {
            if (_closing) return DispatchResult.Parked;
            generation = CurrentGeneration;
            pendingPriorityRevision = _pendingPriorityRevision;
            round = [.. _queues.Values
                .Select(static queue => new QueueRound(queue, queue.CaptureRound()))
                .OrderBy(static entry => entry.Snapshot.Depth)
                .ThenBy(static entry => entry.Queue.CreationOrdinal)];
        }

        var result = DispatchResult.Parked;
        var comparison = ReadSwitchComparison(generation, pendingPriorityRevision);
        for (var index = 0; index < round.Length; index++)
        {
            var through = index;
            foreach (var request in round[index].Snapshot.Requests)
            {
                if (_shutdownToken.IsCancellationRequested) return result;
                if (request.Ticket.IsTerminal) continue;
                if (request.Ticket.OwnedPolicy is { } policy && _ownedPolicy?.IsCurrent(policy) != true)
                {
                    if (ClaimQueuedEnd(request.Ticket, "policy_group_unavailable") == QueueEndClaim.Removed)
                        SettleQueueEnd(request.Ticket, "policy_group_unavailable", null);
                    continue;
                }
                if (ReadQueueEnd(request) is not null)
                {
                    MaybeFailDeadline(request);
                    continue;
                }
                var evaluation = new DispatchEvaluation(request, generation, _dispatchWake.Revision);
                if (Volatile.Read(ref request.Ticket.PrivatePreparation) is { } privatePreparation)
                {
                    if (!ReferenceEquals(generation.Engines.GetValueOrDefault(privatePreparation.Engine.Name), privatePreparation.Engine))
                    {
                        if (ClaimQueuedEnd(request.Ticket, "policy_route_denied") == QueueEndClaim.Removed)
                            SettleQueueEnd(request.Ticket, "policy_route_denied", null);
                        continue;
                    }
                    if (!privatePreparation.Ready)
                    {
                        var wait = new Blocker("route_preparing", privatePreparation.Engine.Name);
                        evaluation.Record(privatePreparation.Engine.Name, "blocked", wait);
                        PublishDispatchWait(round[index].Queue, request, wait, evaluation);
                        continue;
                    }
                }
                if (!evaluation.HasThreadCapacity)
                {
                    // This is the same request-scoped view used by ranking, including route policy.
                    // A full thread never attempts to acquire an engine or group lease.
                    var infos = BuildEngineInfos(request, generation, comparison, includeAffinity: false);
                    evaluation.Routes = infos;
                    evaluation.RecordUnevaluated(infos.Where(static info => info.CanServeModel).Select(static info => info.Name));
                    PublishDispatchWait(round[index].Queue, request,
                        new("thread_concurrency_limit", request.Ticket.ThreadId), evaluation);
                    continue;
                }

                var transition = DispatchCandidate(round, index, request, generation, comparison, evaluation, ref through);
                if (transition == DispatchResult.Dispatched)
                {
                    result = transition;
                    comparison = null;
                }
                if (stopAfterTransition && transition != DispatchResult.Parked) return transition;
                if (_scheduler is not PrefixAwareScheduler) break;
                // The measured switch comparison already visited the rest of its single-resource cohort.
                if (through != index) break;
            }
            index = through;
        }
        return result;
    }

    private DispatchResult DispatchCandidate(QueueRound[] round, int index, QueuedRequest request,
                                             ConfigurationGeneration generation, SwitchComparison? comparison, DispatchEvaluation evaluation, ref int through)
    {
        var infos = BuildEngineInfos(request, generation, comparison);
        evaluation.Routes = infos;
        var routes = _scheduler is PrefixAwareScheduler ? PrefixAwareScheduler.RankRoutes(request, infos, generation.Settings) : null;
        var custom = routes is null
            ? _scheduler.SelectNext(request, new SchedulerView(infos, _clock.GetUtcNow()), generation.Settings) : null;
        if (custom is { Engine: null })
        {
            evaluation.RecordUnevaluated(infos.Where(static info => info.CanServeModel).Select(static info => info.Name));
            if (ReadQueueEnd(request) is not null) MaybeFailDeadline(request);
            else PublishDispatchWait(round[index].Queue, request, DescribeUnavailable(infos, request), evaluation);
            return DispatchResult.Parked;
        }

        Blocker? blocker = null;
        var routeChanged = false;
        var ownedPreparationExhausted = false;
        var routePreparing = false;
        List<PreparationOffer>? preparations = null;
        PreparationOffer? blockedPreparation = null;
        List<ManagedOffer>? activations = null;
        (Selection Selection, EngineRuntime Engine, RouteProbe Probe)? drain = null;
        // Forwarding-only catalog entries preserve the existing typed switch refusal when no
        // candidate can serve or activate. They must not displace an actual route awaiting capacity.
        var refuseCold = _admission is not null && _hostManager is null || routes is null || routes.TrueForAll(route => route.Target.SwitchRequired &&
            !HasLifecycleRoute(request, generation.Engines[route.Engine.Name]));
        var routeCount = routes?.Count ?? 1;
        for (var routeIndex = 0; routeIndex < routeCount; routeIndex++)
        {
            var decision = routes is null ? custom! : routes[routeIndex].Decision;
            var selection = new Selection(index, request, decision);
            var compared = comparison is not null && decision.SwitchRequired;
            if (comparison is { } cost && compared)
                selection = OrderEligibleSwitches(round, ref through, selection, cost, generation) with
                {
                    PendingPriorityRevision = cost.PendingPriorityRevision,
                };

            var candidate = selection.Request;
            var engine = generation.Engines[selection.Decision.Engine!];
            if (!refuseCold && selection.Decision.SwitchRequired && !HasLifecycleRoute(candidate, engine))
            {
                var noLifecycle = new Blocker("engine_lifecycle_unavailable", engine.Name);
                blocker ??= noLifecycle;
                evaluation.Record(engine.Name, "blocked", noLifecycle);
                continue;
            }
            var observed = comparison?.Observation ?? FindDispatchObservation(infos, engine.Name);
            RouteProbe probe;
            lock (_gate)
                probe = ProbeRoute(candidate, engine, selection.Decision, observed, generation);
            if (probe.Blocker is { } unavailable)
            {
                blocker ??= unavailable;
                evaluation.Record(engine.Name, "blocked", unavailable);
                if (probe.Drain) drain ??= (selection, engine, probe);
                continue;
            }

            if (selection.Decision.SwitchRequired && _hostManager?.Owns(engine.Name) == true)
            {
                var model = candidate.Ticket.Model;
                var tier = probe.Profile?.Name ?? engine.DefaultServingTier(model);
                if (tier is not null && _hostManager.Recipe(engine.Name, model, tier) is { } recipe)
                    (activations ??= []).Add(new(selection, engine, recipe));
                var waiting = new Blocker("lifecycle_group_waiting", _hostManager.Scope(engine.Name));
                blocker ??= waiting;
                evaluation.Record(engine.Name, tier is null ? "blocked" : "activation_required", waiting);
                continue;
            }

            var requiredWindow = EngineRuntime.RequiredWindow(probe.Profile, candidate.ExplicitContextMinimum);
            var needsCurrentObservation = !UsesExactAdmission(engine) && routes is not null || comparison is not null || requiredWindow > 0 ||
                selection.Decision.TargetProfile is not null ||
                (candidate.RequiredContextTokens > 0 && engine.ProfilesForModel(candidate.Ticket.Model).Count > 0);
            EngineRuntime.DispatchObservation? expected = needsCurrentObservation ? observed : null;
            var take = TryAdmit(round, index, selection, engine, expected, requiredWindow, compared, generation,
                routes is null ? ReadRankedResident(infos, engine.Name) : routes[routeIndex].Engine.ManagedResident,
                ref preparations, ref blockedPreparation);
            if (take.Result == TakeResult.Taken)
            {
                var proof = selection.Decision.SwitchRequired ? null : observed.Capability.SwitchProof;
                StartSelected(candidate, engine, generation.Settings, selection.Decision.SwitchRequired, probe.Profile, proof,
                    take.Invocation, blocker is { } skipped ? FormatBlocker(skipped) : null);
                return DispatchResult.Dispatched;
            }
            if (take.Result == TakeResult.Terminal)
            {
                SettleQueueEnd(candidate.Ticket, take.TerminalCode!, candidate.Request.Options?.MaxQueueTime);
                return DispatchResult.Parked;
            }
            if (take.Result == TakeResult.QueueChanged) return DispatchResult.StateChanged;
            if (take.Result == TakeResult.Preparing)
            {
                routePreparing = true;
                var preparing = new Blocker("route_preparing", _hostManager?.Scope(engine.Name) ?? engine.Name);
                blocker ??= preparing;
                evaluation.Record(engine.Name, "blocked", preparing);
                continue;
            }
            if (take.Result == TakeResult.PreparationExhausted)
            {
                ownedPreparationExhausted = true;
                continue;
            }
            routeChanged |= take.Result == TakeResult.RouteChanged;
            var wait = new Blocker(take.Result == TakeResult.RouteChanged ? "route_changed" : "route_admission_wait", engine.Name);
            blocker ??= wait;
            evaluation.Record(engine.Name, take.Result == TakeResult.RouteChanged ? "not_evaluated"
                : selection.Decision.SwitchRequired ? "activation_required" : "observed_ready", wait);
        }
        evaluation.RecordUnevaluated(infos.Where(info => info.CanServeModel && (routes is null
            ? info.Name != custom?.Engine : !routes.Exists(route => ReferenceEquals(route.Engine, info)))).Select(static info => info.Name));

        if (ownedPreparationExhausted && !routePreparing && !routeChanged &&
            ClaimQueuedEnd(request.Ticket, "policy_route_denied") == QueueEndClaim.Removed)
        {
            SettleQueueEnd(request.Ticket, "policy_route_denied", null);
            return DispatchResult.Parked;
        }

        var started = preparations?.Exists(offer => StartExactPreparation(round[offer.Selection.QueueIndex].Queue, offer, generation)) == true;
        if (!started)
            started = activations?.Exists(offer => StartManagedActivation(round[offer.Selection.QueueIndex].Queue, offer, generation)) == true;
        if (!started && blockedPreparation is { } blocked)
            started = StartExactPreparation(round[blocked.Selection.QueueIndex].Queue, blocked, generation);
        if (!started && drain is { } pending &&
            TryStartLocalDrain(round, index, pending.Selection, pending.Engine, pending.Probe, generation, evaluation) is { } owned)
            blocker = new("lifecycle_group_draining", owned.Name);
        if (ReadQueueEnd(request) is not null) MaybeFailDeadline(request);
        else if (!routeChanged && through == index)
            PublishDispatchWait(round[index].Queue, request, blocker ?? DescribeUnavailable(infos, request), evaluation);
        return routeChanged ? DispatchResult.StateChanged : DispatchResult.Parked;
    }

    private AdmissionTake TryAdmit(QueueRound[] round, int index, Selection selection, EngineRuntime engine,
                                   EngineRuntime.DispatchObservation? expected, long requiredWindow, bool compared,
                                   ConfigurationGeneration generation, VerifiedDispatchResident? rankedResident,
                                   ref List<PreparationOffer>? preparations,
                                   ref PreparationOffer? blockedPreparation)
    {
        var candidate = selection.Request;
        if (!OwnedPreparedFor(candidate.Ticket, engine))
            return PrepareOwnedDispatch(round[selection.QueueIndex].Queue, candidate, engine, generation);
        if (!UsesExactAdmission(engine))
        {
            if (compared) BeforeCostCommitForTests?.Invoke();
            BeforeAdmissionCommitForTests?.Invoke(candidate.Ticket);
            if (_hostManager is { } manager)
            {
                lock (manager.Admission.Gate)
                {
                    if (manager.Admission.HasOutstanding(candidate.Ticket)) return new(TakeResult.Ineligible);
                    return TryTake(round, index, selection, engine, expected, requiredWindow, claim: null, generation);
                }
            }
            return TryTake(round, index, selection, engine, expected, requiredWindow, claim: null, generation);
        }

        var admission = _admission!;

        var route = CaptureAdmissionRoute(candidate, engine, selection.Decision.TargetProfile);
        if (admission.Capture(candidate, route, selection.Decision.SwitchRequired, candidate.ExplicitContextMinimum) is not { } claim)
            return new(TakeResult.Ineligible);
        if (_hostManager?.Owns(engine.Name) == true && !ReferenceEquals(rankedResident, claim.Resident))
            return new(TakeResult.RouteChanged);
        if (compared) BeforeCostCommitForTests?.Invoke();
        if (ReadQueueEnd(candidate) is not null)
        {
            MaybeFailDeadline(candidate);
            return new(TakeResult.Ineligible);
        }

        lock (admission.Gate)
        {
            var observed = expected ?? engine.ReadDispatchObservation();
            if (claim.Resident is not null &&
                (!admission.IsOpen(claim) || !VerifiedCapacityFits(claim, observed.Capability, requiredWindow) ||
                 !admission.CanTake(claim, engine.ReadDispatchObservation())))
                return new(TakeResult.Ineligible);
            if (!admission.IsPrepared(claim) || !admission.IsOpen(claim))
            {
                var canStart = admission.CanStartPreparation(claim);
                if (canStart && generation.Engines.Count > 1 && _scheduler is PrefixAwareScheduler)
                {
                    var queue = round[selection.QueueIndex].Queue;
                    lock (_gate)
                    lock (queue.Gate)
                        canStart = IsPreparationCandidate(queue, selection, engine, generation);
                }
                var offer = new PreparationOffer(selection, engine, claim);
                if (canStart) (preparations ??= []).Add(offer);
                else if (_hostManager is null) blockedPreparation ??= offer;
                return new(TakeResult.Preparing);
            }
        }

        BeforeAdmissionCommitForTests?.Invoke(candidate.Ticket);
        lock (admission.Gate)
            return TryTake(round, index, selection, engine, expected, requiredWindow, claim, generation);

    }

    private bool StartExactPreparation(ModelQueue queue, PreparationOffer offer, ConfigurationGeneration generation)
    {
        var admission = _admission!;
        var candidate = offer.Selection.Request;
        BeforeAdmissionPreparationForTests?.Invoke(candidate.Ticket);
        Action? work;
        lock (admission.Gate)
        lock (_gate)
        lock (queue.Gate)
        {
            work = IsPreparationCandidate(queue, offer.Selection, offer.Engine, generation) ? admission.ReservePreparationWork(offer.Claim) : null;
        }
        if (work is null) return false;
        work();
        return true;
    }

    private bool IsPreparationCandidate(ModelQueue queue, Selection selection, EngineRuntime engine, ConfigurationGeneration generation)
    {
        var candidate = selection.Request;
        return IsCurrentQueue(queue, candidate, generation) && IsCurrentPriorityComparison(selection) &&
            IsPendingAndThreadAdmissible(candidate) && queue.FindExactTakeIndexUnderGate(candidate, earlier =>
                _scheduler is PrefixAwareScheduler ? ExactPredecessorCompetes(earlier, candidate, engine, generation)
                    : IsPendingAndThreadAdmissible(earlier)) >= 0;
    }

    private bool StartManagedActivation(ModelQueue queue, ManagedOffer offer, ConfigurationGeneration generation)
    {
        var manager = _hostManager!;
        var candidate = offer.Selection.Request;
        InProcessHostManager.Transition? accepted;
        lock (manager.Admission.Gate)
        lock (_gate)
        {
            if (!IsCurrentQueue(queue, candidate, generation) || !IsCurrentPriorityComparison(offer.Selection) ||
                !IsPendingAndThreadAdmissible(candidate) || candidate.Ticket.ExecCts is not { } execution) return false;
            if (_scheduler is PrefixAwareScheduler && PrefixAwareScheduler.DescribeTarget(candidate,
                ReadRouteFit(candidate, offer.Engine, offer.Engine.ReadDispatchObservation()), generation.Settings) !=
                new PrefixAwareScheduler.Target(SwitchRequired: true, offer.Recipe.Tier)) return false;
            lock (queue.Gate)
            {
                if (queue.FindExactTakeIndexUnderGate(candidate,
                        earlier => ExactPredecessorCompetes(earlier, candidate, offer.Engine, generation)) < 0) return false;
                accepted = manager.TryAccept(offer.Engine.Name, candidate.Ticket.Model, offer.Recipe.Tier, candidate.Ticket,
                    execution.Token, offer.Recipe);
            }
        }
        if (accepted is null) return false;
        _ = CompleteManagedActivationAsync(manager, accepted, candidate);
        return true;
    }

    private async Task CompleteManagedActivationAsync(InProcessHostManager manager, InProcessHostManager.Transition accepted,
        QueuedRequest candidate)
    {
        // Only the admitted operation may mutate. Keep the priority queue entry until an
        // exact verified send can remove it, or the same admission settles its failure.
        if (await ExecuteManagedActivationAsync(manager, accepted).ConfigureAwait(false)) return;
        if (ReadQueueEnd(candidate) is not null)
        {
            MaybeFailDeadline(candidate);
            return;
        }

        if (ClaimQueuedEnd(candidate.Ticket, "switch_failed") != QueueEndClaim.Removed) return;
        SettleQueueEnd(candidate.Ticket, "switch_failed", null, accepted.Cut?.DrainUnproven == true
            ? "The exact incumbent has no current retirement proof. Retry after its physical exchanges retire."
            : null);
        SignalDispatch();
    }

    private static async Task<bool> ExecuteManagedActivationAsync(InProcessHostManager manager, InProcessHostManager.Transition accepted)
    {
        try { return (await manager.ExecuteAsync(accepted).ConfigureAwait(false)).IsReady; }
        catch (Exception) { return false; }
    }

    private AdmissionTake TryTake(QueueRound[] round, int prefix, Selection selection, EngineRuntime engine,
                                  EngineRuntime.DispatchObservation? expected, long requiredWindow,
                                  ExactTicketAdmission.Claim? claim, ConfigurationGeneration generation)
    {
        var candidate = selection.Request;
        var queue = round[selection.QueueIndex].Queue;
        lock (_gate)
        {
            if (_closing || _shutdownToken.IsCancellationRequested) return new(TakeResult.Ineligible);
            if (!IsCurrentQueue(queue, candidate, generation) || !IsCurrentPriorityComparison(selection))
                return new(TakeResult.QueueChanged);
            if (!AllowsOwnedRoute(candidate.Ticket, engine.Name) || !OwnedPreparedFor(candidate.Ticket, engine))
                return new(TakeResult.Ineligible);
            var groupRevision = _localGroups.GetValueOrDefault(engine.Name)?.Revision;
            var resourceAware = _scheduler is PrefixAwareScheduler && claim is null;
            Dictionary<EngineRuntime, PredecessorRoute>? predecessorRoutes = null;
            bool Competes(QueuedRequest earlier) => _scheduler is not PrefixAwareScheduler
                ? IsPendingAndThreadAdmissible(earlier)
                : claim is null ? CanCompeteFor(earlier, candidate, engine, generation, ref predecessorRoutes)
                : ExactPredecessorCompetes(earlier, candidate, engine, generation);

            // The outer order is captured. Order within each still-live queue is current, including
            // a newly published, higher-ranked request. Never acquire two model-queue gates together.
            if ((resourceAware || claim is not null && _hostManager is not null) && HasEarlierQueueCompetitor(round, prefix, Competes))
                return new(TakeResult.Ineligible);
            lock (queue.Gate)
            {
                if (!queue.Contains(candidate)) return new(TakeResult.QueueChanged);
                var terminal = ReadQueueEnd(candidate);
                var index = queue.FindExactTakeIndexUnderGate(candidate, Competes);
                if (index < 0) return new(TakeResult.Ineligible);
                if (terminal is not null)
                {
                    RemoveDispatchCandidate(queue, index, candidate);
                    return new(TakeResult.Terminal, TerminalCode: terminal);
                }
                if (!HasThreadCapacity(candidate.Thread)) return new(TakeResult.Ineligible);
                var observation = engine.ReadDispatchObservation();
                if (claim is { } stamped && (!observation.HasCapacity || !CanTakePrepared(stamped, engine, observation, requiredWindow)))
                    return new(TakeResult.Ineligible);
                if (expected is { } original && !engine.IsDispatchObservationCurrent(original))
                    return new(TakeResult.RouteChanged);
                var probe = ProbeRoute(candidate, engine, selection.Decision, observation, generation);
                if (probe.Blocker is not null) return new(TakeResult.Ineligible);

                var takenAt = _clock.GetTimestamp();
                if (resourceAware && (!engine.IsDispatchObservationCurrent(observation) || !PredecessorRoutesAreCurrent(predecessorRoutes)))
                    return new(TakeResult.RouteChanged);

                var named = candidate.Request.Options?.Profile is not null;
                if (named && (expected is not { } current || !engine.TryReserveNamedUse(current)))
                    return new(TakeResult.RouteChanged);

                if (!ReferenceEquals(candidate.Ticket.LocalDrain, probe.Group)) RetireLocalDrain(candidate.Ticket);
                if (probe.Group is { } local)
                {
                    var lease = local.TryReserve(candidate, engine, probe.Profile, selection.Decision.SwitchRequired,
                        new OwnerSwitchBudget(_clock, takenAt, generation.Settings.Scheduler.SwitchWaitTimeoutMs), groupRevision!.Value);
                    if (lease is null)
                    {
                        if (named) engine.ReleaseNamedUse();
                        return new(local.Revision == groupRevision ? TakeResult.Ineligible : TakeResult.RouteChanged);
                    }
                    candidate.LocalLease = lease;
                }
                RemoveDispatchCandidate(queue, index, candidate);

                var invocation = claim is { } exact ? _admission!.Authorize(exact, engine) : null;
                candidate.ReservedSwitch = selection.Decision.SwitchRequired;
                if (named || candidate.ReservedSwitch)
                {
                    var uses = _profileUsers.GetValueOrDefault(engine);
                    _profileUsers[engine] = (uses.Named + (named ? 1 : 0), uses.Switching + (candidate.ReservedSwitch ? 1 : 0));
                }
                candidate.Thread.IncrementInflight();
                engine.BeginRequest(candidate.PromptChars);
                return new(TakeResult.Taken, invocation);
            }
        }
    }

    private bool CanTakePrepared(ExactTicketAdmission.Claim claim, EngineRuntime engine,
                                 EngineRuntime.DispatchObservation observation, long requiredWindow)
    {
        var admission = _admission!;
        var candidate = claim.Request;
        if (!admission.IsOpen(claim) || !admission.IsPrepared(claim) || !observation.Capability.Healthy ||
            observation.Capability.SwitchingToward is not null ||
            !engine.CanServeModel(candidate.Ticket.Model, observation.Capability)) return false;

        if (candidate.Request.Options?.Profile is { } requested &&
            (engine.GetProfile(candidate.Ticket.Model, requested) is not { } tier ||
             !engine.MatchesNamedResident(candidate.Ticket.Model, tier, observation.Capability) ||
             claim.Resident is { } resident && (resident.Target.Tier != tier.Name ||
                 resident.MeasuredWindow != tier.MaxContext || resident.TotalSlots != tier.MaxSlots))) return false;
        return AdmissionWindowFits(admission, claim, observation.Capability, requiredWindow) && admission.CanTake(claim, observation);
    }

    private bool IsCurrentQueue(ModelQueue queue, QueuedRequest candidate, ConfigurationGeneration generation) =>
        !_closing && !_shutdownToken.IsCancellationRequested && ReferenceEquals(CurrentGeneration, generation) &&
        ReferenceEquals(_queues.GetValueOrDefault(candidate.Ticket.Model), queue) &&
        !candidate.Ticket.PendingEnqueuePublication && !candidate.Ticket.IsTerminal;

    // Ordinary takes read the current queue order. The measured cohort can overtake earlier
    // queues, so its captured priority membership needs its own validation.
    private bool IsCurrentPriorityComparison(Selection selection) =>
        selection.PendingPriorityRevision is not { } revision || revision == _pendingPriorityRevision;

    private bool HasEarlierQueueCompetitor(QueueRound[] round, int prefix, Predicate<QueuedRequest> competes)
    {
        for (var i = 0; i < prefix; i++)
        {
            var queue = round[i].Queue;
            if (ReferenceEquals(_queues.GetValueOrDefault(queue.Model), queue) && queue.HasCompetitor(competes)) return true;
        }
        return false;
    }

    private bool IsPendingAndThreadAdmissible(QueuedRequest request) =>
        !request.Ticket.PendingEnqueuePublication && !request.Ticket.IsTerminal &&
        HasThreadCapacity(request.Thread) && ReadQueueEnd(request) is null;

    // The caller holds the exact admission, router and queue gates. An active preparation
    // excludes other domains for this ticket until retirement settles.
    private bool ExactPredecessorCompetes(QueuedRequest earlier, QueuedRequest candidate, EngineRuntime selected,
        ConfigurationGeneration generation)
    {
        if (!IsPendingAndThreadAdmissible(earlier)) return false;
        var admission = _admission!;
        var sameThread = ReferenceEquals(candidate.Thread, earlier.Thread) && earlier.Thread.Inflight + 1 >= earlier.Thread.MaxInflight;
        foreach (var engine in generation.Engines.Values)
        {
            if (!AllowsOwnedRoute(earlier.Ticket, engine.Name) || (!sameThread && !admission.SharesDomain(selected.Name, engine.Name)) ||
                admission.IsOtherDomain(earlier.Ticket, engine.Name)) continue;
            var observation = engine.ReadDispatchObservation();
            if (!observation.HasCapacity) continue;
            var fit = ReadRouteFit(earlier, engine, observation);
            if (PrefixAwareScheduler.DescribeTarget(earlier, fit, generation.Settings) is not { } target) continue;
            if (!UsesExactAdmission(engine))
            {
                if ((!target.SwitchRequired || HasLifecycleRoute(earlier, engine)) &&
                    ProbeRoute(earlier, engine, target, observation, generation).Blocker is null) return true;
                continue;
            }
            if (ExactRouteCanCompete(earlier, engine, target, observation)) return true;
        }
        return false;
    }

    private bool CanCompeteFor(QueuedRequest earlier, QueuedRequest candidate, EngineRuntime selected,
                               ConfigurationGeneration generation, ref Dictionary<EngineRuntime, PredecessorRoute>? predecessorRoutes,
                               bool includeDrain = false)
    {
        if (!IsPendingAndThreadAdmissible(earlier)) return false;
        var group = _localGroups.GetValueOrDefault(selected.Name);
        var sameThread = ReferenceEquals(candidate.Thread, earlier.Thread) && earlier.Thread.Inflight + 1 >= earlier.Thread.MaxInflight;
        foreach (var engine in generation.Engines.Values)
        {
            if (!AllowsOwnedRoute(earlier.Ticket, engine.Name) || !OwnedPreparedFor(earlier.Ticket, engine)) continue;
            var sameEngine = ReferenceEquals(engine, selected);
            var routeGroup = _localGroups.GetValueOrDefault(engine.Name);
            if (!sameEngine && !sameThread && (group is null || !ReferenceEquals(group, routeGroup))) continue;
            var observed = engine.ReadDispatchObservation();
            if (!sameEngine)
            {
                // Record only routes that can contend for this lifecycle group or the last shared thread slot.
                // The first read fences all subsequent predecessor checks of that route in this take.
                predecessorRoutes ??= [];
                predecessorRoutes.TryAdd(engine, new(observed, routeGroup, routeGroup?.Revision ?? 0,
                    engine.HasNamedUse, engine.InferenceUses));
            }
            var fit = ReadRouteFit(earlier, engine, observed);
            if (PrefixAwareScheduler.DescribeTarget(earlier, fit, generation.Settings) is not { } target) continue;
            if (target.SwitchRequired && !HasLifecycleRoute(earlier, engine)) continue;
            if (_hostManager is { } manager)
            {
                if (!manager.Owns(engine.Name) && manager.Admission.HasOutstanding(earlier.Ticket)) continue;
                if (manager.Owns(engine.Name) && !ExactRouteCanCompete(earlier, engine, target, observed)) continue;
            }
            var probe = ProbeRoute(earlier, engine, target, observed, generation);
            if (probe.Blocker is null || includeDrain && probe.Drain) return true;
        }
        return false;
    }

    private bool ExactRouteCanCompete(QueuedRequest earlier, EngineRuntime engine, PrefixAwareScheduler.Target target,
        EngineRuntime.DispatchObservation observation)
    {
        var admission = _admission!;
        var route = CaptureAdmissionRoute(earlier, engine, target.Profile);
        var window = EngineRuntime.RequiredWindow(engine.GetProfile(earlier.Ticket.Model, target.Profile), earlier.ExplicitContextMinimum);
        return admission.Capture(earlier, route, target.SwitchRequired, earlier.ExplicitContextMinimum) is { } claim &&
            admission.IsOpen(claim) && AdmissionWindowFits(admission, claim, observation.Capability, window) &&
            admission.CanTake(claim, observation);
    }

    private ExactTicketAdmission.Route CaptureAdmissionRoute(QueuedRequest request, EngineRuntime engine, string? selectedTier)
    {
        var model = request.Ticket.Model;
        var tier = engine.GetProfile(model, selectedTier)?.Name ?? selectedTier;
        var recipe = tier is not null ? _hostManager?.Recipe(engine.Name, model, tier) : null;
        return new(engine.Name, model, tier, recipe);
    }

    private static bool PredecessorRoutesAreCurrent(Dictionary<EngineRuntime, PredecessorRoute>? routes)
    {
        if (routes is null) return true;
        foreach (var (engine, route) in routes)
            if (!route.IsCurrent(engine)) return false;
        return true;
    }

    internal LocalLifecycleGroup? LocalGroup(string engine) => _localGroups.GetValueOrDefault(engine);

    private bool HasLifecycleRoute(QueuedRequest request, EngineRuntime engine) =>
        _localGroups.ContainsKey(engine.Name) || engine.CanSwitchTo(request.Ticket.Model);

    private LocalLifecycleGroup? TryStartLocalDrain(QueueRound[] round, int prefix, Selection selection, EngineRuntime engine,
                                                   RouteProbe probe, ConfigurationGeneration generation, DispatchEvaluation evaluation)
    {
        var candidate = selection.Request;
        var queue = round[selection.QueueIndex].Queue;
        lock (_gate)
        {
            if (probe.Group is not { } group || !IsCurrentQueue(queue, candidate, generation) || !IsCurrentPriorityComparison(selection) ||
                !IsPendingAndThreadAdmissible(candidate)) return null;
            if (candidate.Ticket.LocalDrain is { } existing) return existing;
            var revision = group.Revision;
            Dictionary<EngineRuntime, PredecessorRoute>? predecessorRoutes = null;
            bool Competes(QueuedRequest earlier) => CanCompeteFor(earlier, candidate, engine, generation,
                ref predecessorRoutes, includeDrain: true);
            if (HasEarlierQueueCompetitor(round, prefix, Competes)) return null;
            lock (queue.Gate)
            {
                if (queue.FindExactTakeIndexUnderGate(candidate, Competes) >= 0 &&
                    ProbeRoute(candidate, engine, selection.Decision, engine.ReadDispatchObservation(), generation).Drain &&
                    PredecessorRoutesAreCurrent(predecessorRoutes) &&
                    group.TryStartDrain(candidate, engine, probe.Profile, selection.Decision.SwitchRequired, revision))
                {
                    evaluation.AcceptDrain(group, revision + 1, engine.Name);
                    return candidate.Ticket.LocalDrain = group;
                }
            }
            return null;
        }
    }

    private void RemoveDispatchCandidate(ModelQueue queue, int index, QueuedRequest request)
    {
        queue.RemoveExactTakeUnderGate(index);
        ClearQueuedScheduling(request.Ticket);
        RetireQueueIfEmpty(queue);
    }

    private static void ClearQueuedScheduling(RouterTicket ticket)
    {
        Volatile.Write(ref ticket.DispatchWait, null);
        RetireLocalDrain(ticket);
    }

    private static void RetireLocalDrain(RouterTicket ticket)
    {
        var group = ticket.LocalDrain;
        ticket.LocalDrain = null;
        group?.RetireDrain(ticket);
    }

    private static bool AdmissionWindowFits(ExactTicketAdmission admission, ExactTicketAdmission.Claim claim,
                                            EngineCapability capability, long requiredWindow)
    {
        if (claim.Resident is not null) return VerifiedCapacityFits(claim, capability, requiredWindow);
        if (claim.SwitchRequired) return admission.Window(claim) >= requiredWindow;
        return capability.ModelRoster.Contains(claim.Ticket.Model, StringComparer.OrdinalIgnoreCase) &&
            capability.Serving.WindowFor(claim.Ticket.Model) >= requiredWindow;
    }

    private static bool VerifiedCapacityFits(ExactTicketAdmission.Claim claim, EngineCapability capability,
                                            long requiredWindow)
    {
        var resident = claim.Resident!;
        var window = capability.Serving.WindowFor(resident.Target.Model);
        // Current engine observations can veto but cannot supply a missing instance proof.
        return capability.Healthy && capability.SwitchingToward is null &&
            capability.ModelRoster.Contains(resident.Target.Model, StringComparer.OrdinalIgnoreCase) &&
            resident.Fits(requiredWindow) && (window <= 0 || window >= Math.Max(requiredWindow, resident.TierWindow));
    }

    private void RejectVerifiedBinding(ExactTicketAdmission.Claim claim, VerifiedDispatchRefusal refusal)
    {
        var admission = _admission!;
        bool expired;
        lock (admission.Gate)
        lock (_gate)
        {
            if (_closing || _shutdownToken.IsCancellationRequested || !admission.IsCurrentBindingRefusal(claim) ||
                !_queues.TryGetValue(claim.Ticket.Model, out var queue))
                return;
            lock (queue.Gate)
            {
                if (!queue.TryRemoveById(claim.Ticket.Id)) return;
                RetireQueueIfEmpty(queue);
            }
            expired = claim.Request.QueueExpired(_clock);
            ClearQueuedScheduling(claim.Ticket);
            claim.Ticket.RevokeAdmission();
        }
        if (expired)
            Fail(claim.Ticket, "deadline_exceeded", "The queued request expired before verified admission.");
        else
            Fail(claim.Ticket, "engine_unreachable", new VerifiedDispatchBindingException(refusal).Message);
        admission.Invalidate(claim.Ticket);
        SignalDispatch();
    }

    private void StartSelected(QueuedRequest candidate, EngineRuntime engine, RouterConfig settings, bool requiresSwitch,
                               ServingProfile? targetProfile, SwitchServingProof? servingProof,
                               ExactTicketAdmission.Invocation? invocation, string? routingReason)
    {
        var ticket = candidate.Ticket;
        var publishedSelection = false;
        try
        {
            ticket.EndQueueWait();
            var selectedAt = _clock.GetTimestamp();
            var switchRequired = invocation is null && requiresSwitch;
            if (invocation?.IsCommissioned == true && ticket.IsHttpStream && ticket.HttpDelivery is null)
                ticket.HttpDelivery = new HttpStreamDelivery(HttpStreamDelivery.CommissionedDefaultLimit, candidate.CallerToken, _shutdownToken);
            if (candidate.Request.Stream && ticket.StreamChannel is null)
            {
                // Publish the channel with the state change: a listener that sees Executing must be
                // able to read the stream from that moment, not only after the engine call starts.
                ticket.StreamChannel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            }

            ticket.State = switchRequired ? TicketState.WaitingForModel : TicketState.Executing;
            ticket.Engine = engine.Name;
            PublishSelection();
            // No cancellation token on Task.Run here: with an already-cancelled token Task.Run hands
            // back a task that never runs the delegate, which would strand the thread slot and the
            // engine slot reserved at pop. ExecuteAsync links its own CTS to _shutdown.
            _ = Task.Run(() => ExecuteAsync(candidate, engine, settings, switchRequired, targetProfile, servingProof, selectedAt, invocation));
        }
        catch (Exception)
        {
            if (!publishedSelection) PublishSelection();
            _ = AbortBeforeExecutionAsync(candidate, engine, invocation);
        }

        void PublishSelection()
        {
            publishedSelection = true;
            _events.Append(new RouterEvent
            {
                Kind = "request.selected",
                Reason = routingReason,
                ThreadId = ticket.ThreadId, OwnerGroup = ticket.OwnedPolicy?.Key,
                TicketId = ticket.Id,
                Engine = engine.Name,
                Model = ticket.Model,
            });
        }
    }

    private async Task AbortBeforeExecutionAsync(QueuedRequest candidate, EngineRuntime engine,
                                                  ExactTicketAdmission.Invocation? invocation)
    {
        try
        {
            await AbortUnstartedInvocationAsync(candidate.Ticket, invocation).ConfigureAwait(false);
        }
        finally
        {
            EndEngineRequest(candidate, engine, invocation);
            lock (_gate) candidate.Thread.DecrementInflight();
            Fail(candidate.Ticket, "cancelled", "router is shutting down");
            candidate.LocalLease?.Release();
            ReleaseExecutionCancellation(candidate.Ticket);
            SignalDispatch();
        }
    }

    private void EndEngineRequest(QueuedRequest candidate, EngineRuntime engine,
                                         ExactTicketAdmission.Invocation? invocation)
    {
        var named = candidate.Request.Options?.Profile is not null;
        if (named) engine.ReleaseNamedUse();
        if (invocation is null || named || candidate.ReservedSwitch)
        {
            lock (_gate)
            {
                if (named || candidate.ReservedSwitch)
                {
                    var uses = _profileUsers[engine];
                    uses.Named -= named ? 1 : 0;
                    uses.Switching -= candidate.ReservedSwitch ? 1 : 0;
                    if (uses == default) _profileUsers.Remove(engine);
                    else _profileUsers[engine] = uses;
                }
                // A predecessor can compete for a last thread slot from a different engine.
                // Ordinary engine release and the cross-engine exact cut share the router gate.
                if (invocation is null) engine.EndRequest(candidate.PromptChars);
            }
        }
        invocation?.EndOrdinaryUse(engine, candidate.PromptChars);
    }

    // The router creates this source before enqueue. Detaching it transfers the router's
    // ownership to this scope and prevents either terminal path from disposing it twice.
    private static void ReleaseExecutionCancellation(RouterTicket ticket)
    {
        using var owned = Interlocked.Exchange(ref ticket.ExecCts, null);
    }

    private async ValueTask AbortUnstartedInvocationAsync(RouterTicket ticket, ExactTicketAdmission.Invocation? invocation)
    {
        if (invocation is null) return;
        try
        {
            await invocation.AbortAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // A failed cleanup keeps the submitted use counted. Settling the logical ticket
            // must not strand its thread or replace a terminal outcome it already published.
            Fail(ticket, "engine_unreachable", ex.Message);
        }
    }

    private Selection OrderEligibleSwitches(QueueRound[] round, ref int through, Selection baseline,
                                            SwitchComparison comparison, ConfigurationGeneration generation)
    {
        var order = new SwitchCostOrder<Selection>();
        Consider(baseline);
        while (through + 1 < round.Length)
        {
            var next = through + 1;
            if (round[next].Snapshot.Depth != round[baseline.QueueIndex].Snapshot.Depth) break;
            var request = Array.Find(round[next].Snapshot.Requests, IsPendingAndThreadAdmissible);
            if (request is null)
            {
                through = next;
                continue;
            }
            var view = new SchedulerView(BuildEngineInfos(request, generation, comparison), comparison.UtcNow);
            var decision = _scheduler.SelectNext(request, view, generation.Settings);
            if (decision.Engine is not null && (!decision.SwitchRequired ||
                request.PriorityNode.Priority.CompareTo(baseline.Request.PriorityNode.Priority) != 0)) break;
            through = next;
            if (decision.Engine is not null) Consider(new Selection(next, request, decision));
        }
        return order.Selected ?? baseline;

        void Consider(Selection selection)
        {
            var request = selection.Request;
            var estimate = comparison.Engine.EstimateVerifiedTransition(comparison.Incumbent, request.Ticket.Model,
                selection.Decision.TargetProfile, comparison.Timestamp);
            var age = _clock.GetElapsedTime(request.EnqueuedAtTimestamp, comparison.Timestamp);
            order.Consider(selection, request.EnqueuedSeq, age, estimate);
        }
    }

    private RouteProbe ProbeRoute(QueuedRequest candidate, EngineRuntime engine, SchedulerDecision decision,
                                  EngineRuntime.DispatchObservation observation, ConfigurationGeneration generation) =>
        ProbeRoute(candidate, engine, new PrefixAwareScheduler.Target(decision.SwitchRequired, decision.TargetProfile),
            observation, generation);

    // Read only. Each mutation is a separate exact-take or explicit drain-intent commit.
    private RouteProbe ProbeRoute(QueuedRequest candidate, EngineRuntime engine, PrefixAwareScheduler.Target target,
                                  EngineRuntime.DispatchObservation observation, ConfigurationGeneration generation)
    {
        var profile = engine.GetProfile(candidate.Ticket.Model, target.Profile);
        var group = LocalGroup(engine.Name);
        var named = candidate.Request.Options?.Profile;
        var serving = observation.Capability.Serving;
        if (named is not null && (profile is null || engine.GetProfile(candidate.Ticket.Model, named) != profile ||
            !engine.SupportsNamedProfile(candidate.Ticket.Model, profile) || profile.MaxContext < candidate.RequiredContextTokens ||
            (!target.SwitchRequired && !engine.MatchesNamedResident(candidate.Ticket.Model, profile, observation.Capability))))
            return new(profile, group, new("engine_profile_unavailable", engine.Name));
        var uses = _profileUsers.GetValueOrDefault(engine);
        var commissionedSwitch = target.SwitchRequired && _hostManager?.OwnsCommissioned(engine.Name) == true;
        if (!commissionedSwitch && ((uses.Switching != 0 && (named is not null || uses.Named != 0)) ||
            ((uses.Named != 0 || engine.HasNamedUse) && target.SwitchRequired) ||
            (named is not null && target.SwitchRequired && group is null && (observation.Inflight != 0 || engine.InferenceUses != 0))))
            return new(profile, group, new("engine_profile_in_use", engine.Name));
        var requiredWindow = EngineRuntime.RequiredWindow(profile, candidate.ExplicitContextMinimum);
        if (!UsesExactAdmission(engine) && requiredWindow > serving.WindowFor(candidate.Ticket.Model) &&
            !WillWaitForModel(generation.Settings, target.SwitchRequired, profile))
            return new(profile, group, new("engine_context_unavailable", engine.Name));

        if (group is not null)
        {
            switch (group.Probe(candidate, engine, profile, target.SwitchRequired))
            {
                case LocalLifecycleGroup.Availability.Draining:
                    return new(profile, group, new("lifecycle_group_draining", group.Name), Drain: true);
                case LocalLifecycleGroup.Availability.Owned:
                    return new(profile, group, new("lifecycle_group_reserved", group.Name));
                case LocalLifecycleGroup.Availability.Switching:
                    return new(profile, group, new("lifecycle_group_switching", group.Name));
                case LocalLifecycleGroup.Availability.Cold when !target.SwitchRequired:
                    return new(profile, group, new("lifecycle_group_residency_changed", group.Name));
            }
        }
        return new(profile, group, commissionedSwitch || observation.HasCapacity ? null : new("engine_concurrency_limit", engine.Name));
    }

    private Blocker DescribeUnavailable(EngineRuntimeInfo[] infos, QueuedRequest request)
    {
        foreach (var engine in infos)
        {
            if (!engine.CanServeModel) continue;
            if (engine.Switching)
            {
                var group = LocalGroup(engine.Name);
                return group is null ? new("engine_switching", engine.Name) : new("lifecycle_group_switching", group.Name);
            }
            if (!engine.Healthy) return new("engine_unhealthy", engine.Name);
            if (!engine.LoadedModels.Contains(request.Ticket.Model, StringComparer.OrdinalIgnoreCase))
                return new("engine_model_not_ready", engine.Name);
        }
        return new("no_available_engine");
    }

    private static string FormatBlocker(Blocker blocker) => blocker.Scope is { } scope
        ? $"{blocker.Reason} ({WireText.Ident(scope)})" : blocker.Reason;

    private static bool WillWaitForModel(RouterConfig settings, bool switchRequired, ServingProfile? profile) =>
        settings.AutoSwitch && (switchRequired || profile is not null);

    private static EngineRuntime.DispatchObservation FindDispatchObservation(EngineRuntimeInfo[] infos, string engine)
    {
        foreach (var info in infos)
            if (info.Name == engine) return info.DispatchObservation;
        throw new InvalidOperationException("The selected engine is missing from the scheduler view.");
    }

    private static VerifiedDispatchResident? ReadRankedResident(EngineRuntimeInfo[] infos, string engine)
    {
        foreach (var info in infos)
            if (info.Name == engine) return info.ManagedResident;
        return null;
    }

    private PrefixAwareScheduler.RouteFit ReadRouteFit(QueuedRequest request, EngineRuntime engine,
                                                        EngineRuntime.DispatchObservation observation)
    {
        var capability = observation.Capability;
        var model = request.Ticket.Model;
        var group = LocalGroup(engine.Name);
        var managed = _hostManager?.Owns(engine.Name) == true;
        var resident = managed ? _hostManager!.Admission.ReadManagedResident(engine.Name) : null;
        PrefixAwareScheduler.NamedRoute? named = null;
        if (request.Request.Options?.Profile is { } spelling)
        {
            var profile = engine.GetProfile(model, spelling);
            if (profile is not null && engine.SupportsNamedProfile(model, profile))
                named = new(profile, managed
                    ? resident is not null && resident.Target == _hostManager!.Recipe(engine.Name, model, profile.Name) &&
                        capability.Serving.Matches(model, profile)
                    : engine.MatchesNamedResident(model, profile, capability));
            else named = new(null, false);
        }
        return new(capability.Healthy, capability.SwitchingToward is not null, engine.CanServeModel(model, capability),
            group is not null || managed && engine.CanSwitchTo(model),
            request.Request.Options?.Profile is not null && engine.CanSwitchTo(model),
            (group is null || group.IsResident(engine.Name, model)) && (!managed || resident is not null)
                ? capability.ModelRoster : [],
            capability.Serving.WindowFor(model), engine.ProfilesForModel(model), observation, named, resident,
            managed ? _hostManager!.SupportedProfiles(engine.Name, model) : null, engine.DefaultServingTier(model));
    }

    private EngineRuntimeInfo[] BuildEngineInfos(QueuedRequest request, ConfigurationGeneration generation,
        SwitchComparison? comparison, bool includeAffinity = true)
    {
        includeAffinity &= !OwnedPolicyEnabled;
        var messages = includeAffinity ? request.Messages() : [];
        var infos = new EngineRuntimeInfo[generation.Engines.Count];
        var index = 0;
        foreach (var engine in generation.Engines.Values)
        {
            if (!AllowsOwnedRoute(request.Ticket, engine.Name)) continue;
            // The loaded roster, switch marker, and fit come from one published capability.
            var group = LocalGroup(engine.Name);
            var groupRevision = group?.Revision ?? 0;
            var observation = comparison?.Observation ?? engine.ReadDispatchObservation();
            var namedUse = engine.HasNamedUse;
            var inferenceUses = engine.InferenceUses;
            var fit = ReadRouteFit(request, engine, observation);
            var switching = observation.Capability.SwitchingToward;
            infos[index++] = new EngineRuntimeInfo
            {
                Name = engine.Name,
                LoadedModels = fit.LoadedModels,
                CanLoad = fit.CanLoad,
                CanSwitchProfile = fit.CanSwitchProfile,
                CatalogModels = engine.CatalogModelNames.ToArray(),
                SwitchingToward = switching is null ? [] : [switching],
                CanServeModel = fit.CanServeModel,
                Inflight = observation.Inflight,
                InflightTokenProxy = observation.InflightTokenProxy,
                Healthy = fit.Healthy,
                Switching = fit.Switching,
                MatchedPrefixChars = includeAffinity ? engine.MatchedPrefixChars(messages) : 0,
                ActiveContextWindow = fit.ActiveContextWindow,
                ServingProfiles = fit.ServingProfiles,
                DispatchObservation = observation,
                DispatchGroup = group,
                DispatchGroupRevision = groupRevision,
                NamedPhysicalUse = namedUse,
                DispatchInferenceUses = inferenceUses,
                NamedProfile = fit.NamedProfile,
                ManagedResident = fit.ManagedResident,
                ManagedColdProfiles = fit.ManagedColdProfiles,
                DefaultProfile = fit.DefaultProfile,
            };
        }
        return index == infos.Length ? infos : infos[..index];
    }

    // ── execution ───────────────────────────────────────────────────────────

    private async Task ExecuteAsync(QueuedRequest candidate, EngineRuntime engine, RouterConfig settings, bool switchRequired,
                                    ServingProfile? targetProfile, SwitchServingProof? servingProof, long selectedAt,
                                    ExactTicketAdmission.Invocation? invocation)
    {
        var ticket = candidate.Ticket;
        var thread = candidate.Thread;
        var execCts = ticket.ExecCts ??= new CancellationTokenSource();
        var decremented = false;
        var ended = false;
        var optInOwnsInvocation = false;
        var lateForwardOwnsLocalLease = false;
        var journalOwned = UsesOwnedJournal(ticket);
        void DecrementOnce()
        {
            if (decremented) return;
            decremented = true;
            lock (_gate) thread.DecrementInflight();
        }
        void EndEngineOnce()
        {
            if (ended) return;
            ended = true;
            EndEngineRequest(candidate, engine, invocation);
        }
        try
        {

            if (invocation is null && (candidate.LocalLease is { Cold: true } ||
                (WillWaitForModel(settings, switchRequired, targetProfile) && (switchRequired || candidate.Request.Options?.Profile is null))))
            {
                ticket.State = TicketState.WaitingForModel;
                // A ticket's own deadline bounds only its time in the queue, so the wait for a model
                // switch needs its own ceiling: without one a request is parked through a model load as
                // long as the engine's lifecycle wait allows (two hours for a strata select). 0 = no
                // ceiling, which is the old behaviour.
                var ceilingMs = settings.Scheduler.SwitchWaitTimeoutMs;
                var switchWait = candidate.LocalLease?.Budget.Remaining ??
                    (ceilingMs <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(ceilingMs));
                // The caller's token bounds only its own wait: the switch transaction runs on the engine's
                // token, so giving up here never leaves the engine half-switched with nothing loaded.
                EngineRuntime.ObservedSwitchResult observed;
                if (candidate.LocalLease is { Cold: true } localOwner)
                {
                    using var localWait = CancellationTokenSource.CreateLinkedTokenSource(execCts.Token, candidate.CallerToken, _shutdownToken);
                    var preparation = localOwner.PrepareAsync(execCts.Token, _shutdownToken);
                    try
                    {
                        observed = await preparation.WaitAsync(switchWait, localWait.Token).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        observed = new(new EngineRuntime.SwitchResult(EngineRuntime.SwitchOutcome.TimedOut, null));
                    }
                    localWait.Token.ThrowIfCancellationRequested();
                }
                else
                {
                    observed = await engine.EnsureLoadedAsync(ticket.Model, targetProfile, switchWait,
                        new SwitchQueueTiming(candidate.EnqueuedAtTimestamp, selectedAt), execCts.Token,
                        explicitContextMinimum: candidate.ExplicitContextMinimum,
                        exactProfile: candidate.Request.Options?.Profile is not null).ConfigureAwait(false);
                }
                var switchResult = observed.Result;
                AfterModelReadinessForTests?.Invoke(ticket.Id, switchResult.Outcome);
                if (switchResult.Outcome == EngineRuntime.SwitchOutcome.RouterStopped)
                {
                    Fail(ticket, "cancelled", "cancelled");
                    return;
                }
                if (!switchResult.IsReady)
                {
                    var what = targetProfile is null
                        ? WireText.Ident(ticket.Model)
                        : $"{WireText.Ident(ticket.Model)} @ {WireText.Ident(targetProfile.Name)}";
                    // Failed and TimedOut answer the only question the caller has — retry now and hit the
                    // same wall, or retry later and probably get through — so the message keeps them apart. A
                    // completed attempt names one token from the closed set in parentheses; an expired wait names
                    // its own ceiling and no category, because nothing of the attempt ended (§5.5).
                    var detail = switchResult.Detail is { Length: > 0 } d ? $" ({d})" : "";
                    Fail(ticket, "switch_failed", switchResult.Outcome == EngineRuntime.SwitchOutcome.TimedOut
                        ? $"engine {WireText.Ident(engine.Name)} did not become ready to serve {what} within {ceilingMs} ms" +
                          (candidate.LocalLease is null ? " — the switch is still running, retry once it completes" : "") + detail
                        : $"engine {WireText.Ident(engine.Name)} could not serve {what}{detail}");
                    return;
                }
                if (candidate.ReservedSwitch)
                {
                    lock (_gate)
                    {
                        var uses = _profileUsers[engine];
                        if (--uses.Switching == 0 && uses.Named == 0) _profileUsers.Remove(engine);
                        else _profileUsers[engine] = uses;
                        candidate.ReservedSwitch = false;
                    }
                    SignalDispatch();
                }
                servingProof = observed.Proof;
                ticket.State = TicketState.Executing;
            }

            Interlocked.Increment(ref thread.Enqueued);
            if (candidate.Request.Stream)
            {
                ticket.StreamChannel ??= Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            }
            if (ticket.IsHttpStream && (ticket.HttpDelivery is not null || engine.StreamIdleTimeout is not null))
            {
                optInOwnsInvocation = true;
                await ExecuteOptInStreamAsync(candidate, engine, servingProof, invocation, execCts.Token)
                    .ConfigureAwait(false);
                return;
            }
            if (invocation?.IsCommissioned == true)
            {
                optInOwnsInvocation = true;
                await ExecuteCommissionedAsync(candidate, engine, invocation, execCts.Token).ConfigureAwait(false);
                return;
            }
            var ownsForward = false;
            try
            {
                var executionToken = execCts.Token;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, executionToken, candidate.CallerToken);
                var upstreamModel = engine.CanonicalModel(ticket.Model) ?? ticket.Model;
                var choices = ticket.PrivatePreparation?.ExpectedChoices ?? 0;
                var body = journalOwned ? [] : ModelRewriter.Rewrite(candidate.Request.Body, upstreamModel, candidate.Request.Stream, out choices);
                var suffix = CompletionSuffix(candidate);

                if (invocation is not null)
                {
                    using var request = MakeUpstreamRequest(engine, body, suffix, ticket);
                    await invocation.RunAsync(request, response =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        return FinishWithResponseAsync(engine, candidate, response, upstreamModel, choices, gate: null,
                            executionToken, cts.Token);
                    }, ReportExecutionFailure, cts.Token, invocation.VerifiedCredentialEpoch).ConfigureAwait(false);
                    return;
                }

                if (!engine.TryBeginForward(cts.Token, candidate.Request.Options?.Profile is not null))
                {
                    Fail(ticket, "cancelled", "cancelled");
                    return;
                }
                ownsForward = true;
                if (engine.AfterForwardAdmissionForTests is { } afterAdmission)
                    await afterAdmission().ConfigureAwait(false);

                await engine.VerifyRequestedProfileAsync(candidate, cts.Token).ConfigureAwait(false);

                // The cap bounds the wait for the engine to *answer*: the whole response body for a
                // non-stream call, the first byte of the body for a stream. Response headers are not an
                // answer — an engine may send 200 and then go quiet, and holding a slot, a ticket and a
                // caller through that is what this cap exists to prevent. Once the owed bytes are in hand
                // the gate is disarmed and a long generation runs to its end: one gate per request, not
                // per token.
                var responseTimeout = engine.ResponseTimeout;
                if (responseTimeout is null)
                {
                    using var res = await SendAsync(engine, body, suffix, candidate, servingProof, cts.Token)
                        .ConfigureAwait(false);
                    await FinishWithResponseAsync(engine, candidate, res, upstreamModel, choices, gate: null,
                        executionToken, cts.Token).ConfigureAwait(false);
                }
                else
                {
                    // The gate must NOT share the caller's token: cancelling that token would complete the
                    // delay, Task.WhenAny would report the delay as the winner, and a caller who hung up
                    // would be answered `engine_timeout` instead of `cancelled`. It runs on its own source,
                    // which is disarmed when the request ends or when the engine delivers what it owed.
                    using var gate = new ResponseGate(responseTimeout.Value, ResponseTimeoutClockForTests);
                    var delay = Task.Delay(Timeout.Infinite, gate.Token);
                    var send = SendAsync(engine, body, suffix, candidate, servingProof, cts.Token);
                    HttpResponseMessage? res = null;
                    bool silent;
                    try
                    {
                        if (await Task.WhenAny(send, delay).ConfigureAwait(false) == send)
                        {
                            res = await send.ConfigureAwait(false);
                            try
                            {
                                await FinishWithResponseAsync(engine, candidate, res, upstreamModel, choices, gate,
                                    executionToken, cts.Token).ConfigureAwait(false);
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
                            ownsForward = false;
                            lateForwardOwnsLocalLease = true;
                            _ = DisposeLateForwardAsync(engine, send, candidate, journalOwned);
                        }
                    }

                    if (silent)
                    {
                        cts.Cancel();          // release the connection to the engine that never answered
                        if (execCts.IsCancellationRequested || _shutdownToken.IsCancellationRequested)
                        {
                            // Somebody upstream gave up before the ceiling did, and their reason is the
                            // honest one: a request the caller abandoned stays `cancelled` even when the
                            // engine's handler ignored the cancellation we sent and the ceiling fired first.
                            Fail(ticket, "cancelled", "cancelled");
                            return;
                        }

                        ticket.ObserveStreamRefusal();
                        Fail(ticket, "engine_timeout",
                            $"engine {WireText.Ident(engine.Name)} sent no response to {WireText.Ident(ticket.Model)} within " +
                            $"{responseTimeout.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
                        return;
                    }
                }
            }
            finally
            {
                if (ownsForward) engine.EndForward(candidate.Request.Options?.Profile is not null);
                if (!OwnedPolicyEnabled) engine.RecordPrefix(candidate.Messages());
            }
        }
        catch (Exception ex)
        {
            ReportExecutionFailure(ex);
        }
        finally
        {
            try
            {
                if (!lateForwardOwnsLocalLease && !optInOwnsInvocation) candidate.LocalLease?.Release();
                if (!optInOwnsInvocation)
                    await AbortUnstartedInvocationAsync(ticket, invocation).ConfigureAwait(false);
            }
            finally
            {
                ReleaseLogical();
            }
        }

        void ReportExecutionFailure(Exception error)
        {
            if (error is OperationCanceledException) Fail(ticket, "cancelled", "cancelled");
            else if (error is RouterRequestException refusal) Fail(ticket, refusal.Code, refusal.Message);
            else Fail(ticket, "engine_unreachable", OwnedPolicyEnabled ? "the private upstream exchange failed" : error.Message);
        }

        void ReleaseLogical()
        {
            if (!journalOwned || (!lateForwardOwnsLocalLease && !optInOwnsInvocation)) EndEngineOnce();
            if (!optInOwnsInvocation && (!journalOwned || !lateForwardOwnsLocalLease))
                ReleaseExecutionCancellation(ticket);
            DecrementOnce();
            SignalDispatch();          // a retained physical sender issues its own wake after release
            if (!lateForwardOwnsLocalLease && !optInOwnsInvocation) RetireOwnedPreparation(ticket);
        }
    }

    private void ReportAdmissionCleanupFailure(ExactTicketAdmission.Claim claim, Exception error) =>
        _events.Append(new RouterEvent
        {
            Kind = "request.cleanup_failed",
            ThreadId = claim.Ticket.ThreadId, OwnerGroup = claim.Ticket.OwnedPolicy?.Key,
            TicketId = claim.Ticket.Id,
            Engine = claim.Target.Engine,
            Model = claim.Target.Model,
            Reason = error.Message,
        });

    private async Task DisposeLateForwardAsync(EngineRuntime engine, Task<HttpResponseMessage> send,
        QueuedRequest candidate, bool keepOwnedSlot)
    {
        try
        {
            using var response = await send.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The logical ticket already owns the timeout/error outcome. Observe this late
            // send and retain the local client through response disposal.
        }
        finally
        {
            engine.EndForward(candidate.Request.Options?.Profile is not null);
            candidate.LocalLease?.Release();
            if (keepOwnedSlot)
            {
                EndEngineRequest(candidate, engine, invocation: null);
                ReleaseExecutionCancellation(candidate.Ticket);
                SignalDispatch();
            }
            RetireOwnedPreparation(candidate.Ticket);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        EngineRuntime engine, byte[] body, string suffix, QueuedRequest candidate, SwitchServingProof? proof, CancellationToken ct)
    {
        if (!AllowsOwnedRoute(candidate.Ticket, engine.Name))
            throw new RouterRequestException("policy_route_denied", "the owned private route is no longer eligible");
        if (UsesOwnedJournal(candidate.Ticket))
        {
            if (Volatile.Read(ref candidate.Ticket.PrivatePreparation) is not { Ready: true, Attempt: { } attempt } prepared ||
                !ReferenceEquals(prepared.Engine, engine))
                throw new RouterRequestException("policy_route_denied", "the owned private route preparation is no longer eligible");
            return await ForwardAsync(attempt.Request).ConfigureAwait(false);
        }
        using var direct = MakeUpstreamRequest(engine, body, suffix, candidate.Ticket);
        return await ForwardAsync(direct).ConfigureAwait(false);

        async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request)
        {
            engine.ObserveFirstForward(proof, candidate.Ticket.Model, ct);
            try
            {
                return await engine.InferenceHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or IOException && !ct.IsCancellationRequested)
            {
                candidate.Ticket.ObserveStreamRefusal();
                throw;
            }
        }
    }

    private static string CompletionSuffix(QueuedRequest candidate) => (candidate.Ticket.OwnedPolicy is { } policy
        ? policy.Capability == PrivateDestinationCapability.Text : LooksLikeCompletion(candidate.Request.Body))
        ? "completions" : "chat/completions";

    private HttpRequestMessage MakeUpstreamRequest(EngineRuntime engine, byte[] body, string suffix, RouterTicket ticket,
        bool freezeBody = false)
    {
        // The suffix is composed against the endpoint path (EngineRuntime.Combine), so an endpoint
        // mounted under /v1 keeps it.
        var req = new HttpRequestMessage(HttpMethod.Post, engine.CompletionUri(suffix));
        if (engine.ApiKey is not null)
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {engine.ApiKey}");
        if ((engine.SendsThreadCacheHint || _ownedPolicy?.SendsThreadCacheHint(engine.Name) == true) &&
            !ticket.IsStateless && !string.IsNullOrWhiteSpace(ticket.ThreadId) && ticket.ThreadId != "anon")
            req.Headers.Add("X-Inference-Thread", ThreadCacheHint(engine.Name, ticket.ScopedThreadKey ?? ticket.ThreadId));
        req.Content = freezeBody ? new ReadOnlyMemoryContent(body) : new ByteArrayContent(body);
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return req;
    }

    private static string ThreadCacheHint(string engine, string threadId)
    {
        ReadOnlySpan<byte> domain = "AIR local inference thread v1"u8;
        int engineBytes = Encoding.UTF8.GetByteCount(engine);
        var input = new byte[checked(domain.Length + sizeof(int) + engineBytes + Encoding.UTF8.GetByteCount(threadId))];
        domain.CopyTo(input);
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(domain.Length), engineBytes);
        var fields = input.AsSpan(domain.Length + sizeof(int));
        Encoding.UTF8.GetBytes(engine, fields);
        Encoding.UTF8.GetBytes(threadId, fields[engineBytes..]);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>What happens once the engine has answered: an error status becomes the router's own
    /// error contract, a stream is relayed frame by frame, a normal completion is returned as bytes.
    /// Split out so the capped and uncapped send paths cannot drift apart. `gate` is the response cap;
    /// for a non-streaming call it stays armed across the body read, and for a stream the pump disarms it
    /// at the first byte so a generation in progress is never truncated.</summary>
    private async Task FinishWithResponseAsync(EngineRuntime engine, QueuedRequest candidate,
        HttpResponseMessage res, string upstreamModel, ulong expectedChoices, ResponseGate? gate,
        CancellationToken? defaultExecutionToken, CancellationToken ct)
    {
        var ticket = candidate.Ticket;
        // The body is the answer, so the cap covers the body: linked with the caller's token so an abort
        // still ends as cancelled rather than as a timeout.
        using var bodyLink = gate is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, gate.Token);
        var bodyCt = bodyLink?.Token ?? ct;

        if (!res.IsSuccessStatusCode)
        {
            ticket.ObserveStreamRefusal();
            if (OwnedPolicyEnabled)
            {
                // Response status alone is not proof that an ambiguous private attempt settled.
                gate?.Pass();
                Fail(ticket, "engine_error", "the private upstream refused the request");
                return;
            }
            const int errorBodyCeiling = 256;
            var errBody = new byte[errorBodyCeiling];
            using var errorStream = await res.Content.ReadAsStreamAsync(bodyCt).ConfigureAwait(false);
            var errorBytesRead = await errorStream.ReadAtLeastAsync(errBody, errorBodyCeiling,
                throwOnEndOfStream: false, cancellationToken: bodyCt).ConfigureAwait(false);
            // The excerpt is in hand, so pass the response gate *before* the terminal call. Otherwise a
            // failure inside Fail/event work could be caught by the ceiling filter and blamed on the engine.
            gate?.Pass();
            Fail(ticket, "engine_error",
                $"{(int)res.StatusCode}: {System.Text.Encoding.UTF8.GetString(errBody.AsSpan(0, errorBytesRead))}");
            return;
        }

        if (ticket.IsStreamingRequest)
        {
            var channel = ticket.StreamChannel!;
            DefaultStreamShutdown? shutdown = null;
            if (defaultExecutionToken is { } execution && ticket.IsHttpStream &&
                ticket.HttpDelivery is null && engine.StreamIdleTimeout is null)
            {
                var observe = DefaultStreamReadObservedForTests;
                shutdown = new DefaultStreamShutdown(ct, _shutdownToken, execution, candidate.CallerToken,
                    observe is null ? null : (arm, observation) => observe(ticket.Id, arm, observation));
            }
            using var registration = shutdown?.Register() ?? default;
            var observer = StartStreamObservation(ticket, upstreamModel, expectedChoices);
            var error = await PumpStreamAsync(engine, ticket, res.Content, channel, observer, gate, ct, shutdown: shutdown)
                .ConfigureAwait(false);
            // The ticket's terminal transition closes the channel after the safe prefix is queued.
            if (shutdown?.Qualified == true) Fail(ticket, "cancelled", "cancelled", defaultStreamReadShutdown: true);
            else if (error is not null) Fail(ticket, "engine_error", error);
            else if (observer.SawError) Fail(ticket, "engine_error", SemanticStreamObserver.ReportedError, suppressTerminalFrame: true);
            else Complete(ticket);
        }
        else
        {
            var bytes = await res.Content.ReadAsByteArrayAsync(bodyCt).ConfigureAwait(false);
            ticket.PrivateAttempt?.ObserveTerminal();
            gate?.Pass();      // owed bytes delivered: the ceiling is spent before any terminal work
            Complete(ticket, bytes);
        }
    }

    private SemanticStreamObserver StartStreamObservation(RouterTicket ticket, string upstreamModel, ulong expectedChoices)
    {
        var observer = new SemanticStreamObserver(_clock, upstreamModel, expectedChoices);
        ticket.AttachStreamObserver(observer);
        return observer;
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

        public ResponseGate(TimeSpan dueIn, TimeProvider clock) => _cts = new CancellationTokenSource(dueIn, clock);
        public CancellationToken Token => _cts.Token;
        public void Pass() { Interlocked.Exchange(ref _passed, 1); _cts.Cancel(); }
        public bool Tripped => Volatile.Read(ref _passed) == 0 && _cts.IsCancellationRequested;
        public void Dispose() => _cts.Dispose();
    }

    private async Task<string?> PumpStreamAsync(EngineRuntime engine, RouterTicket ticket, HttpContent content,
        Channel<ReadOnlyMemory<byte>> channel, SemanticStreamObserver observer, ResponseGate? gate, CancellationToken ct,
        StreamReadLifetime? lifetime = null, DefaultStreamShutdown? shutdown = null)
    {
        var firstToken = false;
        // The response ceiling applies to the first raw byte. The first complete SSE block can
        // arrive later, after the gate is disarmed, while the HTTP relay still sends keepalives.
        using var firstReadLink = gate is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, gate.Token);
        var firstReadCt = firstReadLink?.Token ?? ct;
        var buffer = new byte[16384];
        var blocks = new SseBlockBuffer(observer: observer);
        // Stream acquisition is inside the ceiling too: with ResponseHeadersRead the content object exists
        // before any byte does, and a handler that builds the stream lazily must not be able to stall the
        // request before the first gated read happens.
        Stream stream;
        try
        {
            stream = lifetime is null
                ? await content.ReadAsStreamAsync(firstReadCt).ConfigureAwait(false)
                : await lifetime.AcquireAsync(content, firstReadCt).ConfigureAwait(false);
        }
        catch (Exception error) when (!ct.IsCancellationRequested &&
            (error is not OperationCanceledException || gate?.Tripped == true))
        {
            observer.End(StreamInputEnd.Fault);
            throw;
        }
        using var ownedStream = lifetime is null ? stream : null;
        var delivery = ticket.HttpDelivery;
        var readCancelled = false;
        while (true)
        {
            if (delivery is not null)
                await delivery.WaitForSpaceAsync(ct).ConfigureAwait(false);
            int read;
            long arm = 0;
            try
            {
                var readToken = firstToken ? ct : firstReadCt;
                if (lifetime is null)
                {
                    var pending = stream.ReadAsync(buffer, readToken);
                    if (shutdown is { PublishedBlock: true } && !pending.IsCompleted)
                        arm = shutdown.TryArm();
                    read = await pending.ConfigureAwait(false);
                }
                else
                {
                    read = await lifetime.ReadAsync(stream, buffer, firstToken, readToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException error)
            {
                if (arm != 0 && shutdown!.ObserveResult(arm, 0, error)) return null;
                // Preserve the first-byte deadline path. Otherwise a stopped read still has to
                // account for any bytes already staged, after checking caller cancellation below.
                if (!firstToken && gate is not null && gate.Tripped)
                {
                    if (!ct.IsCancellationRequested) observer.End(StreamInputEnd.Fault);
                    throw;
                }
                readCancelled = true;
                break;
            }
            catch (Exception error)
            {
                if (arm != 0) shutdown!.ObserveResult(arm, 0, error);
                if (!ct.IsCancellationRequested) observer.End(StreamInputEnd.Fault);
                throw;
            }
            if (arm != 0 && shutdown!.ObserveResult(arm, read)) return null;
            if (read == 0) break;
            if (!firstToken)
            {
                firstToken = true;
                var firstRawByte = observer.ObserveRawByte();
                gate?.Pass();
                _events.Append(new RouterEvent
                {
                    Kind = "request.streaming_first_token",
                    ThreadId = ticket.ThreadId, OwnerGroup = ticket.OwnedPolicy?.Key,
                    TicketId = ticket.Id,
                    Engine = engine.Name,
                    Value = (long)firstRawByte.TotalMilliseconds,
                });
            }
            var (complete, limitExceeded) = blocks.Append(buffer.AsSpan(0, read));
            if (OwnedPolicyEnabled && observer.SawError)
            {
                observer.End(StreamInputEnd.Fault);
                return SemanticStreamObserver.ReportedError;
            }
            if (!complete.IsEmpty)
            {
                if (delivery is null)
                    await channel.Writer.WriteAsync(complete, ct).ConfigureAwait(false);
                else
                    await delivery.PublishAsync(channel.Writer, complete, ct).ConfigureAwait(false);
                if (shutdown is not null) shutdown.PublishedBlock = true;
            }
            if (limitExceeded)
            {
                ct.ThrowIfCancellationRequested();
                observer.End(StreamInputEnd.Fault);
                return "upstream SSE block exceeded 1 MiB before its blank delimiter";
            }
        }

        // Caller cancellation takes precedence over classifying the upstream ending.
        ct.ThrowIfCancellationRequested();
        if (blocks.HasIncompleteBlock)
        {
            observer.End(StreamInputEnd.Torn);
            return "upstream SSE stream ended before a blank delimiter";
        }
        observer.End(readCancelled ? StreamInputEnd.UpstreamCancelledRead : StreamInputEnd.Eof);
        return null;
    }

    private void Complete(RouterTicket ticket, byte[]? body = null)
    {
        if (!ticket.TryEnterTerminal()) return;
        ticket.DetachWaitHooks();
        RetainTerminalTicket(ticket);
        ticket.StoreTerminalState(TicketState.Completed);
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
            ThreadId = ticket.ThreadId, OwnerGroup = ticket.OwnedPolicy?.Key,
            TicketId = ticket.Id,
            Engine = ticket.Engine,
            Model = ticket.Model,
            UpstreamOutcome = ticket.StreamObservation?.OutcomeName,
            UpstreamEndReason = ticket.StreamObservation?.EndReasonName,
        });
        ticket.SignalTerminalStream();
        ticket.StreamChannel?.Writer.TryComplete();
        ticket.CompletionTcs.TrySetResult(body ?? []);
        SignalDispatch();
    }

    private void Fail(RouterTicket ticket, string code, string message, bool suppressTerminalFrame = false,
        bool defaultStreamReadShutdown = false)
    {
        if (ticket.HttpDelivery is { } delivery)
        {
            if (ticket.IsTerminal) return;
            var outcome = delivery.Decide(new HttpStreamOutcome(code, message, suppressTerminalFrame), execution: ticket.ExecCts);
            if (outcome.Code is not { } chosenCode) return;
            code = chosenCode;
            message = outcome.Message;
            suppressTerminalFrame = outcome.SuppressTerminalFrame;
        }
        if (!ticket.TryEnterTerminal()) return;
        ticket.SuppressTerminalFrame = suppressTerminalFrame;
        ticket.DefaultStreamReadShutdown = defaultStreamReadShutdown;
        // One terminal path, one ceiling. From here the same string is three caller-visible things at once —
        // <c>ticket.Error</c>, the <see cref="RouterRequestException"/> the HTTP writer turns into an error body,
        // and the error frame of a stream that already committed its 200 — plus the feed reason below. Some of it
        // the router wrote and cut piece by piece; some of it it did not write at all (an HTTP client's exception
        // text, an upstream sentence), so the ceiling goes on the finished sentence here, where it is true of every
        // consumer, rather than at one writer (§10).
        message = WireText.Bound(message, WireText.ReasonChars);
        ticket.DetachWaitHooks();
        RetainTerminalTicket(ticket);
        // Setting a terminal state can resolve StreamReady without a channel. Publish the error
        // details first so an HTTP relay woken by that path sees the same error as a channel reader.
        ticket.ErrorCode = code;
        ticket.Error = message;
        ticket.StoreTerminalState(code is "cancelled" ? TicketState.Cancelled : TicketState.Failed);
        _events.Append(new RouterEvent
        {
            Kind = code is "cancelled" ? "request.cancelled" : "request.failed",
            ThreadId = ticket.ThreadId, OwnerGroup = ticket.OwnedPolicy?.Key,
            TicketId = ticket.Id,
            Engine = ticket.Engine,
            Model = ticket.Model,
            Reason = message,
            UpstreamOutcome = ticket.StreamObservation?.OutcomeName,
            UpstreamEndReason = ticket.StreamObservation?.EndReasonName,
        });
        ticket.SignalTerminalStream();
        ticket.StreamChannel?.Writer.TryComplete();
        ticket.CompletionTcs.TrySetException(new RouterRequestException(code, message));
        SignalDispatch();
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
        if (ticket.IsStateless) return null;
        lock (_gate) return _threads.GetValueOrDefault(ticket.ScopedThreadKey ?? ticket.ThreadId);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private string NewTicketId() => $"t{Interlocked.Increment(ref _seq):x8}-{Random.Shared.Next(0xffff):x4}";

    internal static long? ObservedModelContextWindow(ConfigurationGeneration generation,
        EngineRuntime engine, EngineCapability capability, string model)
    {
        if (capability.Healthy && capability.SwitchingToward is null &&
            capability.ModelContextWindows.TryGetValue(model, out var tokens) &&
            string.Equals(ResolveCanonical(model, generation), model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(engine.CanonicalModel(model) ?? model, model, StringComparison.OrdinalIgnoreCase))
            return tokens;
        return null;
    }

    private static string ResolveCanonical(string requested, ConfigurationGeneration generation)
    {
        if (generation.Aliases.TryGetValue(requested, out var mapped)) return mapped;
        foreach (var e in generation.Engines.Values)
        {
            var canonical = e.CanonicalModel(requested);
            if (canonical is not null) return canonical;
        }
        return requested;
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

    public ValueTask DisposeAsync()
    {
        // A queued ticket has no execution to cancel, so it needs an explicit terminal transition;
        // without this its relays sit open until the process exits.
        List<QueuedRequest> pending;
        ConfigurationGeneration generation;
        Task? housekeeping;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_shutdownCompletion is { } wait) return new ValueTask(wait.Task);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownCompletion = completion;
            Volatile.Write(ref _closing, true);
            generation = CurrentGeneration;
            housekeeping = _reloadHousekeeping?.Task;
            pending = [];
            foreach (var q in _queues.Values) pending.AddRange(q.DrainAll());
            _queues.Clear();
            foreach (var queued in pending)
            {
                ClearQueuedScheduling(queued.Ticket);
                if (queued.Ticket.PendingEnqueuePublication)
                    queued.Ticket.DeferredQueueEnd ??= "cancelled";
            }
            pending.RemoveAll(queued => queued.Ticket.PendingEnqueuePublication);
        }
        _ = CompleteShutdownAsync(generation, housekeeping, pending, completion);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteShutdownAsync(ConfigurationGeneration generation, Task? housekeeping,
        List<QueuedRequest> pending, TaskCompletionSource completion)
    {
        try
        {
            BeforeShutdownCancellationForTests?.Invoke();
            _admission?.Stop();
            _shutdown.Cancel();
            foreach (var queued in pending)
            {
                Fail(queued.Ticket, "cancelled", "router shutting down");
                RetireOwnedPreparation(queued.Ticket, queued: true);
                ReleaseExecutionCancellation(queued.Ticket);
            }
            if (housekeeping is not null) await housekeeping.ConfigureAwait(false);
            BeforeShutdownUnsubscribeForTests?.Invoke();
            _events.Unsubscribe(_wakeOnEngineState);
            foreach (var engine in generation.Engines.Values) await engine.DisposeAsync().ConfigureAwait(false);
            _shutdown.Dispose();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            return;
        }
        completion.TrySetResult();
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
    ThreadStats[] Threads)
{
    /// <summary>Feed observer invocations that have thrown since startup (§6.1). A subscriber's exception is
    /// isolated by design — it must not reach the engine that emitted the event — so a broken observer is otherwise
    /// invisible: this count is what tells an operator that a live tail has stopped being delivered.</summary>
    public long FeedSubscriberFaults { get; init; }
}

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
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? QueueWaitReason { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? QueueWaitScope { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public QueueDispatchObservation? QueueDispatch { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpstreamOutcome { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpstreamEndReason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CurrentPolicyRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CurrentSensitivity { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PolicyAdmissionState { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PrivateAttemptMayHaveSent { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AttemptPolicyRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Sensitive { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DestinationTrust { get; init; }
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
    public static byte[] Rewrite(ReadOnlyMemory<byte> body, string model) => Rewrite(body, model, stream: false, out _);

    internal static byte[] Rewrite(ReadOnlyMemory<byte> body, string model, bool stream, out ulong expectedChoices)
    {
        expectedChoices = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            using var ms = new MemoryStream(body.Length + 16);
            using (var writer = new Utf8JsonWriter(ms))
            {
                expectedChoices = WriteWithModel(doc.RootElement, writer, model, stream);
            }
            return ms.ToArray();
        }
        catch
        {
            expectedChoices = 0;
            return body.ToArray();
        }
    }

    private static ulong WriteWithModel(JsonElement element, Utf8JsonWriter writer, string model, bool stream)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            element.WriteTo(writer);
            return 0;
        }
        writer.WriteStartObject();
        var wrote = false;
        var expectedChoices = stream ? 1UL : 0UL;
        var sawChoices = false;
        foreach (var p in element.EnumerateObject())
        {
            if (stream && p.NameEquals("n"))
            {
                expectedChoices = sawChoices ? 0 : SemanticStreamObserver.ExpectedChoices(p.Value);
                sawChoices = true;
            }
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
        return expectedChoices;
    }
}

internal static class QueuedRequestExtensions
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
