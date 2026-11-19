using System.Text;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class VerifiedDispatchEnvelopeTests
{
    private static readonly Uri Endpoint = new("https://fixture.invalid:8443/v1?scope=fixture");
    private static readonly VerifiedDispatchTarget Target = new("fixture", "model.canonical", "wide", new string('A', 64));
    private static readonly VerifiedInstanceRoute Route = new("host", 1, Target.Engine, Target.PlanDigest,
        "fixture-container", "fixture-process", 2, 3);
    private const string Credential = "fixture-credential";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnsExactRewrittenBytesAndCapturedCredentialPolicy(bool bearer)
    {
        var source = """{ "model": "alias", "messages": [{"content": "two  spaces"}] }"""u8.ToArray();
        var auth = Capture(bearer);
        var prepared = VerifiedDispatchEnvelope.Freeze(Target, Route, Guid.NewGuid(), Endpoint, source, auth);
        Array.Fill(source, (byte)0);

        byte[] expected = """{"model":"model.canonical","messages":[{"content":"two  spaces"}]}"""u8.ToArray();
        Assert.Equal(expected, prepared.Body.ToArray());
        Assert.Equal("https://fixture.invalid:8443/v1/chat/completions?scope=fixture",
            prepared.Invocation.LogicalUri.AbsoluteUri);
        Assert.Equal(bearer ? $"Bearer {Credential}" : null, prepared.Credential.Authorization);

        using var offer = Offer(prepared.Invocation.LogicalUri, expected, bearer ? $"Bearer {Credential}" : null);
        await prepared.CheckOfferAsync(prepared.Invocation, offer, credentialEpoch: 7, CancellationToken.None);
    }

    [Fact]
    public async Task CompletionWithOmittedModelBindsTheCanonicalModelAndLegacyPath()
    {
        var prepared = VerifiedDispatchEnvelope.Freeze(Target, Route, Guid.NewGuid(), Endpoint,
            """{"prompt":"legacy"}"""u8.ToArray(), Capture(bearer: false));
        byte[] expected = """{"prompt":"legacy","model":"model.canonical"}"""u8.ToArray();
        Assert.Equal(expected, prepared.Body.ToArray());
        Assert.Equal("/v1/completions?scope=fixture", prepared.Invocation.LogicalUri.PathAndQuery);
        using var offer = Offer(prepared.Invocation.LogicalUri, expected, authorization: null);
        await prepared.CheckOfferAsync(prepared.Invocation, offer, credentialEpoch: 7, CancellationToken.None);
    }

    [Theory]
    [InlineData("{\"model\":\"fixture-secret\"")]
    [InlineData("""["fixture-secret"]""")]
    [InlineData("""{"model":"alias","model":"fixture-secret"}""")]
    [InlineData("""{"model":"alias","m\u006fdel":"fixture-secret"}""")]
    [InlineData("""{"model":"alias","MODEL":"fixture-secret"}""")]
    [InlineData("""{"Model":"fixture-secret"}""")]
    [InlineData("""{"model":{"name":"fixture-secret"}}""")]
    [InlineData("""{"model":" "}""")]
    public void RejectsMalformedDuplicateOrShadowedRawModel(string body)
    {
        var error = Assert.Throws<VerifiedDispatchBindingException>(() => VerifiedDispatchEnvelope.Freeze(
            Target, Route, Guid.NewGuid(), Endpoint, Encoding.UTF8.GetBytes(body), Capture(bearer: false)));
        Assert.Equal(VerifiedDispatchRefusal.Body, error.Refusal);
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CapturesTheResolvedFileCredentialWithoutReopeningTheFile()
    {
        var engine = new EngineDefinition
        {
            Name = "fixture", Endpoint = Endpoint, ApiKeyFile = "/fixture/key",
        };
        var auth = VerifiedDispatchCredential.Capture(engine, Credential, epoch: 7);
        Assert.Equal($"Bearer {Credential}", auth.Authorization);
        Assert.Equal(7L, auth.Epoch);
    }

    [Theory]
    [InlineData("file-unresolved")]
    [InlineData("ambiguous")]
    [InlineData("key-without-policy")]
    [InlineData("invalid-value")]
    [InlineData("changed-inline")]
    [InlineData("invalid-epoch")]
    public void RejectsUnresolvedAmbiguousOrInvalidCredentialPolicy(string issue)
    {
        var engine = new EngineDefinition
        {
            Name = "fixture",
            Endpoint = Endpoint,
            ApiKey = issue is "ambiguous" or "invalid-value" or "changed-inline" ?
                issue == "invalid-value" ? "fixture-secret\r\nInjected: unsafe" : Credential : null,
            ApiKeyFile = issue is "ambiguous" or "file-unresolved" ? "/fixture/key" : null,
        };
        var resolved = issue switch
        {
            "file-unresolved" => null,
            "invalid-value" => engine.ApiKey,
            "changed-inline" => "different-fixture-secret",
            _ => Credential,
        };
        var error = Assert.Throws<VerifiedDispatchBindingException>(
            () => VerifiedDispatchCredential.Capture(engine, resolved, issue == "invalid-epoch" ? 0 : 7));
        Assert.Equal(VerifiedDispatchRefusal.Credentials, error.Refusal);
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/fixture/key", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("method")]
    [InlineData("method-casing")]
    [InlineData("origin")]
    [InlineData("path")]
    [InlineData("query")]
    [InlineData("host")]
    [InlineData("content-type")]
    [InlineData("body")]
    [InlineData("auth-mode")]
    [InlineData("auth-value")]
    [InlineData("auth-duplicate")]
    [InlineData("auth-epoch")]
    [InlineData("generation")]
    [InlineData("invocation")]
    public async Task RefusesAnyChangedOfferedBinding(string change)
    {
        var prepared = VerifiedDispatchEnvelope.Freeze(Target, Route, Guid.NewGuid(), Endpoint,
            """{"model":"alias","prompt":"expected"}"""u8.ToArray(), Capture(bearer: true));
        var invocation = prepared.Invocation;
        var uri = new UriBuilder(invocation.LogicalUri);
        switch (change)
        {
            case "origin": uri.Host = "replacement.invalid"; break;
            case "path": uri.Path = "/v2/completions"; break;
            case "query": uri.Query = "?scope=wrong"; break;
            case "generation": invocation = invocation with { Route = Route with { RouteIncarnation = 4 } }; break;
            case "invocation": invocation = invocation with { Id = Guid.NewGuid() }; break;
        }

        byte[] body = change == "body"
            ? """{"model":"model.canonical","prompt":"fixture-secret"}"""u8.ToArray()
            : prepared.Body.ToArray();
        var authorization = change switch
        {
            "auth-mode" => null,
            "auth-value" => "Bearer different-fixture-secret",
            _ => $"Bearer {Credential}",
        };
        using var offer = Offer(uri.Uri, body, authorization);
        switch (change)
        {
            case "method": offer.Method = HttpMethod.Put; break;
            case "method-casing": offer.Method = new HttpMethod("post"); break;
            case "host": offer.Headers.Host = "replacement.invalid"; break;
            case "content-type":
                offer.Content!.Headers.Remove("Content-Type");
                offer.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
                break;
            case "auth-duplicate": offer.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Credential}"); break;
        }

        var error = await Assert.ThrowsAsync<VerifiedDispatchBindingException>(
            () => prepared.CheckOfferAsync(invocation, offer, change == "auth-epoch" ? 8 : 7, CancellationToken.None).AsTask());
        Assert.Equal(change.StartsWith("auth-", StringComparison.Ordinal)
            ? VerifiedDispatchRefusal.Credentials : VerifiedDispatchRefusal.OfferedRequest, error.Refusal);
        Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnboundOrIncompleteResidentIdentity()
    {
        foreach (var route in new[]
        {
            Route with { EffectiveTarget = new string('B', 64) },
            Route with { ContainerId = "" },
            Route with { ServingIncarnation = "" },
            Route with { EnrollmentGeneration = 0 },
            Route with { LaunchEpoch = 0 },
            Route with { RouteIncarnation = 0 },
        })
        {
            var error = Assert.Throws<VerifiedDispatchBindingException>(() => VerifiedDispatchEnvelope.Freeze(
                Target, route, Guid.NewGuid(), Endpoint, """{"prompt":"fixture"}"""u8.ToArray(), Capture(bearer: false)));
            Assert.Equal(VerifiedDispatchRefusal.Target, error.Refusal);
        }
    }

    private static VerifiedDispatchCredential Capture(bool bearer) =>
        VerifiedDispatchCredential.Capture(new EngineDefinition
        {
            Name = "fixture", Endpoint = Endpoint, ApiKey = bearer ? Credential : null,
        }, bearer ? Credential : null, epoch: 7);

    private static HttpRequestMessage Offer(Uri uri, byte[] body, string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }
}
