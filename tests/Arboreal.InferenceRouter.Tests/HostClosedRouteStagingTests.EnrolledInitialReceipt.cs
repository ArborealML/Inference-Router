using System.Security.Cryptography;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    private static readonly string ReceiptInstance = new('E', 64);
    private static readonly HostEnrollmentScope ReceiptEnrollment = new("initial-receipt-fixture", 2);

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnrolledInitialReceiptAcceptsTransportReplyOrReconcilesTheSameEffect(bool lostReply)
    {
        var pair = ProjectReceiptPair();
        var fx = ProvisionReceipt(pair, lostReply: lostReply);
        var lockObserved = false;
        fx.Worker.BeforeSend = () =>
        {
            Assert.Throws<IOException>(() =>
            {
                using var unexpected = HostReservation.OpenEnrolled(fx.Scope);
            });
            lockObserved = true;
        };

        var first = await fx.Coordinator.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None);
        Assert.True(lockObserved);
        Assert.Equal(2, fx.Worker.WorkerReads);
        Assert.Equal(1, fx.Worker.Sends);
        var history = Assert.IsType<HostInitialHistory>(fx.Worker.ReadHistory(fx.Scope));
        var dispatch = Assert.IsType<HostInitialDispatch>(history.Dispatch);
        Assert.Equal(dispatch, history.Accepted);
        Assert.Equal(dispatch, history.Issued);
        Assert.Equal(1, dispatch.Operation.Epoch);
        Assert.Equal(pair.DeepSeek.Intent, dispatch.Intent);
        Assert.Equal(dispatch.Operation, HostReservation.ReadEnrolled(fx.Scope).Record.State.Pending);

        var reopenedWorker = new HostEffectLedgerProbe.CoordinatedInitialLaunch(fx.Scope, pair.Intents);
        var reopened = ReceiptCoordinator(reopenedWorker, fx.Staging);
        if (lostReply)
        {
            Assert.Null(first);
            Assert.Equal(HostMutationPhase.Dispatching, HostReservation.ReadEnrolled(fx.Scope).Record.State.Phase);
        }
        else
            Assert.Equal(ReceiptInstance, Assert.IsType<HostAppliedIngressClosed>(first).Receipt.InstanceId);

        var result = Assert.IsType<HostAppliedIngressClosed>(await reopened.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        Assert.Equal(new HostInitialReceipt(fx.Scope, dispatch.Operation, pair.DeepSeek.Intent, ReceiptInstance), result.Receipt);
        Assert.Equal(lostReply ? 1 : 0, reopenedWorker.Reconciliations);
        Assert.Equal(0, reopenedWorker.Sends);
        Assert.Equal(HostMutationPhase.LaunchApplied, HostReservation.ReadEnrolled(fx.Scope).Record.State.Phase);
        var applied = Assert.IsType<HostInitialApplied>(history.Applied);
        Assert.Equal(Convert.FromBase64String(applied.ReceiptBytes), reopened.ReadInitialLaunchReceipt(dispatch.Operation, ReceiptInstance));
        Assert.Null(reopened.ReadInitialLaunchReceipt(dispatch.Operation, new string('F', 64)));
        Assert.Null(await reopened.LaunchInitialClosedAsync(pair.Qwen.Intent, CancellationToken.None));
        await AssertReceiptSwitchesBlockedAsync(pair, fx.Staging);
        Assert.Equal(result, await reopened.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        await AssertReceiptIngressClosedAsync(fx.Staging, fx.Scope, result.Receipt);
        AssertOneReceiptEffect(fx.Worker);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnrolledInitialReceiptHistoricalBytesSurviveAChangeInAuthority(bool ownerChanges)
    {
        var pair = ProjectReceiptPair();
        var fx = ProvisionReceipt(pair);
        var result = Assert.IsType<HostAppliedIngressClosed>(await fx.Coordinator.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        var receipt = result.Receipt;
        await AssertReceiptIngressClosedAsync(fx.Staging, fx.Scope, receipt);
        var original = Assert.IsType<byte[]>(fx.Coordinator.ReadInitialLaunchReceipt(receipt.Operation, receipt.InstanceId));
        var installed = File.ReadAllBytes(ReservationPath);
        var history = fx.Worker.ReadHistory(fx.Scope);
        fx.Worker.ChangePolicy(policy => ownerChanges ? policy with { OwnerGeneration = policy.OwnerGeneration + 1 }
            : policy with { PolicyGeneration = policy.PolicyGeneration + 1, IngressClosed = false });

        var worker = new HostEffectLedgerProbe.CoordinatedInitialLaunch(fx.Scope, pair.Intents);
        var reopened = ReceiptCoordinator(worker, fx.Staging);
        Assert.Equal(original, reopened.ReadInitialLaunchReceipt(receipt.Operation, receipt.InstanceId));
        Assert.Null(await reopened.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        Assert.Null(await reopened.LaunchInitialClosedAsync(pair.Qwen.Intent, CancellationToken.None));
        await AssertReceiptSwitchesBlockedAsync(pair, fx.Staging);
        await AssertReceiptIngressClosedAsync(fx.Staging, fx.Scope, receipt);
        Assert.Equal(installed, File.ReadAllBytes(ReservationPath));
        Assert.Equal(history, worker.ReadHistory(fx.Scope));
        Assert.Equal(0, worker.Sends);
        Assert.Equal(0, worker.Reconciliations);
        AssertOneReceiptEffect(fx.Worker);
    }

    [Fact(Timeout = 30_000)]
    public async Task EnrolledInitialReceiptCannotReallocateAnEpochAfterGrantBeforeBegin()
    {
        var pair = ProjectReceiptPair();
        var fx = ProvisionReceipt(pair);
        var interrupted = new HostLifecycleCoordinator(ReservationPath, new ReceiptOnlyProvider(), fx.Staging, fx.Worker)
        {
            AfterInitialGrantForTests = () => throw new IOException("Fixture cut between Grant and Begin."),
        };
        Assert.Null(await interrupted.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        var installed = File.ReadAllBytes(ReservationPath);
        var head = File.ReadAllBytes(Path.Combine(fx.Scope.AuthorityDirectory, "head.json"));

        for (var retry = 0; retry < 2; retry++)
        {
            var reopened = ReceiptCoordinator(fx.Worker, fx.Staging);
            foreach (var intent in pair.Intents)
                Assert.Null(await reopened.LaunchInitialClosedAsync(intent, CancellationToken.None));
        }
        await AssertReceiptSwitchesBlockedAsync(pair, fx.Staging);
        var state = HostReservation.ReadEnrolled(fx.Scope).Record.State;
        Assert.Equal(new HostReservationState(1, pair.DeepSeek.Target.EngineName, null), state);
        Assert.Equal(installed, File.ReadAllBytes(ReservationPath));
        Assert.Equal(head, File.ReadAllBytes(Path.Combine(fx.Scope.AuthorityDirectory, "head.json")));
        Assert.Equal(fx.Scope, HostReservationScope.Read(ReservationPath));
        Assert.Equal(0, fx.Worker.Sends);
        Assert.Equal(0, fx.Worker.Reconciliations);
        AssertNoReceiptEffect(fx.Worker);
    }

    [Fact(Timeout = 30_000)]
    public async Task EnrolledInitialReceiptSwitchesCannotSettlePreparedOrCrossASplitInstall()
    {
        var pair = ProjectReceiptPair();
        var fx = ProvisionReceipt(pair);
        await AssertReceiptSwitchesBlockedAsync(pair, fx.Staging);
        var headPath = Path.Combine(fx.Scope.AuthorityDirectory, "head.json");
        var head = File.ReadAllBytes(headPath);
        using (var reservation = HostReservation.OpenEnrolled(fx.Scope))
        using (fx.Worker.EnterIssueFence(fx.Scope))
        {
            var plan = pair.DeepSeek.Intent.Plan;
            var epoch = reservation.Grant(plan.Engine);
            reservation.Begin(epoch, HostMutationKind.Launch, plan.Engine, plan.Model, plan.Tier, plan.Digest());
        }
        var prepared = File.ReadAllBytes(ReservationPath);
        await AssertReceiptSwitchesBlockedAsync(pair, fx.Staging);
        Assert.Null(await fx.Coordinator.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        Assert.Equal(prepared, File.ReadAllBytes(ReservationPath));

        using (fx.Worker.EnterIssueFence(fx.Scope)) File.Delete(ReservationPath);
        await AssertReceiptSwitchesBlockedAsync(pair, fx.Staging);
        Assert.Null(await fx.Coordinator.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        Assert.False(File.Exists(ReservationPath));
        Assert.Equal(head, File.ReadAllBytes(headPath));

        using (fx.Worker.EnterIssueFence(fx.Scope))
        {
            File.WriteAllBytes(ReservationPath, prepared);
            File.Delete(headPath);
        }
        await AssertReceiptSwitchesBlockedAsync(pair, fx.Staging);
        Assert.Null(await fx.Coordinator.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        Assert.False(File.Exists(headPath));
        Assert.Equal(prepared, File.ReadAllBytes(ReservationPath));
        Assert.Equal(0, fx.Worker.Sends);
        Assert.Equal(0, fx.Worker.Reconciliations);
        AssertNoReceiptEffect(fx.Worker);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnrolledInitialReceiptRefusesModeLessStateWithoutCommissioning(bool used)
    {
        var pair = ProjectReceiptPair();
        var source = Path.Combine(_directory.FullName, "old-format");
        HostReservation.ProvisionNew(source);
        if (used)
        {
            using var old = HostReservation.Open(source);
            old.Grant(pair.DeepSeek.Target.EngineName);
        }
        File.Copy(source, ReservationPath);
        var bytes = File.ReadAllBytes(ReservationPath);
        var unusedScope = new HostReservationScope(ReservationPath, Guid.NewGuid(), ReceiptEnrollment);
        var worker = new HostEffectLedgerProbe.CoordinatedInitialLaunch(unusedScope, pair.Intents);
        var staging = ReceiptStaging();
        await AssertReceiptSwitchesBlockedAsync(pair, staging);
        foreach (var intent in pair.Intents)
            Assert.Null(await ReceiptCoordinator(worker, staging).LaunchInitialClosedAsync(intent, CancellationToken.None));
        Assert.Equal(bytes, File.ReadAllBytes(ReservationPath));
        Assert.Null(worker.LastHistoryScope);
        AssertNoReceiptEffect(worker);
    }

    [Fact(Timeout = 30_000)]
    public async Task EnrolledInitialReceiptKeepsExplicitlyCommissionedLegacySwitch()
    {
        HostReservation.ProvisionNew(ReservationPath);
        Assert.True(HostReservationScope.Read(ReservationPath).IsLegacy);
        var host = new FakeHost();
        var switched = await new HostLifecycleCoordinator(ReservationPath, host).SwitchAsync(Target("b"), CancellationToken.None);
        Assert.True(switched.Succeeded);
        Assert.Equal(1, host.Stops);
        Assert.Equal(1, host.Creates);
        Assert.Equal(1, host.Verified);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnrolledInitialReceiptRejectsWrongLegacyFrame(bool enrolledFrame)
    {
        HostReservation.ProvisionNew(ReservationPath);
        Assert.True(HostReservationScope.Read(ReservationPath).IsLegacy);
        byte[] frame;
        if (enrolledFrame)
        {
            var source = Path.Combine(_directory.FullName, "enrolled-source");
            var scope = HostReservation.ProvisionEnrolled(source, ReceiptEnrollment);
            Assert.Equal(scope, HostReservation.ReadEnrolled(scope).Record.Scope);
            frame = File.ReadAllBytes(source);
        }
        else
        {
            var body = "{}"u8.ToArray();
            frame = [.. SHA256.HashData(body), .. body];
        }
        File.WriteAllBytes(ReservationPath, frame);

        var legacy = ReceiptCoordinator(null, ReceiptStaging());
        var target = Target("b");
        var (plain, staged) = ReceiptWrapperPublications(target);
        var result = await legacy.SwitchAsync(target, CancellationToken.None, stagedPublicationForTests: plain);
        Assert.Equal("host_reservation_unavailable", result.Reason);
        Assert.Equal(frame, File.ReadAllBytes(ReservationPath));
        var stagedResult = await legacy.SwitchStagedAsync(target, staged, CancellationToken.None);
        Assert.Equal("host_reservation_unavailable", stagedResult.Result.Reason);
        Assert.False(stagedResult.Opened);
        Assert.Equal(frame, File.ReadAllBytes(ReservationPath));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnrolledInitialReceiptUsesCurrentTrustedRootAfterLocalRestore(bool legacyRestore)
    {
        var pair = ProjectReceiptPair();
        if (legacyRestore)
            HostReservation.ProvisionNew(ReservationPath);
        else
        {
            var older = HostReservation.ProvisionEnrolled(ReservationPath, ReceiptEnrollment with { Generation = 1 });
            new HostEffectLedgerProbe.CoordinatedInitialLaunch(older, pair.Intents).Provision(ReceiptSeed(pair));
            File.Delete(ReservationPath + ".lock");
        }
        var oldBytes = File.ReadAllBytes(ReservationPath);
        var catalogEntry = Path.Combine(_directory.FullName, "reservation-authority", Path.GetFileName(ReservationPath) + ".scope");
        var oldScope = File.ReadAllBytes(catalogEntry);
        File.Delete(ReservationPath);
        File.Delete(catalogEntry);

        var fx = ProvisionReceipt(pair);
        RestoreReceiptState(fx, oldBytes);
        // Include the old commissioning descriptor among the restored local files. Only the
        // separate current catalog selects the authority root for this slot.
        File.WriteAllBytes(ReservationPath + ".scope", oldScope);
        var head = File.ReadAllBytes(Path.Combine(fx.Scope.AuthorityDirectory, "head.json"));
        await AssertReceiptSwitchesBlockedAsync(pair, fx.Staging);
        foreach (var intent in pair.Intents)
            Assert.Null(await fx.Coordinator.LaunchInitialClosedAsync(intent, CancellationToken.None));
        Assert.Equal(fx.Scope, fx.Worker.LastHistoryScope);
        Assert.Equal(fx.Scope, HostReservationScope.Read(ReservationPath));
        Assert.Equal(oldBytes, File.ReadAllBytes(ReservationPath));
        Assert.Equal(head, File.ReadAllBytes(Path.Combine(fx.Scope.AuthorityDirectory, "head.json")));
        Assert.Equal(0, fx.Worker.Sends);
        AssertNoReceiptEffect(fx.Worker);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnrolledInitialReceiptWorkerRereadsRolledBackStateBeforeAcceptAndIssue(bool accepted)
    {
        var pair = ProjectReceiptPair();
        var fx = ProvisionReceipt(pair, interruptBefore: accepted ? InitialLaunchPhase.MayHaveIssued : InitialLaunchPhase.Accepted);
        var genesis = File.ReadAllBytes(ReservationPath);
        Assert.Null(await fx.Coordinator.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        var history = Assert.IsType<HostInitialHistory>(fx.Worker.ReadHistory(fx.Scope));
        var dispatch = Assert.IsType<HostInitialDispatch>(history.Dispatch);
        Assert.Equal(accepted ? dispatch : null, history.Accepted);
        var policy = fx.Worker.Observe(fx.Scope);
        var reads = fx.Worker.WorkerReads;
        RestoreReceiptState(fx, genesis);

        if (accepted) Assert.Equal(default(HostMutationOutcome), fx.Worker.Issue(dispatch));
        else Assert.False(fx.Worker.Accept(dispatch));
        Assert.Equal(reads + 1, fx.Worker.WorkerReads);
        Assert.Equal(history, fx.Worker.ReadHistory(fx.Scope));
        Assert.Equal(policy, fx.Worker.Observe(fx.Scope));
        Assert.Equal(genesis, File.ReadAllBytes(ReservationPath));
        Assert.Null(await fx.Coordinator.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        Assert.Equal(1, fx.Worker.Sends);
        AssertNoReceiptEffect(fx.Worker);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnrolledInitialReceiptFencesContradictoryOrUnanchoredApplied(bool contradictory)
    {
        var pair = ProjectReceiptPair();
        var fx = ProvisionReceipt(pair, contradictory ? null : InitialLaunchPhase.MayHaveIssued,
            divergentSettings: contradictory);
        Assert.Null(await fx.Coordinator.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        var installed = HostReservation.ReadEnrolled(fx.Scope);
        Assert.Equal(HostMutationPhase.Dispatching, installed.Record.State.Phase);
        var operation = Assert.IsType<HostMutation>(installed.Record.State.Pending);
        if (!contradictory)
        {
            using var reservation = HostReservation.OpenEnrolled(fx.Scope);
            using var fence = fx.Worker.EnterIssueFence(fx.Scope);
            reservation.RecordAppliedLaunch(operation, ReceiptInstance);
        }
        var saved = File.ReadAllBytes(ReservationPath);
        Assert.Null(await fx.Coordinator.LaunchInitialClosedAsync(pair.DeepSeek.Intent, CancellationToken.None));
        Assert.Null(fx.Coordinator.ReadInitialLaunchReceipt(operation, ReceiptInstance));
        await AssertReceiptSwitchesBlockedAsync(pair, fx.Staging);
        Assert.Equal(saved, File.ReadAllBytes(ReservationPath));
        Assert.Equal(1, fx.Worker.Sends);
        Assert.Equal(contradictory ? 1 : 0, fx.Worker.Creates);
        Assert.Equal(contradictory ? 1 : 0, fx.Worker.Starts);
    }

    private async Task AssertReceiptSwitchesBlockedAsync(ReceiptPair pair, HostClosedRouteStaging staging)
    {
        var legacy = ReceiptCoordinator(null, staging);
        foreach (var target in new[] { pair.DeepSeek.Target, pair.Qwen.Target })
        {
            var (plain, staged) = ReceiptWrapperPublications(target);
            Assert.False((await legacy.SwitchAsync(target, CancellationToken.None, stagedPublicationForTests: plain)).Succeeded);
            var result = await legacy.SwitchStagedAsync(target, staged, CancellationToken.None);
            Assert.False(result.Result.Succeeded);
            Assert.False(result.Opened);
        }
    }

    private static (HostStagedPublication Plain, HostStagedDeadlinePublication Staged) ReceiptWrapperPublications(LifecycleTarget target)
    {
        var plain = new HostStagedPublication(Guid.NewGuid(), _ => throw UnexpectedServing(), _ => throw UnexpectedServing());
        var attempt = new SwitchAttemptId(Guid.NewGuid(), 1);
        var key = new SwitchSampleKey(attempt.RuntimeEpoch, DockerLifecycleProvider.ProviderKind, target.Model,
            target.EffectiveProfile!, 1, SwitchObservationClass.FreshContainer, null);
        var staged = new HostStagedDeadlinePublication(Guid.NewGuid(), attempt, key, TimeProvider.System,
            (_, _) => throw UnexpectedServing(), (_, _, _, _) => throw UnexpectedServing(), _ => throw UnexpectedServing(),
            (_, _, _) => throw UnexpectedServing(), () => throw UnexpectedServing());
        return (plain, staged);
    }

    private static async Task AssertReceiptIngressClosedAsync(HostClosedRouteStaging staging, HostReservationScope scope, HostInitialReceipt receipt)
    {
        var operation = receipt.Operation;
        var route = new VerifiedInstanceRoute(scope.Enrollment!.Group, scope.Enrollment.Generation, operation.Engine,
            operation.PlanDigest!, receipt.InstanceId, "fixture-serving-instance", operation.Epoch, 1);
        var issuer = new FixtureIssuer(staging);
        await AssertDeniedAsync(issuer, Invocation(route), "{}"u8.ToArray(), CancellationToken.None);
        Assert.Equal(0, issuer.Grants);
        Assert.Equal(0, issuer.Permits);
    }

    private ReceiptFixture ProvisionReceipt(ReceiptPair pair, InitialLaunchPhase? interruptBefore = null, bool lostReply = false,
        bool divergentSettings = false)
    {
        var scope = HostReservation.ProvisionEnrolled(ReservationPath, ReceiptEnrollment);
        var worker = new HostEffectLedgerProbe.CoordinatedInitialLaunch(scope, pair.Intents)
        {
            InterruptBefore = interruptBefore, LoseReply = lostReply,
        };
        worker.Provision(ReceiptSeed(pair, divergentSettings));
        var staging = ReceiptStaging();
        return new(scope, worker, staging, ReceiptCoordinator(worker, staging));
    }

    private static CoordinatedDaemonSeed ReceiptSeed(ReceiptPair pair, bool divergent = false) =>
        new(ReceiptInstance, divergent ? pair.Qwen.Approved.CreateDigest : pair.DeepSeek.Approved.CreateDigest,
            CompletePair: true, ReceiptInstance);

    private void RestoreReceiptState(ReceiptFixture fixture, byte[] bytes)
    {
        using var fence = fixture.Worker.EnterIssueFence(fixture.Scope);
        File.WriteAllBytes(ReservationPath, bytes);
    }

    private HostLifecycleCoordinator ReceiptCoordinator(IHostInitialLaunchAuthority? worker, HostClosedRouteStaging staging) =>
        new(ReservationPath, new ReceiptOnlyProvider(), staging, worker);

    private static HostClosedRouteStaging ReceiptStaging() => new(_ => throw UnexpectedServing(), (_, _) => throw UnexpectedServing());

    private static void AssertNoReceiptEffect(HostEffectLedgerProbe.CoordinatedInitialLaunch worker)
    {
        Assert.Equal(0, worker.Creates);
        Assert.Equal(0, worker.Starts);
    }

    private static void AssertOneReceiptEffect(HostEffectLedgerProbe.CoordinatedInitialLaunch worker)
    {
        Assert.Equal(1, worker.Creates);
        Assert.Equal(1, worker.Starts);
    }

    private static ReceiptPair ProjectReceiptPair()
    {
        using var file = OpenPairResource("deepseek.toml");
        using var reader = new StreamReader(file, leaveOpen: true);
        var deepSeek = RouterConfigLoader.Load(reader.ReadToEnd()).Engines["deepseek"];
        using var archiveFile = OpenPairResource("selector-reference.json");
        using var archive = JsonDocument.Parse(archiveFile);
        return new(Project(deepSeek, "deepseek-v4.1-flash-1017-iq2-xxs", "max"),
            Project(LoadPairEngine(), "qwen3.8-flash-next-iq3_s", "256k-4"));

        ReceiptTarget Project(EngineDefinition engine, string model, string tier)
        {
            var definition = engine.Catalog[model];
            var profile = Assert.Single(definition.ServingProfiles, item => item.Name == tier);
            var target = new LifecycleTarget(engine.Name, model, profile, definition);
            var archived = Assert.Single(archive.RootElement.EnumerateArray(), row => row.GetProperty("engine").GetString() == engine.Name &&
                row.GetProperty("model").GetString() == model && row.GetProperty("tier").GetString() == tier).GetProperty("create");
            var approved = ReadPairCreate(archived);
            using var handler = new FakeDockerDaemonHandler { TransportFails = true };
            using var projection = new DockerLifecycleProvider(engine.Docker!, engine.Name, handler);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(projection.BuildCreateRequest(target, engine.Docker!.ImageIdentity!),
                DockerJsonContext.Default.DockerCreateRequest);
            var approvedBytes = JsonSerializer.SerializeToUtf8Bytes(approved, DockerJsonContext.Default.DockerCreateRequest);
            var plan = PairCheckpoint(engine, target, approved) with { Configuration = engine.Name, BackendRoute = engine.Endpoint.AbsoluteUri };
            Assert.Empty(handler.Requests);
            return new(target, HostInitialLaunchIntent.Freeze(plan, bytes), HostInitialLaunchIntent.Freeze(plan, approvedBytes));
        }
    }

    private static InvalidOperationException UnexpectedServing() => new("Receipt-only work reached legacy provider or serving code.");

    private sealed class ReceiptOnlyProvider : IHostLifecycleProvider
    {
        public ValueTask<HostOccupancy> ObserveAsync(Guid queryId, CancellationToken ct) => throw UnexpectedServing();
        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct) => throw UnexpectedServing();
        public ValueTask<IHostDrain> CloseAndDrainAsync(HostResident? incumbent, CancellationToken ct) => throw UnexpectedServing();
        public string GetLaunchPlanDigest(LifecycleTarget target) => throw UnexpectedServing();
        public ValueTask<HostMutationOutcome> StopAsync(HostMutation operation, CancellationToken ct) => throw UnexpectedServing();
        public ValueTask<HostMutationOutcome> LaunchAsync(HostMutation operation, LifecycleTarget target, CancellationToken ct) => throw UnexpectedServing();
        public ValueTask<HostMutationOutcome> ReconcileAsync(HostMutation operation, CancellationToken ct) => throw UnexpectedServing();
        public ValueTask<LifecycleResult> VerifyReadyAsync(HostMutation launch, string containerId, CancellationToken ct) => throw UnexpectedServing();
    }

    private sealed record ReceiptTarget(LifecycleTarget Target, HostInitialLaunchIntent Intent, HostInitialLaunchIntent Approved);
    private sealed record ReceiptPair(ReceiptTarget DeepSeek, ReceiptTarget Qwen)
    {
        public HostInitialLaunchIntent[] Intents => [DeepSeek.Approved, Qwen.Approved];
    }
    private sealed record ReceiptFixture(HostReservationScope Scope, HostEffectLedgerProbe.CoordinatedInitialLaunch Worker,
        HostClosedRouteStaging Staging, HostLifecycleCoordinator Coordinator);
}
