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
        foreach (var arg in args)
        {
            if (arg != "--commissioned" && !arg.StartsWith("--commissioned=", StringComparison.Ordinal)) continue;
            Console.Error.WriteLine("error: --commissioned must be handled by protected startup");
            return false;
        }
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

    // A protected bootstrap supplies the enrolled physical providers and certified issuers.
    // Both modes use the same server, endpoints, scheduler and queues.
    internal static WebApplication BuildCommissioned(string[] args,
        IReadOnlyList<HostManagerEnrollment> enrollments, string? configPath = null, RouterConfig? configOverride = null,
        TimeProvider? clock = null, TimeSpan? drainBudget = null)
    {
        if (enrollments.Count == 0 || enrollments.Any(static enrollment => enrollment.Issuer is not Lifecycle.ICommissionedServingAuthority))
            throw new RouterConfigException("Commissioned startup requires a certified physical serving authority for every enrollment.");
        return Build(args, configPath, configOverride, clock ?? TimeProvider.System,
            hostManager: new InProcessHostManager(enrollments, drainBudget));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Owned by the DI container: disposed (async) when the WebApplication is disposed in RunAsync.")]
    internal static WebApplication Build(string[] args, string? configPath, RouterConfig? configOverride,
        TimeProvider streamClock, Func<EngineDefinition, Lifecycle.IEngineLifecycleProvider>? lifecycleFactory = null,
        Func<EngineDefinition, HttpMessageHandler>? httpHandlerFactoryForTests = null, string? reloadTokenForTests = null,
        Func<string?>? readTargetResetOptInForTests = null, TimeProvider? adminSwitchClockForTests = null,
        OwnedPrivateServerComposition? ownedComposition = null, InProcessHostManager? hostManager = null,
        bool startDispatcher = true,
        ControlledDirectChat? directChat = null)
    {
        configPath = RouterConfigLoader.ResolveFilePath(configPath);
        var credential = configOverride is null && hostManager is null
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
        if (config.OwnedPrivatePolicy != (ownedComposition is not null))
            throw new RouterConfigException("Owned private policy requires a verified external composition.");
        ownedComposition?.Policy.ValidateTopology(config, lifecycleFactory is not null || targetResetInstance is not null || hostManager is not null);

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Services.AddSingleton(_ => new RouterCore(config, null, null, TimeProvider.System,
            lifecycleFactory, reload: loaded, nativeResetStartupInstance: targetResetInstance,
            hostManager: hostManager, ownedPolicy: ownedComposition?.Policy, directChat: directChat));
        builder.Services.AddSingleton(streamClock);
        builder.Services.AddSingleton(new AdminSwitchOperations(adminSwitchClockForTests ?? TimeProvider.System));

        var app = builder.Build();
        if (ownedComposition is not null)
            app.Use((ctx, next) => AuthorizeOwnedRequestAsync(ctx, next, ownedComposition));
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
        if (ownedComposition is not null)
            app.MapPost("/admin/policy/sensitivity", (HttpContext ctx, RouterCore r) => HandleOwnedSensitivityAsync(ctx, r));
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

        if (startDispatcher) app.Services.GetRequiredService<RouterCore>().Start();
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
        var owned = ReadOwnedRequest(ctx);
        if (router.OwnedPolicyEnabled && owned is null)
        {
            await WriteErrorAsync(ctx, 403, "verified_scope_required", "a verified owned inference scope is required").ConfigureAwait(false);
            return;
        }

        // Priority and identity defects are decided before consuming the body (§6).
        ThreadPriority? declaredPriority;
        string? threadId;
        try
        {
            declaredPriority = PriorityFromHeader(ctx);
            threadId = InferenceThreadFromHeader(ctx);
            if (owned is not null && threadId is { Length: > 96 })
                throw new RouterRequestException("invalid_request", "the owned thread name exceeds its limit");
        }
        catch (RouterRequestException ex)
        {
            await WriteErrorAsync(ctx, 400, ex.Code, ex.Message).ConfigureAwait(false);
            return;
        }

        var read = await ReadBodyAsync(ctx, router, ct).ConfigureAwait(false);
        if (read is null) return;              // the refusal is already on the wire
        var payload = read;

        // Minimal probe of the raw body (model, stream) — full passthrough otherwise.
        string? requestedModel = null;
        bool stream = false;
        PrivateDestinationCapability? requiredCapability = null;
        try
        {
            using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { AllowDuplicateProperties = owned is null });
            if (owned is not null)
                requiredCapability = RouterCore.CheckOwnedWorkload(doc.RootElement,
                    !ctx.Request.Path.Value!.TrimEnd('/').Equals("/v1/completions", StringComparison.OrdinalIgnoreCase));
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
        catch (RouterRequestException ex)
        {
            await WriteErrorAsync(ctx, MapStatus(ex.Code), ex.Code, ex.Message).ConfigureAwait(false);
            return;
        }

        var affinity = ctx.Request.Headers["X-Inference-Affinity"].ToString() is { Length: > 0 } a ? a : null;

        RouterTicket ticket;
        try
        {
            var request = new InferenceRequest
            {
                Model = requestedModel,
                IsLegacyCompletionHttp = ctx.Request.Path.Value!.TrimEnd('/').Equals("/v1/completions", StringComparison.OrdinalIgnoreCase),
                Stream = stream,
                Body = payload,
                Options = new RouterRequestOptions
                {
                    Priority = declaredPriority,
                    CacheAffinityHash = owned is null ? affinity : null,
                    Profile = ProfileFromHeader(ctx),
                    RequiredContext = RequiredContextFromHeader(ctx),
                },
            };
            if (owned is null) ticket = await router.EnqueueHttpAsync(threadId, request, ct).ConfigureAwait(false);
            else
            {
                var group = owned.Group ?? await router.ResolveOwnedPolicyAsync(owned.Scope, owned.RequestedGroup,
                    OwnedInferencePermission.Inference, mayCreate: true, ct).ConfigureAwait(false);
                ctx.Response.Headers[PolicyGroupHeader] = group.Group;
                ticket = await router.EnqueueOwnedHttpAsync(threadId, request, new OwnedTicketPolicy(group, owned.Scope,
                    requiredCapability!.Value), ct).ConfigureAwait(false);
            }
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
            var (core, admitted) = ((RouterCore, RouterTicket))state!;
            core.CancelTicket(admitted);
        }, (router, ticket));

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

    private static string? InferenceThreadFromHeader(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("X-Inference-Thread", out var header)) return null;
        if (header.Count != 1 || header[0] is not { } name || string.IsNullOrWhiteSpace(name) ||
            name.Contains(',', StringComparison.Ordinal) || name.Any(char.IsControl))
            throw new RouterRequestException("invalid_request", "X-Inference-Thread requires one non-blank thread name without commas or control characters.");
        return name;
    }

    /// <summary>X-Inference-Priority: the request's own priority as `class[:sub]` (§6). Absent → the
    /// configured default, which is the only way a caller asks for nothing. A value that is present but
    /// outside the grammar is refused with 400 `invalid_priority` rather than dropped: a caller that asked
    /// for `interactive` and was served at the default has no way to notice, and the queue would already
    /// have been mutated for a priority nobody asked for. A repeated header arrives as one joined string and
    /// fails the same way, because two values for one request is not a choice the router gets to make.
    /// The refusal happens while the headers are still being read, before the body is read and before the
    /// ticket exists (§6.1).</summary>
    private static ThreadPriority? PriorityFromHeader(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("X-Inference-Priority", out var values)) return null;
        var declared = values.ToString();
        if (ThreadPriority.TryParse(declared, out var priority)) return priority;
        throw new RouterRequestException("invalid_priority", FormattableString.Invariant(
            $"X-Inference-Priority '{WireText.Ident(declared)}' is not one of background, low, normal, high, interactive with an optional :sub of 0-{ThreadPriority.MaxSubPriority}."));
    }

    private static string? ProfileFromHeader(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("X-Inference-Profile", out var values)) return null;
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
        try
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            if (HttpProtocol.IsHttp11(ctx.Request.Protocol))
                ctx.Response.Headers.Connection = "keep-alive";

            var writer = ctx.Response.BodyWriter;
            var output = ticket.HttpDelivery is null && ticket.IsHttpStream ? new DefaultStreamOutput() : null;
            var sink = new InferenceStreamSink(writer, ticket, router, clock, output);
            ct.ThrowIfCancellationRequested();
            var (reader, first) = await ReadFirstChunkAsync(sink, ticket, clock, ct).ConfigureAwait(false);
            // Commissioned selection installs delivery before publishing its stream reader.
            var delivery = ticket.HttpDelivery;
            if (delivery is not null && ticket.HttpOutputEnded) throw new OperationCanceledException(ct);
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
                delivery.AcknowledgeFlush(ticket.ExecCts, ct);
                // Publish the outcome before the endpoint returns HTTP EOF.
                try { await ticket.CompletionTask.WaitAsync(ct).ConfigureAwait(false); }
                catch (RouterRequestException) { }
            }
            var defaultShutdown = delivery is null && ticket.State is TicketState.Cancelled && ticket.DefaultStreamReadShutdown &&
                ctx.Response.HasStarted && ctx.Response.StatusCode == 200 && output?.CanAppendTerminal == true;
            if ((ticket.State is TicketState.Failed && !ticket.SuppressTerminalFrame) || defaultShutdown)
            {
                var json = JsonSerializer.Serialize(
                    new ErrorBody { Error = new ErrorDetail { Message = ticket.Error ?? "engine error", Code = ticket.ErrorCode ?? "engine_error" } },
                    AirServerJson.AirServerContext.Default.ErrorBody);
                await sink.WriteFrameAsync(AirJson.Frame(json, "[DONE]"), ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (ticket.HttpDelivery is { } delivery)
        {
            // A failed writer can leave RequestAborted unset. Always end the sink's side of the
            // exchange, keeping any cause or acknowledgment that already won.
            // Capture and cancel the admitted execution before a fallback outcome can publish
            // logical completion and make an indexed cancellation ineligible.
            router?.CancelTicket(ticket);
            delivery.Decide(HttpStreamOutcome.Cancelled);
            ctx.Abort();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away. HandleInference links the abort to the producer.
        }
        finally
        {
            ticket.HttpDelivery?.StopAndDiscard(ticket.StreamChannel?.Reader);
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
            if (ticket.HttpOutputEnded && ticket.HttpDelivery is not null) throw new OperationCanceledException(ct);
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
        "invalid_profile" or "invalid_priority" or "commercial_request_unsupported" or "commercial_provider_bad_request" => 400,
        "policy_revision_conflict" or "policy_transition_pending" => 409,
        "policy_journal_full" or "policy_journal_quarantined" or "commercial_rate_limited" => 429,
        "verified_scope_required" or "policy_group_unavailable" or "policy_route_denied" or
            "policy_capability_unavailable" or "policy_restriction_invalid" => 403,
        "owned_thread_limit" => 429,
        "queue_overflow" => 429,
        "deadline_exceeded" => 408,
        "cancelled" => 499,
        "switch_failed" or "model_unavailable" or "commercial_transport_unavailable" or "commercial_provider_unavailable" => 503,
        "commercial_identity_timeout" or "commercial_stream_idle_timeout" or "commercial_stream_deadline" => 504,
        "commercial_identity_invalid" or "commercial_exchange_failed" => 502,
        "engine_error" or "engine_unreachable" => 502,
        "engine_timeout" => 504,
        _ => 500,
    };

    // ── admin endpoints ─────────────────────────────────────────────────────

    private static async Task HandleModels(HttpContext ctx, RouterCore router)
    {
        var owned = ReadOwnedRequest(ctx);
        if (router.OwnedPolicyEnabled && owned?.Group is null)
        {
            await OwnedScopeError(ctx).ConfigureAwait(false);
            return;
        }
        using var ms = new MemoryStream(1024);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("object", "list");
            w.WriteStartArray("data");
            var generation = router.CurrentGeneration;
            foreach (var e in generation.Engines.Values)
            {
                if (owned?.Group is { } group)
                {
                    if (!router.CanSeeOwnedEngine(group, owned.Scope, e.Name)) continue;
                    foreach (var model in e.Definition.Catalog.Values.OrderBy(static model => model.Name, StringComparer.Ordinal).Take(128))
                        WriteModel(WireText.Ident(model.Name), e.Name, created: 0, window: null);
                    continue;
                }
                var capability = e.Capability;
                foreach (var model in capability.ModelRoster)
                    WriteModel(model, e.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        RouterCore.ObservedModelContextWindow(generation, e, capability, model));
            }
            w.WriteEndArray();
            w.WriteEndObject();

            void WriteModel(string name, string engine, long created, long? window)
            {
                w.WriteStartObject();
                w.WriteString("id", name);
                w.WriteString("object", "model");
                w.WriteNumber("created", created);
                w.WriteString("owned_by", engine);
                if (window is { } context) w.WriteNumber("context_window", context);
                w.WriteEndObject();
            }
        }
        ctx.Response.Headers.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-store";
        await ctx.Response.Body.WriteAsync(ms.ToArray().AsMemory(), ctx.RequestAborted).ConfigureAwait(false);
    }

    private static Task HandleCatalog(HttpContext ctx, RouterCore router, string instance)
    {
        var owned = ReadOwnedRequest(ctx);
        if (router.OwnedPolicyEnabled && owned?.Group is null) return OwnedScopeError(ctx);
        var generation = router.CurrentGeneration;
        var tag = FormattableString.Invariant($"\"catalog-{instance}-{generation.Number}\"");
        if (owned is null)
        {
            ctx.Response.Headers.ETag = tag;
            ctx.Response.Headers.CacheControl = "no-cache";
            if (ctx.Request.GetTypedHeaders().IfNoneMatch?.Any(item => item.Tag.Value == tag || item.Tag.Value == "*") == true)
            {
                ctx.Response.StatusCode = StatusCodes.Status304NotModified;
                return Task.CompletedTask;
            }
        }
        var settings = generation.Settings;
        var engines = settings.Engines.Values.Where(engine => owned?.Group is not { } group ||
                router.CanSeeOwnedEngine(group, owned.Scope, engine.Name)).OrderBy(engine => engine.Name, StringComparer.Ordinal)
            .Select(engine => new CatalogEngine(engine.Name, engine.Catalog.Values.OrderBy(model => model.Name, StringComparer.Ordinal)
                .Select(model => new CatalogModel(model.Name, model.Aliases,
                    model.Profile ?? (engine.Lifecycle == EngineLifecycleKind.Docker && model.ServingProfiles.Count == 1
                        ? model.ServingProfiles[0].Name : null), model.ServingProfiles)).ToArray())).ToArray();
        var defaultModel = owned is null || engines.Any(engine => engine.Models.Any(model =>
            model.Id == settings.DefaultModel || model.Aliases.Contains(settings.DefaultModel, StringComparer.OrdinalIgnoreCase)))
            ? settings.DefaultModel : string.Empty;
        var catalog = new CatalogSnapshot(instance, generation.Number, defaultModel, engines);
        return AirJson.WriteAsync(ctx.Response, catalog, AirServerJson.AirServerContext.Default.CatalogSnapshot, ctx.RequestAborted);
    }

    private static async Task HandleStatus(HttpContext ctx, RouterCore router)
    {
        var owned = ReadOwnedRequest(ctx);
        if (router.OwnedPolicyEnabled && owned?.Group is null)
        {
            await OwnedScopeError(ctx).ConfigureAwait(false);
            return;
        }
        if (owned?.Group is { } group)
            await AirJson.WriteAsync(ctx.Response, router.GetOwnedStats(group, owned.Scope),
                AirServerJson.AirServerContext.Default.ScopedRouterStats, ctx.RequestAborted).ConfigureAwait(false);
        else
            await AirJson.WriteAsync(ctx.Response, router.GetStats(),
                AirServerJson.AirServerContext.Default.RouterStats, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static Task HandleEvents(HttpContext ctx, RouterCore router, string? ticketOverride = null)
    {
        var owned = ReadOwnedRequest(ctx);
        if (router.OwnedPolicyEnabled && owned?.Group is null) return OwnedScopeError(ctx);
        var filter = BuildEventFilter(ctx, ticketOverride);
        var key = owned?.Group?.Key;
        if (key is { } scope && filter.TicketId is { } ticket && router.TryGetTicket(ticket, scope) is null &&
            !router.HasOwnedEvent(scope, ticket)) return WriteErrorAsync(ctx, 404, "not_found", "ticket not found");
        return WriteEventStreamAsync(ctx, router, filter, key);
    }

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

    private static async Task WriteEventStreamAsync(HttpContext ctx, RouterCore router, RouterEventFilter filter, PolicyGroupKey? ownedGroup)
    {
        // A feed is a subscription the client keeps open, so a full ring is refused with a plain 503
        // before any SSE headers go out. A 200 that ends at once is indistinguishable from a filter
        // that matches nothing, and an exception after the headers are flushed looks to the client
        // like a truncated success. The ring's own refusal is the backstop for a race past this check.
        if (router.EventFeed.LiveSubscribers >= router.EventFeed.MaxLiveSubscribers)
        {
            await WriteErrorAsync(ctx, 503, "too_many_subscribers",
                "the event feed is at its subscriber limit; close a feed and reconnect with the last Seq you saw")
                .ConfigureAwait(false);
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = ownedGroup is null ? "no-cache" : "no-store";
        var writer = ctx.Response.BodyWriter;
        // Headers out before the first event: a filtered stream that matches nothing (an unknown
        // ticket, a quiet engine) must still answer 200 and hold the stream open, not appear to hang.
        await writer.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
        try
        {
            var events = ownedGroup is { } key ? router.SubscribeOwnedEvents(filter, key, ctx.RequestAborted)
                : router.SubscribeEvents(filter, ctx.RequestAborted);
            await foreach (var e in events.ConfigureAwait(false))
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
        var owned = ReadOwnedRequest(ctx);
        if (router.OwnedPolicyEnabled && owned?.Group is null)
        {
            await OwnedScopeError(ctx).ConfigureAwait(false);
            return;
        }
        var body = await ReadBodyAsync(ctx, router, ctx.RequestAborted).ConfigureAwait(false);
        if (body is null) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await WriteErrorAsync(ctx, 400, "invalid_request", "body must be a JSON object").ConfigureAwait(false);
                return;
            }
            if (!TryReadAdminString(root, "threadId", out var threadId) || string.IsNullOrEmpty(threadId))
            {
                await WriteErrorAsync(ctx, 400, "invalid_request", "threadId is required").ConfigureAwait(false);
                return;
            }
            var priority = router.DefaultPriority;
            if (root.TryGetProperty("priority", out var prio))
            {
                // A supplied priority is read exactly or refused: this surface admits what the
                // `X-Inference-Priority` header admits, and nothing else (§6). An absent `sub` is the whole
                // class at 0; a present one that this cannot read is the caller's defect, not something to
                // cast and clamp into a value the caller could not have written on the request path.
                if (prio.ValueKind != JsonValueKind.Object)
                {
                    await WriteErrorAsync(ctx, 400, "invalid_request",
                        "priority must be an object or absent").ConfigureAwait(false);
                    return;
                }
                // A `class` that is not a JSON string is unreadable rather than something to call GetString on:
                // JsonElement.GetString throws for any other kind, and an exception here answers 500 to a caller
                // whose body the route can read well enough to refuse (§6.1).
                var classStr = prio.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null;
                if (classStr is null || !ThreadPriorityClass.TryParseClass(classStr, out var cls))
                {
                    await WriteErrorAsync(ctx, 400, "invalid_request", $"priority.class '{WireText.Ident(classStr ?? string.Empty)}' unknown")
                        .ConfigureAwait(false);
                    return;
                }
                byte sub = 0;
                if (prio.TryGetProperty("sub", out var subElement) && !TryReadSubPriority(subElement, out sub))
                {
                    await WriteErrorAsync(ctx, 400, "invalid_request",
                        $"priority.sub '{WireText.Ident(subElement.GetRawText())}' is not an integer from 0 to {ThreadPriority.MaxSubPriority}")
                        .ConfigureAwait(false);
                    return;
                }
                priority = new ThreadPriority(cls, sub);
            }
            if (!TryReadAdminString(root, "modelAffinity", out var modelAffinity))
            {
                await WriteErrorAsync(ctx, 400, "invalid_request", "modelAffinity must be a string or null")
                    .ConfigureAwait(false);
                return;
            }
            var options = new RouterThreadOptions { ModelAffinity = modelAffinity };
            if (root.TryGetProperty("weight", out var wt) && wt.ValueKind == JsonValueKind.Number)
                options = options with { Weight = wt.GetDouble() };

            if (owned?.Group is { } group)
            {
                if (threadId is not { Length: <= 96 } || string.IsNullOrWhiteSpace(threadId) || threadId.Any(char.IsControl))
                    throw new RouterRequestException("invalid_request", "the owned thread name is invalid");
                router.RegisterOwnedThread(group, threadId, priority, options);
            }
            else router.RegisterThread(threadId!, priority, options);
            ctx.Response.StatusCode = 204;
        }
        catch (JsonException)
        {
            await WriteErrorAsync(ctx, 400, "invalid_request", "body is not valid JSON").ConfigureAwait(false);
        }
        catch (RouterRequestException error)
        {
            await WriteErrorAsync(ctx, error.Code == "invalid_request" ? 400 : MapStatus(error.Code), error.Code, error.Message)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The sub-priority as the admin JSON has to write it: a JSON number whose own text is plain
    /// decimal digits within <see cref="ThreadPriority.MaxSubPriority"/>. The text is the check rather than the
    /// parsed double, so a fractional (<c>255.7</c>), signed (<c>-5</c>) or exponent (<c>1e10</c>) form is
    /// refused instead of arriving at a byte through a cast and a clamp — spellings the header grammar does
    /// not admit and <c>ThreadPriority.ToString</c> can never echo back (§6).</summary>
    private static bool TryReadSubPriority(JsonElement element, out byte sub)
    {
        sub = 0;
        return element.ValueKind == JsonValueKind.Number
            && byte.TryParse(element.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out sub);
    }

    // The callers have validated the object root. Preserve explicit null as an omitted string.
    private static bool TryReadAdminString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return true;
    }

    private static async Task HandleSwitch(HttpContext ctx, RouterCore router, AdminSwitchOperations operations)
    {
        if (router.OwnedPolicyEnabled)
        {
            await WriteErrorAsync(ctx, 403, "policy_capability_unavailable", "the owned private composition provides forward-only engines")
                .ConfigureAwait(false);
            return;
        }
        var body = await ReadBodyAsync(ctx, router, ctx.RequestAborted).ConfigureAwait(false);
        if (body is null) return;
        var ownsAdminUse = false;
        AdminSwitchOperations.Operation? accepted = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await WriteErrorAsync(ctx, 400, "invalid_request", "body must be a JSON object").ConfigureAwait(false);
                return;
            }
            if (!TryReadAdminString(root, "profile", out var profileName))
            {
                await WriteErrorAsync(ctx, 400, "invalid_request", "profile must be a string or null")
                    .ConfigureAwait(false);
                return;
            }
            if (!TryReadAdminString(root, "engine", out var engineName) ||
                !TryReadAdminString(root, "model", out var model) || engineName is null || model is null ||
                router.AcceptEngineAdmin(engineName) is not { } engine)
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
        var key = ReadOwnedRequest(ctx)?.Group?.Key;
        if (router.OwnedPolicyEnabled && key is null)
        {
            await OwnedScopeError(ctx).ConfigureAwait(false);
            return;
        }
        var snapshot = router.TryGetTicket(id, key);
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
        var key = ReadOwnedRequest(ctx)?.Group?.Key;
        if (router.OwnedPolicyEnabled && key is null) return OwnedScopeError(ctx);
        var ok = router.TryCancel(id, key);
        ctx.Response.StatusCode = ok ? 204 : 404;
        return Task.CompletedTask;
    }
}
