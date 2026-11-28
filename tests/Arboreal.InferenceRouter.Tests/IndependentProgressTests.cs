using System.Net;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Arboreal.InferenceRouter.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed class IndependentProgressTests
{
    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteCaptureSeesSameAndDifferentModelsWhileLocalWorkIsHeld(bool holdActivation)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var entered = NewBarrier();
        using var local = new FakeEngineHandler
        {
            LoadedModels = ["shared"],
            HoldCompletions = !holdActivation,
            AfterHeldCompletionEnteredForTests = () => entered.TrySetResult(),
        };
        using var remote = new FakeEngineHandler { LoadedModels = ["other"] };
        var provider = new LocalProvider(holdActivation ? entered : null);
        await using var router = MakeRouter(local, remote, provider, ["shared"], ["other"], maxSlots: 1);
        router.Start();
        var held = await router.EnqueueAsync("held", Request("shared", "held"), ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            remote.LoadedModels = ["shared", "other"];
            Assert.True(await router.Engines["remote"].ProbeHealthOnceAsync(ct));
            var same = await router.EnqueueAsync("same", Request("shared", "same"), ct);
            var different = await router.EnqueueAsync("different", Request("other", "different"), ct);
            await Task.WhenAll(same.CompletionTask, different.CompletionTask).WaitAsync(ct);

            Assert.Equal("remote", same.Engine);
            Assert.Equal("remote", different.Engine);
            Assert.Contains("same", remote.ExecutionOrder);
            Assert.Contains("different", remote.ExecutionOrder);
            Assert.Equal(2, remote.CompletionCalls);
            Assert.False(held.CompletionTask.IsCompleted);
        }
        finally
        {
            provider.Release();
            local.ReleaseGate();
        }
        await held.CompletionTask.WaitAsync(ct);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedDrainIsRetiredWhenItsTicketCancelsOrFindsARemoteRoute(bool cancel)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var heldEntered = NewBarrier();
        var followerEntered = NewBarrier();
        using var local = new FakeEngineHandler { LoadedModels = ["resident"], HoldCompletions = true, TotalSlots = 2 };
        local.AfterHeldCompletionEnteredForTests = () =>
        {
            if (local.CompletionCalls == 1) heldEntered.TrySetResult();
            else followerEntered.TrySetResult();
        };
        using var remote = new FakeEngineHandler { LoadedModels = ["other"] };
        var provider = new LocalProvider();
        await using var router = MakeRouter(local, remote, provider, ["resident", "cold"], ["cold", "other"], maxSlots: 2);
        var draining = NewBarrier();
        var blocked = NewBarrier();
        router.Events.Subscribe(entry =>
        {
            if (entry.Kind != "request.waiting") return;
            if (entry.ThreadId == "cold" && entry.Reason == "lifecycle_group_draining (private-gpu)")
                draining.TrySetResult();
            if (entry.ThreadId == "follower" && entry.Reason == "lifecycle_group_reserved (private-gpu)")
                blocked.TrySetResult();
        });
        router.Start();
        var held = await router.EnqueueAsync("held", Request("resident", "held"), ct);
        try
        {
            await heldEntered.Task.WaitAsync(ct);
            var cold = await router.EnqueueAsync("cold", Request("cold", "cold"), ct);
            await draining.Task.WaitAsync(ct);
            var wait = Assert.IsType<TicketSnapshot>(router.TryGetTicket(cold.Id));
            Assert.Equal("lifecycle_group_draining", wait.QueueWaitReason);
            Assert.Equal("private-gpu", wait.QueueWaitScope);
            var follower = await router.EnqueueAsync("follower", Request("resident", "follower"), ct);
            await blocked.Task.WaitAsync(ct);
            Assert.Equal(1, local.CompletionCalls);

            if (cancel)
            {
                Assert.True(router.TryCancel(cold.Id));
                var failure = await Assert.ThrowsAsync<RouterRequestException>(() => cold.CompletionTask.WaitAsync(ct));
                Assert.Equal("cancelled", failure.Code);
                Assert.Contains(router.Events.Replay(0), entry => entry.TicketId == cold.Id && entry.Kind == "request.cancelled");
            }
            else
            {
                remote.LoadedModels = ["cold", "other"];
                Assert.True(await router.Engines["remote"].ProbeHealthOnceAsync(ct));
                await cold.CompletionTask.WaitAsync(ct);
                Assert.Equal("remote", cold.Engine);
                Assert.Equal("cold", Assert.Single(remote.ExecutionOrder));
            }
            await followerEntered.Task.WaitAsync(ct);
            Assert.Equal("local", follower.Engine);
            Assert.Equal(1, provider.Launches);
            Assert.False(held.CompletionTask.IsCompleted);
            Assert.Null(router.TryGetTicket(cold.Id)?.QueueWaitReason);
        }
        finally { local.ReleaseGate(); }
        await held.CompletionTask.WaitAsync(ct);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RemoteCapacityDoesNotBlockNormalHttpToManagedLocal(bool warm, bool sameModel)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = bound.Token;
        var remoteBody = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readiness = NewBarrier();
        var measuredModel = sameModel ? "m0" : "m1";
        using var local = new FakeEngineHandler { LoadedModels = [], TotalSlots = 1 };
        var localBodies = new System.Collections.Concurrent.ConcurrentQueue<string>();
        local.CaptureCompletionBodyForTests = localBodies.Enqueue;
        using var remote = new FakeEngineHandler
        {
            LoadedModels = ["m0"],
            TotalSlots = 1,
            HoldCompletions = true,
            CaptureCompletionBodyForTests = body => remoteBody.TrySetResult(body),
        };
        var provider = new LocalProvider(readiness, target => local.LoadedModels = [target.Model]);
        var config = new RouterConfig
        {
            AutoSwitch = true,
            Engines = new Dictionary<string, EngineDefinition>
            {
                ["local"] = Definition("local", [measuredModel], managed: true),
                ["remote"] = Definition("remote", ["m0", "m1"], managed: false),
            },
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 1 },
            HttpHandlerFactory = engine => engine.Name == "local" ? local : remote,
        };
        await using var app = ServerApp.Build(["--urls", "http://127.0.0.1:0"], null, config, TimeProvider.System,
            lifecycleFactory: _ => provider);
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        var router = app.Services.GetRequiredService<RouterCore>();
        // The first remote call has no ready local contender, even for the shared model.
        Assert.True(await router.Engines["remote"].ProbeHealthOnceAsync(ct));
        var held = SubmitAsync("m0", "held remote");
        try
        {
            Assert.Equal(Encoding.UTF8.GetString(Request("m0", "held remote").Body.Span), await remoteBody.Task.WaitAsync(ct));
            if (warm)
            {
                var prime = SubmitAsync(measuredModel, "prime local");
                await readiness.Task.WaitAsync(ct);
                Assert.Equal(0, local.CompletionCalls);
                provider.Release();
                Assert.Equal(HttpStatusCode.OK, (await prime).Status);
                while (router.Engines["local"].Inflight != 0) await Task.Delay(10, ct);
            }

            var launches = provider.Launches;
            var stops = provider.Stops;
            var writes = local.CompletionCalls;
            var measured = SubmitAsync(measuredModel, "original measured request");
            if (!warm)
            {
                await readiness.Task.WaitAsync(ct);
                Assert.False(measured.IsCompleted);
                Assert.Equal(writes, local.CompletionCalls);
                provider.Release();
            }
            var response = await measured;
            Assert.Equal(HttpStatusCode.OK, response.Status);
            Assert.False(held.IsCompleted);
            Assert.Equal(1, router.Engines["remote"].Inflight);
            Assert.Equal(1, remote.CompletionCalls);
            Assert.Equal(writes + 1, local.CompletionCalls);
            Assert.Equal(launches + (warm ? 0 : 1), provider.Launches);
            if (warm) Assert.Equal(stops, provider.Stops);
            Assert.Equal("original measured request", local.ExecutionOrder.Last());
            Assert.Equal(Encoding.UTF8.GetString(Request(measuredModel, "original measured request").Body.Span), localBodies.Last());
            var snapshot = Assert.IsType<TicketSnapshot>(router.TryGetTicket(response.Ticket));
            Assert.Equal(measuredModel, snapshot.Model);
            Assert.Equal("local", snapshot.Engine);
            var history = router.Events.Replay(0).Where(row => row.TicketId == response.Ticket).ToArray();
            Assert.Single(history, row => row.Kind == "request.enqueued");
            Assert.Single(history, row => row.Kind == "request.selected");
            Assert.Single(history, row => row.Kind == "request.completed");
        }
        finally
        {
            provider.Release();
            remote.ReleaseGate();
        }

        var released = await held;
        Assert.Equal(HttpStatusCode.OK, released.Status);
        Assert.Equal("held remote", Assert.Single(remote.ExecutionOrder));
        Assert.Equal(1, remote.CompletionCalls);
        Assert.Equal(warm ? 2 : 1, local.CompletionCalls);

        async Task<(HttpStatusCode Status, string Ticket)> SubmitAsync(string model, string prompt)
        {
            using var content = new ByteArrayContent(Request(model, prompt).Body.ToArray());
            content.Headers.ContentType = new("application/json");
            using var response = await client.PostAsync("/v1/chat/completions", content, ct);
            return (response.StatusCode, Assert.Single(response.Headers.GetValues("X-Inference-Ticket")));
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DrainProbeIsPureAndTheOnlyAcceptedInterestRetiresBeforeReassignmentOrExpiry(bool expires)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var entered = NewBarrier();
        using var local = new FakeEngineHandler { LoadedModels = ["resident"], HoldCompletions = true, TotalSlots = 2,
            AfterHeldCompletionEnteredForTests = () => entered.TrySetResult() };
        using var remote = new FakeEngineHandler { LoadedModels = ["other"] };
        var clock = new ReadinessDeadlineTests.ManualClock();
        var provider = new LocalProvider();
        await using var router = MakeRouter(local, remote, provider, ["resident", "cold"], ["cold", "other"], 2, clock);
        router.SuppressQueueHooksForTests = true;
        await ProbeAllAsync(router, ct);
        var incumbent = await router.EnqueueAsync("incumbent", Request("resident", "held local"), ct);
        Assert.True(router.TryDispatchOnce());
        try
        {
            await entered.Task.WaitAsync(ct);
            var targetThread = router.RegisterThread("switch", ThreadPriority.Normal());
            var request = new InferenceRequest
            {
                Model = "cold",
                Body = Request("cold", "same queued request").Body,
                Options = new RouterRequestOptions { MaxQueueTime = TimeSpan.FromMilliseconds(100) },
            };
            var target = await router.EnqueueAsync(targetThread.ThreadId, request, ct);
            var originalAge = target.QueuedAt;
            var group = Assert.IsType<LocalLifecycleGroup>(router.LocalGroup("local"));
            var before = group.ReadDrainInterest();
            var inspection = new QueuedRequest
            {
                Ticket = target, Request = request, Thread = targetThread,
                PriorityNode = new(target, targetThread.Priority, 0), EnqueuedSeq = 0, PromptChars = 0,
            };
            for (var i = 0; i < 3; i++)
                Assert.Equal(LocalLifecycleGroup.Availability.Draining,
                    group.Probe(inspection, router.Engines["local"], profile: null, switchRequired: true));
            Assert.Null(before.Owner);
            Assert.Equal(before, group.ReadDrainInterest());

            Assert.False(router.TryDispatchRound());
            var accepted = group.ReadDrainInterest();
            Assert.Same(target, accepted.Owner);
            Assert.Equal(before.Revision + 1, accepted.Revision);
            var observation = Assert.IsType<QueueDispatchObservation>(router.TryGetTicket(target.Id)?.QueueDispatch);
            Assert.Equal("private-gpu", observation.AcceptedDrainScope);
            Assert.False(router.TryDispatchRound());
            Assert.Equal(accepted, group.ReadDrainInterest());
            Assert.Equal(observation.ObservationId, router.TryGetTicket(target.Id)?.QueueDispatch?.ObservationId);

            var capturedOwner = new TaskCompletionSource<RouterTicket?>(TaskCreationOptions.RunContinuationsAsynchronously);
            remote.CaptureCompletionBodyForTests = _ => capturedOwner.TrySetResult(group.ReadDrainInterest().Owner);
            remote.LoadedModels = ["other", "cold"];
            Assert.True(await router.Engines["remote"].ProbeHealthOnceAsync(ct));
            clock.AdvanceTo(expires ? 101 : 90);
            if (expires)
            {
                Assert.False(router.TryDispatchRound());
                var error = await Assert.ThrowsAsync<RouterRequestException>(() => target.CompletionTask.WaitAsync(ct));
                Assert.Equal("deadline_exceeded", error.Code);
                Assert.Equal(0, remote.CompletionCalls);
                var unrelated = await router.EnqueueAsync("independent", Request("other", "independent progress"), ct);
                Assert.True(router.TryDispatchOnce());
                await unrelated.CompletionTask.WaitAsync(ct);
            }
            else
            {
                Assert.True(router.TryDispatchOnce());
                await target.CompletionTask.WaitAsync(ct);
                Assert.Equal("remote", target.Engine);
                Assert.Equal("same queued request", Assert.Single(remote.ExecutionOrder));
            }

            Assert.Null(await capturedOwner.Task.WaitAsync(ct));
            Assert.Null(group.ReadDrainInterest().Owner);
            Assert.False(incumbent.CompletionTask.IsCompleted);
            Assert.Equal(1, router.Engines["local"].InferenceUses);
            Assert.Equal(1, provider.Launches);
            var snapshot = Assert.IsType<TicketSnapshot>(router.TryGetTicket(target.Id));
            Assert.Equal(originalAge, snapshot.QueuedAt);
            Assert.Null(snapshot.QueueDispatch);
            Assert.Single(router.Events.Replay(0), row => row.TicketId == target.Id &&
                row.Kind is "request.completed" or "request.cancelled" or "request.failed");
        }
        finally { local.ReleaseGate(); }
        await incumbent.CompletionTask.WaitAsync(ct);
    }

    [Fact]
    public async Task DispatcherPublishesOneBoundedWaitPacketForARealSharedThreadCap()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var fake = new FakeEngineHandler { LoadedModels = ["shared"] };
        var ct = bound.Token;
        var engines = Enumerable.Range(0, 12).Select(i => Definition(FormattableString.Invariant($"route-{i}"), ["shared"], managed: false))
            .ToDictionary(static engine => engine.Name, StringComparer.Ordinal);
        await using var router = new RouterCore(new RouterConfig { Engines = engines, HttpHandlerFactory = _ => fake });
        var thread = router.RegisterThread("shared-cap", ThreadPriority.Normal());
        thread.IncrementInflight();
        try
        {
            var ticket = await router.EnqueueAsync(thread.ThreadId, Request("shared", "private prompt"), ct);
            Assert.False(router.TryDispatchRound());
            var snapshot = Assert.IsType<TicketSnapshot>(router.TryGetTicket(ticket.Id));
            var packet = Assert.IsType<QueueDispatchObservation>(snapshot.QueueDispatch);
            Assert.Equal("thread_concurrency_limit", packet.Reason);
            Assert.Equal(thread.ThreadId, packet.Scope);
            Assert.Null(packet.AcceptedDrainScope);
            Assert.Equal(8, packet.Alternatives.Count);
            Assert.True(packet.AlternativesTruncated);
            Assert.All(packet.Alternatives, route =>
            {
                Assert.True(engines.ContainsKey(route.Engine));
                Assert.Equal("not_evaluated", route.State);
                Assert.Null(route.Scope);
            });
            Assert.False(router.TryDispatchRound());
            var wait = Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.waiting");
            Assert.Same(packet, wait.QueueDispatch);
            Assert.Same(packet, router.TryGetTicket(ticket.Id)?.QueueDispatch);
            using var ticketJson = JsonDocument.Parse(AirJson.ToBytes(snapshot, AirServerJson.AirServerContext.Default.TicketSnapshot));
            using var eventJson = JsonDocument.Parse(AirJson.ToBytes(wait, AirServerJson.AirServerContext.Default.RouterEvent));
            var ticketReport = ticketJson.RootElement.GetProperty("QueueDispatch");
            Assert.Equal(ticketReport.GetRawText(), eventJson.RootElement.GetProperty("QueueDispatch").GetRawText());
            Assert.Equal(packet.ObservationId, ticketReport.GetProperty("ObservationId").GetInt64());
            Assert.True(ticketReport.GetProperty("AlternativesTruncated").GetBoolean());
            Assert.DoesNotContain("private prompt", ticketReport.GetRawText(), StringComparison.Ordinal);
            Assert.True(router.TryCancel(ticket.Id));
            await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
            Assert.Null(router.TryGetTicket(ticket.Id)?.QueueDispatch);
        }
        finally { thread.DecrementInflight(); }
    }

    [Fact(Timeout = 20_000)]
    public async Task ScopedHttpFeedAndTicketShowOnlyTheEligibleAlternativeAtAFullThread()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = bound.Token;
        var entered = NewBarrier();
        using var eligible = new FakeEngineHandler
        {
            LoadedModels = ["a"], TotalSlots = 2, HoldCompletions = true,
            AfterHeldCompletionEnteredForTests = () => entered.TrySetResult(),
        };
        using var irrelevant = new FakeEngineHandler { LoadedModels = ["b"] };
        var engines = Enumerable.Range(0, 11).Select(i => Definition(FormattableString.Invariant($"unrelated-{i}"), ["b"], managed: false))
            .ToDictionary(static engine => engine.Name, StringComparer.Ordinal);
        engines.Add("eligible", Definition("eligible", ["a"], managed: false));
        var config = new RouterConfig
        {
            Engines = engines,
            HttpHandlerFactory = engine => engine.Name == "eligible" ? eligible : irrelevant,
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = 2 },
        };
        await using var app = ServerApp.Build(["--urls", "http://127.0.0.1:0"], configOverride: config);
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        var router = app.Services.GetRequiredService<RouterCore>();
        Assert.True(await router.Engines["eligible"].ProbeHealthOnceAsync(ct));
        var (_, first) = await WireHarness.StartStreamAsync(client, "shared-cap", StreamBody("active prompt marker"), ct);
        using var held = first;
        try
        {
            await entered.Task.WaitAsync(ct);
            var claim = router.Engines["eligible"].AdmissionClaims;
            Assert.Equal(1, claim);
            var (ticketId, second) = await WireHarness.StartStreamAsync(client, "shared-cap", StreamBody("second private marker"), ct);
            using var queued = second;
            var (otherId, third) = await WireHarness.StartStreamAsync(client, "shared-cap", StreamBody("third private marker"), ct);
            using var another = third;

            var frames = await WireHarness.ReadSseFramesAsync(client, $"/admin/events?ticket={ticketId}", idleMs: 400);
            Assert.NotEmpty(frames);
            string? packet = null;
            foreach (var frame in frames)
            {
                using var json = JsonDocument.Parse(frame);
                var row = json.RootElement;
                Assert.Equal(ticketId, row.GetProperty("TicketId").GetString());
                Assert.DoesNotContain(otherId, frame, StringComparison.Ordinal);
                Assert.DoesNotContain("private marker", frame, StringComparison.Ordinal);
                if (row.GetProperty("Kind").GetString() != "request.waiting") continue;
                Assert.Null(packet);
                packet = row.GetProperty("QueueDispatch").GetRawText();
            }

            Assert.NotNull(packet);
            var status = await client.GetStringAsync($"/admin/tickets/{ticketId}", ct);
            using var ticket = JsonDocument.Parse(status);
            Assert.Equal("Queued", ticket.RootElement.GetProperty("State").GetString());
            var report = ticket.RootElement.GetProperty("QueueDispatch");
            Assert.Equal(report.GetRawText(), packet);
            Assert.Equal("thread_concurrency_limit", report.GetProperty("Reason").GetString());
            Assert.Equal("shared-cap", report.GetProperty("Scope").GetString());
            Assert.False(report.TryGetProperty("AlternativesTruncated", out _));
            var alternative = Assert.Single(report.GetProperty("Alternatives").EnumerateArray());
            Assert.Equal("eligible", alternative.GetProperty("Engine").GetString());
            Assert.Equal("not_evaluated", alternative.GetProperty("State").GetString());
            Assert.DoesNotContain("private marker", status, StringComparison.Ordinal);

            Assert.Empty(await WireHarness.ReadSseFramesAsync(client,
                $"/admin/events?ticket={ticketId}&engine=eligible&kinds=request.waiting", idleMs: 150));
            Assert.Equal(claim, router.Engines["eligible"].AdmissionClaims);
            Assert.Equal(1, eligible.CompletionCalls);
            Assert.All(router.Engines.Values.Where(engine => engine.Name != "eligible"), engine => Assert.Equal(0, engine.AdmissionClaims));
            Assert.Equal(0, irrelevant.CompletionCalls);

            eligible.ReleaseGate();
            await Task.WhenAll(held.Content.ReadAsStringAsync(ct), queued.Content.ReadAsStringAsync(ct),
                another.Content.ReadAsStringAsync(ct));
            Assert.Equal(3, router.Engines["eligible"].AdmissionClaims);
            Assert.Equal(3, eligible.CompletionCalls);
        }
        finally { eligible.ReleaseGate(); }

        static string StreamBody(string prompt) =>
            $$"""{"model":"a","stream":true,"messages":[{"role":"user","content":"{{prompt}}"}]}""";
    }

    [Fact(Timeout = 20_000)]
    public async Task ReadyProbeReportsTheEarlierCompetitorWithoutClaimingAnEngineOrGroupUse()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var local = new FakeEngineHandler { LoadedModels = ["shared"], TotalSlots = 1 };
        using var remote = new FakeEngineHandler { LoadedModels = ["other"] };
        var provider = new LocalProvider();
        await using var router = MakeRouter(local, remote, provider, ["shared"], ["other"], 1);
        await ProbeAllAsync(router, ct);
        var priming = await router.EnqueueAsync("prime", Request("shared", "prime"), ct);
        Assert.True(router.TryDispatchOnce());
        await priming.CompletionTask.WaitAsync(ct);

        var group = Assert.IsType<LocalLifecycleGroup>(router.LocalGroup("local"));
        var engine = router.Engines["local"];
        while (!group.IsIdle || engine.Inflight != 0) await Task.Delay(10, ct);
        var claims = engine.AdmissionClaims;
        var revision = group.Revision;
        var earlierThread = router.RegisterThread("earlier", ThreadPriority.High());
        earlierThread.IncrementInflight();
        var earlier = await router.EnqueueAsync(earlierThread.ThreadId, Request("shared", "earlier"), ct);
        var follower = await router.EnqueueAsync("follower", Request("shared", "follower"), ct);
        router.BeforeAdmissionCommitForTests = ticket =>
        {
            Assert.Same(follower, ticket);
            earlierThread.DecrementInflight();
        };

        Assert.False(router.TryDispatchRound());
        Assert.Equal(claims, engine.AdmissionClaims);
        Assert.Equal(revision, group.Revision);
        Assert.Equal(0, engine.Inflight);
        Assert.Equal(0, engine.InferenceUses);
        Assert.Equal(1, local.CompletionCalls);
        Assert.Null(group.ReadDrainInterest().Owner);
        Assert.Equal(TicketState.Queued, follower.State);
        var report = Assert.IsType<QueueDispatchObservation>(router.TryGetTicket(follower.Id)?.QueueDispatch);
        Assert.Equal("route_admission_wait", report.Reason);
        var alternative = Assert.Single(report.Alternatives);
        Assert.Equal("observed_ready", alternative.State);
        Assert.Equal("route_admission_wait", alternative.Reason);
        Assert.Equal("local", alternative.Scope);
        Assert.DoesNotContain(router.Events.Replay(0), row => row.TicketId == follower.Id && row.Kind == "request.selected");

        router.BeforeAdmissionCommitForTests = null;
        Assert.True(router.TryDispatchOnce());
        await earlier.CompletionTask.WaitAsync(ct);
        Assert.Equal(claims + 1, engine.AdmissionClaims);
        Assert.Equal("earlier", local.ExecutionOrder.Last());
        Assert.Equal(TicketState.Queued, follower.State);
    }

    [Fact]
    public async Task RouteChangeDiscardsTheObservedWaitBeforeItReachesTicketOrFeed()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var heldEngine = new FakeEngineHandler { LoadedModels = ["shared"] };
        using var alternateEngine = new FakeEngineHandler { LoadedModels = [] };
        var ct = bound.Token;
        await using var router = MakeRouter(heldEngine, alternateEngine, null, ["shared"], ["shared"], 1);
        await ProbeAllAsync(router, ct);
        var held = router.Engines["local"];
        held.BeginRequest(0);
        try
        {
            var ticket = await router.EnqueueAsync("caller", Request("shared", "original"), ct);
            var age = ticket.QueuedAt;
            router.BeforeDispatchWaitPublicationForTests = observed =>
            {
                Assert.Same(ticket, observed);
                alternateEngine.LoadedModels = ["shared"];
                Assert.True(router.Engines["remote"].ProbeHealthOnceAsync(ct).GetAwaiter().GetResult());
            };
            Assert.False(router.TryDispatchRound());
            Assert.Null(router.TryGetTicket(ticket.Id)?.QueueDispatch);
            Assert.DoesNotContain(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.waiting");
            router.BeforeDispatchWaitPublicationForTests = null;
            Assert.True(router.TryDispatchOnce());
            await ticket.CompletionTask.WaitAsync(ct);
            Assert.Equal(age, router.TryGetTicket(ticket.Id)?.QueuedAt);
            Assert.Equal("remote", ticket.Engine);
            Assert.Equal(1, alternateEngine.CompletionCalls);
            Assert.Equal(0, heldEngine.CompletionCalls);
            Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.selected");
        }
        finally { held.EndRequest(0); }
    }

    [Fact(Timeout = 20_000)]
    public async Task AFullRemoteRouteDoesNotHideTheOtherReadyRoute()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        var entered = NewBarrier();
        using var preferred = new FakeEngineHandler
        {
            LoadedModels = ["held", "shared"],
            HoldCompletions = true,
            AfterHeldCompletionEnteredForTests = () => entered.TrySetResult(),
        };
        using var alternate = new FakeEngineHandler { LoadedModels = ["shared", "other"] };
        await using var router = MakeRouter(preferred, alternate, null, ["held", "shared"], ["shared", "other"], maxSlots: 1);
        try
        {
            router.Start();
            var held = await router.EnqueueAsync("held", Request("held", "held"), ct);
            await entered.Task.WaitAsync(ct);
            // Hold the same accounting split as the existing pressure fixture so the full route
            // ranks first and the test exercises capacity fallback rather than simple load balancing.
            router.Engines["remote"].BeginRequest(10_000);
            router.Engines["remote"].EndRequest(0); // Keep busy score high at zero logical in-flight.
            try
            {
                var same = await router.EnqueueAsync("same", Request("shared", "same"), ct);
                var different = await router.EnqueueAsync("different", Request("other", "different"), ct);
                await Task.WhenAll(same.CompletionTask, different.CompletionTask).WaitAsync(ct);
                Assert.Equal("remote", same.Engine);
                Assert.Equal("remote", different.Engine);
                Assert.Equal(2, alternate.CompletionCalls);
                Assert.Contains(router.Events.Replay(0), entry => entry.TicketId == same.Id && entry.Kind == "request.selected" &&
                    entry.Reason == "engine_concurrency_limit (local)");
                Assert.False(held.CompletionTask.IsCompleted);
            }
            finally
            {
                router.Engines["remote"].BeginRequest(0);
                router.Engines["remote"].EndRequest(10_000);
                preferred.ReleaseGate();
            }
            await held.CompletionTask.WaitAsync(ct);
        }
        finally { preferred.ReleaseGate(); }
    }

    [Fact(Timeout = 20_000)]
    public async Task RouteLosingCapacityAtExactTakeUsesTheNextRouteOnlyOnce()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var first = new FakeEngineHandler { LoadedModels = ["shared"] };
        using var second = new FakeEngineHandler { LoadedModels = ["shared"] };
        await using var router = MakeRouter(first, second, null, ["shared"], ["shared"], maxSlots: 1);
        await ProbeAllAsync(router, ct);
        var ticket = await router.EnqueueAsync("caller", Request("shared", "original"), ct);
        var forced = false;
        router.BeforeAdmissionCommitForTests = _ =>
        {
            if (forced) return;
            forced = true;
            router.Engines["local"].BeginRequest(0);
        };
        try
        {
            Assert.True(router.TryDispatchOnce());
            await ticket.CompletionTask.WaitAsync(ct);
            Assert.Equal("remote", ticket.Engine);
            Assert.Equal("original", Assert.Single(second.ExecutionOrder));
            Assert.Equal(0, first.CompletionCalls);
            Assert.Single(router.Events.Replay(0), entry => entry.TicketId == ticket.Id && entry.Kind == "request.selected");
        }
        finally
        {
            if (forced) router.Engines["local"].EndRequest(0);
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task OneCapturedRoundPassesABlockedPrefixAndIgnoresNewArrivals()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var early = new FakeEngineHandler { LoadedModels = ["early"], NCtx = 128 };
        using var later = new FakeEngineHandler { LoadedModels = ["later"], NCtx = 128 };
        await using var router = MakeRouter(early, later, null, ["early"], ["later"], maxSlots: 1);
        await ProbeAllAsync(router, ct);
        var original = await router.EnqueueAsync("first", Request("early", "original"), ct);
        const int blockedCount = 40;
        for (var i = 0; i < blockedCount; i++)
            await router.EnqueueAsync("blocked-prefix", Request("later", "requires a larger window", context: 256), ct);
        var follower = await router.EnqueueAsync("independent", Request("later", "follower", context: 64), ct);
        RouterTicket? late = null;
        router.BeforeAdmissionCommitForTests = ticket =>
        {
            if (!ReferenceEquals(ticket, original)) return;
            late = EnqueueDuringCut(router, "first", Request("early", "late"), ct);
        };

        Assert.True(router.TryDispatchRound());
        await Task.WhenAll(original.CompletionTask, follower.CompletionTask).WaitAsync(ct);
        Assert.Equal("original", Assert.Single(early.ExecutionOrder));
        Assert.Equal("follower", Assert.Single(later.ExecutionOrder));
        Assert.Equal(TicketState.Queued, Assert.IsType<RouterTicket>(late).State);
        Assert.Equal(blockedCount, router.GetStats().QueueDepths["later"]);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactTakeRechecksANewOrNewlyAdmissibleEarlierCompetitor(bool releaseThread)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var one = new FakeEngineHandler { LoadedModels = ["model"] };
        using var two = new FakeEngineHandler { LoadedModels = [] };
        await using var router = MakeRouter(one, two, null, ["model"], [], maxSlots: 1);
        await ProbeAllAsync(router, ct);
        var earlier = router.RegisterThread("earlier", ThreadPriority.High());
        RouterTicket? predecessor = null;
        if (releaseThread)
        {
            earlier.IncrementInflight();
            predecessor = await router.EnqueueAsync(earlier.ThreadId, Request("model", "earlier"), ct);
        }
        var follower = await router.EnqueueAsync("follower", Request("model", "follower"), ct);
        router.BeforeAdmissionCommitForTests = ticket =>
        {
            Assert.Same(follower, ticket);
            if (releaseThread) earlier.DecrementInflight();
            else predecessor = EnqueueDuringCut(router, earlier.ThreadId, Request("model", "earlier"), ct);
        };

        Assert.False(router.TryDispatchRound());
        Assert.Equal(0, one.CompletionCalls);
        Assert.Equal(TicketState.Queued, follower.State);
        router.BeforeAdmissionCommitForTests = null;
        Assert.True(router.TryDispatchOnce());
        await Assert.IsType<RouterTicket>(predecessor).CompletionTask.WaitAsync(ct);
        Assert.Equal("earlier", Assert.Single(one.ExecutionOrder));
    }

    [Fact(Timeout = 20_000)]
    public async Task AnEarlierRouteBecomingReadyAfterTheProbeProtectsTheLastSharedThreadSlot()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = bound.Token;
        using var selected = new FakeEngineHandler { LoadedModels = ["second"] };
        using var other = new FakeEngineHandler { LoadedModels = [] };
        var clock = new ExactTakeClock();
        await using var router = MakeRouter(selected, other, null, ["second"], ["first"], maxSlots: 1, clock);
        await ProbeAllAsync(router, ct);
        var earlier = await router.EnqueueAsync("shared", Request("first", "earlier"), ct);
        var later = await router.EnqueueAsync("shared", Request("second", "later"), ct);
        using var cut = new ConfigReloadRaceTests.Cut();
        router.BeforeAdmissionCommitForTests = ticket =>
        {
            Assert.Same(later, ticket);
            clock.AtNextTimestamp = cut.Hold;
        };

        var dispatch = Task.Run(router.TryDispatchRound, ct);
        try
        {
            // TryTake timestamps the candidate after probing earlier contenders for this slot.
            await cut.Entered.Task.WaitAsync(ct);
            other.LoadedModels = ["first"];
            Assert.True(await router.Engines["remote"].ProbeHealthOnceAsync(ct));
            Assert.True(router.Engines["remote"].IsReadyFor("first", null));
        }
        finally { cut.Release(); }

        Assert.False(await dispatch.WaitAsync(ct));
        Assert.Equal(TicketState.Queued, later.State);
        Assert.Equal(0, selected.CompletionCalls);
        router.BeforeAdmissionCommitForTests = null;
        Assert.True(router.TryDispatchOnce());
        await earlier.CompletionTask.WaitAsync(ct);
        Assert.Equal("earlier", Assert.Single(other.ExecutionOrder));
    }

    private static RouterCore MakeRouter(FakeEngineHandler first, FakeEngineHandler second, LocalProvider? provider,
                                         string[] firstCatalog, string[] secondCatalog, int maxSlots, TimeProvider? clock = null)
    {
        var config = new RouterConfig
        {
            AutoSwitch = provider is not null,
            Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal)
            {
                ["local"] = Definition("local", firstCatalog, provider is not null),
                ["remote"] = Definition("remote", secondCatalog, managed: false),
            },
            Scheduler = new SchedulerConfig { MaxInflightPerEngine = maxSlots, PrefillCostAlpha = 1 },
            HttpHandlerFactory = engine => engine.Name == "local" ? first : second,
        };
        return new RouterCore(config, scheduler: null, events: null, clock ?? TimeProvider.System,
            lifecycleFactory: provider is null ? null : _ => provider);
    }

    private static EngineDefinition Definition(string name, string[] catalog, bool managed) => new()
    {
        Name = name,
        Endpoint = managed ? new Uri("http://127.0.0.1:12000/v1") : new Uri($"http://{name}.invalid/v1"),
        Remote = !managed,
        Lifecycle = managed ? EngineLifecycleKind.Docker : EngineLifecycleKind.Delegated,
        LifecycleOwner = managed ? EngineLifecycleOwner.Router : null,
        LifecycleGroup = managed ? "private-gpu" : null,
        Docker = managed ? new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" } : null,
        Health = new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 },
        Catalog = catalog.ToDictionary(model => model, model => new EngineModelDefinition { Name = model }, StringComparer.Ordinal),
    };

    private static InferenceRequest Request(string model, string text, long? context = null) => new()
    {
        Model = model,
        Body = Encoding.UTF8.GetBytes($$"""{"model":"{{model}}","messages":[{"role":"user","content":"{{text}}"}]}"""),
        Options = new RouterRequestOptions { RequiredContext = context },
    };

    private static async Task ProbeAllAsync(RouterCore router, CancellationToken ct)
    {
        foreach (var engine in router.Engines.Values)
            Assert.True(await engine.ProbeHealthOnceAsync(ct));
    }

    private static RouterTicket EnqueueDuringCut(RouterCore router, string thread, InferenceRequest request, CancellationToken ct)
    {
        var enqueue = router.EnqueueAsync(thread, request, ct);
        if (!enqueue.IsCompletedSuccessfully) throw new InvalidOperationException("The enqueue must complete inside the dispatch cut.");
        return enqueue.Result;
    }

    private static TaskCompletionSource NewBarrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ExactTakeClock : TimeProvider
    {
        internal Action? AtNextTimestamp;

        public override long GetTimestamp()
        {
            Interlocked.Exchange(ref AtNextTimestamp, null)?.Invoke();
            return base.GetTimestamp();
        }
    }

    private sealed class LocalProvider(TaskCompletionSource? entered = null, Action<LifecycleTarget>? onLaunch = null) : IEngineLifecycleProvider
    {
        private readonly TaskCompletionSource _release = NewBarrier();
        private int _launches;
        private int _stops;
        public string Kind => "docker";
        internal int Launches => Volatile.Read(ref _launches);
        internal int Stops => Volatile.Read(ref _stops);
        internal void Release() => _release.TrySetResult();
        public async ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
        {
            if (entered is not null)
            {
                entered.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
            return LifecycleResult.Ok();
        }
        public ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<EnginePeer>>([]);
        public ValueTask<LifecycleResult> StopAsync(LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
        {
            Interlocked.Increment(ref _stops);
            return ValueTask.FromResult(LifecycleResult.Ok());
        }

        public ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
        {
            Interlocked.Increment(ref _launches);
            onLaunch?.Invoke(target);
            return ValueTask.FromResult(LifecycleResult.Ok());
        }
        public ReadinessPolicy Readiness(LifecycleTarget target) => new(true, TimeSpan.FromSeconds(5));
    }
}
