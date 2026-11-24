using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Arboreal.InferenceRouter;
using Microsoft.AspNetCore.Http;

namespace Arboreal.InferenceRouter.Server;

/// <summary>HTTP composition root: builds the minimal-API server around a RouterCore.
/// AOT strategy: CreateSlimBuilder + statically declared handlers; JSON bodies are read as raw
/// bytes and forwarded verbatim to engines (full OpenAI surface incl. tools/multimodal without
/// DTO binding); responses are raw passthrough bytes; admin DTOs use AirServerJson.</summary>
public static partial class ServerApp
{
    public static bool TryReadConfigOption(ReadOnlySpan<string> args, out string? path, List<string>? remaining = null)
    {
        path = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not ("--config" or "-c"))
            {
                remaining?.Add(args[i]);
                continue;
            }
            if (++i >= args.Length || args[i].Length == 0 || args[i][0] == '-')
            {
                Console.Error.WriteLine("error: --config requires a file path");
                return false;
            }
            path ??= args[i];
        }
        return true;
    }

    public static WebApplication Build(string[] args, string? configPath = null, RouterConfig? configOverride = null)
        => Build(args, configPath, configOverride, TimeProvider.System);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Owned by the DI container: disposed (async) when the WebApplication is disposed in RunAsync.")]
    internal static WebApplication Build(string[] args, string? configPath, RouterConfig? configOverride,
        TimeProvider streamClock, Func<EngineDefinition, Lifecycle.IEngineLifecycleProvider>? lifecycleFactory = null,
        Func<EngineDefinition, HttpMessageHandler>? httpHandlerFactoryForTests = null, string? reloadTokenForTests = null,
        Func<string?>? readTargetResetOptInForTests = null, TimeProvider? adminSwitchClockForTests = null)
    {
        configPath = RouterConfigLoader.ResolveFilePath(configPath);
        var credential = configOverride is null
            ? reloadTokenForTests ?? Environment.GetEnvironmentVariable("ARBOREAL_ROUTER_RELOAD_TOKEN")
            : null;
        FileReloadEndpoint? reload = null;
        ReloadConfiguration? loaded = null;
        string? targetResetInstance = null;
        var instance = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(credential))
        {
            var path = Path.GetFullPath(configPath);
            loaded = RouterConfigLoader.LoadForReload(File.ReadAllText(path), path);
            reload = new FileReloadEndpoint(path, credential, instance);
            var resetOptIn = readTargetResetOptInForTests is { } readOptIn
                ? readOptIn() : Environment.GetEnvironmentVariable("ARBOREAL_ROUTER_TARGET_RESET_ENABLED");
            if (string.Equals(resetOptIn, "true", StringComparison.OrdinalIgnoreCase))
                targetResetInstance = instance;
        }
        var config = configOverride ?? loaded?.Config ?? RouterConfigLoader.LoadFile(configPath);
        if (httpHandlerFactoryForTests is not null) config.HttpHandlerFactory = httpHandlerFactoryForTests;

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services.AddSingleton(_ => new RouterCore(config, null, null, TimeProvider.System,
            lifecycleFactory, reload: loaded, nativeResetStartupInstance: targetResetInstance));
        builder.Services.AddSingleton(streamClock);
        builder.Services.AddSingleton(new AdminSwitchOperations(adminSwitchClockForTests ?? TimeProvider.System));

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
        app.MapPost("/v1/chat/completions", (HttpContext ctx, RouterCore r, TimeProvider clock) => HandleInference(ctx, r, clock));
        app.MapPost("/v1/completions", (HttpContext ctx, RouterCore r, TimeProvider clock) => HandleInference(ctx, r, clock));
        app.MapGet("/v1/models", (HttpContext ctx, RouterCore r) => HandleModels(ctx, r));

        // Admin / control surface.
        app.MapGet("/admin/catalog", (HttpContext ctx, RouterCore r) => HandleCatalog(ctx, r, instance));
        app.MapGet("/admin/status", (HttpContext ctx, RouterCore r) => HandleStatus(ctx, r));
        app.MapGet("/admin/events", (HttpContext ctx, RouterCore r) => HandleEvents(ctx, r));
        app.MapPost("/admin/threads", (HttpContext ctx, RouterCore r) => HandleThreads(ctx, r));
        app.MapPost("/admin/switch", (HttpContext ctx, RouterCore r, AdminSwitchOperations operations) =>
            HandleSwitch(ctx, r, operations));
        app.MapGet("/admin/switch/{operationId}", (string operationId, HttpContext ctx, AdminSwitchOperations operations) =>
            HandleSwitchLookup(ctx, operations, operationId));
        if (reload is not null)
        {
            app.MapGet("/admin/config/reload", (HttpContext ctx, RouterCore r) => reload.GetAsync(ctx, r));
            app.MapPost("/admin/config/reload", (HttpContext ctx, RouterCore r) => reload.ApplyAsync(ctx, r));
            if (targetResetInstance is not null)
                app.MapPost("/admin/config/target-reset", (HttpContext ctx, RouterCore r) => reload.ResetTargetAsync(ctx, r));
        }
        app.MapGet("/admin/tickets/{id}", (string id, HttpContext ctx, RouterCore r) => HandleTicket(ctx, r, id));
        app.MapGet("/admin/tickets/{id}/events", (string id, HttpContext ctx, RouterCore r) => HandleEvents(ctx, r, id));
        app.MapDelete("/admin/tickets/{id}", (string id, HttpContext ctx, RouterCore r) => HandleCancel(ctx, r, id));

        app.MapGet("/healthz", () => Results.Text("ok"));
#pragma warning restore IL3050
#pragma warning restore IL2026

        app.Services.GetRequiredService<RouterCore>().Start();
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

    private static async Task HandleInference(HttpContext ctx, RouterCore router, TimeProvider streamClock)
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
            ticket = await router.EnqueueHttpAsync(threadId, new InferenceRequest
            {
                Model = requestedModel,
                Stream = stream,
                Body = payload,
                Options = new RouterRequestOptions
                {
                    Priority = priority,
                    CacheAffinityHash = affinity,
                    Profile = ProfileFromHeader(ctx, "X-Inference-Profile"),
                    ThreadTitle = OriginLabel(ctx, "X-Inference-Thread-Title"),
                    TmuxSession = OriginLabel(ctx, "X-Inference-Tmux-Session"),
                    PreferredProfile = ProfileFromHeader(ctx, "X-Inference-Preferred-Profile"),
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
            await StreamTicketAsync(ctx, ticket, streamClock, router).ConfigureAwait(false);
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

    private static string? OriginLabel(HttpContext ctx, string header)
    {
        if (!ctx.Request.Headers.TryGetValue(header, out var values) || values.Count != 1 ||
            values[0] is not { Length: > 0 and <= 1536 } raw) return null;
        var label = Uri.UnescapeDataString(raw).Trim();
        if (label.Length is 0 or > 128) return null;
        foreach (var rune in label.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) return null;
        return label;
    }

    private static string? ProfileFromHeader(HttpContext ctx, string header)
    {
        if (!ctx.Request.Headers.TryGetValue(header, out var values)) return null;
        if (values.Count != 1 || values[0] is not { } name || string.IsNullOrWhiteSpace(name) || name.Contains(',', StringComparison.Ordinal))
            throw new RouterRequestException("invalid_profile", "Send a single named serving profile.");
        return name;
    }

    private static readonly TimeSpan StreamKeepaliveInterval = TimeSpan.FromSeconds(4);

    /// <summary>SSE passthrough with keepalive comments until a complete upstream block or terminal result.</summary>
    internal static async Task StreamTicketAsync(HttpContext ctx, RouterTicket ticket, TimeProvider clock,
        RouterCore? router = null)
    {
        var ct = ctx.RequestAborted;
        var delivery = ticket.HttpDelivery;
        try
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            if (HttpProtocol.IsHttp11(ctx.Request.Protocol))
                ctx.Response.Headers.Connection = "keep-alive";

            var writer = ctx.Response.BodyWriter;
            var output = delivery is null && ticket.IsHttpStream ? new DefaultStreamOutput() : null;
            var sink = new InferenceStreamSink(writer, ticket, router, clock, output);
            ct.ThrowIfCancellationRequested();
            var (reader, first) = await ReadFirstChunkAsync(sink, ticket, clock, ct).ConfigureAwait(false);
            if (reader is not null)
            {
                await sink.WritePayloadAsync(first, ct).ConfigureAwait(false);
                while (await WaitForStreamDataAsync(reader, delivery, ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var chunk))
                        await sink.WritePayloadAsync(chunk, ct).ConfigureAwait(false);
                    if (delivery is null)
                    {
                        var result = await writer.FlushAsync(ct).ConfigureAwait(false);
                        output?.Observe(result);
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            if (delivery is not null && delivery.Sealed.IsCompletedSuccessfully)
            {
                delivery.Decide(HttpStreamOutcome.Acknowledged, ticket.ExecCts, ct);
                // Publish the outcome before the endpoint returns HTTP EOF.
                try { await ticket.CompletionTask.WaitAsync(ct).ConfigureAwait(false); }
                catch (RouterRequestException) { }
            }
            var defaultShutdown = ticket.State is TicketState.Cancelled && ticket.DefaultStreamReadShutdown &&
                ctx.Response.HasStarted && ctx.Response.StatusCode == 200 && output?.CanAppendTerminal == true;
            if ((ticket.State is TicketState.Failed && !ticket.SuppressIdleTerminal) || defaultShutdown)
            {
                var json = JsonSerializer.Serialize(
                    new ErrorBody { Error = new ErrorDetail { Message = ticket.Error ?? "engine error", Code = ticket.ErrorCode ?? "engine_error" } },
                    AirServerJson.AirServerContext.Default.ErrorBody);
                await sink.WriteFrameAsync(AirJson.Frame(json, "[DONE]"), ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (delivery is not null)
        {
            // A failed writer can leave RequestAborted unset. Always end the sink's side of the
            // exchange, keeping any cause or acknowledgment that already won.
            delivery.Decide(HttpStreamOutcome.Cancelled);
            router?.TryCancel(ticket.Id);
            ctx.Abort();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away. HandleInference links the abort to the producer.
        }
        finally
        {
            delivery?.StopAndDiscard(ticket.StreamChannel?.Reader);
        }
    }

    private static ValueTask<bool> WaitForStreamDataAsync(ChannelReader<ReadOnlyMemory<byte>> reader,
        HttpStreamDelivery? delivery, CancellationToken ct)
    {
        if (delivery is null) return reader.WaitToReadAsync(ct);
        if (reader.TryPeek(out _)) return ValueTask.FromResult(true);
        if (delivery.Sealed.IsCompletedSuccessfully) return ValueTask.FromResult(false);
        return WaitForDataOrSealAsync(reader, delivery.Sealed, ct);
    }

    private static async ValueTask<bool> WaitForDataOrSealAsync(ChannelReader<ReadOnlyMemory<byte>> reader,
        Task seal, CancellationToken ct)
    {
        var data = reader.WaitToReadAsync(ct).AsTask();
        await Task.WhenAny(data, seal).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return data.IsCompleted ? await data.ConfigureAwait(false) : reader.TryPeek(out _);
    }

    private static async Task<(ChannelReader<ReadOnlyMemory<byte>>? Reader, ReadOnlyMemory<byte> First)> ReadFirstChunkAsync(
        InferenceStreamSink sink, RouterTicket ticket, TimeProvider clock, CancellationToken ct)
    {
        var lastStatus = await WriteKeepaliveAsync(sink, ticket, 0, "", ct).ConfigureAwait(false);
        var sequence = 1;
        using var stopHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? heartbeat = null;
        try
        {
            var ready = ticket.StreamReady;
            await WaitWithKeepalivesAsync(ready).ConfigureAwait(false);
            var reader = (await ready.WaitAsync(ct).ConfigureAwait(false))?.Reader;
            if (reader is null) return default;

            while (true)
            {
                // Keep this wait in flight across ticks. Its producer publishes the terminal
                // outcome before completing the channel, so buffered data still drains first.
                var readable = WaitForStreamDataAsync(reader, ticket.HttpDelivery, ct).AsTask();
                await WaitWithKeepalivesAsync(readable, reader).ConfigureAwait(false);
                if (!await readable.ConfigureAwait(false)) return default;

                while (reader.TryRead(out var chunk))
                {
                    if (!chunk.IsEmpty) return (reader, chunk);
                }
            }
        }
        finally
        {
            // The relay owns the timer. Disarm it before returning the first upstream bytes or
            // letting the caller write the terminal frame.
            if (heartbeat is { IsCompleted: false }) await stopHeartbeat.CancelAsync().ConfigureAwait(false);
        }

        async Task WaitWithKeepalivesAsync(Task progress, ChannelReader<ReadOnlyMemory<byte>>? reader = null)
        {
            while (!progress.IsCompleted && !ticket.IsTerminal)
            {
                // Retain the same deadline when StreamReady hands off to the first read.
                heartbeat ??= Task.Delay(StreamKeepaliveInterval, clock, stopHeartbeat.Token);
                await Task.WhenAny(progress, heartbeat).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (progress.IsCompleted || ticket.IsTerminal || reader?.TryPeek(out _) == true) return;

                lastStatus = await WriteKeepaliveAsync(sink, ticket, sequence++, lastStatus, ct).ConfigureAwait(false);
                heartbeat = null;
            }
        }
    }

    private static async Task<string> WriteKeepaliveAsync(InferenceStreamSink sink, RouterTicket ticket, int seq,
        string lastStatus, CancellationToken ct)
    {
        var status = ticket.State switch
        {
            TicketState.Queued => $"queued model={WireText.Ident(ticket.Model)}",
            TicketState.WaitingForModel => $"switching model={WireText.Ident(ticket.Model)} engine={WireText.Ident(ticket.Engine ?? "")}",
            _ => $"engine={WireText.Ident(ticket.Engine ?? "")}",
        };
        // Encode as a JSON string so a model name cannot introduce another SSE line.
        var statusField = string.Equals(status, lastStatus, StringComparison.Ordinal)
            ? ""
            : $" status={JsonSerializer.Serialize(status, AirServerJson.AirServerContext.Default.String)}";
        var frame = string.Create(CultureInfo.InvariantCulture, $": [router keepalive seq={seq}{statusField}]\n\n");
        await sink.WriteKeepaliveAsync(Encoding.UTF8.GetBytes(frame), ct).ConfigureAwait(false);
        return status;
    }

    /// <summary>Read a request body under the configured cap. Content-Length is a fast path, not the
    /// check: a caller that understates it is still cut off, because the copy counts bytes. Refusing with
    /// the error contract matters more than it looks — the alternative is Kestrel's own opaque failure,
    /// which is not a shape a caller can branch on.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext ctx, RouterCore router, CancellationToken ct)
    {
        var limit = router.MaxRequestBodyBytes;
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
        // The terminal paths in the router cap the sentences they build, and the identifiers inside a sentence
        // this assembly writes are cut before interpolation. This is the ceiling for the rest: text a caller put
        // into a URL segment or a header, and anything a future producer hands the writer unbounded. The body's
        // shape is the contract (§6.1); its size is the router's, never the caller's (§10).
        message = WireText.Bound(message, WireText.ReasonChars);
        ctx.Response.StatusCode = status;
        ctx.Response.Headers.ContentType = "application/json";
        var json = JsonSerializer.Serialize(
            new ErrorBody { Error = new ErrorDetail { Message = message, Code = code } },
            AirServerJson.AirServerContext.Default.ErrorBody);
        await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json), ctx.RequestAborted).ConfigureAwait(false);
    }

    private static int MapStatus(string code) => code switch
    {
        "invalid_profile" => 400,
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
            var generation = router.CurrentGeneration;
            foreach (var e in generation.Engines.Values)
            {
                var capability = e.Capability;
                foreach (var model in capability.ModelRoster)
                {
                    w.WriteStartObject();
                    w.WriteString("id", model);
                    w.WriteString("object", "model");
                    w.WriteNumber("created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    w.WriteString("owned_by", e.Name);
                    if (RouterCore.ObservedModelContextWindow(generation, e, capability, model) is { } window)
                        w.WriteNumber("context_window", window);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        ctx.Response.Headers.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.Body.WriteAsync(ms.ToArray().AsMemory(), ctx.RequestAborted).ConfigureAwait(false);
    }

    private static Task HandleCatalog(HttpContext ctx, RouterCore router, string instance)
    {
        var generation = router.CurrentGeneration;
        var tag = FormattableString.Invariant($"\"catalog-{instance}-{generation.Number}\"");
        ctx.Response.Headers.ETag = tag;
        ctx.Response.Headers.CacheControl = "no-cache";
        if (ctx.Request.GetTypedHeaders().IfNoneMatch?.Any(item => item.Tag.Value == tag || item.Tag.Value == "*") == true)
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }
        var settings = generation.Settings;
        var engines = settings.Engines.Values.OrderBy(engine => engine.Name, StringComparer.Ordinal)
            .Select(engine => new CatalogEngine(engine.Name, engine.Catalog.Values.OrderBy(model => model.Name, StringComparer.Ordinal)
                .Select(model => new CatalogModel(model.Name, model.Aliases,
                    model.Profile ?? (engine.Lifecycle == EngineLifecycleKind.Docker && model.ServingProfiles.Count == 1
                        ? model.ServingProfiles[0].Name : null), model.ServingProfiles)).ToArray())).ToArray();
        var catalog = new CatalogSnapshot(instance, generation.Number, settings.DefaultModel, engines);
        return AirJson.WriteAsync(ctx.Response, catalog, AirServerJson.AirServerContext.Default.CatalogSnapshot, ctx.RequestAborted);
    }

    private static async Task HandleStatus(HttpContext ctx, RouterCore router)
    {
        await AirJson.WriteAsync(ctx.Response, router.GetStats(includeRequests: ctx.Request.Query["requests"] == "1"),
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
        var body = await ReadBodyAsync(ctx, router, ctx.RequestAborted).ConfigureAwait(false);
        if (body is null) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var threadId = root.TryGetProperty("threadId", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(threadId))
            {
                await WriteErrorAsync(ctx, 400, "invalid_request", "threadId is required").ConfigureAwait(false);
                return;
            }
            var priority = router.DefaultPriority;
            if (root.TryGetProperty("priority", out var prio) && prio.ValueKind == JsonValueKind.Object)
            {
                var classStr = prio.TryGetProperty("class", out var c) ? c.GetString() : null;
                var sub = prio.TryGetProperty("sub", out var sub2) && sub2.ValueKind == JsonValueKind.Number
                    ? (int)sub2.GetDouble() : 0;
                if (classStr is null || !ThreadPriorityClass.TryParseClass(classStr, out var cls))
                {
                    await WriteErrorAsync(ctx, 400, "invalid_request", $"priority.class '{WireText.Ident(classStr ?? string.Empty)}' unknown")
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

    private static async Task HandleSwitch(HttpContext ctx, RouterCore router, AdminSwitchOperations operations)
    {
        var body = await ReadBodyAsync(ctx, router, ctx.RequestAborted).ConfigureAwait(false);
        if (body is null) return;
        var ownsAdminUse = false;
        AdminSwitchOperations.Operation? accepted = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var engineName = doc.RootElement.TryGetProperty("engine", out var e) ? e.GetString() : null;
            var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
            var profileName = doc.RootElement.TryGetProperty("profile", out var p) ? p.GetString() : null;
            if (engineName is null || model is null || router.AcceptEngineAdmin(engineName) is not { } engine)
            {
                await WriteErrorAsync(ctx, 400, "invalid_request", "engine and model are required (engine must exist)")
                    .ConfigureAwait(false);
                return;
            }
            ownsAdminUse = true;
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
            if (!operations.TryReserve(out accepted))
            {
                await WriteErrorAsync(ctx, 503, "too_many_switch_operations",
                    "The explicit switch operation limit is full. Retry later.").ConfigureAwait(false);
                return;
            }

            // The accepted record owns the admin use before the worker is submitted.
            operations.Schedule(accepted, router, engine, model, profile);
            ctx.Response.StatusCode = 202;
            var ack = new SwitchAck
            {
                OperationId = accepted.Id.ToString("N"),
                Engine = engineName,
                Model = model,
                Profile = profile?.Name,
                Switching = true,
            };
            await SendSwitchAckAsync(ctx, ack, operations.Clock).ConfigureAwait(false);
        }
        catch (JsonException) when (accepted is null)
        {
            await WriteErrorAsync(ctx, 400, "invalid_request", "body is not valid JSON").ConfigureAwait(false);
        }
        finally
        {
            if (accepted is not null) operations.CloseAck(accepted);
            else if (ownsAdminUse) router.ReleaseEngineAdmin();
        }
    }

    private static async Task SendSwitchAckAsync(HttpContext ctx, SwitchAck ack, TimeProvider clock)
    {
        var started = clock.GetTimestamp();
        var send = WriteSwitchAckAsync(ctx, ack);
        try
        {
            var remaining = TimeSpan.FromSeconds(30) - clock.GetElapsedTime(started);
            await send.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, clock, ctx.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Successful completion can win the race with the watchdog. Otherwise Abort owns
            // transport termination, including a suffix that suppresses RequestAborted.
            try
            {
                if (!send.IsCompletedSuccessfully) ctx.Abort();
            }
            finally
            {
                // The deadline initiates abort. The ACK half stays open until this same send retires.
                await send.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private static async Task WriteSwitchAckAsync(HttpContext ctx, SwitchAck ack)
    {
        ctx.RequestAborted.ThrowIfCancellationRequested();
        await AirJson.WriteAsync(ctx.Response, ack,
            AirServerJson.AirServerContext.Default.SwitchAck, ctx.RequestAborted).ConfigureAwait(false);
        await ctx.Response.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task HandleSwitchLookup(HttpContext ctx, AdminSwitchOperations operations, string operationId)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (operationId.Length != 32 || !Guid.TryParseExact(operationId, "N", out var id) ||
            operations.Lookup(id) is not { } snapshot)
        {
            await WriteErrorAsync(ctx, 404, "not_found", "Switch operation not found.").ConfigureAwait(false);
            return;
        }
        await AirJson.WriteAsync(ctx.Response, snapshot,
            AirServerJson.AirServerContext.Default.SwitchOperationSnapshot, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task HandleTicket(HttpContext ctx, RouterCore router, string id)
    {
        var snapshot = router.TryGetTicket(id);
        if (snapshot is null)
        {
            await WriteErrorAsync(ctx, 404, "not_found", $"ticket {WireText.Ident(id)} not found").ConfigureAwait(false);
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
