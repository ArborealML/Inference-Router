using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    [Fact(Timeout = 30_000)]
    public async Task RecoveredEnrolledLaunchChecksOriginalDeadlineAtGuardAndReopensOnlyFreshProof()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = watchdog.Token;
        var callerWait = TimeSpan.FromSeconds(10);
        var tier = new ServingProfile { Name = "fixture-tier", MaxContext = 32_768 };
        var model = new EngineModelDefinition { Name = "b.model", Profile = tier.Name, ServingProfiles = [tier] };
        var definition = new EngineDefinition
        {
            Name = "b",
            Endpoint = new Uri("http://127.0.0.1/v1"),
            LifecycleOwner = EngineLifecycleOwner.Router,
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "test", WaitTimeoutMs = 1000 },
            Catalog = new Dictionary<string, EngineModelDefinition> { [model.Name] = model },
        };
        var target = new LifecycleTarget(definition.Name, model.Name, tier, model);
        var host = await SeedAppliedDeadlineLaunchAsync(target, ct);
        var retained = ReadReservation();
        var launch = Assert.IsType<HostCheckpointLaunchEffect>(host.FindLaunch(Assert.IsType<HostMutation>(retained.Pending)));
        var staging = new HostClosedRouteStaging(host.FindLaunch, Route, EligibleOldRoute);
        var issuer = new FixtureIssuer(staging);
        var old = Invocation(EligibleOldRoute);
        await SendAsync(issuer, old, OldBody, ct);
        AssertBody(OldBody, issuer.Bytes(old));
        var oldPrepared = Assert.IsType<VerifiedInstanceGrant>(issuer.Mint(Invocation(EligibleOldRoute)));

        try
        {
            var first = await RecoverAsync(commitAt: 99);
            var expired = await RecoverAsync(commitAt: 100);
            await AssertDeniedAsync(issuer, Invocation(first.Route), SuccessorBody, ct);
            await AssertDeniedAsync(issuer, Invocation(expired.Route), SuccessorBody, ct);
            await AssertDeniedAsync(issuer, Invocation(EligibleOldRoute), OldBody, ct);
            await using var stalePermit = issuer.TryAuthorizeFirstWrite(oldPrepared);
            Assert.Null(stalePermit);
            Assert.Empty(issuer.Bytes(oldPrepared.Invocation));

            var fresh = await RecoverAsync(commitAt: 99);
            Assert.NotEqual(first.ClaimId, expired.ClaimId);
            Assert.NotEqual(expired.ClaimId, fresh.ClaimId);
            Assert.True(first.Route.RouteIncarnation < expired.Route.RouteIncarnation);
            Assert.True(expired.Route.RouteIncarnation < fresh.Route.RouteIncarnation);
            await AssertDeniedAsync(issuer, Invocation(first.Route), SuccessorBody, ct);
            await AssertDeniedAsync(issuer, Invocation(expired.Route), SuccessorBody, ct);
            await AssertDeniedAsync(issuer, Invocation(EligibleOldRoute), OldBody, ct);
            Assert.Equal(3, issuer.Permits);
            Assert.Equal(4, issuer.Grants);
            Assert.Equal(1, host.Stops);
            Assert.Equal(1, host.Creates);
            Assert.Equal(retained, ReadReservation());
        }
        finally { await oldPrepared.CloseAsync(); }

        async Task<HostClosedRouteStage> RecoverAsync(int commitAt)
        {
            var clock = new ReadinessDeadlineTests.ManualClock();
            var feed = new EventRing();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wakes = new List<bool>();
            var feedCompletions = new List<bool>();
            EngineCapability? afterEnd = null;
            var ready = commitAt < 100;
            var reads = 0;
            var postDrainIdentities = 0;
            var completions = 0;
            var genericInvalidations = 0;
            var originalPermits = issuer.Permits;
            HostClosedRouteStage? prepared = null;
            SwitchServingProof? proof = null;
            VerifiedInstanceInvocation invocation = default;

            host.OnObserve = (query, _) =>
            {
                if (host.Draining)
                {
                    postDrainIdentities++;
                    Assert.True(clock.ActiveTimers > 0);
                    clock.AdvanceTo(25, fireTimers: false);
                }
                return ValueTask.FromResult(new HostOccupancy(query, HostOccupancyKind.Owned,
                    new HostResident(launch.Operation.Engine, launch.InstanceId)));
            };
            feed.Subscribe(item =>
            {
                if (item.Kind == "engine.switch_completed" && prepared is { } stage)
                    feedCompletions.Add(staging.CanMint(stage.Route));
            });
            var fixture = new HostStagedRuntimeFixture(
                (stage, _) =>
                {
                    reads++;
                    Assert.Equal(1, postDrainIdentities);
                    Assert.Equal(TimeSpan.FromMilliseconds(25), clock.GetElapsedTime(0));
                    Assert.Equal(launch, stage.Launch);
                    Assert.Equal(100, stage.Launch.Plan.ReadinessTimeoutMs);
                    Assert.True(stage.Route.RouteIncarnation > EligibleOldRoute.RouteIncarnation);
                    Assert.Equal(launch.InstanceId, stage.Route.ContainerId);
                    prepared = stage;
                    invocation = Invocation(stage.Route);
                    Assert.Null(issuer.Mint(invocation));
                    clock.AdvanceTo(99, fireTimers: false);
                    return Task.FromResult<HostStagedReadiness?>(new(stage, model.Name,
                        new ServingCapacity(model.Name, tier.MaxContext, 2, tier.Name)));
                },
                AfterStageInstalled: () =>
                {
                    Assert.Equal(prepared, HostClosedRouteStageFile.Read(ReservationPath));
                    var timers = clock.ActiveTimers;
                    Assert.True(timers > 0);
                    clock.AdvanceTo(commitAt, fireTimers: false);
                    Assert.Equal(timers, clock.ActiveTimers);
                    Assert.Null(issuer.Mint(invocation));
                },
                AfterProof: committedProof => proof = committedProof,
                AfterComplete: () =>
                {
                    completions++;
                    Assert.True(host.Draining);
                    Assert.Null(issuer.Mint(invocation));
                },
                AfterRun: () => completed.TrySetResult());
            using var unused = new UnusedSharedReadiness();
            var enrollment = new HostEngineEnrollment("test", definition.Name,
                new HostLifecycleCoordinator(ReservationPath, host, staging), fixture);
            await using var runtime = new EngineRuntime(definition, new RouterConfig(), feed, unused, lifecycle: null,
                dispatchHint: () =>
                {
                    if (prepared is { } stage) wakes.Add(staging.CanMint(stage.Route));
                }, hostEnrollment: enrollment)
            {
                ReadinessClockForTests = clock,
                AfterFailedMutationUnknownForTests = _ => genericInvalidations++,
            };
            runtime.AfterEndCommitForTests = _ => afterEnd = runtime.Capability;

            var result = await runtime.WaitForProfileAsync(model.Name, tier, callerWait, ct);
            await completed.Task.WaitAsync(ct);
            Assert.Equal(ready, result.IsReady);
            Assert.Equal(1, reads);
            Assert.Equal(1, postDrainIdentities);
            Assert.Equal(0, unused.Requests);
            Assert.Equal(0, genericInvalidations);
            Assert.Same(Assert.IsType<EngineCapability>(afterEnd), runtime.Capability);
            Assert.False(host.Draining);
            Assert.Equal(retained, ReadReservation());
            Assert.Equal(originalPermits, issuer.Permits);
            var terminal = Assert.Single(feed.Replay(0), item => item.Kind is "engine.switch_completed" or "engine.switch_failed");
            var installed = Assert.IsType<HostClosedRouteStage>(HostClosedRouteStageFile.Read(ReservationPath));
            Assert.Equal(prepared, installed);

            if (ready)
            {
                Assert.Equal("engine.switch_completed", terminal.Kind);
                Assert.Same(Assert.IsType<SwitchServingProof>(proof), runtime.Capability.SwitchProof);
                Assert.Equal(1, completions);
                Assert.False(Assert.Single(feedCompletions));
                Assert.Collection(wakes, open => Assert.False(open), open => Assert.True(open));
                await SendAsync(issuer, invocation, SuccessorBody, ct);
                AssertBody(SuccessorBody, issuer.Bytes(invocation));
                Assert.Equal(originalPermits + 1, issuer.Permits);
            }
            else
            {
                Assert.Equal(SwitchReason.HealthTimeout, result.Detail);
                Assert.Equal("engine.switch_failed", terminal.Kind);
                Assert.StartsWith("health_timeout", terminal.Reason, StringComparison.Ordinal);
                Assert.Null(proof);
                Assert.Null(runtime.Capability.SwitchProof);
                Assert.Equal(0, completions);
                Assert.Empty(feedCompletions);
                Assert.DoesNotContain(true, wakes);
                await AssertDeniedAsync(issuer, invocation, SuccessorBody, ct);
                Assert.Equal(originalPermits, issuer.Permits);
            }
            return installed;
        }
    }

    private sealed class UnusedSharedReadiness : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Enrolled readiness must use the injected instance-bound reader.");
        }
    }
}
