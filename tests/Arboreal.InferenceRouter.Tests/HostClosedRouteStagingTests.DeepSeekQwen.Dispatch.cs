using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    private static readonly Uri MayaPairEndpoint = new("http://127.0.0.1:28060/v1");
    private const long PairCredentialEpoch = 1;

    private static async Task CheckDeepSeekPairProbesAsync(EngineDefinition maya, CancellationToken ct)
    {
        Assert.Equal(MayaPairEndpoint, maya.Endpoint);
        Assert.Equal("/v1/models", maya.ModelsEndpoint);
        Assert.True(maya.LoadedRequiresStatusLoaded);
        Assert.Equal("meta.n_ctx", maya.ModelsContextWindowPath);
        var health = Assert.IsType<HealthProbeDefinition>(maya.Health);
        Assert.Equal("/props", health.Path);
        Assert.Equal("default_generation_settings.n_ctx", health.ContextPath);
        Assert.Equal("total_slots", health.SlotsPath);
        var view = new EngineDefinition
        {
            Name = maya.Name, Endpoint = maya.Endpoint, LifecycleOwner = EngineLifecycleOwner.External, ApiKey = PairProbeKey,
            Catalog = maya.Catalog, ModelsEndpoint = maya.ModelsEndpoint, ModelsContextWindowPath = maya.ModelsContextWindowPath,
            LoadedRequiresStatusLoaded = maya.LoadedRequiresStatusLoaded, Health = health,
        };
        using var handler = new DeepSeekPairProbeHandler();
        await using var runtime = new EngineRuntime(view, new RouterConfig(), new EventRing(), handler);
        Assert.True(await runtime.ProbeHealthOnceAsync(ct));
        var loaded = runtime.Capability;
        Assert.Equal(DeepSeekPairModel, Assert.Single(loaded.LoadedModels));
        Assert.Equal(32_769L, loaded.ModelContextWindows[DeepSeekPairModel]);
        Assert.Equal(32_768L, loaded.Serving.WindowFor(DeepSeekPairModel));
        Assert.Equal(1, loaded.Serving.TotalSlots);

        Assert.True(await runtime.ProbeHealthOnceAsync(ct));
        var unloaded = runtime.Capability;
        Assert.Empty(unloaded.LoadedModels);
        Assert.Empty(unloaded.ModelContextWindows);
        Assert.Equal(0L, unloaded.Serving.WindowFor(DeepSeekPairModel));
        Assert.Equal(1, unloaded.Serving.TotalSlots);
        Assert.Equal(4, handler.Requests);
    }

    private sealed class DeepSeekPairProbeHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordinal = ++Requests;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(PairProbeKey, request.Headers.Authorization?.Parameter);
            var path = ordinal % 2 == 1 ? "/v1/models" : "/props";
            Assert.Equal(new Uri(MayaPairEndpoint, path), request.RequestUri);
            var state = ordinal == 1 ? "loaded" : "unloaded";
            var body = ordinal % 2 == 1
                ? $$"""{"data":[{"id":"{{DeepSeekPairModel}}","status":{"value":"{{state}}"},"meta":{"n_ctx":32769} }]}"""
                : """{"default_generation_settings":{"n_ctx":32768},"total_slots":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static async Task CheckDeepSeekQwenCertifiedDispatchAsync(DeepSeekQwenTarget incumbent, DeepSeekQwenTarget successor,
        VerifiedInstanceRoute routeA, CertifiedPairIssuer issuer, Func<Task<VerifiedInstanceRoute>> recover, CancellationToken ct)
    {
        var baseline = issuer.Traffic;
        var requests = issuer.CertifiedCheckouts;
        var firstWrites = issuer.FirstWriteAttempts;
        var exactA = FreezePairChat(successor, routeA);
        var wrongEndpoint = Assert.Throws<VerifiedDispatchBindingException>(() =>
            VerifiedDispatchEnvelope.Freeze(successor.Frozen, routeA, Guid.NewGuid(), incumbent.Engine.Endpoint,
                successor.Alias, PairChat(successor.Alias), exactA.Credential));
        Assert.Equal(VerifiedDispatchRefusal.Target, wrongEndpoint.Refusal);
        var wrongEngine = routeA with { Engine = incumbent.Frozen.Engine };
        Assert.False(successor.Frozen.Matches(wrongEngine));
        Assert.Equal(VerifiedDispatchRefusal.Target,
            Assert.Throws<VerifiedDispatchBindingException>(() => FreezePairChat(successor, wrongEngine)).Refusal);

        var peerUri = EngineRuntime.Combine(incumbent.Engine.Endpoint, "chat/completions");
        using var wrongUri = PairOffer(exactA, peerUri, exactA.Invocation.LogicalUri.Authority);
        using var wrongHost = PairOffer(exactA, exactA.Invocation.LogicalUri, peerUri.Authority);
        await CheckPairOfferRefusalAsync(exactA, wrongUri, ct);
        await CheckPairOfferRefusalAsync(exactA, wrongHost, ct);
        Assert.Equal(requests, issuer.CertifiedCheckouts);
        Assert.Equal(firstWrites, issuer.FirstWriteAttempts);
        Assert.Equal(baseline, issuer.Traffic);

        var held = new TaskCompletionSource<VerifiedInstanceInvocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        issuer.BeforeFirstWrite = invocation =>
        {
            if (invocation != exactA.Invocation) return;
            held.SetResult(invocation);
            release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).GetAwaiter().GetResult();
        };
        var blocked = Task.Run(() => AssertCertifiedPairRefusedAsync(exactA, issuer, ct), ct);
        VerifiedInstanceRoute routeB;
        try
        {
            Assert.Equal(exactA.Invocation, await held.Task.WaitAsync(ct));
            Assert.Equal((baseline.Grants + 1, baseline.Permits), issuer.Traffic);
            Assert.Equal(requests + 1, issuer.CertifiedCheckouts);
            Assert.Equal(firstWrites + 1, issuer.FirstWriteAttempts);
            Assert.Empty(issuer.Bytes(exactA.Invocation));

            // Reverify the same launch and instance. The route incarnation is the only change.
            routeB = await recover();
            Assert.True(routeB.RouteIncarnation > routeA.RouteIncarnation);
            Assert.Equal(routeA with { RouteIncarnation = routeB.RouteIncarnation }, routeB);
            Assert.True(successor.Frozen.Matches(routeA));
            var staleCheckout = FreezePairChat(successor, routeA);
            await AssertCertifiedPairRefusedAsync(staleCheckout, issuer, ct);
            Assert.Equal(requests + 2, issuer.CertifiedCheckouts);
            Assert.Equal(firstWrites + 1, issuer.FirstWriteAttempts);
            Assert.Equal(1, issuer.NoTransfers);
            Assert.Equal((baseline.Grants + 1, baseline.Permits), issuer.Traffic);
            Assert.Empty(issuer.Bytes(staleCheckout.Invocation));
        }
        finally
        {
            release.TrySetResult();
            await blocked.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            issuer.BeforeFirstWrite = null;
        }

        Assert.Equal((baseline.Grants + 1, baseline.Permits), issuer.Traffic);
        Assert.Empty(issuer.Bytes(exactA.Invocation));
        var exactB = FreezePairChat(successor, routeB);
        await SendCertifiedPairChatAsync(successor, exactB, issuer, ct);
        Assert.Equal((baseline.Grants + 2, baseline.Permits + 1), issuer.Traffic);
        Assert.Equal(requests + 3, issuer.CertifiedCheckouts);
        Assert.Equal(firstWrites + 2, issuer.FirstWriteAttempts);
        Assert.Equal(1, issuer.NoTransfers);
        Assert.Empty(issuer.Bytes(exactA.Invocation));
    }

    private static async Task CheckPairOfferRefusalAsync(VerifiedDispatchEnvelope envelope, HttpRequestMessage offer,
        CancellationToken ct)
    {
        var error = await Assert.ThrowsAsync<VerifiedDispatchBindingException>(() =>
            envelope.CheckOfferAsync(envelope.Invocation, offer, PairCredentialEpoch, ct).AsTask());
        Assert.Equal(VerifiedDispatchRefusal.OfferedRequest, error.Refusal);
    }

    private static VerifiedDispatchEnvelope FreezePairChat(DeepSeekQwenTarget target, VerifiedInstanceRoute route)
    {
        var credential = VerifiedDispatchCredential.Capture(target.Engine, resolvedKey: null, PairCredentialEpoch);
        return VerifiedDispatchEnvelope.Freeze(target.Frozen, route, Guid.NewGuid(), target.Engine.Endpoint,
            target.Alias, PairChat(target.Alias), credential);
    }

    private static byte[] PairChat(string model) =>
        Encoding.UTF8.GetBytes($$"""{"model":"{{model}}","messages":[{"role":"user","content":"fixture"}]}""");

    private static HttpRequestMessage PairOffer(VerifiedDispatchEnvelope envelope, Uri endpoint, string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(envelope.Body.ToArray()),
        };
        request.Headers.Host = host;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    private static async Task SendCertifiedPairChatAsync(DeepSeekQwenTarget target, VerifiedDispatchEnvelope envelope,
        CertifiedPairIssuer issuer, CancellationToken ct)
    {
        var invocation = envelope.Invocation;
        var expectedBody = PairChat(target.Frozen.Model);
        Assert.Equal(expectedBody, envelope.Body.ToArray());
        Assert.Equal(new Uri(target.Engine.Endpoint, "/v1/chat/completions"), invocation.LogicalUri);
        using var offer = PairOffer(envelope, invocation.LogicalUri, invocation.LogicalUri.Authority);
        await envelope.CheckOfferAsync(invocation, offer, PairCredentialEpoch, ct);
        var operation = new VerifiedInstanceTransport(invocation, issuer).SendVerified(envelope, ct);
        try
        {
            using var response = await operation.Primary.WaitAsync(ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("OK", await response.Content.ReadAsStringAsync(ct));
        }
        finally { await operation.EndStandaloneAsync().WaitAsync(ct); }

        var terminal = await operation.Terminal;
        Assert.Equal(VerifiedRetirement.Confirmed, terminal.Retirement);
        var bytes = issuer.Bytes(invocation);
        var boundary = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
        Assert.True(boundary > 0);
        var headers = Encoding.ASCII.GetString(bytes, 0, boundary).Split("\r\n", StringSplitOptions.None);
        Assert.Equal("POST /v1/chat/completions HTTP/1.1", headers[0]);
        Assert.Equal("Host: " + invocation.LogicalUri.Authority,
            Assert.Single(headers, entry => entry.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)));
        AssertBody(expectedBody, bytes);
    }

    private static async Task AssertCertifiedPairRefusedAsync(VerifiedDispatchEnvelope envelope, CertifiedPairIssuer issuer,
        CancellationToken ct)
    {
        var invocation = envelope.Invocation;
        using var offer = PairOffer(envelope, invocation.LogicalUri, invocation.LogicalUri.Authority);
        await envelope.CheckOfferAsync(invocation, offer, PairCredentialEpoch, ct);
        var operation = new VerifiedInstanceTransport(invocation, issuer).SendVerified(envelope, ct);
        try { await Assert.ThrowsAsync<HttpRequestException>(() => operation.Primary.WaitAsync(ct)); }
        finally { await operation.EndStandaloneAsync().WaitAsync(ct); }

        var terminal = await operation.Terminal;
        Assert.Equal(VerifiedRetirement.NoUse, terminal.Retirement);
        Assert.Empty(issuer.Bytes(invocation));
    }

    private sealed class CertifiedPairIssuer(HostClosedRouteStaging staging) : IVerifiedDispatchIssuer
    {
        private readonly FixtureIssuer _issuer = new(staging);
        private int _checkouts;
        private int _noTransfers;
        private int _firstWrites;
        internal Action<VerifiedInstanceInvocation>? BeforeFirstWrite { private get; set; }
        internal int CertifiedCheckouts => Volatile.Read(ref _checkouts);
        internal int NoTransfers => Volatile.Read(ref _noTransfers);
        internal int FirstWriteAttempts => Volatile.Read(ref _firstWrites);
        internal (int Grants, int Permits) Traffic => (_issuer.Grants, _issuer.Permits);
        internal byte[] Bytes(VerifiedInstanceInvocation invocation) => _issuer.Bytes(invocation);

        public ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken ct) =>
            throw new InvalidOperationException("The companion fixture requires certified checkout.");

        public ValueTask<VerifiedCheckout> CheckoutVerifiedAsync(VerifiedInstanceInvocation invocation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _checkouts);
            if (_issuer.Mint(invocation) is { } grant)
                return ValueTask.FromResult(new VerifiedCheckout(VerifiedCheckoutDisposition.Transferred, grant));

            Interlocked.Increment(ref _noTransfers);
            return ValueTask.FromResult(new VerifiedCheckout(VerifiedCheckoutDisposition.NoTransfer));
        }

        public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
        {
            Interlocked.Increment(ref _firstWrites);
            // The hold is before FixtureIssuer takes its own mutex or calls the staged guard.
            BeforeFirstWrite?.Invoke(grant.Invocation);
            return _issuer.TryAuthorizeFirstWrite(grant);
        }
    }
}
