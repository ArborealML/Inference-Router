using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Arboreal.InferenceRouter.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class OwnedPrivatePolicyTests
{
    [Theory(Timeout = 25_000)]
    [InlineData("body")]
    [InlineData("same_length_body")]
    [InlineData("thread")]
    [InlineData("content_type")]
    [InlineData("authority")]
    public async Task MutationAcrossTheFinalHandoffRefusesBeforeAnyUpstreamInference(string mutation)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var capture = new Capture();
        await using var upstream = capture.Build();
        await upstream.StartAsync(ct);
        using var store = await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, new Authority(), new GroupAuthority(),
            initialize: true, ct);
        var verifier = new Verifier();
        verifier.Scopes["alpha"] = new("alpha", MutationScope, newRootIntent: "root");
        var endpoint = EngineUri(upstream);
        var composition = new OwnedPrivateServerComposition(store,
            [new("private", endpoint, "/models", "/props", PrivateDestinationCapability.Chat | PrivateDestinationCapability.ThreadCache)],
            verifier.Verify);
        composition.Policy.BeforeHandoffForTests = attempt =>
        {
            var request = attempt.Request;
            switch (mutation)
            {
                case "body":
                    request.Content?.Dispose();
                    request.Content = new ByteArrayContent("altered"u8.ToArray());
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    break;
                case "same_length_body":
                    attempt.AlterProjectedBodyForTests();
                    break;
                case "thread":
                    Assert.True(request.Headers.Remove("X-Inference-Thread"));
                    request.Headers.Add("X-Inference-Thread", "altered");
                    break;
                case "content_type":
                    request.Content!.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                    break;
                case "authority":
                    request.Headers.Host = "other.invalid";
                    break;
            }
            return Task.CompletedTask;
        };
        await using var app = ServerApp.BuildOwnedPrivate(["--urls=http://127.0.0.1:0"], composition, configOverride: Config(endpoint));
        await app.StartAsync(ct);
        using var direct = DirectHandler();
        using var client = ClientAt(app, direct);
        using var refused = await Send(client, HttpMethod.Post, ChatPath, "alpha", null, "named", Chat, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("policy_route_denied", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        var ticket = await ReadTicket(client, "alpha", refused.Headers.GetValues("X-Inference-Ticket").Single(), ct);
        Assert.False(ticket.GetProperty("PrivateAttemptMayHaveSent").GetBoolean());
        Assert.Empty(capture.Inferences);
        var key = new PolicyGroupKey("alpha", refused.Headers.GetValues(GroupHeader).Single());
        await WaitUntilAsync(() => store.ReadEffectivePolicy(key)?.UnresolvedAttempts == 0, ct);
    }

    [Fact(Timeout = 25_000)]
    public async Task UpstreamSocketClosedAfterReadingTheBodyRetainsOneUnresolvedIntent()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var capture = new Capture();
        await using var upstream = capture.Build();
        await upstream.StartAsync(ct);
        using var store = await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, new Authority(), new GroupAuthority(),
            initialize: true, ct);
        var verifier = new Verifier();
        verifier.Scopes["alpha"] = new("alpha", MutationScope, newRootIntent: "root");
        var endpoint = EngineUri(upstream);
        await using var app = BuildRouter(store, verifier, endpoint);
        await app.StartAsync(ct);
        using var direct = DirectHandler();
        using var client = ClientAt(app, direct);
        var (alpha, _) = await OpenRoot(client, "alpha", ct);
        var key = new PolicyGroupKey("alpha", alpha);
        await WaitUntilAsync(() => store.ReadEffectivePolicy(key)?.UnresolvedAttempts == 0, ct);
        capture.DropNextResponseAfterBody();
        using var lost = await Send(client, HttpMethod.Post, ChatPath, "alpha", null, null, Chat, ct);
        Assert.Equal(HttpStatusCode.BadGateway, lost.StatusCode);
        var ticket = await ReadTicket(client, "alpha", lost.Headers.GetValues("X-Inference-Ticket").Single(), ct);
        Assert.True(ticket.GetProperty("PrivateAttemptMayHaveSent").GetBoolean());
        Assert.Equal(1, capture.DroppedResponses);
        Assert.Equal(2, capture.Inferences.Count);
        Assert.Equal(1, store.ReadEffectivePolicy(key)?.UnresolvedAttempts);
        using var fresh = await Send(client, HttpMethod.Post, ChatPath, "alpha", null, null, Chat, ct);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(3, capture.Inferences.Count);
        await WaitUntilAsync(() => store.ReadEffectivePolicy(key)?.UnresolvedAttempts == 1, ct);
    }

    [Fact(Timeout = 45_000)]
    public async Task AmbiguousIntentCeilingQuarantinesOneGroupAndSurvivesRestore()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var ct = deadline.Token;
        var capture = new Capture();
        await using var upstream = capture.Build();
        await upstream.StartAsync(ct);
        var roots = new Authority();
        var groups = new GroupAuthority();
        var alphaScope = new VerifiedInferenceScope("alpha", FullScope, newRootIntent: "root");
        var verifier = new Verifier();
        verifier.Scopes["alpha"] = alphaScope;
        verifier.Scopes["beta"] = new("beta", FullScope, newRootIntent: "root");
        PolicyGroupKey key;
        using (var store = await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, roots, groups, initialize: true, ct))
        {
            var endpoint = EngineUri(upstream);
            await using var app = BuildRouter(store, verifier, endpoint);
            await app.StartAsync(ct);
            using var direct = DirectHandler();
            using var client = ClientAt(app, direct);
            var (alpha, _) = await OpenRoot(client, "alpha", ct);
            key = new("alpha", alpha);
            await WaitUntilAsync(() => store.ReadEffectivePolicy(key)?.UnresolvedAttempts == 0, ct);
            capture.RefuseInferences(128);
            for (var index = 0; index < 128; index++)
            {
                using var failed = await Send(client, HttpMethod.Post, ChatPath, "alpha", null, null, Chat, ct);
                Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
            }
            using var blocked = await Send(client, HttpMethod.Post, ChatPath, "alpha", null, null, Chat, ct);
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.Contains("policy_journal_quarantined", await blocked.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            Assert.Equal(129, capture.Inferences.Count);
            using var status = await Send(client, HttpMethod.Get, "/admin/status", "alpha", null, null, null, ct);
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            using var json = JsonDocument.Parse(await status.Content.ReadAsByteArrayAsync(ct));
            var effective = json.RootElement.GetProperty("Policy").GetProperty("Effective");
            Assert.Equal("quarantined", effective.GetProperty("AdmissionState").GetString());
            Assert.Equal("external_attempt_reconciliation", effective.GetProperty("RecoveryRequired").GetString());
            Assert.Equal(128, effective.GetProperty("UnresolvedAttempts").GetInt32());
            Assert.Equal(128, effective.GetProperty("MaximumUnresolvedAttempts").GetInt32());
            var (beta, _) = await OpenRoot(client, "beta", ct);
            await WaitUntilAsync(() => store.ReadEffectivePolicy(new("beta", beta))?.UnresolvedAttempts == 0, ct);
            var router = app.Services.GetRequiredService<RouterCore>();
            await router.DisposeAsync();
        }
        using var reopened = await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, roots, groups, cancellationToken: ct);
        Assert.NotNull(await reopened.ResolveAsync(alphaScope, null, false, ct));
        Assert.Equal("quarantined", reopened.ReadEffectivePolicy(key)?.AdmissionState);
        Assert.Equal(128, reopened.ReadEffectivePolicy(key)?.UnresolvedAttempts);
        Assert.Equal(130, capture.Inferences.Count);
    }
}
