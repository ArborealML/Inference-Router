using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

/// <summary>Runtime for one configured engine: health probing, loaded-model detection,
/// prefix trie, switch execution (serialized per engine), busy-ness accounting.</summary>
public sealed class EngineRuntime : IAsyncDisposable, ICapabilitySource
{
    private readonly EngineDefinition _def;
    private readonly RouterConfig _config;
    private readonly EventRing _events;
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _ownedHandler;   // disposed with the client
    private readonly string? _apiKey;
    private readonly PrefixTrie _trie;
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly EngineLifecycleCoordinator _lifecycle;
    private readonly int _maxInflight;

    /// <summary>The one place health, reported models and the switch marker live. Written only by Publish*,
    /// which publish a whole snapshot through a CAS loop, so a reader never gets a state no writer ever
    /// wrote and never sees data change without its version moving with it.</summary>
    private EngineCapability _cap = EngineCapability.Initial;
    private SwitchState _switchState = new();   // accessed via Volatile.Read/Write

    // Test seam for switch-start ordering: invoked inside the transition gate immediately after the switch
    // state is installed, so a test can read what the shared snapshot said at the exact moment the engine was
    // claimed. Null everywhere else; one null check on a path that runs once per switch.
    internal Action? AfterClaimForTests;

    /// <summary>More ordering seams, null everywhere outside tests (one null check each, on a once-per-switch
    /// path). <see cref="BeforeSwitchWaitForTests"/> runs on the way to waiting for the claim, which is where a
    /// second caller has to be parked for single-flight claiming to be testable without a sleep. The argument to
    /// <see cref="AfterEndCommitForTests"/> says whether the call happens while the transition gate is held: it
    /// is false for a normal switch end *and* for the rollback of a failed claim, which is the ordering those two
    /// paths must keep — the readiness settle and the dispatch nudge do not belong inside a lock that every later
    /// claim queues behind. <see cref="BeforeScheduleSwitchForTests"/> runs at the scheduling boundary, so a
    /// synchronous failure there is testable; a real thread-pool rejection is not, and is not claimed.</summary>
    internal Action? BeforeSwitchWaitForTests;
    internal Action<bool>? AfterEndCommitForTests;
    internal Action? BeforeScheduleSwitchForTests;

    /// <summary>Runs on a waiter immediately after it is woken from the readiness wait and **before** it reads
    /// the snapshot that decides what to answer, with the argument saying which wake-up it got: <c>true</c> when
    /// the engine's switch token cancelled the wait, <c>false</c> when the switch settled it. A test parks here to
    /// make the ordering the answer depends on — switch settled, then the waiter decides — a fact it observes
    /// rather than a fact it hopes the scheduler produces. Null outside tests.</summary>
    internal Action<bool>? AfterReadyWaitForTests;

    /// <summary>Pairs a switch-state change with its marker change and nothing else. The two are read by
    /// different consumers — the scheduler's view takes "switching" from the capability marker
    /// (RouterCore.BuildEngineInfos), the switch machinery from <c>_switchState</c> — so they have to move
    /// together: state-installed-but-marker-absent lets a dispatch pass into an engine whose switch has
    /// already been claimed, and marker-cleared-but-state-live tells a dispatcher the engine is free while
    /// a model load is still running. Taken only around await-free work, so it is a short critical section,
    /// and always acquired *after* <c>_switchLock</c>, never the other way round: EnsureLoadedAsync calls
    /// EndSwitch while holding the switch lock, so the reverse order would deadlock it.</summary>
    private readonly object _transitionGate = new();

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
    // Active context window in tokens (0 = engine does not report it → no constraint),
    // active slot count (0 = unknown → fall back to configured max_inflight), and the
    // catalog profile name inferred from the window.
    private long _activeContextWindow;
    private int _activeSlots;
    private string? _activeProfileName;

    public long ActiveContextWindow => Volatile.Read(ref _activeContextWindow);
    public int ActiveSlots => Volatile.Read(ref _activeSlots);
    public string? ActiveProfile => Volatile.Read(ref _activeProfileName);

    private sealed class SwitchState
    {
        public string? Model;
        /// <summary>Set when the switch targets a serving profile (context-window escalation)
        /// rather than a model load; carries "profile:<name>" for observability.</summary>
        public string? Profile;
        public TaskCompletionSource<bool> Ready = new(TaskCreationOptions.None);
        public DateTimeOffset StartedAt;
        /// <summary>True when the switch settled while the engine's own switch token was already cancelled,
        /// i.e. the router stopped under it. Written by <see cref="EndSwitch"/> **before** the readiness task
        /// is settled, so a waiter woken by that task always sees the value it was settled with.</summary>
        public bool CancelledByShutdown;
        /// <summary>The reason category this attempt ended with, written by <see cref="RunSwitchAsync"/> at the
        /// same place it emits the feed event — **before** the readiness task settles, so a waiter always reads
        /// the reason of the attempt it joined. Deliberately not the operator's sentence: that carries provider
        /// text and stays on the feed; a caller gets the category (§6.1, §5.5).</summary>
        public string? FailureReason;
        public override string ToString() => Model is null ? "none" : Model;
    }

    public EngineRuntime(EngineDefinition def, RouterConfig config, EventRing events, HttpMessageHandler? handler = null,
                         IEngineLifecycleProvider? lifecycle = null, Action? dispatchHint = null)
    {
        _def = def;
        _config = config;
        _events = events;
        _dispatchHint = dispatchHint;
        var ownProvider = lifecycle is null;
        _lifecycle = new EngineLifecycleCoordinator(lifecycle ?? DefaultLifecycleProvider(def), ownsProvider: ownProvider);
        _maxInflight = Math.Max(1, config.Scheduler.MaxInflightPerEngine);
        _trie = new PrefixTrie(config.Scheduler.PrefixTrieMaxNodes);
        _apiKey = ResolveApiKey(def);
        if (handler is null)
        {
            _ownedHandler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.None };
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
    }

    /// <summary>Ceiling on the wait for this engine's first response byte (null = no ceiling). Worked
    /// out once at startup: the request path must not re-derive config.</summary>
    internal TimeSpan? ResponseTimeout { get; }

    /// <summary>Config picks the provider: native Docker lifecycle when the engine declares it, the
    /// delegated selector program otherwise. The router's own HTTP handler is deliberately not reused:
    /// it is bound to the engine endpoint, not the daemon.</summary>
    private static IEngineLifecycleProvider DefaultLifecycleProvider(EngineDefinition def) =>
        def.Lifecycle == EngineLifecycleKind.Docker && def.Docker is { } docker
            ? new DockerLifecycleProvider(docker, def.Name)
            : new DelegatedExecProvider();

    public string Name => _def.Name;
    public EngineDefinition Definition => _def;
    public string LifecycleProvider => _lifecycle.Kind;
    /// <summary>Advisory trace of the last lifecycle transactions on this engine: why a switch was
    /// refused, what was stopped, what was launched. Not part of the wire contract.</summary>
    public IReadOnlyList<string> LifecycleJournal => _lifecycle.Journal;
    public bool Healthy => Volatile.Read(ref _cap).Healthy;
    public string[] LoadedModels => Volatile.Read(ref _cap).LoadedModels;

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
        foreach (var name in _def.Catalog.Keys)
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
        foreach (var name in cap.LoadedModels)
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
    private void PublishProbeResult(long expectedVersion, bool healthy, string[] models)
    {
        while (true)
        {
            var cur = Volatile.Read(ref _cap);
            if (cur.Version != expectedVersion) return;   // someone else published; their answer is fresher
            if (!healthy)
            {
                PublishProbeFailure(expectedVersion);
                return;
            }
            // A switch mutates this engine, so a poll that merely got an answer out of it — possibly from the
            // process about to be stopped — is not evidence of capability. Only the switch's own proof may
            // make a positive claim while the marker is up.
            if (cur.SwitchingToward is not null) return;
            var unchanged = cur.Healthy && SameModels(cur.LoadedModels, models);
            if (unchanged) return;
            var next = cur with
            {
                Version = cur.Version + 1,
                Healthy = true,
                LoadedModels = SameModels(cur.LoadedModels, models) ? cur.LoadedModels : models,
                // An answered probe is an observation: whatever claim put the marker up, this publication is
                // not the price of a transaction, so the provenance does not survive it.
                HealthFromClaim = false,
            };
            if (Interlocked.CompareExchange(ref _cap, next, cur) == cur) return;
        }
    }

    /// <summary>Publish a probe's *negative*: health false, the reported list untouched, and only while
    /// nothing has published since <paramref name="expectedVersion"/> — the version read before the probe went
    /// out. Same fence as the positive answer for the same reason: the poll interval is short, so losing a
    /// stale negative costs one tick, while applying it marks a working engine unknown for a whole interval
    /// and costs that model its engine (§5.5). A probe carries no list evidence, so the models the engine
    /// last actually reported stay as history; only the entitlement to use them moves.</summary>
    private void PublishProbeFailure(long expectedVersion)
    {
        while (true)
        {
            var cur = Volatile.Read(ref _cap);
            if (cur.Version != expectedVersion) return;   // someone else published; their answer is fresher
            if (!cur.Healthy) return;                     // already unknown: there is nothing to withdraw
            // The withdrawal this publishes was authored by a probe, not by a claim, and that distinction is the
            // whole reason the provenance bit exists: a waiter must not read a failed probe as a transaction
            // boundary and keep a proof the engine just contradicted (§5.5, AC #7).
            var unknown = cur with { Version = cur.Version + 1, Healthy = false, HealthFromClaim = false };
            if (Interlocked.CompareExchange(ref _cap, unknown, cur) == cur) return;
        }
    }

    /// <summary>Publish a switch's readiness proof: the reported list and the health that came with it, in
    /// one snapshot, while the switch marker is still up. Health is part of the proof, not a fact for the
    /// next poll to supply: a switch that proved an engine healthy and left it reading unhealthy would be
    /// skipped by the scheduler anyway, and the whole point of the proof is to remove that skip. The marker
    /// is cleared only after this, so no snapshot ever reads "not switching and does not have it" for a
    /// model that was in fact being loaded (§5.5).</summary>
    private void PublishProof(string[] models)
    {
        while (true)
        {
            var cur = Volatile.Read(ref _cap);
            if (cur.Healthy && SameModels(cur.LoadedModels, models)) return;
            var next = cur with
            {
                Version = cur.Version + 1,
                Healthy = true,
                LoadedModels = SameModels(cur.LoadedModels, models) ? cur.LoadedModels : models,
                HealthFromClaim = false,
            };
            if (Interlocked.CompareExchange(ref _cap, next, cur) == cur) return;
        }
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
    private void PublishUnknown()
    {
        while (true)
        {
            var cur = Volatile.Read(ref _cap);
            // Health already false is not always nothing to publish. While a claim is in flight the bit says that
            // claim authored the unknown health, and a switch exit that lays hands on the engine supersedes that
            // claim as the author — a *provenance-only* change, which still has to advance `Version` or a reader
            // could not tell the two states apart (AC #8).
            if (!cur.Healthy && !cur.HealthFromClaim) return;
            var next = cur with { Version = cur.Version + 1, Healthy = false, HealthFromClaim = false };
            if (Interlocked.CompareExchange(ref _cap, next, cur) == cur) return;
        }
    }

    /// <summary>Open a switch in the snapshot — marker up and health unknown in one publication — but only while
    /// nothing has published since <paramref name="expectedVersion"/>, the version of the snapshot the readiness
    /// question was just answered on. What follows is a mutation of the engine, so its previous answer has to stop
    /// being claimed at the moment the mutation is <i>decided</i>, not after it turns out to have failed. Returns
    /// false when it did not claim, and the caller re-decides against the newer snapshot: the health loop and every
    /// switch exit publish <i>outside</i> the transition gate, so a poll that proves the requested model a moment
    /// before the claim has to retire the switch rather than buy a second launch of a model already served (§5.5).</summary>
    private bool TryPublishSwitchStarted(string model, long expectedVersion)
    {
        var cur = Volatile.Read(ref _cap);
        if (cur.Version != expectedVersion) return false;   // someone published: their answer is the one to decide on
        // `Healthy` was true in the snapshot this claim is decided on, so this publication is the author of the
        // withdrawal it makes; starting from an already-unknown engine authors nothing (AC #8).
        var next = cur with
        {
            Version = cur.Version + 1,
            SwitchingToward = model,
            Healthy = false,
            HealthFromClaim = cur.Healthy,
        };
        return Interlocked.CompareExchange(ref _cap, next, cur) == cur;
    }

    /// <summary>Move the switch marker only — used to clear it once a proof has been published. Opening a
    /// switch goes through PublishSwitchStarted, which makes the engine unknown in the same publication.</summary>
    private void PublishSwitchMarker(string? toward)
    {
        while (true)
        {
            var cur = Volatile.Read(ref _cap);
            if (string.Equals(cur.SwitchingToward, toward, StringComparison.Ordinal)) return;
            // Clearing the marker ends the claim, so the claim can no longer be the author of anything; raising
            // one goes through TryPublishSwitchStarted, which sets the provenance from the health it displaced.
            var next = cur with
            {
                Version = cur.Version + 1,
                SwitchingToward = toward,
                HealthFromClaim = toward is null ? false : cur.HealthFromClaim,
            };
            if (Interlocked.CompareExchange(ref _cap, next, cur) == cur) return;
        }
    }

    /// <summary>Do two reported model sets say the same thing? Content rather than identity: a probe
    /// allocates a fresh array every tick, so comparing references made every ordinary poll a version bump,
    /// and a scan straddling a poll then burned its one retry on a poll that had learned nothing (§5.5).
    /// Pairwise and case-insensitive because every consumer of the list compares model names that way. The
    /// scope is deliberate: a reorder, or a name reported twice instead of once, still counts as a change —
    /// nothing in the router treats those as noise today.</summary>
    private static bool SameModels(string[] a, string[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
    public int Inflight => Volatile.Read(ref _inflight);
    private int _inflight;
    public long InflightTokenProxy => Interlocked.Read(ref _tokenProxy);
    private long _tokenProxy;
    public bool Switching => Volatile.Read(ref _switchState).Model is not null;
    public string? SwitchingToward => Volatile.Read(ref _cap).SwitchingToward;

    private static string? ResolveApiKey(EngineDefinition def)
    {
        if (def.ApiKey is not null) return def.ApiKey;
        if (def.ApiKeyFile is not null && File.Exists(def.ApiKeyFile))
            try { return File.ReadAllText(def.ApiKeyFile).Trim(); }
            catch { /* unreadable key file: treated as no key */ }
        return def.ApiKey;
    }

    public void StartHealthLoop(CancellationToken ct) { _ = Task.Run(() => HealthLoop(ct), ct); }

    private async Task HealthLoop(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(_def.Health?.IntervalMs ?? 2_000);
        using var timer = new PeriodicTimer(interval);
        while (true)
        {
            // bootstrap: probe immediately at startup, then on the interval
            // Read the version before the round trip, *outside* the try: both the success publish and the
            // failure publish below are fenced on it, so a poll can never overwrite fresher evidence with
            // its own older answer — in either direction (§5.5).
            var capBefore = Capability.Version;
            try
            {
                // CA2000's "recommended dispose pattern" doesn't track `using var` in an async
                // loop body; the CTS is disposed at each iteration and the await completes inside.
#pragma warning disable CA2000
                using var cts = new CancellationTokenSource(_def.Health?.TimeoutMs ?? 5_000);
#pragma warning restore CA2000
                var models = await FetchLoadedModelsAsync(cts.Token).ConfigureAwait(false);
                PublishProbeResult(capBefore, healthy: true, models);
                Emit("engine.health", value: models.Length);
                // Same payload family feeds the serving gauges (context window, slots). The readiness rule's
                // verdict is deliberately not folded into Healthy: Healthy means this engine's /models probe
                // answered, while health.expect decides whether a *switch* counts as ready — two different
                // questions, and collapsing them would make a passing /models answer look like a passing
                // readiness rule (§5.5). The discard is spelled out so nobody "fixes" it by accident: a
                // failing expect rule moves no health flag, in this loop or anywhere else.
                _ = await ProbeStateAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The /models fetch is the observation that can fail here and the only one that moves health:
                // the last reported model set stays as the last thing the engine actually said, and
                // Healthy=false makes it unknown rather than evidence of absence. Fenced like the positive
                // answer, so a poll that failed while a switch was proving the engine cannot withdraw that
                // proof. It also cannot withdraw the recovery this same tick just published: if the fetch
                // answered and a later line in the try threw, this tick's own publication has moved the
                // version, and the fence drops the negative.
                PublishProbeFailure(capBefore);
                Emit("engine.health", reason: WireText.Bound(ex.Message, ProviderReasonChars), value: 0);
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) { break; }
        }
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

    /// <summary>Loaded model ids from the configured models endpoint (llama.cpp /v1/models shape).
    /// Optionally requires status==loaded and no alias_of.</summary>
    internal async Task<string[]> FetchLoadedModelsAsync(CancellationToken ct)
    {
        using var req = AuthedGet(_def.ModelsEndpoint);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"models endpoint {(int)res.StatusCode}");
        var bodyBytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bodyBytes);
        var list = new List<string>();
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object || m.TryGetProperty("id", out var id) == false) continue;
                if (_def.LoadedRequiresStatusLoaded)
                {
                    if (m.TryGetProperty("alias_of", out var alias) && alias.ValueKind != JsonValueKind.Null) continue;
                    if (!m.TryGetProperty("status", out var status) ||
                        status.ValueKind != JsonValueKind.Object ||
                        !status.TryGetProperty("value", out var sv) || sv.ValueKind != JsonValueKind.String ||
                        sv.GetString() != "loaded") continue;
                }
                if (id.ValueKind == JsonValueKind.String) list.Add(id.GetString()!);
            }
        }
        return list.ToArray();
    }

    /// <summary>Run the configured health probe (GET path + Expect DSL), returns true when satisfied.</summary>
    internal Task<bool> ProbeAsync(CancellationToken ct) => ProbeStateAsync(ct);

    /// <summary>One probe fetch that both evaluates the Expect DSL and refreshes the serving
    /// gauges (active context window, slot count, inferred profile name) from the same payload.</summary>
    internal async Task<bool> ProbeStateAsync(CancellationToken ct)
    {
        var path = _def.Health?.Path ?? "/props";
        try
        {
            using var req = AuthedGet(path);
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return false;
            var probeBytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(probeBytes);
            UpdateServingGauges(doc.RootElement);
            return _def.Health?.Expect is { } expect ? ProbeExpect.Evaluate(doc.RootElement, expect) : true;
        }
        catch
        {
            return false;   // unreachable or malformed payload
        }
    }

    private void UpdateServingGauges(JsonElement root)
    {
        var window = TryReadGauge(root, _def.Health?.ContextPath ?? "n_ctx", out var ctx) ? ctx : 0;
        var slots = TryReadGauge(root, _def.Health?.SlotsPath ?? "total_slots", out var sl) ? sl : 0;
        var previousWindow = ActiveContextWindow;
        if (window > 0) Volatile.Write(ref _activeContextWindow, window);
        if (slots > 0) Volatile.Write(ref _activeSlots, (int)Math.Min(int.MaxValue, slots));
        var name = InferProfileName(window);
        Volatile.Write(ref _activeProfileName, name);
        if (window > 0 && window != previousWindow)
            Emit("engine.profile", reason: name is null ? "unrecognized_window" : WireText.Ident(name), value: window);
    }

    private static bool TryReadGauge(JsonElement root, string dottedPath, out long value)
    {
        value = 0;
        if (!ProbeExpect.TryResolvePath(root, dottedPath, out var leaf)) return false;
        if (leaf.ValueKind == JsonValueKind.Number)
        {
            value = (long)leaf.GetDouble();
            return value > 0;
        }
        if (leaf.ValueKind == JsonValueKind.String &&
            long.TryParse(leaf.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            value = parsed;
            return true;
        }
        return false;
    }

    /// <summary>Catalog profile whose MaxContext equals the live window (null when unrecognized).</summary>
    private string? InferProfileName(long window)
    {
        if (window <= 0) return null;
        foreach (var md in _def.Catalog.Values)
            foreach (var p in md.ServingProfiles)
                if (p.MaxContext == window) return p.Name;
        return null;
    }

    public IReadOnlyList<ServingProfile> ProfilesForModel(string canonical)
        => _def.Catalog.TryGetValue(canonical, out var md) ? md.ServingProfiles : [];

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

    public ServingProfile? GetProfile(string canonical, string? name)
    {
        if (name is null) return null;
        foreach (var p in ProfilesForModel(canonical))
            if (p.Name.Equals(name, StringComparison.Ordinal)) return p;
        return null;
    }

    /// <summary>How a switch attempt ended for the requests waiting on it. Failed and TimedOut are
    /// different answers: a failed switch will not get better if you wait, and a switch that is still
    /// running usually will, which is exactly the retry decision a caller has to make.</summary>
    public enum SwitchOutcome { Ready, Failed, TimedOut }

    /// <summary>A switch outcome plus what the caller was waiting on, phrased for an error body. The
    /// switch's own one-line reason goes to the event feed as `engine.switch_failed`, where an operator
    /// can read it without a log; this carries the caller-facing half.</summary>
    public sealed record SwitchResult(SwitchOutcome Outcome, string? Detail)
    {
        public bool IsReady => Outcome == SwitchOutcome.Ready;

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
        /// anything — the <c>finally</c> path, a shutdown cancellation folded into a false end — takes the catch-all
        /// name rather than a bare sentence. Exactly one arm of one expression applies that fallback, and the
        /// source audit says so (§5.5).</summary>
        internal static SwitchResult FailedFromRecord(string? recorded) =>
            Failed(recorded is { Length: > 0 } why ? why : SwitchReason.SwitchError);
    }

    /// <summary>Ensure the model is loaded on this engine: no-op when loaded; otherwise performs
    /// (or waits for) the configured switch. Resolves Ready when loaded, Failed when the switch
    /// itself failed, TimedOut when `timeout` ran out while the switch was still going.</summary>
    public Task<SwitchResult> WaitForModelAsync(string model, TimeSpan timeout, CancellationToken ct)
        => EnsureLoadedAsync(model, null, timeout, ct);

    /// <summary>Ensure the model is loaded AND served under a context window that fits the profile.
    /// Exceeding the active window queues behind a profile switch instead of compacting.</summary>
    public Task<SwitchResult> WaitForProfileAsync(string model, ServingProfile profile, TimeSpan timeout, CancellationToken ct)
        => EnsureLoadedAsync(model, profile, timeout, ct);

    /// <summary>Loaded, and (when a profile is required) serving a window that fits it.
    /// An engine that reports no window (gauge 0) is never escalated blind.</summary>
    /// <summary>Loaded, and (when a profile is required) serving a window that fits it.
    /// An engine that reports no window (gauge 0) is never escalated blind.
    /// The answer comes from one current, verified snapshot, not from the reported list alone: a list is
    /// history once a switch has started, and health unknown means the router does not know what is running
    /// now, so neither can license a dispatch (§5.5).</summary>
    public bool IsReadyFor(string canonical, ServingProfile? profile) => ReadyIn(Capability, canonical, profile);

    /// <summary>The same predicate against a snapshot the caller already holds. The claim path needs that form:
    /// the readiness decision and the switch marker have to come from one snapshot, or a poll that proves the
    /// model in between buys a second launch (§5.5).</summary>
    private bool ReadyIn(EngineCapability cap, string canonical, ServingProfile? profile)
    {
        if (!cap.Healthy || cap.SwitchingToward is not null) return false;
        foreach (var name in cap.LoadedModels)
        {
            if (string.Equals(name, canonical, StringComparison.OrdinalIgnoreCase)) return WindowFits(profile);
        }
        return false;
    }

    /// <summary>Live window fits the profile (gauge 0 = unknown → never block on it).</summary>
    private bool WindowFits(ServingProfile? profile)
    {
        if (profile is null) return true;
        var window = ActiveContextWindow;
        return window == 0 || window >= profile.MaxContext;
    }

    /// <summary>Does the proof this caller's **own switch published** still stand? A later claim on the engine
    /// raises its marker and withdraws health in one publication, and neither is an observation about what the
    /// engine serves: it is somebody else's transaction starting. Only a contradicting observation takes the proof
    /// away — the target gone from the reported list, a window that no longer fits, or health withdrawn while no
    /// switch is in flight (a probe, not a claim) (§5.5, the correction to decision-34). Holding authority across
    /// the admission handoff is a different question (TASK-25, TASK-64); this is only about not punishing a caller
    /// for a switch it did not start.</summary>
    private bool OwnProofStillStands(EngineCapability cap, string canonical, ServingProfile? profile) =>
        (cap.Healthy || (cap.SwitchingToward is not null && cap.HealthFromClaim))
        && cap.LoadedModels.Contains(canonical, StringComparer.OrdinalIgnoreCase)
        && WindowFits(profile);

    /// <summary>What the catalog says about a model a **new claim** would run on. Three answers, because the two
    /// catalog questions have different fixes and one name: no entry at all says the model is absent here, and an
    /// entry whose selector cannot be run says the configuration is incomplete. Both are <c>no_select_command</c>
    /// — the caller's action is the same, fix the configuration — and the operator's sentence is what tells them
    /// apart (§5.5).</summary>
    private enum EntryGuard { Ok, Absent, NoUsableSelector }

    /// <summary>Resolve and validate, once for this acquisition attempt, the catalog entry a new claim would run
    /// on. Nothing is published or mutated here, and the predicate for "can this selector be run" is the provider's
    /// own — <see cref="DelegatedExecProvider.HasUsableSelector"/> — so the answer the router gives in front of the
    /// claim and the answer preflight would give inside it cannot drift. Called only where the caller has already
    /// declined to join an in-flight switch: asked anywhere earlier it would refuse a caller that was about to wait
    /// behind a switch this configuration never governed (§5.5, decisions 49 and 51).</summary>
    private EntryGuard ResolveClaimEntry(EngineDefinition def, string canonical, out EngineModelDefinition? entry)
    {
        entry = def.Catalog.TryGetValue(canonical, out var found) ? found : null;
        if (entry is null) return EntryGuard.Absent;
        return _lifecycle.Kind == DelegatedExecProvider.ProviderKind &&
               !DelegatedExecProvider.HasUsableSelector(entry)
            ? EntryGuard.NoUsableSelector
            : EntryGuard.Ok;
    }

    private async Task<SwitchResult> EnsureLoadedAsync(string model, ServingProfile? profileOverride, TimeSpan timeout, CancellationToken ct)
    {
        var canonical = CanonicalModel(model) ?? model;
        if (IsReadyFor(canonical, profileOverride)) return new SwitchResult(SwitchOutcome.Ready, null);
        // No lifecycle owner. Today the single `remote` flag is the whole predicate; the *name* states the
        // relation, so the wire token does not have to change again when reach and ownership split into two
        // config keys. Nothing was attempted and nothing was mutated, so the engine keeps its last proof, and no
        // switch marker went up, so there is no switch to end (§5.5).
        // One definition for both sides of the claim. The runtime holds the definition it was built with, while
        // the switch task re-read the config collection by name — two sources that a same-name definition handed to
        // the constructor, or an entry swapped between the check and the switch, can make disagree. Resolving once
        // here and passing the result down is what stops a refusal decided in front of the claim from being
        // contradicted inside it; re-asking the question later would not, because the later ask would still read the
        // second source (§5.5).
        var def = _config.Engines.TryGetValue(_def.Name, out var cfgDef) ? cfgDef : _def;
        if (def.Remote) return SwitchResult.Failed(SwitchReason.NoLifecycleOwner);

        // Neither catalog question is asked here any more, and the reason is a caller that has nothing to do with
        // the catalog. Both used to sit ahead of the loop, so they answered a caller that had arrived to **join** a
        // switch already running: A claims on a usable entry, the entry is replaced, B asks for the same model and is
        // told `no_select_command` for configuration state that never governed the switch it was about to wait
        // behind — and a refusal is terminal, so B never learns what that switch decided. The two questions (does an
        // entry name this model, and does it name a selector the router can run) move into the acquisition path below,
        // where they are asked only of a caller about to claim. What does not move: one predicate, asked once per
        // acquisition attempt, off the provider's own `HasUsableSelector` so the two answers cannot drift; nothing
        // published or mutated on the way to a refusal, so the refusal costs the engine nothing it had; and the entry
        // read stays the **mutation policy** — a configuration edit landing after it changes no switch already
        // claimed from it, that transaction runs on the entry it was authorised on, and the edit is seen by the next
        // claim (§5.5, decisions 48, 49, 51).

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
        while (true)
        {
            SwitchState state;
            // The outcome of *this* acquisition attempt, selected under the gate and acted on after release: the
            // entry the claim runs on when there is a claim, and the catalog miss when there is not. Emitting inside
            // the switch lock is what TASK-103 #4 forbids — the ring runs every subscriber inside its append with no
            // isolation — so the refusal is chosen inside the gate and spoken outside it.
            EngineModelDefinition? claimEntry = null;
            var entryMiss = EntryGuard.Ok;
            BeforeSwitchWaitForTests?.Invoke();
            // IDISP013 is a false positive at both waits on `waiter`: this source is disposed when the method
            // returns, and every wait on its token is awaited inline before that — nothing here is detached.
#pragma warning disable IDISP013 // await-in-using not applicable: the wait is awaited, not detached
            await _switchLock.WaitAsync(waiter).ConfigureAwait(false);
#pragma warning restore IDISP013
            try
            {
                SwitchState? mine = null;
                Exception? claimFault = null;
                CancellationToken switchToken = default;
                // The read and the claim move together. A switch's end runs outside the switch lock, so this is
                // the point where a second waiter can meet a switch that is committing its last step: reading
                // outside the gate lets this caller coalesce onto a readiness task that resolves a moment later
                // and return "ready" for a model nobody loaded.
                lock (_transitionGate)
                {
                    state = Volatile.Read(ref _switchState);
                    if (state.Model is null)
                    {
                        // Decide and claim from **one** snapshot: readiness is read off `cap`, and the marker goes up
                        // by CAS on `cap.Version`. The health loop and every switch exit publish outside this gate,
                        // so the exchange can lose — and losing means something published while this caller was
                        // deciding, which is re-decided on rather than launched over. A poll that proved *this*
                        // caller's model retires the switch outright: that is the race that used to buy a second
                        // launch of a model that was already served (§5.5).
                        while (true)
                        {
                            var cap = Capability;
                            if (ReadyIn(cap, canonical, profileOverride))
                                return new SwitchResult(SwitchOutcome.Ready, null);

                            // **Prospective validation never precedes a join.** Everything above this line has already
                            // declined to join: the engine is idle, and this caller is an acquirer. Only now is the
                            // catalog asked, and only for this acquisition attempt — a lost exchange below re-reads
                            // capability *and* entry, because something published while this caller was deciding
                            // changes both (§5.5, decision-51).
                            entryMiss = ResolveClaimEntry(def, canonical, out var entry);
                            if (entryMiss != EntryGuard.Ok) break;
                            claimEntry = entry;

                            var candidate = new SwitchState { Model = canonical, Profile = profileOverride?.Name, StartedAt = DateTimeOffset.UtcNow };
                            candidate.Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            if (!TryPublishSwitchStarted(canonical, cap.Version)) continue;

                            // Marker before state, both inside the gate: the scheduler reads "switching" from the
                            // capability snapshot, so installing the switch state before the marker goes up leaves a
                            // window in which a dispatch pass sees an engine whose switch has already been claimed as
                            // idle and sends a request into the engine this switch is about to stop (§5.5). The switch
                            // token is read *before* scheduling: reading it inside the task body races DisposeAsync,
                            // and an ObjectDisposedException thrown before RunSwitchAsync is entered would strand
                            // `Ready` with no finally to settle it, leaving waiters out their ceiling on a switch
                            // that never ran.
                            switchToken = _switchCts.Token;
                            Volatile.Write(ref _switchState, candidate);
                            try { AfterClaimForTests?.Invoke(); }
                            catch (Exception ex) { claimFault = ex; }   // the claim is undone below, outside the gate
                            mine = candidate;
                            break;
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
                        EndSwitch(claim, false);
                        ExceptionDispatchInfo.Capture(fault).Throw();
                    }
                    try
                    {
                        BeforeScheduleSwitchForTests?.Invoke();
                        // IDISP013 again: the switch task is deliberately detached, and it never touches the linked
                        // waiter source above — it runs on the engine's own switch token — so the using scope cannot
                        // dispose anything this task is still using.
#pragma warning disable IDISP013 // await-in-using not applicable: intentionally detached Task, no shared source
                        _ = Task.Run(
                            () => RunSwitchAsync(claim, canonical, profileOverride, claimEntry!, switchToken),
                            // Deliberately not the caller's token: the switch's own token above decides whether the
                            // transaction runs, and a request that hangs up must not be able to abort it halfway
                            // through a model load.
                            CancellationToken.None);
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
                        return SwitchResult.Failed(SwitchReason.SwitchError);
                    }
                    state = claim;
                }
            }
            finally
            {
                _switchLock.Release();
            }

            // Spoken outside both the switch lock and the transition gate: the refusal was selected under the gate
            // and is emitted after release, so a feed subscriber runs with no engine lock held (TASK-103 #4). One
            // `engine.switch_failed` per refused attempt, and nothing else: no marker went up, so there is no
            // switch to end and the engine keeps the proof it had (§5.5).
            if (entryMiss != EntryGuard.Ok)
            {
                Emit("engine.switch_failed", model: canonical,
                    reason: entryMiss == EntryGuard.Absent
                        ? "no_select_command: no catalog entry names this model on this engine — add it under " +
                          "[engine.strata.catalog], move the request to an engine that declares it, or mark this " +
                          "engine remote"
                        : "no_select_command: the catalog entry has no usable select exec — name one with " +
                          "select = { exec = … }, move the model to an engine whose lifecycle the router owns, or " +
                          "mark this engine remote");
                return SwitchResult.Failed(SwitchReason.NoSelectCommand);
            }

            var remaining = deadline == DateTimeOffset.MaxValue
                ? Timeout.InfiniteTimeSpan
                : deadline - DateTimeOffset.UtcNow;
            // InfiniteTimeSpan is -1 ms, so the sentinel must never reach the comparison below: a
            // request with no ceiling would otherwise be "timed out" before it ever waited.
            if (deadline != DateTimeOffset.MaxValue && remaining <= TimeSpan.Zero)
            {
                // Nothing of this caller's own attempt ended, so nothing is quoted back: what was in front of them
                // belongs to another request, and the caller's ceiling is already in the sentence the request path
                // writes around this outcome (§5.5).
                return IsReadyFor(canonical, profileOverride)
                    ? new SwitchResult(SwitchOutcome.Ready, null)
                    : new SwitchResult(SwitchOutcome.TimedOut, null);
            }

            bool ready;
            bool wokeByCancellation;
            try
            {
#pragma warning disable IDISP013 // await-in-using not applicable: the wait is awaited, not detached
                ready = await state.Ready.Task.WaitAsync(remaining, waiter).ConfigureAwait(false);
#pragma warning restore IDISP013
                wokeByCancellation = false;
            }
            catch (TimeoutException)
            {
                // The switch may have finished and loaded the model in the instant before the wait gave
                // up, so re-check rather than trusting the timeout alone. The sentence is the caller's own ceiling
                // and the engine's own state — never the target of the switch they were parked behind (§5.5).
                return IsReadyFor(canonical, profileOverride)
                    ? new SwitchResult(SwitchOutcome.Ready, null)
                    : new SwitchResult(SwitchOutcome.TimedOut,
                        $"still switching after {FormatWait(timeout)}; a switch for another request is in flight");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Not the caller hanging up: the engine's switch token. Fold it into the same decision as a
                // switch that settled false, so the answer does not depend on which of the two reaches the
                // waiter first (the caller's own cancellation still propagates, filter above).
                wokeByCancellation = true;
                ready = false;
            }

            AfterReadyWaitForTests?.Invoke(wokeByCancellation);

            // A switch the router itself stopped is not a model load that failed. The provider sees the
            // engine's switch token, so a shutdown mid-load comes back as a lifecycle failure
            // ("cancelled_during_launch") and the switch settles false: told to this caller as a failed
            // switch, a shutting-down router is described as a broken engine — the same wrong answer a
            // disposed switch lock used to give. Both wake-ups say the same thing: the flag is written before
            // the readiness task settles, and a token-cancelled wake-up is folded into the same branch above.
            if (!ready && (wokeByCancellation || state.CancelledByShutdown))
            {
                // No target is named. This is the switch this caller was waiting behind, which is not
                // necessarily the switch this caller asked for, and the message reaches that caller's error
                // body — another request's model id must not travel in it (§5.5).
                throw new OperationCanceledException("the router stopped while the model was being loaded");
            }

            if (IsReadyFor(canonical, profileOverride)) return new SwitchResult(SwitchOutcome.Ready, null);
            // Four answers at resume, in this order (§5.5). The snapshot still authorises the target: Ready. A caller
            // whose **own** attempt *proved* its target keeps that proof unless something contradicts it: a
            // successor's claim raises the marker and withdraws health in one publication, and that is somebody
            // else's transaction starting, not evidence that the engine stopped serving — parking the caller behind
            // it, or failing it for a switch that worked, both lose a proof the router itself published. This
            // caller's own attempt ended false: Failed, once, because nothing retries inside the engine and a caller
            // with no ceiling would otherwise relaunch the same broken switch forever. Anything else — a proof a
            // later observation contradicted, or a switch that belonged to someone else's target or profile — takes
            // its own turn through the claim path, exactly as decision-9 prescribes, bounded by its own ceiling and
            // never by a refusal.
            if (ready && state.Model == canonical && state.Profile == profileOverride?.Name &&
                OwnProofStillStands(Capability, canonical, profileOverride))
            {
                return new SwitchResult(SwitchOutcome.Ready, null);
            }
            if (!ready && state.Model == canonical && state.Profile == profileOverride?.Name)
            {
                // This attempt ended false, and it was this caller's own: the answer is Failed once, with the
                // reason *category* the attempt recorded and nothing else — not the operator's sentence, which
                // carries provider text and stays on the feed, and not a sentence of the router's own either.
                // Wrapping the token in `switch to <target> failed` described the same target the caller had just
                // named and left the caller's half shaped like prose at the exit that matters most; the door takes
                // the recorded name, so a tenth exit that records something outside the set fails here, at the
                // answer, rather than only in the source audit. An attempt that ended false before recording
                // anything — the `finally` path, a shutdown cancellation folded into a false end — takes the
                // catch-all name, the same answer the never-started attempt gives (§5.5).
                // No retry happens here: the caller who gave no ceiling would otherwise relaunch a switch that is
                // not going to get better.
                return SwitchResult.FailedFromRecord(state.FailureReason);
            }
            // A switch that belonged to another request's target ended: this caller takes its own turn through
            // the claim path. Nothing is remembered for the caller — an expired wait is answered by the ceiling
            // sentence alone, which names this caller's own target and their own ceiling (§5.5).
        }
    }

    // Bounds for what a refusal puts in front of an operator: the feed record is built from values the engine or
    // a provider chose, and the event ring caps entries rather than characters. Identifier caps and the ceiling
    // for one whole reason live in WireText; these are the provider-side caps the feed sentences use. The
    // caller-facing half carries no value from here at all — it carries a SwitchReason token (§5.5).
    private const int ReportedNameCount = 8;
    private const int ReportedNameHeadChars = 24;
    private const int ReportedNamesChars = 500;
    private const int ProviderReasonChars = 160;
    private const int ProviderActionChars = 200;

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
    private void EndSwitch(SwitchState state, bool ready)
    {
        var committed = false;
        lock (_transitionGate)
        {
            if (ReferenceEquals(Volatile.Read(ref _switchState), state))
            {
                Volatile.Write(ref _switchState, new SwitchState());
                if (Volatile.Read(ref _switchState) is { Model: null, Profile: null })
                {
                    PublishSwitchMarker(null);
                    committed = true;
                }
            }
            // The reference guard runs inside the gate for a reason: without it, the switch that just
            // finished can clear the marker a successor published in the gap between this switch writing
            // idle state and this line, which would hide a running model load from the scheduler. A second
            // EndSwitch for the same attempt (the finally block) sees a successor's state and does nothing.
        }
        // Settle the waiters first, then nudge: an injected callback, or the dispatcher the nudge wakes, must not
        // be able to rewrite a switch that has already committed, and a waiter must not stay parked behind a
        // callback that throws. Every Emit has already happened before EndSwitch, so the feed still precedes the
        // wake (decision 7); the hint's own failure costs at most one poll interval, because the next dispatch
        // pass is woken by the next event instead.
        // Recorded before the waiters are woken: a waiter has to be able to tell "the switch failed" from
        // "the router stopped in the middle of it" without racing the shutdown against its own wake-up.
        state.CancelledByShutdown = _switchCts.IsCancellationRequested;
        state.Ready.TrySetResult(ready);
        if (committed)
        {
            try { _dispatchHint?.Invoke(); }
            catch (Exception) { /* a lost nudge is a late dispatch, not a lost switch */ }
            AfterEndCommitForTests?.Invoke(Monitor.IsEntered(_transitionGate));
        }
    }

    /// <summary>Why the prove-health loop ended without readiness, as its last poll saw it: the category the
    /// caller is told follows from this, so an engine that is up and wrong is not reported as an engine that is
    /// still starting (§5.5).</summary>
    private enum ReadinessMiss { NeverHealthy, ModelNotReported, WindowTooSmall }

    private async Task RunSwitchAsync(
        SwitchState state, string model, ServingProfile? profileOverride,
        EngineModelDefinition md, CancellationToken ct)
    {
        // `md` is the entry the claim was authorised on — resolved once, in front of the claim and inside the
        // acquisition gate — not a re-read. There is no missing-entry branch left to guard against: the claim could
        // not have been made without one, and a second ask of the catalog would be a second snapshot of a mutable
        // dictionary, which is the mistake v0.12.44 removed the definition re-read for (§5.5).
        try
        {
            // The entry exists but names no usable selector: that is refused **before** the claim, off this same
            // predicate, so the caller is told `no_select_command` and the engine keeps its last proof. There is
            // deliberately no second ask here. With the refusal in front of the claim and one predicate on both
            // sides of it, a branch at this point could only be reached by a catalog changing under a running
            // switch — and a guard no configuration can reach is a claim about the code the code does not support (§5.5).
            var profileName = profileOverride?.Name ?? md.Profile;
            Emit("engine.switch_started", model: model,
                reason: profileName is null ? null : $"profile={WireText.Ident(profileName)}");

            // One transaction through the provider seam: prove ownership, validate, stop what is ours,
            // launch the exact target. What "launch" means is the provider's business; the ordering is
            // the router's, and it is what keeps a refusal from costing the engine anything it had.
            var target = new LifecycleTarget(_def.Name, model, profileOverride, md);
            var launched = await _lifecycle.SwitchAsync(target, ct).ConfigureAwait(false);
            if (!launched.Succeeded)
            {
                // The short reason goes on the feed for grepping; the provider's one actionable sentence
                // rides along so the operator reading /admin/events does not have to go find a log.
                // Both halves are bounded before they reach the feed: the ring caps entries, not characters,
                // and a provider is free to answer with a sentence of any length.
                var why = WireText.Bound(launched.Reason ?? "lifecycle_failed", ProviderReasonChars);
                var detail = launched.Action is { Length: > 0 } action
                    ? $"{why} — {WireText.Bound(action, ProviderActionChars)}"
                    : why;
                Emit("engine.switch_failed", model: model, reason: detail);
                // The provider's own words stay on the feed; the caller is told only the category.
                state.FailureReason = SwitchReason.LaunchFailed;
                // A failed launch means the engine was stopped first: its previous answer described a
                // process that is gone, so the capability becomes unknown before the marker comes down.
                PublishUnknown();
                EndSwitch(state, false);
                return;
            }
            var readiness = _lifecycle.Readiness(target);
            if (readiness.ProveHealth)
            {
                var deadline = DateTimeOffset.UtcNow + readiness.Timeout;
                // What the last poll actually saw, so the refusal names why readiness never arrived rather than
                // naming the clock. Three ways this loop ends unsatisfied, and the caller's action differs for
                // each: an engine that never said it was healthy is worth retrying later, an engine that stayed up
                // and never named the model has a catalog key that does not match what it reports, and an engine
                // serving the model in too small a window needs a ladder tier. Under the clock alone all three
                // were `health_timeout` — a fixed mismatch reading as a transient one (§5.5).
                var miss = ReadinessMiss.NeverHealthy;
                string[] lastReported = [];
                while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    // Probe first: it refreshes the window gauge that the profile check below reads.
                    var healthy = await ProbeStateAsync(ct).ConfigureAwait(false);
                    var modelsNow = await FetchLoadedModelsAsync(ct).ConfigureAwait(false);
                    if (healthy && modelsNow.Contains(model, StringComparer.OrdinalIgnoreCase) &&
                        WindowFits(profileOverride))
                    {
                        // Published before EndSwitch clears the marker, and as a proof: the switch saw the
                        // model reported, so it publishes the list *and* the health together rather than
                        // leaving a successful switch sitting behind a stale unhealthy flag until the next
                        // poll happens to answer (§5.5).
                        PublishProof(modelsNow);
                        Emit("engine.switch_completed", model: model,
                            reason: state.Profile is null ? null : $"profile={WireText.Ident(state.Profile)}",
                            value: (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds);
                        EndSwitch(state, true);
                        return;
                    }
                    if (!healthy) miss = ReadinessMiss.NeverHealthy;
                    else if (!modelsNow.Contains(model, StringComparer.OrdinalIgnoreCase))
                    {
                        miss = ReadinessMiss.ModelNotReported;
                        lastReported = modelsNow;
                    }
                    else miss = ReadinessMiss.WindowTooSmall;
                    try { await Task.Delay(2_000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                // A readiness timeout is the one failure that has no exit code and no stderr, so the
                // message has to carry the actionable part itself: what to look at, and the fact that
                // the router will not quietly retry or route elsewhere. The clock stays in the operator's
                // sentence whatever the caller is told, so the grep key for an expired readiness window
                // survives the category change (§5.5).
                var window = $"health_timeout after {readiness.Timeout.TotalMilliseconds:F0}ms";
                var askedFor = WireText.Ident(model);
                switch (miss)
                {
                    case ReadinessMiss.ModelNotReported:
                        state.FailureReason = SwitchReason.RefreshOmits;
                        Emit("engine.switch_failed", model: model, reason:
                            $"{window} — refresh_omits_{askedFor}: engine {WireText.Ident(_def.Name)} kept answering but never " +
                            $"reported {askedFor} serving inside the readiness window: it said " +
                            $"[{DescribeReportedNames(lastReported)}] and none of those names is {askedFor}: the catalog name " +
                            "has to match what the engine reports (an alias is the router's spelling, not the engine's); " +
                            "the router does not retry or fall back to another engine");
                        break;
                    case ReadinessMiss.WindowTooSmall:
                        state.FailureReason = SwitchReason.WindowTooSmall;
                        Emit("engine.switch_failed", model: model, reason:
                            $"{window} — window_{ActiveContextWindow}_lt_{profileOverride!.MaxContext}: engine " +
                            $"{WireText.Ident(_def.Name)} serves {askedFor} in a context window too small for the profile the " +
                            "caller asked for: give the engine a ladder tier that fits or ask a smaller profile; " +
                            "the router does not retry or fall back to another engine");
                        break;
                    default:
                        state.FailureReason = SwitchReason.HealthTimeout;
                        Emit("engine.switch_failed", model: model, reason:
                            $"{window} — engine {WireText.Ident(_def.Name)} never reported {askedFor} serving: " +
                            $"inspect what the {_lifecycle.Kind} provider started " +
                            "(for docker: containers labelled arboreal.engine=<name>) or raise the wait timeout; " +
                            "the router does not retry or fall back to another engine");
                        break;
                }
                // The engine was relaunched by now, whatever the last poll made of it, so the list from before the
                // switch is not evidence of what it holds: unknown is the honest answer at all three exits (§5.5).
                PublishUnknown();
                EndSwitch(state, false);
                return;
            }
            // wait_health=false: the provider's exec says it launched, so the router asks what is loaded.
            // An answered refresh that names the model is evidence of the list and of health, so it is published
            // as a proof. Anything less — a list that omits the model, or no answer at all — refuses the switch:
            // the engine was relaunched, so its old list is not evidence of what it holds now, and an exit code
            // is not a loaded model (§5.5, decision-27).
            string[]? refreshed = null;
            try { refreshed = await FetchLoadedModelsAsync(ct).ConfigureAwait(false); }
            catch { /* best-effort refresh: no evidence, so no claim */ }
            if (refreshed is { } freshModels)
            {
                // The refresh is the only evidence this path has, and it has to name the model that was
                // asked for. `wait_health = false` skips the readiness *rule*, never the check that the
                // engine says it holds this model: publishing a proof for a list that omits the target
                // would have the router vouch for a model nothing ever reported, and the switch would end
                // ready on its own say-so. Checked **before** PublishProof because a proof is a published
                // claim: checking afterwards would leave the engine verified for whatever else it holds,
                // and burn the version, on a switch this call is about to refuse (§5.5).
                if (!freshModels.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    // Names the rule and shows what the engine actually said, because the fix is in config:
                    // the catalog key has to match what `/models` reports. Aliases are the router's own
                    // request-side spelling and are never what an engine reports.
                    state.FailureReason = SwitchReason.RefreshOmits;
                    var askedFor = WireText.Ident(model);
                    Emit("engine.switch_failed", model: model, reason:
                        $"refresh_omits_{askedFor}: engine {WireText.Ident(_def.Name)} answered /models after the launch with " +
                        $"[{DescribeReportedNames(freshModels)}] and none of those names is {askedFor}: the catalog name has to match what " +
                        "the engine reports (an alias is the router's spelling, not the engine's); the router " +
                        "does not retry or fall back to another engine");
                    // The engine was relaunched, whatever it turned out to be serving: the list from before
                    // the switch describes a process that has been stopped.
                    PublishUnknown();
                    EndSwitch(state, false);
                    return;
                }
                PublishProof(freshModels);
            }
            else
            {
                // The other half of the same rule, and the half that used to be policy: an engine that will
                // not answer after the launch has not been verified, and `wait_health = false` skips the
                // readiness *rule*, never the requirement that something confirm the model. Serving here was
                // a promise made in configuration rather than an observation the router had made; the owner's
                // answer is refuse (decision-27). The reason names what could not be confirmed, because the
                // fix is the select command or the engine, not a retry.
                state.FailureReason = SwitchReason.RefreshUnanswered;
                Emit("engine.switch_failed", model: model, reason:
                    $"refresh_unanswered_{WireText.Ident(model)}: engine {WireText.Ident(_def.Name)} did not answer /models after the launch, " +
                    $"so nothing confirms it serves {WireText.Ident(model)}: with wait_health = false the exec's exit status is " +
                    "all the router is given, and an exit code is not a loaded model — check that the select " +
                    "command leaves the engine answering /models; the router does not retry or fall back to " +
                    "another engine");
                // Relaunched, whatever it turned out to be serving: the list from before the switch describes
                // a process that has been stopped, so it is not presented as current.
                PublishUnknown();
                EndSwitch(state, false);
                return;
            }
            // wait_health=false: trust the exec, but still verify the window when a profile was requested
            // (the gauge may already reflect the new window, e.g. an engine that reloads in place).
            if (!WindowFits(profileOverride))
            {
                state.FailureReason = SwitchReason.WindowTooSmall;
                Emit("engine.switch_failed", model: model,
                    reason: $"window_{ActiveContextWindow}_lt_{profileOverride!.MaxContext} (the caller is told window_too_small)");
                // The engine has been relaunched by now, whatever the window says about it.
                PublishUnknown();
                EndSwitch(state, false);
                return;
            }
            Emit("engine.switch_completed", model: model,
                reason: state.Profile is null ? null : $"profile={WireText.Ident(state.Profile)}",
                value: (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds);
            EndSwitch(state, true);
        }
        catch (Exception ex)
        {
            // Provider and IO text reaches the feed here, bounded; the caller is told only the category, so a
            // message that names a socket path or an address never travels to whoever made the request.
            state.FailureReason = SwitchReason.SwitchError;
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
            EndSwitch(state, false);
        }
    }


    public string? CanonicalModel(string requested)
    {
        var def = _config.Engines.TryGetValue(_def.Name, out var d) ? d : _def;
        foreach (var (name, md) in def.Catalog)
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
        Interlocked.Increment(ref _inflight);
        Interlocked.Add(ref _tokenProxy, promptChars);
    }

    public void EndRequest(long promptChars)
    {
        Interlocked.Decrement(ref _inflight);
        Interlocked.Add(ref _tokenProxy, -promptChars);
    }

    public bool HasCapacity => Inflight < EffectiveSlotLimit;

    /// <summary>Admission ceiling: the tighter of the configured max_inflight_per_engine and the
    /// slots the engine currently advertises (profile switches change the advertised count).
    /// Unknown advertised slots → configured ceiling alone.</summary>
    private int EffectiveSlotLimit => ActiveSlots > 0 ? Math.Max(1, Math.Min(_maxInflight, ActiveSlots)) : _maxInflight;

    public HttpClient Http => _http;
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
        // Settle the waiters of an in-flight switch *before* cancelling anything. A waiter parked on the
        // readiness task can be released two ways — the switch settling, or the switch token cancelling its
        // wait — and while both now answer the same thing, letting a shutdown choose between them is what made
        // the outcome depend on thread scheduling. Settling the readiness task first fixes the order: the wait
        // is already resolved when the cancellation fan-out runs. Only the waiters are settled here; the switch
        // marker and the switch state stay exactly as they are, because a lifecycle transaction is still
        // running and the scheduler must keep seeing this engine as mid-switch until that transaction ends.
        var stopping = Volatile.Read(ref _switchState);
        if (stopping.Model is not null)
        {
            stopping.CancelledByShutdown = true;
            stopping.Ready.TrySetResult(false);
        }
        // Stop the switch before its engine goes away: cancelling the switch token is the only way an
        // in-flight model load learns the router is shutting down.
        _switchCts.Cancel();
        // Deliberately not disposed: an in-flight switch may still hold this token, and disposing the
        // source under it turns a clean shutdown into ObjectDisposedException inside the switch task.
        // Cancel is the whole contract — the switch stops; the source is collected with the runtime.
        _http.Dispose();
        _ownedHandler?.Dispose();
        // The switch lock is deliberately not disposed: a waiter parked on it during shutdown would answer with
        // ObjectDisposedException, which the request path can only report as engine_unreachable — a shutting-down
        // router described as a broken engine. Waiters are released by the token linked to _switchCts instead, and
        // that source *is* disposed with the call, so nothing here leaks a live linked token.
        _lifecycle.Dispose();
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
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur))
                return false;
        }
        if (cur.ValueKind == JsonValueKind.Null) return false;
        value = cur;
        return true;
    }
}
