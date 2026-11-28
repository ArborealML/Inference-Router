using System.Net;
using Arboreal.InferenceRouter.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class OwnedPrivatePolicyTests
{
    [Theory(Timeout = 32_000)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task StalledOwnerJournalDoesNotHoldTheSharedPhysicalSlot(bool preSend, bool stream)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(23));
        var ct = deadline.Token;
        var capture = new Capture();
        await using var upstream = capture.Build();
        await upstream.StartAsync(ct);
        var roots = new Authority();
        var groups = new GroupAuthority();
        using var store = await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, roots, groups, initialize: true, ct);
        var verifier = new Verifier();
        verifier.Scopes["alpha"] = new("alpha", MutationScope, newRootIntent: "root");
        verifier.Scopes["beta"] = new("beta", FullScope, newRootIntent: "root");
        var endpoint = EngineUri(upstream);
        var config = Config(endpoint);
        config.Scheduler.MaxInflightPerEngine = 1;
        await using var app = BuildRouter(store, verifier, endpoint, config);
        await app.StartAsync(ct);
        using var direct = DirectHandler();
        using var client = ClientAt(app, direct);
        var (alpha, _) = await OpenRoot(client, "alpha", ct);
        var (beta, _) = await OpenRoot(client, "beta", ct);
        var alphaKey = new PolicyGroupKey("alpha", alpha);
        var betaKey = new PolicyGroupKey("beta", beta);
        await WaitUntilAsync(() => store.ReadEffectivePolicy(alphaKey)?.UnresolvedAttempts == 0 &&
            store.ReadEffectivePolicy(betaKey)?.UnresolvedAttempts == 0, ct);
        var router = app.Services.GetRequiredService<RouterCore>();
        var engine = router.CurrentGeneration.Engines["private"];
        TaskCompletionSource? releaseCommit = null;
        try
        {
            Task<HttpResponseMessage> active;
            Task<HttpResponseMessage> other;
            if (preSend)
            {
                var hold = groups.HoldNext(alpha, ignoreCancellation: true);
                releaseCommit = hold.Release;
                active = Send(client, HttpMethod.Post, ChatPath, "alpha", null, "stalled", Chat, ct);
                await hold.Entered.Task.WaitAsync(ct);
                Assert.Equal(0, engine.Inflight);
                Assert.Equal(2, capture.Inferences.Count);
                Assert.False(active.IsCompleted);
                other = Send(client, HttpMethod.Post, ChatPath, "beta", null, "progress", Chat, ct);
            }
            else
            {
                capture.HoldNextInference();
                active = Send(client, HttpMethod.Post, ChatPath, "alpha", null, "stalled", stream ? Stream : Chat, ct);
                await capture.InferenceEntered.Task.WaitAsync(ct);
                var hold = groups.HoldNext(alpha, ignoreCancellation: true);
                releaseCommit = hold.Release;
                other = Send(client, HttpMethod.Post, ChatPath, "beta", null, "progress", Chat, ct);
                await WaitUntilAsync(() => router.EventFeed.Replay(0).Any(static item => item.Kind == "request.enqueued" &&
                    item.ThreadId == "progress"), ct);
                Assert.Equal(1, engine.Inflight);
                Assert.Equal(3, capture.Inferences.Count);
                Assert.False(other.IsCompleted);
                capture.AllowInference.TrySetResult();
                await hold.Entered.Task.WaitAsync(ct);
                using var completed = await active;
                Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
                if (stream) Assert.Contains("data: [DONE]", await completed.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            }

            using var progressed = await other.WaitAsync(TimeSpan.FromSeconds(6), ct);
            Assert.Equal(HttpStatusCode.OK, progressed.StatusCode);
            Assert.False(releaseCommit.Task.IsCompleted);
            Assert.Equal(preSend ? 3 : 4, capture.Inferences.Count);
            if (preSend) Assert.False(active.IsCompleted);
            await WaitUntilAsync(() => store.ReadEffectivePolicy(betaKey)?.UnresolvedAttempts == 0, ct);
            await router.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct);
            if (preSend)
            {
                using var cancelled = await active;
                Assert.Equal((HttpStatusCode)499, cancelled.StatusCode);
            }
            store.Dispose();
            await Assert.ThrowsAsync<IOException>(() => OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, roots, groups,
                cancellationToken: ct));
            releaseCommit.TrySetResult();
            using var reopened = await ReopenAfterRetirement();
            Assert.NotNull(await reopened.ResolveAsync(verifier.Scopes["alpha"], null, false, ct));
            Assert.Equal(preSend ? 1 : 0, reopened.ReadEffectivePolicy(alphaKey)?.UnresolvedAttempts);
        }
        finally
        {
            capture.AllowInference.TrySetResult();
            releaseCommit?.TrySetResult();
        }

        async Task<OwnedPolicyStore> ReopenAfterRetirement()
        {
            while (true)
            {
                try { return await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, roots, groups, cancellationToken: ct); }
                catch (IOException) { await Task.Delay(TimeSpan.FromMilliseconds(10), ct); }
            }
        }
    }
    [Fact(Timeout = 25_000)]
    public async Task FullPreparedRouteTakesAFreshIntentForTheAvailableEngine()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(17));
        var ct = deadline.Token;
        var first = new Capture();
        var second = new Capture();
        await using var firstUpstream = first.Build();
        await using var secondUpstream = second.Build();
        await firstUpstream.StartAsync(ct);
        await secondUpstream.StartAsync(ct);
        var endpoint = EngineUri(firstUpstream);
        var secondEndpoint = EngineUri(secondUpstream);
        var config = Config(endpoint);
        config.Scheduler.MaxInflightPerEngine = 1;
        config.Engines = new Dictionary<string, EngineDefinition>(config.Engines, StringComparer.Ordinal)
        {
            ["second"] = new() { Name = "second", Endpoint = secondEndpoint, Catalog = config.Engines["private"].Catalog },
        };
        using var store = await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, new Authority(), new GroupAuthority(),
            initialize: true, ct);
        var verifier = new Verifier();
        verifier.Scopes["alpha"] = new("alpha", FullScope, newRootIntent: "root");
        var composition = new OwnedPrivateServerComposition(store, [Destination("private", endpoint), Destination("second", secondEndpoint)],
            verifier.Verify);
        await using var app = ServerApp.BuildOwnedPrivate(["--urls=http://127.0.0.1:0"], composition, configOverride: config);
        await app.StartAsync(ct);
        using var direct = DirectHandler();
        using var client = ClientAt(app, direct);
        var router = app.Services.GetRequiredService<RouterCore>();
        foreach (var engine in router.CurrentGeneration.Engines.Values) Assert.True(await engine.ProbeHealthOnceAsync(ct));
        OwnedPrivateAttempt? blocked = null;
        var reached = new TaskCompletionSource<OwnedPrivateAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeforeAdmissionCommitForTests = ticket =>
        {
            if (blocked is not null) return;
            blocked = ticket.PrivatePreparation!.Attempt!;
            blocked.Engine.BeginRequest(0);
        };
        composition.Policy.BeforeHandoffForTests = attempt =>
        {
            reached.TrySetResult(attempt);
            return Task.CompletedTask;
        };
        try
        {
            using var delivered = await Send(client, HttpMethod.Post, ChatPath, "alpha", null, "reroute", Chat, ct);
            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            var accepted = await reached.Task.WaitAsync(ct);
            var original = Assert.IsType<OwnedPrivateAttempt>(blocked);
            Assert.Equal(original.Record.Ticket, accepted.Record.Ticket);
            Assert.NotEqual(original.Record.Attempt, accepted.Record.Attempt);
            Assert.NotSame(original.Engine, accepted.Engine);
            Assert.False(original.Consumed);
            Assert.True(accepted.Consumed);
            Assert.Empty((original.Engine.Name == "private" ? first : second).Inferences);
            Assert.Single((accepted.Engine.Name == "private" ? first : second).Inferences);
            var key = new PolicyGroupKey("alpha", delivered.Headers.GetValues(GroupHeader).Single());
            await WaitUntilAsync(() => store.ReadEffectivePolicy(key)?.UnresolvedAttempts == 0, ct);
        }
        finally { blocked?.Engine.EndRequest(0); }
    }

}
