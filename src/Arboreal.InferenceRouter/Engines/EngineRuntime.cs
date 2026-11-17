using System.Globalization;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

/// <summary>Runtime for one configured engine: health probing, loaded-model detection,
/// prefix trie, switch execution (serialized per engine), busy-ness accounting.</summary>
public sealed class EngineRuntime : IAsyncDisposable
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

    private volatile bool _healthy;
    private volatile string[] _loadedModels = [];
    private SwitchState _switchState = new();   // accessed via Volatile.Read/Write

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
                         IEngineLifecycleProvider? lifecycle = null)
    {
        _def = def;
        _config = config;
        _events = events;
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
    }

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
    public bool Healthy => _healthy;
    public string[] LoadedModels => _loadedModels;
    public int Inflight => Volatile.Read(ref _inflight);
    private int _inflight;
    public long InflightTokenProxy => Interlocked.Read(ref _tokenProxy);
    private long _tokenProxy;
    public bool Switching => Volatile.Read(ref _switchState).Model is not null;
    public string? SwitchingToward => Volatile.Read(ref _switchState).Model;

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
            try
            {
                // CA2000's "recommended dispose pattern" doesn't track `using var` in an async
                // loop body; the CTS is disposed at each iteration and the await completes inside.
#pragma warning disable CA2000
                using var cts = new CancellationTokenSource(_def.Health?.TimeoutMs ?? 5_000);
#pragma warning restore CA2000
                var models = await FetchLoadedModelsAsync(cts.Token).ConfigureAwait(false);
                _loadedModels = models;
                _healthy = true;
                Emit("engine.health", value: models.Length);
                // Same payload family feeds the serving gauges (context window, slots).
                await ProbeStateAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _healthy = false;
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

    /// <summary>Ensure the model is loaded on this engine: no-op when loaded; otherwise performs
    /// (or waits for) the configured switch. Resolves true when loaded, false when it cannot.</summary>
    public Task<bool> WaitForModelAsync(string model, TimeSpan timeout, CancellationToken ct)
        => EnsureLoadedAsync(model, null, timeout, ct);

    /// <summary>Ensure the model is loaded AND served under a context window that fits the profile.
    /// Exceeding the active window queues behind a profile switch instead of compacting.</summary>
    public Task<bool> WaitForProfileAsync(string model, ServingProfile profile, TimeSpan timeout, CancellationToken ct)
        => EnsureLoadedAsync(model, profile, timeout, ct);

    /// <summary>Loaded, and (when a profile is required) serving a window that fits it.
    /// An engine that reports no window (gauge 0) is never escalated blind.</summary>
    public bool IsReadyFor(string canonical, ServingProfile? profile)
    {
        if (!_loadedModels.Contains(canonical, StringComparer.OrdinalIgnoreCase)) return false;
        return WindowFits(profile);
    }

    /// <summary>Live window fits the profile (gauge 0 = unknown → never block on it).</summary>
    private bool WindowFits(ServingProfile? profile)
    {
        if (profile is null) return true;
        var window = ActiveContextWindow;
        return window == 0 || window >= profile.MaxContext;
    }

    private async Task<bool> EnsureLoadedAsync(string model, ServingProfile? profileOverride, TimeSpan timeout, CancellationToken ct)
    {
        var canonical = CanonicalModel(model) ?? model;
        if (IsReadyFor(canonical, profileOverride)) return true;
        if (_def.Remote) return false; // remote peers switch themselves; we only route

        await _switchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = Volatile.Read(ref _switchState);
            if (state.Model is null)
            {
                var newState = new SwitchState { Model = canonical, Profile = profileOverride?.Name, StartedAt = DateTimeOffset.UtcNow };
                newState.Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _switchState, newState);
                _ = Task.Run(() => RunSwitchAsync(newState, canonical, profileOverride, ct), ct);
            }
        }
        finally
        {
            _switchLock.Release();
        }

        // Wait on the current switch's readiness task (shared across waiters).
        var st = Volatile.Read(ref _switchState);
        try
        {
            await st.Ready.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return IsReadyFor(canonical, profileOverride); }
        return IsReadyFor(canonical, profileOverride);
    }

    private async Task RunSwitchAsync(SwitchState state, string model, ServingProfile? profileOverride, CancellationToken ct)
    {
        try
        {
            var def = _config.Engines[_def.Name];
            if (!def.Catalog.TryGetValue(model, out var md))
            {
                state.Ready.TrySetResult(false);
                Emit("engine.switch_failed", model: model, reason: "no_select_command");
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
                state.Ready.TrySetResult(false);
                // The short reason goes on the feed for grepping; the provider's one actionable sentence
                // rides along so the operator reading /admin/events does not have to go find a log.
                var detail = launched.Reason ?? "lifecycle_failed";
                if (launched.Action is { Length: > 0 } action)
                {
                    if (action.Length > 200) action = action[..200];
                    detail = $"{detail} — {action}";
                }
                Emit("engine.switch_failed", model: model, reason: detail);
                Volatile.Write(ref _switchState, new SwitchState());
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
                        state.Ready.TrySetResult(true);
                        _loadedModels = modelsNow;
                        Emit("engine.switch_completed", model: model,
                            reason: state.Profile is null ? null : $"profile={state.Profile}",
                            value: (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds);
                        Volatile.Write(ref _switchState, new SwitchState());
                        return;
                    }
                    try { await Task.Delay(2_000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                state.Ready.TrySetResult(false);
                Emit("engine.switch_failed", model: model, reason: "health_timeout");
                Volatile.Write(ref _switchState, new SwitchState());
                return;
            }
            try { _loadedModels = await FetchLoadedModelsAsync(ct).ConfigureAwait(false); }
            catch { /* best-effort refresh; the switch itself succeeded */ }
            // wait_health=false: trust the exec, but still verify the window when a profile was requested
            // (the gauge may already reflect the new window, e.g. an engine that reloads in place).
            if (!WindowFits(profileOverride))
            {
                state.Ready.TrySetResult(false);
                Emit("engine.switch_failed", model: model,
                    reason: $"window_{ActiveContextWindow}_lt_{profileOverride!.MaxContext}");
                Volatile.Write(ref _switchState, new SwitchState());
                return;
            }
            state.Ready.TrySetResult(true);
            Emit("engine.switch_completed", model: model,
                reason: state.Profile is null ? null : $"profile={state.Profile}",
                value: (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds);
            Volatile.Write(ref _switchState, new SwitchState());
        }
        catch (Exception ex)
        {
            state.Ready.TrySetResult(false);
            Emit("engine.switch_failed", model: model, reason: ex.Message);
            Volatile.Write(ref _switchState, new SwitchState());
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
        _http.Dispose();
        _ownedHandler?.Dispose();
        _switchLock.Dispose();
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
