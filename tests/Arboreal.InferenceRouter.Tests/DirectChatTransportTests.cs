using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arboreal.InferenceRouter.Tests;

public sealed class DirectChatTransportTests
{
    private const string Chat = "/v1/chat/completions";
    private const string Standard = """{"model":"actual","choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":0,"completion_tokens":12}}""";
    private const string Started = "data: {\"model\":\"actual\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"allowed\"}}]}\r\n\r\n";
    private const string Meta = "data: {\"model\":\"actual\",\"choices\":[],\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":12}}\n\n";
    private const string Done = "data: [DONE]\r\n\r\n";

    [Fact]
    public void WireFormatsAcceptTheSameDefinition()
    {
        var origin = new Uri("http://127.0.0.1:14141");
        var toml = RouterConfigLoader.Load(Declaration(origin));
        var json = RouterConfigLoader.Load(JsonDeclaration(origin), isJson: true);
        var left = toml.CommercialProviders["fixture"];
        var right = json.CommercialProviders["fixture"];
        Assert.Equal(left.ChatUri, right.ChatUri);
        Assert.Equal(left.Models["public"].ServedId, right.Models["public"].ServedId);
        Assert.Equal(left.CredentialReference, right.CredentialReference);
        Assert.Equal(left.MaximumAttemptMicroUsd, right.MaximumAttemptMicroUsd);
    }

    [Theory]
    [InlineData("unknown_switch = true")]
    [InlineData("api_key = \"inline\"")]
    [InlineData("chat_path = \"//elsewhere/path\"")]
    [InlineData("protocol = \"different\"")]
    [InlineData("origin = \"http://example.invalid\"")]
    [InlineData("output_price_microusd_per_million = -1")]
    [InlineData("maximum_attempt_microusd = 99999999999999999999999")]
    [InlineData("identity_buffer_bytes = 1048577")]
    public void InvalidProviderDeclarationsFailClosed(string invalid)
    {
        var origin = new Uri("http://127.0.0.1:14141");
        var requested = Declaration(origin);
        var key = invalid[..invalid.IndexOf(' ', StringComparison.Ordinal)];
        var insertion = requested.IndexOf("[commercial_provider.fixture.model.", StringComparison.Ordinal);
        if (requested.IndexOf(key + " =", StringComparison.Ordinal) is >= 0 and var start)
            requested = requested[..start] + invalid + requested[requested.IndexOf('\n', start)..];
        else requested = requested.Insert(insertion, invalid + "\n");
        Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(requested));
    }

    [Fact(Timeout = 20_000)]
    public async Task DisabledProvidersHaveNoDiscoveryCredentialsOrInferenceAndActivatedStockRefuses()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        var capture = new Capture(Standard);
        await using var upstream = await capture.StartAsync(ct);
        var origin = new Uri(upstream.Urls.Single());
        var calls = 0;
        var fixture = new ControlledDirectChat((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult<ControlledChatCredential?>(new(1, "fixture-only"));
        }, (binding, _) => ValueTask.FromResult<ControlledChatPermit?>(new(binding)));
        var enabled = RouterConfigLoader.Load(Declaration(origin));
        Assert.Throws<RouterConfigException>(() => new RouterCore(enabled));
        var mixed = RouterConfigLoader.Load(Declaration(origin) + """

            [engine.legacy]
            kind = "openai-compatible"
            endpoint = "http://127.0.0.1:1"
            remote = true
            """);
        Assert.Throws<RouterConfigException>(() => new RouterCore(mixed, null, null, TimeProvider.System, directChat: fixture));
        var disabled = RouterConfigLoader.Load(Declaration(origin, enabled: false));
        await using var app = await RouterAsync(disabled, fixture, ct);
        using var transport = DirectHandler();
        using var client = ClientAt(app, transport);
        using var models = await client.GetAsync("/v1/models", ct);
        Assert.Equal("{\"object\":\"list\",\"data\":[]}", await models.Content.ReadAsStringAsync(ct));
        using var discovery = await client.GetAsync("/admin/catalog", ct);
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        using var refused = await SendAsync(client, Chat, Request("public"), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        using var tokenize = await SendAsync(client, "/v1/tokenize", Request("public"), ct);
        Assert.Equal(HttpStatusCode.NotFound, tokenize.StatusCode);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Empty(capture.Requests);
    }

    [Fact(Timeout = 25_000)]
    public async Task OneRouterRelaysTwoConfiguredIdentitiesAndSendsOnlyProjectedHeaders()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var first = new Capture(Standard);
        var second = new Capture(Started + Meta + Done, stream: true);
        await using var firstApp = await first.StartAsync(ct);
        await using var secondApp = await second.StartAsync(ct);
        var text = Declaration(new(firstApp.Urls.Single())) + Declaration(new(secondApp.Urls.Single()),
            name: "another", model: "second", root: false);
        var sent = new ConcurrentQueue<DirectChatBinding>();
        var authorization = new ControlledDirectChat((_, _) => ValueTask.FromResult<ControlledChatCredential?>(new(2, "fixture-only")),
            (binding, _) => { sent.Enqueue(binding); return ValueTask.FromResult<ControlledChatPermit?>(new(binding)); });
        await using var app = await RouterAsync(RouterConfigLoader.Load(text), authorization, ct);
        using var handler = DirectHandler();
        using var client = ClientAt(app, handler);
        using var buffered = await SendAsync(client, Chat, Request("public"), ct);
        Assert.Equal(HttpStatusCode.OK, buffered.StatusCode);
        Assert.Equal(Standard, await buffered.Content.ReadAsStringAsync(ct));
        using var stream = await SendAsync(client, Chat, Request("second", stream: true), ct);
        Assert.Equal(Started + Meta + Done, StripRouterComments(await stream.Content.ReadAsStringAsync(ct)));
        var bufferedTicket = await TicketAsync(client, buffered, ct);
        Assert.Equal(0, Metric(bufferedTicket, "Prompt").GetProperty("Value").GetInt64());
        var streamedTicket = await TicketAsync(client, stream, ct);
        Assert.Equal(12, Metric(streamedTicket, "Completion").GetProperty("Value").GetInt64());
        Assert.Equal("matched", streamedTicket.GetProperty("CommercialEvidence").GetProperty("ModelIdentity").GetString());
        Assert.True(streamedTicket.GetProperty("CommercialMayHaveSent").GetBoolean());
        Assert.Equal(["fixture", "another"], sent.Select(binding => binding.Provider.Name));
        Assert.All(sent, binding => { Assert.Equal(2, binding.CredentialEpoch); Assert.Equal(64, binding.BodySha256.Length); });
        foreach (var call in first.Requests.Concat(second.Requests))
        {
            Assert.Equal(Chat, call.Path);
            Assert.Equal("Bearer fixture-only", call.Authorization);
            Assert.Equal("application/json", call.ContentType);
            Assert.Equal(string.Empty, call.PrivateHeader);
            Assert.DoesNotContain("sensitive-thread", call.Body, StringComparison.Ordinal);
            using var body = JsonDocument.Parse(call.Body);
            Assert.Equal("requested", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("allow_fallback").GetBoolean());
        }
        Assert.Single(first.Requests);
        Assert.Single(second.Requests);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("wrong")]
    [InlineData("missing")]
    [InlineData("late_conflict")]
    [InlineData("error_after_done")]
    public async Task StrictStreamingRefusesUnboundOutputAndPreservesOnlyTheAllowedPrefix(string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        const string wrong = "data: {\"model\":\"different\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"blocked\"}}]}\n\n";
        var body = mode switch
        {
            "wrong" => wrong + Done,
            "missing" => wrong.Replace("\"model\":\"different\",", string.Empty, StringComparison.Ordinal) + Done,
            "late_conflict" => Started + wrong + Done,
            _ => Started + Done + "data: {\"error\":{\"message\":\"sensitive upstream detail\"}}\n\n",
        };
        var capture = new Capture(body, stream: true);
        await using var upstream = await capture.StartAsync(ct);
        await using var app = await RouterAsync(RouterConfigLoader.Load(Declaration(new(upstream.Urls.Single()))), PermitAll(), ct);
        using var handler = DirectHandler();
        using var client = ClientAt(app, handler);
        using var result = await SendAsync(client, Chat, Request("public", stream: true), ct);
        var delivered = await result.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("blocked", delivered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive upstream detail", delivered, StringComparison.Ordinal);
        Assert.Equal(mode is "late_conflict" or "error_after_done", delivered.Contains("allowed", StringComparison.Ordinal));
        Assert.Contains(mode == "error_after_done" ? "commercial_exchange_failed" : "commercial_identity_invalid", delivered, StringComparison.Ordinal);
        Assert.Single(capture.Requests);
        var ticket = await TicketAsync(client, result, ct);
        Assert.True(ticket.GetProperty("CommercialMayHaveSent").GetBoolean());
        Assert.Equal("Failed", ticket.GetProperty("State").GetString());
    }

    [Theory(Timeout = 20_000)]
    [InlineData("revoked")]
    [InlineData("altered")]
    [InlineData("spent")]
    [InlineData("adapter_failure")]
    [InlineData("fallback")]
    [InlineData("legacy")]
    [InlineData("duplicate")]
    public async Task AuthorizationAndProjectionRefusalsProduceZeroUpstreamRequests(string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        var capture = new Capture(Standard);
        await using var upstream = await capture.StartAsync(ct);
        ControlledChatPermit? issued = null;
        var permit = new ControlledDirectChat((_, _) => mode == "adapter_failure"
            ? throw new IOException("fixture credential must stay private")
            : ValueTask.FromResult<ControlledChatCredential?>(new(1, "fixture-only")),
            (binding, _) =>
            {
                issued = new(mode == "altered" ? binding with { CredentialEpoch = 2 } : binding);
                if (mode == "spent") Assert.True(issued.TryConsume(binding));
                return ValueTask.FromResult<ControlledChatPermit?>(issued);
            });
        if (mode == "revoked") permit.BeforeFirstWriteForTests = _ => issued!.Revoke();
        await using var app = await RouterAsync(RouterConfigLoader.Load(Declaration(new(upstream.Urls.Single()))), permit, ct);
        using var handler = DirectHandler();
        using var client = ClientAt(app, handler);
        var body = Request("public");
        if (mode == "fallback") body = body.Insert(1, "\"allow_fallback\":true,");
        if (mode == "duplicate") body = body.Insert(1, "\"model\":\"another\",");
        using var refused = await SendAsync(client, mode == "legacy" ? "/v1/completions" : Chat, body, ct);
        Assert.Equal(mode is "revoked" or "altered" or "spent" or "adapter_failure" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(capture.Requests);
        Assert.Equal(mode == "spent", issued?.Consumed ?? false);
        Assert.DoesNotContain("fixture credential", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        var ticket = await TicketAsync(client, refused, ct);
        Assert.False(ticket.TryGetProperty("CommercialMayHaveSent", out _));
    }

    [Fact(Timeout = 20_000)]
    public async Task RevocationAtFirstWriteAllowsNoApplicationBytesOnAnAcceptedSocket()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var accepting = listener.AcceptTcpClientAsync(ct);
        ControlledChatPermit? issued = null;
        var actor = new ControlledDirectChat((_, _) => ValueTask.FromResult<ControlledChatCredential?>(new(1, "fixture-only")),
            (binding, _) => ValueTask.FromResult<ControlledChatPermit?>(issued = new(binding)))
        {
            BeforeFirstWriteForTests = _ => issued!.Revoke(),
        };
        var origin = new UriBuilder("http", "127.0.0.1", endpoint.Port).Uri;
        await using var app = await RouterAsync(RouterConfigLoader.Load(Declaration(origin)), actor, ct);
        using var handler = DirectHandler();
        using var client = ClientAt(app, handler);
        var request = SendAsync(client, Chat, Request("public"), ct);
        using var socket = await accepting;
        using var stream = socket.GetStream();
        var scratch = new byte[1024];
        Assert.Equal(0, await stream.ReadAsync(scratch, ct));
        using var refused = await request;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.False(issued!.Consumed);
    }

    [Fact(Timeout = 20_000)]
    public async Task KeepaliveAndModelWithoutSemanticOutputDoNotExtendTheAbsoluteDeadline()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new Capture(string.Empty, stream: true)
        {
            Write = async context =>
            {
                await context.Response.WriteAsync(": upstream only\n\ndata: {\"model\":\"actual\",\"choices\":[]}\n\n", context.RequestAborted);
                await finish.Task.WaitAsync(context.RequestAborted);
            },
        };
        await using var upstream = await capture.StartAsync(ct);
        var declaration = Declaration(new(upstream.Urls.Single())).Replace("allows_streaming = true",
            "allows_streaming = true\nfirst_output_ms = 500", StringComparison.Ordinal);
        await using var app = await RouterAsync(RouterConfigLoader.Load(declaration), PermitAll(), ct);
        var clock = new ReadinessDeadlineTests.ManualClock();
        var core = app.Services.GetRequiredService<RouterCore>();
        core.ResponseTimeoutClockForTests = clock;
        using var handler = DirectHandler();
        using var client = ClientAt(app, handler);
        var result = SendAsync(client, Chat, Request("public", stream: true), ct);
        try
        {
            while (!core.Events.Replay(0).Any(static row => row.Kind == "request.streaming_first_token"))
                await Task.Delay(TimeSpan.FromMilliseconds(2), ct);
            Assert.Equal(1, clock.ActiveTimers);
            clock.AdvanceTo(499);
            Assert.False(result.IsCompleted);
            clock.AdvanceTo(500);
            using var refused = await result;
            var body = await refused.Content.ReadAsStringAsync(ct);
            Assert.DoesNotContain("upstream only", body, StringComparison.Ordinal);
            Assert.Contains("commercial_identity_timeout", body, StringComparison.Ordinal);
            var ticket = await TicketAsync(client, refused, ct);
            Assert.True(ticket.GetProperty("CommercialMayHaveSent").GetBoolean());
            Assert.Equal(JsonValueKind.Null, ticket.GetProperty("CommercialEvidence").GetProperty("FirstSemanticMilliseconds").ValueKind);
            Assert.Single(capture.Requests);
        }
        finally { finish.TrySetResult(); }
    }

    [Theory]
    [InlineData("0", "available", 0L)]
    [InlineData("81", "available", 81L)]
    [InlineData("9223372036854775808", "invalid", null)]
    [InlineData("-4", "invalid", null)]
    [InlineData("", "missing", null)]
    public void ScalarMappingsDistinguishZeroMissingAndInvalid(string value, string state, long? expected)
    {
        using var response = new HttpResponseMessage();
        if (value.Length != 0) response.Headers.TryAddWithoutValidation("X-Prompt-Tokens", value);
        var mapping = DirectChatResponseMapping.Parse(new()
        {
            Prompt = new() { Source = "header", Name = "X-Prompt-Tokens", Unit = "tokens" },
            Completion = new() { Source = "json", Name = "usage.output", Unit = "tokens" },
            RetryAfter = new() { Source = "json", Name = "wait.seconds", Unit = "seconds" },
        });
        var metrics = new DirectChatMetrics(mapping, response.Headers, TimeProvider.System);
        using var json = JsonDocument.Parse("""{"usage":{"output":2},"wait":{"seconds":9223372036854775807}}""");
        metrics.Observe(json.RootElement);
        var observed = metrics.Evidence();
        Assert.Equal(new DirectChatMetric(state, expected), observed.Prompt);
        Assert.Equal(new DirectChatMetric("available", 2), observed.Completion);
        Assert.Equal("invalid", observed.RetryAfterMilliseconds.State);
    }

    [Fact(Timeout = 20_000)]
    public async Task AuthenticatedReloadValidatesButDoesNotActivateAProviderChange()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = deadline.Token;
        var capture = new Capture(Standard);
        await using var upstream = await capture.StartAsync(ct);
        var directory = Directory.CreateTempSubdirectory("air-config-direct-");
        try
        {
            var path = Path.Combine(directory.FullName, "router.toml");
            var origin = new Uri(upstream.Urls.Single());
            await File.WriteAllTextAsync(path, Declaration(origin, enabled: false), ct);
            await using var app = ServerApp.Build(["--urls=http://127.0.0.1:0"], path, null, TimeProvider.System,
                reloadTokenForTests: "fixture-reload");
            await app.StartAsync(ct);
            using var handler = DirectHandler();
            using var client = ClientAt(app, handler);
            using var observe = new HttpRequestMessage(HttpMethod.Get, "/admin/config/reload");
            observe.Headers.Authorization = new("Bearer", "fixture-reload");
            using var generation = await client.SendAsync(observe, ct);
            Assert.Equal(HttpStatusCode.OK, generation.StatusCode);
            await File.WriteAllTextAsync(path, Declaration(origin), ct);
            using var apply = new HttpRequestMessage(HttpMethod.Post, "/admin/config/reload");
            apply.Headers.Authorization = new("Bearer", "fixture-reload");
            apply.Headers.IfMatch.Add(generation.Headers.ETag!);
            using var refusal = await client.SendAsync(apply, ct);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refusal.StatusCode);
            Assert.Contains("reload_requires_restart", await refusal.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            using var inference = await SendAsync(client, Chat, Request("public"), ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, inference.StatusCode);
            Assert.Empty(capture.Requests);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static JsonElement Metric(JsonElement ticket, string name) => ticket.GetProperty("CommercialEvidence").GetProperty(name);

    private static async Task<JsonElement> TicketAsync(HttpClient client, HttpResponseMessage response, CancellationToken ct)
    {
        using var lookup = await client.GetAsync("/admin/tickets/" + response.Headers.GetValues("X-Inference-Ticket").Single(), ct);
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        using var document = JsonDocument.Parse(await lookup.Content.ReadAsByteArrayAsync(ct));
        return document.RootElement.Clone();
    }

    private static string StripRouterComments(string body)
    {
        while (body.StartsWith(": [router keepalive ", StringComparison.Ordinal)) body = body[(body.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
        return body;
    }

    private static ControlledDirectChat PermitAll() => new((_, _) => ValueTask.FromResult<ControlledChatCredential?>(new(1, "fixture-only")),
        (binding, _) => ValueTask.FromResult<ControlledChatPermit?>(new(binding)));

    private static async Task<WebApplication> RouterAsync(RouterConfig config, ControlledDirectChat permit, CancellationToken ct)
    {
        var app = ServerApp.Build(["--urls=http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning"], null, config, TimeProvider.System,
            directChat: permit);
        try { await app.StartAsync(ct); return app; }
        catch { await app.DisposeAsync(); throw; }
    }

    private static HttpClientHandler DirectHandler() => new() { UseProxy = false, AllowAutoRedirect = false };
    private static HttpClient ClientAt(WebApplication app, HttpClientHandler handler) => new(handler, disposeHandler: false)
        { BaseAddress = new Uri(app.Urls.Single()) };

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Inference-Thread", "sensitive-thread");
        request.Headers.Add("X-Inference-Affinity", "sensitive-affinity");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request, ct);
    }

    private static string Request(string model, bool stream = false) => FormattableString.Invariant(
        $$"""{"model":"{{model}}","stream":{{(stream ? "true" : "false")}},"messages":[{"role":"user","content":"fixture"}],"reasoning_effort":"low"}""");

    private static string Declaration(Uri origin, bool enabled = true, string name = "fixture", string model = "public", bool root = true)
    {
        var header = root ? "default_model = \"public\"\n" : "\n";
        return header + FormattableString.Invariant($$"""
            [commercial_provider.{{name}}]
            enabled = {{(enabled ? "true" : "false")}}
            protocol = "openai-chat-completions"
            origin = "{{origin.GetLeftPart(UriPartial.Authority)}}"
            chat_path = "/v1/chat/completions"
            account = "example"
            credential_ref = "test-reference"
            allows_streaming = true
            allows_reasoning = true
            backup_parameter = "allow_fallback"
            input_price_microusd_per_million = 0
            output_price_microusd_per_million = 15
            maximum_attempt_microusd = 10
            [commercial_provider.{{name}}.model.{{model}}]
            request_id = "requested"
            served_id = "actual"
            equivalence = "fixture-revision"
            reasoning_efforts = ["low"]
            """) + "\n";
    }

    private static string JsonDeclaration(Uri origin) => """
        {"commercial_provider":{"fixture":{"protocol":"openai-chat-completions","enabled":true,
        "origin":"@ORIGIN@","chat_path":"/v1/chat/completions","account":"example","credential_ref":"test-reference",
        "allows_streaming":true,"allows_reasoning":true,"backup_parameter":"allow_fallback",
        "input_price_microusd_per_million":0,"output_price_microusd_per_million":15,"maximum_attempt_microusd":10,
        "model":{"public":{"request_id":"requested","served_id":"actual","equivalence":"fixture-revision","reasoning_efforts":["low"]}}}}
        }
        """.Replace("@ORIGIN@", origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);

    private sealed class Capture(string text, bool stream = false)
    {
        internal ConcurrentQueue<Captured> Requests { get; } = new();
        internal Func<HttpContext, Task>? Write { get; init; }
        internal async Task<WebApplication> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Run(Receive);
            try { await app.StartAsync(ct); return app; }
            catch { await app.DisposeAsync(); throw; }
        }

        private async Task Receive(HttpContext context)
        {
            var request = context.Request;
            using var reader = new StreamReader(request.Body);
            Requests.Enqueue(new(request.Path.Value ?? string.Empty, request.Headers.Authorization.ToString(), request.ContentType ?? string.Empty,
                request.Headers["X-Inference-Thread"].ToString() + request.Headers["X-Inference-Affinity"].ToString(),
                await reader.ReadToEndAsync(context.RequestAborted)));
            context.Response.ContentType = stream ? "text/event-stream" : "application/json";
            if (Write is { } write) await write(context);
            else await context.Response.WriteAsync(text, context.RequestAborted);
        }
    }

    private sealed record Captured(string Path, string Authorization, string ContentType, string PrivateHeader, string Body);
}
