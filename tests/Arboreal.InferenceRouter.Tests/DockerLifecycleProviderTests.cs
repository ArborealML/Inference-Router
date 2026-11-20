using System.Net;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>A stand-in for the Docker daemon: it answers the Engine API with canned bodies and records
/// every request, so the provider's requests, its refusals, and the absence of anything it must never do
/// (pull, stop a foreign container, escalate) are all observable without a daemon on the machine.</summary>
public sealed class FakeDockerDaemonHandler : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    public List<string> Bodies { get; } = [];
    /// <summary>When true the transport itself fails, which is how an unreachable socket looks.</summary>
    public bool TransportFails { get; set; }
    /// <summary>Milliseconds the daemon "thinks" before answering: how a wedged dockerd looks.</summary>
    public int StallMs { get; set; }
    public Func<string, (HttpStatusCode Status, string Body)> Responder { get; set; } =
        _ => (HttpStatusCode.NotFound, "{}");
    public Func<string, CancellationToken, Task<(HttpStatusCode Status, string Body)>>? AsyncResponder { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        Requests.Add($"{request.Method.Method} {path}");
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Bodies.Add(body);

        if (TransportFails) throw new HttpRequestException("connection refused");

        if (StallMs > 0) await Task.Delay(StallMs, cancellationToken);
        var (status, text) = AsyncResponder is { } respond
            ? await respond(path, cancellationToken)
            : Responder(path);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(text, Encoding.UTF8, "application/json"),
        };
    }

}

public sealed class DockerLifecycleProviderTests
{
    private const string Ours = FullPeerId;
    internal const string FullPeerId = "abc123def4567890abc123def4567890abc123def4567890abc123def4567890";
    private const string Locator = "registry.local/strata:2026-11";
    private const string ImageId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string DifferentId = "sha256:3333333333333333333333333333333333333333333333333333333333333333";
    private const string Manifest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string RepoDigest = "registry.local/strata@" + Manifest;
    private const string LocalSocket = "unix:///run/docker.sock";
    private const string RemoteBase = "https://daemon.invalid:2376/docker/";

    internal static DockerLifecycleConfig Cfg(string api = "v1.44", bool pinned = true, string platform = "linux/amd64",
        string socket = LocalSocket, string routerId = "r1", bool waitHealth = true, bool removeStopped = true,
        int callTimeoutMs = 15_000, int waitTimeoutMs = 600_000) => new()
    {
        Image = Locator,
        ImageIdentity = pinned ? new DockerImageIdentity(ImageId, RepoDigest, platform) : null,
        Socket = socket,
        ApiVersion = api,
        RouterId = routerId,
        Command = ["--model", "{{model}}", "--ctx", "{{profile}}"],
        Env = ["AIR_ENGINE={{endpoint}}"],
        HostPort = 18021,
        ContainerPort = 8080,
        WaitHealth = waitHealth,
        RemoveStopped = removeStopped,
        CallTimeoutMs = callTimeoutMs,
        WaitTimeoutMs = waitTimeoutMs,
    };

    private static LifecycleTarget Target() => new(
        "strata", "strata-8b", null,
        new EngineModelDefinition { Name = "strata-8b" });

    [Fact]
    public void NamedPipeDockerDirectProviderRefusesBeforeIo()
    {
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        var error = Assert.Throws<RouterConfigException>(() =>
        {
            using var provider = new DockerLifecycleProvider(
                Cfg(socket: LifecycleAuthorityTests.NamedPipeDockerAddress), "fixture", daemon);
        });

        Assert.Equal(LifecycleAuthorityTests.NamedPipeDockerError, error.Message);

        var missingSocket = Assert.Throws<RouterConfigException>(() =>
        {
            using var provider = new DockerLifecycleProvider(Cfg(socket: null!), "fixture", daemon);
        });
        Assert.Equal("engine.fixture: docker.socket must name a Unix socket or an absolute HTTP(S) daemon base", missingSocket.Message);
        Assert.Empty(daemon.Requests);
    }

    internal static string Inspect(string id = ImageId, string digest = RepoDigest, string os = "linux",
        string architecture = "amd64", string? variant = null)
    {
        var variantField = variant is null ? "" : $",\"Variant\":\"{variant}\"";
        return $"{{\"Id\":\"{id}\",\"RepoDigests\":[\"{digest}\"],\"Os\":\"{os}\",\"Architecture\":\"{architecture}\"{variantField}}}";
    }

    internal static string NativeContainerInspect(string id, string model, string profile = "", string router = "r1",
        string engine = "strata", string image = ImageId, string startedAt = "2026-01-01T00:00:00.123456781Z",
        int restarts = 0, string state = "running", bool running = true, bool paused = false,
        bool restarting = false, bool dead = false) =>
        $$"""
        {"Id":"{{id}}","Image":"{{image}}","RestartCount":{{restarts}},
         "Config":{"Labels":{"arboreal.router":"{{router}}","arboreal.engine":"{{engine}}",
                             "arboreal.model":"{{model}}","arboreal.profile":"{{profile}}" } },
         "State":{"Status":"{{state}}","StartedAt":"{{startedAt}}",
                  "Running":{{(running ? "true" : "false")}},"Paused":{{(paused ? "true" : "false")}},
                  "Restarting":{{(restarting ? "true" : "false")}},"Dead":{{(dead ? "true" : "false")}} } }
        """;

    private static (HttpStatusCode, string) OwnedIncumbent() => (HttpStatusCode.OK,
        $$$"""[{"Id":"{{{Ours}}}","Labels":{"arboreal.router":"r1","arboreal.engine":"strata","arboreal.model":"incumbent"}}]""");

    private static RouterConfig BindingConfig(string socket, bool external = false)
    {
        var mode = external ? "remote = true" : "";
        return RouterConfigLoader.Load($$"""
            [engine.strata]
            kind = "openai-compatible"
            endpoint = "http://localhost:18021/v1"
            {{mode}}

            [engine.strata.docker]
            image = "{{Locator}}"
            image_id = "{{ImageId}}"
            image_repo_digest = "{{RepoDigest}}"
            image_platform = "linux/amd64"
            socket = "{{socket}}"
            router_id = "r1"
            wait_health = false

            [engine.strata.catalog."strata-8b"]
            """);
    }

    [Theory]
    [InlineData("engine name", "other", "r1", LocalSocket, LocalSocket)]
    [InlineData("docker.router_id", "strata", "R1", LocalSocket, LocalSocket)]
    [InlineData("docker.socket", "strata", "r1", LocalSocket, "unix:///run/Docker.sock")]
    [InlineData("docker.socket", "strata", "r1", LocalSocket, "http://docker.engine/")]
    [InlineData("docker.socket", "strata", "r1", RemoteBase, "http://daemon.invalid:2376/docker/")]
    [InlineData("docker.socket", "strata", "r1", RemoteBase, "https://other.invalid:2376/docker/")]
    [InlineData("docker.socket", "strata", "r1", RemoteBase, "https://daemon.invalid:2377/docker/")]
    [InlineData("docker.socket", "strata", "r1", RemoteBase, "https://daemon.invalid:2376/other/")]
    [InlineData("docker.socket", "strata", "r1", RemoteBase + "#first", RemoteBase + "#second")]
    public async Task RuntimeRejectsMismatchedConcreteDockerScopeBeforeIo(string field, string providerEngine,
        string routerId, string expectedSocket, string providerSocket)
    {
        var config = BindingConfig(expectedSocket);
        var def = config.Engines["strata"];
        Assert.Equal(EngineLifecycleOwner.Router, def.LifecycleOwner);
        Assert.True(def.LegacyLifecycleConfiguration);
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var inference = new FakeDockerDaemonHandler { TransportFails = true };
        using var provider = new DockerLifecycleProvider(Cfg(socket: providerSocket, routerId: routerId), providerEngine, daemon);
        var feed = new EventRing();

        var error = await Assert.ThrowsAsync<RouterConfigException>(async () =>
        {
            await using var engine = new EngineRuntime(def, config, feed, inference, lifecycle: provider);
        });

        Assert.Equal($"engine.strata: injected Docker lifecycle provider {field} does not match", error.Message);
        Assert.Empty(daemon.Requests);
        Assert.Empty(inference.Requests);
        Assert.Empty(feed.Replay(0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unix://")]
    public async Task RuntimeRejectsEqualInvalidDockerScopesBeforeIo(string socket)
    {
        var config = BindingConfig(socket);
        var def = config.Engines["strata"];
        Assert.Equal(socket, def.Docker?.Socket);
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var inference = new FakeDockerDaemonHandler { TransportFails = true };

        var error = await Assert.ThrowsAsync<RouterConfigException>(async () =>
        {
            using var provider = new DockerLifecycleProvider(Cfg(socket: socket), def.Name, daemon);
            await using var engine = new EngineRuntime(def, config, new EventRing(), inference, lifecycle: provider);
        });

        Assert.Equal("engine.strata: docker.socket must name a Unix socket or an absolute HTTP(S) daemon base", error.Message);
        Assert.Empty(daemon.Requests);
        Assert.Empty(inference.Requests);
    }

    [Fact(Timeout = 20_000)]
    public async Task RuntimeSwitchesWithMatchingUnixScopeUsingTheSameOwnershipLabels()
    {
        const string replacement = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        var config = BindingConfig(LocalSocket);
        var def = config.Engines["strata"];
        using var inference = new FakeEngineHandler { LoadedModels = ["incumbent"] };
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path =>
            {
                if (path.Contains("/containers/json", StringComparison.Ordinal))
                    return (HttpStatusCode.OK, $$$"""
                        [
                          {"Id":"{{{Ours}}}","Labels":{"arboreal.router":"r1","arboreal.engine":"strata","arboreal.model":"incumbent"}},
                          {"Id":"foreign","Labels":{"arboreal.router":"r1","arboreal.engine":"other","arboreal.model":"other"}}
                        ]
                        """);
                if (path.EndsWith("/version", StringComparison.Ordinal))
                    return (HttpStatusCode.OK, """{"ApiVersion":"1.44"}""");
                if (path.Contains("/images/", StringComparison.Ordinal)) return (HttpStatusCode.OK, Inspect());
                if (path.EndsWith("/containers/create", StringComparison.Ordinal))
                    return (HttpStatusCode.Created, $$"""{"Id":"{{replacement}}"}""");
                if (path.EndsWith($"/containers/{replacement}/json", StringComparison.Ordinal))
                    return (HttpStatusCode.OK, NativeContainerInspect(replacement, "strata-8b"));
                if (path.EndsWith($"/containers/{replacement}/start", StringComparison.Ordinal))
                    inference.LoadedModels = ["strata-8b"];
                return (HttpStatusCode.NoContent, "");
            },
        };
        using var provider = new DockerLifecycleProvider(Cfg(socket: "/run/docker.sock", waitHealth: false), def.Name, daemon);
        await using var engine = new EngineRuntime(def, config, new EventRing(), inference, lifecycle: provider);

        Assert.True((await engine.WaitForModelAsync("strata-8b", TimeSpan.FromSeconds(10), CancellationToken.None)).IsReady);
        Assert.Contains("/models", inference.SeenRoutes);
        const string listPrefix = "GET /v1.44/containers/json?all=true&filters=";
        Assert.StartsWith(listPrefix, daemon.Requests[0]);
        using var filters = JsonDocument.Parse(WebUtility.UrlDecode(daemon.Requests[0][listPrefix.Length..]));
        Assert.Equal(["arboreal.router=r1", "arboreal.engine=strata"],
            filters.RootElement.GetProperty("label").EnumerateArray().Select(label => label.GetString()));
        Assert.Equal([
            "GET /v1.44/version",
            $"GET /v1.44/images/{Uri.EscapeDataString(Locator)}/json",
            $"POST /v1.44/containers/{Ours}/stop?t=30",
            $"DELETE /v1.44/containers/{Ours}?force=true",
            "POST /v1.44/containers/create",
            $"POST /v1.44/containers/{replacement}/start",
            $"GET /v1.44/containers/{replacement}/json",
            $"GET /v1.44/containers/{replacement}/json",
        ], daemon.Requests.Skip(1));
        using var create = JsonDocument.Parse(Assert.Single(daemon.Bodies, body => body.Length != 0));
        Assert.Equal(ImageId, create.RootElement.GetProperty("Image").GetString());
        var labels = create.RootElement.GetProperty("Labels");
        Assert.Equal("r1", labels.GetProperty("arboreal.router").GetString());
        Assert.Equal("strata", labels.GetProperty("arboreal.engine").GetString());
        Assert.Equal("strata-8b", labels.GetProperty("arboreal.model").GetString());
    }

    [Theory]
    [InlineData("https://DAEMON.invalid:2376/docker", false)]
    [InlineData(RemoteBase, true)]
    public async Task RuntimeAcceptsEquivalentLegacyHttpScopeIndependentlyOfReadiness(string socket, bool providerWaitHealth)
    {
        var config = BindingConfig(RemoteBase);
        var def = config.Engines["strata"];
        Assert.True(def.LegacyLifecycleConfiguration);
        Assert.False(Assert.IsType<DockerLifecycleConfig>(def.Docker).WaitHealth);
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var inference = new FakeDockerDaemonHandler { TransportFails = true };
        using var provider = new DockerLifecycleProvider(Cfg(socket: socket, waitHealth: providerWaitHealth), def.Name, daemon);
        Assert.Equal(providerWaitHealth, provider.Readiness(Target()).ProveHealth);
        await using var engine = new EngineRuntime(def, config, new EventRing(), inference, lifecycle: provider);

        Assert.Equal("docker", engine.LifecycleProvider);
        Assert.Empty(daemon.Requests);
        Assert.Empty(inference.Requests);
    }

    [Fact(Timeout = 20_000)]
    public async Task ExternalRuntimeIgnoresMismatchedConcreteDockerScope()
    {
        var config = BindingConfig(LocalSocket, external: true);
        var def = config.Engines["strata"];
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var inference = new FakeEngineHandler { LoadedModels = ["incumbent"] };
        using var provider = new DockerLifecycleProvider(Cfg(socket: RemoteBase, routerId: "other-router"), "other", daemon);
        var feed = new EventRing();
        await using var engine = new EngineRuntime(def, config, feed, inference, lifecycle: provider);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        engine.StartHealthLoop(deadline.Token);
        try
        {
            while (!engine.Healthy) await Task.Delay(10, deadline.Token);
            var proof = engine.Capability;

            Assert.True((await engine.WaitForModelAsync("incumbent", TimeSpan.FromSeconds(5), deadline.Token)).IsReady);
            Assert.Equal("no_lifecycle_owner", (await engine.WaitForModelAsync("strata-8b", TimeSpan.FromSeconds(5), deadline.Token)).Detail);
            Assert.Equal("none", engine.LifecycleProvider);
            Assert.Same(proof, engine.Capability);
            Assert.DoesNotContain(feed.Replay(0), item => item.Kind == "engine.switch_started");
            Assert.Empty(daemon.Requests);
        }
        finally
        {
            await deadline.CancelAsync();
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeLeavesTheInjectedDockerProviderCallerOwned(bool rejected)
    {
        var config = BindingConfig(LocalSocket);
        var def = config.Engines["strata"];
        using var daemon = new FakeDockerDaemonHandler { Responder = _ => (HttpStatusCode.OK, "[]") };
        using var inference = new FakeDockerDaemonHandler { TransportFails = true };
        using var provider = new DockerLifecycleProvider(Cfg(), rejected ? "other" : def.Name, daemon);

        if (rejected)
        {
            await Assert.ThrowsAsync<RouterConfigException>(async () =>
            {
                await using var engine = new EngineRuntime(def, config, new EventRing(), inference, lifecycle: provider);
            });
        }
        else
        {
            await using var engine = new EngineRuntime(def, config, new EventRing(), inference, lifecycle: provider);
        }
        Assert.Empty(daemon.Requests);
        Assert.Empty(inference.Requests);

        // The caller can still use the provider after either constructor refusal or runtime disposal.
        Assert.Empty(await provider.OwnedPeersAsync(Target(), CancellationToken.None));
        Assert.Single(daemon.Requests);
    }

    public static TheoryData<string, string> RejectedImages => new()
    {
        { "", "image_inspect_unverifiable" },
        { "not json", "image_inspect_unverifiable" },
        { "{}", "image_id_unverifiable" },
        { Inspect(id: "sha256:1111"), "image_id_unverifiable" },
        { Inspect(id: DifferentId), "image_id_mismatch" },
        { Inspect(id: Manifest), "image_id_mismatch" },
        { Inspect().Replace("\"RepoDigests\"", "\"OtherDigests\"", StringComparison.Ordinal), "image_repo_digest_unverifiable" },
        { Inspect().Replace($"\"{RepoDigest}\"", "", StringComparison.Ordinal), "image_repo_digest_unverifiable" },
        { Inspect(digest: Manifest), "image_repo_digest_unverifiable" },
        { Inspect(digest: "registry.local/other@" + Manifest), "image_repo_digest_mismatch" },
        { Inspect(digest: "registry.local/strata@" + ImageId), "image_repo_digest_mismatch" },
        { Inspect().Replace("\"Os\"", "\"OtherOs\"", StringComparison.Ordinal), "image_platform_unverifiable" },
        { Inspect(architecture: ""), "image_platform_unverifiable" },
        { Inspect(os: "linux/amd64", architecture: "v3"), "image_platform_unverifiable" },
        { Inspect(variant: " "), "image_platform_unverifiable" },
        { Inspect(os: "windows"), "image_platform_mismatch" },
        { Inspect(architecture: "arm64"), "image_platform_mismatch" },
        { Inspect(variant: "v3"), "image_platform_mismatch" },
    };

    [Theory(Timeout = 20_000)]
    [MemberData(nameof(RejectedImages))]
    public async Task DockerImageIdentityMismatchRefusesBeforeEngineMutation(string imageBody, string reason)
    {
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path switch
            {
                _ when path.Contains("/containers/json", StringComparison.Ordinal) => OwnedIncumbent(),
                _ when path.EndsWith("/version", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"ApiVersion":"1.44"}"""),
                _ when path.Contains("/images/", StringComparison.Ordinal) => (HttpStatusCode.OK, imageBody),
                _ => (HttpStatusCode.InternalServerError, ""),
            },
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);
        using var coordinator = new EngineLifecycleCoordinator(provider, ownsProvider: false);

        var report = await coordinator.SwitchWithReportAsync(Target(), CancellationToken.None);
        var result = report.Result;

        Assert.True(report.RefusedBeforeMutation);
        Assert.False(result.Succeeded);
        Assert.Equal(reason, result.Reason);
        Assert.False(string.IsNullOrWhiteSpace(result.Action));
        Assert.Collection(daemon.Requests,
            request => Assert.StartsWith("GET /v1.44/containers/json?", request),
            request => Assert.Equal("GET /v1.44/version", request),
            request => Assert.Equal($"GET /v1.44/images/{Uri.EscapeDataString(Locator)}/json", request));
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DockerImageIdentityPreflightPinsAnImmutableCreateReference(bool imageDisappears)
    {
        var locatorId = ImageId;
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path =>
            {
                if (path.Contains("/containers/json", StringComparison.Ordinal)) return OwnedIncumbent();
                if (path.EndsWith("/version", StringComparison.Ordinal))
                    return (HttpStatusCode.OK, """{"ApiVersion":"1.44"}""");
                if (path.Contains("/images/", StringComparison.Ordinal)) return (HttpStatusCode.OK, Inspect(locatorId));
                if (path.Contains("/stop?", StringComparison.Ordinal))
                {
                    locatorId = DifferentId;
                    return (HttpStatusCode.NoContent, "");
                }
                if (path.EndsWith("/containers/create", StringComparison.Ordinal))
                    return imageDisappears
                        ? (HttpStatusCode.NotFound, """{"message":"image disappeared"}""")
                        : (HttpStatusCode.Created, """{"Id":"new-container"}""");
                return (HttpStatusCode.NoContent, "");
            },
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);
        using var coordinator = new EngineLifecycleCoordinator(provider, ownsProvider: false);

        var result = await coordinator.SwitchAsync(Target(), CancellationToken.None);

        Assert.Equal(!imageDisappears, result.Succeeded);
        if (imageDisappears) Assert.Equal("http_404", result.Reason);
        Assert.Equal(DifferentId, locatorId);
        Assert.StartsWith("GET /v1.44/containers/json?", daemon.Requests[0]);
        var expected = new List<string>
        {
            "GET /v1.44/version",
            $"GET /v1.44/images/{Uri.EscapeDataString(Locator)}/json",
            $"POST /v1.44/containers/{Ours}/stop?t=30",
            $"DELETE /v1.44/containers/{Ours}?force=true",
            "POST /v1.44/containers/create",
        };
        if (!imageDisappears) expected.Add("POST /v1.44/containers/new-container/start");
        Assert.Equal(expected, daemon.Requests.Skip(1));
        using var create = JsonDocument.Parse(Assert.Single(daemon.Bodies, body => body.Length != 0));
        Assert.Equal(ImageId, create.RootElement.GetProperty("Image").GetString());
    }

    [Theory(Timeout = 20_000)]
    [InlineData("linux/amd64", null, true)]
    [InlineData("linux/amd64", "", true)]
    [InlineData("linux/amd64/v3", "v3", true)]
    [InlineData("linux/amd64/v3", null, false)]
    [InlineData("linux/amd64/v3", "v2", false)]
    public async Task DockerImagePlatformIncludesTheExactVariant(string platform, string? variant, bool valid)
    {
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path.EndsWith("/version", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """{"ApiVersion":"1.44"}""")
                : (HttpStatusCode.OK, Inspect(variant: variant)),
        };
        using var provider = new DockerLifecycleProvider(Cfg(platform: platform), "strata", daemon);

        var result = await provider.PreflightAsync(Target(), CancellationToken.None);

        Assert.Equal(valid, result.Succeeded);
        if (!valid) Assert.Equal("image_platform_mismatch", result.Reason);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectUnpinnedPreflightAndLaunchRefuseWithoutDaemonCalls(bool launch)
    {
        using var daemon = new FakeDockerDaemonHandler();
        using var provider = new DockerLifecycleProvider(Cfg(pinned: false), "strata", daemon);

        var result = launch
            ? await provider.LaunchAsync(Target(), CancellationToken.None)
            : await provider.PreflightAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("image_identity_required", result.Reason);
        Assert.Contains("docker.image_id", result.Action);
        Assert.Empty(daemon.Requests);
    }

    [Fact(Timeout = 20_000)]
    public async Task ImageInspectPropagatesCallerCancellation()
    {
        using var caller = new CancellationTokenSource();
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path =>
            {
                if (path.Contains("/images/", StringComparison.Ordinal))
                {
                    caller.Cancel();
                    caller.Token.ThrowIfCancellationRequested();
                }
                return (HttpStatusCode.OK, """{"ApiVersion":"1.44"}""");
            },
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.PreflightAsync(Target(), caller.Token).AsTask());

        Assert.Equal(["GET /v1.44/version", $"GET /v1.44/images/{Uri.EscapeDataString(Locator)}/json"], daemon.Requests);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("running")]
    [InlineData("restarting")]
    [InlineData("paused")]
    public async Task HostInventoryChecksUnfilteredActiveContainers(string state)
    {
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = _ => (HttpStatusCode.OK,
                $$$"""[{"Id":"sibling","State":"{{{state}}}","Labels":{"arboreal.router":"r1","arboreal.engine":"unrelated"}}]"""),
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);
        var queryId = Guid.NewGuid();

        var observed = await provider.ObserveExclusiveHostAsync(queryId, new HashSet<string>(StringComparer.Ordinal) { "strata" },
            CancellationToken.None);

        Assert.Equal(new HostOccupancy(queryId, HostOccupancyKind.Foreign), observed);
        Assert.Equal(["GET /v1.44/containers/json?all=true"], daemon.Requests);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(HttpStatusCode.OK, "[]")]
    [InlineData(HttpStatusCode.OK, "[null]")]
    [InlineData(HttpStatusCode.OK, "{broken")]
    [InlineData(HttpStatusCode.NoContent, "")]
    [InlineData(HttpStatusCode.Forbidden, "[]")]
    [InlineData(HttpStatusCode.OK, """[{"Id":"managed","State":"running","Labels":{"arboreal.router":"r1","arboreal.engine":"strata"}}]""")]
    public async Task RawDockerInventoryCannotGrantHostAuthority(HttpStatusCode status, string body)
    {
        using var daemon = new FakeDockerDaemonHandler { Responder = _ => (status, body) };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);
        var queryId = Guid.NewGuid();

        var observed = await provider.ObserveExclusiveHostAsync(queryId, new HashSet<string>(StringComparer.Ordinal) { "strata" },
            CancellationToken.None);

        Assert.Equal(new HostOccupancy(queryId, HostOccupancyKind.Unknown), observed);
        Assert.Equal(["GET /v1.44/containers/json?all=true"], daemon.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LostDockerMutationResponseLeavesItsExactOperationUnsettled(bool stop)
    {
        var operation = new HostMutation(1, Guid.NewGuid(), stop ? HostMutationKind.Stop : HostMutationKind.Launch,
            "strata", stop ? Ours : "model-a", null, stop ? null : new string('A', 64));

        var outcome = DockerLifecycleProvider.ReconcileUnacknowledgedHostMutation(operation);

        Assert.Equal(new HostMutationOutcome(operation, HostMutationStatus.UnknownAfterIssue), outcome);
    }

    [Fact(Timeout = 20_000)]
    public async Task PreflightRefusesAnImageThatIsNotOnTheHost_AndPullsNothing()
    {
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path switch
            {
                _ when path.EndsWith("/version", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"ApiVersion":"1.44"}"""),
                _ when path.Contains("/images/") => (HttpStatusCode.NotFound, """{"message":"No such image"}"""),
                _ => (HttpStatusCode.InternalServerError, "{}"),
            },
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);

        var result = await provider.PreflightAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("image_not_present", result.Reason);
        Assert.Contains("never pulls", result.Action);
        // The whole point of preflight: nothing was mutated on the way to that refusal.
        Assert.DoesNotContain(daemon.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
        Assert.DoesNotContain(daemon.Requests, r => r.Contains("/pull", StringComparison.Ordinal));
    }

    [Fact(Timeout = 20_000)]
    public async Task PreflightRefusesADaemonOlderThanThePinnedApi()
    {
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path.EndsWith("/version", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """{"ApiVersion":"1.24"}""")
                : (HttpStatusCode.OK, "{}"),
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);

        var result = await provider.PreflightAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("daemon_api_1.24_below_v1.44", result.Reason);
    }

    [Fact(Timeout = 20_000)]
    public async Task AContainerWithoutOurLabelsIsNotOursEvenWhenTheDaemonListsIt()
    {
        // The label filter is an optimisation; the local check is the proof. A daemon (or an operator
        // editing labels) that hands back somebody else's container must not make it ours to stop.
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = _ => (HttpStatusCode.OK,
                $$$"""[{"Id":"{{{Ours}}}","Image":"img","State":"running","Labels":{"arboreal.router":"r1","arboreal.engine":"strata","arboreal.model":"old"}},""" +
                """{"Id":"foreign","Image":"img","State":"running","Labels":{"arboreal.router":"someone-else"}}]"""),
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);

        var peers = await provider.OwnedPeersAsync(Target(), CancellationToken.None);

        Assert.Equal(2, peers.Count);
        Assert.True(peers.Single(p => p.Id == Ours).OwnedByUs);
        Assert.False(peers.Single(p => p.Id == "foreign").OwnedByUs);
    }

    [Fact(Timeout = 20_000)]
    public async Task StopRefusesAPeerWeDoNotOwn_AndSendsNothingForIt()
    {
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = _ => (HttpStatusCode.NoContent, ""),
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);

        var result = await provider.StopAsync(Target(),
            [new EnginePeer("foreign", "img", null, OwnedByUs: false)], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("refused_to_stop_unowned", result.Reason);
        Assert.Empty(daemon.Requests);
    }

    [Fact(Timeout = 20_000)]
    public async Task LaunchCreatesTheContainerWithOurLabelsAndThenStartsIt()
    {
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path.Contains("/containers/create")
                ? (HttpStatusCode.Created, """{"Id":"deadbeefcafebabe","Warnings":[]}""")
                : (HttpStatusCode.NoContent, ""),
        };
        var config = Cfg();
        using var provider = new DockerLifecycleProvider(config, "strata", daemon);
        config.Command[0] = "changed-command";
        config.Env[0] = "changed-environment";

        var result = await provider.LaunchAsync(Target(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["POST /v1.44/containers/create", "POST /v1.44/containers/abc/start"],
            daemon.Requests.Select(r => r.Replace("deadbeefcafebabe", "abc")).ToArray());
        var created = daemon.Bodies.Single(b => b.Contains("arboreal.router"));
        Assert.Contains("\"arboreal.model\":\"strata-8b\"", created);
        Assert.Contains($"\"Image\":\"{ImageId}\"", created);
        Assert.Contains("--model", created);
        Assert.Contains("--ctx", created);         // {{profile}} expanded, empty because no profile
        Assert.Contains("AIR_ENGINE=strata", created);
        Assert.DoesNotContain("changed-command", created);
        Assert.DoesNotContain("changed-environment", created);
    }

    [Fact(Timeout = 20_000)]
    public async Task EveryCallUsesThePinnedApiVersion()
    {
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path.Contains("/images/")
                ? (HttpStatusCode.OK, Inspect())
                : path.Contains("/version")
                    ? (HttpStatusCode.OK, """{"ApiVersion":"1.44"}""")
                    : path.Contains("/containers/create")
                        ? (HttpStatusCode.Created, """{"Id":"new-container"}""")
                        : (HttpStatusCode.NoContent, ""),
        };
        using var provider = new DockerLifecycleProvider(Cfg("v1.41"), "strata", daemon);

        Assert.True((await provider.PreflightAsync(Target(), CancellationToken.None)).Succeeded);
        await provider.OwnedPeersAsync(Target(), CancellationToken.None);
        Assert.True((await provider.LaunchAsync(Target(), CancellationToken.None)).Succeeded);

        Assert.NotEmpty(daemon.Requests);
        Assert.All(daemon.Requests, r => Assert.Contains("/v1.41/", r));
    }

    [Fact(Timeout = 20_000)]
    public async Task AnUnreachableDaemonIsOneFailureThatNamesTheSocketAndRefusesToEscalate()
    {
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);

        var result = await provider.PreflightAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("daemon_unreachable", result.Reason);
        Assert.Contains("unix:///run/docker.sock", result.Action);
        Assert.Contains("not retry with elevated rights", result.Action);
    }


    [Fact(Timeout = 20_000)]
    public async Task AWedgedDaemonIsCutOffByThePerCallDeadlineInsteadOfHoldingTheSwitchOpen()
    {
        // The wait_timeout is how long a model may take to load. It is not a licence for one HTTP call to
        // hang forever: a daemon that accepts the connection and says nothing would otherwise keep the
        // engine in "switching" and the ticket waiting on a socket that will never answer.
        using var daemon = new FakeDockerDaemonHandler
        {
            StallMs = 4_000,
            Responder = _ => (HttpStatusCode.OK, "{}"),
        };
        var cfg = Cfg();
        cfg = new DockerLifecycleConfig
        {
            Image = cfg.Image, ImageIdentity = cfg.ImageIdentity, Socket = cfg.Socket, ApiVersion = cfg.ApiVersion, RouterId = cfg.RouterId,
            Command = cfg.Command, Env = cfg.Env, HostPort = cfg.HostPort, ContainerPort = cfg.ContainerPort,
            CallTimeoutMs = 150,
        };
        using var provider = new DockerLifecycleProvider(cfg, "strata", daemon);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));   // the test itself must not hang

        var result = await provider.PreflightAsync(Target(), timeout.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("daemon_call_timeout_150ms", result.Reason);
        Assert.Contains("wedged", result.Action);
    }

    [Fact(Timeout = 20_000)]
    public async Task AFailedOwnershipQueryStopsTheTransactionRatherThanAssumingNothingIsRunning()
    {
        // If the daemon will not answer, "I could not ask" must not become "there is nothing to stop":
        // that mistake starts a second copy of a model that is already loaded.
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);
        using var coordinator = new EngineLifecycleCoordinator(provider, ownsProvider: false);

        var result = await coordinator.SwitchAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("ownership_query_failed", result.Reason);
        Assert.DoesNotContain(daemon.Requests, r => r.Contains("/containers/create", StringComparison.Ordinal));
    }

    [Fact(Timeout = 20_000)]
    public async Task AListThatAnswersTwoHundredWithGarbageIsNotReadAsNothingRunning()
    {
        // An unparsable 200 is not "no containers". Read as an empty list it would let the router start a
        // second copy of a model that is already loaded, which is the exact mistake the ownership proof exists to stop.
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path.Contains("/containers/json")
                ? (HttpStatusCode.OK, "this is not JSON")
                : (HttpStatusCode.NoContent, ""),
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);
        using var coordinator = new EngineLifecycleCoordinator(provider, ownsProvider: false);

        var result = await coordinator.SwitchAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("ownership_query_failed daemon:unparsable_container_list", result.Reason);
        Assert.DoesNotContain(daemon.Requests, r => r.Contains("/containers/create", StringComparison.Ordinal));
    }

    [Fact(Timeout = 20_000)]
    public async Task APublishedPortAlwaysNamesTheInterfaceItBindsAndDefaultsToLoopback()
    {
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path.Contains("/containers/create")
                ? (HttpStatusCode.Created, """{"Id":"deadbeefcafebabe","Warnings":[]}""")
                : (HttpStatusCode.NoContent, ""),
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);

        var result = await provider.LaunchAsync(Target(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var created = daemon.Bodies.Single(b => b.Contains("PortBindings", StringComparison.Ordinal));
        Assert.Contains("\"HostIp\":\"127.0.0.1\"", created);   // an empty HostIp would mean every interface
        Assert.Contains("\"HostPort\":\"18021\"", created);
    }

    [Fact(Timeout = 20_000)]
    public async Task AnOperatorCanWidenTheInterfaceButTheRouterNeverInventsOne()
    {
        var cfg = Cfg();
        cfg = new DockerLifecycleConfig
        {
            Image = cfg.Image, ImageIdentity = cfg.ImageIdentity, Socket = cfg.Socket, ApiVersion = cfg.ApiVersion, RouterId = cfg.RouterId,
            Command = cfg.Command, Env = cfg.Env, HostPort = cfg.HostPort, ContainerPort = cfg.ContainerPort,
            HostIp = "10.0.0.5",
        };
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path.Contains("/containers/create")
                ? (HttpStatusCode.Created, """{"Id":"deadbeefcafebabe","Warnings":[]}""")
                : (HttpStatusCode.NoContent, ""),
        };
        using var provider = new DockerLifecycleProvider(cfg, "strata", daemon);

        var result = await provider.LaunchAsync(Target(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("\"HostIp\":\"10.0.0.5\"", daemon.Bodies.Single(b => b.Contains("PortBindings", StringComparison.Ordinal)));
    }

    [Fact(Timeout = 20_000)]
    public async Task StoppingRemovesTheContainerWeStoppedSoSwitchesDoNotAccumulateThem()
    {
        using var daemon = new FakeDockerDaemonHandler { Responder = _ => (HttpStatusCode.NoContent, "") };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);

        var result = await provider.StopAsync(Target(), [new EnginePeer(Ours, "img", "strata-8b", OwnedByUs: true)], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [$"POST /v1.44/containers/{Ours}/stop?t=30", $"DELETE /v1.44/containers/{Ours}?force=true"],
            daemon.Requests.ToArray());
    }

    [Fact(Timeout = 20_000)]
    public async Task LeavingStoppedContainersBehindIsAnOptOutNotATypo()
    {
        var cfg = Cfg();
        cfg = new DockerLifecycleConfig
        {
            Image = cfg.Image, ImageIdentity = cfg.ImageIdentity, Socket = cfg.Socket, ApiVersion = cfg.ApiVersion, RouterId = cfg.RouterId,
            Command = cfg.Command, Env = cfg.Env, HostPort = cfg.HostPort, ContainerPort = cfg.ContainerPort,
            RemoveStopped = false,
        };
        using var daemon = new FakeDockerDaemonHandler { Responder = _ => (HttpStatusCode.NoContent, "") };
        using var provider = new DockerLifecycleProvider(cfg, "strata", daemon);

        var result = await provider.StopAsync(Target(), [new EnginePeer(Ours, "img", "strata-8b", OwnedByUs: true)], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([$"POST /v1.44/containers/{Ours}/stop?t=30"], daemon.Requests.ToArray());
    }

    [Fact(Timeout = 20_000)]
    public async Task AStartThatAnswersFourOhFourIsAFailureNotALaunchThatWorked()
    {
        // The daemon just handed us an id and then says that container does not exist. Reporting success here
        // leaves the engine marked loaded with nothing behind it, and the first request gets a connection error.
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path.Contains("/containers/create")
                ? (HttpStatusCode.Created, """{"Id":"deadbeefcafebabe","Warnings":[]}""")
                : (HttpStatusCode.NotFound, """{"message":"No such container:"}"""),
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);

        var result = await provider.LaunchAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("http_404", result.Reason);
        Assert.Contains("404", result.Action);
    }

    [Fact(Timeout = 20_000)]
    public async Task ACreateThatNamesNoContainerIsAFailureBeforeAnyStart()
    {
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path.Contains("/containers/create")
                ? (HttpStatusCode.Created, """{"Warnings":[]}""")
                : (HttpStatusCode.NoContent, ""),
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);

        var result = await provider.LaunchAsync(Target(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("create_returned_no_id", result.Reason);
        Assert.Equal(["POST /v1.44/containers/create"], daemon.Requests.ToArray());
    }

    private const string ExitedState = """{"Status":"exited","Running":false,"Restarting":false,"Paused":false,"Dead":false}""";

    private static string StopConflictInspect(string id = FullPeerId, string router = "r1", string engine = "strata",
        string state = ExitedState) =>
        $$$"""{"Id":"{{{id}}}","Config":{"Labels":{"arboreal.router":"{{{router}}}","arboreal.engine":"{{{engine}}}"}},"State":{{{state}}}}""";

    [Theory(Timeout = 20_000)]
    [InlineData("exited", true)]
    [InlineData("exited", false)]
    [InlineData("absent", true)]
    [InlineData("absent", false)]
    [InlineData("running", true)]
    [InlineData("running", false)]
    [InlineData("wrong-id", true)]
    [InlineData("wrong-router", true)]
    [InlineData("wrong-engine", true)]
    [InlineData("missing-state", true)]
    [InlineData("malformed", true)]
    [InlineData("short-id", true)]
    [InlineData("unexpected-inspect", true)]
    [InlineData("restart", true)]
    [InlineData("unexpected-stop", true)]
    [InlineData("unexpected-remove", true)]
    public async Task StopConflictRequiresExactStoppedInstance(string scenario, bool removeStopped)
    {
        var id = scenario == "short-id" ? FullPeerId[..12] : FullPeerId;
        var containerPath = $"/v1.44/containers/{id}";
        var stopPath = $"{containerPath}/stop?t=30";
        var inspectPath = $"{containerPath}/json";
        var imagePath = $"/v1.44/images/{Uri.EscapeDataString(Locator)}/json";
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path => path switch
            {
                var list when list.StartsWith("/v1.44/containers/json?all=true&filters=", StringComparison.Ordinal) =>
                    (HttpStatusCode.OK, $$$"""[{"Id":"{{{id}}}","Labels":{"arboreal.router":"r1","arboreal.engine":"strata"}}]"""),
                "/v1.44/version" => (HttpStatusCode.OK, """{"ApiVersion":"1.44"}"""),
                _ when path == imagePath => (HttpStatusCode.OK, Inspect()),
                _ when path == stopPath => (scenario == "unexpected-stop" ? HttpStatusCode.Accepted : HttpStatusCode.Conflict, ""),
                _ when path == inspectPath => scenario switch
                {
                    "absent" => (HttpStatusCode.NotFound, ""),
                    "running" => (HttpStatusCode.OK, StopConflictInspect(state:
                        """{"Status":"exited","Running":true,"Restarting":false,"Paused":false,"Dead":false}""")),
                    "wrong-id" => (HttpStatusCode.OK, StopConflictInspect(id: FullPeerId.ToUpperInvariant())),
                    "wrong-router" => (HttpStatusCode.OK, StopConflictInspect(router: "other")),
                    "wrong-engine" => (HttpStatusCode.OK, StopConflictInspect(engine: "other")),
                    "missing-state" => (HttpStatusCode.OK, StopConflictInspect(state:
                        """{"Status":"exited","Running":false,"Paused":false,"Dead":false}""")),
                    "malformed" => (HttpStatusCode.OK, "{"),
                    "unexpected-inspect" => (HttpStatusCode.Accepted, StopConflictInspect()),
                    _ => (HttpStatusCode.OK, StopConflictInspect()),
                },
                _ when path == containerPath => (scenario switch
                {
                    "restart" => HttpStatusCode.Conflict,
                    "unexpected-remove" => HttpStatusCode.Accepted,
                    _ => HttpStatusCode.NoContent,
                }, ""),
                "/v1.44/containers/create" => (HttpStatusCode.Created, """{"Id":"replacement"}"""),
                "/v1.44/containers/replacement/start" => (HttpStatusCode.NoContent, ""),
                _ => throw new InvalidOperationException($"Unexpected fake daemon request {path}"),
            },
        };
        using var provider = new DockerLifecycleProvider(Cfg(removeStopped: removeStopped), "strata", daemon);
        using var coordinator = new EngineLifecycleCoordinator(provider);

        var result = await coordinator.SwitchAsync(Target(), CancellationToken.None);

        var replacement = scenario is "exited" or "absent";
        Assert.Equal(replacement, result.Succeeded);
        if (!replacement)
            Assert.Equal(scenario switch
            {
                "short-id" => "native_peer_id_unverifiable",
                "unexpected-stop" or "unexpected-remove" => "http_202",
                _ => "http_409",
            }, result.Reason);
        Assert.StartsWith("GET /v1.44/containers/json?all=true&filters=", daemon.Requests[0], StringComparison.Ordinal);
        var expected = new List<string> { "GET /v1.44/version", $"GET {imagePath}" };
        if (scenario != "short-id") expected.Add($"POST {stopPath}");
        if (scenario is not ("short-id" or "unexpected-stop")) expected.Add($"GET {inspectPath}");
        if (removeStopped && scenario is ("exited" or "restart" or "unexpected-remove"))
            expected.Add($"DELETE {containerPath}");
        if (replacement)
        {
            expected.Add("POST /v1.44/containers/create");
            expected.Add("POST /v1.44/containers/replacement/start");
        }
        Assert.Equal(expected, daemon.Requests.Skip(1));
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, false, "timeout")]
    [InlineData(false, true, "disconnect")]
    [InlineData(false, true, "conflict")]
    [InlineData(true, true, "accepted")]
    [InlineData(true, true, "server-error")]
    [InlineData(true, true, "cancelled")]
    public async Task UncertainPeerMutationFencesReentry(bool delete, bool removeStopped, string fault)
    {
        const string inventory = "/v1.44/containers/json?";
        var id = fault == "disconnect" ? FullPeerId.ToUpperInvariant() : FullPeerId;
        var container = $"/v1.44/containers/{id}";
        var stop = $"{container}/stop?t=30";
        var removal = $"{container}?force=true";
        var affected = delete ? removal : stop;
        var phase = "issue";
        using var caller = new CancellationTokenSource();
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path =>
            {
                if (path == affected && phase == "issue")
                {
                    if (fault == "cancelled")
                    {
                        caller.Cancel();
                        caller.Token.ThrowIfCancellationRequested();
                    }
                    return fault switch
                    {
                        "disconnect" => throw new HttpRequestException("lost response"),
                        "timeout" => throw new OperationCanceledException(),
                        "conflict" => (HttpStatusCode.Conflict, ""),
                        "server-error" => (HttpStatusCode.InternalServerError, ""),
                        _ => (HttpStatusCode.Accepted, ""),
                    };
                }
                if (path == $"{container}/json")
                    return phase switch
                    {
                        "issue" => (HttpStatusCode.OK, StopConflictInspect(id: id, state: """{"Status":"running","Running":true}""")),
                        "present" => (HttpStatusCode.OK, StopConflictInspect(id: id)),
                        _ => (HttpStatusCode.NotFound, ""),
                    };
                if (path.StartsWith(inventory, StringComparison.Ordinal))
                    return phase switch
                    {
                        "issue" => (HttpStatusCode.OK,
                            $$$"""[{"Id":"{{{id}}}","Labels":{"arboreal.router":"r1","arboreal.engine":"strata"}}]"""),
                        "unexpected-list" => (HttpStatusCode.Accepted, ""),
                        "malformed-list" => (HttpStatusCode.OK, "[null]"),
                        "case-alias" => (HttpStatusCode.OK,
                            $$$"""[{"Id":"{{{id.ToUpperInvariant()}}}","Labels":{"arboreal.router":"foreign"}}]"""),
                        _ => (HttpStatusCode.OK, "[]"),
                    };
                if (path == "/v1.44/version") return (HttpStatusCode.OK, """{"ApiVersion":"1.44"}""");
                if (path.Contains("/images/", StringComparison.Ordinal)) return (HttpStatusCode.OK, Inspect());
                if (path == stop && delete && phase == "issue") return (HttpStatusCode.NoContent, "");
                if (path == "/v1.44/containers/create") return (HttpStatusCode.Created, """{"Id":"replacement"}""");
                if (path == "/v1.44/containers/replacement/start") return (HttpStatusCode.NoContent, "");
                throw new InvalidOperationException($"Unexpected fake daemon request {path}");
            },
        };
        using var provider = new DockerLifecycleProvider(Cfg(removeStopped: removeStopped), "strata", daemon);
        using var coordinator = new EngineLifecycleCoordinator(provider);
        var target = Target();
        EnginePeer[] peers = [new(id, Locator, "incumbent", OwnedByUs: true)];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.StopAsync(target, peers, new CancellationToken(canceled: true)).AsTask());
        Assert.Empty(daemon.Requests);

        if (fault == "cancelled")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.SwitchAsync(target, caller.Token).AsTask());
        else
        {
            var failed = await coordinator.SwitchAsync(target, caller.Token);
            Assert.False(failed.Succeeded);
            if (delete) Assert.Equal(NativeStopDisposition.Stopped, Assert.Single(failed.NativeStops).Stop);
        }

        var tail = daemon.Requests.Count;
        var blocked = await provider.StopAsync(target, peers, CancellationToken.None);
        Assert.Equal("native_peer_mutation_unresolved", blocked.Reason);
        Assert.Contains(delete ? "Delete" : "Stop", blocked.Action, StringComparison.Ordinal);
        Assert.Contains(id, blocked.Action, StringComparison.Ordinal);
        Assert.Equal("native_peer_mutation_unresolved", (await provider.LaunchAsync(target, CancellationToken.None)).Reason);
        Assert.Equal(tail, daemon.Requests.Count);

        phase = "present";
        Assert.False((await coordinator.SwitchAsync(target, CancellationToken.None)).Succeeded);
        Assert.Equal([$"GET {container}/json"], daemon.Requests.Skip(tail).ToArray());
        if (fault == "timeout")
        {
            foreach (var invalid in new[] { "unexpected-list", "malformed-list", "case-alias" })
            {
                phase = invalid;
                tail = daemon.Requests.Count;
                Assert.False((await coordinator.SwitchAsync(target, CancellationToken.None)).Succeeded);
                Assert.Equal(tail + 2, daemon.Requests.Count);
                Assert.Equal($"GET {container}/json", daemon.Requests[tail]);
                Assert.StartsWith($"GET {inventory}", daemon.Requests[tail + 1], StringComparison.Ordinal);
            }
        }

        phase = "vacant";
        tail = daemon.Requests.Count;
        Assert.True((await coordinator.SwitchAsync(target, CancellationToken.None)).Succeeded);
        Assert.Equal($"GET {container}/json", daemon.Requests[tail]);
        Assert.StartsWith($"GET {inventory}", daemon.Requests[tail + 1], StringComparison.Ordinal);
        Assert.Single(daemon.Requests, request => request == $"POST {stop}");
        Assert.Equal(delete ? 1 : 0, daemon.Requests.Count(request => request == $"DELETE {removal}"));
        Assert.Single(daemon.Requests, request => request == "POST /v1.44/containers/create");
        Assert.Single(daemon.Requests, request => request == "POST /v1.44/containers/replacement/start");
    }

    [Fact(Timeout = 20_000)]
    public async Task DirectLaunchCannotPassTheHeldRecoveryInventory()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var container = $"/v1.44/containers/{FullPeerId}";
        using var daemon = new FakeDockerDaemonHandler
        {
            AsyncResponder = async (path, ct) =>
            {
                if (path == $"{container}/stop?t=30") return (HttpStatusCode.Accepted, "");
                if (path == $"{container}/json") return (HttpStatusCode.NotFound, "");
                if (!path.StartsWith("/v1.44/containers/json?", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Unexpected fake daemon request {path}");
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return (HttpStatusCode.Accepted, "");
            },
        };
        using var provider = new DockerLifecycleProvider(Cfg(removeStopped: false), "strata", daemon);
        using var coordinator = new EngineLifecycleCoordinator(provider);
        var target = Target();
        Assert.False((await provider.StopAsync(target,
            [new EnginePeer(FullPeerId, Locator, "incumbent", OwnedByUs: true)], CancellationToken.None)).Succeeded);

        var recovery = coordinator.SwitchAsync(target, CancellationToken.None).AsTask();
        Task<LifecycleResult> direct;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            direct = provider.LaunchAsync(target, CancellationToken.None).AsTask();
            Assert.False(direct.IsCompleted);
            Assert.Equal(3, daemon.Requests.Count);
        }
        finally { release.TrySetResult(); }

        Assert.False((await recovery).Succeeded);
        Assert.Equal("native_peer_mutation_unresolved", (await direct).Reason);
        Assert.Equal(3, daemon.Requests.Count);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(204, "incumbent", true)]
    [InlineData(204, "different-model", false)]
    [InlineData(304, "incumbent", false)]
    [InlineData(404, "incumbent", false)]
    [InlineData(409, "incumbent", false)]
    public async Task NativeShutdownTimingRequiresTheOwnedIncumbentAndAnActualStop(
        int stopStatus, string proofModel, bool comparable)
    {
        var clock = new TimingClock();
        var id = FullPeerId;
        var containerPath = $"/v1.44/containers/{id}";
        var removalPath = stopStatus == 409 ? containerPath : $"{containerPath}?force=true";
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path =>
            {
                if (path.Contains("/containers/json", StringComparison.Ordinal))
                    return (HttpStatusCode.OK,
                        $$$"""[{"Id":"{{{id}}}","ImageID":"{{{ImageId}}}","Labels":{"arboreal.router":"r1","arboreal.engine":"strata","arboreal.model":"incumbent","arboreal.profile":"p"}}]""");
                if (path.EndsWith("/version", StringComparison.Ordinal))
                    return (HttpStatusCode.OK, """{"ApiVersion":"1.44"}""");
                if (path.Contains("/images/", StringComparison.Ordinal)) return (HttpStatusCode.OK, Inspect());
                if (path.Contains("/stop?", StringComparison.Ordinal))
                {
                    clock.Advance(TimeSpan.FromMilliseconds(7));
                    return ((HttpStatusCode)stopStatus, "");
                }
                if (stopStatus == 409 && path == $"{containerPath}/json") return (HttpStatusCode.OK, StopConflictInspect());
                if (path == removalPath)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(2));
                    return (HttpStatusCode.NoContent, "");
                }
                if (path.EndsWith("/create", StringComparison.Ordinal))
                    return (HttpStatusCode.Created, """{"Id":"replacement"}""");
                if (path.EndsWith("/start", StringComparison.Ordinal)) return (HttpStatusCode.NoContent, "");
                return (HttpStatusCode.InternalServerError, "");
            },
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);
        using var coordinator = new EngineLifecycleCoordinator(provider);
        var runtime = new Guid("78e6d6db-f0b8-4725-8953-79fcd2145163");
        var target = Target();
        var incumbent = new SwitchSampleKey(runtime, DockerLifecycleProvider.ProviderKind, proofModel, "p", 0,
            SwitchObservationClass.FreshContainer, ImageId);
        var incoming = incumbent with { CanonicalModel = target.Model, EffectiveProfile = target.EffectiveProfile };
        var proof = new SwitchServingProof(new SwitchAttemptId(runtime, 1), 1, incumbent,
            new NativeLaunch(id, "incumbent", "p", ImageId));
        var attempt = new SwitchAttempt(clock, new SwitchAttemptId(runtime, 2), incoming);

        var report = await coordinator.SwitchWithReportAsync(target, attempt, proof, CancellationToken.None);

        Assert.True(report.Result.Succeeded);
        var record = Assert.IsType<SwitchAttemptRecord>(attempt.TrySeal(SwitchAttemptOutcome.Ready));
        Assert.Equal(comparable ? incumbent : (SwitchSampleKey?)null, record.ShutdownSource);
        var shutdown = Assert.IsType<SwitchStageObservation>(record.GetStage(SwitchStage.Shutdown));
        Assert.Equal(TimeSpan.FromMilliseconds(9), clock.GetElapsedTime(shutdown.StartedAt, shutdown.ObservedUntil));
        var receipt = Assert.Single(record.NativeStops);
        if (stopStatus == 409)
        {
            Assert.Equal(NativeStopDisposition.AlreadyStopped, receipt.Stop);
            Assert.Contains($"GET {containerPath}/json", daemon.Requests);
            Assert.Contains($"DELETE {containerPath}", daemon.Requests);
        }
        Assert.Equal(NativeRemovalDisposition.Removed, receipt.Removal);
        Assert.NotNull(record.NativeLaunch);
        Assert.Null(record.GetStage(SwitchStage.Readiness));
    }

    [Fact(Timeout = 20_000)]
    public async Task CancellationDuringRemovalKeepsTheStopReceiptAndCensorsShutdownTiming()
    {
        using var caller = new CancellationTokenSource();
        using var daemon = new FakeDockerDaemonHandler
        {
            Responder = path =>
            {
                if (path.Contains("/stop?", StringComparison.Ordinal)) return (HttpStatusCode.NoContent, "");
                caller.Cancel();
                caller.Token.ThrowIfCancellationRequested();
                return (HttpStatusCode.InternalServerError, "");
            },
        };
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);
        var peer = new EnginePeer(Ours, Locator, "incumbent", OwnedByUs: true)
        {
            ImageId = ImageId,
            EffectiveProfile = "p",
        };
        var runtime = new Guid("78e6d6db-f0b8-4725-8953-79fcd2145163");
        var key = new SwitchSampleKey(runtime, DockerLifecycleProvider.ProviderKind, "replacement", "", 0,
            SwitchObservationClass.FreshContainer, ImageId);
        var attempt = new SwitchAttempt(new TimingClock(), new SwitchAttemptId(runtime, 1), key);
        Assert.True(attempt.TryStartStage(SwitchStage.Shutdown));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.StopWithObservationsAsync([peer], attempt.ObserveNativeStop, caller.Token).AsTask());

        var record = Assert.IsType<SwitchAttemptRecord>(attempt.TrySeal(SwitchAttemptOutcome.Cancelled));
        Assert.Null(record.ShutdownSource);
        var stop = Assert.Single(record.NativeStops);
        Assert.Equal(NativeStopDisposition.Stopped, stop.Stop);
        Assert.Equal(NativeRemovalDisposition.Incomplete, stop.Removal);
        Assert.Equal(SwitchStageOutcome.Incomplete,
            Assert.IsType<SwitchStageObservation>(record.GetStage(SwitchStage.Shutdown)).Outcome);
    }

    private sealed class TimingClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
