using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Arboreal.InferenceRouter.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class OwnedPrivatePolicyTests
{
    private const string MutationPath = "/admin/policy/sensitivity";
    private const string ChatPath = "/v1/chat/completions";
    private const OwnedInferencePermission MutationScope = FullScope | OwnedInferencePermission.EscalateSensitivity;

    [Theory(Timeout = 35_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SensitivityFenceOrdersTheFinalHttpHandoffAndLeavesOtherGroupsLive(bool claimFirst, bool reachedUpstream)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = time.Token;
        var alphaCapture = new Capture();
        var betaCapture = new Capture();
        await using var alphaUpstream = alphaCapture.Build();
        await using var betaUpstream = betaCapture.Build();
        await alphaUpstream.StartAsync(ct);
        await betaUpstream.StartAsync(ct);
        var authority = new GroupAuthority();
        using var activeStore = await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, new Authority(),
            authority, initialize: true, ct);
        var verifier = new Verifier();
        verifier.Scopes["alpha-root"] = new("alpha", FullScope, newRootIntent: "root", allow: ["private"]);
        verifier.Scopes["beta-root"] = new("beta", FullScope, newRootIntent: "root", allow: ["second"]);
        var first = EngineUri(alphaUpstream);
        var second = EngineUri(betaUpstream);
        var config = Config(first);
        config.Engines = new Dictionary<string, EngineDefinition>(config.Engines, StringComparer.Ordinal)
        {
            ["second"] = new() { Name = "second", Endpoint = second, Catalog = config.Engines["private"].Catalog },
        };
        var composition = new OwnedPrivateServerComposition(activeStore,
            [Destination("private", first), Destination("second", second)], verifier.Verify);
        await using var app = ServerApp.BuildOwnedPrivate(["--urls=http://127.0.0.1:0"], composition, configOverride: config);
        await app.StartAsync(ct);
        using var direct = DirectHandler();
        using var client = ClientAt(app, direct);
        var (alpha, original) = await OpenRoot(client, "alpha-root", ct);
        var (beta, _) = await OpenRoot(client, "beta-root", ct);
        verifier.Scopes["escalate"] = new("alpha", MutationScope, existingGroup: alpha);
        verifier.Scopes["unbound-escalation"] = new("alpha", MutationScope, newRootIntent: "root");
        var alphaKey = new PolicyGroupKey("alpha", alpha);
        await WaitUntilAsync(() => activeStore.ReadEffectivePolicy(alphaKey)?.UnresolvedAttempts == 0, ct);
        using var forged = await Send(client, HttpMethod.Post, MutationPath, "alpha-root", null, null,
            Mutation("raise", original), ct);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        using var unbound = await Send(client, HttpMethod.Post, MutationPath, "unbound-escalation", null, null,
            Mutation("raise", original), ct);
        Assert.Equal(HttpStatusCode.Forbidden, unbound.StatusCode);
        using var lowering = await Send(client, HttpMethod.Post, MutationPath, "escalate", alpha, null,
            Mutation("lower", original, sensitive: false), ct);
        Assert.Equal(HttpStatusCode.Forbidden, lowering.StatusCode);
        using var wrongGroup = await Send(client, HttpMethod.Post, MutationPath, "escalate", beta, null,
            Mutation("raise", original), ct);
        Assert.Equal(HttpStatusCode.Forbidden, wrongGroup.StatusCode);

        var reached = new TaskCompletionSource<OwnedPrivateAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (claimFirst) composition.Policy.AfterHandoffClaimForTests = Hold;
        else composition.Policy.BeforeHandoffForTests = Hold;
        if (reachedUpstream) alphaCapture.HoldNextInference();
        var request = Send(client, HttpMethod.Post, ChatPath, "alpha-root", null, "derived", claimFirst ? Stream : Chat, ct);
        TaskCompletionSource? finishCommit = null;
        try
        {
            var attempt = await reached.Task.WaitAsync(ct);
            if (reachedUpstream) await alphaCapture.InferenceEntered.Task.WaitAsync(ct);
            var expectedSends = reachedUpstream ? 2 : 1;
            var hold = authority.HoldNext(alpha);
            finishCommit = hold.Release;
            Assert.NotNull(authority.ReadConfirmed(alpha));
            Assert.Equal(claimFirst, attempt.Consumed);
            Assert.Equal(expectedSends, alphaCapture.Inferences.Count);
            var raise = Send(client, HttpMethod.Post, MutationPath, "escalate", alpha, null, Mutation("raise", original), ct);
            await hold.Entered.Task.WaitAsync(ct);
            Assert.False(raise.IsCompleted);
            if (reachedUpstream) await alphaCapture.InferenceAborted.Task.WaitAsync(ct);
            Assert.Equal("pending", activeStore.ReadEffectivePolicy(alphaKey)?.AdmissionState);
            using var racing = await Send(client, HttpMethod.Post, ChatPath, "alpha-root", null, null, Chat, ct);
            Assert.Equal(HttpStatusCode.Forbidden, racing.StatusCode);
            var engine = app.Services.GetRequiredService<RouterCore>().CurrentGeneration.Engines["private"];
            var modelsBefore = alphaCapture.ModelsProbes;
            var servingBefore = alphaCapture.ServingProbes;
            Assert.True(await engine.ProbeHealthOnceAsync(ct));
            Assert.True(alphaCapture.ModelsProbes > modelsBefore);
            Assert.True(alphaCapture.ServingProbes > servingBefore);
            using var fakeService = new HttpRequestMessage(HttpMethod.Get, new Uri(first, "/models"));
            var refusedService = await Assert.ThrowsAsync<RouterRequestException>(() => engine.InferenceHttp.SendAsync(fakeService, ct));
            Assert.Equal("policy_route_denied", refusedService.Code);
            Assert.Equal(expectedSends, alphaCapture.Inferences.Count);
            using var other = await Send(client, HttpMethod.Post, ChatPath, "beta-root", null, null, Chat, ct);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            Assert.Equal(2, betaCapture.Inferences.Count);
            finishCommit.TrySetResult();
            using var raised = await raise;
            Assert.Equal(HttpStatusCode.OK, raised.StatusCode);
            using var result = JsonDocument.Parse(await raised.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(original + 1, result.RootElement.GetProperty("Revision").GetInt64());
            Assert.Equal(claimFirst ? 1 : 0, result.RootElement.GetProperty("PossibleExposures").GetInt32());
            Assert.True(result.RootElement.GetProperty("CurrentSensitive").GetBoolean());
            release.TrySetResult();
            using var finished = await request;
            if (!claimFirst) Assert.Equal(HttpStatusCode.Forbidden, finished.StatusCode);
            Assert.Equal(expectedSends, alphaCapture.Inferences.Count);

            var current = await ReadTicket(client, "alpha-root", attempt.Record.Ticket, ct);
            Assert.Equal(original, current.GetProperty("AttemptPolicyRevision").GetInt64());
            Assert.Equal(original + 1, current.GetProperty("CurrentPolicyRevision").GetInt64());
            Assert.Equal(claimFirst, current.GetProperty("PrivateAttemptMayHaveSent").GetBoolean());
            Assert.True(current.GetProperty("CurrentSensitivity").GetBoolean());
            Assert.Equal("confirmed", current.GetProperty("PolicyAdmissionState").GetString());
            using var repeated = await Send(client, HttpMethod.Post, MutationPath, "escalate", alpha, null,
                Mutation("raise", original), ct);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.Equal(result.RootElement.GetRawText(), await repeated.Content.ReadAsStringAsync(ct));
            using var changedInputs = await Send(client, HttpMethod.Post, MutationPath, "escalate", alpha, null,
                Mutation("raise", original + 1), ct);
            Assert.Equal(HttpStatusCode.Conflict, changedInputs.StatusCode);
            using var renewed = await Send(client, HttpMethod.Post, "/v1/completions", "alpha-root", null, null, Text, ct);
            Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
            var next = await ReadTicket(client, "alpha-root", renewed.Headers.GetValues("X-Inference-Ticket").Single(), ct);
            Assert.Equal(original + 1, next.GetProperty("AttemptPolicyRevision").GetInt64());
            Assert.True(next.GetProperty("Sensitive").GetBoolean());
            Assert.Equal(expectedSends + 1, alphaCapture.Inferences.Count);
            Assert.Equal("/v1/completions", alphaCapture.Inferences.Last().Path);
        }
        finally
        {
            release.TrySetResult();
            alphaCapture.AllowInference.TrySetResult();
            finishCommit?.TrySetResult();
        }

        async Task Hold(OwnedPrivateAttempt attempt)
        {
            if (attempt.Key != alphaKey) return;
            reached.TrySetResult(attempt);
            if (!reachedUpstream) await release.Task.WaitAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task LostGroupConfirmationRecoversTheConfirmedOperationWithoutReplayingIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = time.Token;
        var roots = new Authority();
        var groups = new GroupAuthority();
        var caller = new VerifiedInferenceScope("alpha", MutationScope, newRootIntent: "root");
        var other = new VerifiedInferenceScope("beta", FullScope, newRootIntent: "root");
        OwnedPolicyGroup original;
        using (var store = await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, roots, groups, initialize: true, ct))
        {
            original = Assert.IsType<OwnedPolicyGroup>(await store.ResolveAsync(caller, null, true, ct));
            groups.LoseConfirmation = original.Group;
            var failed = await Assert.ThrowsAsync<RouterRequestException>(() =>
                store.EscalateAsync(original, "retryable", original.Revision, ct).AsTask());
            Assert.Equal("policy_group_unavailable", failed.Code);
            Assert.False(store.IsCurrent(original));
            Assert.Equal("uncertain", store.ReadEffectivePolicy(original.Key)?.AdmissionState);
            var beta = Assert.IsType<OwnedPolicyGroup>(await store.ResolveAsync(other, null, true, ct));
            Assert.True(store.IsCurrent(beta));
            Assert.True(File.Exists(StatePath + "." + original.Group + ".proposed"));
        }
        await Assert.ThrowsAsync<IOException>(() => OwnedPolicyStore.OpenAsync(StatePath, roots, cancellationToken: ct));
        using var restored = await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, roots, groups, cancellationToken: ct);
        var recovered = Assert.IsType<OwnedPolicyGroup>(await restored.ResolveAsync(caller, null, false, ct));
        Assert.True(recovered.Sensitive);
        Assert.Equal(original.Revision + 1, recovered.Revision);
        var beforeRetry = groups.ReadConfirmed(original.Group);
        var outcome = await restored.EscalateAsync(recovered, "retryable", original.Revision, ct);
        Assert.Equal(beforeRetry, groups.ReadConfirmed(original.Group));
        Assert.Equal(recovered.Revision, outcome.Revision);
        Assert.Equal(recovered.Revision, outcome.CurrentRevision);
        Assert.False(File.Exists(StatePath + "." + original.Group + ".proposed"));
    }

    [Fact(Timeout = 25_000)]
    public async Task LostCommonRootConfirmationClosesAllGroupsUntilRootRecovery()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var time = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = time.Token;
        var roots = new Authority();
        var groups = new GroupAuthority();
        var alpha = new VerifiedInferenceScope("alpha", MutationScope, newRootIntent: "root");
        var beta = new VerifiedInferenceScope("beta", FullScope, newRootIntent: "root");
        using (var store = await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, roots, groups, initialize: true, ct))
        {
            var previous = Assert.IsType<OwnedPolicyGroup>(await store.ResolveAsync(alpha, null, true, ct));
            var confirmedRoot = await File.ReadAllBytesAsync(StatePath, ct);
            roots.LoseConfirmationOnCommit = true;
            Assert.Null(await store.ResolveAsync(beta, null, true, ct));
            Assert.False(store.Available);
            Assert.False(store.IsCurrent(previous));
            Assert.Equal(confirmedRoot, await File.ReadAllBytesAsync(StatePath, ct));
            Assert.True(File.Exists(StatePath + ".root-proposed"));
        }
        using var reopened = await OwnedPolicyStore.OpenWithGroupAuthorityAsync(StatePath, roots, groups, cancellationToken: ct);
        Assert.NotNull(await reopened.ResolveAsync(beta, null, false, ct));
        Assert.NotNull(await reopened.ResolveAsync(alpha, null, false, ct));
        Assert.False(File.Exists(StatePath + ".root-proposed"));
    }

    private static PrivateDestinationEnrollment Destination(string name, Uri endpoint) =>
        new(name, endpoint, "/models", "/props", PrivateDestinationCapability.Chat | PrivateDestinationCapability.Text);

    private static string Mutation(string operation, long revision, bool sensitive = true) => FormattableString.Invariant(
        $$"""{"operation":"{{operation}}","expectedRevision":{{revision}},"sensitive":{{(sensitive ? "true" : "false")}}}""");

    private static async Task<(string Id, long Revision)> OpenRoot(HttpClient client, string credential, CancellationToken ct)
    {
        using var response = await Send(client, HttpMethod.Post, ChatPath, credential, null, "root", Chat, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = await ReadTicket(client, credential, response.Headers.GetValues("X-Inference-Ticket").Single(), ct);
        return (response.Headers.GetValues(GroupHeader).Single(), ticket.GetProperty("AttemptPolicyRevision").GetInt64());
    }

    private static async Task<JsonElement> ReadTicket(HttpClient client, string credential, string ticket, CancellationToken ct)
    {
        using var response = await Send(client, HttpMethod.Get, "/admin/tickets/" + ticket, credential, null, null, null, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        return body.RootElement.Clone();
    }

    private sealed class GroupAuthority : IOwnedPolicyGroupHeadAuthority
    {
        private readonly ConcurrentDictionary<string, OwnedPolicyHead> _heads = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (TaskCompletionSource Entered, TaskCompletionSource Release, bool IgnoreCancellation)> _holds =
            new(StringComparer.Ordinal);
        internal string? LoseConfirmation;

        internal OwnedPolicyHead? ReadConfirmed(string group) => _heads.GetValueOrDefault(group);

        internal (TaskCompletionSource Entered, TaskCompletionSource Release) HoldNext(string group, bool ignoreCancellation = false)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_holds.TryAdd(group, (entered, release, ignoreCancellation)));
            return (entered, release);
        }

        public ValueTask<OwnedPolicyHead?> ReadAsync(Guid store, string owner, string group, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ReadConfirmed(group));

        public async ValueTask<bool> CompareAndCommitAsync(Guid store, string owner, string group, OwnedPolicyHead? previous,
            OwnedPolicyHead proposed, CancellationToken cancellationToken)
        {
            if (_holds.TryRemove(group, out var hold))
            {
                hold.Entered.TrySetResult();
                await hold.Release.Task.WaitAsync(hold.IgnoreCancellation ? CancellationToken.None : cancellationToken);
            }
            var committed = previous is null ? _heads.TryAdd(group, proposed) : _heads.TryUpdate(group, proposed, previous);
            if (committed && LoseConfirmation == group)
            {
                LoseConfirmation = null;
                throw new IOException("fixture lost group confirmation");
            }
            return committed;
        }
    }
}
