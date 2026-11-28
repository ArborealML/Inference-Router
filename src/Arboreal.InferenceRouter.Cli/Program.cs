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
    internal static string DefaultServer =>
        Environment.GetEnvironmentVariable("AIR_ROUTER_URL") ?? "http://127.0.0.1:8321";

    /// <summary>Absolutises a router path against the configured server. HttpClient rejects relative
    /// request URIs, so every call site goes through here (regression: the POST commands used to
    /// hand <c>HttpClient</c> a bare path and fail before reaching the router).</summary>
    private static string Url(string server, string path) => server.TrimEnd('/') + path;

    public static Task<int> Main(string[] args) => RunAsync(args, null);

    internal static async Task<int> RunAsync(string[] args, ITuneAdmission? tuneAdmission)
    {
        bool json = HasFlag(args, "--json");
        while (args is ["--json", ..]) args = args[1..];
        if (args.Length == 0) return await RunHelpAsync().ConfigureAwait(false);
        return args[0] switch
        {
            "completion" => await ShellCompletion.RunAsync(args[1..]).ConfigureAwait(false),
            "serve" => await RunServeAsync(args[1..]).ConfigureAwait(false),
            "chat" or "complete" => await RunChatAsync(args[1..], json).ConfigureAwait(false),
            "models" => await RunModelsAsync(args[1..], json).ConfigureAwait(false),
            "top" => await AirTop.RunAsync(args[1..], json).ConfigureAwait(false),
            "status" => await RunStatusAsync(args[1..], json).ConfigureAwait(false),
            "events" => await RunEventsAsync(args[1..], json).ConfigureAwait(false),
            "threads" => await RunThreadAsync(args[1..], json).ConfigureAwait(false),
            "switch" => await RunSwitchAsync(args[1..], json).ConfigureAwait(false),
            "config" => await RunConfigCommandAsync(args[1..], json).ConfigureAwait(false),
            "tickets" => await RunTicketAsync(args[1..], json).ConfigureAwait(false),
            "tune" => await RunTuneAsync(args[1..].Where(a => a != "--json").ToArray(), json, tuneAdmission).ConfigureAwait(false),
            "help" or "-h" or "--help" => await RunHelpAsync().ConfigureAwait(false),
            _ => await RunHelpAsync().ConfigureAwait(false),
        };
    }

    private static Task<int> RunHelpAsync()
    {
        Console.WriteLine("""
            arboreal inference router

            usage: air [--json] <command> [options]

            Output is readable text by default. Use --json before or after the command for machine output.
            Tune compare --json wraps the existing text in a Report string. Tune plan always writes its
            JSON manifest. Serve starts the host.

            commands:
              completion bash|powershell|zsh|fish       print the shell’s tab-completion registration
              serve [--config path] [--listen url]      run the router host in this process
              chat  [-m model] [-t thread] [-p prio]    send a chat completion (prompt args or stdin)
                    [--system text] [--no-stream] [--json] [-s server-url]
              models [-s server-url]                    list observed models and context windows
              top [-s server-url] [--ticket ID]         monitor engines, queues and ticket activity
              status [-s server-url]                    router stats (engines, queues, threads)
              events [-s server-url] [--since seq]      tail the live event stream (SSE)
              threads -i threadId [-p prio]             register a chat thread (admin)
              switch -e engine -m model [-s url]        start a model switch on an engine
              tickets <id> [-s server-url]              inspect one ticket
              config [--config path]                    validate a config file and print the plan
                                                      (no sockets, no lifecycle commands)
              tune compare --baseline FILE --candidate FILE
                                                      compare saved tuning runs or summaries (Linux x64)
              tune plan [--config FILE] --engine ENGINE --model MODEL --seed HEX
                        --max-concurrency N [--profile TIER]
                                                      plan an offline tuning workload (Linux x64)
              tune run [--config FILE] --engine ENGINE --model MODEL --profile TIER
                       --seed HEX --max-concurrency 1 --host-record FILE --out FILE
                                                      require an enrolled private tuning resident (Linux x64)
            """);
        return Task.FromResult(0);
    }

    private static async Task<int> RunTuneAsync(string[] args, bool json, ITuneAdmission? tuneAdmission)
    {
        if (args is ["plan", ..])
        {
            using var output = Console.OpenStandardOutput();
            return AirTuneWorkloadPlan.Run(args.AsSpan(1), output, Console.Error);
        }

        if (args is ["run", ..])
            return await AirTuneRun.RunAsync(args[1..], tuneAdmission, Console.Error).ConfigureAwait(false);

        if (!json) return AirTuneCompare.Run(args, Console.Out, Console.Error);
        using var report = new StringWriter(CultureInfo.InvariantCulture);
        int code = AirTuneCompare.Run(args, report, Console.Error);
        if (code == 0) WriteJsonString("Report", report.ToString());
        return code;
    }

    // ── serve ───────────────────────────────────────────────────────────────
    private static async Task<int> RunServeAsync(string[] args)
    {
        if (!ServerApp.TryReadConfigOption(args, out var config))
            return 2;
        string? listen = GetOpt(args, ["--listen", "-l"]);
        var runArgs = listen is { Length: > 0 } ? new[] { $"--urls={listen}" } : [];
        return await ServerApp.RunAsync(runArgs, config).ConfigureAwait(false);
    }

    // ── config (dry run) ────────────────────────────────────────────────────

    /// <summary>Parse + validate a config file and print the resolved engine / model /
    /// serving-profile plan. Deliberately touches nothing: no sockets, no lifecycle commands.</summary>
    private static Task<int> RunConfigCommandAsync(string[] args, bool json)
    {
        if (!ServerApp.TryReadConfigOption(args, out var config))
            return Task.FromResult(2);
        try
        {
            var path = RouterConfigLoader.ResolveFilePath(config);
            var cfg = RouterConfigLoader.LoadFile(path);
            var s = cfg.Scheduler;
            if (json)
            {
                WriteConfigJson(path, cfg);
                return Task.FromResult(0);
            }
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
                var (owner, provider) = LifecycleNames(e.LifecycleOwner);
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

    private static async Task<int> RunChatAsync(string[] args, bool json)
    {
        var model = GetOpt(args, ["-m", "--model"]);
        if (!TryGetPriorityOption(args, out var priority, out var priorityUsage))
        {
            Console.Error.WriteLine(priorityUsage);
            return 2;
        }
        if (!TryGetInferenceThreadOption(args, out var thread))
        {
            Console.Error.WriteLine("invalid thread: --thread requires one non-blank thread name without commas or control characters");
            return 2;
        }
        var system = GetOpt(args, ["--system"]);
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        var since = GetOpt(args, ["--since"]);
        bool stream = !HasFlag(args, "--no-stream");
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
        if (thread is not null) req.Headers.TryAddWithoutValidation("X-Inference-Thread", thread);
        // The value is forwarded unread: this caller asks the router, so the router's own 400 is what it
        // reads (§6). Only the option's own shape is checked here, in TryGetPriorityOption.
        if (priority is not null) req.Headers.TryAddWithoutValidation("X-Inference-Priority", priority);

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

    private static async Task<int> RunModelsAsync(string[] args, bool json)
    {
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        return await GetPrintAsync(server, "/v1/models", ResponseView.Models, json).ConfigureAwait(false);
    }

    private static async Task<int> RunStatusAsync(string[] args, bool json)
    {
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        return await GetPrintAsync(server, "/admin/status", ResponseView.Status, json).ConfigureAwait(false);
    }

    private static async Task<int> RunTicketAsync(string[] args, bool json)
    {
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        var id = args.Where((a, i) => !a.StartsWith('-') && (i == 0 || args[i - 1] is not ("-s" or "--server"))).FirstOrDefault() ?? "";
        if (id.Length == 0)
        {
            Console.Error.WriteLine("tickets <id>");
            return 2;
        }
        return await GetPrintAsync(server, $"/admin/tickets/{Uri.EscapeDataString(id)}", ResponseView.Ticket, json).ConfigureAwait(false);
    }

    private static async Task<int> RunEventsAsync(string[] args, bool json)
    {
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        var since = GetOpt(args, ["--since"]) ?? "0";
        using var http = new HttpClient();
        using var res = await http.GetAsync($"{server.TrimEnd('/')}/admin/events?since={since}",
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            CliOutput.Print(await res.Content.ReadAsStringAsync().ConfigureAwait(false), ResponseView.Event, json, (int)res.StatusCode);
            return 1;
        }
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync().ConfigureAwait(false));
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                CliOutput.Print(line["data: ".Length..], ResponseView.Event, json, (int)res.StatusCode);
        }
        return 0;
    }

    private static async Task<int> RunThreadAsync(string[] args, bool json)
    {
        var server = GetOpt(args, ["-s", "--server"]) ?? DefaultServer;
        var id = GetOpt(args, ["-i", "--id"]) ?? GetOpt(args, ["--thread"]);
        if (id is null)
        {
            Console.Error.WriteLine("threads -i threadId [-p class:sub]");
            return 2;
        }
        if (!TryGetPriorityOption(args, out var priority, out var priorityUsage))
        {
            Console.Error.WriteLine(priorityUsage);
            return 2;
        }
        // A malformed -p is refused here, before the request goes out. The admin route answers 400
        // invalid_request for a class name it does not know, but it takes class and sub as separate JSON
        // fields, so TryParse's Normal-on-failure used to post "high:+12" as normal/0 and take the 204 — a
        // priority nobody asked for (§6). `air chat -p` is deliberately different: it forwards the raw
        // header, so that caller reads the router's own 400.
        ThreadPriority requested = default;
        if (priority is not null && !ThreadPriority.TryParse(priority, out requested))
        {
            Console.Error.WriteLine($"invalid priority \"{priority}\": expected {PriorityGrammar}");
            return 2;
        }
        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("threadId", id);
            if (priority is not null)
            {
                w.WriteStartObject("priority");
                w.WriteString("class", ThreadPriorityClass.ClassName(requested.Class));
                w.WriteNumber("sub", requested.Sub);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, Url(server, "/admin/threads"));
        req.Content = new ByteArrayContent(body.ToArray());
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var res = await http.SendAsync(req).ConfigureAwait(false);
        if (res.IsSuccessStatusCode)
        {
            if (json) WriteJsonString("ThreadId", id);
            else Console.WriteLine($"thread {id} registered");
        }
        else CliOutput.Print(await res.Content.ReadAsStringAsync().ConfigureAwait(false), ResponseView.Thread,
            json, (int)res.StatusCode);
        return (int)res.StatusCode is >= 200 and < 300 ? 0 : 1;
    }

    private static async Task<int> RunSwitchAsync(string[] args, bool json)
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
        CliOutput.Print(text, ResponseView.Switch, json, (int)res.StatusCode);
        return (int)res.StatusCode is >= 200 and < 300 ? 0 : 1;
    }

    // ── shared helpers ──────────────────────────────────────────────────────

    private static async Task<int> GetPrintAsync(string server, string path, ResponseView view, bool json)
    {
        using var http = new HttpClient();
        using var res = await http.GetAsync($"{server.TrimEnd('/')}{path}").ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        CliOutput.Print(text, view, json, (int)res.StatusCode);
        return (int)res.StatusCode is >= 200 and < 300 ? 0 : 1;
    }

    private static void WriteJsonString(string key, string value)
    {
        using var output = Console.OpenStandardOutput();
        using var json = new Utf8JsonWriter(output);
        json.WriteStartObject();
        json.WriteString(key, value);
        json.WriteEndObject();
        json.Flush();
        output.WriteByte((byte)'\n');
    }

    private static (string Owner, string Provider) LifecycleNames(EngineLifecycleOwner? owner) => owner switch
    {
        EngineLifecycleOwner.Router => ("router", "docker"),
        EngineLifecycleOwner.Delegated => ("delegated", "exec"),
        _ => ("external", "none"),
    };

    private static void WriteConfigJson(string path, RouterConfig cfg)
    {
        using var output = Console.OpenStandardOutput();
        using var json = new Utf8JsonWriter(output);
        json.WriteStartObject();
        json.WriteString("Path", path);
        json.WriteString("Listen", cfg.Listen);
        json.WriteString("DefaultModel", cfg.DefaultModel);
        json.WriteBoolean("AutoSwitch", cfg.AutoSwitch);
        json.WriteNumber("SwitchCooldownMs", cfg.SwitchCooldownMs);
        json.WriteStartObject("Scheduler");
        json.WriteNumber("MaxInflightPerEngine", cfg.Scheduler.MaxInflightPerEngine);
        json.WriteNumber("MaxQueuePerModel", cfg.Scheduler.MaxQueuePerModel);
        json.WriteNumber("CharsPerTokenEstimate", cfg.Scheduler.CharsPerTokenEstimate);
        json.WriteNumber("PrefixAffinityThresholdChars", cfg.Scheduler.PrefixAffinityThresholdChars);
        json.WriteEndObject();
        json.WriteStartArray("Engines");
        foreach (var (name, engine) in cfg.Engines)
        {
            json.WriteStartObject();
            json.WriteString("Name", name);
            json.WriteString("Endpoint", engine.Endpoint.ToString());
            json.WriteBoolean("Remote", engine.Remote);
            var (_, provider) = LifecycleNames(engine.LifecycleOwner);
            json.WriteString("Kind", engine.Kind.ToString());
            json.WriteString("LifecycleOwner", engine.LifecycleOwner.ToString());
            json.WriteString("Provider", provider);
            json.WriteString("ApiKeySource", engine.ApiKey is { Length: > 0 } ? "inline" :
                engine.ApiKeyFile is { Length: > 0 } ? "file" : "none");
            json.WriteStartObject("HealthProbe");
            json.WriteString("Path", engine.Health?.Path ?? "/props");
            json.WriteString("ContextPath", engine.Health?.ContextPath ?? "n_ctx");
            json.WriteString("SlotsPath", engine.Health?.SlotsPath ?? "total_slots");
            json.WriteEndObject();
            json.WriteStartArray("Models");
            foreach (var (id, model) in engine.Catalog)
            {
                json.WriteStartObject();
                json.WriteString("Id", id);
                json.WriteString("DefaultProfile", model.Profile);
                if (model.Select is { } selector)
                {
                    json.WriteStartObject("Select");
                    json.WriteString("Exec", selector.Exec);
                    json.WriteStartArray("Args");
                    foreach (var arg in selector.Args) json.WriteStringValue(arg);
                    json.WriteEndArray();
                    json.WriteBoolean("WaitHealth", selector.WaitHealth);
                    json.WriteNumber("WaitTimeoutMs", selector.WaitTimeoutMs);
                    json.WriteEndObject();
                }
                json.WriteStartArray("Aliases");
                foreach (var alias in model.Aliases) json.WriteStringValue(alias);
                json.WriteEndArray();
                json.WriteStartArray("Profiles");
                foreach (var profile in model.ServingProfiles)
                {
                    json.WriteStartObject();
                    json.WriteString("Name", profile.Name);
                    json.WriteNumber("MaxContext", profile.MaxContext);
                    json.WriteNumber("MaxSlots", profile.MaxSlots);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("Threads");
        foreach (var (id, thread) in cfg.Threads)
        {
            json.WriteStartObject();
            json.WriteString("Id", id);
            json.WriteString("Priority", thread.Priority.ToString());
            json.WriteNumber("Weight", thread.Weight);
            json.WriteString("ModelAffinity", thread.ModelAffinity);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        output.WriteByte((byte)'\n');
    }

    internal static string? GetOpt(string[] args, string[] keys)
    {
        for (var i = 0; i < args.Length; i++)
            foreach (var k in keys)
                if (args[i] == k && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    return args[i + 1];
        return null;
    }

    /// <summary>Read the priority option from the raw arguments, where its shape is visible. An absent option
    /// is the caller asking for nothing, which is the default (§6); one option with one usable value is the
    /// request. Anything else is a typing mistake the CLI can see for itself — <c>-p</c> followed by another
    /// option, by nothing, or by an empty string, or the option given twice — and each of those used to read
    /// exactly like an absent one, registering the default priority and exiting 0 for a value the caller
    /// typed. The verbs check this rather than the shared <see cref="GetOpt"/>, because other verbs pass
    /// values that legitimately begin with a dash (a <c>--system</c> prompt, a <c>--since</c> sequence) and
    /// GetOpt cannot tell those from an option. The value is returned unread: only <c>air threads</c> parses
    /// it, and <c>air chat</c> forwards it so the router refuses it in its own words.
    /// </summary>
    private static bool TryGetPriorityOption(string[] args, out string? priority, out string usage)
    {
        priority = null;
        usage = string.Empty;
        var given = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not ("-p" or "--priority")) continue;
            given++;
            var value = i + 1 < args.Length ? args[i + 1] : null;
            if (value is null) usage = "--priority given with no value";
            else if (value.Length == 0) usage = "--priority given an empty value";
            else if (value.StartsWith('-')) usage = $"--priority given \"{value}\", which reads as another option";
            else priority ??= value;
            i++;    // the token after the option belongs to it, even when it spells another option
        }
        if (given == 0) return true;
        if (usage.Length == 0 && given <= 1) return true;
        if (usage.Length == 0) usage = $"--priority given {given} times, expected one value";
        usage = $"invalid priority: {usage}; expected {PriorityGrammar}";
        return false;
    }

    private static bool TryGetInferenceThreadOption(string[] args, out string? thread)
    {
        thread = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not ("-t" or "--thread")) continue;
            if (thread is not null || ++i >= args.Length) return false;
            var name = args[i];
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-') ||
                name.Contains(',', StringComparison.Ordinal) || name.Any(char.IsControl))
                return false;
            thread = name;
        }
        return true;
    }

    private static bool HasFlag(string[] args, string flag) => args.Contains(flag);

    /// <summary>The priority grammar, in the one form the router's parser admits (§6). Quoted on stderr by
    /// <c>air threads -p</c> so the operator can see the spelling to retry rather than a silent default.</summary>
    private const string PriorityGrammar =
        "class[:sub] with class one of background|low|normal|high|interactive written in lowercase and " +
        "sub 0-255 as one to three digits with no sign, spaces or leading zero";
}
