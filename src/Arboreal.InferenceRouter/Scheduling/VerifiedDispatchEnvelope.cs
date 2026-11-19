using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal enum VerifiedDispatchRefusal { Target, Body, Credentials, OfferedRequest }

internal sealed class VerifiedDispatchBindingException(VerifiedDispatchRefusal refusal)
    : InvalidOperationException(refusal switch
    {
        VerifiedDispatchRefusal.Target => "The verified dispatch target did not match.",
        VerifiedDispatchRefusal.Body => "The verified dispatch requires a canonical JSON request.",
        VerifiedDispatchRefusal.Credentials => "The verified dispatch credential policy did not match.",
        _ => "The offered request did not match the prepared verified dispatch.",
    })
{
    internal VerifiedDispatchRefusal Refusal { get; } = refusal;
}

/// <summary>A captured upstream credential policy, with no access to inbound headers or key files.</summary>
internal sealed class VerifiedDispatchCredential
{
    private VerifiedDispatchCredential(long epoch, string? authorization)
    {
        Epoch = epoch;
        Authorization = authorization;
    }

    internal long Epoch { get; }
    internal string? Authorization { get; }

    // resolvedKey is the value captured by the runtime at startup, including any configured file read.
    internal static VerifiedDispatchCredential Capture(EngineDefinition engine, string? resolvedKey, long epoch)
    {
        var inline = engine.ApiKey;
        var file = engine.ApiKeyFile;
        if (epoch <= 0 || (inline is not null && file is not null) ||
            (inline is not null && !string.Equals(inline, resolvedKey, StringComparison.Ordinal)))
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Credentials);

        if (inline is null && file is null)
        {
            if (resolvedKey is not null)
                throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Credentials);
            return new VerifiedDispatchCredential(epoch, authorization: null);
        }

        if (!IsBearerToken(resolvedKey))
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Credentials);
        return new VerifiedDispatchCredential(epoch, $"Bearer {resolvedKey}");
    }

    private static bool IsBearerToken(string? value)
    {
        // Bearer uses a nonempty token followed by optional equals padding.
        if (string.IsNullOrEmpty(value)) return false;
        var token = value.AsSpan().TrimEnd('=');
        if (token.IsEmpty) return false;
        foreach (var c in token)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_' or '~' or '+' or '/'))
                return false;
        return true;
    }
}

/// <summary>The single request that the prepared route may submit. Construction and comparison perform no I/O to the issuer.</summary>
internal sealed class VerifiedDispatchEnvelope
{
    private const string JsonContentType = "application/json";

    private VerifiedDispatchEnvelope(VerifiedInstanceInvocation invocation, ImmutableArray<byte> body,
                                     VerifiedDispatchCredential credential)
    {
        Invocation = invocation;
        Body = body;
        Credential = credential;
    }

    internal VerifiedInstanceInvocation Invocation { get; }
    internal ImmutableArray<byte> Body { get; }
    internal VerifiedDispatchCredential Credential { get; }

    internal static VerifiedDispatchEnvelope Freeze(
        VerifiedDispatchTarget target, VerifiedInstanceRoute route, Guid invocationId, Uri endpoint,
        string requestedSpelling, ReadOnlyMemory<byte> rawBody, VerifiedDispatchCredential credential)
    {
        if (!target.Matches(route) || invocationId == Guid.Empty || !SameEffectiveUri(target.Endpoint, endpoint))
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);

        // Validate and rewrite the same owned source bytes.
        var source = rawBody.ToArray();
        ValidateModel(source, requestedSpelling, StringComparison.OrdinalIgnoreCase, requireMember: false);
        var suffix = RouterCore.LooksLikeCompletion(source) ? "completions" : "chat/completions";
        var rewritten = ModelRewriter.Rewrite(source, target.Model);
        ValidateModel(rewritten, target.Model, StringComparison.Ordinal, requireMember: true);

        var uri = EngineRuntime.Combine(endpoint, suffix);
        return new VerifiedDispatchEnvelope(new VerifiedInstanceInvocation(route, invocationId, uri),
            ImmutableArray.CreateRange(rewritten), credential);
    }

    internal async ValueTask CheckOfferAsync(
        VerifiedInstanceInvocation invocation, HttpRequestMessage offer, long credentialEpoch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (invocation.Route != Invocation.Route || invocation.Id != Invocation.Id ||
            !SameEffectiveUri(Invocation.LogicalUri, invocation.LogicalUri) ||
            !SameEffectiveUri(Invocation.LogicalUri, offer.RequestUri) ||
            !string.Equals(offer.Method.Method, "POST", StringComparison.Ordinal) ||
            !MatchesRequestHeaders(offer.Headers, Invocation.LogicalUri.Authority) ||
            offer.Content is not { } content || !MatchesContentHeaders(content.Headers))
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.OfferedRequest);

        if (credentialEpoch != Credential.Epoch ||
            !MatchesHeader(offer.Headers, "Authorization", Credential.Authorization))
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Credentials);

        byte[] bytes;
        try
        {
            bytes = await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            ct.ThrowIfCancellationRequested();
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.OfferedRequest);
        }
        if (!Body.AsSpan().SequenceEqual(bytes))
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.OfferedRequest);
    }

    private static bool MatchesRequestHeaders(HttpRequestHeaders headers, string authority)
    {
        foreach (var header in headers.NonValidated)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
                header.Value.Count != 1 ||
                !string.Equals(header.Value.ToString(), authority, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool MatchesContentHeaders(HttpContentHeaders headers)
    {
        foreach (var header in headers.NonValidated)
            if (!string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                return false;
        return MatchesHeader(headers, "Content-Type", JsonContentType);
    }

    private static bool MatchesHeader(HttpHeaders headers, string name, string? expected)
    {
        if (!headers.NonValidated.TryGetValues(name, out var values)) return expected is null;
        return expected is not null && values.Count == 1 &&
            string.Equals(values.ToString(), expected, StringComparison.Ordinal);
    }

    internal static bool IsHttpEndpoint(Uri? uri) =>
        uri is { IsAbsoluteUri: true, Scheme: "http" or "https", UserInfo.Length: 0, Fragment.Length: 0 };

    internal static bool SameEffectiveUri(Uri expected, Uri? offer) =>
        offer is not null && IsHttpEndpoint(offer) && IsHttpEndpoint(expected) &&
        string.Equals(expected.Scheme, offer.Scheme, StringComparison.Ordinal) &&
        string.Equals(expected.Authority, offer.Authority, StringComparison.Ordinal) &&
        string.Equals(expected.PathAndQuery, offer.PathAndQuery, StringComparison.Ordinal);

    // Enumerate the original members so duplicate and case-shadowed spellings cannot collapse in lookup.
    private static void ValidateModel(ReadOnlyMemory<byte> body, string expected, StringComparison comparison, bool requireMember)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Body);

            var found = false;
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(member.Name, "model", StringComparison.OrdinalIgnoreCase)) continue;
                if (found || !member.NameEquals("model") || member.Value.ValueKind != JsonValueKind.String)
                    throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Body);
                var model = member.Value.GetString();
                if (string.IsNullOrWhiteSpace(model) || !string.Equals(model, expected, comparison))
                    throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Body);
                found = true;
            }
            if (requireMember && !found)
                throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Body);
        }
        catch (JsonException)
        {
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Body);
        }
    }
}
