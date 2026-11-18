using System.Net;
using System.Text;
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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        Requests.Add($"{request.Method.Method} {path}");
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Bodies.Add(body);

        if (TransportFails) throw new HttpRequestException("connection refused");

        if (StallMs > 0) await Task.Delay(StallMs, cancellationToken);
        var (status, text) = Responder(path);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(text, Encoding.UTF8, "application/json"),
        };
    }

}

public sealed class DockerLifecycleProviderTests
{
    private const string Ours = "abc123def456";

    private static DockerLifecycleConfig Cfg(string api = "v1.44") => new()
    {
        Image = "registry.local/strata:2026-11",
        Socket = "unix:///run/docker.sock",
        ApiVersion = api,
        RouterId = "r1",
        Command = ["--model", "{{model}}", "--ctx", "{{profile}}"],
        Env = ["AIR_ENGINE={{endpoint}}"],
        HostPort = 18021,
        ContainerPort = 8080,
    };

    private static LifecycleTarget Target() => new(
        "strata", "strata-8b", null,
        new EngineModelDefinition { Name = "strata-8b" });

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
                """[{"Id":"abc123def456","Image":"img","State":"running","Labels":{"arboreal.router":"r1","arboreal.engine":"strata","arboreal.model":"old"}},""" +
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
        Assert.Contains("\"Image\":\"registry.local/strata:2026-11\"", created);
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
                ? (HttpStatusCode.OK, """{"Id":"sha256:x"}""")
                : path.Contains("/version")
                    ? (HttpStatusCode.OK, """{"ApiVersion":"1.44"}""")
                    : (HttpStatusCode.NoContent, ""),
        };
        using var provider = new DockerLifecycleProvider(Cfg("v1.41"), "strata", daemon);

        await provider.PreflightAsync(Target(), CancellationToken.None);
        await provider.OwnedPeersAsync(Target(), CancellationToken.None);
        await provider.LaunchAsync(Target(), CancellationToken.None);

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
            Image = cfg.Image, Socket = cfg.Socket, ApiVersion = cfg.ApiVersion, RouterId = cfg.RouterId,
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
            Image = cfg.Image, Socket = cfg.Socket, ApiVersion = cfg.ApiVersion, RouterId = cfg.RouterId,
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
            ["POST /v1.44/containers/abc123def456/stop?t=30", "DELETE /v1.44/containers/abc123def456?force=true"],
            daemon.Requests.ToArray());
    }

    [Fact(Timeout = 20_000)]
    public async Task LeavingStoppedContainersBehindIsAnOptOutNotATypo()
    {
        var cfg = Cfg();
        cfg = new DockerLifecycleConfig
        {
            Image = cfg.Image, Socket = cfg.Socket, ApiVersion = cfg.ApiVersion, RouterId = cfg.RouterId,
            Command = cfg.Command, Env = cfg.Env, HostPort = cfg.HostPort, ContainerPort = cfg.ContainerPort,
            RemoveStopped = false,
        };
        using var daemon = new FakeDockerDaemonHandler { Responder = _ => (HttpStatusCode.NoContent, "") };
        using var provider = new DockerLifecycleProvider(cfg, "strata", daemon);

        var result = await provider.StopAsync(Target(), [new EnginePeer(Ours, "img", "strata-8b", OwnedByUs: true)], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["POST /v1.44/containers/abc123def456/stop?t=30"], daemon.Requests.ToArray());
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
}
