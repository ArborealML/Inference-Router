using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Arboreal.InferenceRouter.Lifecycle;

/// <summary>Native lifecycle: the router talks to the Docker Engine API itself instead of running a
/// program that talks to it. Two things come with that, and they are the whole reason for it. The router
/// can prove what it owns (labels it wrote itself), so switching a model never touches a container
/// somebody else started. And every step is a request with a status code, so a refusal is something the
/// operator can read rather than the tail of a script's stderr.</summary>
/// <remarks>
/// Deliberate limits:
/// <list type="bullet">
/// <item>It never pulls. An image that is not already on the host is a preflight failure, because a
/// multi-gigabyte pull inside a switch is an outage with extra steps.</item>
/// <item>It never escalates. A daemon it cannot reach, or a socket it is not allowed to use, produces one
/// actionable message and stops there (decision: refuse rather than re-exec under sudo).</item>
/// <item>It never stops a container whose labels it did not write, even if the daemon's own filter handed
/// it back.</item>
/// </list>
/// </remarks>
public sealed class DockerLifecycleProvider : IEngineLifecycleProvider, IDisposable
{
    public const string ProviderKind = "docker";

    public const string LabelRouter = "arboreal.router";
    public const string LabelEngine = "arboreal.engine";
    public const string LabelModel = "arboreal.model";

    private readonly DockerLifecycleConfig _cfg;
    private readonly string[] _command;
    private readonly string[] _env;
    private readonly string _engineName;
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _ownedHandler;

    public DockerLifecycleProvider(DockerLifecycleConfig cfg, string engineName, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _command = cfg.Command.ToArray();
        _env = cfg.Env.ToArray();
        _engineName = engineName;
        if (handler is null)
        {
            var sockets = new SocketsHttpHandler
            {
                // A daemon is not a web server: a system proxy would send container control to some
                // third host, and a redirect would do the same on the daemon's say-so.
                UseProxy = false,
                AllowAutoRedirect = false,
            };
            if (TryParseUnixSocket(cfg.Socket, out var socketPath))
                sockets.ConnectCallback = (_, ct) => ConnectUnixAsync(socketPath, ct);
            _ownedHandler = sockets;
            _http = new HttpClient(_ownedHandler, disposeHandler: false);
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: false);
        }
        _http.BaseAddress = new Uri(BaseAddressFor(cfg.Socket));
        _http.Timeout = Timeout.InfiniteTimeSpan;      // every call carries its own token
    }

    public string Kind => ProviderKind;

    public void Dispose()
    {
        _http.Dispose();
        _ownedHandler?.Dispose();
    }

    public ReadinessPolicy Readiness(LifecycleTarget target) =>
        new(_cfg.WaitHealth, TimeSpan.FromMilliseconds(_cfg.WaitTimeoutMs));

    public async ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
    {
        if (_cfg.ImageIdentity is not { } identity) return MissingImageIdentity();

        // Reachability first: every later failure is ambiguous if the daemon is not answering at all.
        var (ok, versionBody, failure) = await CallAsync(
            HttpMethod.Get, $"{_cfg.ApiVersion}/version", null, ct).ConfigureAwait(false);
        if (!ok) return failure!;
        var version = TryRead(versionBody, DockerJsonContext.Default.DockerVersionResponse);
        if (version is not null && !ApiVersionAtLeast(version.ApiVersion, _cfg.ApiVersion))
            return LifecycleResult.Fail(
                $"daemon_api_{version.ApiVersion}_below_{_cfg.ApiVersion}",
                $"this daemon is older than the pinned API version {_cfg.ApiVersion}: raise the engine's docker.api_version to what the daemon reports, or update the daemon");

        // The image has to be here already. A pull during a switch is an outage.
        var imageRef = _cfg.Image;
        var (imageOk, imageBody, imageFailure) = await CallAsync(
            HttpMethod.Get, $"{_cfg.ApiVersion}/images/{Uri.EscapeDataString(imageRef)}/json", null, ct).ConfigureAwait(false);
        if (!imageOk)
        {
            if (imageFailure!.Reason == "http_404")
                return LifecycleResult.Fail(
                    $"image_not_present {imageRef}",
                    $"image {imageRef} is not on this host and the router never pulls: load it (docker load, or a pull by hand) before switching {target.Model} here");
            return imageFailure!;
        }

        var image = TryRead(imageBody, DockerJsonContext.Default.DockerImageInspectResponse);
        return ImageIdentityFailure(image, identity) ?? LifecycleResult.Ok($"daemon_ok image={imageRef}");
    }

    private static LifecycleResult MissingImageIdentity() => LifecycleResult.Fail(
        "image_identity_required", "set docker.image_id, docker.image_repo_digest and docker.image_platform before switching");

    private static LifecycleResult? ImageIdentityFailure(DockerImageInspectResponse? image, DockerImageIdentity expected)
    {
        if (image is null)
            return LifecycleResult.Fail("image_inspect_unverifiable",
                "check that this daemon returns valid JSON for the configured image lookup");
        if (!DockerImageIdentity.IsFullSha256(image.Id.AsSpan()))
            return LifecycleResult.Fail("image_id_unverifiable",
                "check that this daemon reports a full sha256 image ID for the configured image lookup");
        if (!string.Equals(image.Id, expected.Id, StringComparison.Ordinal))
            return LifecycleResult.Fail("image_id_mismatch",
                "use the image that matches docker.image_id or update the pins for the intended image");

        if (image.RepoDigests is not { } digests || !digests.Any(DockerImageIdentity.IsNamedDigest))
            return LifecycleResult.Fail("image_repo_digest_unverifiable",
                "use an image with a named repository digest reported by this daemon");
        if (!digests.Contains(expected.RepoDigest, StringComparer.Ordinal))
            return LifecycleResult.Fail("image_repo_digest_mismatch",
                "use the image that reports docker.image_repo_digest or update the pins for the intended image");

        var variant = image.Variant;
        if (!DockerImageIdentity.IsPlatformSegment(image.Os) || !DockerImageIdentity.IsPlatformSegment(image.Architecture) ||
            (!string.IsNullOrEmpty(variant) && !DockerImageIdentity.IsPlatformSegment(variant)))
            return LifecycleResult.Fail("image_platform_unverifiable",
                "check that this daemon reports the image OS, architecture and any platform variant");
        var platform = string.IsNullOrEmpty(variant)
            ? $"{image.Os}/{image.Architecture}"
            : $"{image.Os}/{image.Architecture}/{variant}";
        if (!string.Equals(platform, expected.Platform, StringComparison.Ordinal))
            return LifecycleResult.Fail("image_platform_mismatch",
                "use the image for docker.image_platform or update the pins for the intended image");

        return null;
    }

    /// <summary>Read unfiltered Docker inventory for the host boundary. A Docker inventory alone
    /// cannot grant exclusive host authority: it omits other launchers and host processes.</summary>
    internal async ValueTask<HostOccupancy> ObserveExclusiveHostAsync(
        Guid queryId, IReadOnlySet<string> managedEngines, CancellationToken ct)
    {
        var (ok, body, _) = await CallAsync(HttpMethod.Get, $"{_cfg.ApiVersion}/containers/json?all=true",
            null, ct, requiredStatus: 200).ConfigureAwait(false);
        var containers = ok ? TryRead(body, DockerJsonContext.Default.ListDockerContainerSummary) : null;
        if (containers is not null)
        {
            foreach (var container in containers)
            {
                if (container is null || container.State is not ("running" or "restarting" or "paused")) continue;
                var labels = container.Labels;
                if (labels is null || !labels.TryGetValue(LabelRouter, out var router) || router != _cfg.RouterId ||
                    !labels.TryGetValue(LabelEngine, out var engine) || !managedEngines.Contains(engine))
                    return new HostOccupancy(queryId, HostOccupancyKind.Foreign);
            }
        }

        return new HostOccupancy(queryId, HostOccupancyKind.Unknown);
    }

    /// <summary>Preserve a pending operation after a lost Docker response. Docker inventory and
    /// inspect cannot supply a terminal receipt for the operation ID or rule out a delayed effect.</summary>
    internal static HostMutationOutcome ReconcileUnacknowledgedHostMutation(HostMutation operation) =>
        new(operation.Id, HostMutationStatus.Unknown);

    public async ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
    {
        // Ask the daemon to filter, then prove the labels locally: the filter is an optimisation, the
        // labels are the ownership proof.
        // Built through the source-generated context: router_id and the engine name come from config,
        // and pasting them into JSON would let a quote or a brace change which containers are listed.
        var filterBody = new DockerListFilter
        {
            Label = [$"{LabelRouter}={_cfg.RouterId}", $"{LabelEngine}={_engineName}"],
        };
        var filter = System.Text.Json.JsonSerializer.Serialize(filterBody, DockerJsonContext.Default.DockerListFilter);
        var (ok, bodies, failure) = await CallAsync(
            HttpMethod.Get, $"{_cfg.ApiVersion}/containers/json?all=true&filters={WebUtility.UrlEncode(filter)}", null, ct).ConfigureAwait(false);
        if (!ok)
        {
            // An unreachable daemon is not "no peers": pretending otherwise would let a switch proceed
            // and start a second copy of a model that is already running.
            return [FailedPeer(failure!.Reason ?? "unknown")];
        }

        if (string.IsNullOrWhiteSpace(bodies)) return [];        // 204 / empty answer: nothing is running
        var list = TryRead(bodies, DockerJsonContext.Default.ListDockerContainerSummary);
        if (list is null)
        {
            // A body we cannot read is not "no containers". Treating a malformed answer as an empty
            // list is how a router starts a second copy of a model that is already loaded.
            return [FailedPeer("unparsable_container_list")];
        }
        var peers = new List<EnginePeer>(list.Count);
        foreach (var c in list)
        {
            var ours = c.Labels is not null
                && c.Labels.TryGetValue(LabelRouter, out var router) && router == _cfg.RouterId
                && c.Labels.TryGetValue(LabelEngine, out var engine) && engine == _engineName;
            var model = c.Labels is not null && c.Labels.TryGetValue(LabelModel, out var m) ? m : null;
            peers.Add(new EnginePeer(c.Id ?? "", c.Image ?? "", model, ours));
        }
        return peers;
    }

    public async ValueTask<LifecycleResult> StopAsync(
        LifecycleTarget target, IReadOnlyList<EnginePeer> peers, CancellationToken ct)
    {
        foreach (var peer in peers)
        {
            if (!peer.OwnedByUs)
                return LifecycleResult.Fail(
                    $"refused_to_stop_unowned {peer.Id}",
                    "the lifecycle provider was asked to stop a container it did not start; this is a bug, not a configuration problem");

            var t = Math.Max(1, _cfg.StopTimeoutMs / 1000);
            // 304 (already stopped), 404 (already gone) and 409 (not running) all mean the desired
            // state holds. A previous switch leaves stopped containers behind, and treating that as a
            // failure would refuse every switch after the first.
            var (ok, _, failure) = await CallAsync(
                HttpMethod.Post, $"{_cfg.ApiVersion}/containers/{Uri.EscapeDataString(peer.Id)}/stop?t={t}", null, ct,
                already: [304, 404, 409]).ConfigureAwait(false);
            if (!ok) return failure!;

            if (_cfg.RemoveStopped)
            {
                // Removing is what keeps a model change from accumulating stopped containers on the
                // host. It is refused for anything we do not own, which is the same proof as above.
                var (removed, _, removeFailure) = await CallAsync(
                    HttpMethod.Delete, $"{_cfg.ApiVersion}/containers/{Uri.EscapeDataString(peer.Id)}?force=true", null, ct,
                    already: [404]).ConfigureAwait(false);
                if (!removed) return removeFailure!;
            }
        }
        return LifecycleResult.Ok($"stopped={peers.Count}");
    }

    public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
    {
        if (_cfg.ImageIdentity is not { } identity) return MissingImageIdentity();

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LabelRouter] = _cfg.RouterId,
            [LabelEngine] = _engineName,
            [LabelModel] = target.Model,
        };
        var body = new DockerCreateRequest
        {
            Image = identity.Id,
            Labels = labels,
            StopTimeoutSeconds = Math.Max(1, _cfg.StopTimeoutMs / 1000),
        };
        if (_command.Length > 0) body.Cmd = Expand(_command, target);
        if (_env.Length > 0) body.Env = Expand(_env, target);
        if (_cfg.HostPort is not null)
        {
            body.HostConfig = new DockerHostConfig
            {
                NetworkMode = _cfg.NetworkMode,
                PortBindings = new Dictionary<string, DockerPortBinding[]>(StringComparer.Ordinal)
                {
                    [$"{_cfg.ContainerPort}/tcp"] = [new DockerPortBinding
                    {
                        // Always name the interface: an empty HostIp is Docker's shorthand for every
                        // interface, and an unauthenticated backend on 0.0.0.0 is a public port.
                        HostIp = _cfg.HostIp,
                        HostPort = _cfg.HostPort.Value.ToString(CultureInfo.InvariantCulture),
                    }],
                },
            };
        }
        else if (_cfg.NetworkMode is not null)
        {
            body.HostConfig = new DockerHostConfig { NetworkMode = _cfg.NetworkMode };
        }

        var json = System.Text.Json.JsonSerializer.Serialize(body, DockerJsonContext.Default.DockerCreateRequest);
        using var payload = new StringContent(json, Encoding.UTF8, "application/json");
        var (created, bodies, failure) = await CallAsync(
            HttpMethod.Post, $"{_cfg.ApiVersion}/containers/create", payload, ct).ConfigureAwait(false);
        if (!created) return failure!;

        var response = TryRead(bodies, DockerJsonContext.Default.DockerCreateResponse);
        var id = response?.Id;
        if (string.IsNullOrEmpty(id))
            return LifecycleResult.Fail("create_returned_no_id", "the daemon created a container but did not name it: check the daemon's logs");

        // 304 means it was already running. 404 must be a failure: the container we were just given an
        // id for is gone, and reporting a successful launch with nothing running is worse than failing.
        var (started, _, startFailure) = await CallAsync(
            HttpMethod.Post, $"{_cfg.ApiVersion}/containers/{Uri.EscapeDataString(id)}/start", null, ct,
            already: [304]).ConfigureAwait(false);
        if (!started) return startFailure!;

        return LifecycleResult.Ok($"container={id[..Math.Min(12, id.Length)]}");
    }

    private string[] Expand(string[] template, LifecycleTarget target)
    {
        var outValues = new string[template.Length];
        for (var i = 0; i < template.Length; i++)
        {
            outValues[i] = template[i]
                .Replace("{{model}}", target.Model, StringComparison.Ordinal)
                .Replace("{{profile}}", target.Profile?.Name ?? target.Definition.Profile ?? "", StringComparison.Ordinal)
                .Replace("{{endpoint}}", _engineName, StringComparison.Ordinal);
        }
        return outValues;
    }

    internal static bool TryParseUnixSocket(string socket, out string path)
    {
        if (socket.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            path = socket["unix://".Length..];
            return path.Length > 0;
        }
        if (socket.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            socket.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            path = "";
            return false;
        }
        // A bare value is a socket path, which is what an operator writes most often.
        path = socket;
        return path.Length > 0;
    }

    private static string BaseAddressFor(string socket)
    {
        if (TryParseUnixSocket(socket, out _)) return "http://docker.engine/";
        if (socket.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            socket.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return socket.EndsWith('/', StringComparison.Ordinal) ? socket : socket + "/";
        // A bare path is a socket path, not a host name: it must not be sent to the HTTP base below.
        return "http://docker.engine/";
    }

    // The socket is not disposed on the success path because its ownership moves to the NetworkStream
    // that wraps it; the analyzers cannot see that transfer.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to the NetworkStream created below (ownsSocket: true).")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness",
        "IDISP001:Dispose created",
        Justification = "Ownership transfers to the NetworkStream created below (ownsSocket: true).")]
    private static async ValueTask<Stream> ConnectUnixAsync(string path, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool ApiVersionAtLeast(string? daemon, string pinned)
    {
        if (daemon is null) return true;        // daemon did not say: let the calls themselves decide
        if (!pinned.StartsWith('v', StringComparison.OrdinalIgnoreCase)) return true;
        var want = pinned[1..];
        return VersionToLong(daemon) >= VersionToLong(want);
    }

    private static long VersionToLong(string version)
    {
        var parts = version.Split('.', 3);
        long acc = 0;
        foreach (var p in parts)
        {
            acc = acc * 1000 + (int.TryParse(p, CultureInfo.InvariantCulture, out var n) ? n : 0);
        }
        return acc;
    }

    private async ValueTask<(bool Ok, string Body, LifecycleResult? Failure)> CallAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct, int[]? already = null,
        int? requiredStatus = null)
    {
        // Every call carries its own deadline. Without it a daemon that accepts the connection and then
        // says nothing holds a switch open indefinitely, the engine stays in "switching", and the request
        // that triggered it waits on a socket that will never answer.
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callCts.CancelAfter(TimeSpan.FromMilliseconds(_cfg.CallTimeoutMs));
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (content is not null) request.Content = content;
            response = await _http.SendAsync(request, callCts.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(callCts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var accepted = requiredStatus is { } required ? status == required : response.IsSuccessStatusCode;
            if (accepted || (already is not null && Array.IndexOf(already, status) >= 0))
                return (true, body, default);   // the desired state already holds

            // Which statuses mean "already in the desired state" is decided per call site, not here: a
            // 404 from stop means the container is gone, but a 404 from start means the container we were
            // just given an id for does not exist, and calling that a successful launch is worse than failing.

            var snippet = body.Trim();
            if (snippet.Length > 180) snippet = snippet[..180];
            var reason = $"http_{status}";
            var action = status switch
            {
                401 or 403 => $"the daemon refused the router ({status}); it will not escalate privileges: give the router user access to {_cfg.Socket}, or leave this engine on a delegated select command",
                404 => $"the daemon answered 404 for {path}: check the pinned API version {_cfg.ApiVersion} and that this engine's containers exist",
                409 => $"the daemon refused because of a conflict: {snippet}",
                >= 500 => $"the daemon failed ({status}) on {path}: {snippet}",
                _ => $"the daemon answered {status} for {method.Method} {path}: {snippet}",
            };
            return (false, body, LifecycleResult.Fail(reason, action));
        }
        catch (HttpRequestException ex)
        {
            return (false, "", LifecycleResult.Fail(
                $"daemon_unreachable {ex.Message}",
                $"cannot reach the Docker daemon at {_cfg.Socket}: start it, or point this engine's docker.socket somewhere the router user may reach — the router will not retry with elevated rights"));
        }
        catch (SocketException ex)
        {
            return (false, "", LifecycleResult.Fail(
                $"socket_unreachable {ex.Message}",
                $"the socket {_cfg.Socket} refused the connection: check its path and the router user's permission on it"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "", LifecycleResult.Fail(
                $"daemon_call_timeout_{_cfg.CallTimeoutMs}ms",
                $"the daemon at {_cfg.Socket} accepted the request and did not answer within {_cfg.CallTimeoutMs} ms: " +
                "check whether dockerd is wedged, or raise docker.call_timeout_ms if this host is slow"));
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>Read a body the daemon may legitimately leave empty or fill with something that is not
    /// the shape we asked for. A malformed answer is an unknown, never a crash.</summary>
    private static T? TryRead<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        if (string.IsNullOrWhiteSpace(body)) return default;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(body, type);
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
    }

    private static EnginePeer FailedPeer(string reason) =>
        new(Id: "daemon:" + reason, Image: "", Model: null, OwnedByUs: false, QueryFailed: true);
}
