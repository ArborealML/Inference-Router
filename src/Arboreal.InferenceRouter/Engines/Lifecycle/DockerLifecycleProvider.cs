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
    private readonly string _engineName;
    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _ownedHandler;

    public DockerLifecycleProvider(DockerLifecycleConfig cfg, string engineName, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _engineName = engineName;
        if (handler is null)
        {
            var sockets = new SocketsHttpHandler();
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
        var (imageOk, _, imageFailure) = await CallAsync(
            HttpMethod.Get, $"{_cfg.ApiVersion}/images/{Uri.EscapeDataString(imageRef)}/json", null, ct).ConfigureAwait(false);
        if (!imageOk)
        {
            if (imageFailure!.Reason == "http_404")
                return LifecycleResult.Fail(
                    $"image_not_present {imageRef}",
                    $"image {imageRef} is not on this host and the router never pulls: load it (docker load, or a pull by hand) before switching {target.Model} here");
            return imageFailure!;
        }

        return LifecycleResult.Ok($"daemon_ok image={imageRef}");
    }

    public async ValueTask<IReadOnlyList<EnginePeer>> OwnedPeersAsync(LifecycleTarget target, CancellationToken ct)
    {
        // Ask the daemon to filter, then prove the labels locally: the filter is an optimisation, the
        // labels are the ownership proof.
        var filter = $"{{\"label\":[\"{LabelRouter}={_cfg.RouterId}\",\"{LabelEngine}={_engineName}\"]}}";
        var (ok, bodies, failure) = await CallAsync(
            HttpMethod.Get, $"{_cfg.ApiVersion}/containers/json?all=true&filters={WebUtility.UrlEncode(filter)}", null, ct).ConfigureAwait(false);
        if (!ok)
        {
            // An unreachable daemon is not "no peers": pretending otherwise would let a switch proceed
            // and start a second copy of a model that is already running.
            return [FailedPeer(failure!.Reason ?? "unknown")];
        }

        // An empty body means no containers. Tolerating it matters: a daemon that answers 200 with
        // nothing would otherwise turn a routine "nothing is running" into an exception mid-switch.
        var list = TryRead(bodies, DockerJsonContext.Default.ListDockerContainerSummary) ?? [];
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
            var (ok, _, failure) = await CallAsync(
                HttpMethod.Post, $"{_cfg.ApiVersion}/containers/{peer.Id}/stop?t={t}", null, ct).ConfigureAwait(false);
            if (!ok) return failure!;
        }
        return LifecycleResult.Ok($"stopped={peers.Count}");
    }

    public async ValueTask<LifecycleResult> LaunchAsync(LifecycleTarget target, CancellationToken ct)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LabelRouter] = _cfg.RouterId,
            [LabelEngine] = _engineName,
            [LabelModel] = target.Model,
        };
        var body = new DockerCreateRequest
        {
            Image = _cfg.Image,
            Labels = labels,
            StopTimeoutSeconds = Math.Max(1, _cfg.StopTimeoutMs / 1000),
        };
        if (_cfg.Command.Length > 0) body.Cmd = Expand(_cfg.Command, target);
        if (_cfg.Env.Length > 0) body.Env = Expand(_cfg.Env, target);
        if (_cfg.HostPort is not null)
        {
            body.HostConfig = new DockerHostConfig
            {
                NetworkMode = _cfg.NetworkMode,
                PortBindings = new Dictionary<string, DockerPortBinding[]>(StringComparer.Ordinal)
                {
                    [$"{_cfg.ContainerPort}/tcp"] = [new DockerPortBinding { HostPort = _cfg.HostPort.Value.ToString(CultureInfo.InvariantCulture) }],
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

        var (started, _, startFailure) = await CallAsync(
            HttpMethod.Post, $"{_cfg.ApiVersion}/containers/{id}/start", null, ct).ConfigureAwait(false);
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

    private static bool TryParseUnixSocket(string socket, out string path)
    {
        if (socket.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            path = socket["unix://".Length..];
            return path.Length > 0;
        }
        path = "";
        return false;
    }

    private static string BaseAddressFor(string socket)
    {
        if (TryParseUnixSocket(socket, out _)) return "http://docker.engine/";
        if (socket.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            socket.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return socket.EndsWith('/', StringComparison.Ordinal) ? socket : socket + "/";
        // A bare path is still a socket.
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
        HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (content is not null) request.Content = content;
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (true, body, default);

            var status = (int)response.StatusCode;
            // A stop or start that answers 404 means the container is already in the state we wanted;
            // a 404 from create means the image is missing, and that must never be swallowed here.
            if (status == 404 && method == HttpMethod.Post &&
                (path.Contains("/stop", StringComparison.Ordinal) || path.Contains("/start", StringComparison.Ordinal)))
                return (true, body, default);      // already stopped: the desired state holds

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
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "", LifecycleResult.Fail(
                "daemon_timeout",
                $"the daemon at {_cfg.Socket} did not answer in time: check that it is loaded rather than wedged"));
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
