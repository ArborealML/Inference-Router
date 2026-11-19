using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineHostGuardRejectsMissingProofOrCrossThreadCommit(bool crossThread)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var (host, retained, staging, issuer) = await PrepareGuardCaseAsync(ct);
        var deliveries = 0;
        var openAttempts = 0;
        HostClosedRouteStage? installed = null;
        var expected = GuardPublication();
        var publication = expected with
        {
            Settle = (_, _, _, commit) =>
            {
                var proof = GuardProof(expected.ExpectedAttempt, expected.ExpectedTarget);
                if (!crossThread)
                {
                    commit(proof);
                    return new(HostStagedVerdict.Committed);
                }
                Task.Run(() => commit(proof), ct).WaitAsync(ct).GetAwaiter().GetResult();
                return new(HostStagedVerdict.Committed, proof);
            },
            Deliver = _ => deliveries++,
            TryOpen = (_, _, _) => { openAttempts++; return false; },
            AfterStageInstalledForTests = stage => installed = stage,
        };

        var result = await new HostLifecycleCoordinator(ReservationPath, host, staging).SwitchStagedAsync(Target("b"), publication, ct);
        Assert.Equal("host_publication_refused", result.Result.Reason);
        Assert.Equal(HostStagedVerdict.Refused, result.Decision.Verdict);
        Assert.Null(result.DeliveryFailure);
        Assert.False(result.Opened);
        Assert.Equal(0, deliveries);
        Assert.Equal(0, openAttempts);
        var stage = Assert.IsType<HostClosedRouteStage>(installed);
        await AssertDeniedAsync(issuer, Invocation(stage.Route), SuccessorBody, ct);
        Assert.Equal(0, issuer.Permits);
        AssertGuardLaunchRetained(host, retained);
    }

    public enum ProofSubstitution { Attempt, Target, ReturnedWitness }

    [Theory(Timeout = 30_000)]
    [InlineData(ProofSubstitution.Attempt)]
    [InlineData(ProofSubstitution.Target)]
    [InlineData(ProofSubstitution.ReturnedWitness)]
    public async Task OfflineHostProofBindingRejectsSubstitutionBeforeDelivery(ProofSubstitution substitution)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var (host, retained, staging, issuer) = await PrepareGuardCaseAsync(ct);
        var expected = GuardPublication();
        var deliveries = 0;
        var openAttempts = 0;
        var wakes = 0;
        HostClosedRouteStage? installed = null;
        var publication = expected with
        {
            Settle = (_, _, _, commit) =>
            {
                var attempt = substitution == ProofSubstitution.Attempt
                    ? expected.ExpectedAttempt with { Sequence = expected.ExpectedAttempt.Sequence + 1 }
                    : expected.ExpectedAttempt;
                var target = substitution == ProofSubstitution.Target
                    ? expected.ExpectedTarget with { ConfigGeneration = expected.ExpectedTarget.ConfigGeneration + 1 }
                    : expected.ExpectedTarget;
                var stored = GuardProof(attempt, target);
                commit(stored);
                var returned = substitution == ProofSubstitution.ReturnedWitness ? GuardProof(attempt, target) : stored;
                return new(HostStagedVerdict.Committed, returned);
            },
            Deliver = _ => deliveries++,
            TryOpen = (_, _, open) => { openAttempts++; return open(); },
            Opened = () => wakes++,
            AfterStageInstalledForTests = stage => installed = stage,
        };

        var result = await new HostLifecycleCoordinator(ReservationPath, host, staging).SwitchStagedAsync(Target("b"), publication, ct);
        Assert.Equal("host_publication_refused", result.Result.Reason);
        Assert.Equal(HostStagedVerdict.Refused, result.Decision.Verdict);
        Assert.Null(result.Decision.Proof);
        Assert.Null(result.DeliveryFailure);
        Assert.False(result.Opened);
        Assert.Equal(0, deliveries);
        Assert.Equal(0, openAttempts);
        Assert.Equal(0, wakes);
        var stage = Assert.IsType<HostClosedRouteStage>(installed);
        Assert.Equal(expected.ClaimId, stage.ClaimId);
        Assert.NotEqual(expected.ExpectedAttempt.RuntimeEpoch, stage.ClaimId);
        await AssertDeniedAsync(issuer, Invocation(stage.Route), SuccessorBody, ct);
        Assert.Equal(0, issuer.Grants);
        Assert.Equal(0, issuer.Permits);
        AssertGuardLaunchRetained(host, retained);
    }

    [Fact(Timeout = 30_000)]
    public async Task OfflineHostProofBindingPreservesActualOpenOnNotificationFailure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var (host, retained, staging, issuer) = await PrepareGuardCaseAsync(ct);
        var failure = new InvalidOperationException("Injected notification fault after Open.");
        HostClosedRouteStage? installed = null;
        SwitchServingProof? delivered = null;
        var notifications = 0;
        var publication = GuardPublication() with
        {
            Deliver = decision => delivered = decision.Proof,
            AfterStageInstalledForTests = stage => installed = stage,
            Opened = () =>
            {
                notifications++;
                Assert.False(host.Draining);
                Assert.True(staging.CanMint(Assert.IsType<HostClosedRouteStage>(installed).Route));
                throw failure;
            },
        };

        var result = await new HostLifecycleCoordinator(ReservationPath, host, staging).SwitchStagedAsync(Target("b"), publication, ct);
        Assert.Equal("host_route_delivery_failed", result.Result.Reason);
        Assert.Equal(HostStagedVerdict.Committed, result.Decision.Verdict);
        Assert.Same(Assert.IsType<SwitchServingProof>(delivered), result.Decision.Proof);
        Assert.True(result.Opened);
        Assert.Same(failure, result.DeliveryFailure);
        Assert.Equal(1, notifications);
        var invocation = Invocation(Assert.IsType<HostClosedRouteStage>(installed).Route);
        await SendAsync(issuer, invocation, SuccessorBody, ct);
        AssertBody(SuccessorBody, issuer.Bytes(invocation));
        Assert.Equal(1, issuer.Permits);
        AssertGuardLaunchRetained(host, retained);
    }

    [Fact(Timeout = 30_000)]
    public async Task OfflineHostGuardRequiresSynchronousOpenAndMatchingReturn()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var (host, retained, staging, issuer) = await PrepareGuardCaseAsync(ct);
        var operation = Assert.IsType<HostMutation>(retained.Pending);
        var containerId = Assert.IsType<string>(retained.LaunchedContainerId);
        Func<bool>? escapedOpen = null;
        var deferred = GuardPublication() with { TryOpen = (_, _, open) => { escapedOpen = open; return true; } };

        // Keep the attempt alive after the guard returns so disposal cannot explain the denial.
        using (var attempt = staging.Begin(deferred))
        {
            attempt.Close();
            using (var reservation = HostReservation.Open(ReservationPath))
                Assert.True((await attempt.VerifyAndPublishAsync(ReservationPath, reservation.State, operation, containerId,
                    _ => Task.FromResult(true), ct)).Succeeded);
            Assert.False(attempt.Open(ct));
            Assert.False(Assert.IsType<Func<bool>>(escapedOpen)());
            Assert.False(attempt.Opened);
        }

        HostClosedRouteStage? installed = null;
        var wakes = 0;
        var reachedOpen = false;
        var contradictory = GuardPublication() with
        {
            TryOpen = (stage, _, open) =>
            {
                reachedOpen = true;
                Assert.True(open());
                Assert.False(open());
                Assert.False(staging.CanMint(stage.Route));
                return false;
            },
            AfterStageInstalledForTests = stage => installed = stage,
            Opened = () => wakes++,
        };

        var result = await new HostLifecycleCoordinator(ReservationPath, host, staging).SwitchStagedAsync(Target("b"), contradictory, ct);
        Assert.True(reachedOpen);
        Assert.Equal("host_route_not_opened", result.Result.Reason);
        Assert.Equal(HostStagedVerdict.Committed, result.Decision.Verdict);
        Assert.NotNull(result.Decision.Proof);
        Assert.Null(result.DeliveryFailure);
        Assert.False(result.Opened);
        Assert.Equal(0, wakes);
        await AssertDeniedAsync(issuer, Invocation(Assert.IsType<HostClosedRouteStage>(installed).Route), SuccessorBody, ct);
        Assert.Equal(0, issuer.Permits);
        AssertGuardLaunchRetained(host, retained);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineHostGuardKeepsValidDecisionOnDeliveryOrCleanupFailure(bool failCleanup)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var (host, retained, staging, issuer) = await PrepareGuardCaseAsync(ct);
        var failure = new InvalidOperationException("Injected post-decision fault.");
        if (failCleanup) host.OnDrainDispose = () => throw failure;
        HostStagedDecision delivered = default;
        HostClosedRouteStage? installed = null;
        var publication = GuardPublication() with
        {
            Deliver = decision =>
            {
                delivered = decision;
                if (!failCleanup) throw failure;
            },
            AfterStageInstalledForTests = stage => installed = stage,
        };

        var result = await new HostLifecycleCoordinator(ReservationPath, host, staging).SwitchStagedAsync(Target("b"), publication, ct);
        Assert.Equal("host_route_delivery_failed", result.Result.Reason);
        Assert.Equal(HostStagedVerdict.Committed, result.Decision.Verdict);
        Assert.NotNull(delivered.Proof);
        Assert.Equal(delivered, result.Decision);
        Assert.Same(failure, result.DeliveryFailure);
        Assert.False(result.Opened);
        await AssertDeniedAsync(issuer, Invocation(Assert.IsType<HostClosedRouteStage>(installed).Route), SuccessorBody, ct);
        Assert.Equal(0, issuer.Permits);
        AssertGuardLaunchRetained(host, retained);
    }

    [Fact(Timeout = 30_000)]
    public async Task OfflineHostGuardClosesStaleRouteBeforeMismatchedRecoveryRefusal()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var (host, retained, staging, issuer) = await PrepareGuardCaseAsync(ct);
        var old = Invocation(EligibleOldRoute);
        var prepared = Assert.IsType<VerifiedInstanceGrant>(issuer.Mint(Invocation(EligibleOldRoute)));
        try
        {
            var observations = host.Observations;
            var result = await new HostLifecycleCoordinator(ReservationPath, host, staging)
                .SwitchStagedAsync(Target("c"), GuardPublication(), ct);

            Assert.Equal("host_operation_unsettled", result.Result.Reason);
            Assert.Equal(HostStagedVerdict.Refused, result.Decision.Verdict);
            Assert.False(result.Opened);
            Assert.Equal(observations, host.Observations);
            await AssertDeniedAsync(issuer, old, OldBody, ct);
            await using var stalePermit = issuer.TryAuthorizeFirstWrite(prepared);
            Assert.Null(stalePermit);
            Assert.Empty(issuer.Bytes(prepared.Invocation));
            Assert.Equal(0, issuer.Permits);
            AssertGuardLaunchRetained(host, retained);
        }
        finally { await prepared.CloseAsync(); }
    }

    private async Task<(FakeHost Host, HostReservationState Retained, HostClosedRouteStaging Staging, FixtureIssuer Issuer)>
        PrepareGuardCaseAsync(CancellationToken ct)
    {
        var host = await SeedAppliedDeadlineLaunchAsync(Target("b"), ct);
        var staging = new HostClosedRouteStaging(host.FindLaunch, Route, EligibleOldRoute);
        return (host, ReadReservation(), staging, new FixtureIssuer(staging));
    }

    private void AssertGuardLaunchRetained(FakeHost host, HostReservationState retained)
    {
        Assert.Equal(retained, ReadReservation());
        Assert.Equal(1, host.Stops);
        Assert.Equal(1, host.Creates);
        Assert.False(host.Draining);
    }

    private static HostStagedDeadlinePublication GuardPublication()
    {
        var runtimeEpoch = Guid.NewGuid();
        var attempt = new SwitchAttemptId(runtimeEpoch, 1);
        var target = new SwitchSampleKey(runtimeEpoch, "docker", "b.model", "fixture-tier", 0,
            SwitchObservationClass.Unknown, null);
        return new(Guid.NewGuid(), attempt, target, new ReadinessDeadlineTests.ManualClock(),
            (stage, _) => Task.FromResult<HostStagedReadiness?>(new(stage, stage.Launch.Plan.Model,
                new ServingCapacity(stage.Launch.Plan.Model, 32_768, 2, stage.Launch.Plan.Tier))),
            (_, _, _, commit) =>
            {
                var proof = GuardProof(attempt, target);
                commit(proof);
                return new(HostStagedVerdict.Committed, proof);
            },
            _ => { }, (_, _, open) => open(), () => { });
    }

    private static SwitchServingProof GuardProof(SwitchAttemptId attempt, SwitchSampleKey target) =>
        new(attempt, 1, target, nativeLaunch: null);
}
