using System.Text.Json;
using System.Diagnostics;

namespace Arboreal.InferenceRouter;

/// <summary>Runtime for one configured engine: health probing, loaded-model detection,
/// prefix trie, switch execution (serialized per engine), busy-ness accounting.</summary>
public sealed class EngineRuntime : IAsyncDisposable
{
    private readonly EngineDefinition _def;
    private readonly RouterConfig _config;
    private readonly EventRing _events;
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly PrefixTrie _trie;
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly int _maxInflight;

    private volatile bool _healthy;
    private volatile string[] _loadedModels = [];
    private SwitchState _switchState = new();   // accessed via Volatile.Read/Write

    private sealed class SwitchState
    {
        public string? Model;
        public TaskCompletionSource<bool> Ready = new(TaskCreationOptions.None);
        public DateTimeOffset StartedAt;
        public override string ToString() => Model is null ? "none" : Model;
    }

    public EngineRuntime(EngineDefinition def, RouterConfig config, EventRing events, HttpMessageHandler? handler = null)
    {
        _def = def;
        _config = config;
        _events = events;
        _maxInflight = Math.Max(1, config.Scheduler.MaxInflightPerEngine);
        _trie = new PrefixTrie(config.Scheduler.PrefixTrieMaxNodes);
        _apiKey = ResolveApiKey(def);
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.None })
            : new HttpClient(handler);
        _http.BaseAddress = def.Endpoint;
        _http.Timeout = TimeSpan.FromMilliseconds(Timeout.Infinite); // long streams; per-request CTS applies
    }

    public string Name => _def.Name;
    public EngineDefinition Definition => _def;
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
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                using var cts = new CancellationTokenSource(_def.Health?.TimeoutMs ?? 5_000);
                var models = await FetchLoadedModelsAsync(cts.Token).ConfigureAwait(false);
                _loadedModels = models;
                _healthy = true;
                Emit("engine.health", value: models.Length);
            }
            catch (Exception ex)
            {
                _healthy = false;
                Emit("engine.health", reason: ex.Message, value: 0);
            }
        }
    }

    private HttpRequestMessage AuthedGet(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (_apiKey is not null) req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        return req;
    }

    /// <summary>Loaded model ids from the configured models endpoint (llama.cpp /v1/models shape).
    /// Optionally requires status==loaded and no alias_of.</summary>
    internal async Task<string[]> FetchLoadedModelsAsync(CancellationToken ct)
    {
        using var res = await _http.SendAsync(AuthedGet(_def.ModelsEndpoint), ct).ConfigureAwait(false);
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
    internal async Task<bool> ProbeAsync(CancellationToken ct)
    {
        var path = _def.Health?.Path ?? "/props";
        try
        {
            using var res = await _http.SendAsync(AuthedGet(path), ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return false;
            var probeBytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(probeBytes);
            return _def.Health?.Expect is { } expect ? ProbeExpect.Evaluate(doc.RootElement, expect) : true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Ensure the model is loaded on this engine: no-op when loaded; otherwise performs
    /// (or waits for) the configured switch. Resolves true when loaded, false when it cannot.</summary>
    public async Task<bool> WaitForModelAsync(string model, TimeSpan timeout, CancellationToken ct)
    {
        if (_loadedModels.Contains(model, StringComparer.OrdinalIgnoreCase)) return true;
        if (_def.Remote) return false; // remote peers switch themselves; we only route

        var canonical = CanonicalModel(model) ?? model;
        if (_loadedModels.Contains(canonical, StringComparer.OrdinalIgnoreCase)) return true;

        await _switchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = Volatile.Read(ref _switchState);
            if (state.Model is null)
            {
                var newState = new SwitchState { Model = canonical, StartedAt = DateTimeOffset.UtcNow };
                newState.Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _switchState, newState);
                _ = Task.Run(() => RunSwitchAsync(newState, canonical, ct), ct);
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
        catch (TimeoutException) { return _loadedModels.Contains(canonical, StringComparer.OrdinalIgnoreCase); }
        return _loadedModels.Contains(canonical, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RunSwitchAsync(SwitchState state, string model, CancellationToken ct)
    {
        try
        {
            var def = _config.Engines[_def.Name];
            if (!def.Catalog.TryGetValue(model, out var md) || md.Select is null)
            {
                state.Ready.TrySetResult(false);
                Emit("engine.switch_failed", model: model, reason: "no_select_command");
                return;
            }
            Emit("engine.switch_started", model: model);
            var cmd = md.Select;
            var args = ExpandArgs(cmd.Args, model, md.Profile);
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = cmd.Exec,
                    Arguments = args.Length == 1 ? args[0] : string.Join(' ', args),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            using var exitCts = new CancellationTokenSource(cmd.WaitTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, exitCts.Token);
            if (!proc.Start()) throw new IOException($"failed to start {cmd.Exec}");
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                var stderr = await proc.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
                state.Ready.TrySetResult(false);
                var tail = stderr.Trim();
                Emit("engine.switch_failed", model: model, reason: $"exit={proc.ExitCode} {tail}"[..Math.Min(168, tail.Length + 10)]);
                return;
            }
            if (cmd.WaitHealth)
            {
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(cmd.WaitTimeoutMs);
                while (DateTimeOffset.UtcNow < deadline && !linked.IsCancellationRequested)
                {
                    if ((await ProbeAsync(ct).ConfigureAwait(false)) &&
                        (await FetchLoadedModelsAsync(ct).ConfigureAwait(false)).Contains(model, StringComparer.OrdinalIgnoreCase))
                    {
                        state.Ready.TrySetResult(true);
                        _loadedModels = await FetchLoadedModelsAsync(ct).ConfigureAwait(false);
                        Emit("engine.switch_completed", model: model, value: (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds);
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
            state.Ready.TrySetResult(true);
            Emit("engine.switch_completed", model: model, value: (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds);
            Volatile.Write(ref _switchState, new SwitchState());
        }
        catch (Exception ex)
        {
            state.Ready.TrySetResult(false);
            Emit("engine.switch_failed", model: model, reason: ex.Message);
            Volatile.Write(ref _switchState, new SwitchState());
        }
    }

    private string[] ExpandArgs(string[] args, string model, string? profile)
    {
        var expanded = new string[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            expanded[i] = args[i]
                .Replace("{{model}}", model)
                .Replace("{{profile}}", profile ?? "")
                .Replace("{{suffixCache}}", "");
        }
        return expanded;
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

    public bool HasCapacity => Inflight < _maxInflight;

    public HttpClient Http => _http;
    public string? ApiKey => _apiKey;

    internal void Emit(string kind, string? model = null, string? reason = null, double? value = null)
        => _events.Append(new RouterEvent { Kind = kind, Engine = Name, Model = model, Reason = reason, Value = value });

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await Task.CompletedTask;
        GC.SuppressFinalize(this);
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
        if (!double.TryParse(rhs, out var r)) return false;
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

    private static bool TryResolvePath(JsonElement root, string dottedPath, out JsonElement value)
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
