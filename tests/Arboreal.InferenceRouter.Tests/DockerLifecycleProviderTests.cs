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
    public Func<string, (HttpStatusCode Status, string Body)> Responder { get; set; } =
        _ => (HttpStatusCode.NotFound, "{}");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        Requests.Add($"{request.Method.Method} {path}");
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Bodies.Add(body);

        if (TransportFails) throw new HttpRequestException("connection refused");

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
        using var provider = new DockerLifecycleProvider(Cfg(), "strata", daemon);

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
}
