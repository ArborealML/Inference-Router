using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// MA0001 ("use the StringComparison overload") and CA1865 ("use the char overload") collide on
// single-char prefix tests. The char overload is ordinal by definition and allocation-free, so
// it wins; the collision is confined to this argument-parsing file.
#pragma warning disable MA0001

using Arboreal.InferenceRouter.Server;
namespace Arboreal.InferenceRouter.Cli;

/// <summary>CLI entry point: runs the router host locally, or talks to a running router.
/// Commands: serve | chat | complete | models | status | events | threads | switch | tickets | help.
/// Parsing is hand-rolled (no reflection-dependent command frameworks; AOT-safe).</summary>
public static class Program
{
    private static string DefaultServer =>
        Environment.GetEnvironmentVariable("AIR_ROUTER_URL") ?? "http://127.0.0.1:8321";

    /// <summary>Absolutises a router path against the configured server. HttpClient rejects relative
    /// request URIs, so every call site goes through here (regression: the POST commands used to
    /// hand <c>HttpClient</c> a bare path and fail before reaching the router).</summary>
    private static string Url(string server, string path) => server.TrimEnd('/') + path;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) return await RunHelpAsync().ConfigureAwait(false);
        return args[0] switch
        {
            "serve" => await RunServeAsync(args[1..]).ConfigureAwait(false),
            "chat" or "complete" => await RunChatAsync(args[1..]).ConfigureAwait(false),
            "models" => await RunModelsAsync(args[1..]).ConfigureAwait(false),
            "status" => await RunStatusAsync(args[1..]).ConfigureAwait(false),
            "events" => await RunEventsAsync(args[1..]).ConfigureAwait(false),
            "threads" => await RunThreadAsync(args[1..]).ConfigureAwait(false),
            "switch" => await RunSwitchAsync(args[1..]).ConfigureAwait(false),
            "config" => await RunConfigCommandAsync(args[1..]).ConfigureAwait(false),
            "tickets" => await RunTicketAsync(args[1..]).ConfigureAwait(false),
            "tune" => AirTuneCompare.Run(args.AsSpan(1), Console.Out, Console.Error),
            "help" or "-h" or "--help" => await RunHelpAsync().ConfigureAwait(false),
            _ => await RunHelpAsync().ConfigureAwait(false),
        };
    }

    private static Task<int> RunHelpAsync()
    {
        Console.WriteLine("""
            arboreal inference router

            usage: air <command> [options]

            commands:
              serve [--config path] [--listen url]      run the router host in this process
              chat  [-m model] [-t thread] [-p prio]    send a chat completion (prompt args or stdin)
                    [--system text] [--no-stream] [--json] [-s server-url]
              models [-s server-url]                    list models (OpenAI format)
              status [-s server-url]                    router stats (engines, queues, threads)
              events [-s server-url] [--since seq]      tail the live event stream (SSE)
              threads -i threadId [-p prio]             register a chat thread (admin)
              switch -e engine -m model [-s url]        start a model switch on an engine
              tickets <id> [-s server-url]              inspect one ticket
              config [--config path]                    validate a config file and print the plan
                                                      (no sockets, no lifecycle commands)
              tune compare --baseline FILE --candidate FILE
                                                      compare saved tuning summaries (Linux x64)
            """);
        return Task.FromResult(0);
    }

    // ── serve ───────────────────────────────────────────────────────────────
    private static async Task<int> RunServeAsync(string[] args)
    {
        string? config = GetOpt(args, ["--config", "-c"]);
        string? listen = GetOpt(args, ["--listen", "-l"]);
        var runArgs = listen is { Length: > 0 } ? new[] { $"--urls={listen}" } : [];
        return await ServerApp.RunAsync(runArgs, config).ConfigureAwait(false);
    }

    // ── config (dry run) ────────────────────────────────────────────────────

    /// <summary>Parse + validate a config file and print the resolved engine / model /
    /// serving-profile plan. Deliberately touches nothing: no sockets, no lifecycle commands.</summary>
    private static Task<int> RunConfigCommandAsync(string[] args)
    {
        var path = GetOpt(args, ["--config", "-c"])
            ?? Environment.GetEnvironmentVariable("ARBOREAL_ROUTER_CONFIG")
            ?? "router.toml";
        try
        {
            var cfg = RouterConfigLoader.LoadFile(path);
            var s = cfg.Scheduler;
            Console.WriteLine($"config ok: {path}");
            Console.WriteLine($"  listen={cfg.Listen}  default_model={cfg.DefaultModel}  " +
                $"auto_switch={cfg.AutoSwitch}  switch_cooldown_ms={cfg.SwitchCooldownMs}");
            Console.WriteLine($"  scheduler: max_inflight_per_engine={s.MaxInflightPerEngine}  " +
                $"max_queue_per_model={s.MaxQueuePerModel}  " +
                $"chars_per_token_estimate={s.CharsPerTokenEstimate.ToString(CultureInfo.InvariantCulture)}  " +
                $"prefix_affinity_threshold_chars={s.PrefixAffinityThresholdChars}");
            foreach (var (name, e) in cfg.Engines)
            {
                var key = e.ApiKey is { Length: > 0 } ? "inline" : e.ApiKeyFile is { Length: > 0 } ? $"file:{e.ApiKeyFile}" : "none";
                var (owner, provider) = e.LifecycleOwner switch
                {
                    EngineLifecycleOwner.Router => ("router", "docker"),
                    EngineLifecycleOwner.Delegated => ("delegated", "exec"),
                    _ => ("external", "none"),
                };
                Console.WriteLine($"engine {name}: kind={e.Kind} endpoint={e.Endpoint} remote={e.Remote} " +
                    $"lifecycle_owner={owner} provider={provider} " +
                    $"health={(e.Health?.Path ?? "/props (default)")} ctx={(e.Health?.ContextPath ?? "n_ctx (default)")} " +
                    $"slots={(e.Health?.SlotsPath ?? "total_slots (default)")} key={key}");
                foreach (var (model, md) in e.Catalog)
                {
                    var ladder = md.ServingProfiles.Count == 0
                        ? "-"
                        : string.Join(", ", md.ServingProfiles.Select(p =>
                            $"{p.Name}({p.MaxContext}tok/{p.MaxSlots}slot)"));
                    var select = md.Select is { } selector ? $"{selector.Exec} {string.Join(' ', selector.Args)}" : "none";
                    Console.WriteLine($"  model {model}: aliases=[{string.Join(", ", md.Aliases)}] " +
                        $"default_profile={(md.Profile ?? "-")} serving=[{ladder}] select={select}");
                }
            }
            foreach (var (id, t) in cfg.Threads)
                Console.WriteLine($"thread {id}: priority={t.Priority.Class}/{t.Priority.Sub} " +
                    $"weight={t.Weight.ToString(CultureInfo.InvariantCulture)} affinity={(t.ModelAffinity ?? "-")}");
            return Task.FromResult(0);
        }
        catch (RouterConfigException ex)
        {
            Console.Error.WriteLine($"config invalid: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    // ── chat ────────────────────────────────────────────────────────────────

    private static async Task<int> RunChatAsync(string[] args)
    {
        var model = GetOpt(args, ["-m", "--model"]);
        var thread = GetOpt(args, ["-t", "--thread"]) ?? "anon";
        var priority = GetOpt(args, ["-p", "--priority"]);
        var system = GetOpt(args, ["--system"]);
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        var since = GetOpt(args, ["--since"]);
        bool stream = !HasFlag(args, "--no-stream");
        bool json = HasFlag(args, "--json");
        var promptArgs = args.Where((arg, i) => !arg.StartsWith('-') &&
            (i == 0 || !IsChatValueOption(args[i - 1]))).ToArray();
        string prompt = promptArgs.Length > 0
            ? string.Join(' ', promptArgs)
            : Console.In.ReadToEnd().Trim();

        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            if (model is { Length: > 0 }) w.WriteString("model", model);
            w.WriteBoolean("stream", stream);
            w.WriteStartArray("messages");
            if (system is { Length: > 0 })
            {
                w.WriteStartObject();
                w.WriteString("role", "system");
                w.WriteString("content", system);
                w.WriteEndObject();
            }
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", prompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, Url(server, "/v1/chat/completions"));
        req.Content = new ByteArrayContent(body.ToArray());
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.TryAddWithoutValidation("X-Inference-Thread", thread);
        if (priority is { Length: > 0 }) req.Headers.TryAddWithoutValidation("X-Inference-Priority", priority);

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.Error.WriteLine($"error {(int)res.StatusCode}: {err}");
            return 1;
        }

        if (!stream)
        {
            var bytes = await res.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (json) { Console.Write(Encoding.UTF8.GetString(bytes)); }
            else Console.WriteLine(ExtractMessage(bytes));
            return 0;
        }

        // The router's reserved comments report status to humans. JSON mode writes data payloads.
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync().ConfigureAwait(false));
        string? lastStatus = null;
        var exitCode = 0;
        var sawDone = false;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith(':'))
            {
                if (!json && ReadKeepaliveStatus(line) is { } status &&
                    !string.Equals(status, lastStatus, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(line[2..]);
                    lastStatus = status;
                }
                continue;
            }
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line["data: ".Length..];
            if (payload == "[DONE]")
            {
                sawDone = true;
                break;
            }
            if (json) Console.Write(payload);
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    if (!json) Console.Error.WriteLine(payload);
                    exitCode = 1;
                    continue;
                }
                if (json || !root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array) continue;

                var choice = choices.EnumerateArray().FirstOrDefault();
                if (choice.ValueKind == JsonValueKind.Object &&
                    choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    Console.Write(content.GetString());
            }
            catch (JsonException) { } // Human mode only renders completion chunks and errors.
        }
        if (!json) Console.WriteLine();
        if (!sawDone && exitCode == 0)
        {
            Console.Error.WriteLine("error: completion stream ended before [DONE]");
            return 1;
        }
        return exitCode;
    }

    private static bool IsChatValueOption(string arg) => arg is
        "-m" or "--model" or "-t" or "--thread" or "-p" or "--priority" or
        "--system" or "-s" or "--server" or "--since";

    private static string? ReadKeepaliveStatus(string line)
    {
        const string prefix = ": [router keepalive seq=";
        const string statusField = " status=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || !line.EndsWith(']')) return null;
        var fields = line.AsSpan(prefix.Length, line.Length - prefix.Length - 1);
        var separator = fields.IndexOf(' ');
        if (separator <= 0 || fields[..separator].IndexOfAnyExceptInRange('0', '9') >= 0 ||
            !fields[separator..].StartsWith(statusField, StringComparison.Ordinal)) return null;

        var encodedStatus = fields[(separator + statusField.Length)..];
        if (encodedStatus.Length < 2 || encodedStatus[0] != '"' || encodedStatus[^1] != '"') return null;
        // Retain JSON escaping in the diagnostic so a model name stays on one stderr line.
        var status = encodedStatus.ToString();
        try
        {
            using var doc = JsonDocument.Parse(status);
            return doc.RootElement.ValueKind == JsonValueKind.String ? status : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? ExtractMessage(byte[] response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.EnumerateArray().FirstOrDefault() is { } choice &&
                choice.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
                return content.GetString();
            return null;
        }
        catch (JsonException) { return null; }
    }

    // ── admin commands ──────────────────────────────────────────────────────

    private static async Task<int> RunModelsAsync(string[] args)
    {
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        return await GetPrintAsync(server, "/v1/models").ConfigureAwait(false);
    }

    private static async Task<int> RunStatusAsync(string[] args)
    {
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        return await GetPrintAsync(server, "/admin/status").ConfigureAwait(false);
    }

    private static async Task<int> RunTicketAsync(string[] args)
    {
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        var id = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "";
        if (id.Length == 0)
        {
            Console.Error.WriteLine("tickets <id>");
            return 2;
        }
        return await GetPrintAsync(server, $"/admin/tickets/{Uri.EscapeDataString(id)}").ConfigureAwait(false);
    }

    private static async Task<int> RunEventsAsync(string[] args)
    {
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        var since = GetOpt(args, ["--since"]) ?? "0";
        using var http = new HttpClient();
        using var res = await http.GetAsync($"{server.TrimEnd('/')}/admin/events?since={since}",
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync().ConfigureAwait(false));
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                Console.WriteLine(line["data: ".Length..]);
        }
        return 0;
    }

    private static async Task<int> RunThreadAsync(string[] args)
    {
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        var id = GetOpt(args, ["-i", "--id"]) ?? GetOpt(args, ["--thread"]);
        var priority = GetOpt(args, ["-p", "--priority"]);
        if (id is null)
        {
            Console.Error.WriteLine("threads -i threadId [-p class:sub]");
            return 2;
        }
        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("threadId", id);
            if (priority is { Length: > 0 })
            {
                var p = ParsePriority(priority);
                w.WriteStartObject("priority");
                w.WriteString("class", ThreadPriorityClass.ClassName(p.Class));
                w.WriteNumber("sub", p.Sub);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, Url(server, "/admin/threads"));
        req.Content = new ByteArrayContent(body.ToArray());
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var res = await http.SendAsync(req).ConfigureAwait(false);
        Console.WriteLine(res.IsSuccessStatusCode ? $"thread {id} registered" : $"error {(int)res.StatusCode}");
        return (int)res.StatusCode is >= 200 and < 300 ? 0 : 1;
    }

    private static async Task<int> RunSwitchAsync(string[] args)
    {
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        var engine = GetOpt(args, ["-e", "--engine"]);
        var model = GetOpt(args, ["-m", "--model"]);
        if (engine is null || model is null)
        {
            Console.Error.WriteLine("switch -e engine -m model");
            return 2;
        }
        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("engine", engine);
            w.WriteString("model", model);
            w.WriteEndObject();
        }
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, Url(server, "/admin/switch"));
        req.Content = new ByteArrayContent(body.ToArray());
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var res = await http.SendAsync(req).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        Console.WriteLine($"[{(int)res.StatusCode}] {text}");
        return (int)res.StatusCode is >= 200 and < 300 ? 0 : 1;
    }

    // ── shared helpers ──────────────────────────────────────────────────────

    private static async Task<int> GetPrintAsync(string server, string path)
    {
        using var http = new HttpClient();
        using var res = await http.GetAsync($"{server.TrimEnd('/')}{path}").ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        Console.WriteLine(text);
        return (int)res.StatusCode is >= 200 and < 300 ? 0 : 1;
    }

    private static string? GetOpt(string[] args, string[] keys)
    {
        for (var i = 0; i < args.Length; i++)
            foreach (var k in keys)
                if (args[i] == k && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    return args[i + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string flag) => args.Contains(flag);

    private static ThreadPriority ParsePriority(string value)
    {
        _ = ThreadPriority.TryParse(value, out var p);
        return p;
    }
}
