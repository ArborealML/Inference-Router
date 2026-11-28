using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

/// <summary>Runtime for one configured engine: health probing, loaded-model detection,
/// prefix trie, switch execution (serialized per engine), busy-ness accounting.</summary>
public sealed partial class EngineRuntime : IAsyncDisposable, ICapabilitySource
{
    private readonly EngineDefinition _def;
    private readonly ReloadConfiguration? _reloadSource;
    private readonly Dictionary<string, EngineModelDefinition> _catalog;
    private readonly EventRing _events;
    private readonly TimeProvider _clock;
    private readonly Guid _runtimeEpoch = Guid.NewGuid();
    private readonly SwitchAttemptHistory _switchHistory;
    private long _switchSequence;
    private readonly HttpClient _http;
    private readonly bool _ownedPrivateTransport;
    private readonly HttpMessageHandler? _ownedHandler;   // disposed with the client
    private readonly string? _apiKey;
    internal bool SendsThreadCacheHint { get; }
    private readonly PrefixTrie _trie;
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly LifecycleBinding? _lifecycleBinding;
    private readonly EngineLifecycleCoordinator? _lifecycle;
    private readonly int _maxInflight;

    /// <summary>The one place health, reported models and the switch marker live. Written only by Publish*,
    /// which publish a whole snapshot through a CAS loop, so a reader never gets a state no writer ever
    /// wrote and never sees data change without its version moving with it.</summary>
    private EngineCapability _cap = EngineCapability.Initial;
    private SwitchState _switchState = new();   // guarded by the transition gate

    // Test seam for switch-start ordering: invoked inside the transition gate immediately after the switch
    // state is installed, so a test can read what the shared snapshot said at the exact moment the engine was
    // claimed. Null everywhere else; one null check on a path that runs once per switch.
    internal Action? AfterClaimForTests;

    // Runs inside the transition gate after the capability marker is raised, before private state is installed.
    internal Action? AfterSwitchMarkerRaisedForTests;

    /// <summary>More ordering seams, null everywhere outside tests (one null check each, on a once-per-switch
    /// path). <see cref="BeforeSwitchWaitForTests"/> runs on the way to waiting for the claim, which is where a
    /// second caller has to be parked for single-flight claiming to be testable without a sleep. The argument to
    /// <see cref="AfterEndCommitForTests"/> says whether the call happens while the transition gate is held: it
    /// is false for a normal switch end *and* for the rollback of a failed claim, which is the ordering those two
    /// paths must keep — the readiness settle and the dispatch nudge do not belong inside a lock that every later
    /// claim queues behind. <see cref="BeforeScheduleSwitchForTests"/> runs at the scheduling boundary, so a
    /// synchronous failure there is testable; a real thread-pool rejection is not, and is not claimed.</summary>
    internal Action? BeforeSwitchWaitForTests;
    // Runs with the switch semaphore held, before observing the claim under the transition gate.
    internal Action<DateTimeOffset>? AfterSwitchLockAcquiredForTests;
    internal Action<bool>? AfterEndCommitForTests;
    internal Action? BeforeScheduleSwitchForTests;
    // Observes the published proof before the switch clears the marker, outside the transition gate.
    internal Action? AfterSwitchProofForTests;
    // Captures the uncertain snapshot produced after a failed mutation and before marker clear.
    internal Action<EngineCapability>? AfterFailedMutationUnknownForTests;
    // Throws from round setup on demand, outside the scope that can withdraw probe proof.
    internal Action? BeforeModelsFetchForTests;

    /// <summary>Runs on a waiter immediately after it is woken from the readiness wait and **before** it reads
    /// the snapshot that decides what to answer, with the argument saying which wake-up it got: <c>true</c> when
    /// the engine's switch token cancelled the wait, <c>false</c> when the switch settled it. A test parks here to
    /// make the ordering the answer depends on — switch settled, then the waiter decides — a fact it observes
    /// rather than a fact it hopes the scheduler produces. Null outside tests.</summary>
    internal Action<bool>? AfterReadyWaitForTests;
    internal Action<Task>? AfterOutcomeDeliveryWaitForTests;
    internal Action? BeforeReadinessCommitForTests;
    internal TimeProvider ReadinessClockForTests = TimeProvider.System;
    // Overrides the time used to check a finite wait budget after capturing the attempt.
    internal Func<DateTimeOffset, DateTimeOffset>? SwitchWaitTimeForTests;

    /// <summary>Orders claims, switch outcomes and shutdown. It also pairs each switch-state change with its
    /// marker change. The two are read by different consumers — the scheduler's view takes "switching" from the capability marker
    /// (RouterCore.BuildEngineInfos), the switch machinery from <c>_switchState</c> — so they have to move
    /// together: state-installed-but-marker-absent lets a dispatch pass into an engine whose switch has
    /// already been claimed, and marker-cleared-but-state-live tells a dispatcher the engine is free while
    /// a model load is still running. Taken only around await-free work, so it is a short critical section,
    /// and always acquired *after* <c>_switchLock</c>, never the other way round: EnsureLoadedAsync calls
    /// EndSwitch while holding the switch lock, so the reverse order would deadlock it.</summary>
    private readonly object _transitionGate = new();
    // Only the exact claim can withdraw its restoration or the snapshot left by its own serving withdrawal.
    private (EngineCapability Claim, EngineCapability Snapshot)? _restoredIncumbent; // guarded by the transition gate
    // Written once under the transition gate and acquired on the fast path and at the two waits.
    private bool _stopped;
    // Local HTTP owners and deferred disposal share the stop decision's transition gate.
    private int _httpUses;
    private bool _closeHttpWhenIdle;
    internal Func<Task>? AfterForwardAdmissionForTests;
    internal Action? AfterForwardReleaseForTests;
    // Runs outside the gate after publishing the stop, before waking any waiters.
    internal Action? AfterStopDecisionForTests;
    internal Action? AfterLifecycleDisposeForTests;

    /// <summary>Optional nudge to reconsider dispatch once a capability transition commits. The switch
    /// outcome events are emitted *before* the transition commits, so a dispatcher woken by them sees the
    /// marker still up and skips this engine; the marker clear itself emits nothing, so without this the
    /// next look at the queue comes with the next health poll — correct, but up to a poll interval late
    /// (§5.5). Wired to the same signal the request path uses, so there is no new event kind and no change
    /// to any wire surface.</summary>
    private readonly Action? _dispatchHint;

    /// <summary>The token a switch transaction runs on. A switch is an engine-level transaction, not a
    /// per-request one: the request that triggered it can hang up, fail, or give up at its switch ceiling,
    /// and the engine must still finish loading what the router decided to load. So the switch is tied to
    /// this runtime's lifetime, never to a caller's — and it is what stops switches on shutdown.</summary>
    private readonly CancellationTokenSource _switchCts = new();

    // ── serving-profile state (context-window escalation) ───────────────────
    public long ActiveContextWindow => Capability.Serving.ContextWindow;
    public int ActiveSlots => Capability.Serving.TotalSlots;
    public string? ActiveProfile => Capability.Serving.Profile;

    private const int MaxClaimLosses = 8;

    private enum SwitchEnd { Pending, Ready, Failed, RouterStopped }

    private sealed class SwitchState
    {
        public string? Model;
        /// <summary>Set when the switch targets a serving profile (context-window escalation)
        /// rather than a model load; carries "profile:<name>" for observability.</summary>
        public string? Profile;
        public LocalLifecycleGroup.Lease? LocalOwner;
        public InProcessHostManager.Transition? HostTransition;
        public LocalLifecycleGroup.Lease? AdoptionOwner => LocalOwner?.Adopt is null ? null : LocalOwner;
        public LegacyReadinessObservation? AdoptionProbe;
        public ServingProfile? ExactProfile;
        public long RequiredWindow;
        public long CommittedWindow;
        public EngineCapability? IncumbentProof;
        // Read and written under the transition gate. In-flight probes cannot publish a new proof.
        public bool IncumbentContradicted;
        public bool IncumbentServingContradicted;
        public bool ReadOnlyFailureCanRestore;
        public NativeSwitchQuarantine.Target? QuarantineTarget;
        public SwitchResult? NativeFailure;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Set before the volatile terminal verdict is published, and completed after its outcome is delivered.
        public TaskCompletionSource? ReadinessOutcomeDelivered;
        public SwitchAttempt? Timing;
        public SwitchServingProof? Proof;
        private int _end;
        /// <summary>Written under the transition gate before releasing waiters. A waiter can acquire this
        /// decision even if cancellation won the race to wake it.</summary>
        public SwitchEnd End
        {
            get => (SwitchEnd)Volatile.Read(ref _end);
            set => Volatile.Write(ref _end, (int)value);
        }
        /// <summary>The reason category this attempt ended with, written by <see cref="RunSwitchAsync"/> at the
        /// same place it emits the feed event — **before** the readiness task settles, so a waiter always reads
        /// the reason of the attempt it joined. Deliberately not the operator's sentence: that carries provider
        /// text and stays on the feed; a caller gets the category (§6.1, §5.5).</summary>
        public string? FailureReason;
        public override string ToString() => Model is null ? "none" : Model;
    }

    private sealed record LifecycleBinding(EngineLifecycleOwner Owner, string ProviderKind, string EngineName);

    public EngineRuntime(EngineDefinition def, RouterConfig config, EventRing events, HttpMessageHandler? handler = null,
                         IEngineLifecycleProvider? lifecycle = null, Action? dispatchHint = null)
        : this(def, config, events, handler, lifecycle, dispatchHint, hostEnrollment: null)
    {
    }

    internal EngineRuntime(EngineDefinition def, RouterConfig config, EventRing events, HttpMessageHandler? handler,
                           IEngineLifecycleProvider? lifecycle, Action? dispatchHint, HostEngineEnrollment? hostEnrollment,
                           TimeProvider? clock = null, ReloadConfiguration? reloadSource = null, bool localManaged = false,
                           bool ownedPrivateTransport = false)
    {
        if (config.OwnedPrivatePolicy != ownedPrivateTransport || ownedPrivateTransport && handler is not null)
            throw new RouterConfigException("Owned private destinations require the qualified router transport.");
        _ownedPrivateTransport = ownedPrivateTransport;
        if (def.LifecycleGroup is not null && !localManaged)
            throw new RouterConfigException("Local lifecycle engines must be bound through their router group.");
        var manager = hostEnrollment?.Manager;
        config.ValidateLifecycleTopology(def, manager is null ? null : manager.OwnsCommissioned);
        if (!localManaged && def.Docker?.HasUnqualifiedTargetDocker == true)
            throw new RouterConfigException($"engine.{WireText.Ident(def.Name)}: serving-tier Docker targets require host qualification before activation");
        if (!localManaged && def.Docker?.HasUnqualifiedLaunchOptions == true)
        {
            var fields = def.Docker.ContainerPolicy is null
                ? "docker.entrypoint and docker.ipc_mode" : "Docker container policy options";
            throw new RouterConfigException($"engine.{WireText.Ident(def.Name)}: {fields} require host qualification before activation");
        }
        _lifecycleBinding = BindLifecycle(def, lifecycle, localManaged);
        if (hostEnrollment is { } enrollment &&
            (!def.HasExplicitRouterLifecycle || enrollment.Engine != def.Name || enrollment.RouterId != def.Docker?.RouterId ||
             lifecycle is not null))
            throw new RouterConfigException($"engine.{def.Name}: host enrollment does not match the bound lifecycle owner");
        if (hostEnrollment is not null && def.Docker?.Resources is not null && manager?.OwnsCommissioned(def.Name) != true)
            throw new RouterConfigException($"engine.{def.Name}: Docker resources require host qualification before enrollment");
        _hostEnrollment = hostEnrollment;
        if (hostEnrollment is null && _lifecycleBinding?.ProviderKind == DockerLifecycleProvider.ProviderKind &&
            def.Docker is { } docker)
            _nativeQuarantine = new(docker, _runtimeEpoch);
        _def = def;
        _reloadSource = reloadSource;
        _catalog = def.Catalog.ToDictionary(entry => entry.Key, entry => CopyCatalogEntry(entry.Value), StringComparer.Ordinal);
        _events = events;
        _clock = clock ?? TimeProvider.System;
        _switchHistory = new SwitchAttemptHistory(_clock, _runtimeEpoch);
        _dispatchHint = dispatchHint;
        if (_lifecycleBinding is { } binding && _hostEnrollment is null)
        {
            _lifecycle = new EngineLifecycleCoordinator(lifecycle ?? CreateLifecycleProvider(def, binding.Owner, localManaged),
                ownsProvider: lifecycle is null);
        }
        _maxInflight = Math.Max(1, config.Scheduler.MaxInflightPerEngine);
        _trie = new PrefixTrie(config.Scheduler.PrefixTrieMaxNodes);
        _apiKey = ResolveApiKey(def);
        SendsThreadCacheHint = hostEnrollment?.Manager is null && localManaged && def.LifecycleGroup is not null && def.HasExplicitRouterLifecycle &&
            def.Lifecycle == EngineLifecycleKind.Docker && def.Endpoint.IsLoopback;
        if (handler is null)
        {
            _ownedHandler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                AllowAutoRedirect = !SendsThreadCacheHint && !ownedPrivateTransport,
                UseProxy = !SendsThreadCacheHint && !ownedPrivateTransport,
                UseCookies = !ownedPrivateTransport,
            };
            _http = new HttpClient(_ownedHandler, disposeHandler: false);
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: false);   // caller owns the handler
        }
        _http.BaseAddress = def.Endpoint;
        _http.Timeout = TimeSpan.FromMilliseconds(Timeout.Infinite); // long streams; per-request CTS applies
        ResponseTimeout = def.ResponseTimeoutMs <= 0
            ? null
            : TimeSpan.FromMilliseconds(def.ResponseTimeoutMs);
        StreamIdleTimeout = def.StreamIdleTimeoutMs > 0 ? TimeSpan.FromMilliseconds(def.StreamIdleTimeoutMs) : null;
    }

    /// <summary>Ceiling on the wait for this engine's first response byte (null = no ceiling). Worked
    /// out once at startup: the request path must not re-derive config.</summary>
    internal TimeSpan? ResponseTimeout { get; }
    internal TimeSpan? StreamIdleTimeout { get; }

    private static LifecycleBinding? BindLifecycle(EngineDefinition def, IEngineLifecycleProvider? provider, bool localManaged)
    {
        var owner = def.ResolveLifecycleOwner();
        if (owner == EngineLifecycleOwner.External) return null;

        var providerKind = owner == EngineLifecycleOwner.Router
            ? DockerLifecycleProvider.ProviderKind : DelegatedExecProvider.ProviderKind;
        if (provider is not null)
        {
            if (!localManaged && def.Docker?.Resources is not null)
                throw new RouterConfigException($"engine.{WireText.Ident(def.Name)}: Docker resources require host qualification before activation");

            var actualKind = provider.Kind;
            if (!string.Equals(actualKind, providerKind, StringComparison.Ordinal))
                throw new RouterConfigException(
                    $"engine.{WireText.Ident(def.Name)}: injected lifecycle provider kind '{WireText.Ident(actualKind ?? "<null>")}' does not match configured kind '{providerKind}'");
            if (provider is DockerLifecycleProvider docker)
                docker.ValidateScope(def.Name, def.Docker!);
        }

        return new LifecycleBinding(owner, providerKind, def.Name);
    }

    private static IEngineLifecycleProvider CreateLifecycleProvider(EngineDefinition def, EngineLifecycleOwner owner, bool localManaged) =>
        owner == EngineLifecycleOwner.Router
            ? new DockerLifecycleProvider(def.Docker!, def.Name, handler: null, localManaged: localManaged)
            : new DelegatedExecProvider();

    private static EngineModelDefinition CopyCatalogEntry(EngineModelDefinition entry) => entry with
    {
        Aliases = entry.Aliases.ToArray(),
        Profiles = entry.Profiles.ToArray(),
        ServingProfiles = Array.AsReadOnly(entry.ServingProfiles.ToArray()),
        Select = entry.Select is { } select ? new LifecycleCommand
        {
            Exec = select.Exec,
            Args = select.Args.ToArray(),
            WaitHealth = select.WaitHealth,
            WaitTimeoutMs = select.WaitTimeoutMs,
        } : null,
    };

    internal IEngineLifecycleProvider LocalLifecycle => _lifecycle?.Provider ??
        throw new InvalidOperationException("The engine has no local lifecycle provider.");
    internal LifecycleTarget LocalTarget(string model, ServingProfile? profile) =>
        new(Name, model, profile, _catalog[model]);
    internal void WithdrawLocalResident() => PublishUnknown();

    public string Name => _def.Name;
    public EngineDefinition Definition => _reloadSource?.Observe().Engines[Name] ?? _def;
    internal IReadOnlyCollection<string> CatalogModelNames => _catalog.Keys;
    internal IReadOnlyList<RetainedSwitchAttempt> SwitchHistory => _switchHistory.Snapshot();
    public string LifecycleProvider => _lifecycleBinding?.ProviderKind ?? "none";
    /// <summary>Advisory trace of the last lifecycle transactions on this engine: why a switch was
    /// refused, what was stopped, what was launched. Not part of the wire contract.</summary>
    public IReadOnlyList<string> LifecycleJournal => _lifecycle?.Journal ?? [];
    public bool Healthy => Volatile.Read(ref _cap).Healthy;
    public string[] LoadedModels => Capability.LoadedModels;

    /// <summary>This engine's capability claim as one atomic, versioned snapshot. The scheduler's view and
    /// the admission refusal both read it exactly once per engine per decision, so a decision cannot mix an
    /// old model list with a new one (§5.5).</summary>
    public EngineCapability Capability => Volatile.Read(ref _cap);

    /// <summary>Does this engine's configured catalog name the model? Static configuration, so it needs no
    /// snapshot; case-insensitive to match the scheduler's comparison, not the catalog's own ordinal one.</summary>
    public bool DeclaresModel(string model)
    {
        // Loops, not a dictionary lookup: the catalog's comparer is ordinal and model comparison here is
        // case-insensitive, so a lookup would answer the wrong question. Enumerator over Keys is a struct.
        foreach (var name in _catalog.Keys)
            if (string.Equals(name, model, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Does this engine either declare the model in its configured catalog or report it loaded in
    /// <paramref name="cap"/>? Case-insensitive, and deliberately silent about health and switching: those
    /// are separate questions with separate answers. A model the engine serves out-of-band — loaded by hand,
    /// not named in any catalog — counts here, and that is not a concession: the scheduler has always
    /// treated a reported model as eligible (§5.5).</summary>
    public bool CanServeModel(string model, EngineCapability cap)
    {
        if (DeclaresModel(model)) return true;
        foreach (var name in cap.ModelRoster)
            if (string.Equals(name, model, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Convenience for a caller that has no snapshot of its own: takes one, then answers.</summary>
    public bool CanServeModel(string model) => CanServeModel(model, Capability);

    EngineCapability ICapabilitySource.Capability => Capability;
    bool ICapabilitySource.DeclaresModel(string model) => DeclaresModel(model);

    /// <summary>Publish what a model probe learned, and only while nothing has published since the probe was
    /// taken. <paramref name="expectedVersion"/> is read before the request goes out: a probe is a network
    /// round trip, a model switch can land inside it, and writing the older answer afterwards would replace
    /// fresher evidence with stale evidence — the failure the snapshot exists to prevent (§5.5). Dropping a
    /// stale publication costs one poll interval, not an answer: the next tick reads the engine again. The
    /// fence covers both directions of the answer: a probe that could not reach the engine publishes its
    /// health-only negative through <see cref="PublishProbeFailure"/> under the same version, so a poll that
    /// failed before a switch proved the engine cannot withdraw that proof either.</summary>
    private void PublishProbeResult(EngineCapability expected, ModelsObservation observation, ServingCapacity serving)
    {
        if (Volatile.Read(ref _stopped)) return;
        var current = Capability;
        if (!ReferenceEquals(current, expected) || current.SwitchingToward is not null)
        {
            if (IsIncumbentClaim(expected))
                ReconcileIncumbentObservation(expected, observation, serving);
            return;
        }

        var next = WithModelsObservation(expected, observation, serving);
        if (!ReferenceEquals(expected, next) && ReferenceEquals(Interlocked.CompareExchange(ref _cap, next, expected), expected))
            NotifyServingChange(expected.Serving, next.Serving);
    }

    /// <summary>Publish a probe's *negative*: health false, the reported list untouched, and only while
    /// nothing has published since the <paramref name="expected"/> snapshot read before the probe went
    /// out. Same fence as the positive answer for the same reason: the poll interval is short, so losing a
    /// stale negative costs one tick, while applying it marks a working engine unknown for a whole interval
    /// and costs that model its engine (§5.5). A probe carries no list evidence, so the models the engine
    /// last actually reported stay as history; only the entitlement to use them moves.</summary>
    private void PublishProbeFailure(EngineCapability expected)
    {
        if (Volatile.Read(ref _stopped)) return;
        var current = Capability;
        if (!ReferenceEquals(current, expected) || current.SwitchingToward is not null)
        {
            if (IsIncumbentClaim(expected))
                ReconcileIncumbentObservation(expected, observation: null);
            return;
        }

        if (!expected.Healthy) return;
        var unknown = WithoutProbeProof(expected);
        if (ReferenceEquals(Interlocked.CompareExchange(ref _cap, unknown, expected), expected))
            NotifyServingChange(expected.Serving, unknown.Serving);
    }

    private static EngineCapability WithoutProbeProof(EngineCapability current) => current with
    {
        Version = current.Version + 1,
        Healthy = false,
        HealthFromClaim = false,
        ModelContextWindows = EngineCapability.NoModelContextWindows,
        Serving = default,
        SwitchProof = null,
        NativeWatch = UnknownNativeWatch(current.NativeWatch),
    };

    private static bool IsIncumbentClaim(EngineCapability current) =>
        current.SwitchingToward is not null && !current.Healthy && current.HealthFromClaim;

    // An observation on the matching claim can veto restoration or withdraw the exact restored
    // generation. It can never publish a new roster or revoke a successor's proof.
    private void ReconcileIncumbentObservation(EngineCapability expected, ModelsObservation? observation,
        ServingCapacity serving = default)
    {
        lock (_transitionGate)
        {
            if (_stopped) return;
            var current = Capability;
            var state = _switchState;
            if (ReferenceEquals(current, expected) &&
                (state.End == SwitchEnd.Pending || (state.End == SwitchEnd.Failed && state.ReadOnlyFailureCanRestore)) &&
                string.Equals(state.Model, current.SwitchingToward, StringComparison.Ordinal) &&
                state.IncumbentProof is { } incumbent && incumbent.Version + 1 == expected.Version)
            {
                if (ContradictsIncumbent(incumbent, observation)) state.IncumbentContradicted = true;
                else if (incumbent.Serving != serving) state.IncumbentServingContradicted = true;
                return;
            }

            if (_restoredIncumbent is not { } restored ||
                !ReferenceEquals(restored.Claim, expected) || !ReferenceEquals(restored.Snapshot, current)) return;

            EngineCapability next;
            if (ContradictsIncumbent(current, observation))
                next = WithoutProbeProof(current);
            else if (current.Serving != default && current.Serving != serving)
                next = current with { Version = current.Version + 1, Serving = default, SwitchProof = null };
            else
                return;

            if (!ReferenceEquals(Interlocked.CompareExchange(ref _cap, next, current), current)) return;
            _restoredIncumbent = next.Healthy ? (expected, next) : null;
        }
        NudgeDispatch();
    }

    private static bool ContradictsIncumbent(EngineCapability incumbent, ModelsObservation? observation) =>
        observation is not { } answered || CompareModelsObservation(incumbent, answered) != (true, true);

    // Callers hold the transition gate and have checked that this claim is current and pending.
    private bool TryPublishClaimProof(SwitchState state, ModelsObservation observation, ServingCapacity serving,
        ReadinessDeadline? deadline, out ReadinessStepOutcome settlement, NativeLaunchWatch? nativeWatch = null,
        ReadinessDeadline? dockerDeadline = null)
    {
        var current = Capability;
        var next = WithModelsObservation(current, observation, serving);
        var timing = state.Timing!;
        var proof = timing.Proof(ReferenceEquals(next, current) ? current.Version + 1 : next.Version);
        next = next with { Version = proof.Version, SwitchProof = proof, NativeWatch = nativeWatch ?? current.NativeWatch };

        settlement = deadline?.OutcomeForSettlement ?? ReadinessStepOutcome.Completed;
        if (settlement == ReadinessStepOutcome.Completed && dockerDeadline is { IsExpired: true })
            settlement = ReadinessStepOutcome.Expired;
        if (settlement != ReadinessStepOutcome.Completed ||
            !ReferenceEquals(Interlocked.CompareExchange(ref _cap, next, current), current)) return false;

        state.Proof = proof;
        state.CommittedWindow = serving.WindowFor(state.Model!);
        timing.TryCompleteStage(SwitchStage.Readiness, succeeded: true);
        return true;
    }

    private static (bool SameRoster, bool SameWindows) CompareModelsObservation(
        EngineCapability current, ModelsObservation observation)
    {
        // A row carrying proof keeps the exact spelling that accompanied that proof. On an unqualified roster,
        // preserve the existing quiet behavior for case-only changes.
        var nameComparer = current.ModelContextWindows.IsEmpty && observation.ContextWindows.IsEmpty
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var sameRoster = current.ModelRoster.SequenceEqual(observation.LoadedModels, nameComparer);
        var sameWindows = current.ModelContextWindows.Count == observation.ContextWindows.Count &&
            current.ModelContextWindows.All(pair =>
                observation.ContextWindows.TryGetValue(pair.Key, out var tokens) && tokens == pair.Value);
        return (sameRoster, sameWindows);
    }

    private static EngineCapability WithModelsObservation(
        EngineCapability current, ModelsObservation observation, ServingCapacity serving)
    {
        var (sameRoster, sameWindows) = CompareModelsObservation(current, observation);
        if (current.Healthy && sameRoster && sameWindows && current.Serving == serving) return current;
        var compatibleProof = current.Healthy && sameRoster &&
            current.Serving.ContextWindow == serving.ContextWindow &&
            string.Equals(current.Serving.Model, serving.Model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.Serving.Profile, serving.Profile, StringComparison.Ordinal);
        return current with
        {
            Version = current.Version + 1,
            Healthy = true,
            ModelRoster = sameRoster ? current.ModelRoster : observation.LoadedModels,
            ModelContextWindows = sameWindows ? current.ModelContextWindows : observation.ContextWindows,
            Serving = serving,
            HealthFromClaim = false,
            SwitchProof = compatibleProof ? current.SwitchProof : null,
        };
    }

    /// <summary>Make this engine's capability unknown: health false, the reported list kept as the last thing
    /// the engine actually said, marker untouched. Published at every switch exit where the router had
    /// already laid hands on the engine — stopped it, launched something, or left it restarting — because
    /// after that the last good probe describes a process that may no longer exist. Unknown is the one
    /// answer that cannot be wrong in either direction: the scheduler skips an unknown engine, and the
    /// admission refusal refuses nothing while health is unknown (§5.5).
    /// <para><b>Deliberately unfenced</b>, unlike a probe's two publications. Every caller is a switch's own
    /// exit, and a switch is the fresher publisher by construction: it just stopped or relaunched this
    /// engine, so its account cannot be stale relative to a poll that happened to start earlier. Fencing it
    /// would let a quiet poll drop the one publication that says the engine is no longer what it reported.
    /// Do not "finish the fencing" here — fence a *probe*, never a switch exit.</para></summary>
    private EngineCapability PublishUnknown()
    {
        while (true)
        {
            var cur = Volatile.Read(ref _cap);
            // Health already false is not always nothing to publish. While a claim is in flight the bit says that
            // claim authored the unknown health, and a switch exit that lays hands on the engine supersedes that
            // claim as the author — a *provenance-only* change, which still has to advance `Version` or a reader
            // could not tell the two states apart (AC #8).
            if (!cur.Healthy && !cur.HealthFromClaim && cur.ModelContextWindows.IsEmpty && cur.Serving == default &&
                cur.SwitchProof is null && cur.NativeWatch?.Health != NativeLaunchHealth.Eligible)
                return cur;
            var next = cur with
            {
                Version = cur.Version + 1,
                Healthy = false,
                HealthFromClaim = false,
                ModelContextWindows = EngineCapability.NoModelContextWindows,
                Serving = default,
                SwitchProof = null,
                NativeWatch = UnknownNativeWatch(cur.NativeWatch),
            };
            if (Interlocked.CompareExchange(ref _cap, next, cur) == cur) return next;
        }
    }

    private void WithdrawDiscoveryOnStop()
    {
        // Fence in-flight polls even if the snapshot is already unknown. The retained roster
        // is historical, and a committed switch delivers its own outcome from the claim.
        while (true)
        {
            var cur = Capability;
            var retired = WithoutProbeProof(cur);
            if (Interlocked.CompareExchange(ref _cap, retired, cur) == cur) return;
        }
    }

    /// <summary>Open a switch in the snapshot — marker up and health unknown in one publication — but only while
    /// the exact <paramref name="expected"/> snapshot still holds the evidence the readiness
    /// question was just answered on. What follows is a mutation of the engine, so its previous answer has to stop
    /// being claimed at the moment the mutation is <i>decided</i>, not after it turns out to have failed. Returns
    /// false when it did not claim. Ordinary probes can publish without the transition gate. If a poll proves
    /// the requested model before the claim, the caller can use that proof on its next attempt (§5.5).</summary>
    private bool TryPublishSwitchStarted(string model, EngineCapability expected)
    {
        var cur = expected;
        // `Healthy` was true in the snapshot this claim is decided on, so this publication is the author of the
        // withdrawal it makes; starting from an already-unknown engine authors nothing (AC #8).
        var next = cur with
        {
            Version = cur.Version + 1,
            SwitchingToward = model,
            Healthy = false,
            HealthFromClaim = cur.Healthy,
            ModelContextWindows = EngineCapability.NoModelContextWindows,
            Serving = default,
            SwitchProof = null,
        };
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _cap, next, cur), cur)) return false;
        _restoredIncumbent = null;
        return true;
    }

    /// <summary>Clear the marker and optionally restore the proof displaced by the matching claim in one publication.
    /// The caller holds the transition gate and still owns the marker.</summary>
    private void ClearSwitchMarker(EngineCapability? incumbent, bool restoreServing)
    {
        while (true)
        {
            var cur = Volatile.Read(ref _cap);
            if (cur.SwitchingToward is null) return;
            var restored = incumbent is not null && cur.HealthFromClaim && cur.Version == incumbent.Version + 1
                ? incumbent : null;
            var serving = cur.Serving;
            var switchProof = cur.SwitchProof;
            if (restored is not null)
            {
                serving = restoreServing ? restored.Serving : default;
                switchProof = restoreServing ? restored.SwitchProof : null;
            }
            var next = cur with
            {
                Version = cur.Version + 1,
                Healthy = cur.Healthy || restored is not null,
                ModelContextWindows = restored?.ModelContextWindows ?? cur.ModelContextWindows,
                Serving = serving,
                SwitchProof = switchProof,
                SwitchingToward = null,
                HealthFromClaim = false,
                NativeWatch = cur.Healthy || restored is not null ? cur.NativeWatch : UnknownNativeWatch(cur.NativeWatch),
            };
            if (Interlocked.CompareExchange(ref _cap, next, cur) != cur) continue;
            _restoredIncumbent = restored is null ? null : (cur, next);
            return;
        }
    }

    public int Inflight => Volatile.Read(ref _inflight);
    private int _inflight;
    private long _admissionClaims;
    internal long AdmissionClaims => Interlocked.Read(ref _admissionClaims);
    public long InflightTokenProxy => Interlocked.Read(ref _tokenProxy);
    private long _tokenProxy;
    public bool Switching => SwitchingToward is not null;
    public string? SwitchingToward => Volatile.Read(ref _cap).SwitchingToward;

    private static string? ResolveApiKey(EngineDefinition def)
    {
        if (def.ApiKey is not null) return def.ApiKey;
        if (def.ApiKeyFile is not null && File.Exists(def.ApiKeyFile))
            try { return File.ReadAllText(def.ApiKeyFile).Trim(); }
            catch { /* unreadable key file: treated as no key */ }
        return def.ApiKey;
    }

    public void StartHealthLoop(CancellationToken ct) { _ = Task.Run(() => RunHealthLoopAsync(ct), ct); }

    internal async Task RunHealthLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(_def.Health?.IntervalMs ?? 2_000);
        using var ticks = CancellationTokenSource.CreateLinkedTokenSource(ct, _switchCts.Token);
        using var timer = new PeriodicTimer(interval, _lifecycle?.NativeProvider is null ? TimeProvider.System : _clock);
        while (!ticks.IsCancellationRequested)
        {
            if (!await ProbeHealthOnceAsync(ct).ConfigureAwait(false)) break;
            try
            {
                if (!await timer.WaitForNextTickAsync(ticks.Token).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<bool> ProbeHealthOnceAsync(CancellationToken ct)
    {
        // Capture the version before either read. The poll may publish only against that evidence.
        var capBefore = Capability;
        try
        {
            BeforeModelsFetchForTests?.Invoke();
            return await ProbeHealthAsync(capBefore, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ReloadableExternal) return PublishExternalProbe(capBefore, null, default, ex, withdraw: false);
            if (Volatile.Read(ref _stopped)) return false;
            Emit("engine.health", reason: WireText.Bound(ex.Message, ProviderReasonChars), value: 0);
            return true;
        }
    }

    private async Task<bool> ProbeHealthAsync(EngineCapability capBefore, CancellationToken loopToken)
    {
        if (capBefore.NativeWatch is { } watch && _lifecycle?.NativeProvider is { } native)
            return UsesVllmArgv
                ? await ProbeManagedProfileAsync(capBefore, watch, native, loopToken).ConfigureAwait(false)
                : await ProbeTrackedNativeAsync(capBefore, watch, native, loopToken).ConfigureAwait(false);
        if (UsesVllmArgv) return !Volatile.Read(ref _stopped);

        // The loop token stops new rounds. An admitted round retains its own timeout and
        // publication fences, including when the loop is cancelled while /models is in flight.
        using var budget = new CancellationTokenSource(_def.Health?.TimeoutMs ?? 5_000);
        if (!TryBeginHttpUse(loopToken)) return false;
        ModelsObservation observation;
        try
        {
            observation = await FetchModelsObservationAsync(budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!loopToken.IsCancellationRequested ||
            ex is not OperationCanceledException canceled || canceled.CancellationToken != loopToken)
        {
            if (ReloadableExternal) return PublishExternalProbe(capBefore, null, default, ex, withdraw: true);
            PublishProbeFailure(capBefore);
            throw;
        }
        finally
        {
            EndHttpUse();
        }

        // Stop can win between the two reads even when another caller still owns the HTTP client.
        if (!TryBeginHttpUse(CancellationToken.None)) return false;
        ServingProbe state;
        try
        {
            state = await ReadServingProbeAsync(budget.Token).ConfigureAwait(false);
        }
        finally
        {
            EndHttpUse();
        }
        var serving = BindServing(observation, state);
        if (ReloadableExternal) return PublishExternalProbe(capBefore, observation, serving, error: null, withdraw: false);
        PublishProbeResult(capBefore, observation, serving);
        if (Volatile.Read(ref _stopped)) return false;
        Emit("engine.health", value: observation.LoadedModels.Count);
        return true;
    }

    private HttpRequestMessage AuthedGet(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Combine(_def.Endpoint, path));
        if (_apiKey is not null) req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        return req;
    }

    /// <summary>The URL for one OpenAI completion call. The suffix is composed against the
    /// endpoint's own path, so `endpoint = "http://host:8080/v1"` reaches /v1/chat/completions —
    /// the base path is not silently dropped (see <see cref="Combine"/>).</summary>
    internal Uri CompletionUri(string suffix) => Combine(_def.Endpoint, suffix);

    internal static string ServingProbePath(EngineDefinition definition) => definition.Health?.Path ?? "/props";

    /// <summary>Compose a configured path against the engine endpoint.
    /// A leading '/' is absolute from the server root ("/health", "/models"); a bare name appends
    /// to the endpoint's path ("chat/completions" under …/v1 → /v1/chat/completions). A '?' in the
    /// value supplies the query string; without one, the endpoint's own query is preserved.</summary>
    internal static Uri Combine(Uri endpoint, string path)
    {
        if (string.IsNullOrEmpty(path)) return endpoint;

        var cut = path.Split('?', 2);
        var pathPart = cut[0];
        var query = cut.Length > 1 ? cut[1] : null;

        var absolute = pathPart.Length > 0 && pathPart[0] == '/';
        var basePath = endpoint.AbsolutePath;
        var joined = absolute
            ? pathPart
            : (basePath.Length > 0 && basePath[^1] == '/' ? basePath : basePath + "/") + pathPart;

        var builder = new UriBuilder(endpoint) { Path = joined };
        builder.Query = query ?? endpoint.Query;
        return builder.Uri;
    }

    private readonly record struct ModelsObservation(
        ImmutableList<string> LoadedModels, ImmutableDictionary<string, long> ContextWindows, string? ServingModel = null);

    private readonly record struct ServingProbe(bool Satisfied, long Window, int Slots, bool Answered = false);

    private async Task<ModelsObservation> FetchModelsObservationAsync(CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var req = AuthedGet(_def.ModelsEndpoint);
        using var res = await _http.SendAsync(req, completion, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"models endpoint {(int)res.StatusCode}");
        var bodyBytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bodyBytes);
        var list = ImmutableList.CreateBuilder<string>();
        var contextPath = _def.ModelsContextWindowPath;
        var windows = contextPath is null ? null : EngineCapability.NoModelContextWindows.ToBuilder();
        var seenIds = contextPath is null ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? servingModel = null;
        var rows = 0;
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object ||
                    !m.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                var model = id.GetString()!;
                rows++;
                // Filtered rows still contribute evidence that an ID is ambiguous.
                var duplicate = seenIds?.Add(model) == false;
                if (duplicate) windows!.Remove(model);
                var alias = m.TryGetProperty("alias_of", out var aliasOf) && aliasOf.ValueKind != JsonValueKind.Null;
                var hasStatus = m.TryGetProperty("status", out var status);
                var loaded = status.ValueKind == JsonValueKind.Object &&
                    status.TryGetProperty("value", out var state) &&
                    state.ValueKind == JsonValueKind.String && state.ValueEquals("loaded");
                if (_def.LoadedRequiresStatusLoaded && (alias || !loaded)) continue;

                list.Add(model);
                if (rows == 1 && !alias && (!hasStatus || loaded)) servingModel = model;
                if (duplicate || windows is null || contextPath is null) continue;
                if (!alias && (!hasStatus || loaded) && !string.IsNullOrWhiteSpace(model) &&
                    ProbeExpect.TryResolvePath(m, contextPath, out var value) &&
                    value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var tokens) && tokens > 0)
                    windows.Add(model, tokens);
            }
        }
        // A duplicate property can conceal another model or context. Retain the roster for
        // timing the opted-in verification window, but withhold the model-bound capacity.
        if (UsesVllmArgv && HasAmbiguousProfileJson(doc.RootElement))
            return new(list.ToImmutable(), EngineCapability.NoModelContextWindows);
        return new(list.ToImmutable(), windows?.ToImmutable() ?? EngineCapability.NoModelContextWindows,
            rows == 1 ? servingModel : null);
    }

    private async Task<ServingProbe> ReadServingProbeAsync(CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            using var req = AuthedGet(ServingProbePath(_def));
            using var res = await _http.SendAsync(req, completion, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return default;
            var bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (UsesVllmArgv && HasAmbiguousProfileJson(root)) return default;
            var window = ReadGauge(root, _def.Health?.ContextPath ?? "n_ctx");
            var slots = (int)Math.Min(int.MaxValue, ReadGauge(root, _def.Health?.SlotsPath ?? "total_slots"));
            var satisfied = _def.Health?.Expect is not { } expect || ProbeExpect.Evaluate(root, expect);
            return new(satisfied, window, slots, Answered: true);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private ServingCapacity BindServing(ModelsObservation observation, ServingProbe probe)
    {
        if (probe.Window <= 0 || observation.ServingModel is not { } model)
            return new(null, 0, probe.Slots, null);

        string? boundModel = null;
        foreach (var canonical in _catalog.Keys)
        {
            if (!string.Equals(canonical, model, StringComparison.OrdinalIgnoreCase)) continue;
            if (boundModel is not null) return new(null, 0, probe.Slots, null);
            boundModel = canonical;
        }
        if (boundModel is null) return new(null, 0, probe.Slots, null);

        string? profile = null;
        string? exact = null;
        var windows = 0;
        var pairs = 0;
        foreach (var candidate in _catalog[boundModel].ServingProfiles)
        {
            if (candidate.MaxContext != probe.Window) continue;
            windows++;
            profile = candidate.Name;
            if (candidate.MaxSlots != probe.Slots) continue;
            pairs++;
            exact = candidate.Name;
        }
        return new(boundModel, probe.Window, probe.Slots, windows == 1 ? profile : pairs == 1 ? exact : null);
    }

    private void NotifyServingChange(ServingCapacity previous, ServingCapacity next)
    {
        if (previous == next || Volatile.Read(ref _stopped)) return;
        EmitServingProfileChange(previous, next);
        NudgeDispatch();
    }

    private void NudgeDispatch()
    {
        if (Volatile.Read(ref _stopped)) return;
        try { _dispatchHint?.Invoke(); }
        catch (Exception) { /* The next poll or switch event can wake dispatch. */ }
    }

    private void EmitServingProfileChange(ServingCapacity previous, ServingCapacity next)
    {
        if (!Volatile.Read(ref _stopped) && next.ContextWindow > 0 &&
            (next.ContextWindow != previous.ContextWindow || next.Profile != previous.Profile))
            Emit("engine.profile", reason: next.Profile is { } profile ? WireText.Ident(profile) : "unrecognized_window",
                value: next.ContextWindow);
    }

    private static long ReadGauge(JsonElement root, string path)
    {
        if (!ProbeExpect.TryResolvePath(root, path, out var leaf)) return 0;
        if (leaf.ValueKind == JsonValueKind.Number)
            return leaf.TryGetInt64(out var numeric) && numeric > 0 ? numeric : 0;
        return leaf.ValueKind == JsonValueKind.String &&
            long.TryParse(leaf.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text) &&
            text > 0 ? text : 0;
    }

    public IReadOnlyList<ServingProfile> ProfilesForModel(string canonical)
        => _catalog.TryGetValue(canonical, out var md) ? md.ServingProfiles : [];

    /// <summary>Smallest declared serving profile that fits requiredTokens (null: no ladder, or none fits).</summary>
    public ServingProfile? FindProfile(string canonical, long requiredTokens)
    {
        ServingProfile? best = null;
        foreach (var p in ProfilesForModel(canonical))
        {
            if (p.MaxContext < requiredTokens) continue;
            if (best is null || p.MaxContext < best.MaxContext) best = p;
        }
        return best;
    }

    internal bool CanSwitchTo(string model) => _lifecycleBinding is { } binding &&
        _catalog.TryGetValue(model, out var entry) &&
        (binding.Owner != EngineLifecycleOwner.Delegated || DelegatedExecProvider.HasUsableSelector(entry));

    internal static ServingProfile? UniqueNamedProfile(IReadOnlyList<ServingProfile> tiers, string name)
    {
        ServingProfile? result = null;
        foreach (var tier in tiers)
        {
            if (tier.Name != name) continue;
            if (result is not null) return null;
            result = tier;
        }
        return result;
    }

    public ServingProfile? GetProfile(string canonical, string? name)
    {
        if (name is null) return null;
        var recipe = _def.Docker?.TargetPlans?.Find(canonical, name);
        return UniqueNamedProfile(ProfilesForModel(recipe?.Model ?? CanonicalModel(canonical) ?? canonical), recipe?.Profile ?? name);
    }

    internal string? DefaultServingTier(string model) => _catalog.GetValueOrDefault(model)?.Profile;

    internal bool SupportsNamedProfile(string model, ServingProfile profile) =>
        (_hostEnrollment?.Manager is not { } manager || manager.Recipe(Name, model, profile.Name) is not null) &&
        (_def.Docker?.TargetPlans?.Find(model, requestedTier: null) is null ||
            _def.Docker.TargetPlans.Find(model, profile.Name) is not null);

    internal bool MatchesNamedResident(string model, ServingProfile profile, EngineCapability capability) =>
        capability.Serving.Matches(model, profile) &&
        (_hostEnrollment?.Manager is { } manager ? manager.MatchesResident(Name, model, profile.Name) :
            MatchesNativeRecipe(model, profile, capability));

    private bool MatchesNativeRecipe(string model, ServingProfile profile, EngineCapability capability)
    {
        var plans = _def.Docker?.TargetPlans;
        if (plans?.Find(model, requestedTier: null) is null) return true;
        return plans.Find(model, profile.Name) is { } requested &&
            capability.NativeWatch is { Health: NativeLaunchHealth.Eligible } watch &&
            watch.Launch.ImageId == _def.Docker?.ImageIdentity?.Id &&
            ReferenceEquals(requested, plans.Find(watch.Launch.Model, watch.Launch.Profile));
    }

    /// <summary>How a switch attempt ended for the requests waiting on it. Failed and TimedOut are
    /// different answers: a failed switch will not get better if you wait, and a switch that is still
    /// running usually will, which is exactly the retry decision a caller has to make.</summary>
    public enum SwitchOutcome { Ready, Failed, TimedOut, RouterStopped }

    /// <summary>A switch outcome plus what the caller was waiting on, phrased for an error body. The
    /// switch's own one-line reason goes to the event feed as `engine.switch_failed`, where an operator
    /// can read it without a log; this carries the caller-facing half.</summary>
    public sealed record SwitchResult(SwitchOutcome Outcome, string? Detail)
    {
        public bool IsReady => Outcome == SwitchOutcome.Ready;
        internal static SwitchResult RouterStopped { get; } = new(SwitchOutcome.RouterStopped, null);

        /// <summary>The only door a failed switch answer leaves the engine by: the caller is told one name from
        /// <see cref="SwitchReason.All"/> and nothing else from the attempt. A category outside that set is a
        /// programming error rather than a wire value, and it fails here instead of reaching a caller as a sentence
        /// they cannot act on — the request path's outer catch turns it into the 500 that §6.1 already calls a bug
        /// to fix at the producer. A wait that merely ran out is not a failed attempt: it is
        /// <see cref="SwitchOutcome.TimedOut"/>, and it carries the caller's own ceiling and no name from this set
        /// (§5.5).</summary>
        internal static SwitchResult Failed(string category)
        {
            if (!SwitchReason.All.Contains(category))
                throw new ArgumentException(
                    "a switch refusal named something outside the closed set of categories (§5.5); the name is " +
                    "not quoted here, because the point of this check is that such a name never reaches a caller",
                    nameof(category));
            return new SwitchResult(SwitchOutcome.Failed, category);
        }

        /// <summary>The one place an attempt's <i>recorded</i> reason becomes a caller-facing answer: the recorded
        /// name goes through the door above exactly as written, and an attempt that ended false before it recorded
        /// anything, such as the <c>finally</c> path, takes the catch-all
        /// name rather than a bare sentence. Exactly one arm of one expression applies that fallback, and the
        /// source audit says so (§5.5).</summary>
        internal static SwitchResult FailedFromRecord(string? recorded) =>
            Failed(recorded is { Length: > 0 } why ? why : SwitchReason.SwitchError);
    }

    /// <summary>Ensure the model is loaded on this engine: no-op when loaded; otherwise performs
    /// (or waits for) the configured switch. Resolves Ready when loaded, Failed when the switch
    /// itself failed, TimedOut when `timeout` ran out while the switch was still going, or RouterStopped
    /// when shutdown refused or ended the attempt.</summary>
    public async Task<SwitchResult> WaitForModelAsync(string model, TimeSpan timeout, CancellationToken ct)
        => (await EnsureLoadedAsync(model, null, timeout, queue: null, ct).ConfigureAwait(false)).Result;

    /// <summary>Ensure the model is loaded AND served under a context window that fits the profile.
    /// Exceeding the active window queues behind a profile switch instead of compacting.</summary>
    public async Task<SwitchResult> WaitForProfileAsync(string model, ServingProfile profile, TimeSpan timeout, CancellationToken ct)
        => (await EnsureLoadedAsync(model, profile, timeout, queue: null, ct,
            exactProfile: _def.Docker?.TargetPlans?.Find(CanonicalModel(model) ?? model, requestedTier: null) is not null)
            .ConfigureAwait(false)).Result;

    internal readonly record struct ObservedSwitchResult(SwitchResult Result, SwitchServingProof? Proof = null);

    /// <summary>Loaded, and (when a profile is required) serving a window that fits it.
    /// An engine that reports no window (gauge 0) is never escalated blind.</summary>
    /// <summary>Loaded, and (when a profile is required) serving a window that fits it.
    /// An engine that reports no window (gauge 0) is never escalated blind.
    /// The answer comes from one current, verified snapshot, not from the reported list alone: a list is
    /// history once a switch has started, and health unknown means the router does not know what is running
    /// now, so neither can license a dispatch (§5.5).</summary>
    public bool IsReadyFor(string canonical, ServingProfile? profile) =>
        ReadyIn(Capability, canonical, RequiredWindow(profile, explicitContextMinimum: 0));

    /// <summary>The same predicate against a snapshot the caller already holds. The claim path needs that form:
    /// the readiness decision and the switch marker have to come from one snapshot, or a poll that proves the
    /// model in between buys a second launch (§5.5).</summary>
    private bool ReadyIn(EngineCapability cap, string canonical, long requiredWindow, ServingProfile? exact = null)
    {
        // The lane floor here is deliberate: this asks whether an engine that is already up satisfies the named
        // tier, which is resident satisfaction, not a publication proof. A launch's Ready claim is proved exactly
        // at the publication sites (§5.3.1).
        if (!cap.Healthy || cap.SwitchingToward is not null || (exact is not null &&
            (!cap.Serving.Matches(canonical, exact) || _hostEnrollment?.Manager is null && !MatchesNativeRecipe(canonical, exact, cap)))) return false;
        foreach (var name in cap.ModelRoster)
        {
            if (string.Equals(name, canonical, StringComparison.OrdinalIgnoreCase))
                return WindowFits(cap.Serving.WindowFor(canonical), requiredWindow);
        }
        return false;
    }

    internal static long RequiredWindow(ServingProfile? profile, long explicitContextMinimum) =>
        Math.Max(profile is null ? 0L : Math.Max(1L, profile.MaxContext), explicitContextMinimum);

    private static bool WindowFits(long window, long requiredWindow) => window >= requiredWindow;

    private bool OwnProofStillStands(EngineCapability cap, string canonical, long requiredWindow)
    {
        if (cap.Healthy)
            return cap.ModelRoster.Contains(canonical, StringComparer.OrdinalIgnoreCase) &&
                WindowFits(cap.Serving.WindowFor(canonical), requiredWindow);
        lock (_transitionGate)
        {
            var state = _switchState;
            return ReferenceEquals(cap, Capability) && IsIncumbentClaim(cap) &&
                state.End == SwitchEnd.Pending && !state.IncumbentContradicted &&
                (requiredWindow == 0 || !state.IncumbentServingContradicted) &&
                cap.SwitchingToward == state.Model && state.IncumbentProof is { } incumbent &&
                incumbent.Version + 1 == cap.Version && ReadyIn(incumbent, canonical, requiredWindow);
        }
    }

    /// <summary>Authority and catalog checks for an idle acquisition.</summary>
    private enum EntryGuard { Ok, NoLifecycleOwner, Absent, NoUsableSelector, MissingHostCapacity }

    /// <summary>Resolve and validate, once for this acquisition attempt, the catalog entry a new claim would run
    /// on. Nothing is published or mutated here, and the predicate for "can this selector be run" is the provider's
    /// own — <see cref="DelegatedExecProvider.HasUsableSelector"/> — so the answer the router gives in front of the
    /// claim and the answer preflight would give inside it cannot drift. Called only where the caller has already
    /// declined to join an in-flight switch: asked anywhere earlier it would refuse a caller that was about to wait
    /// behind a switch this configuration never governed (§5.5, decisions 49 and 51).</summary>
    private EntryGuard ResolveClaimTarget(string canonical, ServingProfile? profile, out LifecycleTarget? target)
    {
        target = null;
        if (_lifecycleBinding is not { } binding) return EntryGuard.NoLifecycleOwner;
        if (!_catalog.TryGetValue(canonical, out var entry)) return EntryGuard.Absent;
        if (binding.Owner == EngineLifecycleOwner.Delegated && !DelegatedExecProvider.HasUsableSelector(entry))
            return EntryGuard.NoUsableSelector;
        target = new LifecycleTarget(binding.EngineName, canonical, profile, entry);
        return EntryGuard.Ok;
    }

    private SwitchSampleKey CostKey(LifecycleTarget target)
    {
        var kind = _lifecycleBinding!.ProviderKind;
        var imageId = _def.Docker?.ImageIdentity?.Id;
        var comparable = kind == DockerLifecycleProvider.ProviderKind && imageId is not null &&
            (target.Profile is null || target.Profile == GetProfile(target.Model, target.Profile.Name));
        // The runtime binds lifecycle settings and copies its catalog at construction. A new
        // configuration will need a new runtime or an explicit generation before it can share history.
        return new SwitchSampleKey(_runtimeEpoch, kind, target.Model, target.EffectiveProfile, 0,
            comparable ? SwitchObservationClass.FreshContainer : SwitchObservationClass.Unknown, imageId);
    }

    internal SwitchSampleKey? ReadVerifiedSwitchIncumbent(EngineCapability cap) =>
        !Volatile.Read(ref _stopped) && cap.Healthy && cap.SwitchingToward is null &&
        cap.SwitchProof is { NativeLaunch: not null } proof &&
        proof.Target.ObservationClass == SwitchObservationClass.FreshContainer &&
        cap.ModelRoster.Count == 1 &&
        string.Equals(cap.ModelRoster[0], proof.Target.CanonicalModel, StringComparison.OrdinalIgnoreCase)
            ? proof.Target : null;

    internal TimeSpan? EstimateVerifiedTransition(SwitchSampleKey incumbent, string model, string? profile, long now) =>
        ResolveClaimTarget(model, GetProfile(model, profile), out var target) == EntryGuard.Ok
            ? _switchHistory.EstimateTransition(incumbent, CostKey(target!), now)
            : null;

    internal void ObserveFirstForward(SwitchServingProof? proof, string model, CancellationToken ct)
    {
        if (proof is null || proof.ForwardObserved || ct.IsCancellationRequested ||
            !string.Equals(proof.Target.CanonicalModel, model, StringComparison.OrdinalIgnoreCase))
            return;
        lock (_transitionGate)
        {
            var cap = Capability;
            if (_stopped || ct.IsCancellationRequested || !cap.Healthy || cap.SwitchingToward is not null ||
                !ReferenceEquals(cap.SwitchProof, proof) || proof.ForwardObserved)
                return;
            _switchHistory.TryAttachFirstForward(proof.Attempt, _clock.GetTimestamp());
            proof.MarkForwardObserved();
        }
    }

    private static ObservedSwitchResult ReadyFrom(SwitchServingProof? proof, string model, ServingProfile? profile)
    {
        var matching = proof is not null &&
            string.Equals(proof.Target.CanonicalModel, model, StringComparison.OrdinalIgnoreCase) &&
            (profile is null || string.Equals(proof.Target.EffectiveProfile, profile.Name, StringComparison.Ordinal));
        return new ObservedSwitchResult(new SwitchResult(SwitchOutcome.Ready, null), matching ? proof : null);
    }

    internal async Task<ObservedSwitchResult> EnsureLoadedAsync(
        string model, ServingProfile? profileOverride, TimeSpan timeout, SwitchQueueTiming? queue, CancellationToken ct,
        long explicitContextMinimum = 0, LocalLifecycleGroup.Lease? localOwner = null, bool exactProfile = false,
        InProcessHostManager.Transition? hostTransition = null)
    {
        if (Volatile.Read(ref _stopped) || Volatile.Read(ref _retiring)) return new(SwitchResult.RouterStopped);
        var canonical = CanonicalModel(model) ?? model;
        if (_hostEnrollment?.Manager is { } manager &&
            (hostTransition is null || !manager.TryClaimRuntime(hostTransition, this, canonical, profileOverride)))
            return new(SwitchResult.Failed(SwitchReason.NoLifecycleOwner));
        if (profileOverride is { } suppliedProfile && _def.Docker?.TargetPlans?.Find(canonical, requestedTier: null) is not null)
        {
            var resolved = GetProfile(canonical, suppliedProfile.Name);
            if (resolved is null || suppliedProfile.MaxContext != resolved.MaxContext || suppliedProfile.MaxSlots != resolved.MaxSlots)
                return new(SwitchResult.Failed(SwitchReason.NoSelectCommand));
            profileOverride = resolved;
        }
        var requiredWindow = RequiredWindow(profileOverride, explicitContextMinimum);
        if (localOwner is not null && (!ReferenceEquals(localOwner.Engine, this) || !localOwner.Cold))
            return new(SwitchResult.Failed(SwitchReason.NoLifecycleOwner));
        var exact = exactProfile ? profileOverride : null;
        var initial = Capability;
        if (localOwner is null && hostTransition is null && ReadyIn(initial, canonical, requiredWindow, exact))
            return Volatile.Read(ref _stopped) || Volatile.Read(ref _retiring)
                ? new(SwitchResult.RouterStopped) : ReadyFrom(initial.SwitchProof, canonical, profileOverride);
        // `timeout` bounds the *waiting*, never the switch: the transaction belongs to the engine and
        // runs on _switchCts, so a caller that gives up leaves the engine switching rather than
        // half-switched. The loop is what makes single-flight fair: if the switch in progress is aimed
        // at another request's model, this caller waits for it and then takes its own turn rather than
        // being failed by someone else's switch. The deadline is set once, before the first lock wait, and is
        // never reset across a wait, a join or a re-loop: a budget that expires while joined is a **timeout**, and
        // no catalog state can turn it into a refusal (§5.5).
        var deadline = timeout == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + timeout;
        // Shutdown has to reach the two waits below as cancellation, not as an ObjectDisposedException: the switch
        // lock is deliberately never disposed (see DisposeAsync), so a waiter is released by a token linked to the
        // engine's switch token instead. This source is disposed with the call; the engine's own is not.
        using var waiterCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _switchCts.Token);
        var waiter = waiterCts.Token;
        var claimLosses = 0;
        while (true)
        {
            SwitchState state;
            // The outcome of *this* acquisition attempt, selected under the gate and acted on after release: the
            // entry the claim runs on when there is a claim, and the catalog miss when there is not. Emitting inside
            // the switch lock is what TASK-103 #4 forbids — the ring runs every subscriber inside its append with no
            // isolation — so the refusal is chosen inside the gate and spoken outside it.
            LifecycleTarget? claimTarget = null;
            var entryMiss = EntryGuard.Ok;
            var retryClaim = false;
            Task? localTransaction = null;
            Task? waitForNamedUses = null;
            var offered = Capability;
            BeforeSwitchWaitForTests?.Invoke();
            try
            {
                // The linked source outlives this await.
#pragma warning disable IDISP013 // await-in-using not applicable: the wait is awaited, not detached
                if (!await TryWaitForSwitchLockAsync(deadline, waiter).ConfigureAwait(false))
                    return UnclaimedWaitExpired(ct);
#pragma warning restore IDISP013
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && Volatile.Read(ref _stopped))
            {
                return new(SwitchResult.RouterStopped);
            }
            try
            {
                AfterSwitchLockAcquiredForTests?.Invoke(deadline);
                SwitchState? mine = null;
                Exception? claimFault = null;
                CancellationToken switchToken = default;
                // The read and the claim move together. A switch's end runs outside the switch lock, so this is
                // the point where a second waiter can meet a switch that is committing its last step: reading
                // outside the gate lets this caller coalesce onto a readiness task that resolves a moment later
                // and return "ready" for a model nobody loaded.
                lock (_transitionGate)
                {
                    if (_stopped || _retiring) return new(SwitchResult.RouterStopped);
                    ct.ThrowIfCancellationRequested();
                    if (deadline != DateTimeOffset.MaxValue && DateTimeOffset.UtcNow >= deadline)
                        return UnclaimedWaitExpired(ct);

                    state = _switchState;
                    if (state.Model is null)
                    {
                        // An active claim takes precedence over the optimistic offer. An idle attempt can publish
                        // only from the exact snapshot it evaluated, and retries release both locks.
                        if (localOwner is null && hostTransition is null && ReadyIn(offered, canonical, requiredWindow, exact))
                        {
                            if (ReferenceEquals(Capability, offered))
                                return ReadyFrom(offered.SwitchProof, canonical, profileOverride);
                            retryClaim = true;
                        }
                        else if (!exactProfile && _namedUses > 0)
                        {
                            _namedDrain ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                            waitForNamedUses = _namedDrain.Task;
                        }
                        else
                        {
                            entryMiss = ResolveClaimTarget(canonical, profileOverride, out var target);
                            if (entryMiss == EntryGuard.Ok && _hostEnrollment is { UsesStagedSwitch: false } &&
                                (requiredWindow > 0 || GetProfile(canonical, target!.EffectiveProfile) is not null))
                                entryMiss = EntryGuard.MissingHostCapacity;
                            if (entryMiss != EntryGuard.Ok)
                            {
                                retryClaim = !ReferenceEquals(Capability, offered);
                            }
                            else
                            {
                                var quarantineTarget = _nativeQuarantine?.Resolve(target!);
                                if (quarantineTarget is { } key && _nativeQuarantine!.Read(key) is { } failed &&
                                    ReferenceEquals(Capability, offered))
                                    return new(failed);

                                claimTarget = target;
                                var costKey = CostKey(target!);
                                var candidate = new SwitchState
                                {
                                    Model = canonical,
                                    Profile = profileOverride?.Name,
                                    LocalOwner = localOwner,
                                    HostTransition = hostTransition,
                                    ExactProfile = exact,
                                    RequiredWindow = requiredWindow,
                                    QuarantineTarget = quarantineTarget,
                                    IncumbentProof = offered.Healthy ? offered : null,
                                };
                                ct.ThrowIfCancellationRequested();
                                if (deadline != DateTimeOffset.MaxValue && DateTimeOffset.UtcNow >= deadline)
                                    return UnclaimedWaitExpired(ct);
                                retryClaim = !TryPublishSwitchStarted(canonical, offered);
                                if (!retryClaim)
                                {
                                    candidate.Timing = new SwitchAttempt(_clock,
                                        new SwitchAttemptId(_runtimeEpoch, ++_switchSequence), costKey, queue);
                                    switchToken = _switchCts.Token;
                                    try
                                    {
                                        AfterSwitchMarkerRaisedForTests?.Invoke();
                                        _switchState = candidate;
                                        AfterClaimForTests?.Invoke();
                                    }
                                    catch (Exception ex)
                                    {
                                        // Rollback outside the gate must find the installed claim even
                                        // when the marker hook threw before the state write.
                                        _switchState = candidate;
                                        claimFault = ex;
                                    }
                                    mine = candidate;
                                }
                            }
                        }
                        // A refused attempt claims nothing, so there is no state to adopt: `state` stays the idle
                        // value read above and the refusal is spoken below, after the lock is released.
                        if (mine is not null) state = mine;
                    }
                }

                if (mine is { } claim)
                {
                    if (claimFault is { } fault)
                    {
                        // Undo the claim **after** leaving the gate: EndSwitch settles the waiters and nudges dispatch,
                        // and Monitor is reentrant, so undoing it inside the lock would run both of those while every
                        // later claim queues behind the gate. Then let the hook's failure reach the caller.
                        if (EndSwitch(claim, false) == SwitchEnd.RouterStopped) return new(SwitchResult.RouterStopped);
                        ExceptionDispatchInfo.Capture(fault).Throw();
                    }
                    try
                    {
                        BeforeScheduleSwitchForTests?.Invoke();
                        // IDISP013 again: the switch task is deliberately detached, and it never touches the linked
                        // waiter source above — it runs on the engine's own switch token — so the using scope cannot
                        // dispose anything this task is still using.
#pragma warning disable IDISP013 // await-in-using not applicable: intentionally detached Task, no shared source
                        var transaction = Task.Run(
                            () => RunSwitchAsync(claim, claimTarget!, switchToken),
                            // Deliberately not the caller's token: the switch's own token above decides whether the
                            // transaction runs, and a request that hangs up must not be able to abort it halfway
                            // through a model load.
                            CancellationToken.None);
                        if (localOwner is not null || hostTransition is not null) localTransaction = transaction;
#pragma warning restore IDISP013
                    }
                    catch (Exception)
                    {
                        // The task never started: a synchronous failure at the scheduling boundary (the case the seam
                        // pins) and a thread-pool rejection look the same from here. Settle the waiters and put the
                        // engine back rather than leaving it "switching" forever. The caller gets the catch-all
                        // category, because their action — retry — is the same as for an attempt that threw, and never
                        // the target of the switch it could not start. The *phase* is what tells the two apart and it
                        // goes to the feed, where an operator reads it (§5.5).
                        // Recorded before the switch is ended, like every other exit, so a waiter never wakes onto a
                        // failure with no explanation on the feed yet. EndSwitch stays in the finally because a feed
                        // subscriber runs inside the ring's append with no isolation: an observer that throws must not
                        // be able to leave the engine latched mid-switch (that isolation is a change of its own).
                        try { Emit("engine.switch_failed", model: model, reason: FeedReason.SwitchNotStarted); }
                        finally { EndSwitch(claim, false); }
                        return new(claim.End == SwitchEnd.RouterStopped
                            ? SwitchResult.RouterStopped : SwitchResult.Failed(SwitchReason.SwitchError));
                    }
                    state = claim;
                }
            }
            finally
            {
                _switchLock.Release();
            }

            // A local group can abandon admission immediately, but once this owner issues
            // a transaction its group stays reserved until the provider has unwound.
            if (localTransaction is not null)
            {
                await localTransaction.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }

            if (waitForNamedUses is not null)
            {
                var left = RemainingSwitchWait(deadline);
                if (left != Timeout.InfiniteTimeSpan && left <= TimeSpan.Zero) return UnclaimedWaitExpired(ct);
                try { await waitForNamedUses.WaitAsync(left, waiter).ConfigureAwait(false); }
                catch (TimeoutException) { return UnclaimedWaitExpired(ct); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && Volatile.Read(ref _stopped))
                {
                    return new(SwitchResult.RouterStopped);
                }
                continue;
            }
            if (retryClaim)
            {
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _stopped)) return new(SwitchResult.RouterStopped);
                if (deadline != DateTimeOffset.MaxValue && RemainingSwitchWait(deadline) <= TimeSpan.Zero)
                    return UnclaimedWaitExpired(ct);
                if (++claimLosses < MaxClaimLosses) continue;

                EmitUnclaimedRetryFailure(canonical);
                return new(SwitchResult.Failed(SwitchReason.SwitchError));
            }

            // Spoken outside both the switch lock and the transition gate: the refusal was selected under the gate
            // and is emitted after release, so a feed subscriber runs with no engine lock held (TASK-103 #4). One
            // `engine.switch_failed` per refused attempt, and nothing else: no marker went up, so there is no
            // switch to end and the engine keeps the proof it had (§5.5).
            if (entryMiss == EntryGuard.NoLifecycleOwner)
                return new(SwitchResult.Failed(SwitchReason.NoLifecycleOwner));
            if (entryMiss == EntryGuard.MissingHostCapacity)
            {
                Emit("engine.switch_failed", model: canonical,
                    reason: "window_too_small: enrolled switch requires an exact-instance serving window");
                return new(SwitchResult.Failed(SwitchReason.WindowTooSmall));
            }
            if (entryMiss != EntryGuard.Ok)
            {
                Emit("engine.switch_failed", model: canonical,
                    reason: entryMiss == EntryGuard.Absent
                        ? "no_select_command: no catalog entry names this model on this engine — add it under " +
                          "[engine.strata.catalog], move the request to an engine that declares it, or mark this " +
                          "engine lifecycle_owner = external"
                        : "no_select_command: the catalog entry has no usable select exec — name one with " +
                          "select = { exec = … }, move the model to an engine whose lifecycle the router owns, or " +
                          "set lifecycle_owner = external");
                return new(SwitchResult.Failed(SwitchReason.NoSelectCommand));
            }

            var remaining = RemainingSwitchWait(deadline);
            if (deadline != DateTimeOffset.MaxValue && remaining <= TimeSpan.Zero)
                return await AnswerExpiredSwitchWaitAsync(state, canonical, profileOverride, exact, requiredWindow, null, ct).ConfigureAwait(false);

            var wokeByCancellation = false;
            try
            {
#pragma warning disable IDISP013 // await-in-using not applicable: the wait is awaited, not detached
                await state.Ready.Task.WaitAsync(remaining, waiter).ConfigureAwait(false);
#pragma warning restore IDISP013
            }
            catch (TimeoutException)
            {
                return await AnswerExpiredSwitchWaitAsync(state, canonical, profileOverride, exact, requiredWindow, timeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && Volatile.Read(ref _stopped))
            {
                // Shutdown records the attempt's outcome before cancelling this token. Read that outcome
                // for either wake-up. The caller's own cancellation still propagates.
                wokeByCancellation = true;
            }

            AfterReadyWaitForTests?.Invoke(wokeByCancellation);
            if (wokeByCancellation && FindReadinessDelivery(state, canonical, profileOverride) is { } delivery)
                await WaitForOutcomeDeliveryAsync(delivery.Task, ct).ConfigureAwait(false);

            if (ReadSwitchAnswer(state, canonical, profileOverride, exact, requiredWindow, woke: true) is { } answer) return answer;
            // A contradicted proof or an unrelated outcome sends the caller back through the claim gate.
        }
    }

    private async Task<bool> TryWaitForSwitchLockAsync(DateTimeOffset deadline, CancellationToken ct)
    {
        if (deadline == DateTimeOffset.MaxValue)
        {
            await _switchLock.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }

        // A single semaphore wait takes integer milliseconds. Keep the deadline for callers with longer waits.
        var remaining = deadline - DateTimeOffset.UtcNow;
        while (remaining > TimeSpan.Zero)
        {
            var waitMs = (int)Math.Min(int.MaxValue, Math.Ceiling(remaining.TotalMilliseconds));
            if (await _switchLock.WaitAsync(waitMs, ct).ConfigureAwait(false)) return true;
            if (waitMs < int.MaxValue) break;
            remaining = deadline - DateTimeOffset.UtcNow;
        }
        return false;
    }

    private TimeSpan RemainingSwitchWait(DateTimeOffset deadline) => deadline == DateTimeOffset.MaxValue
        ? Timeout.InfiniteTimeSpan
        : deadline - (SwitchWaitTimeForTests?.Invoke(deadline) ?? DateTimeOffset.UtcNow);

    private ObservedSwitchResult UnclaimedWaitExpired(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new(Volatile.Read(ref _stopped)
            ? SwitchResult.RouterStopped : new SwitchResult(SwitchOutcome.TimedOut, null));
    }

    private ObservedSwitchResult? ReadSwitchAnswer(SwitchState state, string canonical, ServingProfile? profile, ServingProfile? exact,
                                                   long requiredWindow, bool woke)
    {
        var end = state.End;
        if (end == SwitchEnd.RouterStopped) return new ObservedSwitchResult(SwitchResult.RouterStopped);

        var own = state.Model == canonical && state.Profile == profile?.Name;
        if (end is SwitchEnd.Ready or SwitchEnd.Failed && Volatile.Read(ref _stopped) &&
            state.ReadinessOutcomeDelivered is not null)
        {
            if (!own) return new ObservedSwitchResult(SwitchResult.RouterStopped);
            // The historical verdict survives shutdown independently of the withdrawn dispatch proof.
            if (end == SwitchEnd.Failed)
                return new ObservedSwitchResult(SwitchResult.FailedFromRecord(state.FailureReason));
            return new ObservedSwitchResult(WindowFits(state.CommittedWindow, requiredWindow)
                ? new SwitchResult(SwitchOutcome.Ready, null)
                : SwitchResult.RouterStopped);
        }

        if (own && end == SwitchEnd.Failed && state.NativeFailure is { } failure)
            return new ObservedSwitchResult(failure);

        var cap = Capability;
        if (ReadyIn(cap, canonical, requiredWindow, exact)) return ReadyFrom(cap.SwitchProof, canonical, profile);
        if (!own) return null;
        if (exact is not null && end == SwitchEnd.Ready)
            return new ObservedSwitchResult(SwitchResult.Failed(SwitchReason.WindowTooSmall));

        // Same target and tier can have waiters with different explicit minima.
        if (end == SwitchEnd.Ready)
        {
            if (!WindowFits(state.CommittedWindow, requiredWindow))
                return new ObservedSwitchResult(Volatile.Read(ref _stopped)
                    ? SwitchResult.RouterStopped
                    : SwitchResult.Failed(SwitchReason.WindowTooSmall));
            // An own proof still authorises this waiter while a successor's claim alone has withdrawn health.
            return Volatile.Read(ref _stopped) || OwnProofStillStands(cap, canonical, requiredWindow)
                ? ReadyFrom(state.Proof, canonical, profile) : null;
        }

        // A real wake requires a terminal state. If that invariant is broken, fail with the same
        // bounded category used by a failed switch with no recorded reason.
        return end == SwitchEnd.Failed || woke
            ? new ObservedSwitchResult(SwitchResult.FailedFromRecord(state.FailureReason)) : null;
    }

    private static TaskCompletionSource? FindReadinessDelivery(SwitchState state, string canonical, ServingProfile? profile) =>
        state.End is SwitchEnd.Ready or SwitchEnd.Failed && state.Model == canonical && state.Profile == profile?.Name
            ? state.ReadinessOutcomeDelivered : null;

    private Task WaitForOutcomeDeliveryAsync(Task delivery, CancellationToken caller)
    {
        var wait = delivery.WaitAsync(caller);
        AfterOutcomeDeliveryWaitForTests?.Invoke(wait);
        return wait;
    }

    private async ValueTask<ObservedSwitchResult> AnswerExpiredSwitchWaitAsync(
        SwitchState state, string canonical, ServingProfile? profile, ServingProfile? exact, long requiredWindow, TimeSpan? wait,
        CancellationToken caller)
    {
        TaskCompletionSource? delivery;
        lock (_transitionGate)
        {
            delivery = FindReadinessDelivery(state, canonical, profile);
            if (delivery is null)
                return ExpiredSwitchAnswer(state, canonical, profile, exact, requiredWindow, wait);
        }

        await WaitForOutcomeDeliveryAsync(delivery.Task, caller).ConfigureAwait(false);
        lock (_transitionGate)
            return ExpiredSwitchAnswer(state, canonical, profile, exact, requiredWindow, wait);
    }

    private ObservedSwitchResult ExpiredSwitchAnswer(SwitchState state, string canonical, ServingProfile? profile, ServingProfile? exact,
                                                     long requiredWindow, TimeSpan? wait) =>
        // A foreign terminal outcome leaves this caller owing another claim. The stop latch can
        // refuse that claim even after the foreign attempt has left the runtime idle.
        ReadSwitchAnswer(state, canonical, profile, exact, requiredWindow, woke: false) ??
        new ObservedSwitchResult(_stopped ? SwitchResult.RouterStopped :
            new SwitchResult(SwitchOutcome.TimedOut, wait is { } elapsed ?
            $"still switching after {FormatWait(elapsed)}; a switch for another request is in flight" : null));

    // Bounds for what a refusal puts in front of an operator: the feed record is built from values the engine or
    // a provider chose, and the event ring caps entries rather than characters. Identifier caps and the ceiling
    // for one whole reason live in WireText; these are the provider-side caps the feed sentences use. The
    // caller-facing half carries no value from here at all — it carries a SwitchReason token (§5.5).
    private const int ReportedNameCount = 8;
    private const int ReportedNameHeadChars = 24;
    private const int ReportedNamesChars = 500;
    private const int ProviderReasonChars = 160;
    private const int ProviderActionChars = 200;
    private const int TrustStateReadCapMs = 5_000;

    /// <summary>What an engine reported, for a refusal reason: at most <see cref="ReportedNameCount"/> names, each
    /// keeping its first <see cref="ReportedNameHeadChars"/> characters with its true length and a fingerprint
    /// marked, and always the count of what was left out. A cap on the count alone is not a cap — an engine can
    /// answer <c>/models</c> with a hundred-character id per model and eight names is then eight unbounded strings.
    /// The head is the number stated here because it is the number an operator acts on: enough of the provider's
    /// spelling to tell one quantisation tag from another (§5.5).</summary>
    private static string DescribeReportedNames(IReadOnlyList<string> reported)
    {
        var shown = Math.Min(reported.Count, ReportedNameCount);
        var sb = new StringBuilder();
        for (var i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(WireText.Head(reported[i], ReportedNameHeadChars));
        }
        if (reported.Count > shown)
            sb.Append(CultureInfo.InvariantCulture, $" … (+{reported.Count - shown} more)");
        return WireText.Bound(sb.ToString(), ReportedNamesChars);
    }

    /// <summary>The target as a caller may spell it, at reporting length. Only ever the *asking* caller's own
    /// model and profile: another request's target never appears in this sentence (§5.5).</summary>
    private static string DescribeTarget(string? model, string? profile) =>
        profile is { Length: > 0 } p
            ? $"{WireText.Ident(model ?? "(none)")} @ {WireText.Ident(p)}"
            : model is null ? "(none)" : WireText.Ident(model);

    private static string DescribeTarget(string model, ServingProfile? profile) =>
        profile is null ? WireText.Ident(model) : $"{WireText.Ident(model)} @ {WireText.Ident(profile.Name)}";

    private static string FormatWait(TimeSpan wait) =>
        wait == Timeout.InfiniteTimeSpan ? "the whole wait" : $"{wait.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms";

    /// <summary>
    /// Runs one switch transaction and resolves its shared readiness task. Ordering rule: the feed
    /// records the outcome *before* the waiters are woken. A ticket that fails because a switch failed
    /// is observable the instant `Ready` completes, so an event published afterwards leaves a window
    /// where an operator (or a test) sees a failed request with no explanation on the feed yet.
    /// </summary>
    /// <summary>Ends one switch attempt. The engine stops being "switching" *before* the waiters are
    /// woken, so a waiter that re-checks and does not get its own target either starts its own switch or
    /// ends, instead of rejoining a readiness task that is already resolved. The feed event is always
    /// emitted before this call, so the outcome is on the feed before the request that failed on it
    /// resumes.</summary>
    private SwitchEnd EndSwitch(SwitchState state, bool ready, bool restoreIncumbent = false) =>
        DeliverSwitchEnd(state, CommitSwitchEnd(state, ready, restoreIncumbent));

    private readonly record struct SwitchEndCommit(SwitchEnd End, bool Committed, ServingCapacity Serving,
        EngineCapability? MutationUnknown);

    // A read-only refusal from the actual Docker provider is committed with its operation gate held.
    // Delivery runs after the provider and transition gates are released.
    private SwitchEndCommit CommitSwitchEnd(SwitchState state, bool ready, bool restoreIncumbent,
        DockerLifecycleProvider? native = null, bool withdrawForFailure = false)
    {
        var committed = false;
        var serving = default(ServingCapacity);
        EngineCapability? mutationUnknown = null;
        SwitchEnd end;
        lock (_transitionGate)
        {
            if (state.End == SwitchEnd.Pending) state.End = ready ? SwitchEnd.Ready : SwitchEnd.Failed;
            end = state.End;
            if (ReferenceEquals(_switchState, state))
            {
                SealAttempt(state, end);
                // A proof observed after shutdown may describe the provider's result, but it cannot authorize
                // dispatch. Withdraw it before clearing the marker.
                if (end == SwitchEnd.RouterStopped || (_stopped && state.ReadinessOutcomeDelivered is not null))
                    PublishUnknown();
                if (end == SwitchEnd.Failed && withdrawForFailure && !_stopped)
                {
                    InvalidateNativeWatch(Capability);
                    mutationUnknown = PublishUnknown();
                }
                var canRestore = end == SwitchEnd.Failed && restoreIncumbent && !_stopped && !_retiring;
                if (canRestore && (Capability.NativeWatch is { } watch
                    ? native is null || !CanRestoreNativeWatch(state, watch, native)
                    : native is not null &&
                      !native.CanRestoreIncumbentUnderGate(state.IncumbentProof?.SwitchProof?.NativeLaunch?.ContainerId)))
                {
                    canRestore = false;
                    mutationUnknown = PublishUnknown();
                }
                var incumbent = canRestore && !state.IncumbentContradicted
                    ? state.IncumbentProof : null;
                if (end == SwitchEnd.Ready) serving = Capability.Serving;
                _switchState = new SwitchState();
                ClearSwitchMarker(incumbent, restoreServing: !state.IncumbentServingContradicted);
                committed = true;
            }
            // The reference guard runs inside the gate for a reason: without it, the switch that just
            // finished can clear the marker a successor published in the gap between this switch writing
            // idle state and this line, which would hide a running model load from the scheduler. A second
            // EndSwitch for the same attempt (the finally block) sees a successor's state and does nothing.
        }
        return new(end, committed, serving, mutationUnknown);
    }

    private SwitchEnd DeliverSwitchEnd(SwitchState state, SwitchEndCommit commit)
    {
        // A switch reports its outcome before EndSwitch. Report the profile after marker clear, then release
        // waiters before invoking the explicit nudge. A throwing nudge cannot hold a waiter or rewrite the
        // committed outcome, and the next engine observation can still wake dispatch.
        try
        {
            if (commit.MutationUnknown is { } unknown) AfterFailedMutationUnknownForTests?.Invoke(unknown);
            if (commit.Committed && commit.End == SwitchEnd.Ready)
                EmitServingProfileChange(state.IncumbentProof?.Serving ?? default, commit.Serving);
        }
        finally
        {
            state.Ready.TrySetResult();
            if (commit.Committed)
            {
                try { _dispatchHint?.Invoke(); }
                catch (Exception) { /* a lost nudge is a late dispatch, not a lost switch */ }
                AfterEndCommitForTests?.Invoke(Monitor.IsEntered(_transitionGate));
            }
        }
        return commit.End;
    }

    private void SealAttempt(SwitchState state, SwitchEnd end)
    {
        var outcome = end switch
        {
            SwitchEnd.Ready => SwitchAttemptOutcome.Ready,
            SwitchEnd.RouterStopped => SwitchAttemptOutcome.Cancelled,
            _ => SwitchAttemptOutcome.Failed,
        };
        if (state.Timing?.TrySeal(outcome) is { } record) _switchHistory.Add(record);
    }

    /// <summary>Why the prove-health loop ended without readiness, as its last poll saw it: the category the
    /// caller is told follows from this, so an engine that is up and wrong is not reported as an engine that is
    /// still starting (§5.5).</summary>
    private enum ReadinessMiss { NeverHealthy, ModelNotReported, WindowTooSmall, RefreshUnanswered, Error, NativeObservation, LoadedProfile }

    private async Task RunSwitchAsync(SwitchState state, LifecycleTarget target, CancellationToken ct)
    {
        var model = target.Model;
        var profileOverride = target.Profile;
        var lifecycle = _lifecycle!;
        var timing = state.Timing!;
        var nativeTransaction = false;
        var authorityToken = state.LocalOwner?.MutationToken ?? state.HostTransition?.Cancellation;
        using var ownerLifetime = authorityToken is { } authority
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, authority) : null;
        var operation = ownerLifetime?.Token ?? ct;
        // The idle claim already validated this target against the runtime binding.
        try
        {
            if (state.End == SwitchEnd.RouterStopped) return;

            var profileName = profileOverride?.Name ?? target.Definition.Profile;
            Emit("engine.switch_started", model: model,
                reason: profileName is null ? null : $"profile={WireText.Ident(profileName)}");

            // One transaction through the provider seam: prove ownership, validate, stop what is ours,
            // launch the exact target. What "launch" means is the provider's business; the ordering is
            // the router's, and it is what keeps a refusal from costing the engine anything it had.
            EngineLifecycleCoordinator.SwitchReport report;
            if (_hostEnrollment is { UsesStagedSwitch: true } stagedEnrollment)
            {
                var fixture = stagedEnrollment.StagedForTests;
                var staged = await SwitchStagedHostAsync(stagedEnrollment.Controller, fixture, state, target, operation)
                    .ConfigureAwait(false);
                fixture?.AfterHostResult?.Invoke(staged);
                // The guarded verdict owns the outcome even when delivery, cleanup or Open refuses.
                if (staged.Decision.Verdict != HostStagedVerdict.Refused || Volatile.Read(ref _stopped)) return;
                report = new(staged.Result, RefusedBeforeMutation: false);
            }
            else if (_hostEnrollment is { } enrollment)
            {
                await RunImmediateHostSwitchAsync(enrollment, state, target, ct).ConfigureAwait(false);
                return;
            }
            else
            {
                nativeTransaction = state.QuarantineTarget is not null;
                report = await lifecycle.SwitchWithReportAsync(target, timing, state.IncumbentProof?.SwitchProof,
                    observeLaunch: true, operation, state.LocalOwner,
                    state.AdoptionOwner is null || UsesVllmArgv ? null : token => ProbeAdoptionAsync(state, target, token),
                    verifyAdoptedProfileAfterClaim: UsesVllmArgv)
                    .ConfigureAwait(false);
            }
            if (!report.RefusedBeforeMutation) state.AdoptionProbe = null;
            var launched = report.Result;
            if (!launched.Succeeded)
            {
                if (nativeTransaction)
                {
                    await FinishNativeFailureAsync(state, DescribeLaunchFailure(launched), providerRefusal: true,
                        report.RefusedBeforeMutation, lifecycle.NativeProvider, ct).ConfigureAwait(false);
                    return;
                }

                Emit("engine.switch_failed", model: model, reason: DescribeLaunchFailure(launched));
                // The provider's own words stay on the feed; the caller is told only the category.
                RecordLifecycleFailureReason(state, providerRefusal: true);
                // An attempted stop or launch can invalidate the earlier proof even when it returns failure.
                // Only an explicit refusal before either call can restore a still-valid incumbent.
                if (!report.RefusedBeforeMutation)
                {
                    var unknown = PublishUnknown();
                    AfterFailedMutationUnknownForTests?.Invoke(unknown);
                }
                EndSwitch(state, false, restoreIncumbent: report.RefusedBeforeMutation);
                return;
            }
            if (_hostEnrollment is not null) return; // the controller committed readiness under its host guard
            if (lifecycle.NativeProvider is not null)
            {
                if (launched.NativeLaunch is { } launch)
                {
                    if (!RecordPendingNativeLaunch(state, launch, report.Baseline)) return;
                    if (report.Baseline.Status != NativeContainerStatus.Running)
                    {
                        await FinishNativeFailureAsync(state, "Docker did not confirm the exact started container.",
                            providerRefusal: false, restoreIncumbent: false, native: null, ct).ConfigureAwait(false);
                        return;
                    }
                }
                else if (Capability.NativeWatch is not null)
                {
                    await FinishNativeFailureAsync(state, "Docker did not acknowledge a fresh start for a tracked engine.",
                        providerRefusal: false, restoreIncumbent: false, native: null, ct).ConfigureAwait(false);
                    return;
                }
            }
            timing.TryStartStage(SwitchStage.Readiness);
            var readiness = lifecycle.Readiness(target);
            if (UsesVllmArgv)
                await RunManagedReadinessAsync(state, target, readiness, lifecycle.NativeProvider!, operation).ConfigureAwait(false);
            else
                await RunLegacyReadinessAsync(state, target, readiness, lifecycle.Kind, operation).ConfigureAwait(false);
        }
        catch (Exception) when (state.ReadinessOutcomeDelivered is not null)
        {
            // The terminal winner has already settled. Delivery faults cannot write a second outcome.
        }
        catch (Exception) when (ct.IsCancellationRequested || Volatile.Read(ref _stopped))
        {
            // Shutdown owns the terminal outcome while an in-flight operation unwinds.
        }
        catch (Exception ex)
        {
            if (nativeTransaction)
            {
                await FinishNativeFailureAsync(state, WireText.Bound(ex.Message, ProviderReasonChars),
                    providerRefusal: false, restoreIncumbent: false, native: null, ct).ConfigureAwait(false);
                return;
            }
            // Provider and IO text reaches the feed here, bounded; the caller is told only the category, so a
            // message that names a socket path or an address never travels to whoever made the request.
            RecordLifecycleFailureReason(state, providerRefusal: false);
            Emit("engine.switch_failed", model: model, reason: WireText.Bound(ex.Message, ProviderReasonChars));
            // An exception can escape after the engine was stopped and before anything was relaunched, and
            // the router cannot tell from here which half happened. Unknown is the honest answer (§5.5).
            PublishUnknown();
            EndSwitch(state, false);
        }
        finally
        {
            // Two ways a switch can die without reaching any of the exits above: the task never starts
            // (thread-pool starvation, provider disposed under it) or it throws on the way in. Either one
            // would leave `Ready` unresolved forever and the engine latched mid-switch, so the state
            // machine closes itself here: resolve the waiters, and put the engine back to "not switching"
            // if this attempt is still the current one.
            try { EndSwitch(state, false); }
            finally
            {
                if (_hostEnrollment?.StagedForTests is { } stagedFixture) stagedFixture.AfterRun?.Invoke();
                else _hostEnrollment?.ImmediateForTests?.AfterRun?.Invoke();
            }
        }
    }

    private static void RecordLifecycleFailureReason(SwitchState state, bool providerRefusal)
    {
        if (providerRefusal)
        {
            state.FailureReason = SwitchReason.LaunchFailed;
        }
        else
        {
            state.FailureReason = SwitchReason.SwitchError;
        }
    }

    private void RememberNativeFailure(SwitchState state)
    {
        if (state.QuarantineTarget is not { } target) return;
        var failure = SwitchResult.FailedFromRecord(state.FailureReason);
        if (_def.LifecycleGroup is null) _nativeQuarantine!.Remember(target, failure);
        state.NativeFailure = failure;
    }

    private async Task FinishNativeFailureAsync(SwitchState state, string reason, bool providerRefusal,
        bool restoreIncumbent, DockerLifecycleProvider? native, CancellationToken stop)
    {
        TaskCompletionSource? delivery = null;
        SwitchEnd previousEnd;
        lock (_transitionGate)
        {
            previousEnd = state.End;
            if (!_stopped && !_retiring && ReferenceEquals(_switchState, state) && previousEnd == SwitchEnd.Pending)
            {
                delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RecordLifecycleFailureReason(state, providerRefusal);
                state.ReadOnlyFailureCanRestore = restoreIncumbent;
                RememberNativeFailure(state);
                state.ReadinessOutcomeDelivered = delivery;
                state.End = SwitchEnd.Failed;
            }
        }

        if (delivery is null)
        {
            // A returned provider failure can describe how a stopped transaction unwound.
            if (providerRefusal && previousEnd == SwitchEnd.RouterStopped)
                Emit("engine.switch_failed", model: state.Model, reason: reason);
            return;
        }

        try
        {
            try { BeforeNativeFailureEventForTests?.Invoke(); }
            finally { Emit("engine.switch_failed", model: state.Model, reason: reason); }
        }
        finally
        {
            try
            {
                var commit = await CommitNativeFailureAsync(state, restoreIncumbent, native, stop).ConfigureAwait(false);
                DeliverSwitchEnd(state, commit);
            }
            finally { delivery.TrySetResult(); }
        }
    }

    private async ValueTask<SwitchEndCommit> CommitNativeFailureAsync(SwitchState state, bool restoreIncumbent,
        DockerLifecycleProvider? native, CancellationToken stop)
    {
        if (!restoreIncumbent || native is null)
            return CommitSwitchEnd(state, ready: false, restoreIncumbent, withdrawForFailure: !restoreIncumbent);

        // Restoration of a native incumbent is revalidated against the same provider in
        // provider-to-transition order. Stops can cancel this wait after Failed has won.
        try { await native.EnterOperationAsync(stop).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return CommitSwitchEnd(state, ready: false, restoreIncumbent: false);
        }

        try { return CommitSwitchEnd(state, ready: false, restoreIncumbent: true, native); }
        finally { native.ExitOperation(); }
    }

    private static string DescribeLaunchFailure(LifecycleResult result)
    {
        // Bound the reason and the operator's action independently before they reach the feed.
        var why = WireText.Bound(result.Reason ?? "lifecycle_failed", ProviderReasonChars);
        return result.Action is { Length: > 0 } action ? $"{why} — {WireText.Bound(action, ProviderActionChars)}" : why;
    }

    private readonly record struct ReadinessFailure(
        ReadinessMiss Miss = ReadinessMiss.NeverHealthy, IReadOnlyList<string>? Reported = null,
        long Window = 0, string? Error = null)
    {
        public string Reason => Miss switch
        {
            ReadinessMiss.ModelNotReported => SwitchReason.RefreshOmits,
            ReadinessMiss.WindowTooSmall => SwitchReason.WindowTooSmall,
            ReadinessMiss.RefreshUnanswered => SwitchReason.RefreshUnanswered,
            ReadinessMiss.Error => SwitchReason.SwitchError,
            ReadinessMiss.NativeObservation => SwitchReason.SwitchError,
            ReadinessMiss.LoadedProfile => SwitchReason.SwitchError,
            _ => SwitchReason.HealthTimeout,
        };
    }

    private readonly record struct LegacyReadinessObservation(
        ModelsObservation? Models = null, ServingCapacity Serving = default, ReadinessFailure Failure = default);

    private static bool MatchesAdoptionProfile(LifecycleTarget target, ServingCapacity serving) =>
        serving.Model == target.Model &&
        (target.Definition.ServingProfiles.FirstOrDefault(p => p.Name == target.EffectiveProfile) is not { } tier ||
            serving.ContextWindow == tier.MaxContext && serving.TotalSlots == tier.MaxSlots);

    private async Task<bool> ProbeAdoptionAsync(SwitchState state, LifecycleTarget target, CancellationToken ct)
    {
        using var deadline = ReadinessDeadline.Start(ReadinessClockForTests, _lifecycle!.NativeProvider!.ObservationTimeout, ct);
        try
        {
            var models = await deadline.ObserveAsync(ReadReadinessModelsAsync).ConfigureAwait(false);
            if (models.Outcome != ReadinessStepOutcome.Completed) return false;
            var health = await deadline.ObserveAsync(ReadReadinessStateAsync).ConfigureAwait(false);
            if (health.Outcome != ReadinessStepOutcome.Completed || !health.Observation.Satisfied) return false;
            var serving = BindServing(models.Observation, health.Observation);
            if (!MatchesAdoptionProfile(target, serving) || !WindowFits(serving.WindowFor(target.Model), state.RequiredWindow))
                return false;
            state.AdoptionProbe = new(models.Observation, serving);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return false;
        }
    }

    private async Task RunLegacyReadinessAsync(SwitchState state, LifecycleTarget target,
        ReadinessPolicy readiness, string providerKind, CancellationToken stop)
    {
        var timeout = readiness.Timeout;
        if (state.AdoptionOwner is { } owner && owner.Budget.Remaining is var remaining && remaining != Timeout.InfiniteTimeSpan)
            timeout = Shorter(timeout, remaining);
        using var deadline = ReadinessDeadline.Start(ReadinessClockForTests, timeout, stop);
        var lastMiss = default(ReadinessFailure);
        var result = default(LegacyReadinessObservation);
        try
        {
            if (state.AdoptionProbe is { } verified) result = verified;
            else if (!readiness.ProveHealth && state.AdoptionOwner is null)
            {
                result = await ReadTrustedLaunchAsync(target, state.RequiredWindow, deadline).ConfigureAwait(false);
                // A retired or out-of-budget read returns no diagnosis.
                lastMiss = result.Failure;
            }
            else
            {
                while (true)
                {
                    try
                    {
                        // Changing one half of a round cannot replace the diagnosis from its last complete round.
                        var modelsStep = await deadline.ObserveAsync(ReadReadinessModelsAsync).ConfigureAwait(false);
                        if (modelsStep.Outcome != ReadinessStepOutcome.Completed) break;
                        var stateStep = await deadline.ObserveAsync(ReadReadinessStateAsync).ConfigureAwait(false);
                        if (stateStep.Outcome != ReadinessStepOutcome.Completed) break;

                        var models = modelsStep.Observation;
                        var stateProbe = stateStep.Observation;
                        var serving = BindServing(models, stateProbe);
                        var window = serving.WindowFor(target.Model);
                        if (!stateProbe.Satisfied)
                            lastMiss = new(ReadinessMiss.NeverHealthy);
                        else if (!models.LoadedModels.Contains(target.Model, StringComparer.OrdinalIgnoreCase))
                            lastMiss = new(ReadinessMiss.ModelNotReported, models.LoadedModels);
                        else if (state.AdoptionOwner is not null && !MatchesAdoptionProfile(target, serving))
                        {
                            result = new(Failure: NativeObservationFailure);
                            break;
                        }
                        // The floor in this observation is deliberate: it keeps the resident-satisfaction reading,
                        // and the observation only decides whether the prove-health loop keeps polling. What the
                        // launch may certify is proved exactly at the publication sites (§5.3.1).
                        else if (!WindowFits(window, state.RequiredWindow) ||
                            (state.ExactProfile is { } exact && !serving.Matches(target.Model, exact)))
                            lastMiss = new(ReadinessMiss.WindowTooSmall, Window: window);
                        else
                        {
                            result = new(models, serving);
                            break;
                        }
                    }
                    catch (HttpRequestException) when (!stop.IsCancellationRequested)
                    {
                        // A newly started container can be running before its listener binds.
                        // Keep the original readiness deadline while retrying this incomplete round.
                        lastMiss = new(ReadinessMiss.NeverHealthy);
                    }

                    if (Capability.NativeWatch is { } pending && _lifecycle?.NativeProvider is { } provider)
                    {
                        var resident = await provider.ObserveContainerAsync(pending.Launch, deadline,
                            provider.ObservationTimeout).ConfigureAwait(false);
                        if (resident.Outcome != ReadinessStepOutcome.Completed) break;
                        if (resident.Observation.Status == NativeContainerStatus.Invalid ||
                            resident.Observation.Status == NativeContainerStatus.Running && resident.Observation.Stamp != pending.Baseline)
                        {
                            result = new(Failure: NativeObservationFailure);
                            break;
                        }
                        if (resident.Observation.Status != NativeContainerStatus.Running)
                        {
                            lastMiss = NativeObservationFailure;
                            if (await deadline.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false) != ReadinessStepOutcome.Completed) break;
                            continue;
                        }
                    }
                    var delay = await deadline.DelayAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    if (delay != ReadinessStepOutcome.Completed) break;
                }
            }
        }
        catch (Exception ex)
        {
            result = new(Failure: new(ReadinessMiss.Error, Error: WireText.Bound(ex.Message, ProviderReasonChars)));
        }

        BeforeReadinessCommitForTests?.Invoke();
        var commit = Capability.NativeWatch is { } watch && _lifecycle?.NativeProvider is { } native
            ? await ConfirmNativeReadinessAsync(state, result, lastMiss, watch, native, deadline, stop).ConfigureAwait(false)
            : CommitLegacyReadiness(state, result, lastMiss, deadline);
        if (commit is { } settled) DeliverLegacyReadiness(state, target, readiness, providerKind, settled);
    }

    private Task<ModelsObservation> ReadReadinessModelsAsync(CancellationToken ct) =>
        FetchModelsObservationAsync(ct, HttpCompletionOption.ResponseHeadersRead);

    private Task<ServingProbe> ReadReadinessStateAsync(CancellationToken ct) =>
        ReadServingProbeAsync(ct, HttpCompletionOption.ResponseHeadersRead);

    private async Task<LegacyReadinessObservation> ReadTrustedLaunchAsync(
        LifecycleTarget target, long requiredWindow, ReadinessDeadline deadline)
    {
        ReadinessStep<ModelsObservation> modelsStep;
        try { modelsStep = await deadline.ObserveAsync(ReadReadinessModelsAsync).ConfigureAwait(false); }
        catch (Exception) { return new(Failure: new(ReadinessMiss.RefreshUnanswered)); }
        if (modelsStep.Outcome != ReadinessStepOutcome.Completed) return default;

        var models = modelsStep.Observation;
        if (!models.LoadedModels.Contains(target.Model, StringComparer.OrdinalIgnoreCase))
            return new(Failure: new(ReadinessMiss.ModelNotReported, models.LoadedModels));

        if (requiredWindow == 0) return new(models);

        var probeMs = _def.Health?.TimeoutMs ?? TrustStateReadCapMs;
        var stepCap = TimeSpan.FromMilliseconds(probeMs == Timeout.Infinite
            ? TrustStateReadCapMs : Math.Min(TrustStateReadCapMs, probeMs));
        var stateStep = await deadline.ObserveAsync(ReadReadinessStateAsync, stepCap).ConfigureAwait(false);
        if (stateStep.Outcome != ReadinessStepOutcome.Completed) return default;

        var serving = BindServing(models, stateStep.Observation);
        var window = serving.WindowFor(target.Model);
        return WindowFits(window, requiredWindow)
            ? new(models, serving)
            : new(Failure: new(ReadinessMiss.WindowTooSmall, Window: window));
    }

    private readonly record struct LegacyReadinessCommit(bool Ready, ReadinessFailure Failure, TaskCompletionSource Delivery);

    private LegacyReadinessCommit? CommitLegacyReadiness(SwitchState state, LegacyReadinessObservation result,
        ReadinessFailure lastMiss, ReadinessDeadline deadline, NativeLaunchWatch? qualifiedWatch = null,
        ReadinessDeadline? dockerDeadline = null, SwitchAttemptId? invalidNativeAttempt = null,
        DockerLifecycleProvider? native = null)
    {
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = result.Failure;
        ReadinessStepOutcome settlement;
        bool ready;
        lock (_transitionGate)
        {
            if (_stopped || !ReferenceEquals(_switchState, state) || state.End != SwitchEnd.Pending) return null;

            var nativeWatch = Capability.NativeWatch;
            if (invalidNativeAttempt is { } invalid && nativeWatch is { } currentWatch &&
                currentWatch.Attempt == invalid && currentWatch.Health != NativeLaunchHealth.Invalid)
            {
                var current = Capability;
                nativeWatch = currentWatch with { Health = NativeLaunchHealth.Invalid };
                Volatile.Write(ref _cap, WithoutProbeProof(current) with { NativeWatch = nativeWatch });
            }
            var watchAllowsReady = nativeWatch is null || qualifiedWatch is not null &&
                nativeWatch.Attempt == qualifiedWatch.Attempt && nativeWatch.Health == NativeLaunchHealth.Pending &&
                native is not null && native.CanRestoreIncumbentUnderGate(nativeWatch.Launch.ContainerId);
            var window = result.Serving.WindowFor(state.Model!);
            while (true)
            {
                if (watchAllowsReady && result.Models is { } models && WindowFits(window, state.RequiredWindow) &&
                    (state.ExactProfile is not { } exact || result.Serving.MatchesExactly(state.Model!, exact)))
                {
                    // The shared writer rechecks the deadline after preparing the snapshot, before its CAS.
                    ready = TryPublishClaimProof(state, models, result.Serving, deadline, out settlement,
                        qualifiedWatch, dockerDeadline);
                    if (!ready && settlement == ReadinessStepOutcome.Completed) continue;
                    if (!ready && dockerDeadline is { IsExpired: true } && !deadline.IsExpired)
                    {
                        settlement = ReadinessStepOutcome.Completed;
                        failure = NativeObservationFailure;
                    }
                }
                else
                {
                    ready = false;
                    settlement = deadline.OutcomeForSettlement;
                    if (result.Models is not null)
                        failure = watchAllowsReady ? new(ReadinessMiss.WindowTooSmall, Window: window) : NativeObservationFailure;
                }

                if (settlement == ReadinessStepOutcome.Stopped) return null;
                break;
            }

            if (!ready)
            {
                failure = settlement switch
                {
                    ReadinessStepOutcome.Expired => lastMiss,
                    ReadinessStepOutcome.StepExpired when failure.Miss != ReadinessMiss.NativeObservation =>
                        new(ReadinessMiss.WindowTooSmall),
                    _ => failure,
                };
                state.FailureReason = failure.Reason;
                RememberNativeFailure(state);
                PublishUnknown();
            }

            state.ReadinessOutcomeDelivered = delivery;
            state.End = ready ? SwitchEnd.Ready : SwitchEnd.Failed;
        }

        return new(ready, failure, delivery);
    }

    private void DeliverLegacyReadiness(SwitchState state, LifecycleTarget target, ReadinessPolicy readiness,
        string providerKind, LegacyReadinessCommit commit)
    {
        try
        {
            if (commit.Ready)
            {
                try { AfterSwitchProofForTests?.Invoke(); }
                finally
                {
                    Emit("engine.switch_completed", model: target.Model,
                        reason: state.Profile is null ? null : $"profile={WireText.Ident(state.Profile)}",
                        value: _clock.GetElapsedTime(state.Timing!.StartedAt).TotalMilliseconds);
                }
            }
            else
            {
                Emit("engine.switch_failed", model: target.Model,
                    reason: DescribeReadinessFailure(target, state.RequiredWindow, readiness, providerKind, commit.Failure));
            }
        }
        finally
        {
            try { EndSwitch(state, commit.Ready); }
            finally { commit.Delivery.TrySetResult(); }
        }
    }

    private string DescribeReadinessFailure(LifecycleTarget target, long requiredWindow, ReadinessPolicy readiness,
        string providerKind, ReadinessFailure failure)
    {
        var askedFor = WireText.Ident(target.Model);
        var engine = WireText.Ident(_def.Name);
        var timeout = $"health_timeout after {readiness.Timeout.TotalMilliseconds:F0}ms";
        return failure.Miss switch
        {
            ReadinessMiss.ModelNotReported when readiness.ProveHealth =>
                $"{timeout} — refresh_omits_{askedFor}: engine {engine} kept answering but never " +
                $"reported {askedFor} serving inside the readiness window: it said " +
                $"[{DescribeReportedNames(failure.Reported!)}] and none of those names is {askedFor}: the catalog name " +
                "has to match what the engine reports (an alias is the router's spelling, not the engine's); " +
                "the router does not retry or fall back to another engine",
            ReadinessMiss.ModelNotReported =>
                $"refresh_omits_{askedFor}: engine {engine} answered /models after the launch with " +
                $"[{DescribeReportedNames(failure.Reported!)}] and none of those names is {askedFor}: the catalog name " +
                "has to match what the engine reports (an alias is the router's spelling, not the engine's); " +
                "the router does not retry or fall back to another engine",
            ReadinessMiss.WindowTooSmall when readiness.ProveHealth =>
                $"{timeout} — {DescribeWindowMiss(failure.Window, requiredWindow)}: engine {engine} " +
                $"serves {askedFor} in a context window too small for this request: " +
                "give the engine a ladder tier that fits or ask a smaller profile; " +
                "the router does not retry or fall back to another engine",
            ReadinessMiss.WindowTooSmall =>
                $"{DescribeWindowMiss(failure.Window, requiredWindow)} (the caller is told window_too_small)",
            ReadinessMiss.RefreshUnanswered =>
                $"refresh_unanswered_{askedFor}: engine {engine} did not answer /models after the launch, " +
                $"so nothing confirms it serves {askedFor}: with wait_health = false the exec's exit status is " +
                "all the router is given, and an exit code is not a loaded model — check that the select " +
                "command leaves the engine answering /models; the router does not retry or fall back to another engine",
            ReadinessMiss.Error or ReadinessMiss.LoadedProfile => failure.Error!,
            ReadinessMiss.NativeObservation => "Docker did not confirm the exact started container.",
            _ => $"{timeout} — engine {engine} never reported {askedFor} serving: " +
                $"inspect what the {providerKind} provider started " +
                "(for docker: containers labelled arboreal.engine=<name>) or raise the wait timeout; " +
                "the router does not retry or fall back to another engine",
        };
    }

    private static string DescribeWindowMiss(long window, long requiredWindow) => window > 0
        ? $"window_{window}_lt_{requiredWindow}"
        : $"window_unreported_for_{requiredWindow}";

    public string? CanonicalModel(string requested)
    {
        foreach (var (name, md) in _catalog)
        {
            if (name.Equals(requested, StringComparison.OrdinalIgnoreCase)) return name;
            if (md.Aliases.Contains(requested, StringComparer.OrdinalIgnoreCase)) return name;
        }
        return null;
    }

    public void RecordPrefix(IReadOnlyList<string> messages)
    {
        _trie.Record(PrefixNormalizer.NormalizeMessages(messages));
    }

    public long MatchedPrefixChars(IReadOnlyList<string> messages)
    {
        if (messages.Count == 0) return 0;
        var (matched, _, _) = _trie.Match(PrefixNormalizer.NormalizeMessages(messages));
        return matched;
    }

    public void RecordSuffix(IReadOnlyList<string> messages, uint suffixHash, int suffixLen)
        => _trie.RecordSuffix(PrefixNormalizer.NormalizeMessages(messages), suffixHash, suffixLen);

    public (long Matched, uint SuffixHash, int SuffixLen) MatchPrefix(IReadOnlyList<string> messages)
    {
        var (matched, suffixHash, suffixLen) = _trie.Match(PrefixNormalizer.NormalizeMessages(messages));
        return (matched, suffixHash, suffixLen);
    }

    public void BeginRequest(long promptChars)
    {
        Interlocked.Increment(ref _admissionClaims);
        Interlocked.Increment(ref _inflight);
        Interlocked.Add(ref _tokenProxy, promptChars);
    }

    public void EndRequest(long promptChars)
    {
        Interlocked.Decrement(ref _inflight);
        Interlocked.Add(ref _tokenProxy, -promptChars);
    }

    // Historical switch readiness and switch timing do not enter this decision.
    // The first-send claim and engine-local stop use the same short ordering.
    // A named use starts at queue removal and extends through the last physical sender.
    // A switch that needs a new claim checks this count under the same transition gate.
    private int _namedUses;
    private TaskCompletionSource? _namedDrain;
    internal bool HasNamedUse => Volatile.Read(ref _namedUses) > 0;

    internal bool TryReserveNamedUse(DispatchObservation expected)
    {
        lock (_transitionGate)
        {
            if (_stopped || _retiring || Capability.SwitchingToward is not null ||
                !IsDispatchObservationCurrent(expected)) return false;
            _namedUses++;
            return true;
        }
    }

    internal void RetainNamedUse()
    {
        lock (_transitionGate) _namedUses++;
    }

    internal void ReleaseNamedUse()
    {
        lock (_transitionGate)
        {
            if (--_namedUses != 0) return;
            _namedDrain?.TrySetResult();
            _namedDrain = null;
        }
    }

    internal bool TryBeginForward(CancellationToken ct, bool named = false)
    {
        if (_hostEnrollment?.Manager is not null || !TryBeginHttpUse(ct, inference: true)) return false;
        if (named) RetainNamedUse();
        return true;
    }

    internal bool RequestedProfileReady(QueuedRequest candidate)
    {
        var named = candidate.Request.Options?.Profile;
        if (named is null && !UsesVllmArgv && _hostEnrollment?.Manager is null) return true;
        var cap = Capability;
        if (UsesVllmArgv && cap.NativeWatch is not { Health: NativeLaunchHealth.Eligible, ServingProcess: not null })
            return false;
        var exact = named is null ? null : GetProfile(candidate.Ticket.Model, named);
        var window = named is null && !UsesVllmArgv ? candidate.ExplicitContextMinimum : candidate.RequiredContextTokens;
        return (named is null || exact is not null && MatchesNamedResident(candidate.Ticket.Model, exact, cap)) &&
            ReadyIn(cap, candidate.Ticket.Model, window, exact);
    }

    internal static RouterRequestException ProfileNoLongerReady() =>
        new("switch_failed", "The selected serving profile is no longer ready.");

    internal async ValueTask VerifyRequestedProfileAsync(QueuedRequest candidate, CancellationToken ct)
    {
        if (!RequestedProfileReady(candidate)) throw ProfileNoLongerReady();
        if (!UsesVllmArgv) return;
        var expected = Capability;
        if (expected.NativeWatch is not { Baseline: { } baseline, ServingProcess: { } process } watch ||
            _lifecycle?.NativeProvider is not { } native) throw ProfileNoLongerReady();
        using var deadline = ReadinessDeadline.Start(_clock, native.ObservationTimeout, ct);
        var read = await native.ObserveVllmAsync(watch.Launch, baseline, deadline).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (read.Outcome != ReadinessStepOutcome.Completed || read.Observation.Evidence is not { } observed ||
            observed.Process != process || observed.Slots != expected.Serving.TotalSlots)
        {
            var invalid = read.Observation.Status == NativeContainerStatus.Invalid ||
                read.Observation.Evidence is { } changed && changed.Process != process;
            var change = CommitNativePoll(expected, watch, invalid ? NativeLaunchHealth.Invalid : NativeLaunchHealth.Unknown);
            if (change is { } serving) NotifyServingChange(serving.Previous, serving.Current);
            throw ProfileNoLongerReady();
        }
        if (Capability.NativeWatch?.Attempt != watch.Attempt || !RequestedProfileReady(candidate))
            throw ProfileNoLongerReady();
    }

    internal int HttpUsesForTests
    {
        get { lock (_transitionGate) return _httpUses; }
    }

    internal void EndForward(bool named = false)
    {
        if (named) ReleaseNamedUse();
        EndHttpUse(inference: true);
        NudgeDispatch();
        AfterForwardReleaseForTests?.Invoke();
    }

    private bool TryBeginHttpUse(CancellationToken ct, bool inference = false)
    {
        lock (_transitionGate)
        {
            if (_stopped || _retiring || ct.IsCancellationRequested) return false;
            if (inference) _inferenceUses++;
            _httpUses++;
            return true;
        }
    }

    private void EndHttpUse(bool inference = false)
    {
        lock (_transitionGate)
        {
            if (inference) _inferenceUses--;
            _httpUses--;
            if (_httpUses != 0 || !_closeHttpWhenIdle) return;
            _closeHttpWhenIdle = false;
        }

        try { CloseHttp(); }
        catch (Exception)
        {
            // Stop has transferred disposal to the last user. A close fault cannot escape its
            // detached cleanup or write a second outcome for a ticket that has already finished.
        }
    }

    private void CloseHttpWhenIdle()
    {
        lock (_transitionGate)
        {
            if (_httpUses != 0)
            {
                _closeHttpWhenIdle = true;
                return;
            }
        }
        CloseHttp();
    }

    private void CloseHttp()
    {
        try { _http.Dispose(); }
        finally { _ownedHandler?.Dispose(); }
    }

    public bool HasCapacity => Inflight < EffectiveSlotLimit(Capability);

    internal readonly record struct DispatchObservation(
        EngineCapability Capability, int Inflight, long InflightTokenProxy, int SlotLimit, long AdmissionClaims)
    {
        internal bool HasCapacity => Inflight < SlotLimit;
    }

    internal DispatchObservation ReadDispatchObservation()
    {
        var capability = Capability;
        var claims = AdmissionClaims;
        return new DispatchObservation(capability, Inflight, InflightTokenProxy, EffectiveSlotLimit(capability), claims);
    }

    internal bool IsDispatchObservationCurrent(DispatchObservation expected)
    {
        var capability = Capability;
        return ReferenceEquals(capability, expected.Capability) && capability.Version == expected.Capability.Version &&
            Inflight == expected.Inflight;
    }

    /// <summary>Admission ceiling: the tighter of the configured max_inflight_per_engine and the
    /// slots the engine currently advertises (profile switches change the advertised count).
    /// Unknown advertised slots → configured ceiling alone.</summary>
    private int EffectiveSlotLimit(EngineCapability cap) => cap.Serving.TotalSlots > 0
        ? Math.Max(1, Math.Min(_maxInflight, cap.Serving.TotalSlots)) : _maxInflight;

    public HttpClient Http => !_ownedPrivateTransport ? InferenceHttp :
        throw new InvalidOperationException("Owned private transport requires scoped router admission.");

    internal HttpClient InferenceHttp => _hostEnrollment?.Manager is null ? _http :
        throw new InvalidOperationException("This runtime uses the enrolled issuer for upstream requests.");
    public string? ApiKey => _apiKey;

    internal void Emit(string kind, string? model = null, string? reason = null, double? value = null)
        // The pieces of a reason are capped piece by piece at the sentence sites, so the part that distinguishes it
        // survives a cut. The ceiling for one whole reason is applied at the ring's single append funnel, which is
        // what makes that ceiling a property of every producer rather than of this emitter alone.
        => _events.Append(new RouterEvent
        {
            Kind = kind,
            Engine = Name,
            Model = model,
            Reason = reason,
            Value = value,
        });

    public async ValueTask DisposeAsync()
    {
        SwitchState stopping;
        bool wake;
        lock (_transitionGate)
        {
            if (_stopped) return;
            stopping = _switchState;
            wake = stopping.Model is not null && stopping.End == SwitchEnd.Pending;
            if (wake)
            {
                stopping.End = SwitchEnd.RouterStopped;
                SealAttempt(stopping, stopping.End);
            }
            Volatile.Write(ref _stopped, true);
            _restoredIncumbent = null;
            if (!_retiring) WithdrawDiscoveryOnStop();
        }
        AfterStopDecisionForTests?.Invoke();

        // The cause is already visible whether readiness or cancellation wakes the waiter. The transaction
        // still owns the marker until it unwinds.
        if (wake) stopping.Ready.TrySetResult();
        try
        {
            // In-flight switches still hold this token. Cancel it without disposing the source.
            _switchCts.Cancel();
        }
        finally
        {
            try
            {
                // Counted HTTP uses own the client through their local response cleanup.
                CloseHttpWhenIdle();
            }
            finally
            {
                // The switch lock remains valid for waiters cancelled by the linked token.
                _lifecycle?.Dispose();
                AfterLifecycleDisposeForTests?.Invoke();
            }
        }
        await Task.CompletedTask;
    }
}

/// <summary>Health probe Expect DSL: "path OP value" — dotted path into the probe JSON,
/// OP in &gt; &gt;= == != &lt; &lt;=; value is a number or quoted string. Also accepts bare "path"
/// (property must exist and be truthy).</summary>
public static class ProbeExpect
{
    public static bool Evaluate(JsonElement root, string expect)
    {
        var trimmed = expect.AsSpan().Trim();
        var opIdx = IndexOfAnyOp(trimmed, out var op);
        if (opIdx < 0)
        {
            if (!TryResolvePath(root, trimmed.ToString(), out var leaf)) return false;
            return leaf.ValueKind switch
            {
                JsonValueKind.Number => leaf.GetDouble() != 0,
                JsonValueKind.True => true,
                JsonValueKind.String => leaf.GetString() is { Length: > 0 } && leaf.GetString() != "false",
                _ => false,
            };
        }
        var path = trimmed[..opIdx].Trim().ToString();
        var rhs = trimmed[(opIdx + op.Length)..].Trim();
        if (!TryResolvePath(root, path, out var l))
        {
            // Numeric comparisons against missing paths fail (never crash).
            return false;
        }
        if (rhs.Length > 1 && (rhs[0] == '"' || rhs[0] == '\''))
        {
            var s = rhs[1..^1].ToString();
            if (l.ValueKind != JsonValueKind.String) return false;
            return op switch
            {
                "==" => string.Equals(l.GetString(), s, StringComparison.Ordinal),
                "!=" => !string.Equals(l.GetString(), s, StringComparison.Ordinal),
                _ => false,
            };
        }
        if (!double.TryParse(rhs, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return false;
        if (l.ValueKind != JsonValueKind.Number) return false;
        var lv = l.GetDouble();
        return op switch
        {
            ">" => lv > r,
            ">=" => lv >= r,
            "==" => lv == r,
            "!=" => lv != r,
            "<" => lv < r,
            "<=" => lv <= r,
            _ => false,
        };
    }

    private static int IndexOfAnyOp(ReadOnlySpan<char> span, out string op)
    {
        var candidates = new[] { ">=", "<=", "==", "!=", ">", "<" };
        var found = -1;
        op = "";
        var bestPos = int.MaxValue;
        foreach (var c in candidates)
        {
            var idx = span.IndexOf(c, StringComparison.Ordinal);
            if (idx >= 0 && idx < bestPos)
            {
                bestPos = idx;
                found = idx;
                op = c;
            }
        }
        return found;
    }

    public static bool TryResolvePath(JsonElement root, string dottedPath, out JsonElement value)
    {
        value = default;
        var cur = root;
        foreach (var seg in dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (cur.ValueKind == JsonValueKind.Object)
            {
                if (!cur.TryGetProperty(seg, out cur)) return false;
            }
            else if (cur.ValueKind == JsonValueKind.Array &&
                     int.TryParse(seg, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                     index < cur.GetArrayLength())
            {
                cur = cur[index];
            }
            else return false;
        }
        if (cur.ValueKind == JsonValueKind.Null) return false;
        value = cur;
        return true;
    }
}
