using System.Text;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CombinedHostManagerActivatesAndSendsTheOriginalColdTicket(bool ready)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var readiness = new AdmissionFixtureBarrier();
        using var managed = new FakeEngineHandler { LoadedModels = [] };
        using var remote = new FakeEngineHandler { LoadedModels = ["outside"] };
        var ct = watchdog.Token;
        var first = ManagedEngine("first", "first-model", "first-alias", 19001);
        var alias = ManagedEngine("alias", "alias-model", "second-alias", 19002);
        var external = new EngineDefinition { Name = "external", Endpoint = new("http://external.invalid/v1"), Remote = true };
        var config = new RouterConfig
        {
            AutoSwitch = true,
            Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal)
                { [first.Name] = first, [alias.Name] = alias, [external.Name] = external },
            HttpHandlerFactory = definition => definition == external ? remote : managed,
        };

        var domain = new ExactTicketAdmission.DomainIdentity("enrolled-domain", 3);
        var host = new FakeHost(readinessTimeoutMs: 10_000);
        var staging = new HostClosedRouteStaging(host.FindLaunch, (launch, generation) =>
            new(domain.Name, domain.Generation, launch.Operation.Engine, launch.Operation.PlanDigest!, launch.InstanceId,
                launch.InstanceId + "-serving", launch.Operation.Epoch, generation), enrolledDomain: domain);
        var issuer = new CertifiedPairIssuer(staging);
        var controller = new HostLifecycleCoordinator(ReservationPath, host, staging);
        HostReservation.ProvisionNew(ReservationPath);
        var fixture = new HostStagedRuntimeFixture(async (stage, token) =>
        {
            await readiness.HoldAsync(token);
            return ready ? new(stage, stage.Launch.Plan.Model, new(stage.Launch.Plan.Model, 8_192, 1, "small")) : null;
        });
        var enrollment = new HostManagerEnrollment(domain, controller, staging, issuer,
            new Dictionary<string, HostStagedRuntimeFixture>(StringComparer.Ordinal) { [first.Name] = fixture, [alias.Name] = fixture },
            stage =>
            {
                var engine = config.Engines[stage.Route.Engine];
                var credential = VerifiedDispatchCredential.Capture(engine, resolvedKey: null, epoch: 1);
                var target = new VerifiedDispatchTarget(engine.Name, stage.Launch.Plan.Model, "small", stage.Route.EffectiveTarget,
                    engine.Endpoint);
                return new(target, stage.Route, 8_192, 8_192, 1, engine.Endpoint, credential, issuer);
            });

        Assert.Throws<RouterConfigException>(() => new InProcessHostManager([enrollment with { Domain = domain with { Generation = 2 } }]));
        Assert.Throws<RouterConfigException>(() => new InProcessHostManager([enrollment, enrollment with { Domain = domain with { Name = "duplicate" } }]));
        var partial = new InProcessHostManager([enrollment with
        {
            Members = new Dictionary<string, HostStagedRuntimeFixture>(StringComparer.Ordinal) { [first.Name] = fixture },
        }]);
        Assert.Throws<RouterConfigException>(() => new RouterCore(config, null, null, TimeProvider.System, hostManager: partial));
        Assert.Equal(0, host.Creates);
        Assert.Equal(0, host.Stops);
        Assert.Equal(0, managed.CompletionCalls);

        var manager = new InProcessHostManager([enrollment]);
        await using var router = new RouterCore(config, null, null, TimeProvider.System, hostManager: manager);
        Assert.True(await router.Engines[external.Name].ProbeHealthOnceAsync(ct));
        var invocation = new TaskCompletionSource<VerifiedInstanceInvocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        issuer.BeforeFirstWrite = value => invocation.TrySetResult(value);
        router.Start();
        var ticket = await router.EnqueueAsync("first", ManagedRequest("first-model", "original cold ticket", named: false), ct);
        try
        {
            await readiness.Reached.WaitAsync(ct);
            Assert.True(manager.Snapshot(first.Name)?.Transitioning);
            Assert.Equal(manager.Snapshot(first.Name), manager.Snapshot(alias.Name));
            remote.LoadedModels = ["outside", "first-model"];
            Assert.True(await router.Engines[external.Name].ProbeHealthOnceAsync(ct));
            var contender = await router.EnqueueAsync("alias", ManagedRequest("alias-model", "shared domain contender"), ct);
            var independent = await router.EnqueueAsync("external", ManagedRequest("outside", "external progress", named: false), ct);
            await independent.CompletionTask.WaitAsync(ct);
            Assert.Equal("external progress", Assert.Single(remote.ExecutionOrder));
            Assert.Equal(TicketState.Queued, contender.State);
            Assert.False(ticket.CompletionTask.IsCompleted);
            Assert.Equal(1, host.Creates);
            Assert.Equal(0, issuer.FirstWriteAttempts);
            Assert.Equal(0, managed.CompletionCalls);
            Assert.True(router.TryCancel(contender.Id));
        }
        finally { readiness.Release(); }

        if (!ready)
        {
            var failure = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
            Assert.Equal("switch_failed", failure.Code);
            Assert.Equal(0, issuer.FirstWriteAttempts);
            Assert.Equal(0, managed.CompletionCalls);
            Assert.Equal("external progress", Assert.Single(remote.ExecutionOrder));
            Assert.Equal(1, host.Creates);
            Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.failed");
            return;
        }

        await ticket.CompletionTask.WaitAsync(ct);
        var offered = await invocation.Task.WaitAsync(ct);
        var bytes = Encoding.UTF8.GetString(issuer.Bytes(offered));
        Assert.Equal(domain.Name, offered.Route.HostGroup);
        Assert.Equal(domain.Generation, offered.Route.EnrollmentGeneration);
        Assert.Equal(first.Name, ticket.Engine);
        Assert.Contains("original cold ticket", bytes, StringComparison.Ordinal);
        Assert.Equal(1, issuer.FirstWriteAttempts);
        Assert.Equal(0, managed.CompletionCalls);
        Assert.Equal("external progress", Assert.Single(remote.ExecutionOrder));
        Assert.Equal(1, host.Creates);
        Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.selected");
    }

    private static EngineDefinition ManagedEngine(string engine, string model, string group, int port) => new()
    {
        Name = engine,
        Endpoint = new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port, "/v1").Uri,
        LifecycleGroup = group,
        LifecycleOwner = EngineLifecycleOwner.Router,
        Lifecycle = EngineLifecycleKind.Docker,
        Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "fixture" },
        Catalog = new Dictionary<string, EngineModelDefinition>(StringComparer.Ordinal)
        {
            [model] = new() { Name = model, Profile = "small", ServingProfiles = [new() { Name = "small", MaxContext = 8_192, MaxSlots = 1 }] },
        },
    };

    private static InferenceRequest ManagedRequest(string model, string prompt, bool named = true) => new()
    {
        Model = model,
        Body = Encoding.UTF8.GetBytes($$"""{"model":"{{model}}","messages":[{"role":"user","content":"{{prompt}}"}]}"""),
        Options = new RouterRequestOptions { Profile = named ? "small" : null },
    };
}
