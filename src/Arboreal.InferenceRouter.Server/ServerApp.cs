using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Server;

/// <summary>HTTP composition root: builds the minimal-API server around a RouterCore.
/// AOT strategy: CreateSlimBuilder + statically declared handlers; JSON bodies are read as raw
/// bytes and forwarded verbatim to engines (full OpenAI surface incl. tools/multimodal without
/// DTO binding); responses are raw passthrough bytes; admin DTOs use AirServerJson.</summary>
public static class ServerApp
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Owned by the DI container: disposed (async) when the WebApplication is disposed in RunAsync.")]
    public static WebApplication Build(string[] args, string? configPath = null, RouterConfig? configOverride = null)
    {
        configPath ??= Environment.GetEnvironmentVariable("ARBOREAL_ROUTER_CONFIG")
                       ?? Environment.GetEnvironmentVariable("AIR_CONFIG")
                       ?? "router.toml";
        var config = configOverride ?? RouterConfigLoader.LoadFile(configPath);
        // Owned by the DI container: disposed (async) when the WebApplication is disposed in RunAsync.
        var router = new RouterCore(config);
        router.Start();

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services.AddSingleton(router);

        var app = builder.Build();
        // Explicit --urls / ASPNETCORE_URLS win over the config's listen address; setting
        // app.Urls at all replaces the host's address list, so only do it when needed.
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            app.Urls.Add(config.Listen);

        // Request-delegate lambdas: under PublishAot the RDG source generator rewrites every
        // Map* call into generated reflection-free invocation code, so the compile-time
        // IL2026/IL3050 signal is expected noise in non-publish IsAotCompatible builds only.
        // The AOT publish (the shipping artifact) is verified free of these warnings.
#pragma warning disable IL2026 // CA2026-like: RDG-rewritten Map* request delegates
#pragma warning disable IL3050 // Dynamic code (ReflectionMetadata) in Map* — RDG replaces it
        // OpenAI-compatible surface (passthrough).
        app.MapPost("/v1/chat/completions", (HttpContext ctx, RouterCore r) => HandleInference(ctx, r));
        app.MapPost("/v1/completions", (HttpContext ctx, RouterCore r) => HandleInference(ctx, r));
        app.MapGet("/v1/models", (HttpContext ctx, RouterCore r) => HandleModels(ctx, r));

        // Admin / control surface.
        app.MapGet("/admin/status", (HttpContext ctx, RouterCore r) => HandleStatus(ctx, r));
        app.MapGet("/admin/events", (HttpContext ctx, RouterCore r) => HandleEvents(ctx, r));
        app.MapPost("/admin/threads", (HttpContext ctx, RouterCore r) => HandleThreads(ctx, r));
        app.MapPost("/admin/switch", (HttpContext ctx, RouterCore r) => HandleSwitch(ctx, r));
        app.MapGet("/admin/tickets/{id}", (string id, HttpContext ctx, RouterCore r) => HandleTicket(ctx, r, id));
        app.MapGet("/admin/tickets/{id}/events", (string id, HttpContext ctx, RouterCore r) => HandleEvents(ctx, r, id));
        app.MapDelete("/admin/tickets/{id}", (string id, HttpContext ctx, RouterCore r) => HandleCancel(ctx, r, id));

        app.MapGet("/healthz", () => Results.Text("ok"));
#pragma warning restore IL3050
#pragma warning restore IL2026

        return app;
    }

    public static async Task<int> RunAsync(string[] args, string? configPath = null, RouterConfig? configOverride = null)
    {
        var app = Build(args, configPath, configOverride);
        try
        {
            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);   // disposes RouterCore (IAsyncDisposable)
        }
    }

    // ── inference endpoints ─────────────────────────────────────────────────

    private static async Task HandleInference(HttpContext ctx, RouterCore router)
    {
        var ct = ctx.RequestAborted;
        var read = await ReadBodyAsync(ctx, router, ct).ConfigureAwait(false);
        if (read is null) return;              // the refusal is already on the wire
        var payload = read;

        // Minimal probe of the raw body (model, stream) — full passthrough otherwise.
        string? requestedModel = null;
        bool stream = false;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                    requestedModel = m.GetString() is { Length: > 0 } name ? name : null;
                if (doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True)
                    stream = true;
            }
        }
        catch (JsonException)
        {
            await WriteErrorAsync(ctx, 400, "invalid_request", "body is not valid JSON").ConfigureAwait(false);
            return;
        }

        var threadId = ctx.Request.Headers["X-Inference-Thread"].ToString() is { Length: > 0 } t ? t : "anon";
        ThreadPriority? priority = null;
        if (ThreadPriority.TryParse(ctx.Request.Headers["X-Inference-Priority"].ToString(), out var p))
            priority = p;
        var affinity = ctx.Request.Headers["X-Inference-Affinity"].ToString() is { Length: > 0 } a ? a : null;

        RouterTicket ticket;
        try
        {
            ticket = await router.EnqueueAsync(threadId, new InferenceRequest
            {
                Model = requestedModel,
                Stream = stream,
                Body = payload,
                Options = new RouterRequestOptions
                {
                    Priority = priority,
                    CacheAffinityHash = affinity,
                    RequiredContext = RequiredContextFromHeader(ctx),
                },
            }, ct).ConfigureAwait(false);
        }
        catch (RouterRequestException ex)
        {
            await WriteErrorAsync(ctx, MapStatus(ex.Code), ex.Code, ex.Message).ConfigureAwait(false);
            return;
        }

        // The ticket id is how a client (or an agent harness) reaches /admin/tickets/{id} to watch
        // or cancel this request; without it the id only exists in the event stream.
        ctx.Response.Headers["X-Inference-Ticket"] = ticket.Id;

        // A caller that goes away must stop costing an engine slot and an output buffer. Before this,
        // disconnecting from a stream left the router serving the rest of the response into a closed
        // socket and recording the request as completed: a truncated request looked like a served one,
        // in the stats and in the event feed.
        using var callerAbort = ctx.RequestAborted.Register(static state =>
        {
            var (core, id) = ((RouterCore, string))state!;
            core.TryCancel(id);
        }, (router, ticket.Id));

        if (stream)
        {
            await StreamTicketAsync(ctx, ticket).ConfigureAwait(false);
        }
        else
        {
            try
            {
                var result = await ticket.CompletionTask.WaitAsync(ct).ConfigureAwait(false);
                ctx.Response.Headers.ContentType = "application/json";
                await ctx.Response.Body.WriteAsync(result.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (RouterRequestException ex)
            {
                await WriteErrorAsync(ctx, MapStatus(ex.Code), ex.Code, ex.Message).ConfigureAwait(false);
            }
        }
    }

    /// <summary>X-Inference-Required-Context: context tokens the client must be served under.
    /// Absent → the router estimates from prompt chars. Exceeds the active window → profile switch.</summary>
    private static long? RequiredContextFromHeader(HttpContext ctx)
    {
        var raw = ctx.Request.Headers["X-Inference-Required-Context"].ToString();
        if (raw.Length == 0) return null;
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }

    /// <summary>SSE passthrough: upstream frames verbatim; SSE keepalive comment frames while the
    /// request is queued/switching so clients (e.g. Pi's idle timeout) never time out.</summary>
    private static async Task StreamTicketAsync(HttpContext ctx, RouterTicket ticket)
    {
        var ct = ctx.RequestAborted;
        ctx.Response.StatusCode = 200;
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";

        var writer = ctx.Response.BodyWriter;
        var lastStatus = "";
        var keepaliveSeq = 0;
        lastStatus = await WriteKeepaliveAsync(writer, ticket, keepaliveSeq, lastStatus, ct).ConfigureAwait(false);
        keepaliveSeq++;

        ChannelReader<ReadOnlyMemory<byte>>? reader = null;
        var channel = ticket.StreamChannel;
        if (channel is null)
        {
            // Wait on readiness, not on a poll interval: the gap between the relay starting and the
            // request being admitted is normally microseconds, and a fixed poll would add that
            // interval to time-to-first-token for every stream. Keepalives go out every 4 s while
            // the request is queued or switching models.
            var ready = ticket.StreamReady;
            while (!ready.IsCompleted)
            {
                if (ct.IsCancellationRequested) return;
                lastStatus = await WriteKeepaliveAsync(writer, ticket, keepaliveSeq, lastStatus, ct).ConfigureAwait(false);
                keepaliveSeq++;
                await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(4), ct)).ConfigureAwait(false);
            }

            channel = await ready.ConfigureAwait(false);
        }

        if (channel is not null)
        {
            reader = channel.Reader;
        }

        if (reader is not null)
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var chunk))
                    await writer.WriteAsync(chunk, ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        if (ticket.State is TicketState.Failed)
        {
            var json = JsonSerializer.Serialize(
                new ErrorBody { Error = new ErrorDetail { Message = ticket.Error ?? "engine error", Code = ticket.ErrorCode ?? "engine_error" } },
                AirServerJson.AirServerContext.Default.ErrorBody);
            await AirJson.WriteFrameAsync(writer, AirJson.Frame(json, "[DONE]"), ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> WriteKeepaliveAsync(PipeWriter writer, RouterTicket ticket, int seq,
        string lastStatus, CancellationToken ct)
    {
        var status = ticket.State switch
        {
            TicketState.Queued => $"queued model={ticket.Model}",
            TicketState.WaitingForModel => $"switching model={ticket.Model} engine={ticket.Engine}",
            _ => $"engine={ticket.Engine}",
        };
        var changed = !string.Equals(status, lastStatus, StringComparison.Ordinal);
        lastStatus = status;
        var frame = changed
            ? AirJson.Frame($"[router keepalive seq={seq} status=\"{status}\"]")
            : AirJson.Frame($"[router keepalive seq={seq}]");
        await AirJson.WriteFrameAsync(writer, frame, ct).ConfigureAwait(false);
        return status;
    }

    /// <summary>Read a request body under the configured cap. Content-Length is a fast path, not the
    /// check: a caller that understates it is still cut off, because the copy counts bytes. Refusing with
    /// the error contract matters more than it looks — the alternative is Kestrel's own opaque failure,
    /// which is not a shape a caller can branch on.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext ctx, RouterCore router, CancellationToken ct)
    {
        var limit = router.Config.MaxRequestBodyBytes;
        if (ctx.Request.ContentLength is { } declared && declared > limit)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge, "request_body_too_large",
                $"the request body is {declared} bytes and this router reads at most {limit}; raise max_request_body_bytes if that is really intended")
                .ConfigureAwait(false);
            return null;
        }

        using var body = new MemoryStream(4096);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await ctx.Request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge, "request_body_too_large",
                    $"the request body exceeded {limit} bytes; raise max_request_body_bytes if that is really intended")
                    .ConfigureAwait(false);
                return null;
            }
            body.Write(buffer, 0, read);
        }
        return body.ToArray();
    }

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.Headers.ContentType = "application/json";
        var json = JsonSerializer.Serialize(
            new ErrorBody { Error = new ErrorDetail { Message = message, Code = code } },
            AirServerJson.AirServerContext.Default.ErrorBody);
        await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json), ctx.RequestAborted).ConfigureAwait(false);
    }

    private static int MapStatus(string code) => code switch
    {
        "queue_overflow" => 429,
        "deadline_exceeded" => 408,
        "cancelled" => 499,
        "switch_failed" or "model_unavailable" => 503,
        "engine_error" or "engine_unreachable" => 502,
        "engine_timeout" => 504,
        _ => 500,
    };

    // ── admin endpoints ─────────────────────────────────────────────────────

    private static async Task HandleModels(HttpContext ctx, RouterCore router)
    {
        using var ms = new MemoryStream(1024);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("object", "list");
            w.WriteStartArray("data");
            foreach (var e in router.Engines.Values)
            {
                foreach (var model in e.LoadedModels)
                {
                    w.WriteStartObject();
                    w.WriteString("id", model);
                    w.WriteString("object", "model");
                    w.WriteNumber("created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    w.WriteString("owned_by", e.Name);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        ctx.Response.Headers.ContentType = "application/json";
        await ctx.Response.Body.WriteAsync(ms.ToArray().AsMemory(), ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task HandleStatus(HttpContext ctx, RouterCore router)
    {
        await AirJson.WriteAsync(ctx.Response, router.GetStats(),
            AirServerJson.AirServerContext.Default.RouterStats, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static Task HandleEvents(HttpContext ctx, RouterCore router, string? ticketOverride = null)
        => WriteEventStreamAsync(ctx, router, BuildEventFilter(ctx, ticketOverride));

    /// <summary>Filters come from the query string so one route serves the global feed, a thread's
    /// feed and one ticket's feed. Unknown parameters are ignored, not errors: an agent that sends
    /// `ticket=` to the global route gets what it asked for.</summary>
    private static RouterEventFilter BuildEventFilter(HttpContext ctx, string? ticketOverride)
    {
        var q = ctx.Request.Query;
        long since = 0;
        if (q.TryGetValue("since", out var s)
            && long.TryParse(s.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            since = v;
        string[]? kinds = null;
        if (q.TryGetValue("kinds", out var k) && !string.IsNullOrWhiteSpace(k.ToString()))
            kinds = k.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? Thread() => q.TryGetValue("thread", out var th) ? th.ToString() : null;
        string? Engine() => q.TryGetValue("engine", out var en) ? en.ToString() : null;
        string? Ticket() => ticketOverride ?? (q.TryGetValue("ticket", out var ti) ? ti.ToString() : null);
        return new RouterEventFilter
        {
            SinceSeq = since,
            Kinds = kinds,
            ThreadId = Thread(),
            Engine = Engine(),
            TicketId = Ticket(),
        };
    }

    private static async Task WriteEventStreamAsync(HttpContext ctx, RouterCore router, RouterEventFilter filter)
    {
        // A feed is a subscription the client keeps open, so a full ring is refused with a plain 503
        // before any SSE headers go out. A 200 that ends at once is indistinguishable from a filter
        // that matches nothing, and an exception after the headers are flushed looks to the client
        // like a truncated success. The ring's own refusal is the backstop for a race past this check.
        if (router.Events.LiveSubscribers >= router.Events.MaxLiveSubscribers)
        {
            await WriteErrorAsync(ctx, 503, "too_many_subscribers",
                "the event feed is at its subscriber limit; close a feed and reconnect with the last Seq you saw")
                .ConfigureAwait(false);
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        var writer = ctx.Response.BodyWriter;
        // Headers out before the first event: a filtered stream that matches nothing (an unknown
        // ticket, a quiet engine) must still answer 200 and hold the stream open, not appear to hang.
        await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
        try
        {
            await foreach (var e in router.SubscribeEvents(filter, ctx.RequestAborted).ConfigureAwait(false))
            {
                await AirJson.WriteSseFrameAsync(writer, e, AirServerJson.AirServerContext.Default.RouterEvent, ctx.RequestAborted)
                    .ConfigureAwait(false);
                await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnect
        }
    }

    private static async Task HandleThreads(HttpContext ctx, RouterCore router)
    {
        using var body = new MemoryStream(1024);
        await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            var threadId = root.TryGetProperty("threadId", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(threadId))
            {
                await WriteErrorAsync(ctx, 400, "invalid_request", "threadId is required").ConfigureAwait(false);
                return;
            }
            var priority = router.Config.DefaultPriority;
            if (root.TryGetProperty("priority", out var prio) && prio.ValueKind == JsonValueKind.Object)
            {
                var classStr = prio.TryGetProperty("class", out var c) ? c.GetString() : null;
                var sub = prio.TryGetProperty("sub", out var sub2) && sub2.ValueKind == JsonValueKind.Number
                    ? (int)sub2.GetDouble() : 0;
                if (classStr is null || !ThreadPriorityClass.TryParseClass(classStr, out var cls))
                {
                    await WriteErrorAsync(ctx, 400, "invalid_request", $"priority.class '{classStr}' unknown")
                        .ConfigureAwait(false);
                    return;
                }
                priority = new ThreadPriority(cls, (byte)Math.Clamp(sub, 0, ThreadPriority.MaxSubPriority));
            }
            var options = new RouterThreadOptions();
            if (root.TryGetProperty("weight", out var wt) && wt.ValueKind == JsonValueKind.Number)
                options = options with { Weight = wt.GetDouble() };
            if (root.TryGetProperty("modelAffinity", out var ma) && ma.ValueKind == JsonValueKind.String)
                options = options with { ModelAffinity = ma.GetString() };

            router.RegisterThread(threadId!, priority, options);
            ctx.Response.StatusCode = 204;
        }
        catch (JsonException)
        {
            await WriteErrorAsync(ctx, 400, "invalid_request", "body is not valid JSON").ConfigureAwait(false);
        }
    }

    private static async Task HandleSwitch(HttpContext ctx, RouterCore router)
    {
        using var body = new MemoryStream(1024);
        await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var engineName = doc.RootElement.TryGetProperty("engine", out var e) ? e.GetString() : null;
            var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
            var profileName = doc.RootElement.TryGetProperty("profile", out var p) ? p.GetString() : null;
            if (engineName is null || model is null || !router.Engines.TryGetValue(engineName, out var engine))
            {
                await WriteErrorAsync(ctx, 400, "invalid_request", "engine and model are required (engine must exist)")
                    .ConfigureAwait(false);
                return;
            }
            if (engine.Switching && engine.SwitchingToward != model)
            {
                await WriteErrorAsync(ctx, 409, "switch_in_progress",
                    $"engine {engineName} is switching to {engine.SwitchingToward}").ConfigureAwait(false);
                return;
            }
            var profile = engine.GetProfile(model!, profileName);
            if (profileName is { Length: > 0 } && profile is null)
            {
                await WriteErrorAsync(ctx, 400, "unknown_profile",
                    $"engine {engineName} has no serving profile '{profileName}' for {model}").ConfigureAwait(false);
                return;
            }
            // Fire-and-forget by design: the switch continues after the 202 ack is written;
            // its lifetime is bounded by engine shutdown, not by this request.
#pragma warning disable IDISP013 // await-in-using not applicable: intentionally detached Task
            if (profile is null)
            {
                _ = Task.Run(() => engine.WaitForModelAsync(model!, Timeout.InfiniteTimeSpan, CancellationToken.None));
            }
            else
            {
                _ = Task.Run(() => engine.WaitForProfileAsync(model!, profile, Timeout.InfiniteTimeSpan, CancellationToken.None));
            }
#pragma warning restore IDISP013
            ctx.Response.StatusCode = 202;
            var ack = new SwitchAck { Engine = engineName!, Model = model!, Profile = profile?.Name, Switching = true };
            await AirJson.WriteAsync(ctx.Response, ack,
                AirServerJson.AirServerContext.Default.SwitchAck, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(ctx, 400, "invalid_request", "body is not valid JSON").ConfigureAwait(false);
        }
    }

    private static async Task HandleTicket(HttpContext ctx, RouterCore router, string id)
    {
        var snapshot = router.TryGetTicket(id);
        if (snapshot is null)
        {
            await WriteErrorAsync(ctx, 404, "not_found", $"ticket {id} not found").ConfigureAwait(false);
            return;
        }
        await AirJson.WriteAsync(ctx.Response, snapshot, AirServerJson.AirServerContext.Default.TicketSnapshot,
            ctx.RequestAborted).ConfigureAwait(false);
    }

    private static Task HandleCancel(HttpContext ctx, RouterCore router, string id)
    {
        var ok = router.TryCancel(id);
        ctx.Response.StatusCode = ok ? 204 : 404;
        return Task.CompletedTask;
    }
}
