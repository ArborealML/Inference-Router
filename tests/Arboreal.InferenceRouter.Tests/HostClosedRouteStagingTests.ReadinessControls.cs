using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    [Theory(Timeout = 30_000)]
    [InlineData(true, """{"n_ctx":32768}""", null)]
    [InlineData(true, """{"n_ctx":32768,"total_slots":0}""", 0)]
    [InlineData(false, """{"n_ctx":32768}""", null)]
    [InlineData(false, """{"n_ctx":32768,"total_slots":0}""", 0)]
    [InlineData(false, """{"n_ctx":32768,"total_slots":2}""", 2)]
    public async Task EnrolledReadinessRequiresMeasuredSlots(bool explicitTier, string observation, int? reportedSlots)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = watchdog.Token;
        var (definition, target, tier) = ReadinessControlTarget(explicitTier);

        // The offline staged reader accepts bound ServingCapacity. Normalize the two raw inputs here
        // as the gauge reader does so this exercises the staged guard independently of HTTP parsing.
        using var document = JsonDocument.Parse(observation);
        var window = document.RootElement.GetProperty("n_ctx").GetInt64();
        Assert.Equal(tier.MaxContext, window);
        var hasSlots = document.RootElement.TryGetProperty("total_slots", out var slots);
        Assert.Equal(reportedSlots.HasValue, hasSlots);
        if (hasSlots) Assert.Equal(reportedSlots.GetValueOrDefault(), slots.GetInt32());
        var measuredSlots = hasSlots ? slots.GetInt32() : 0;
        var serving = new ServingCapacity(target.Model, window, measuredSlots, tier.Name);
        var ready = measuredSlots > 0;

        var host = await SeedAppliedDeadlineLaunchAsync(target, ct);
        var retained = ReadReservation();
        var staging = new HostClosedRouteStaging(host.FindLaunch, Route, EligibleOldRoute);
        var issuer = new FixtureIssuer(staging);
        var clock = new ReadinessDeadlineTests.ManualClock();
        var feed = new EventRing();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var completions = 0;
        var unknowns = 0;
        var wakes = new List<bool>();
        SwitchServingProof? delivered = null;
        HostClosedRouteStage? bound = null;
        HostStagedSwitchResult? hostResult = null;
        EngineRuntime? runtimeForHook = null;
        staging.BeforeOpenForTests = (_, _) => opens++;

        var fixture = new HostStagedRuntimeFixture(
            (stage, _) =>
            {
                bound = stage;
                clock.AdvanceTo(99, fireTimers: false);
                return Task.FromResult<HostStagedReadiness?>(new(stage, target.Model, serving));
            },
            AfterProof: proof =>
            {
                delivered = proof;
                var route = Assert.IsType<HostClosedRouteStage>(bound).Route;
                var engine = Assert.IsType<EngineRuntime>(runtimeForHook);
                Assert.True(host.Draining);
                Task.Run(() =>
                {
                    Assert.False(staging.CanMint(route));
                    Assert.Equal(0, engine.HttpUsesForTests);
                    Assert.Throws<IOException>(() =>
                    {
                        using var unexpected = HostReservation.Open(ReservationPath);
                    });
                }, ct).WaitAsync(TimeSpan.FromSeconds(5), ct).GetAwaiter().GetResult();
                Assert.True(host.Draining);
            },
            AfterComplete: () => completions++,
            AfterRun: () => ended.TrySetResult(),
            AfterHostResult: result => hostResult = result);
        using var unused = new UnusedSharedReadiness();
        var enrollment = new HostEngineEnrollment("test", definition.Name,
            new HostLifecycleCoordinator(ReservationPath, host, staging), fixture);
        await using var runtime = new EngineRuntime(definition, new RouterConfig(), feed, unused, lifecycle: null,
            dispatchHint: () =>
            {
                if (bound is { } stage) wakes.Add(staging.CanMint(stage.Route));
            }, hostEnrollment: enrollment)
        {
            ReadinessClockForTests = clock,
            AfterFailedMutationUnknownForTests = _ => unknowns++,
        };
        runtimeForHook = runtime;

        var result = explicitTier
            ? await runtime.WaitForProfileAsync(target.Model, tier, TimeSpan.FromSeconds(10), ct)
            : await runtime.WaitForModelAsync(target.Model, TimeSpan.FromSeconds(10), ct);
        await ended.Task.WaitAsync(ct);

        Assert.Equal(ready ? EngineRuntime.SwitchOutcome.Ready : EngineRuntime.SwitchOutcome.Failed, result.Outcome);
        var staged = Assert.IsType<HostStagedSwitchResult>(hostResult);
        Assert.Equal(ready ? HostStagedVerdict.Committed : HostStagedVerdict.Refused, staged.Decision.Verdict);
        Assert.Equal(ready, staged.Opened);
        Assert.Null(staged.DeliveryFailure);
        Assert.Equal(ready ? 1 : 0, opens);
        Assert.Equal(ready ? 1 : 0, completions);
        Assert.Equal(ready ? 0 : 1, unknowns);
        Assert.Equal(0, unused.Requests);
        Assert.Equal(TimeSpan.FromMilliseconds(99), clock.GetElapsedTime(0));

        var savedStage = Assert.IsType<HostClosedRouteStage>(bound);
        Assert.Equal(savedStage, HostClosedRouteStageFile.Read(ReservationPath));
        Assert.Equal(retained.LaunchedContainerId, savedStage.Route.ContainerId);
        Assert.Equal(retained.Pending, savedStage.Launch.Operation);
        AssertGuardLaunchRetained(host, retained);
        var invocation = Invocation(savedStage.Route);
        var terminal = Assert.Single(feed.Replay(0), entry => entry.Kind is "engine.switch_completed" or "engine.switch_failed");
        Assert.Equal(0, issuer.Permits);

        if (ready)
        {
            Assert.Equal("engine.switch_completed", terminal.Kind);
            var proof = Assert.IsType<SwitchServingProof>(delivered);
            Assert.Same(proof, staged.Decision.Proof);
            Assert.Same(proof, runtime.Capability.SwitchProof);
            Assert.Equal(tier.Name, proof.Target.EffectiveProfile);
            Assert.Equal(serving, runtime.Capability.Serving);
            Assert.Collection(wakes, open => Assert.False(open), open => Assert.True(open));
            await SendAsync(issuer, invocation, SuccessorBody, ct);
            AssertBody(SuccessorBody, issuer.Bytes(invocation));
            Assert.Equal(1, issuer.Permits);
        }
        else
        {
            Assert.Equal(SwitchReason.LaunchFailed, result.Detail);
            Assert.Equal("engine.switch_failed", terminal.Kind);
            Assert.StartsWith("host_publication_refused", terminal.Reason, StringComparison.Ordinal);
            Assert.Null(delivered);
            Assert.Null(staged.Decision.Proof);
            Assert.Null(runtime.Capability.SwitchProof);
            Assert.False(runtime.Capability.Healthy);
            Assert.DoesNotContain(true, wakes);
            await AssertDeniedAsync(issuer, invocation, SuccessorBody, ct);
            Assert.Equal(0, issuer.Grants);
            Assert.Equal(0, issuer.Permits);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task EnrolledTypedStopSurvivesTheRuntimeUnwind()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = watchdog.Token;
        var (definition, target, tier) = ReadinessControlTarget(explicitTier: true);
        var host = await SeedAppliedDeadlineLaunchAsync(target, ct);
        var retained = ReadReservation();
        var staging = new HostClosedRouteStaging(host.FindLaunch, Route, EligibleOldRoute);
        var issuer = new FixtureIssuer(staging);
        var clock = new ReadinessDeadlineTests.ManualClock();
        var feed = new EventRing();
        var entered = new TaskCompletionSource<HostClosedRouteStage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HostStagedReadiness?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hints = new List<bool>();
        var opens = 0;
        var installs = 0;
        var proofs = 0;
        var completions = 0;
        var unknowns = 0;
        HostClosedRouteStage? bound = null;
        HostStagedSwitchResult? hostResult = null;
        EngineCapability? atEnd = null;
        SwitchAttemptRecord? stoppedTerminal = null;
        staging.BeforeOpenForTests = (_, _) => opens++;

        var fixture = new HostStagedRuntimeFixture(
            (stage, _) =>
            {
                bound = stage;
                clock.AdvanceTo(99, fireTimers: false);
                entered.TrySetResult(stage);
                return release.Task;
            },
            AfterStageInstalled: () => installs++,
            AfterProof: _ => proofs++,
            AfterComplete: () => completions++,
            AfterRun: () => ended.TrySetResult(),
            AfterHostResult: result => hostResult = result);
        using var unused = new UnusedSharedReadiness();
        var enrollment = new HostEngineEnrollment("test", definition.Name,
            new HostLifecycleCoordinator(ReservationPath, host, staging), fixture);
        await using var runtime = new EngineRuntime(definition, new RouterConfig(), feed, unused, lifecycle: null,
            dispatchHint: () => hints.Add(bound is { } stage && staging.CanMint(stage.Route)), hostEnrollment: enrollment)
        {
            ReadinessClockForTests = clock,
            AfterFailedMutationUnknownForTests = _ => unknowns++,
        };
        runtime.AfterStopDecisionForTests = () => stoppedTerminal = Assert.Single(runtime.SwitchHistory).Terminal;
        runtime.AfterEndCommitForTests = _ => atEnd = runtime.Capability;

        try
        {
            var waiter = runtime.WaitForProfileAsync(target.Model, tier, TimeSpan.FromSeconds(10), ct);
            var stage = await entered.Task.WaitAsync(ct);
            Assert.False(waiter.IsCompleted);
            await runtime.DisposeAsync().AsTask().WaitAsync(ct);

            var answer = await waiter.WaitAsync(ct);
            await ended.Task.WaitAsync(ct);
            Assert.False(release.Task.IsCompleted);
            Assert.False(ct.IsCancellationRequested);
            Assert.Equal(EngineRuntime.SwitchOutcome.RouterStopped, answer.Outcome);
            var staged = Assert.IsType<HostStagedSwitchResult>(hostResult);
            Assert.Equal(HostStagedVerdict.Stopped, staged.Decision.Verdict);
            Assert.Equal("router_stopped", staged.Result.Reason);
            Assert.Null(staged.Decision.Proof);
            Assert.Null(staged.DeliveryFailure);
            Assert.False(staged.Opened);

            var terminal = Assert.IsType<SwitchAttemptRecord>(stoppedTerminal);
            Assert.Equal(SwitchAttemptOutcome.Cancelled, terminal.Outcome);
            Assert.Same(terminal, Assert.Single(runtime.SwitchHistory).Terminal);
            var stopped = Assert.IsType<EngineCapability>(atEnd);
            Assert.Same(stopped, runtime.Capability);
            Assert.Null(stopped.SwitchProof);
            Assert.False(stopped.Healthy);
            var events = feed.Replay(0).ToArray();
            Assert.Single(events, entry => entry.Kind == "engine.switch_started");
            Assert.DoesNotContain(events, entry => entry.Kind is "engine.switch_completed" or "engine.switch_failed" or "engine.profile");

            // The observer still accepts a fitting late result with the original exact binding.
            release.SetResult(new(stage, target.Model, new ServingCapacity(target.Model, tier.MaxContext, 2, tier.Name)));
            await AssertDeniedAsync(issuer, Invocation(stage.Route), SuccessorBody, ct);
            Assert.Same(stopped, runtime.Capability);
            Assert.Equal(events, feed.Replay(0).ToArray());
            Assert.Same(terminal, Assert.Single(runtime.SwitchHistory).Terminal);
            Assert.Equal(0, installs);
            Assert.Equal(0, proofs);
            Assert.Equal(0, completions);
            Assert.Equal(0, unknowns);
            Assert.Equal(0, opens);
            Assert.DoesNotContain(true, hints);
            Assert.Null(HostClosedRouteStageFile.Read(ReservationPath));
            Assert.Equal(0, issuer.Grants);
            Assert.Equal(0, issuer.Permits);
            Assert.Equal(0, unused.Requests);
            AssertGuardLaunchRetained(host, retained);
        }
        finally
        {
            release.TrySetResult(null);
        }
    }

    private static (EngineDefinition Definition, LifecycleTarget Target, ServingProfile Tier)
        ReadinessControlTarget(bool explicitTier)
    {
        var tier = new ServingProfile { Name = "fixture-tier", MaxContext = 32_768, MaxSlots = 7 };
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
        return (definition, new LifecycleTarget(definition.Name, model.Name, explicitTier ? tier : null, model), tier);
    }
}
