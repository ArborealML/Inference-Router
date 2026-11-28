using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arboreal.InferenceRouter.Tests;

[CollectionDefinition("Owned private HTTP", DisableParallelization = true)]
public sealed class OwnedPrivateHttpCollection;

[Collection("Owned private HTTP")]
public sealed partial class OwnedPrivatePolicyTests : IDisposable
{
    private const string Key = "fixture-private-upstream-key";
    private const string Model = "fixture-model";
    private const string GroupHeader = "X-Inference-Policy-Group";
    private const string Chat = """{"model":"alias","messages":[{"role":"user","content":"fixture prompt"}]}""";
    private const string Text = """{"model":"alias","prompt":"fixture text"}""";
    private const string Stream = """{"model":"alias","messages":[{"role":"user","content":"fixture streaming"}],"stream":true}""";
    private const OwnedInferencePermission FullScope = OwnedInferencePermission.Inference | OwnedInferencePermission.Read |
        OwnedInferencePermission.ControlThread | OwnedInferencePermission.Cancel;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("air-private-policy-");

    [Fact(Timeout = 35_000)]
    public async Task RealHttpPersistsBeforeDispatchAndKeepsGroupsAndStatelessCallsSeparate()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = time.Token;
        var capture = new Capture();
        await using var upstream = capture.Build();
        await upstream.StartAsync(ct);
        var head = new Authority();
        using var store = await OwnedPolicyStore.OpenAsync(StatePath, head, initialize: true, ct);
        var verifier = new Verifier();
        verifier.Scopes["alpha-new"] = new("alpha", FullScope, newRootIntent: "root");
        verifier.Scopes["beta-new"] = new("beta", FullScope, newRootIntent: "root");
        verifier.Scopes["admin"] = new("operator", OwnedInferencePermission.Administer);
        var endpoint = EngineUri(upstream);
        await using var app = BuildRouter(store, verifier, endpoint);
        await app.StartAsync(ct);
        using var direct = DirectHandler();
        using var client = ClientAt(app, direct);

        head.HoldNextCommit();
        var doing = Send(client, HttpMethod.Post, "/v1/chat/completions", "alpha-new", null, "same", Chat, ct);
        try
        {
            await head.Entered.Task.WaitAsync(ct);
            Assert.Empty(capture.Inferences);
            Assert.False(doing.IsCompleted);
        }
        finally { head.AllowCommit.TrySetResult(); }
        using var alpha = await doing;
        Assert.Equal(HttpStatusCode.OK, alpha.StatusCode);
        Assert.Equal("fixture response", await alpha.Content.ReadAsStringAsync(ct));
        var alphaGroup = alpha.Headers.GetValues(GroupHeader).Single();
        var alphaTicket = alpha.Headers.GetValues("X-Inference-Ticket").Single();
        var alphaRevision = head.Current!.Revision;
        verifier.Scopes["alpha-existing"] = new("alpha", FullScope, existingGroup: alphaGroup);

        using var retry = await Send(client, HttpMethod.Post, "/v1/chat/completions", "alpha-new", null, "same", Chat, ct);
        Assert.Equal(alphaGroup, retry.Headers.GetValues(GroupHeader).Single());
        Assert.Equal(alphaRevision, head.Current.Revision);
        using var beta = await Send(client, HttpMethod.Post, "/v1/completions", "beta-new", null, "same", Text, ct);
        Assert.Equal(HttpStatusCode.OK, beta.StatusCode);
        var betaGroup = beta.Headers.GetValues(GroupHeader).Single();
        var betaTicket = beta.Headers.GetValues("X-Inference-Ticket").Single();
        Assert.NotEqual(alphaGroup, betaGroup);
        verifier.Scopes["beta-existing"] = new("beta", FullScope, existingGroup: betaGroup);

        verifier.Scopes["gamma-new"] = new("gamma", FullScope, newRootIntent: "root", allow: []);
        head.HoldNextCommit();
        var waitingWrite = Send(client, HttpMethod.Post, "/v1/chat/completions", "gamma-new", null, null, Chat, ct);
        try
        {
            await head.Entered.Task.WaitAsync(ct);
            using var stateless = await Send(client, HttpMethod.Post, "/v1/chat/completions", "alpha-existing", alphaGroup,
                null, Stream, ct);
            Assert.Equal(HttpStatusCode.OK, stateless.StatusCode);
            Assert.Contains("data: [DONE]", await stateless.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            Assert.False(waitingWrite.IsCompleted);
        }
        finally { head.AllowCommit.TrySetResult(); }
        using var emptyAllow = await waitingWrite;
        Assert.Equal(HttpStatusCode.Forbidden, emptyAllow.StatusCode);
        verifier.Scopes["deny-new"] = new("delta", FullScope, newRootIntent: "root", allow: ["private"], deny: ["private"]);
        using var deny = await Send(client, HttpMethod.Post, "/v1/chat/completions", "deny-new", null, null, Chat, ct);
        Assert.Equal(HttpStatusCode.Forbidden, deny.StatusCode);
        var deniedGroup = deny.Headers.GetValues(GroupHeader).Single();
        verifier.Scopes["deny-existing"] = new("delta", FullScope, existingGroup: deniedGroup, allow: ["private"]);
        using var cannotEraseDenial = await Send(client, HttpMethod.Post, "/v1/chat/completions", "deny-existing", deniedGroup, null, Chat, ct);
        Assert.Equal(HttpStatusCode.Forbidden, cannotEraseDenial.StatusCode);
        var beforeUnknown = head.Current!.Revision;
        verifier.Scopes["unknown-restriction"] = new("epsilon", FullScope, newRootIntent: "root", allow: ["unknown"]);
        using var unknownRestriction = await Send(client, HttpMethod.Post, "/v1/chat/completions", "unknown-restriction", null, null, Chat, ct);
        Assert.Equal(HttpStatusCode.Forbidden, unknownRestriction.StatusCode);
        Assert.Equal(beforeUnknown, head.Current.Revision);
        var calls = capture.Inferences.ToArray();
        Assert.Equal(["/v1/chat/completions", "/v1/chat/completions", "/v1/completions", "/v1/chat/completions"],
            calls.Select(static call => call.Path));
        Assert.Equal(64, calls[0].Hint.Length);
        Assert.Equal(calls[0].Hint, calls[1].Hint);
        Assert.NotEqual(calls[0].Hint, calls[2].Hint);
        Assert.Empty(calls[3].Hint);
        Assert.All(calls, call => Assert.Equal("Bearer " + Key, call.Authority));
        var savedPolicy = await File.ReadAllTextAsync(StatePath, ct);
        Assert.DoesNotContain("fixture prompt", savedPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, savedPolicy, StringComparison.Ordinal);

        using var ownTicket = await Send(client, HttpMethod.Get, "/admin/tickets/" + alphaTicket, "alpha-existing", alphaGroup, null, null, ct);
        Assert.Equal(HttpStatusCode.OK, ownTicket.StatusCode);
        var own = await ownTicket.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"Sensitive\":false", own, StringComparison.Ordinal);
        Assert.Contains("\"DestinationTrust\":\"private\"", own, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, own, StringComparison.Ordinal);
        foreach (var (method, suffix) in new[] { (HttpMethod.Get, ""), (HttpMethod.Delete, ""), (HttpMethod.Get, "/events") })
        {
            using var foreign = await Send(client, method, "/admin/tickets/" + alphaTicket + suffix, "beta-existing", betaGroup, null, null, ct);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }
        using var noGroup = await Send(client, HttpMethod.Post, "/v1/chat/completions", "alpha-existing", null, "renamed", Chat, ct);
        Assert.Equal(HttpStatusCode.Forbidden, noGroup.StatusCode);
        using var forged = await Send(client, HttpMethod.Post, "/v1/chat/completions", "alpha-existing", betaGroup, "renamed", Chat, ct);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        using var noScope = await Send(client, HttpMethod.Post, "/v1/chat/completions", "Bearer-only", alphaGroup, "same", Chat, ct);
        Assert.Equal(HttpStatusCode.Forbidden, noScope.StatusCode);
        using var incapable = await Send(client, HttpMethod.Post, "/v1/chat/completions", "alpha-existing", alphaGroup, "same",
            """{"messages":[{"role":"user","content":[]}],"tools":[]}""", ct);
        Assert.Equal(HttpStatusCode.Forbidden, incapable.StatusCode);
        Assert.Equal(4, capture.Inferences.Count);

        using var status = await Send(client, HttpMethod.Get, "/admin/status", "beta-existing", betaGroup, null, null, ct);
        using var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct));
        var row = Assert.Single(statusJson.RootElement.GetProperty("Threads").EnumerateArray());
        Assert.Equal("same", row.GetProperty("ThreadId").GetString());
        Assert.Equal(1L, row.GetProperty("Enqueued").GetInt64());
        Assert.DoesNotContain(alphaGroup, statusJson.RootElement.GetRawText(), StringComparison.Ordinal);

        using var catalog = await Send(client, HttpMethod.Get, "/admin/catalog", "beta-existing", betaGroup, null, null, ct,
            request => request.Headers.TryAddWithoutValidation("If-None-Match", "*"));
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Null(catalog.Headers.ETag);
        Assert.True(catalog.Headers.CacheControl?.NoStore);

        using var replay = await Send(client, HttpMethod.Get, "/admin/events?since=0", "beta-existing", betaGroup, null, null, ct,
            completion: HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using (var stream = await replay.Content.ReadAsStreamAsync(ct))
        using (var lines = new StreamReader(stream))
        {
            bool sawEnd = false;
            while (!sawEnd && await lines.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                Assert.DoesNotContain(alphaTicket, line, StringComparison.Ordinal);
                using var eventJson = JsonDocument.Parse(line[6..]);
                var item = eventJson.RootElement;
                if (item.GetProperty("TicketId").ValueKind == JsonValueKind.String)
                    Assert.Equal(betaTicket, item.GetProperty("TicketId").GetString());
                sawEnd = item.GetProperty("Kind").GetString() == "request.completed";
            }
            Assert.True(sawEnd);
        }

        using var admin = await Send(client, HttpMethod.Post, "/admin/switch", "admin", null, null, "{}", ct);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        var core = app.Services.GetRequiredService<RouterCore>();
        var evicted = "retained-only-in-event-ring";
        core.EventFeed.Append(new RouterEvent { Kind = "request.completed", TicketId = evicted, ThreadId = "same",
            OwnerGroup = new("alpha", alphaGroup) });
        using var foreignReplay = await Send(client, HttpMethod.Get, "/admin/tickets/" + evicted + "/events", "beta-existing",
            betaGroup, null, null, ct, completion: HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.NotFound, foreignReplay.StatusCode);
        using var ownedReplay = await Send(client, HttpMethod.Get, "/admin/tickets/" + evicted + "/events", "alpha-existing",
            alphaGroup, null, null, ct, completion: HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, ownedReplay.StatusCode);
        await using (var retained = await ownedReplay.Content.ReadAsStreamAsync(ct))
        using (var retainedLines = new StreamReader(retained))
            Assert.Contains(evicted, await retainedLines.ReadLineAsync(ct), StringComparison.Ordinal);
        Assert.Null(core.TryGetTicket(alphaTicket));
        Assert.False(core.TryCancel(alphaTicket));
        Assert.Throws<RouterRequestException>(core.GetStats);
        Assert.Throws<RouterRequestException>(() => core.Events);

        capture.HoldNextInference();
        var active = Send(client, HttpMethod.Post, "/v1/chat/completions", "alpha-existing", alphaGroup, "active", Chat, ct);
        try
        {
            await capture.InferenceEntered.Task.WaitAsync(ct);
            var id = core.EventFeed.Replay(0).Single(e => e.Kind == "request.enqueued" && e.ThreadId == "active").TicketId;
            using var foreignCancel = await Send(client, HttpMethod.Delete, "/admin/tickets/" + id, "beta-existing", betaGroup, null, null, ct);
            Assert.Equal(HttpStatusCode.NotFound, foreignCancel.StatusCode);
            Assert.False(active.IsCompleted);
            using var allowedCancel = await Send(client, HttpMethod.Delete, "/admin/tickets/" + id, "alpha-existing", alphaGroup, null, null, ct);
            Assert.Equal(HttpStatusCode.NoContent, allowedCancel.StatusCode);
            using var canceled = await active;
            Assert.Equal((HttpStatusCode)499, canceled.StatusCode);
        }
        finally { capture.AllowInference.TrySetResult(); }
    }

    [Fact(Timeout = 35_000)]
    public async Task QueuedOwnedHttpDiagnosticsStayWithTheCurrentOwnerAndAllowedRoute()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;
        var allowed = new Capture();
        var denied = new Capture();
        await using var allowedServer = allowed.Build();
        await using var deniedServer = denied.Build();
        await allowedServer.StartAsync(ct);
        await deniedServer.StartAsync(ct);
        var authority = new Authority();
        using var store = await OwnedPolicyStore.OpenAsync(StatePath, authority, initialize: true, ct);
        var verifier = new Verifier();
        verifier.Scopes["alpha"] = new("alpha", FullScope, newRootIntent: "root", deny: ["restricted"]);
        verifier.Scopes["beta"] = new("beta", FullScope, newRootIntent: "root", allow: ["restricted"]);
        var endpoint = EngineUri(allowedServer);
        var restrictedEndpoint = EngineUri(deniedServer);
        var config = Config(endpoint);
        config.Engines = new Dictionary<string, EngineDefinition>(config.Engines, StringComparer.Ordinal)
        {
            ["restricted"] = new() { Name = "restricted", Endpoint = restrictedEndpoint, Catalog = config.Engines["private"].Catalog },
        };
        await using var app = ServerApp.BuildOwnedPrivate(["--urls=http://127.0.0.1:0"], new OwnedPrivateServerComposition(store,
            [new("private", endpoint, "/models", "/props", PrivateDestinationCapability.Chat),
             new("restricted", restrictedEndpoint, "/models", "/props", PrivateDestinationCapability.Chat)], verifier.Verify),
            configOverride: config);
        await app.StartAsync(ct);
        using var direct = DirectHandler();
        using var client = ClientAt(app, direct);
        const string thread = "same-thread";
        const string inference = "/v1/chat/completions";
        using var beta = await Send(client, HttpMethod.Post, inference, "beta", null, thread, Chat, ct);
        Assert.Equal(HttpStatusCode.OK, beta.StatusCode);
        Assert.Single(denied.Inferences);

        allowed.HoldNextInference();
        var incumbent = Send(client, HttpMethod.Post, inference, "alpha", null, thread, Chat, ct);
        var router = app.Services.GetRequiredService<RouterCore>();
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await allowed.InferenceEntered.Task.WaitAsync(ct);
            var pending = Send(client, HttpMethod.Post, inference, "alpha", null, thread, Chat, ct);
            using var feed = await Send(client, HttpMethod.Get, "/admin/events?kinds=request.waiting", "alpha", null, null, null, ct,
                completion: HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
            await using var stream = await feed.Content.ReadAsStreamAsync(ct);
            using var lines = new StreamReader(stream);
            string? line;
            do { line = await lines.ReadLineAsync(ct); }
            while (line is not null && !line.StartsWith("data: ", StringComparison.Ordinal));
            using var waiting = JsonDocument.Parse(Assert.IsType<string>(line)[6..]);
            var item = waiting.RootElement;
            Assert.Equal("request.waiting", item.GetProperty("Kind").GetString());
            Assert.Equal(thread, item.GetProperty("ThreadId").GetString());
            var report = item.GetProperty("QueueDispatch");
            Assert.Equal("thread_concurrency_limit", report.GetProperty("Reason").GetString());
            Assert.Equal(thread, report.GetProperty("Scope").GetString());
            var route = Assert.Single(report.GetProperty("Alternatives").EnumerateArray());
            Assert.Equal("private", route.GetProperty("Engine").GetString());
            Assert.Equal("not_evaluated", route.GetProperty("State").GetString());
            var ticketPath = "/admin/tickets/" + item.GetProperty("TicketId").GetString();
            using var ticket = await Send(client, HttpMethod.Get, ticketPath, "alpha", null, null, null, ct);
            Assert.Equal(HttpStatusCode.OK, ticket.StatusCode);
            using var snapshot = JsonDocument.Parse(await ticket.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(thread, snapshot.RootElement.GetProperty("ThreadId").GetString());
            Assert.Equal(report.GetRawText(), snapshot.RootElement.GetProperty("QueueDispatch").GetRawText());
            using var foreignTicket = await Send(client, HttpMethod.Get, ticketPath, "beta", null, null, null, ct);
            Assert.Equal(HttpStatusCode.NotFound, foreignTicket.StatusCode);
            using var foreignFeed = await Send(client, HttpMethod.Get, ticketPath + "/events", "beta", null, null, null, ct,
                completion: HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.NotFound, foreignFeed.StatusCode);
            using var cancel = await Send(client, HttpMethod.Delete, ticketPath, "alpha", null, null, null, ct);
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
            using var canceled = await pending;
            Assert.Equal((HttpStatusCode)499, canceled.StatusCode);
            Assert.False(incumbent.IsCompleted);

            var beforePublication = new TaskCompletionSource<RouterTicket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var afterRound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            router.BeforeDispatchWaitPublicationForTests = candidate =>
            {
                if (beforePublication.TrySetResult(candidate)) releasePublication.Task.WaitAsync(ct).GetAwaiter().GetResult();
            };
            router.BeforeDispatchParkForTests = armed =>
            {
                if (!armed && beforePublication.Task.IsCompletedSuccessfully) afterRound.TrySetResult();
            };
            var invalidated = Send(client, HttpMethod.Post, inference, "alpha", null, thread, Chat, ct);
            var stale = await beforePublication.Task.WaitAsync(ct);
            var group = Assert.IsType<OwnedTicketPolicy>(stale.OwnedPolicy).Group;
            Assert.True(store.IsCurrent(group));
            authority.ThrowOnCommit = true;
            Assert.Null(await store.ResolveAsync(new("lost-head", FullScope, newRootIntent: "root"), null, true, ct));
            Assert.False(store.IsCurrent(group));
            releasePublication.TrySetResult();
            await afterRound.Task.WaitAsync(ct);
            Assert.Null(stale.DispatchWait);
            Assert.DoesNotContain(router.EventFeed.Replay(0), evt => evt.TicketId == stale.Id && evt.Kind == "request.waiting");
            allowed.AllowInference.TrySetResult();
            using var refused = await invalidated;
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("policy_group_unavailable", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            Assert.Single(allowed.Inferences);
            Assert.Single(denied.Inferences);
        }
        finally
        {
            releasePublication.TrySetResult();
            allowed.AllowInference.TrySetResult();
            router.BeforeDispatchWaitPublicationForTests = null;
            router.BeforeDispatchParkForTests = null;
        }
        using var completed = await incumbent;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task BuiltInTransportDoesNotFollowPrivateUpstreamRedirects(int status)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = time.Token;
        var escape = new Capture();
        await using var escapedServer = escape.Build();
        await escapedServer.StartAsync(ct);
        var capture = new Capture { Redirect = (status, escapedServer.Urls.Single() + "/escaped") };
        await using var upstream = capture.Build();
        await upstream.StartAsync(ct);
        using var store = await OwnedPolicyStore.OpenAsync(StatePath, new Authority(), initialize: true, ct);
        var verifier = new Verifier();
        verifier.Scopes["new"] = new("alpha", FullScope, newRootIntent: "root");
        await using var app = BuildRouter(store, verifier, EngineUri(upstream));
        await app.StartAsync(ct);
        using var direct = DirectHandler();
        using var client = ClientAt(app, direct);
        using var refused = await Send(client, HttpMethod.Post, "/v1/chat/completions", "new", null, null, Chat, ct);
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Single(capture.Inferences);
        Assert.Equal(0, escape.Requests);
        Assert.DoesNotContain("fixture upstream detail", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        await using var legacy = ServerApp.Build(["--urls=http://127.0.0.1:0"], configOverride: Config(EngineUri(upstream), owned: false));
        await legacy.StartAsync(ct);
        using var legacyClient = ClientAt(legacy, direct);
        using var exercised = await Send(legacyClient, HttpMethod.Post, "/v1/chat/completions", "unused", null, null, Chat, ct);
        Assert.Equal(2, capture.Inferences.Count);
        Assert.True(escape.Requests > 0);
    }

    [Fact(Timeout = 30_000)]
    public async Task BuiltInPrivateTransportIgnoresProcessProxyAndRequiresIndependentEnrollment()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = time.Token;
        var proxy = new Capture();
        await using var proxyApp = proxy.Build();
        await proxyApp.StartAsync(ct);
        var capture = new Capture();
        await using var upstream = capture.Build();
        await upstream.StartAsync(ct);
        using var store = await OwnedPolicyStore.OpenAsync(StatePath, new Authority(), initialize: true, ct);
        var verifier = new Verifier();
        verifier.Scopes["new"] = new("alpha", FullScope, newRootIntent: "root");
        var endpoint = EngineUri(upstream);
        var prior = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy(proxyApp.Urls.Single(), BypassOnLocal: false);
        try
        {
            var config = Config(endpoint);
            Assert.Throws<RouterConfigException>(() => ServerApp.Build([], configOverride: config));
            var unverified = new OwnedPrivateServerComposition(store, [], verifier.Verify);
            Assert.Throws<RouterConfigException>(() => ServerApp.BuildOwnedPrivate([], unverified, configOverride: config));
            var wrongProbe = new OwnedPrivateServerComposition(store, [new("private", endpoint, "/models", "/unverified",
                PrivateDestinationCapability.Chat)], verifier.Verify);
            Assert.Throws<RouterConfigException>(() => ServerApp.BuildOwnedPrivate([], wrongProbe, configOverride: config));
            Assert.Equal(0, capture.Requests);
            await using var app = BuildRouter(store, verifier, endpoint);
            await app.StartAsync(ct);
            using var direct = DirectHandler();
            using var client = ClientAt(app, direct);
            using var accepted = await Send(client, HttpMethod.Post, "/v1/chat/completions", "new", null, null, Chat, ct);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Single(capture.Inferences);
            Assert.Equal(0, proxy.Requests);
        }
        finally { HttpClient.DefaultProxy = prior; }
    }

    [Fact(Timeout = 30_000)]
    public async Task ReloadNeedsIndependentPrivilegeAndRefusesModeRemovalOrUnknownEgress()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = time.Token;
        var privateCapture = new Capture();
        await using var upstream = privateCapture.Build();
        await upstream.StartAsync(ct);
        var unclassified = new Capture();
        await using var rejectedUpstream = unclassified.Build();
        await rejectedUpstream.StartAsync(ct);
        var endpoint = EngineUri(upstream);
        var configPath = Path.Combine(_directory.FullName, "router.toml");
        var original = $$"""
            owned_private_policy = true
            default_model = "{{Model}}"
            auto_switch = false
            [engine.private]
            kind = "openai-compatible"
            lifecycle_owner = "external"
            endpoint = "{{endpoint}}"
            [engine.private.catalog."{{Model}}"]
            aliases = ["alias"]
            """;
        await File.WriteAllTextAsync(configPath, original, ct);
        using var store = await OwnedPolicyStore.OpenAsync(StatePath, new Authority(), initialize: true, ct);
        var verifier = new Verifier();
        verifier.Scopes["admin"] = new("operator", OwnedInferencePermission.Administer);
        var composition = new OwnedPrivateServerComposition(store, [new("private", endpoint, "/models", "/props",
            PrivateDestinationCapability.Chat)], verifier.Verify);
        const string reload = "/admin/config/reload";
        const string bearer = "Bearer fixture-reload";
        await using var app = ServerApp.Build(["--urls=http://127.0.0.1:0"], configPath, configOverride: null, TimeProvider.System,
            reloadTokenForTests: "fixture-reload", readTargetResetOptInForTests: () => null, ownedComposition: composition);
        await app.StartAsync(ct);
        using var direct = DirectHandler();
        using var client = ClientAt(app, direct);
        using var bearerOnly = await Send(client, HttpMethod.Get, reload, "unused", null, null, null, ct,
            request => request.Headers.TryAddWithoutValidation("Authorization", bearer));
        Assert.Equal(HttpStatusCode.Forbidden, bearerOnly.StatusCode);
        using var authorized = await Send(client, HttpMethod.Get, reload, "admin", null, null, null, ct,
            request => request.Headers.TryAddWithoutValidation("Authorization", bearer));
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        var tag = authorized.Headers.ETag!.ToString();

        await File.WriteAllTextAsync(configPath, original.Replace("owned_private_policy = true", "owned_private_policy = false",
            StringComparison.Ordinal), ct);
        using var downgrade = await Send(client, HttpMethod.Post, reload, "admin", null, null, null, ct, SetReloadHeaders);
        Assert.Equal((HttpStatusCode)422, downgrade.StatusCode);
        Assert.Contains("reload_requires_restart", await downgrade.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        var addition = $$"""

            [engine.unknown]
            kind = "openai-compatible"
            lifecycle_owner = "external"
            endpoint = "{{EngineUri(rejectedUpstream)}}"
            """;
        await File.WriteAllTextAsync(configPath, original + "\n" + addition, ct);
        using var unknown = await Send(client, HttpMethod.Post, reload, "admin", null, null, null, ct, SetReloadHeaders);
        Assert.Equal((HttpStatusCode)422, unknown.StatusCode);
        Assert.Contains("reload_invalid_config", await unknown.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(0, unclassified.Requests);
        Assert.True(app.Services.GetRequiredService<RouterCore>().CurrentGeneration.Settings.OwnedPrivatePolicy);

        void SetReloadHeaders(HttpRequestMessage request)
        {
            request.Headers.TryAddWithoutValidation("Authorization", bearer);
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task ReopenVerifiesFreshnessAndNeverReinitializesExistingIntent()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = time.Token;
        var authority = new Authority();
        var root = new VerifiedInferenceScope("alpha", FullScope, newRootIntent: "root", deny: ["restricted"]);
        OwnedPolicyGroup first;
        byte[] prior;
        using (var store = await OwnedPolicyStore.OpenAsync(StatePath, authority, initialize: true, ct))
        {
            var roots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.ResolveAsync(root, null, true, ct).AsTask()));
            first = roots[0]!;
            Assert.All(roots, item => Assert.Same(first, item));
            Assert.Equal(2L, authority.Current!.Revision);
            prior = await File.ReadAllBytesAsync(StatePath, ct);
            await Assert.ThrowsAnyAsync<IOException>(() => OwnedPolicyStore.OpenAsync(StatePath, authority, cancellationToken: ct));
        }

        // Install independently confirmed restored policy. This fixture does not exercise a policy mutation endpoint.
        var snapshot = JsonSerializer.Deserialize(prior, OwnedPolicyJsonContext.Default.Snapshot)!;
        var sensitive = first with { Sensitive = true, Revision = first.Revision + 1 };
        var current = snapshot with { Groups = [sensitive], Revision = sensitive.Revision };
        var currentBytes = JsonSerializer.SerializeToUtf8Bytes(current, OwnedPolicyJsonContext.Default.Snapshot);
        await File.WriteAllBytesAsync(StatePath, currentBytes, ct);
        authority.Current = new(current.Store, current.Revision, Convert.ToHexStringLower(SHA256.HashData(currentBytes)));

        using (var reopened = await OwnedPolicyStore.OpenAsync(StatePath, authority, cancellationToken: ct))
        {
            var repeated = await reopened.ResolveAsync(root, null, true, ct);
            Assert.NotNull(repeated);
            Assert.Equal(first.Group, repeated.Group);
            Assert.True(repeated.Sensitive);
            Assert.Equal(["restricted"], repeated.Deny);
            Assert.Null(await reopened.ResolveAsync(new("beta", FullScope, existingGroup: first.Group), first.Group, false, ct));
        }

        await File.WriteAllBytesAsync(StatePath, prior, ct);
        await Assert.ThrowsAsync<IOException>(() => OwnedPolicyStore.OpenAsync(StatePath, authority, cancellationToken: ct));
        await File.WriteAllTextAsync(StatePath, "{corrupt", ct);
        await Assert.ThrowsAsync<IOException>(() => OwnedPolicyStore.OpenAsync(StatePath, authority, cancellationToken: ct));
        File.Delete(StatePath);
        await Assert.ThrowsAnyAsync<IOException>(() => OwnedPolicyStore.OpenAsync(StatePath, authority, cancellationToken: ct));
    }

    [Fact(Timeout = 20_000)]
    public async Task FilePublicationWithoutAuthorityConfirmationCannotGrantAnyAdmission()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = time.Token;
        var head = new Authority();
        using (var store = await OwnedPolicyStore.OpenAsync(StatePath, head, initialize: true, ct))
        {
            var initial = await store.ResolveAsync(new("alpha", FullScope, newRootIntent: "root"), null, true, ct);
            Assert.NotNull(initial);
            head.ThrowOnCommit = true;
            Assert.Null(await store.ResolveAsync(new("beta", FullScope, newRootIntent: "root"), null, true, ct));
            Assert.False(store.IsCurrent(initial));
            Assert.Null(await store.ResolveAsync(new("alpha", FullScope, existingGroup: initial.Group), initial.Group, false, ct));
        }

        Assert.NotEqual(head.Current!.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(StatePath, ct))));
        await Assert.ThrowsAsync<IOException>(() => OwnedPolicyStore.OpenAsync(StatePath, head, cancellationToken: ct));
    }

    private string StatePath => Path.Combine(_directory.FullName, "state.json");

    private static WebApplication BuildRouter(OwnedPolicyStore store, Verifier verifier, Uri endpoint, RouterConfig? config = null) =>
        ServerApp.BuildOwnedPrivate(["--urls=http://127.0.0.1:0"],
            new OwnedPrivateServerComposition(store, [new("private", endpoint, "/models", "/props",
                PrivateDestinationCapability.Chat | PrivateDestinationCapability.Text | PrivateDestinationCapability.ThreadCache)], verifier.Verify),
            configOverride: config ?? Config(endpoint));

    private static RouterConfig Config(Uri endpoint, bool owned = true, int streamBufferLimit = 0, bool extraModel = false,
        int upstreamIdleMs = 0)
    {
        var catalog = new Dictionary<string, EngineModelDefinition>(StringComparer.Ordinal)
        {
            [Model] = new() { Name = Model, Aliases = ["alias"] },
        };
        if (extraModel) catalog.Add("configured-only", new() { Name = "configured-only" });
        return new RouterConfig
        {
            OwnedPrivatePolicy = owned,
            StreamBufferLimitBytes = streamBufferLimit,
            DefaultModel = Model,
            AutoSwitch = false,
            Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal)
            {
                ["private"] = new() { Name = "private", Endpoint = endpoint, ApiKey = Key, Catalog = catalog,
                    StreamIdleTimeoutMs = upstreamIdleMs },
            },
        };
    }

    private static Uri EngineUri(WebApplication app) => new(app.Urls.Single() + "/v1");

    private static HttpClientHandler DirectHandler() => new() { UseProxy = false, AllowAutoRedirect = false };

    private static HttpClient ClientAt(WebApplication app, HttpClientHandler handler) => new(handler, disposeHandler: false)
        { BaseAddress = new Uri(app.Urls.Single()) };

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string uri, string credential,
        string? group, string? thread, string? body, CancellationToken ct, Action<HttpRequestMessage>? setup = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Fixture-Authority", credential);
        if (group is not null) request.Headers.Add(GroupHeader, group);
        if (thread is not null) request.Headers.Add("X-Inference-Thread", thread);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        setup?.Invoke(request);
        return await client.SendAsync(request, completion, ct);
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private sealed class Verifier
    {
        internal ConcurrentDictionary<string, VerifiedInferenceScope> Scopes { get; } = new(StringComparer.Ordinal);
        internal ValueTask<VerifiedInferenceScope?> Verify(HttpContext context, CancellationToken ct) =>
            ValueTask.FromResult(Scopes.GetValueOrDefault(context.Request.Headers["X-Fixture-Authority"].ToString()));
    }

    private sealed class Authority : IOwnedPolicyHeadAuthority
    {
        internal OwnedPolicyHead? Current { get; set; }
        internal bool ThrowOnCommit { get; set; }
        internal bool LoseConfirmationOnCommit { get; set; }
        private int _hold;
        internal TaskCompletionSource Entered { get; private set; } = NewSignal();
        internal TaskCompletionSource AllowCommit { get; private set; } = NewSignal();
        internal void HoldNextCommit()
        {
            Entered = NewSignal();
            AllowCommit = NewSignal();
            Interlocked.Exchange(ref _hold, 1);
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<OwnedPolicyHead?> ReadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Current);

        public async ValueTask<bool> CompareAndCommitAsync(OwnedPolicyHead? previous, OwnedPolicyHead proposed, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _hold, 0) != 0)
            {
                Entered.TrySetResult();
                await AllowCommit.Task.WaitAsync(cancellationToken);
            }
            if (ThrowOnCommit) throw new TimeoutException("mock head confirmation is unavailable");
            if (Current != previous) return false;
            Current = proposed;
            if (LoseConfirmationOnCommit) throw new TimeoutException("mock head committed without a reply");
            return true;
        }
    }

    private sealed class Capture
    {
        private int _requests;
        private int _modelsProbes;
        private int _servingProbes;
        private int _holdNext;
        internal int Requests => Volatile.Read(ref _requests);
        internal int ModelsProbes => Volatile.Read(ref _modelsProbes);
        internal int ServingProbes => Volatile.Read(ref _servingProbes);
        internal (int Status, string Address)? Redirect { get; init; }
        internal Func<HttpContext, Task>? StreamResponse { get; init; }
        internal bool ReportExtraModel { get; init; }
        internal ConcurrentQueue<(string Path, string Hint, string Authority)> Inferences { get; } = new();
        internal TaskCompletionSource InferenceEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowInference { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource InferenceAborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void HoldNextInference() => Interlocked.Exchange(ref _holdNext, 1);

        internal WebApplication Build()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Run(Respond);
            return app;
        }

        private async Task Respond(HttpContext context)
        {
            Interlocked.Increment(ref _requests);
            var request = context.Request;
            var response = context.Response;
            if (request.Path == "/models")
            {
                Interlocked.Increment(ref _modelsProbes);
                await response.WriteAsync(ReportExtraModel
                    ? """{"data":[{"id":"fixture-model"},{"id":"unconfigured-resident"}]}"""
                    : """{"data":[{"id":"fixture-model"}]}""", request.HttpContext.RequestAborted);
                return;
            }
            if (request.Path == "/props")
            {
                Interlocked.Increment(ref _servingProbes);
                await response.WriteAsync("""{"n_ctx":32768,"total_slots":1}""", request.HttpContext.RequestAborted);
                return;
            }
            Inferences.Enqueue((request.Path.ToString(), request.Headers["X-Inference-Thread"].ToString(), request.Headers.Authorization.ToString()));
            if (Interlocked.Exchange(ref _holdNext, 0) != 0)
            {
                InferenceEntered.TrySetResult();
                try { await AllowInference.Task.WaitAsync(request.HttpContext.RequestAborted); }
                catch (OperationCanceledException) when (request.HttpContext.RequestAborted.IsCancellationRequested)
                {
                    InferenceAborted.TrySetResult();
                    return;
                }
            }
            if (Redirect is { } target)
            {
                response.StatusCode = target.Status;
                response.Headers.Location = target.Address;
                await response.WriteAsync("fixture upstream detail", request.HttpContext.RequestAborted);
                return;
            }
            using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            if (json.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
            {
                response.ContentType = "text/event-stream";
                if (StreamResponse is { } scripted)
                {
                    await scripted(context);
                    return;
                }
                await response.WriteAsync("data: {\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n",
                    request.HttpContext.RequestAborted);
            }
            else await response.WriteAsync("fixture response", request.HttpContext.RequestAborted);
        }
    }
}
