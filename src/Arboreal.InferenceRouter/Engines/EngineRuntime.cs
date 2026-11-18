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
            var unknown = cur with { Version = cur.Version + 1, Healthy = false };
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
            if (!cur.Healthy) return;
            var next = cur with { Version = cur.Version + 1, Healthy = false };
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
        var next = cur with { Version = cur.Version + 1, SwitchingToward = model, Healthy = false };
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
            var next = cur with { Version = cur.Version + 1, SwitchingToward = toward };
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
                Emit("engine.health", reason: ex.Message, value: 0);
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
            Emit("engine.profile", reason: name ?? "unrecognized_window", value: window);
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

    private async Task<SwitchResult> EnsureLoadedAsync(string model, ServingProfile? profileOverride, TimeSpan timeout, CancellationToken ct)
    {
        var canonical = CanonicalModel(model) ?? model;
        if (IsReadyFor(canonical, profileOverride)) return new SwitchResult(SwitchOutcome.Ready, null);
        if (_def.Remote) return new SwitchResult(SwitchOutcome.Failed, "remote engine loads its own models");

        // `timeout` bounds the *waiting*, never the switch: the transaction belongs to the engine and
        // runs on _switchCts, so a caller that gives up leaves the engine switching rather than
        // half-switched. The loop is what makes single-flight fair: if the switch in progress is aimed
        // at another request's model, this caller waits for it and then takes its own turn rather than
        // being failed by someone else's switch.
        var deadline = timeout == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + timeout;
        var last = new SwitchResult(SwitchOutcome.Failed, null);
        // Shutdown has to reach the two waits below as cancellation, not as an ObjectDisposedException: the switch
        // lock is deliberately never disposed (see DisposeAsync), so a waiter is released by a token linked to the
        // engine's switch token instead. This source is disposed with the call; the engine's own is not.
        using var waiterCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _switchCts.Token);
        var waiter = waiterCts.Token;
        while (true)
        {
            SwitchState state;
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
                        state = mine!;
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
                            () => RunSwitchAsync(claim, canonical, profileOverride, switchToken),
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
                        // engine back rather than leaving it "switching" forever.
                        EndSwitch(claim, false);
                        return new SwitchResult(SwitchOutcome.Failed,
                            $"could not start the switch to {DescribeTarget(canonical, profileOverride?.Name)}");
                    }
                    state = claim;
                }
            }
            finally
            {
                _switchLock.Release();
            }

            var remaining = deadline == DateTimeOffset.MaxValue
                ? Timeout.InfiniteTimeSpan
                : deadline - DateTimeOffset.UtcNow;
            // InfiniteTimeSpan is -1 ms, so the sentinel must never reach the comparison below: a
            // request with no ceiling would otherwise be "timed out" before it ever waited.
            if (deadline != DateTimeOffset.MaxValue && remaining <= TimeSpan.Zero)
            {
                return IsReadyFor(canonical, profileOverride)
                    ? new SwitchResult(SwitchOutcome.Ready, null)
                    : new SwitchResult(SwitchOutcome.TimedOut, last.Detail);
            }

            bool ready;
            try
            {
#pragma warning disable IDISP013 // await-in-using not applicable: the wait is awaited, not detached
                ready = await state.Ready.Task.WaitAsync(remaining, waiter).ConfigureAwait(false);
#pragma warning restore IDISP013
            }
            catch (TimeoutException)
            {
                // The switch may have finished and loaded the model in the instant before the wait gave
                // up, so re-check rather than trusting the timeout alone.
                return IsReadyFor(canonical, profileOverride)
                    ? new SwitchResult(SwitchOutcome.Ready, null)
                    : new SwitchResult(SwitchOutcome.TimedOut,
                        $"still switching after {FormatWait(timeout)}; a switch to {DescribeTarget(state.Model, state.Profile)} is in flight");
            }

            if (IsReadyFor(canonical, profileOverride)) return new SwitchResult(SwitchOutcome.Ready, null);
            // Ready is never reported for a switch that was not this request's. A switch aimed at the same
            // model under a *different* serving profile proves nothing about the window this caller needs,
            // and a switch that failed for someone else's target must not fail this one: in both cases the
            // waiter loops and takes its own turn.
            if (state.Model == canonical && state.Profile == profileOverride?.Name)
            {
                // Our own attempt ended. Trust its own readiness proof (RunSwitchAsync proves health,
                // loaded model and window before it says success) instead of looping into another switch.
                return ready
                    ? new SwitchResult(SwitchOutcome.Ready, null)
                    : new SwitchResult(SwitchOutcome.Failed,
                        $"switch to {DescribeTarget(canonical, profileOverride?.Name)} failed");
            }
            last = new SwitchResult(SwitchOutcome.Failed,
                $"a switch to {DescribeTarget(state.Model, state.Profile)} ended; waiting for this request's own turn");
        }
    }

    private static string DescribeTarget(string? model, string? profile) =>
        profile is { Length: > 0 } p ? $"{model} @ {p}" : model ?? "(none)";

    private static string DescribeTarget(string model, ServingProfile? profile) =>
        profile is null ? model : $"{model} @ {profile.Name}";

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
        state.Ready.TrySetResult(ready);
        if (committed)
        {
            try { _dispatchHint?.Invoke(); }
            catch (Exception) { /* a lost nudge is a late dispatch, not a lost switch */ }
            AfterEndCommitForTests?.Invoke(Monitor.IsEntered(_transitionGate));
        }
    }

    private async Task RunSwitchAsync(SwitchState state, string model, ServingProfile? profileOverride, CancellationToken ct)
    {
        try
        {
            var def = _config.Engines[_def.Name];
            if (!def.Catalog.TryGetValue(model, out var md))
            {
                Emit("engine.switch_failed", model: model, reason: "no_select_command");
                // Nothing was mutated on this path — the catalog was consulted and the switch refused — so
                // the last probe is still a true account of the engine and health is left alone (§5.5).
                EndSwitch(state, false);
                // Reset, like every other exit: a request for a model this engine cannot select must not
                // leave the engine permanently "switching", because every later waiter would join a
                // readiness task that is already resolved false and never start a switch of its own.
                return;
            }
            var profileName = profileOverride?.Name ?? md.Profile;
            Emit("engine.switch_started", model: model, reason: profileName is null ? null : $"profile={profileName}");

            // One transaction through the provider seam: prove ownership, validate, stop what is ours,
            // launch the exact target. What "launch" means is the provider's business; the ordering is
            // the router's, and it is what keeps a refusal from costing the engine anything it had.
            var target = new LifecycleTarget(_def.Name, model, profileOverride, md);
            var launched = await _lifecycle.SwitchAsync(target, ct).ConfigureAwait(false);
            if (!launched.Succeeded)
            {
                // The short reason goes on the feed for grepping; the provider's one actionable sentence
                // rides along so the operator reading /admin/events does not have to go find a log.
                var detail = launched.Reason ?? "lifecycle_failed";
                if (launched.Action is { Length: > 0 } action)
                {
                    if (action.Length > 200) action = action[..200];
                    detail = $"{detail} — {action}";
                }
                Emit("engine.switch_failed", model: model, reason: detail);
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
                            reason: state.Profile is null ? null : $"profile={state.Profile}",
                            value: (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds);
                        EndSwitch(state, true);
                        return;
                    }
                    try { await Task.Delay(2_000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                // A readiness timeout is the one failure that has no exit code and no stderr, so the
                // message has to carry the actionable part itself: what to look at, and the fact that
                // the router will not quietly retry or route elsewhere.
                Emit("engine.switch_failed", model: model, reason:
                    $"health_timeout after {readiness.Timeout.TotalMilliseconds:F0}ms — engine {_def.Name} " +
                    $"never reported {model} serving: inspect what the {_lifecycle.Kind} provider started " +
                    "(for docker: containers labelled arboreal.engine=<name>) or raise the wait timeout; " +
                    "the router does not retry or fall back to another engine");
                PublishUnknown();
                EndSwitch(state, false);
                return;
            }
            // wait_health=false: the provider's exec says it launched, so the router asks what is loaded.
            // A successful refresh is evidence of the list and of health, so it is published as a proof; a
            // failed refresh leaves nothing to claim, and the engine becomes unknown rather than continuing
            // to present the old list as current (§5.5).
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
                    var reported = string.Join(", ", freshModels.Take(8));
                    if (freshModels.Length > 8) reported += $" … (+{freshModels.Length - 8} more)";
                    Emit("engine.switch_failed", model: model, reason:
                        $"refresh_omits_{model}: engine {_def.Name} answered /models after the launch with " +
                        $"[{reported}] and none of those names is {model}: the catalog name has to match what " +
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
            else PublishUnknown();
            // wait_health=false: trust the exec, but still verify the window when a profile was requested
            // (the gauge may already reflect the new window, e.g. an engine that reloads in place).
            if (!WindowFits(profileOverride))
            {
                Emit("engine.switch_failed", model: model,
                    reason: $"window_{ActiveContextWindow}_lt_{profileOverride!.MaxContext}");
                // The engine has been relaunched by now, whatever the window says about it.
                PublishUnknown();
                EndSwitch(state, false);
                return;
            }
            Emit("engine.switch_completed", model: model,
                reason: state.Profile is null ? null : $"profile={state.Profile}",
                value: (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds);
            EndSwitch(state, true);
        }
        catch (Exception ex)
        {
            Emit("engine.switch_failed", model: model, reason: ex.Message);
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
        => _events.Append(new RouterEvent { Kind = kind, Engine = Name, Model = model, Reason = reason, Value = value });

    public async ValueTask DisposeAsync()
    {
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
