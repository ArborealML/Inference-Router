using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
                        ExactTicketAdmission? admission = null, ReloadConfiguration? reload = null, string? nativeResetStartupInstance = null)
    {
        config.ValidateLifecycleTopology();
        _shutdownToken = _shutdown.Token;
        _config = config;
        _lifecycleFactory = lifecycleFactory;
        _nativeResetStartupInstance = nativeResetStartupInstance;
        _clock = clock;
        _wakeOnEngineState = WakeOnEngineState;
        _scheduler = scheduler ?? new PrefixAwareScheduler();
        _events = events ?? new EventRing(maxLiveSubscribers: config.MaxEventSubscribers);
        reload?.ValidateNames();
        if (config.Engines.Values.Any(static definition => definition.LifecycleGroup is not null) &&
            admission is not null)
            throw new RouterConfigException("Local lifecycle groups require ordinary queue admission.");
        var engines = new Dictionary<string, EngineRuntime>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, def) in config.Engines)
            // The dispatch hint closes the one wake gap the event feed leaves: switch outcome events are
            // emitted before the capability transition commits, so a dispatcher woken by them still sees the
            // engine marked as switching. Committing the transition nudges the loop again (§5.5).
            engines[name] = CreateOrdinaryEngine(def, config, reload);
        var groups = new Dictionary<string, LocalLifecycleGroup>(StringComparer.Ordinal);
        foreach (var engine in engines.Values)
        {
            if (engine.Definition.LifecycleGroup is not { } groupName) continue;
            if (!groups.TryGetValue(groupName, out var group))
                groups.Add(groupName, group = new(groupName, SignalDispatch));
            group.Attach(engine);
            _localGroups[engine.Name] = group;
        }
        foreach (var (id, td) in config.Threads)
            _threads[id] = new RouterThread(id, td.Priority, td.Weight, td.ModelAffinity);
        _generation = new ConfigurationGeneration(1, config, reload, engines);
        _admission = admission;
        admission?.Attach(engines.Count, _clock, SignalDispatch, ReportAdmissionCleanupFailure, RejectVerifiedBinding);
    }

    private EngineRuntime CreateOrdinaryEngine(EngineDefinition definition, RouterConfig settings, ReloadConfiguration? source) =>
        new(definition, settings, _events, _config.HttpHandlerFactory?.Invoke(definition),
            _lifecycleFactory?.Invoke(definition), SignalDispatch, hostEnrollment: null, _clock, source,
            localManaged: definition.LifecycleGroup is not null);

    public EventRing Events => _events;
    public RouterConfig Config => CurrentGeneration.File?.Observe() ?? _config;
    public IReadOnlyDictionary<string, EngineRuntime> Engines => CurrentGeneration.Engines;
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

    public RouterStats GetStats() => GetStats(includeRequests: false);

    public RouterStats GetStats(bool includeRequests)
    {
        lock (_gate)
        {
            TicketSnapshot[] outstanding = [];
            if (includeRequests)
            {
                lock (_ticketGate)
                    outstanding = _tickets.Values.Where(t => !t.IsTerminal).Select(SnapshotTicket).ToArray();
            }
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
                    Requests = !includeRequests ? null : outstanding.Where(t => t.Engine == e.Name ||
                        (t.Engine is null && e.CanServeModel(t.Model, cap) && CanFitTicket(e, t))).ToArray(),
                };
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
            return new RouterStats(Engines: engines, QueueDepths: queues, Threads: threads)
            {
                FeedSubscriberFaults = _events.SubscriberFaults,
            };
        }
    }

    private static bool CanFitTicket(EngineRuntime engine, TicketSnapshot ticket)
    {
        var profiles = engine.ProfilesForModel(ticket.Model);
        return ticket.ExactProfile is { } name
            ? engine.GetProfile(ticket.Model, name) is { } tier && tier.MaxContext >= ticket.RequiredContext
            : profiles.Count == 0 || profiles.Any(p => p.MaxContext >= ticket.RequiredContext) ||
              engine.Capability.Serving.WindowFor(ticket.Model) >= ticket.RequiredContext;
    }

    /// <summary>One ticket's state for the admin surface (§6). The identifiers here are <b>whole</b>, not the
    /// reporting-length excerpt the feed carries: an operator who reads a thread id or a model id off this response
    /// follows it up — a query, a retry, a cancel — and a cut id is a value that looks valid and matches nothing.
    /// What this response cannot inherit from a request body is an unbounded <b>sentence</b>: <c>Error</c> is capped
    /// where the ticket became terminal, so it arrives here already at reporting length (§10).</summary>
    public TicketSnapshot? TryGetTicket(string ticketId)
    {
        lock (_ticketGate)
        {
            if (!_tickets.TryGetValue(ticketId, out var t)) return null;
            return SnapshotTicket(t);
        }
    }

    private static TicketSnapshot SnapshotTicket(RouterTicket t)
    {
        var state = t.State;
        var steps = t.CompletedSwitchSteps;
        return new TicketSnapshot
        {
            Id = t.Id,
            ThreadId = t.ThreadId,
            ThreadTitle = t.OriginOptions?.ThreadTitle,
            TmuxSession = t.OriginOptions?.TmuxSession,
            PreferredProfile = t.OriginOptions?.PreferredProfile,
            ExactProfile = t.OriginOptions?.Profile,
            RequiredContext = t.RequiredContextTokens,
            DispatchedProfile = t.DispatchedProfile,
            Model = t.Model,
            QueuedAt = t.QueuedAt,
            State = state.ToString(),
            CompletedSwitchSteps = state == TicketState.WaitingForModel && steps >= 0 ? steps : null,
            Engine = t.Engine,
            ErrorCode = t.ErrorCode,
            Error = t.Error,
        };
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

    private readonly DispatchWake _dispatchWake = new();

    private void SignalDispatch() => _dispatchWake.Signal();

    private enum DispatchResult { Dispatched, StateChanged, Parked }

    internal async Task DispatchLoop()
    {
        while (!_shutdownToken.IsCancellationRequested)
        {
            var revision = _dispatchWake.Revision;
            if (DispatchOnce() == DispatchResult.Dispatched) continue;

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
        long Timestamp, DateTimeOffset UtcNow);

    private SwitchComparison? ReadSwitchComparison(ConfigurationGeneration generation)
    {
        if (_scheduler is not PrefixAwareScheduler || !generation.Settings.AutoSwitch || generation.Engines.Count != 1)
            return null;

        var engine = generation.Engines.Values.Single();
        var observation = engine.ReadDispatchObservation();
        if (observation.Inflight != 0 || !observation.HasCapacity ||
            engine.ReadVerifiedSwitchIncumbent(observation.Capability) is not { } incumbent)
            return null;

        return new SwitchComparison(engine, observation, incumbent, _clock.GetTimestamp(), _clock.GetUtcNow());
    }

    private readonly record struct QueueHead(ModelQueue Queue, QueueDispatchCandidate Candidate);
    private readonly record struct Selection(QueueHead Head, SchedulerDecision Decision);
    private enum TakeResult { Taken, Changed, Ineligible, Terminal }
    private readonly record struct AdmissionTake(TakeResult Result, ExactTicketAdmission.Invocation? Invocation = null,
        string? TerminalCode = null);

    internal bool TryDispatchOnce() => DispatchOnce() != DispatchResult.Parked;

    private DispatchResult DispatchOnce()
    {
        _admission?.ResumePendingOpeningOwnerPreparations();

        QueueHead[] heads;
        ConfigurationGeneration generation;
        lock (_gate)
        {
            if (_closing) return DispatchResult.Parked;
            generation = CurrentGeneration;
            heads = [.. _queues.Values
                .Select(static queue => new QueueHead(queue, queue.SnapshotCandidate(queue.CreationOrdinal, HasThreadCapacity)))
                .OrderBy(static head => head.Candidate.Depth)
                .ThenBy(static head => head.Candidate.BaselineOrdinal)];
        }

        var comparison = ReadSwitchComparison(generation);
        for (var next = 0; next < heads.Length;)
        {
            var head = heads[next++];
            if (head.Candidate.Request is not { } request) continue;
            var infos = BuildEngineInfos(request, generation, comparison);
            var decision = _scheduler.SelectNext(request,
                new SchedulerView(infos, comparison?.UtcNow ?? _clock.GetUtcNow()), generation.Settings);
            if (decision.Engine is null)
            {
                MaybeFailDeadline(request);
                continue;
            }

            var selection = new Selection(head, decision);
            var compared = comparison is not null && decision.SwitchRequired;
            if (comparison is { } cost && compared)
                selection = OrderEligibleSwitches(heads, ref next, selection, request, cost, generation);

            var candidate = selection.Head.Candidate.Request!;
            var engine = generation.Engines[selection.Decision.Engine!];
            var observation = comparison?.Observation ?? FindDispatchObservation(infos, engine.Name);
            var explicitMinimum = candidate.ExplicitContextMinimum;
            var profile = engine.GetProfile(candidate.Ticket.Model, selection.Decision.TargetProfile);
            if (candidate.Request.Options?.Profile is { } named &&
                (profile?.Name != named || profile.MaxContext < candidate.RequiredContextTokens ||
                 (!selection.Decision.SwitchRequired && !observation.Capability.Serving.Matches(candidate.Ticket.Model, profile)) ||
                 (selection.Decision.SwitchRequired && !_localGroups.ContainsKey(engine.Name) && (engine.Inflight != 0 || engine.InferenceUses != 0))))
            {
                MaybeFailDeadline(candidate);
                continue;
            }
            var requiredWindow = EngineRuntime.RequiredWindow(profile, explicitMinimum);
            var needsCurrentObservation = comparison is not null || requiredWindow > 0 ||
                selection.Decision.TargetProfile is not null ||
                (candidate.RequiredContextTokens > 0 && engine.ProfilesForModel(candidate.Ticket.Model).Count > 0);
            EngineRuntime.DispatchObservation? expected = needsCurrentObservation ? observation : null;
            if (!(expected?.HasCapacity ?? engine.HasCapacity)) continue;
            var proof = selection.Decision.SwitchRequired ? null : observation.Capability.SwitchProof;

            AdmissionTake take;
            if (_admission is { } admission)
            {
                var route = new ExactTicketAdmission.Route(engine.Name, candidate.Ticket.Model, selection.Decision.TargetProfile);
                if (admission.Capture(candidate, route, selection.Decision.SwitchRequired, explicitMinimum) is not { } claim)
                    continue;
                if (compared) BeforeCostCommitForTests?.Invoke();
                if (candidate.QueueExpired(_clock))
                {
                    MaybeFailDeadline(candidate);
                    continue;
                }

                bool prepared;
                lock (admission.Gate)
                {
                    if (claim.Resident is not null &&
                        (!admission.IsOpen(claim) || !VerifiedCapacityFits(claim, observation.Capability, requiredWindow) ||
                         !admission.CanTake(claim, engine.ReadDispatchObservation())))
                        continue;
                    prepared = admission.IsPrepared(claim) && admission.IsOpen(claim);
                }
                if (!prepared)
                {
                    BeforeAdmissionPreparationForTests?.Invoke(candidate.Ticket);
                    admission.Prepare(claim);
                    continue;
                }

                BeforeAdmissionCommitForTests?.Invoke(candidate.Ticket);
                lock (admission.Gate)
                {
                    take = TryTake(selection, engine, expected, requiredWindow, claim, generation);
                }
            }
            else
            {
                if (compared) BeforeCostCommitForTests?.Invoke();
                if (requiredWindow > 0 &&
                    observation.Capability.Serving.WindowFor(candidate.Ticket.Model) < requiredWindow &&
                    !WillWaitForModel(generation.Settings, selection.Decision.SwitchRequired, profile))
                {
                    MaybeFailDeadline(candidate);
                    continue;
                }
                BeforeAdmissionCommitForTests?.Invoke(candidate.Ticket);
                take = TryTake(selection, engine, expected, requiredWindow, claim: null, generation);
            }

            if (take.Result == TakeResult.Taken)
            {
                StartSelected(candidate, engine, generation.Settings, selection.Decision.SwitchRequired, profile, proof, take.Invocation);
                return DispatchResult.Dispatched;
            }
            if (take.Result == TakeResult.Terminal)
            {
                SettleQueueEnd(candidate.Ticket, take.TerminalCode!, candidate.Request.Options?.MaxQueueTime);
                continue;
            }
            if (take.Result == TakeResult.Changed) return DispatchResult.StateChanged;
            if (take.Result == TakeResult.Ineligible) MaybeFailDeadline(candidate);
        }
        return DispatchResult.Parked;
    }

    private AdmissionTake TryTake(Selection selection, EngineRuntime engine, EngineRuntime.DispatchObservation? expected,
                                  long requiredWindow, ExactTicketAdmission.Claim? claim, ConfigurationGeneration generation)
    {
        var head = selection.Head;
        var candidate = head.Candidate.Request!;
        lock (_gate)
        lock (head.Queue.Gate)
        {
            if (_closing || _shutdownToken.IsCancellationRequested) return new(TakeResult.Ineligible);
            if (!ReferenceEquals(CurrentGeneration, generation) ||
                !ReferenceEquals(generation.Engines.GetValueOrDefault(engine.Name), engine))
                return new(TakeResult.Changed);
            if (!ReferenceEquals(_queues.GetValueOrDefault(candidate.Ticket.Model), head.Queue))
                return new(TakeResult.Changed);
            if (candidate.Ticket.PendingEnqueuePublication || candidate.Ticket.IsTerminal)
                return new(TakeResult.Changed);
            if (claim is { } stamped && _admission is { } admission)
            {
                if (!admission.IsOpen(stamped) || !admission.IsPrepared(stamped)) return new(TakeResult.Ineligible);
                var observation = engine.ReadDispatchObservation();
                if (!observation.HasCapacity || !observation.Capability.Healthy ||
                    observation.Capability.SwitchingToward is not null ||
                    !engine.CanServeModel(candidate.Ticket.Model, observation.Capability))
                    return new(TakeResult.Ineligible);

                if (candidate.Request.Options?.Profile is { } requested &&
                    (engine.GetProfile(candidate.Ticket.Model, requested) is not { } tier ||
                     !observation.Capability.Serving.Matches(candidate.Ticket.Model, tier) ||
                     stamped.Resident is { } resident && (resident.Target.Tier != requested ||
                         resident.MeasuredWindow != tier.MaxContext || resident.TotalSlots != tier.MaxSlots)))
                    return new(TakeResult.Ineligible);
                if (!AdmissionWindowFits(admission, stamped, observation.Capability, requiredWindow) ||
                    !admission.CanTake(stamped, observation))
                    return new(TakeResult.Ineligible);
            }

            if (expected is { } observationAtSelection && !engine.IsDispatchObservationCurrent(observationAtSelection))
                return new(TakeResult.Changed);
            if (!engine.HasCapacity) return new(TakeResult.Ineligible);
            var uses = _profileUsers.GetValueOrDefault(engine);
            var named = candidate.HasProfileSelection;
            if ((uses.Switching != 0 && (named || uses.Named != 0)) ||
                ((uses.Named != 0 || engine.HasNamedUse) && selection.Decision.SwitchRequired))
                return new(TakeResult.Ineligible);
            var index = head.Queue.FindExactTakeIndexUnderGate(head.Candidate, HasThreadCapacity);
            if (index < 0) return new(TakeResult.Changed);
            var takenAt = _clock.GetTimestamp();
            var terminal = ReadQueueEnd(candidate);
            if (terminal is null && _localGroups.TryGetValue(engine.Name, out var local))
            {
                var selectedProfile = engine.GetProfile(candidate.Ticket.Model, selection.Decision.TargetProfile);
                var lease = local.TryReserve(candidate, engine, selectedProfile, selection.Decision.SwitchRequired,
                    new OwnerSwitchBudget(_clock, takenAt,
                        generation.Settings.Scheduler.SwitchWaitTimeoutMs));
                if (lease is null) return new(TakeResult.Ineligible);
                candidate.LocalLease = lease;
            }
            if (terminal is null && named && (expected is not { } current || !engine.TryReserveNamedUse(current)))
            {
                candidate.LocalLease?.Release();
                candidate.LocalLease = null;
                return new(TakeResult.Changed);
            }
            head.Queue.RemoveExactTakeUnderGate(index);
            RetireQueueIfEmpty(head.Queue);
            if (terminal is not null) return new(TakeResult.Terminal, TerminalCode: terminal);

            var invocation = claim is { } exact ? _admission!.Authorize(exact, engine) : null;
            candidate.ReservedSwitch = selection.Decision.SwitchRequired;
            if (named || candidate.ReservedSwitch)
                _profileUsers[engine] = (uses.Named + (named ? 1 : 0), uses.Switching + (candidate.ReservedSwitch ? 1 : 0));
            candidate.Thread.IncrementInflight();
            engine.BeginRequest(candidate.PromptChars);
            return new(TakeResult.Taken, invocation);
        }
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
            claim.Ticket.RevokeAdmission();
        }
        if (expired)
            Fail(claim.Ticket, "deadline_exceeded", "The queued request expired before verified admission.");
        else
            Fail(claim.Ticket, "engine_unreachable", new VerifiedDispatchBindingException(refusal).Message);
        SignalDispatch();
    }

    private void StartSelected(QueuedRequest candidate, EngineRuntime engine, RouterConfig settings, bool requiresSwitch,
                               ServingProfile? targetProfile, SwitchServingProof? servingProof,
                               ExactTicketAdmission.Invocation? invocation)
    {
        var ticket = candidate.Ticket;
        var publishedSelection = false;
        try
        {
            ticket.EndQueueWait();
            var selectedAt = _clock.GetTimestamp();
            var switchRequired = invocation is null && requiresSwitch;
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
                ThreadId = ticket.ThreadId,
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
            candidate.Thread.DecrementInflight();
            Fail(candidate.Ticket, "cancelled", "router is shutting down");
            candidate.LocalLease?.Release();
            ReleaseExecutionCancellation(candidate.Ticket);
            SignalDispatch();
        }
    }

    private void EndEngineRequest(QueuedRequest candidate, EngineRuntime engine,
                                         ExactTicketAdmission.Invocation? invocation)
    {
        var named = candidate.HasProfileSelection;
        if (named) engine.ReleaseNamedUse();
        if (named || candidate.ReservedSwitch)
        {
            lock (_gate)
            {
                var uses = _profileUsers[engine];
                uses.Named -= named ? 1 : 0;
                uses.Switching -= candidate.ReservedSwitch ? 1 : 0;
                if (uses == default) _profileUsers.Remove(engine);
                else _profileUsers[engine] = uses;
            }
        }
        if (invocation is null)
            engine.EndRequest(candidate.PromptChars);
        else
            invocation.EndOrdinaryUse(engine, candidate.PromptChars);
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

    private Selection OrderEligibleSwitches(QueueHead[] heads, ref int next, Selection baseline,
                                            QueuedRequest first, SwitchComparison comparison, ConfigurationGeneration generation)
    {
        var order = new SwitchCostOrder<Selection>();
        Consider(first, baseline);
        while (next < heads.Length)
        {
            var head = heads[next];
            if (head.Candidate.Depth != baseline.Head.Candidate.Depth) break;
            if (head.Candidate.Request is not { } request)
            {
                next++;
                continue;
            }
            var decision = _scheduler.SelectNext(request,
                new SchedulerView(BuildEngineInfos(request, generation, comparison), comparison.UtcNow), generation.Settings);
            if (decision.Engine is not null &&
                (!decision.SwitchRequired || request.PriorityNode.Priority.CompareTo(first.PriorityNode.Priority) != 0))
                break;
            next++;
            if (decision.Engine is not null) Consider(request, new Selection(head, decision));
        }
        return order.Selected ?? baseline;

        void Consider(QueuedRequest request, Selection selection)
        {
            var estimate = comparison.Engine.EstimateVerifiedTransition(comparison.Incumbent, request.Ticket.Model,
                selection.Decision.TargetProfile, comparison.Timestamp);
            var age = _clock.GetElapsedTime(request.EnqueuedAtTimestamp, comparison.Timestamp);
            order.Consider(selection, request.EnqueuedSeq, age, estimate);
        }
    }

    private static bool WillWaitForModel(RouterConfig settings, bool switchRequired, ServingProfile? profile) =>
        settings.AutoSwitch && (switchRequired || profile is not null);

    private static EngineRuntime.DispatchObservation FindDispatchObservation(EngineRuntimeInfo[] infos, string engine)
    {
        foreach (var info in infos)
            if (info.Name == engine) return info.DispatchObservation;
        throw new InvalidOperationException("The selected engine is missing from the scheduler view.");
    }

    private EngineRuntimeInfo[] BuildEngineInfos(QueuedRequest head, ConfigurationGeneration generation,
        SwitchComparison? comparison)
    {
        var messages = head.Messages();
        var list = new List<EngineRuntimeInfo>(generation.Engines.Count);
        foreach (var e in generation.Engines.Values)
        {
            // One snapshot per engine, and every capability-shaped field derived from that one read: the
            // loaded list, the switch marker and the eligibility bit cannot come from different instants and
            // then disagree inside one scheduling decision (§5.5).
            var observation = comparison?.Observation ?? e.ReadDispatchObservation();
            var cap = observation.Capability;
            list.Add(new EngineRuntimeInfo
            {
                Name = e.Name,
                LoadedModels = _localGroups.TryGetValue(e.Name, out var local) &&
                    !local.IsResident(e.Name, head.Ticket.Model) ? [] : cap.ModelRoster,
                CanLoad = local is not null,
                CanSwitchProfile = head.HasProfileSelection && e.CanSwitchTo(head.Ticket.Model),
                CatalogModels = e.CatalogModelNames.ToArray(),
                SwitchingToward = cap.SwitchingToward is null ? [] : [cap.SwitchingToward],
                CanServeModel = e.CanServeModel(head.Ticket.Model, cap),
                Inflight = observation.Inflight,
                InflightTokenProxy = observation.InflightTokenProxy,
                Healthy = cap.Healthy,
                Switching = cap.SwitchingToward is not null,
                MatchedPrefixChars = e.MatchedPrefixChars(messages),
                ActiveContextWindow = cap.Serving.WindowFor(head.Ticket.Model),
                ServingProfiles = e.ProfilesForModel(head.Ticket.Model),
                DispatchObservation = observation,
            });
        }
        return list.ToArray();
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
                        exactProfile: candidate.Request.Options?.Profile is not null ||
                            (switchRequired && targetProfile is not null && targetProfile.Name == candidate.Request.Options?.PreferredProfile)).ConfigureAwait(false);
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
            var ownsForward = false;
            try
            {
                var executionToken = execCts.Token;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, executionToken, candidate.CallerToken);
                var body = ModelRewriter.Rewrite(candidate.Request.Body, engine.CanonicalModel(ticket.Model) ?? ticket.Model);
                var suffix = LooksLikeCompletion(candidate.Request.Body) ? "completions" : "chat/completions";

                if (invocation is not null)
                {
                    using var request = MakeUpstreamRequest(engine, body, suffix, ticket.ThreadId);
                    await invocation.RunAsync(request, response =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        return FinishWithResponseAsync(engine, ticket, response, candidate, gate: null, cts.Token,
                            executionToken);
                    }, ReportExecutionFailure, cts.Token, invocation.VerifiedCredentialEpoch).ConfigureAwait(false);
                    return;
                }

                if (!engine.TryBeginForward(cts.Token, candidate.HasProfileSelection))
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
                    await FinishWithResponseAsync(engine, ticket, res, candidate, gate: null, cts.Token,
                        executionToken).ConfigureAwait(false);
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
                                await FinishWithResponseAsync(engine, ticket, res, candidate, gate, cts.Token,
                                    executionToken).ConfigureAwait(false);
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
                            _ = DisposeLateForwardAsync(engine, send, candidate);
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

                        Fail(ticket, "engine_timeout",
                            $"engine {WireText.Ident(engine.Name)} sent no response to {WireText.Ident(ticket.Model)} within " +
                            $"{responseTimeout.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
                        return;
                    }
                }
            }
            finally
            {
                if (ownsForward) engine.EndForward(candidate.HasProfileSelection);
                engine.RecordPrefix(candidate.Messages());
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
            else Fail(ticket, "engine_unreachable", error.Message);
        }

        void ReleaseLogical()
        {
            EndEngineOnce();
            if (!optInOwnsInvocation)
                ReleaseExecutionCancellation(ticket);
            DecrementOnce();
            SignalDispatch();          // wake dispatch AFTER both capacity counts are released
        }
    }

    private void ReportAdmissionCleanupFailure(ExactTicketAdmission.Claim claim, Exception error) =>
        _events.Append(new RouterEvent
        {
            Kind = "request.cleanup_failed",
            ThreadId = claim.Ticket.ThreadId,
            TicketId = claim.Ticket.Id,
            Engine = claim.Target.Engine,
            Model = claim.Target.Model,
            Reason = error.Message,
        });

    private static async Task DisposeLateForwardAsync(EngineRuntime engine, Task<HttpResponseMessage> send,
        QueuedRequest candidate)
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
            engine.EndForward(candidate.HasProfileSelection);
            candidate.LocalLease?.Release();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        EngineRuntime engine, byte[] body, string suffix, QueuedRequest candidate, SwitchServingProof? proof, CancellationToken ct)
    {
        using var req = MakeUpstreamRequest(engine, body, suffix, candidate.Ticket.ThreadId);
        engine.ObserveFirstForward(proof, candidate.Ticket.Model, ct);
        return await engine.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage MakeUpstreamRequest(EngineRuntime engine, byte[] body, string suffix, string threadId)
    {
        // The suffix is composed against the endpoint path (EngineRuntime.Combine), so an endpoint
        // mounted under /v1 keeps it.
        var req = new HttpRequestMessage(HttpMethod.Post, engine.CompletionUri(suffix));
        if (engine.ApiKey is not null)
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {engine.ApiKey}");
        if (engine.SendsThreadCacheHint && !string.IsNullOrWhiteSpace(threadId) && threadId != "anon")
            req.Headers.Add("X-Inference-Thread", ThreadCacheHint(engine.Name, threadId));
        req.Content = new ByteArrayContent(body);
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
    private async Task FinishWithResponseAsync(EngineRuntime engine, RouterTicket ticket,
        HttpResponseMessage res, QueuedRequest candidate, ResponseGate? gate, CancellationToken ct,
        CancellationToken? defaultExecutionToken = null)
    {
        // The body is the answer, so the cap covers the body: linked with the caller's token so an abort
        // still ends as cancelled rather than as a timeout.
        using var bodyLink = gate is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, gate.Token);
        var bodyCt = bodyLink?.Token ?? ct;

        if (!res.IsSuccessStatusCode)
        {
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

        if (candidate.Request.Stream)
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
            var error = await PumpStreamAsync(engine, ticket, res.Content, channel, gate, ct, shutdown: shutdown)
                .ConfigureAwait(false);
            // The ticket's terminal transition closes the channel after the safe prefix is queued.
            if (shutdown?.Qualified == true) Fail(ticket, "cancelled", "cancelled", defaultStreamReadShutdown: true);
            else if (error is null) Complete(ticket);
            else Fail(ticket, "engine_error", error);
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

        public ResponseGate(TimeSpan dueIn, TimeProvider clock) => _cts = new CancellationTokenSource(dueIn, clock);
        public CancellationToken Token => _cts.Token;
        public void Pass() { Interlocked.Exchange(ref _passed, 1); _cts.Cancel(); }
        public bool Tripped => Volatile.Read(ref _passed) == 0 && _cts.IsCancellationRequested;
        public void Dispose() => _cts.Dispose();
    }

    private async Task<string?> PumpStreamAsync(EngineRuntime engine, RouterTicket ticket, HttpContent content,
        Channel<ReadOnlyMemory<byte>> channel, ResponseGate? gate, CancellationToken ct, StreamReadLifetime? lifetime = null,
        DefaultStreamShutdown? shutdown = null)
    {
        var firstToken = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // The response ceiling applies to the first raw byte. The first complete SSE block can
        // arrive later, after the gate is disarmed, while the HTTP relay still sends keepalives.
        using var firstReadLink = gate is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, gate.Token);
        var firstReadCt = firstReadLink?.Token ?? ct;
        var buffer = new byte[16384];
        var blocks = new SseBlockBuffer();
        // Stream acquisition is inside the ceiling too: with ResponseHeadersRead the content object exists
        // before any byte does, and a handler that builds the stream lazily must not be able to stall the
        // request before the first gated read happens.
        var stream = lifetime is null
            ? await content.ReadAsStreamAsync(firstReadCt).ConfigureAwait(false)
            : await lifetime.AcquireAsync(content, firstReadCt).ConfigureAwait(false);
        using var ownedStream = lifetime is null ? stream : null;
        var delivery = ticket.HttpDelivery;
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
                if (!firstToken && gate is not null && gate.Tripped) throw;
                break;
            }
            catch (Exception error) when (arm != 0)
            {
                shutdown!.ObserveResult(arm, 0, error);
                throw;
            }
            if (arm != 0 && shutdown!.ObserveResult(arm, read)) return null;
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
            var (complete, limitExceeded) = blocks.Append(buffer.AsSpan(0, read));
            if (!complete.IsEmpty)
            {
                if (delivery is null)
                    await channel.Writer.WriteAsync(complete, ct).ConfigureAwait(false);
                else
                    await delivery.PublishAsync(channel.Writer, complete, ct).ConfigureAwait(false);
                lifetime?.ObservePublished(complete.Span);
                if (shutdown is not null) shutdown.PublishedBlock = true;
            }
            if (limitExceeded)
            {
                ct.ThrowIfCancellationRequested();
                return "upstream SSE block exceeded 1 MiB before its blank delimiter";
            }
        }

        // Caller cancellation takes precedence over classifying the upstream ending.
        ct.ThrowIfCancellationRequested();
        return blocks.HasIncompleteBlock ? "upstream SSE stream ended before a blank delimiter" : null;
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
            ThreadId = ticket.ThreadId,
            TicketId = ticket.Id,
            Engine = ticket.Engine,
            Model = ticket.Model,
        });
        ticket.SignalTerminalStream();
        ticket.StreamChannel?.Writer.TryComplete();
        ticket.CompletionTcs.TrySetResult(body ?? []);
        SignalDispatch();
    }

    private void Fail(RouterTicket ticket, string code, string message, bool suppressIdleTerminal = false,
        bool defaultStreamReadShutdown = false)
    {
        if (ticket.HttpDelivery is { } delivery)
        {
            if (ticket.IsTerminal) return;
            var outcome = delivery.Decide(new HttpStreamOutcome(code, message, suppressIdleTerminal), execution: ticket.ExecCts);
            if (outcome.Code is not { } chosenCode) return;
            code = chosenCode;
            message = outcome.Message;
            suppressIdleTerminal = outcome.SuppressIdleTerminal;
        }
        if (!ticket.TryEnterTerminal()) return;
        ticket.SuppressIdleTerminal = suppressIdleTerminal;
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
            ThreadId = ticket.ThreadId,
            TicketId = ticket.Id,
            Engine = ticket.Engine,
            Model = ticket.Model,
            Reason = message,
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
        lock (_gate) return _threads.GetValueOrDefault(ticket.ThreadId);
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
                if (queued.Ticket.PendingEnqueuePublication)
                    queued.Ticket.DeferredQueueEnd ??= "cancelled";
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
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TicketSnapshot[]? Requests { get; init; }
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
    public string? ThreadTitle { get; init; }
    public string? TmuxSession { get; init; }
    public string? PreferredProfile { get; init; }
    public string? ExactProfile { get; init; }
    public long RequiredContext { get; init; }
    public string? DispatchedProfile { get; init; }
    /// <summary>Completed local switch milestones out of four, present while the owner waits.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletedSwitchSteps { get; init; }
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
