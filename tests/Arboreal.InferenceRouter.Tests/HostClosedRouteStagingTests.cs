using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("air-closed-route-");
    private string ReservationPath => Path.Combine(_directory.FullName, "host.reservation");

    [Fact(Timeout = 30_000)]
    public async Task DeclineAndDisposalStayClosedUntilTheExactLaunchRecoversAndExplicitlyOpensAFreshGeneration()
    {
        HostReservation.ProvisionNew(ReservationPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var host = new FakeHost();
        var oldRoute = new VerifiedInstanceRoute("fixture-host", 1, "a", "fixture-a-plan", "instance-a", "serving-a", 1, 1);
        var staging = new HostClosedRouteStaging(host.FindLaunch, Route, oldRoute);
        var issuer = new FixtureIssuer(staging);
        var oldInvocation = Invocation(oldRoute);
        var oldBody = "{\"model\":\"a.model\"}"u8.ToArray();
        await SendAsync(issuer, oldInvocation, oldBody, ct);
        AssertBody(oldBody, issuer.Bytes(oldInvocation));
        Assert.Equal(1, issuer.Permits);

        var stale = Invocation(oldRoute);
        var prepared = new TaskCompletionSource<VerifiedInstanceGrant>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheckout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        issuer.HoldCheckout = grant =>
        {
            if (grant.Invocation != stale) return Task.CompletedTask;
            prepared.SetResult(grant);
            return releaseCheckout.Task;
        };
        var sendingStale = new VerifiedInstanceTransport(stale, issuer).SendAsync(stale, oldBody, null, ct);
        Assert.Equal(stale, (await prepared.Task.WaitAsync(ct)).Invocation);
        Assert.Equal(2, issuer.Grants);
        Assert.False(sendingStale.IsCompleted);

        var coordinator = new HostLifecycleCoordinator(ReservationPath, host, staging);
        var b = Target("b");
        var publishes = 0;
        var completes = 0;
        var opens = 0;
        HostClosedRouteStage? declined = null;
        staging.BeforeOpenForTests = (_, _) => opens++;
        var refusal = await coordinator.SwitchAsync(b, ct, stagedPublicationForTests: new HostStagedPublication(Guid.NewGuid(),
            stage =>
            {
                publishes++;
                declined = stage;
                return false;
            }, _ => completes++));

        Assert.Equal("host_publication_refused", refusal.Reason);
        Assert.Equal(1, publishes);
        Assert.Equal(0, completes);
        Assert.Equal(0, opens);
        Assert.Equal(1, host.Verified);
        Assert.Equal(1, host.Disposals);
        Assert.False(host.Draining);
        var installed = Assert.IsType<HostClosedRouteStage>(HostClosedRouteStageFile.Read(ReservationPath));
        Assert.Equal(declined, installed);
        var applied = installed.Launch;
        Assert.Equal(host.FindLaunch(applied.Operation), applied);
        Assert.Equal(host.GetLaunchPlanDigest(b), applied.Plan.Digest());
        Assert.Equal(b.Model, applied.Operation.Target);
        Assert.Equal(b.Definition.Profile, applied.Operation.Profile);
        var retained = ReadReservation();
        Assert.Equal(new HostReservationState(applied.Operation.Epoch, b.EngineName, applied.Operation,
            HostMutationPhase.LaunchApplied, applied.InstanceId), retained);

        var refusedOld = Invocation(oldRoute);
        var refusedStage = Invocation(installed.Route);
        var bBody = "{\"model\":\"b.model\"}"u8.ToArray();
        await AssertDeniedAsync(issuer, refusedOld, oldBody, ct);
        await AssertDeniedAsync(issuer, refusedStage, bBody, ct);
        releaseCheckout.SetResult();
        await Assert.ThrowsAsync<HttpRequestException>(() => sendingStale.WaitAsync(ct));
        Assert.Empty(issuer.Bytes(stale));
        Assert.Equal(1, issuer.Permits);
        Assert.Equal(2, issuer.Grants);
        AssertBody(oldBody, issuer.Bytes(oldInvocation));

        var observations = host.Observations;
        var unexpected = new HostStagedPublication(Guid.NewGuid(), _ => throw new InvalidOperationException("Unexpected publication."),
            _ => throw new InvalidOperationException("Unexpected completion."));
        Assert.Equal("host_operation_unsettled", (await coordinator.SwitchAsync(Target("c"), ct,
            stagedPublicationForTests: unexpected)).Reason);
        Assert.Equal("host_operation_unsettled", (await coordinator.SwitchAsync(Target("b", "drifted-config"), ct,
            stagedPublicationForTests: unexpected)).Reason);
        Assert.Equal("host_publication_refused", (await coordinator.SwitchAsync(b, ct)).Reason);
        Assert.Equal(observations, host.Observations);
        Assert.Equal(retained, ReadReservation());
        Assert.Equal(installed, HostClosedRouteStageFile.Read(ReservationPath));
        Assert.Equal(1, host.Creates);
        Assert.Equal(1, host.Stops);

        VerifiedInstanceInvocation fresh = default;
        HostClosedRouteStage? committed = null;
        staging.BeforeOpenForTests = (stage, tryStaleOpen) =>
        {
            opens++;
            Assert.False(host.Draining);
            Assert.Equal(committed, stage);
            Assert.False(tryStaleOpen(installed));
            Assert.Null(issuer.Mint(fresh));
        };
        var reopened = new HostLifecycleCoordinator(ReservationPath, host, staging);
        var success = await reopened.SwitchAsync(b, ct, stagedPublicationForTests: new HostStagedPublication(Guid.NewGuid(),
            stage =>
            {
                publishes++;
                committed = stage;
                return true;
            }, stage =>
            {
                completes++;
                Assert.True(host.Draining);
                fresh = Invocation(stage.Route);
                Assert.Null(issuer.Mint(fresh));
            }));

        Assert.True(success.Succeeded);
        Assert.Equal(2, publishes);
        Assert.Equal(1, completes);
        Assert.Equal(1, opens);
        Assert.Equal(2, host.Disposals);
        Assert.Equal(2, host.Verified);
        Assert.Equal(observations + 2, host.Observations);
        Assert.Equal(1, host.Creates);
        Assert.Equal(1, host.Stops);
        var freshStage = Assert.IsType<HostClosedRouteStage>(HostClosedRouteStageFile.Read(ReservationPath));
        Assert.Equal(committed, freshStage);
        Assert.Equal(applied, freshStage.Launch);
        Assert.NotEqual(installed.ClaimId, freshStage.ClaimId);
        Assert.True(freshStage.Route.RouteIncarnation > installed.Route.RouteIncarnation);
        Assert.Equal(retained, ReadReservation());

        await SendAsync(issuer, fresh, bBody, ct);
        AssertBody(bBody, issuer.Bytes(fresh));
        await AssertDeniedAsync(issuer, refusedOld, oldBody, ct);
        await AssertDeniedAsync(issuer, refusedStage, bBody, ct);
        Assert.Empty(issuer.Bytes(stale));
        Assert.Equal(2, issuer.Permits);
        Assert.Equal(3, issuer.Grants);
        Assert.Equal(retained, ReadReservation());
    }

    private HostReservationState ReadReservation()
    {
        using var reservation = HostReservation.Open(ReservationPath);
        return reservation.State;
    }

    private static VerifiedInstanceRoute Route(HostCheckpointLaunchEffect launch, long generation) =>
        new("fixture-host", 1, launch.Operation.Engine, launch.Plan.Digest(), launch.InstanceId, "fixture-serving-b",
            launch.Operation.Epoch, generation);

    private static LifecycleTarget Target(string engine, string configuration = "fixture-config") =>
        new(engine, $"{engine}.model", null, new EngineModelDefinition { Name = configuration, Profile = "fixture-tier" });

    private static VerifiedInstanceInvocation Invocation(VerifiedInstanceRoute route) =>
        new(route, Guid.NewGuid(), new Uri("http://fixture.invalid/v1/chat/completions"));

    private static async Task SendAsync(FixtureIssuer issuer, VerifiedInstanceInvocation invocation, byte[] body, CancellationToken ct)
    {
        await using var exchange = await new VerifiedInstanceTransport(invocation, issuer).SendAsync(invocation, body, null, ct);
        Assert.Equal("OK", await exchange.Response.Content.ReadAsStringAsync(ct));
    }

    private static async Task AssertDeniedAsync(FixtureIssuer issuer, VerifiedInstanceInvocation invocation, byte[] body, CancellationToken ct)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new VerifiedInstanceTransport(invocation, issuer).SendAsync(invocation, body, null, ct));
        Assert.Empty(issuer.Bytes(invocation));
    }

    private static void AssertBody(byte[] expected, byte[] received)
    {
        var boundary = received.AsSpan().IndexOf("\r\n\r\n"u8);
        Assert.True(boundary >= 0);
        Assert.Equal(expected, received.AsSpan(boundary + 4).ToArray());
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
