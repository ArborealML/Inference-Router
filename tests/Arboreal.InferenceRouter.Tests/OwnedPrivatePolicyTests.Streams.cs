using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class OwnedPrivatePolicyTests
{
    [Theory(Timeout = 25_000)]
    [InlineData("fault")]
    [InlineData("slow")]
    [InlineData("caller")]
    public async Task OwnedStreamRetiresUpstreamAndKeepsOneTerminalOutcome(string mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var ct = deadline.Token;
        var wroteEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamAborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishBackend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = new string('x', mode == "slow" ? 512 * 1024 : 32);
        var capture = new Capture { StreamResponse = async context =>
        {
            try
            {
                await context.Response.WriteAsync("data: {\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" +
                    payload + "\"}}]}\n\n", context.RequestAborted);
                wroteEvent.TrySetResult();
                await finishBackend.Task.WaitAsync(context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                upstreamAborted.TrySetResult();
            }
        } };
        await using var upstream = capture.Build();
        await upstream.StartAsync(ct);
        using var store = await OwnedPolicyStore.OpenAsync(StatePath, new Authority(), initialize: true, ct);
        var endpoint = EngineUri(upstream);
        // Explicit credit isolates the old cancellation defect from the separate default-credit defect.
        var config = Config(endpoint, streamBufferLimit: mode == "fault" ? 4096 : 0,
            upstreamIdleMs: mode == "fault" ? 60_000 : 0);
        await using var app = BuildRouter(store, new Verifier(), endpoint, config);
        await app.StartAsync(ct);
        var router = app.Services.GetRequiredService<RouterCore>();
        var upstreamClock = new ReadinessDeadlineTests.ManualClock();
        router.StreamIdleClockForTests = upstreamClock;
        var cleaned = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.AfterOptInTransportTaskForTests = id => cleaned.TrySetResult(id);
        var scope = new VerifiedInferenceScope("alpha", FullScope, newRootIntent: "stream");
        var group = await router.ResolveOwnedPolicyAsync(scope, null, OwnedInferencePermission.Inference, true, ct);
        var ticket = await router.EnqueueOwnedHttpAsync("owned-stream", new InferenceRequest
            { Model = Model, Body = Encoding.UTF8.GetBytes(Stream), Stream = true, Options = new RouterRequestOptions() },
            new OwnedTicketPolicy(group, scope, PrivateDestinationCapability.Chat), caller.Token);

        var clock = new ReadinessDeadlineTests.ManualClock();
        var context = new DefaultHttpContext();
        var lifetime = new StreamRelayTests.PassiveLifetime { RequestAborted = caller.Token };
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var writer = new StreamRelayTests.GatedWriter(blockWrite: 2);
        context.Features.Set<IHttpResponseBodyFeature>(new StreamRelayTests.BodyFeature(writer));
        try
        {
            var delivery = Assert.IsType<HttpStreamDelivery>(ticket.HttpDelivery);
            var relay = ServerApp.StreamTicketAsync(context, ticket, clock, router);
            await wroteEvent.Task.WaitAsync(ct);
            await writer.Blocked.Task.WaitAsync(ct);
            Assert.False(delivery.Sealed.IsCompleted);
            if (mode == "slow")
            {
                await WaitUntilAsync(() => delivery.ReservedBytes == 262_144 && clock.ActiveTimers == 1, ct);
                clock.AdvanceTo(29_999);
                Assert.False(ticket.CompletionTask.IsCompleted);
                clock.AdvanceTo(30_001);
            }
            else if (mode == "caller")
                await caller.CancelAsync();
            else
            {
                // Observe the pending upstream read before losing the writer. Discarding the
                // output queue alone cannot wake a producer already waiting on its socket.
                await WaitUntilAsync(() => upstreamClock.ActiveTimers == 1, ct);
                writer.Release.TrySetException(new InvalidOperationException("scripted owned writer fault"));
            }

            await relay.WaitAsync(ct);
            var failure = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
            Assert.Equal(mode == "slow" ? "slow_consumer" : "cancelled", failure.Code);
            Assert.Equal(mode == "caller", caller.IsCancellationRequested);
            Assert.True(lifetime.Aborted);
            await upstreamAborted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(ticket.Id, await cleaned.Task.WaitAsync(ct));
            await WaitUntilAsync(() => delivery.ReservedBytes == 0, ct);
            var terminal = Assert.Single(router.EventFeed.Replay(0), evt => evt.TicketId == ticket.Id &&
                evt.Kind is "request.completed" or "request.failed" or "request.cancelled");
            Assert.Equal(group.Key, terminal.OwnerGroup);
            Assert.Equal(mode == "slow" ? "request.failed" : "request.cancelled", terminal.Kind);
        }
        finally
        {
            writer.Release.TrySetResult(new FlushResult(true, false));
            router.CancelTicket(ticket);
            finishBackend.TrySetResult();
        }
    }

    [Theory(Timeout = 25_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnersOnTheSameEngineSeeOwnActivityOrConfiguredAvailability(bool checkStatus)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var capture = new Capture { ReportExtraModel = true };
        await using var upstream = capture.Build();
        await upstream.StartAsync(ct);
        using var store = await OwnedPolicyStore.OpenAsync(StatePath, new Authority(), initialize: true, ct);
        var verifier = new Verifier();
        verifier.Scopes["alpha"] = new("alpha", FullScope, newRootIntent: "root");
        verifier.Scopes["beta"] = new("beta", FullScope, newRootIntent: "root");
        var endpoint = EngineUri(upstream);
        await using var app = BuildRouter(store, verifier, endpoint, Config(endpoint, extraModel: true));
        await app.StartAsync(ct);
        using var direct = DirectHandler();
        using var client = ClientAt(app, direct);

        using var alpha = await Send(client, HttpMethod.Post, "/v1/chat/completions", "alpha", null, "alpha-thread", Chat, ct);
        using var beta = await Send(client, HttpMethod.Post, "/v1/chat/completions", "beta", null, "beta-thread", Chat, ct);
        Assert.Equal(HttpStatusCode.OK, alpha.StatusCode);
        Assert.Equal(HttpStatusCode.OK, beta.StatusCode);
        capture.HoldNextInference();
        var active = Send(client, HttpMethod.Post, "/v1/chat/completions", "alpha", null, "alpha-thread", Chat, ct);
        try
        {
            await capture.InferenceEntered.Task.WaitAsync(ct);
            if (checkStatus)
            {
                await CheckStatus("alpha", 1, "alpha-thread");
                await CheckStatus("beta", 0, "beta-thread");
            }
            else
            {
                await CheckAvailability("alpha");
                await CheckAvailability("beta");
            }
        }
        finally { capture.AllowInference.TrySetResult(); }
        using var finished = await active;
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);

        async Task CheckStatus(string identity, int inflight, string thread)
        {
            using var response = await Send(client, HttpMethod.Get, "/admin/status", identity, null, null, null, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            var engine = Assert.Single(document.RootElement.GetProperty("Engines").EnumerateArray());
            Assert.Equal("private", engine.GetProperty("Engine").GetString());
            Assert.Equal(inflight, engine.GetProperty("Inflight").GetInt32());
            Assert.Equal(["Engine", "Inflight"], engine.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
            var ownedThread = Assert.Single(document.RootElement.GetProperty("Threads").EnumerateArray());
            Assert.Equal(thread, ownedThread.GetProperty("ThreadId").GetString());
        }

        async Task CheckAvailability(string identity)
        {
            using var models = await Send(client, HttpMethod.Get, "/v1/models", identity, null, null, null, ct);
            Assert.Equal(HttpStatusCode.OK, models.StatusCode);
            using var modelDocument = JsonDocument.Parse(await models.Content.ReadAsByteArrayAsync(ct));
            var rows = modelDocument.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.Equal(["configured-only", Model], rows.Select(static row => row.GetProperty("id").GetString()).Order(StringComparer.Ordinal));
            Assert.All(rows, row => Assert.False(row.TryGetProperty("context_window", out _)));

            using var catalog = await Send(client, HttpMethod.Get, "/admin/catalog", identity, null, null, null, ct);
            Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
            var content = await catalog.Content.ReadAsStringAsync(ct);
            Assert.Contains("configured-only", content, StringComparison.Ordinal);
            Assert.DoesNotContain("unconfigured-resident", content, StringComparison.Ordinal);
            Assert.DoesNotContain("ActiveSlots", content, StringComparison.Ordinal);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> done, CancellationToken cancellationToken)
    {
        while (!done()) await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
    }
}
