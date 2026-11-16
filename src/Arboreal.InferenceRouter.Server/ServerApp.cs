using System.Diagnostics;
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
    public static WebApplication Build(string[] args, string? configPath = null, RouterConfig? configOverride = null)
    {
        configPath ??= Environment.GetEnvironmentVariable("AIRBODAL_ROUTER_CONFIG")
                       ?? Environment.GetEnvironmentVariable("AIR_CONFIG")
                       ?? "router.toml";
        var config = configOverride ?? RouterConfigLoader.LoadFile(configPath);
        var router = new RouterCore(config);
        router.Start();

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services.AddSingleton(router);

        var app = builder.Build();
        app.Urls.Add(config.Listen);

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
        app.MapDelete("/admin/tickets/{id}", (string id, HttpContext ctx, RouterCore r) => HandleCancel(ctx, r, id));

        app.MapGet("/healthz", () => Results.Text("ok"));

        return app;
    }

    public static void Run(string[] args, string? configPath = null, RouterConfig? configOverride = null)
    {
        using var app = Build(args, configPath, configOverride);
        app.Run();
    }

    // ── inference endpoints ─────────────────────────────────────────────────

    private static async Task HandleInference(HttpContext ctx, RouterCore router)
    {
        var ct = ctx.RequestAborted;
        using var body = new MemoryStream(4096);
        await ctx.Request.Body.CopyToAsync(body, ct).ConfigureAwait(false);
        var payload = body.ToArray();

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
                Options = new RouterRequestOptions { Priority = priority, CacheAffinityHash = affinity },
            }, ct).ConfigureAwait(false);
        }
        catch (RouterRequestException ex)
        {
            await WriteErrorAsync(ctx, MapStatus(ex.Code), ex.Code, ex.Message).ConfigureAwait(false);
            return;
        }

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
        var started = Stopwatch.StartNew();
        while (reader is null)
        {
            if (ct.IsCancellationRequested) return;
            if (ticket.StreamChannel is { } ch) { reader = ch; break; }
            if (ticket.State is TicketState.Executing
                or TicketState.Completed or TicketState.Failed or TicketState.Cancelled)
            {
                break;
            }
            if (started.ElapsedMilliseconds > 4_000)
            {
                started.Restart();
                lastStatus = await WriteKeepaliveAsync(writer, ticket, keepaliveSeq, lastStatus, ct).ConfigureAwait(false);
                keepaliveSeq++;
            }
            try { await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
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

    private static async Task HandleEvents(HttpContext ctx, RouterCore router)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        var writer = ctx.Response.BodyWriter;
        var ct = ctx.RequestAborted;
        var since = ctx.Request.Query.TryGetValue("since", out var s) && long.TryParse(s.ToString(), out var v) ? v : 0;

        foreach (var e in router.Events.Replay(since))
            await AirJson.WriteSseFrameAsync(writer, e, AirServerJson.AirServerContext.Default.RouterEvent, ct)
                .ConfigureAwait(false);

        // Live tail until client disconnects.
        var queue = Channel.CreateUnbounded<RouterEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        void Handler(RouterEvent e) => queue.Writer.TryWrite(e);
        router.Events.Subscribe(Handler);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (queue.Reader.TryRead(out var e))
                    await AirJson.WriteSseFrameAsync(writer, e, AirServerJson.AirServerContext.Default.RouterEvent, ct)
                        .ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
                await queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnect
        }
        finally
        {
            router.Events.Unsubscribe(Handler);
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
            _ = Task.Run(() => engine.WaitForModelAsync(model, Timeout.InfiniteTimeSpan, CancellationToken.None));
            ctx.Response.StatusCode = 202;
            var ack = new SwitchAck { Engine = engineName!, Model = model!, Switching = true };
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
