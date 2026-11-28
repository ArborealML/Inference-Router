using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Arboreal.InferenceRouter;

internal sealed class OwnedPrivateAttempt : IDisposable
{
    internal static readonly HttpRequestOptionsKey<OwnedPrivateAttempt> RequestOption = new("AIR owned private attempt");
    private HttpRequestMessage? _request;
    private HttpContent? _projectedContent;
    private readonly Uri _uri;
    private Dictionary<string, string[]>? _headers;
    private Dictionary<string, string[]>? _contentHeaders;
    private readonly Version _version;
    private readonly HttpVersionPolicy _versionPolicy;
    private readonly CancellationTokenSource? _execution;
    private readonly RouterTicket _ticket;
    private int _terminalObserved;
    private int _consumed;

    internal OwnedPrivateAttempt(RouterTicket ticket, EngineRuntime engine, Func<HttpRequestMessage> project, ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> fingerprintKey)
    {
        _ticket = ticket;
        var request = project();
        _request = request;
        _projectedContent = request.Content!;
        _ = _projectedContent.Headers.ContentLength;
        _uri = request.RequestUri!;
        _version = request.Version;
        _versionPolicy = request.VersionPolicy;
        _headers = CaptureHeaders(request.Headers);
        _contentHeaders = CaptureHeaders(_projectedContent.Headers);
        _execution = ticket.ExecCts;
        Policy = ticket.OwnedPolicy!;
        Engine = engine;
        Record = new(Guid.NewGuid().ToString("N"), ticket.Id, engine.Name, Policy.Group.Revision,
            Convert.ToHexStringLower(HMACSHA256.HashData(fingerprintKey, body)));
    }

    internal HttpRequestMessage Request => _request!;
    internal OwnedTicketPolicy Policy { get; }
    internal PolicyGroupKey Key => Policy.Key;
    internal EngineRuntime Engine { get; }
    internal OwnedPolicyStore.AttemptRecord Record { get; }
    internal bool Consumed => Volatile.Read(ref _consumed) != 0;
    internal bool TerminalObserved => Volatile.Read(ref _terminalObserved) != 0;

    internal void Consume()
    {
        Volatile.Write(ref _consumed, 1);
        Volatile.Write(ref _ticket.PrivateAttemptDisposition, 2);
    }

    public void Dispose()
    {
        _request?.Dispose();
        _request = null;
        _projectedContent = null;
        _headers = null;
        _contentHeaders = null;
    }
    internal void ObserveTerminal() => Volatile.Write(ref _terminalObserved, 1);

    internal bool Matches(EngineRuntime engine, HttpRequestMessage request) => ReferenceEquals(_request, request) &&
        ReferenceEquals(Engine, engine) && request.Method == HttpMethod.Post && request.RequestUri == _uri &&
        request.Version == _version && request.VersionPolicy == _versionPolicy &&
        ReferenceEquals(request.Content, _projectedContent) && request.Content is { } content &&
        MatchHeaders(_headers!, request.Headers) && MatchHeaders(_contentHeaders!, content.Headers);

    internal void RequestCancellation()
    {
        try { _ = _execution?.CancelAsync(); }
        catch (ObjectDisposedException) { /* The local execution registration already retired. */ }
    }

    private static Dictionary<string, string[]> CaptureHeaders(HttpHeaders headers) => headers.NonValidated.ToDictionary(
        static row => row.Key, static row => row.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

    private static bool MatchHeaders(Dictionary<string, string[]> frozen, HttpHeaders headers)
    {
        var actual = headers.NonValidated;
        return actual.Count == frozen.Count && actual.All(row => frozen.TryGetValue(row.Key, out var values) &&
            row.Value.SequenceEqual(values, StringComparer.Ordinal));
    }
}

internal sealed class OwnedPrivateTransport(OwnedPrivatePolicy policy, EngineRuntime engine, Uri models, Uri serving,
    HttpMessageHandler inner) : DelegatingHandler(inner)
{
    internal static readonly HttpRequestOptionsKey<bool> ServiceProbe = new("AIR enrolled private service probe");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(OwnedPrivateAttempt.RequestOption, out var attempt))
        {
            if (policy.BeforeHandoffForTests is { } before)
                await before(attempt).ConfigureAwait(false);
            if (!attempt.Matches(engine, request) || !policy.Allows(attempt.Policy, engine.Name)) throw Refuse();
            // This is the final router-controlled handoff before the enrolled handler can transmit.
            policy.Store.ClaimIntent(attempt, cancellationToken);
            if (policy.AfterHandoffClaimForTests is { } after)
            {
                await after(attempt).ConfigureAwait(false);
                if (!attempt.Matches(engine, request)) throw Refuse();
            }
        }
        else if (!policy.Store.Available || request.Method != HttpMethod.Get || request.Content is not null ||
            !request.Options.TryGetValue(ServiceProbe, out var trustedProbe) || !trustedProbe ||
            request.RequestUri != models && request.RequestUri != serving) throw Refuse();

        cancellationToken.ThrowIfCancellationRequested();
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static RouterRequestException Refuse() =>
        new("policy_route_denied", "the final private transport is outside its authorized scope");
}
