using System.IO.Pipelines;
using System.Net;
using System.Text;
using Arboreal.InferenceRouter.Lifecycle;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedSwitchOwnerSealsBusyIncumbentBeforePhysicalCleanup(bool expire)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var discovery = new FakeEngineHandler { LoadedModels = ["shared", "independent"], NCtx = 8_192, TotalSlots = 1 };
        using var outside = new FakeEngineHandler { LoadedModels = ["outside"] };
        var ct = watchdog.Token;
        var a = MakeCommissionedDomain("a", "shared", 19001);
        var b = MakeCommissionedDomain("b", "independent", 19002);
        var legacy = new EngineDefinition { Name = "remote", Endpoint = new("http://external.invalid/v1"), Remote = true };
        var config = new RouterConfig
        {
            AutoSwitch = true,
            Engines = new Dictionary<string, EngineDefinition> { [a.Engine.Name] = a.Engine, [b.Engine.Name] = b.Engine, [legacy.Name] = legacy },
            HttpHandlerFactory = def => def == legacy ? outside : discovery,
        };

        await using var app = ServerApp.BuildCommissioned(["--urls", "http://127.0.0.1:0"],
            [a.Enrollment, b.Enrollment], configOverride: config, drainBudget: TimeSpan.FromSeconds(expire ? 1 : 8));
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        var router = app.Services.GetRequiredService<RouterCore>();
        Assert.True(await router.Engines[legacy.Name].ProbeHealthOnceAsync(ct));
        a.Authority.HoldClose = true;
        var first = await SubmitAsync("shared", "small", "incumbent", "original ticket");
        Assert.Equal(HttpStatusCode.OK, first.Status);
        await a.Authority.CloseStarted.Task.WaitAsync(ct);
        var original = Assert.Single(a.Authority.Offers);
        Assert.Equal("small", a.Authority.CurrentTarget?.Tier);
        while (router.Engines[a.Engine.Name].Inflight != 0) await Task.Delay(10, ct);
        Assert.Equal(0, router.Engines[a.Engine.Name].Inflight);
        Assert.Equal(0, a.Host.Stops);
        Assert.Single(router.Events.Replay(0), row => row.TicketId == first.Ticket && row.Kind == "request.selected");
        Assert.Single(router.Events.Replay(0), row => row.TicketId == first.Ticket && row.Kind == "request.completed");
        Assert.Equal(first.Body, ReadBody(a.Authority.Bytes(original)));
        var owner = SubmitAsync("shared", "alternate", "switch", "replacement ticket");
        try
        {
            await a.Authority.Sealed.Task.WaitAsync(ct);
            Assert.False(a.Enrollment.Staging.CanMint(original.Route));
            Assert.False(owner.IsCompleted);
            Assert.Equal(0, a.Host.Stops);
            Assert.Equal(1, a.Host.Creates);
            Assert.Single(a.Authority.Offers);

            var independent = await SubmitAsync("independent", "small", "independent", "second domain");
            Assert.Equal(HttpStatusCode.OK, independent.Status);
            Assert.Single(b.Authority.Offers);
            Assert.Equal(1, b.Host.Creates);
            var forwarded = await SubmitAsync("outside", null, "external", "legacy");
            Assert.Equal(HttpStatusCode.OK, forwarded.Status);
            Assert.Equal("legacy", Assert.Single(outside.ExecutionOrder));
            if (expire)
            {
                var refusal = await owner.WaitAsync(ct);
                Assert.True((int)refusal.Status >= 400);
                Assert.Equal(0, a.Host.Stops);
                Assert.Equal(1, a.Host.Creates);
                Assert.False(a.Enrollment.Staging.CanMint(original.Route));
                using var slot = HostReservation.OpenEnrolled(a.Authority.Scope);
                Assert.Null(slot.State.Pending);
            }
        }
        finally { a.Authority.ReleaseClose.TrySetResult(); }

        if (expire)
        {
            var timeout = await owner.WaitAsync(ct);
            Assert.Single(router.Events.Replay(0), row => row.TicketId == timeout.Ticket && row.Kind == "request.failed");
            owner = SubmitAsync("shared", "alternate", "retry", "replacement ticket");
        }
        var successor = await owner.WaitAsync(ct);
        Assert.Equal(HttpStatusCode.OK, successor.Status);
        Assert.Equal(1, a.Host.Stops);
        Assert.Equal(2, a.Host.Creates);
        Assert.Equal(0, discovery.CompletionCalls);
        Assert.Equal(2, a.Authority.Offers.Length);
        var replacement = a.Authority.Offers[1];
        Assert.NotEqual(original.Route, replacement.Route);
        Assert.False(a.Enrollment.Staging.CanMint(original.Route));
        Assert.Equal(successor.Body, ReadBody(a.Authority.Bytes(replacement)));
        Assert.Single(router.Events.Replay(0), row => row.TicketId == successor.Ticket && row.Kind == "request.selected");

        async Task<(HttpStatusCode Status, string Ticket, string Body)> SubmitAsync(string model, string? tier, string thread, string prompt)
        {
            var body = Encoding.UTF8.GetString(ManagedRequest(model, prompt, named: false).Body.Span);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Content = content;
            request.Headers.Add("X-Inference-Thread", thread);
            if (tier is not null) request.Headers.Add("X-Inference-Profile", tier);
            using var response = await client.SendAsync(request, ct);
            return (response.StatusCode, Assert.Single(response.Headers.GetValues("X-Inference-Ticket")), body);
        }

        static string ReadBody(byte[] bytes) => Encoding.UTF8.GetString(bytes).Split("\r\n\r\n", 2)[1];
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommissionedServerRebindBetweenCheckoutAndFirstWriteTransfersZeroBytes(bool afterCheckout)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var discovery = new FakeEngineHandler { LoadedModels = ["shared"], NCtx = 8_192, TotalSlots = 1 };
        var ct = watchdog.Token;
        var domain = MakeCommissionedDomain("a", "shared", 19001);
        var config = new RouterConfig
        {
            AutoSwitch = true,
            Engines = new Dictionary<string, EngineDefinition> { [domain.Engine.Name] = domain.Engine },
            HttpHandlerFactory = _ => discovery,
        };
        await using var app = ServerApp.BuildCommissioned([], [domain.Enrollment], configOverride: config);
        var router = app.Services.GetRequiredService<RouterCore>();
        domain.Authority.HoldCheckout = !afterCheckout;
        domain.Authority.HoldFreshEvidence = afterCheckout;
        var ticket = await router.EnqueueAsync("rebound", RecipeRequest("small", stream: true), ct);
        try
        {
            var intercepted = afterCheckout ? domain.Authority.FreshEvidenceStarted : domain.Authority.CheckoutStarted;
            await intercepted.Task.WaitAsync(ct);
            domain.Authority.RebindWorker();
        }
        finally
        {
            domain.Authority.ReleaseCheckout.TrySetResult();
            domain.Authority.ReleaseFreshEvidence.TrySetResult();
        }
        await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
        await domain.Authority.CloseStarted.Task.WaitAsync(ct);
        var old = Assert.Single(domain.Authority.Offers);
        Assert.Empty(domain.Authority.Bytes(old));
        Assert.Equal(0, domain.Authority.Writes);
        Assert.Equal(0, discovery.CompletionCalls);
        Assert.Equal(0, domain.Host.Stops);
        Assert.Equal(1, domain.Host.Creates);
        Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.selected");
        Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.failed");
    }

    [Fact(Timeout = 20_000)]
    public async Task CommissionedServerLostRetirementAndRestartCannotStopTheIncumbent()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var discovery = new FakeEngineHandler { LoadedModels = ["shared"], NCtx = 8_192, TotalSlots = 1 };
        var ct = watchdog.Token;
        var domain = MakeCommissionedDomain("a", "shared", 19001);
        var config = new RouterConfig
        {
            AutoSwitch = true,
            Engines = new Dictionary<string, EngineDefinition> { [domain.Engine.Name] = domain.Engine },
            HttpHandlerFactory = _ => discovery,
        };
        domain.Authority.ForgetTerminal = true;
        await using (var app = ServerApp.BuildCommissioned([], [domain.Enrollment], configOverride: config,
            drainBudget: TimeSpan.FromMilliseconds(150)))
        {
            var router = app.Services.GetRequiredService<RouterCore>();
            var first = await router.EnqueueAsync("incumbent", RecipeRequest("small"), ct);
            await first.CompletionTask.WaitAsync(ct);
            await domain.Authority.CloseStarted.Task.WaitAsync(ct);
            var owner = await router.EnqueueAsync("replacement", RecipeRequest("alternate"), ct);
            var error = await Assert.ThrowsAsync<RouterRequestException>(() => owner.CompletionTask.WaitAsync(ct));
            Assert.Equal("switch_failed", error.Code);
            Assert.Contains("retirement proof", error.Message, StringComparison.Ordinal);
            Assert.False(domain.Enrollment.Staging.CanMint(Assert.Single(domain.Authority.Offers).Route));
        }
        await using (var app = ServerApp.BuildCommissioned([], [domain.Enrollment], configOverride: config))
        {
            var router = app.Services.GetRequiredService<RouterCore>();
            var body = RecipeRequest("alternate");
            var ticket = await router.EnqueueAsync("restart", new InferenceRequest
            {
                Model = body.Model,
                Body = body.Body,
                Options = new RouterRequestOptions { Profile = "alternate", MaxQueueTime = TimeSpan.FromMilliseconds(150) },
            }, ct);
            await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
        }
        Assert.Equal(0, domain.Host.Stops);
        Assert.Equal(1, domain.Host.Creates);
        Assert.Single(domain.Authority.Offers);
        using var slot = HostReservation.OpenEnrolled(domain.Authority.Scope);
        Assert.Null(slot.State.Pending);
    }

    [Fact(Timeout = 20_000)]
    public async Task CommissionedServerAnswerTimeoutReleasesLogicalSlotWhileNativeUseRetires()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var discovery = new FakeEngineHandler { LoadedModels = ["shared"], NCtx = 8_192, TotalSlots = 1 };
        var ct = watchdog.Token;
        var domain = MakeCommissionedDomain("a", "shared", 19001, responseTimeoutMs: 500);
        var config = new RouterConfig
        {
            AutoSwitch = true,
            Engines = new Dictionary<string, EngineDefinition> { [domain.Engine.Name] = domain.Engine },
            HttpHandlerFactory = _ => discovery,
        };
        var authority = domain.Authority;
        authority.HoldReads = true;
        authority.HoldClose = true;
        await using var app = ServerApp.BuildCommissioned([], [domain.Enrollment], configOverride: config,
            drainBudget: TimeSpan.FromMilliseconds(500));
        var router = app.Services.GetRequiredService<RouterCore>();
        var current = await router.EnqueueAsync("incumbent", RecipeRequest("small"), ct);
        try
        {
            await authority.ReadStarted.Task.WaitAsync(ct);
            var error = await Assert.ThrowsAsync<RouterRequestException>(() => current.CompletionTask.WaitAsync(ct));
            Assert.Equal("engine_timeout", error.Code);
            while (router.Engines[domain.Engine.Name].Inflight != 0) await Task.Delay(10, ct);
            var owner = await router.EnqueueAsync("switch", RecipeRequest("alternate"), ct);
            await authority.Sealed.Task.WaitAsync(ct);
            var refusal = await Assert.ThrowsAsync<RouterRequestException>(() => owner.CompletionTask.WaitAsync(ct));
            Assert.Equal("switch_failed", refusal.Code);
            Assert.Equal(0, domain.Host.Stops);
            Assert.Equal(1, domain.Host.Creates);
            Assert.Single(router.Events.Replay(0), row => row.TicketId == current.Id && row.Kind == "request.failed");
        }
        finally
        {
            authority.ReleaseReads.TrySetResult();
            authority.ReleaseClose.TrySetResult();
        }
        await authority.CloseStarted.Task.WaitAsync(ct);
        authority.HoldReads = false;
        var successor = await router.EnqueueAsync("retry", RecipeRequest("alternate"), ct);
        await successor.CompletionTask.WaitAsync(ct);
        Assert.Equal(1, domain.Host.Stops);
        Assert.Equal(2, domain.Host.Creates);
        Assert.Equal(0, discovery.CompletionCalls);
    }

    [Fact(Timeout = 20_000)]
    public async Task CommissionedServerKeepsResponseBorrowedAfterDownstreamDepartureUntilPendingReadSettles()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var discovery = new FakeEngineHandler { LoadedModels = ["shared"], NCtx = 8_192, TotalSlots = 1 };
        var ct = watchdog.Token;
        var domain = MakeCommissionedDomain("a", "shared", 19001);
        var config = new RouterConfig
        {
            AutoSwitch = true,
            Engines = new Dictionary<string, EngineDefinition> { [domain.Engine.Name] = domain.Engine },
            HttpHandlerFactory = _ => discovery,
        };
        var authority = domain.Authority;
        authority.IncompleteStream = true;
        authority.HoldClose = true;
        await using var app = ServerApp.BuildCommissioned(["--urls", "http://127.0.0.1:0"],
            [domain.Enrollment], configOverride: config);
        await app.StartAsync(ct);
        using var handler = new SocketsHttpHandler { MaxResponseDrainSize = 0 };
        using var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        var router = app.Services.GetRequiredService<RouterCore>();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.Add("X-Inference-Profile", "small");
        using var content = new ByteArrayContent(RecipeRequest("small", stream: true).Body.ToArray());
        request.Content = content;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var ticket = Assert.Single(response.Headers.GetValues("X-Inference-Ticket"));
        try
        {
            var body = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(body);
            string? line;
            do
            {
                line = await reader.ReadLineAsync(ct);
                Assert.NotNull(line);
            }
            while (!line.StartsWith("data:", StringComparison.Ordinal));
            Assert.Contains("hello", line, StringComparison.Ordinal);
            await authority.ReadStarted.Task.WaitAsync(ct);
            response.Dispose();

            while (router.TryGetTicket(ticket)?.State != nameof(TicketState.Cancelled)) await Task.Delay(10, ct);
            while (router.Engines[domain.Engine.Name].Inflight != 0) await Task.Delay(10, ct);
            var owner = await router.EnqueueAsync("switch", RecipeRequest("alternate"), ct);
            await authority.Sealed.Task.WaitAsync(ct);
            Assert.False(owner.CompletionTask.IsCompleted);
            Assert.False(authority.CloseStarted.Task.IsCompleted);
            Assert.Equal(0, domain.Host.Stops);
            Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket && row.Kind == "request.cancelled");

            authority.IncompleteStream = false;
            authority.ReleaseReads.TrySetResult();
            await authority.CloseStarted.Task.WaitAsync(ct);
            Assert.Equal(0, domain.Host.Stops);
            authority.ReleaseClose.TrySetResult();
            await owner.CompletionTask.WaitAsync(ct);
            Assert.Equal(1, domain.Host.Stops);
            Assert.Equal(2, domain.Host.Creates);
        }
        finally
        {
            authority.ReleaseReads.TrySetResult();
            authority.ReleaseClose.TrySetResult();
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(0, true)]
    [InlineData(4096, true)]
    [InlineData(0, false)]
    public async Task CommissionedStreamCreditBoundsTwoSlowClientsAndFlushSettlesTheirPhysicalUses(int configuredLimit, bool exceedsCredit)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var relayClock = new ReadinessDeadlineTests.ManualClock();
        using var discovery = new FakeEngineHandler { LoadedModels = ["shared"], NCtx = 8_192, TotalSlots = 2 };
        using var legacyHandler = new FakeEngineHandler { LoadedModels = ["shared"] };
        var ct = watchdog.Token;
        var domain = MakeCommissionedDomain("a", "shared", 19001, slots: 2);
        var legacy = new EngineDefinition { Name = "remote", Endpoint = new("http://external.invalid/v1"), Remote = true };
        var config = new RouterConfig
        {
            AutoSwitch = true,
            Engines = new Dictionary<string, EngineDefinition> { [domain.Engine.Name] = domain.Engine, [legacy.Name] = legacy },
            HttpHandlerFactory = engine => engine == legacy ? legacyHandler : discovery,
            StreamBufferLimitBytes = configuredLimit,
        };
        await using var app = ServerApp.BuildCommissioned([], [domain.Enrollment], configOverride: config);
        var router = app.Services.GetRequiredService<RouterCore>();
        Assert.True(await router.Engines[legacy.Name].ProbeHealthOnceAsync(ct));
        var legacyTicket = await router.EnqueueHttpAsync("legacy", new InferenceRequest
        {
            Model = "shared",
            Stream = true,
            Body = """{"model":"shared","stream":true,"messages":[{"role":"user","content":"legacy route"}]}"""u8.ToArray(),
            Options = new RouterRequestOptions(),
        }, ct);
        var legacyWriter = new StreamRelayTests.GatedWriter(int.MaxValue);
        await RelayAsync(legacyTicket, legacyWriter).WaitAsync(ct);
        await legacyTicket.CompletionTask.WaitAsync(ct);
        Assert.Equal(legacy.Name, legacyTicket.Engine);
        Assert.Equal(configuredLimit != 0, legacyTicket.HttpDelivery is not null);
        Assert.Equal(1, legacyHandler.CompletionCalls);
        Assert.Empty(domain.Authority.Offers);

        var limit = configuredLimit == 0 ? HttpStreamDelivery.CommissionedDefaultLimit : configuredLimit;
        var bytes = Encoding.UTF8.GetBytes(
            "data: {\"model\":\"shared\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" +
            new string('x', exceedsCredit ? 3 * limit : 128) + "\"},\"finish_reason\":\"stop\"}]}\r\n\r\ndata: [DONE]\r\n\r\n");
        domain.Authority.ResponseBytes = [
            .. Encoding.ASCII.GetBytes(FormattableString.Invariant(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n")),
            .. bytes,
        ];
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanups = 0;
        router.AfterOptInTransportTaskForTests = id =>
        {
            if (id != legacyTicket.Id && Interlocked.Increment(ref cleanups) == 2) closed.TrySetResult();
        };
        var one = await router.EnqueueHttpAsync(null, RecipeRequest("small", stream: true), ct);
        var two = await router.EnqueueHttpAsync(null, RecipeRequest("small", stream: true), ct);
        var a = new StreamRelayTests.GatedWriter(2);
        var b = new StreamRelayTests.GatedWriter(2);
        var relayOne = RelayAsync(one, a);
        var relayTwo = RelayAsync(two, b);
        try
        {
            var blocked = await Task.WhenAll(a.Blocked.Task, b.Blocked.Task).WaitAsync(ct);
            Assert.Equal("a", one.Engine);
            Assert.Equal("a", two.Engine);
            Assert.All(blocked, count => Assert.InRange(count, 1, limit));
            var first = Assert.IsType<HttpStreamDelivery>(one.HttpDelivery);
            var second = Assert.IsType<HttpStreamDelivery>(two.HttpDelivery);
            if (!exceedsCredit) await Task.WhenAll(first.Sealed, second.Sealed).WaitAsync(ct);
            var expectedReservation = Math.Min(limit, bytes.Length);
            while (first.ReservedBytes < expectedReservation || second.ReservedBytes < expectedReservation)
                await Task.Delay(10, ct);
            Assert.Equal(expectedReservation, first.ReservedBytes);
            Assert.Equal(expectedReservation, second.ReservedBytes);
            Assert.False(first.Outcome.IsCompleted);
            Assert.False(second.Outcome.IsCompleted);
            Assert.False(domain.Authority.CloseStarted.Task.IsCompleted);
            Assert.Equal(2, domain.Authority.Writes);
            Assert.Equal(1, domain.Host.Creates);
            Assert.Equal(0, domain.Host.Stops);

            a.Release.TrySetResult(new FlushResult(false, false));
            b.Release.TrySetResult(new FlushResult(false, false));
            await Task.WhenAll(relayOne, relayTwo).WaitAsync(ct);
            await Task.WhenAll(one.CompletionTask, two.CompletionTask).WaitAsync(ct);
            await closed.Task.WaitAsync(ct);
            Assert.Equal(2, domain.Authority.ConfirmedCloses);
            Assert.Equal(0, first.ReservedBytes);
            Assert.Equal(0, second.ReservedBytes);
            foreach (var ticket in new[] { one, two })
            {
                var terminal = Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id &&
                    row.Kind is "request.completed" or "request.cancelled" or "request.failed");
                Assert.Equal("request.completed", terminal.Kind);
                Assert.Equal("complete", terminal.UpstreamOutcome);
            }
            Assert.Equal(bytes, WithoutFirstKeepalive(a.Received));
            Assert.Equal(bytes, WithoutFirstKeepalive(b.Received));
        }
        finally
        {
            a.Release.TrySetResult(new FlushResult(true, false));
            b.Release.TrySetResult(new FlushResult(true, false));
        }

        Task RelayAsync(RouterTicket ticket, StreamRelayTests.GatedWriter writer)
        {
            var context = new DefaultHttpContext { RequestAborted = ct };
            context.Features.Set<IHttpResponseBodyFeature>(new StreamRelayTests.BodyFeature(writer));
            return ServerApp.StreamTicketAsync(context, ticket, relayClock, router);
        }

        static byte[] WithoutFirstKeepalive(List<byte[]> chunks) => chunks
            .Where(static chunk => !chunk.AsSpan().StartsWith(": [router keepalive "u8))
            .SelectMany(static chunk => chunk).ToArray();
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShippingServeRefusesMissingProtectedAuthorityWithoutOrdinaryFallback(bool standalone)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var directory = Path.Combine(_directory.FullName, "missing-authority");
        var result = await CliCompletionProcessTests.RunCliAsync("serve", ["--commissioned", directory], null,
            watchdog.Token, start =>
            {
                if (standalone) start.ArgumentList[0] =
                    Path.Combine(AppContext.BaseDirectory, "Arboreal.InferenceRouter.Server.dll");
                start.Environment["ARBOREAL_ROUTER_CONFIG"] = Path.Combine(_directory.FullName, "ordinary.toml");
            });
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("could not bind or retain the protected serving authority", Encoding.UTF8.GetString(result.Stderr),
            StringComparison.Ordinal);
        Assert.Empty(result.Stdout);
    }

    [Theory]
    [InlineData("--commissioned")]
    [InlineData("--commissioned=/absent")]
    public async Task ShippingServeRefusesCompetingOrdinaryConfig(string selection)
    {
        var args = new[] { selection, "--config", "/absent/router.toml" };
        Assert.False(ServerApp.TryReadConfigOption(args, out var config, []));
        Assert.Null(config);
        Assert.Equal(2, await Cli.Program.Main(["serve", .. args]));
    }

    [Fact(Timeout = 15_000)]
    public async Task CheckedInitialAttachOpensOrdinaryHttpForTheOriginalRequestWithoutLaunching()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var discovery = new FakeEngineHandler { LoadedModels = ["shared"], NCtx = 8_192, TotalSlots = 1 };
        var ct = watchdog.Token;
        var domain = MakeCommissionedDomain("a", "shared", 19001);
        var config = new RouterConfig
        {
            AutoSwitch = true,
            Engines = new Dictionary<string, EngineDefinition> { [domain.Engine.Name] = domain.Engine },
            HttpHandlerFactory = _ => discovery,
        };
        var manager = new InProcessHostManager([domain.Enrollment]);
        await using var app = ServerApp.Build(["--urls", "http://127.0.0.1:0"], null, config, TimeProvider.System,
            hostManager: manager, startDispatcher: false);
        var router = app.Services.GetRequiredService<RouterCore>();
        var engine = domain.Engine;
        var entry = engine.Catalog["shared"];
        var profile = Assert.Single(entry.ServingProfiles, tier => tier.Name == "small");
        var lifecycle = new LifecycleTarget(engine.Name, "shared", profile, entry);
        var target = Assert.IsType<VerifiedDispatchTarget>(manager.Recipe(engine.Name, "shared", profile.Name));
        var operation = new HostMutation(1, Guid.NewGuid(), HostMutationKind.Launch, engine.Name, "shared", profile.Name, target.PlanDigest);
        var route = new VerifiedInstanceRoute(domain.Enrollment.Domain.Name, domain.Enrollment.Domain.Generation, engine.Name,
            target.PlanDigest, "fixture-initial-instance", "fixture-serving-generation", operation.Epoch, 1);
        domain.Host.SetAppliedLaunch(operation, lifecycle, route.ContainerId);
        domain.Authority.Publish(route, target);
        const long previousSequence = 600_000;
        VerifiedInstanceInvocation.ContinueAfter(previousSequence);
        domain.Authority.ApplicationWriter = Guid.NewGuid();
        var resident = new VerifiedDispatchResident(target, route, 8_192, 8_192, 1, engine.Endpoint,
            VerifiedDispatchCredential.Capture(engine, resolvedKey: null, epoch: 1), domain.Authority);
        var initial = new EnrolledInitialReadiness(route, "shared", new("shared", 8_192, 1, "small"));
        Assert.False(manager.TryAttachInitial(resident, initial with { Serving = initial.Serving with { Profile = "alternate" } },
            static () => true, ct));
        Assert.False(manager.TryAttachInitial(resident, initial, static () => false, ct));
        Assert.False(domain.Enrollment.Staging.CanMint(route));
        Assert.False(manager.IsResident(engine.Name));
        Assert.True(manager.TryAttachInitial(resident, initial, static () => true, ct));
        Assert.False(manager.TryAttachInitial(resident, initial, static () => true, ct));

        router.Start();
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        var original = Encoding.UTF8.GetString(ManagedRequest("shared", "checked initial original", named: false).Body.Span);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(original, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Inference-Profile", "small");
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = Assert.Single(response.Headers.GetValues("X-Inference-Ticket"));
        var granted = Assert.Single(domain.Authority.Offers);
        Assert.Equal(route, granted.Route);
        Assert.True(granted.Sequence > previousSequence);
        Assert.Equal(domain.Authority.ApplicationWriter, granted.CommissionedWriter);
        Assert.Equal(original, Encoding.UTF8.GetString(domain.Authority.Bytes(granted)).Split("\r\n\r\n", 2)[1]);
        Assert.Equal(0, discovery.CompletionCalls);
        Assert.Equal(0, domain.Host.Creates);
        Assert.Equal(0, domain.Host.Stops);
        Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket && row.Kind == "request.completed");
    }

    private (EngineDefinition Engine, HostManagerEnrollment Enrollment, FakeHost Host, OfflineCommissionedAuthority Authority)
        MakeCommissionedDomain(string name, string model, int port, int responseTimeoutMs = 600_000, int slots = 1)
    {
        var engine = ManagedEngine(name, model, name, port, equalRecipes: true, responseTimeoutMs, maxSlots: slots);
        var identity = new ExactTicketAdmission.DomainIdentity(name, 1);
        var host = new FakeHost(readinessTimeoutMs: 10_000, vacant: true);
        var staging = new HostClosedRouteStaging(host.FindLaunch, (launch, generation) =>
            new(name, identity.Generation, launch.Operation.Engine, launch.Operation.PlanDigest!, launch.InstanceId,
                launch.InstanceId + "-serving", launch.Operation.Epoch, generation), enrolledDomain: identity);
        var scope = HostReservation.ProvisionEnrolled(Path.Combine(_directory.FullName, name + ".commissioned"),
            new HostEnrollmentScope(name, identity.Generation));
        var authority = new OfflineCommissionedAuthority(scope, host, staging);
        var enrollment = new HostManagerEnrollment(identity, new HostLifecycleCoordinator(scope.Slot, host, staging), staging, authority,
            new Dictionary<string, HostStagedRuntimeFixture?>
            {
                [engine.Name] = new((stage, _) => Task.FromResult<HostStagedReadiness?>(new(stage, stage.Launch.Plan.Model,
                    new(stage.Launch.Plan.Model, 8_192, slots, stage.Launch.Plan.Tier)))),
            },
            stage =>
            {
                var target = new VerifiedDispatchTarget(name, model, stage.Launch.Plan.Tier!, stage.Route.EffectiveTarget, engine.Endpoint);
                authority.Publish(stage.Route, target);
                return new(target, stage.Route, 8_192, 8_192, slots, engine.Endpoint,
                    VerifiedDispatchCredential.Capture(engine, resolvedKey: null, epoch: 1), authority);
            }, (definition, target) => target.EffectiveProfile == "unsupported" ? null :
                new(definition.Name, target.Model, target.EffectiveProfile, host.GetLaunchPlanDigest(target), definition.Endpoint));
        return (engine, enrollment, host, authority);
    }

    private sealed class OfflineCommissionedAuthority(HostReservationScope scope, FakeHost host, HostClosedRouteStaging staging)
        : ICommissionedServingAuthority
    {
        private readonly Dictionary<VerifiedInstanceInvocation, byte[]> _bytes = [];
        private CommissionedServingStamp? _stamp;
        private CommissionedServingRetirement? _cutoff;
        private int _confirmedCloses;

        internal bool HoldClose { get; set; }
        internal bool HoldCheckout { get; set; }
        internal bool HoldFreshEvidence { get; set; }
        internal bool ForgetTerminal { get; set; }
        internal bool HoldReads { get; set; }
        internal bool IncompleteStream { get; set; }
        internal byte[]? ResponseBytes { get; set; }
        internal int ConfirmedCloses => Volatile.Read(ref _confirmedCloses);
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseReads { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Writes { get; private set; }
        internal TaskCompletionSource CheckoutStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCheckout { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FreshEvidenceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFreshEvidence { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal VerifiedDispatchTarget? CurrentTarget { get; private set; }
        internal TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Sealed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal VerifiedInstanceInvocation[] Offers { get { lock (staging.IssuerGate) return [.. _bytes.Keys]; } }

        public HostReservationScope Scope => scope;
        public string Daemon => scope.Identity.ToString();
        public string Partition => Daemon;
        public long ResourceEpoch => 1;
        public Guid ApplicationWriter { get; set; }
        public bool IsKnownVacant(long epoch) => epoch == ResourceEpoch && host.Resident is null;
        public bool Approves(VerifiedDispatchTarget target) => target.Engine == scope.Enrollment?.Group;
        public bool IsDisjointFromLocalController(EngineDefinition engine) => false;

        internal byte[] Bytes(VerifiedInstanceInvocation invocation)
        {
            lock (staging.IssuerGate) return _bytes[invocation];
        }

        internal void Publish(VerifiedInstanceRoute route, VerifiedDispatchTarget target)
        {
            lock (staging.IssuerGate)
            {
                _stamp = new(scope, route, ResourceEpoch, route.RouteIncarnation, route.ServingIncarnation, "fixture-worker");
                _cutoff = null;
                CurrentTarget = target;
            }
        }

        internal void RebindWorker()
        {
            lock (staging.IssuerGate) _stamp = _stamp! with { WorkerIncarnation = "rebound-worker" };
        }

        public CommissionedServingStamp? ReadServing(VerifiedInstanceRoute route)
        {
            lock (staging.IssuerGate)
                return _stamp?.Route == route && host.Resident == new HostResident(route.Engine, route.ContainerId) ? _stamp : null;
        }

        public async ValueTask<CommissionedServingRetirement?> SealAndDrainAsync(CommissionedServingStamp stamp, CancellationToken ct)
        {
            lock (staging.IssuerGate)
            {
                if (_stamp != stamp || staging.CanMint(stamp.Route)) return null;
                _cutoff ??= new(stamp, Guid.NewGuid());
                Sealed.TrySetResult();
            }
            if (HoldClose) await ReleaseClose.Task.WaitAsync(ct);
            return _cutoff;
        }

        public ICommissionedStopPermit? TryReserveStop(CommissionedServingRetirement retirement) =>
            _cutoff == retirement && ReadServing(retirement.Stamp.Route) == retirement.Stamp ? new StopPermit(host) : null;

        public ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken ct) =>
            throw new InvalidOperationException("Commissioned dispatch requires certified checkout.");

        public async ValueTask<VerifiedCheckout> CheckoutVerifiedAsync(VerifiedInstanceInvocation invocation, CancellationToken ct)
        {
            VerifiedInstanceGrant grant;
            lock (staging.IssuerGate)
            {
                if (!staging.CanMint(invocation.Route) || ReadServing(invocation.Route) is null)
                    return new(VerifiedCheckoutDisposition.NoTransfer);
                var completeHeaders = IncompleteStream;
                var initial = completeHeaders ? Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: 8192\r\nConnection: close\r\n\r\n" +
                    """data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":1,"model":"shared","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}""" +
                    "\r\n\r\n") : ResponseBytes;
                var reads = 0;
                var channel = new RecordedChannel(initial, HoldReads || completeHeaders ? async () =>
                {
                    if (completeHeaders && Interlocked.Increment(ref reads) == 1) return;
                    ReadStarted.TrySetResult();
                    await ReleaseReads.Task;
                } : null);
                _bytes.Add(invocation, []);
                grant = new VerifiedInstanceGrant(invocation, channel, async () =>
                {
                    lock (staging.IssuerGate) _bytes[invocation] = channel.Bytes;
                    CloseStarted.TrySetResult();
                    if (HoldClose) await ReleaseClose.Task;
                    await channel.DisposeAsync();
                    if (ForgetTerminal) throw new IOException("The offline physical terminal acknowledgment was lost.");
                    Interlocked.Increment(ref _confirmedCloses);
                });
            }
            if (HoldCheckout)
            {
                CheckoutStarted.TrySetResult();
                await ReleaseCheckout.Task;
            }
            return new(VerifiedCheckoutDisposition.Transferred, grant);
        }

        public async ValueTask<bool> CheckBeforeFirstWriteAsync(VerifiedInstanceGrant grant, CancellationToken ct)
        {
            if (!HoldFreshEvidence) return true;
            var before = ReadServing(grant.Invocation.Route);
            FreshEvidenceStarted.TrySetResult();
            await ReleaseFreshEvidence.Task.WaitAsync(ct);
            return before is not null && before == ReadServing(grant.Invocation.Route);
        }

        public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
        {
            if (!staging.CanMint(grant.Invocation.Route) || ReadServing(grant.Invocation.Route) is null) return null;
            Writes++;
            return new Permit();
        }

        private sealed class StopPermit(FakeHost host) : ICommissionedStopPermit
        {
            public ValueTask<HostMutationOutcome> StopAsync(HostMutation operation, CancellationToken ct) => host.StopAsync(operation, ct);
            public void Dispose() { }
        }

        private sealed class Permit : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
