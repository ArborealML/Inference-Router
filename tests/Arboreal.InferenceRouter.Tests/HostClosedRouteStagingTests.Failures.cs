using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    private static readonly VerifiedInstanceRoute EligibleOldRoute =
        new("fixture-host", 1, "a", "fixture-a-plan", "instance-a", "serving-a", 1, 1);
    private static readonly byte[] OldBody = "{\"model\":\"a.model\"}"u8.ToArray();
    private static readonly byte[] SuccessorBody = "{\"model\":\"b.model\"}"u8.ToArray();

    [Fact(Timeout = 30_000)]
    public async Task AppliedRecoveryClosesTheOldRouteBeforeAProbeThatCannotProveOccupancy()
    {
        HostReservation.ProvisionNew(ReservationPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var target = Target("b");
        var host = new FakeHost { Preflight = LifecycleResult.Fail("fixture_preflight_refused", "Unavailable image.") };
        var staging = new HostClosedRouteStaging(host.FindLaunch, Route, EligibleOldRoute);
        var issuer = new FixtureIssuer(staging);
        var coordinator = new HostLifecycleCoordinator(ReservationPath, host, staging);
        var publication = new HostStagedPublication(Guid.NewGuid(),
            _ => throw new InvalidOperationException("Unexpected publication."),
            _ => throw new InvalidOperationException("Unexpected completion."));

        var untouched = await coordinator.SwitchAsync(target, ct, stagedPublicationForTests: publication);
        Assert.Equal("fixture_preflight_refused", untouched.Reason);
        var eligible = Invocation(EligibleOldRoute);
        await SendAsync(issuer, eligible, OldBody, ct);
        AssertBody(OldBody, issuer.Bytes(eligible));
        Assert.Equal(1, issuer.Permits);

        // Simulate reopening after B was applied elsewhere while the old issuer still knows A.
        using (var reservation = HostReservation.Open(ReservationPath))
        {
            const string instance = "saved-instance-b";
            var epoch = reservation.Grant(target.EngineName);
            var operation = reservation.Begin(epoch, HostMutationKind.Launch, target.EngineName, target.Model,
                target.Definition.Profile, host.GetLaunchPlanDigest(target));
            reservation.MarkDispatching(operation);
            host.SetAppliedLaunch(operation, target, instance);
            reservation.RecordAppliedLaunch(operation, instance);
        }
        var retained = ReadReservation();
        Assert.Equal(HostMutationPhase.LaunchApplied, retained.Phase);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnObserve = async (query, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return new HostOccupancy(query, HostOccupancyKind.Unknown);
        };

        var observations = host.Observations;
        var reopened = new HostLifecycleCoordinator(ReservationPath, host, staging);
        var old = Invocation(EligibleOldRoute);
        var prepared = Assert.IsType<VerifiedInstanceGrant>(issuer.Mint(Invocation(EligibleOldRoute)));
        try
        {
            var recovering = reopened.SwitchAsync(target, ct, stagedPublicationForTests: publication).AsTask();
            LifecycleResult refused;
            try
            {
                await entered.Task.WaitAsync(ct);
                Assert.False(recovering.IsCompleted);
                await AssertDeniedAsync(issuer, old, OldBody, ct);
                await using var duringProbe = issuer.TryAuthorizeFirstWrite(prepared);
                Assert.Null(duringProbe);
            }
            finally
            {
                release.TrySetResult();
                refused = await recovering;
            }

            Assert.Equal("host_occupancy_unproven", refused.Reason);
            await AssertDeniedAsync(issuer, old, OldBody, ct);
            await using var afterProbe = issuer.TryAuthorizeFirstWrite(prepared);
            Assert.Null(afterProbe);
            Assert.Empty(issuer.Bytes(prepared.Invocation));
        }
        finally { await prepared.CloseAsync(); }

        Assert.Equal(1, issuer.Permits);
        Assert.Equal(2, issuer.Grants);
        Assert.Equal(observations + 1, host.Observations);
        Assert.Equal(0, host.Disposals);
        Assert.Equal(0, host.Stops);
        Assert.Equal(0, host.Creates);
        Assert.Equal(retained, ReadReservation());
        Assert.Null(HostClosedRouteStageFile.Read(ReservationPath));
    }

    public enum FailureBoundary
    {
        AfterStageInstall,
        AfterStateAcceptance,
        Completion,
        StageIo,
        DrainDisposal,
    }

    [Theory(Timeout = 30_000)]
    [InlineData(FailureBoundary.AfterStageInstall, 0, 0)]
    [InlineData(FailureBoundary.AfterStateAcceptance, 1, 0)]
    [InlineData(FailureBoundary.Completion, 1, 1)]
    [InlineData(FailureBoundary.StageIo, 0, 0)]
    [InlineData(FailureBoundary.DrainDisposal, 1, 1)]
    public async Task StageFailureRetainsTheExactLaunchAndRequiresFreshOpen(
        FailureBoundary boundary, int expectedPublishes, int expectedCompletions)
    {
        HostReservation.ProvisionNew(ReservationPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var host = new FakeHost();
        var target = Target("b");
        VerifiedInstanceRoute? prepared = null;
        var staging = new HostClosedRouteStaging(host.FindLaunch, (launch, generation) =>
        {
            var route = Route(launch, generation);
            prepared = route;
            return route;
        }, EligibleOldRoute);
        var issuer = new FixtureIssuer(staging);
        var eligible = Invocation(EligibleOldRoute);
        await SendAsync(issuer, eligible, OldBody, ct);
        AssertBody(OldBody, issuer.Bytes(eligible));

        var scratchPath = ReservationPath + ".closed-route.new";
        if (boundary == FailureBoundary.StageIo) Directory.CreateDirectory(scratchPath);
        var injected = new InvalidOperationException("Injected " + boundary);
        if (boundary == FailureBoundary.DrainDisposal) host.OnDrainDispose = () => throw injected;
        var installs = 0;
        var publishes = 0;
        var completes = 0;
        var opens = 0;
        staging.BeforeOpenForTests = (_, _) => opens++;
        var publication = new HostStagedPublication(Guid.NewGuid(),
            _ =>
            {
                publishes++;
                return true;
            }, _ =>
            {
                completes++;
                if (boundary == FailureBoundary.Completion) throw injected;
            }, AfterStageInstalledForTests: () =>
            {
                installs++;
                if (boundary == FailureBoundary.AfterStageInstall) attemptCancellation.Cancel();
            }, AfterStateAcceptedForTests: () =>
            {
                if (boundary == FailureBoundary.AfterStateAcceptance) attemptCancellation.Cancel();
            });
        var coordinator = new HostLifecycleCoordinator(ReservationPath, host, staging);
        var attempt = coordinator.SwitchAsync(target, attemptCancellation.Token, stagedPublicationForTests: publication).AsTask();
        switch (boundary)
        {
            case FailureBoundary.AfterStageInstall:
            case FailureBoundary.AfterStateAcceptance:
                var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
                Assert.Equal(attemptCancellation.Token, canceled.CancellationToken);
                break;
            case FailureBoundary.StageIo:
                Assert.Equal("host_reservation_unavailable", (await attempt).Reason);
                Directory.Delete(scratchPath);
                break;
            default:
                Assert.Same(injected, await Assert.ThrowsAsync<InvalidOperationException>(() => attempt));
                break;
        }

        Assert.Equal(expectedPublishes, publishes);
        Assert.Equal(expectedCompletions, completes);
        Assert.Equal(0, opens);
        Assert.Equal(1, host.Verified);
        Assert.Equal(1, host.Disposals);
        Assert.False(host.Draining);
        Assert.Equal(1, host.Stops);
        Assert.Equal(1, host.Creates);
        var failedRoute = Assert.IsType<VerifiedInstanceRoute>(prepared);
        var retained = ReadReservation();
        var operation = Assert.IsType<HostMutation>(retained.Pending);
        var launch = Assert.IsType<HostCheckpointLaunchEffect>(host.FindLaunch(operation));
        Assert.Equal(new HostReservationState(operation.Epoch, target.EngineName, operation, HostMutationPhase.LaunchApplied,
            launch.InstanceId), retained);
        var installed = HostClosedRouteStageFile.Read(ReservationPath);
        if (boundary == FailureBoundary.StageIo)
        {
            Assert.Equal(0, installs);
            Assert.Null(installed);
        }
        else
        {
            Assert.Equal(1, installs);
            Assert.Equal(new HostClosedRouteStage(1, publication.ClaimId, launch, failedRoute), installed);
        }

        var old = Invocation(EligibleOldRoute);
        var failed = Invocation(failedRoute);
        await AssertDeniedAsync(issuer, old, OldBody, ct);
        await AssertDeniedAsync(issuer, failed, SuccessorBody, ct);
        Assert.Equal(1, issuer.Permits);
        Assert.Equal(1, issuer.Grants);

        host.OnDrainDispose = null;
        VerifiedInstanceInvocation fresh = default;
        var retried = await new HostLifecycleCoordinator(ReservationPath, host, staging).SwitchAsync(target, ct,
            stagedPublicationForTests: new HostStagedPublication(Guid.NewGuid(), stage =>
            {
                Assert.Equal(launch, stage.Launch);
                Assert.True(stage.Route.RouteIncarnation > failedRoute.RouteIncarnation);
                fresh = Invocation(stage.Route);
                return true;
            }, _ =>
            {
                completes++;
                Assert.Null(issuer.Mint(fresh));
            }));
        Assert.True(retried.Succeeded);
        Assert.Equal(expectedCompletions + 1, completes);
        Assert.Equal(1, opens);
        Assert.Equal(2, host.Disposals);
        Assert.Equal(2, host.Verified);
        Assert.Equal(1, host.Stops);
        Assert.Equal(1, host.Creates);
        await SendAsync(issuer, fresh, SuccessorBody, ct);
        AssertBody(SuccessorBody, issuer.Bytes(fresh));
        await AssertDeniedAsync(issuer, old, OldBody, ct);
        await AssertDeniedAsync(issuer, failed, SuccessorBody, ct);
        Assert.Equal(2, issuer.Permits);
        Assert.Equal(2, issuer.Grants);
        Assert.Equal(retained, ReadReservation());
    }
}
