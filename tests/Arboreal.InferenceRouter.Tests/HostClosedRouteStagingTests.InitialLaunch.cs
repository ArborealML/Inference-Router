using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    private const int InitialWorkerGeneration = 12;
    private static readonly HostEffectLedgerJsonContext InitialJson = HostEffectLedgerJsonContext.Default;
    private string InitialJournal => Path.Combine(_directory.FullName, "initial-journal");
    private string InitialAuthorityRoot => Path.Combine(_directory.FullName, "independent-authority");
    private string InitialHeadPath => Path.Combine(InitialAuthorityRoot, "head.json");

    [Fact(Timeout = 30_000)]
    public async Task SyntheticInitialLaunchRecoversOriginalReceiptWithoutReissueOrGrant()
    {
        var (qwen, swift) = ProjectInitialPair();
        var ledger = ProvisionInitialLaunch();
        var witness = ledger.Observer.ReadAuthority();
        var command = Assert.IsType<InitialLaunchCommand>(ledger.Dispatch(qwen.Plan, qwen.Bytes, witness));
        Assert.Equal(1, command.Operation.Epoch);
        Assert.Equal(qwen.Bytes, Convert.FromBase64String(command.CreateBytes));
        Assert.True(ledger.Accept(command, InitialWorkerGeneration));
        Assert.Null(ledger.Dispatch(swift.Plan, swift.Bytes, witness));
        var instance = Assert.IsType<string>(ledger.Issue(command, InitialWorkerGeneration));
        Assert.Equal(64, instance.Length);
        Assert.Null(ledger.Lookup(command.Operation, instance));
        Assert.Null(ledger.Dispatch(swift.Plan, swift.Bytes, witness));
        var observations = ledger.Observer.Observe();
        var created = Assert.IsType<InitialLaunchCreate>(observations.Create);
        Assert.Equal(qwen.Bytes, Convert.FromBase64String(created.CreateBytes));
        Assert.Equal("0,1", created.Devices);
        Assert.Equal(new InitialLaunchStart(command.Operation, instance), observations.Start);
        var originalReceipt = Assert.IsType<byte[]>(ledger.Complete(command, instance));

        // A new ledger and observer reopen the checkpoint, local history and separately stored head.
        var reopened = OpenInitialLaunch();
        var readback = Assert.IsType<byte[]>(reopened.Lookup(command.Operation, instance));
        Assert.Equal(originalReceipt, readback);
        Assert.Equal(readback, reopened.Lookup(command.Operation, instance));
        Assert.Null(reopened.Lookup(command.Operation with { Epoch = command.Operation.Epoch + 1 }, instance));
        Assert.Null(reopened.Lookup(command.Operation, new string('F', 64)));
        Assert.False(reopened.Accept(command, InitialWorkerGeneration));
        Assert.Null(reopened.Issue(command, InitialWorkerGeneration));
        Assert.Null(reopened.Dispatch(swift.Plan, swift.Bytes, reopened.Observer.ReadAuthority()));

        var receipt = Assert.IsType<InitialLaunchReceipt>(JsonSerializer.Deserialize(readback, InitialJson.InitialLaunchReceipt));
        Assert.Equal(new HostCheckpointLaunchEffect(command.Operation, qwen.Plan, instance), receipt.Launch);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(qwen.Bytes)), receipt.CreateDigest);
        Assert.Equal(witness.Scope, receipt.Scope);
        Assert.Equal(InitialLaunchPhase.Applied, ReadInitialHead().Phase);
        Assert.Equal(Enum.GetValues<InitialLaunchPhase>(), ReadInitialHistory().Select(record => record.Phase));
        Assert.All(ReadInitialHistory().Skip(1), record => Assert.Equal(command, record.Command));
        AssertInitialAttempts(command.Operation);

        var staging = new HostClosedRouteStaging(operation => operation == receipt.Launch.Operation ? receipt.Launch : null,
            (launch, generation) => PairRoute(launch.Operation, launch.InstanceId, generation));
        var issuer = new FixtureIssuer(staging);
        var invocation = Invocation(PairRoute(receipt.Launch.Operation, receipt.Launch.InstanceId, 1));
        Assert.True(observations.Authority.IngressClosed);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await AssertDeniedAsync(issuer, invocation, "{}"u8.ToArray(), timeout.Token);
        Assert.Equal(0, issuer.Grants);
        Assert.Equal(0, issuer.Permits);
    }

    [Fact]
    public void SyntheticInitialLaunchRequiresCompleteAuthorityAndExactAcceptedBinding()
    {
        var (qwen, swift) = ProjectInitialPair();
        var ledger = ProvisionInitialLaunch();
        var witness = ledger.Observer.ReadAuthority();
        Assert.Null(ledger.Dispatch(qwen.Plan, qwen.Bytes, witness: null));
        var partial = witness with { Devices = "0", PolicyGeneration = witness.PolicyGeneration + 1 };
        ledger.ChangeAuthority(partial);
        Assert.Null(ledger.Dispatch(qwen.Plan, qwen.Bytes, partial));
        var foreign = witness with { ForeignResident = true, PolicyGeneration = partial.PolicyGeneration + 1 };
        ledger.ChangeAuthority(foreign);
        Assert.Null(ledger.Dispatch(qwen.Plan, qwen.Bytes, foreign));
        Assert.Equal(InitialLaunchPhase.Genesis, ReadInitialHead().Phase);

        witness = witness with { PolicyGeneration = foreign.PolicyGeneration + 1 };
        ledger.ChangeAuthority(witness);
        var command = Assert.IsType<InitialLaunchCommand>(ledger.Dispatch(qwen.Plan, qwen.Bytes, witness));
        var substitution = command with { CreateBytes = Convert.ToBase64String(swift.Bytes),
            CreateDigest = Convert.ToHexString(SHA256.HashData(swift.Bytes)) };
        Assert.False(ledger.Accept(substitution, InitialWorkerGeneration));
        Assert.False(ledger.Accept(command, InitialWorkerGeneration - 1));
        Assert.Null(ledger.Issue(command, InitialWorkerGeneration));
        Assert.True(ledger.Accept(command, InitialWorkerGeneration));
        Assert.Null(ledger.Issue(substitution, InitialWorkerGeneration));
        Assert.Null(ledger.Issue(command, InitialWorkerGeneration - 1));
        Assert.Equal(InitialLaunchPhase.Accepted, ReadInitialHead().Phase);
        AssertNoInitialEffects(ledger);
    }

    public enum InitialObservationFault { MissingStart, WrongStartId, WrongCreateBytes, PartialPair }

    [Theory]
    [InlineData(InitialObservationFault.MissingStart)]
    [InlineData(InitialObservationFault.WrongStartId)]
    [InlineData(InitialObservationFault.WrongCreateBytes)]
    [InlineData(InitialObservationFault.PartialPair)]
    public void SyntheticInitialLaunchLeavesContradictoryObserverEvidencePending(InitialObservationFault fault)
    {
        var (qwen, swift) = ProjectInitialPair();
        var ledger = ProvisionInitialLaunch();
        var command = DispatchAndAcceptInitial(ledger, qwen);
        var instance = Assert.IsType<string>(ledger.Issue(command, InitialWorkerGeneration));
        var observation = ledger.Observer.Observe();
        var created = Assert.IsType<InitialLaunchCreate>(observation.Create);
        var started = Assert.IsType<InitialLaunchStart>(observation.Start);
        var startPath = Path.Combine(_directory.FullName, "initial-started.json");
        // Change only the independent fake observation source. Completion reads this source itself.
        switch (fault)
        {
            case InitialObservationFault.MissingStart:
                File.Delete(startPath);
                break;
            case InitialObservationFault.WrongStartId:
                File.WriteAllBytes(startPath,
                    JsonSerializer.SerializeToUtf8Bytes(started with { InstanceId = new string('F', 64) }, InitialJson.InitialLaunchStart));
                break;
            case InitialObservationFault.WrongCreateBytes:
                File.WriteAllBytes(Path.Combine(_directory.FullName, "initial-created.json"),
                    JsonSerializer.SerializeToUtf8Bytes(created with { CreateBytes = Convert.ToBase64String(swift.Bytes) },
                        InitialJson.InitialLaunchCreate));
                break;
            case InitialObservationFault.PartialPair:
                File.WriteAllBytes(Path.Combine(_directory.FullName, "initial-created.json"),
                    JsonSerializer.SerializeToUtf8Bytes(created with { Devices = "0" }, InitialJson.InitialLaunchCreate));
                break;
        }

        var reopened = OpenInitialLaunch();
        Assert.Null(reopened.Complete(command, instance));
        Assert.Null(reopened.Lookup(command.Operation, instance));
        Assert.Null(reopened.Issue(command, InitialWorkerGeneration));
        Assert.Null(reopened.Dispatch(swift.Plan, swift.Bytes, reopened.Observer.ReadAuthority()));
        Assert.Equal(InitialLaunchPhase.MayHaveIssued, ReadInitialHead().Phase);
        AssertLaunchDispatching(command.Operation);
        AssertInitialAttempts(command.Operation);
    }

    [Fact]
    public void SyntheticInitialLaunchFencesValidOldRestoreAgainstAdvancedHead()
    {
        var (qwen, swift) = ProjectInitialPair();
        var ledger = ProvisionInitialLaunch();
        var command = DispatchAndAcceptInitial(ledger, qwen);
        var checkpointFile = ReservationPath + ".checkpoint";
        var oldCheckpoint = File.ReadAllBytes(checkpointFile);
        var acceptedPath = InitialRecordPath(InitialLaunchPhase.Accepted);
        var oldAccepted = File.ReadAllBytes(acceptedPath);
        var instance = Assert.IsType<string>(ledger.Issue(command, InitialWorkerGeneration));
        Assert.NotNull(ledger.Complete(command, instance));
        var advancedHead = File.ReadAllBytes(InitialHeadPath);

        // Restore the earlier, internally valid installed local state. The separate head survives.
        File.WriteAllBytes(checkpointFile, oldCheckpoint);
        File.Delete(InitialRecordPath(InitialLaunchPhase.MayHaveIssued));
        File.Delete(InitialRecordPath(InitialLaunchPhase.Applied));
        Assert.Equal(oldAccepted, File.ReadAllBytes(acceptedPath));
        AssertLaunchDispatching(command.Operation);
        var reopened = OpenInitialLaunch();
        Assert.Throws<IOException>(() => reopened.Lookup(command.Operation, instance));
        Assert.Throws<IOException>(() => reopened.Issue(command, InitialWorkerGeneration));
        Assert.Throws<IOException>(() => reopened.Dispatch(swift.Plan, swift.Bytes, reopened.Observer.ReadAuthority()));
        Assert.Equal(advancedHead, File.ReadAllBytes(InitialHeadPath));
        AssertInitialAttempts(command.Operation);
    }

    [Fact]
    public void SyntheticInitialLaunchSplitHeadCannotAuthorizeFirstDaemonEntry()
    {
        var (qwen, swift) = ProjectInitialPair();
        var ledger = ProvisionInitialLaunch();
        var command = DispatchAndAcceptInitial(ledger, qwen);
        Assert.Throws<IOException>(() => ledger.Issue(command, InitialWorkerGeneration, interruptBeforeHead: true));
        Assert.Equal(InitialLaunchPhase.Accepted, ReadInitialHead().Phase);
        Assert.True(File.Exists(InitialRecordPath(InitialLaunchPhase.MayHaveIssued)));

        var reopened = OpenInitialLaunch();
        Assert.Throws<IOException>(() => reopened.Issue(command, InitialWorkerGeneration));
        Assert.Throws<IOException>(() => reopened.Dispatch(swift.Plan, swift.Bytes, reopened.Observer.ReadAuthority()));
        AssertLaunchDispatching(command.Operation);
        AssertNoInitialEffects(reopened);
    }

    [Fact]
    public void SyntheticInitialLaunchRejectsDelayedDeliveryAfterAuthorityChangesAndRestores()
    {
        var (qwen, swift) = ProjectInitialPair();
        var ledger = ProvisionInitialLaunch();
        var command = DispatchAndAcceptInitial(ledger, qwen);
        var saved = command.Authority;
        ledger.ChangeAuthority(saved with { ExclusiveWriter = false, PolicyGeneration = saved.PolicyGeneration + 1 });
        ledger.ChangeAuthority(saved with { PolicyGeneration = saved.PolicyGeneration + 2, OwnerGeneration = saved.OwnerGeneration + 1 });

        var reopened = OpenInitialLaunch();
        var current = reopened.Observer.ReadAuthority();
        Assert.True(current.ExclusiveWriter);
        Assert.Equal(saved.Coverage, current.Coverage);
        Assert.Equal(saved.Devices, current.Devices);
        Assert.Equal(saved.Scope, current.Scope);
        Assert.Null(reopened.Issue(command, InitialWorkerGeneration));
        Assert.Null(reopened.Issue(command with { Authority = current }, InitialWorkerGeneration));
        Assert.Null(reopened.Dispatch(swift.Plan, swift.Bytes, current));
        Assert.Equal(InitialLaunchPhase.Accepted, ReadInitialHead().Phase);
        AssertLaunchDispatching(command.Operation);
        AssertNoInitialEffects(reopened);
    }

    private sealed record InitialPairPlan(HostCheckpointPlan Plan, byte[] Bytes);

    private static (InitialPairPlan Qwen, InitialPairPlan Swift) ProjectInitialPair()
    {
        var engine = LoadPairEngine();
        using var daemon = new FakeDockerDaemonHandler { TransportFails = true };
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, daemon);
        using var resource = OpenPairResource("selector-reference.json");
        using var reference = JsonDocument.Parse(resource);
        var resolver = new VerifiedDispatchTargetResolver(engine, provider);
        var qwen = FreezePairTarget(engine, provider, resolver, reference.RootElement, "flash-next", "256k-4",
            "qwen3.8-flash-next-iq3_s", "256k-4", 256_000, 4);
        var swift = FreezePairTarget(engine, provider, resolver, reference.RootElement, "swift-flash-next", null,
            "swift-1.5-iq3_s", "max", 262_144, 1);
        var first = Serialize(qwen);
        var second = Serialize(swift);
        Assert.Empty(daemon.Requests);
        return (first, second);

        InitialPairPlan Serialize(FrozenPairTarget target) => new(target.ExpectedPlan, JsonSerializer.SerializeToUtf8Bytes(
            provider.BuildCreateRequest(target.Launch, engine.Docker!.ImageIdentity!), DockerJsonContext.Default.DockerCreateRequest));
    }

    private HostEffectLedgerProbe.InitialLaunchLedger OpenInitialLaunch() =>
        new HostEffectLedgerProbe(ReservationPath).InitialLaunch(InitialAuthorityRoot);

    private HostEffectLedgerProbe.InitialLaunchLedger ProvisionInitialLaunch()
    {
        var ledger = OpenInitialLaunch();
        ledger.Provision();
        return ledger;
    }

    private static InitialLaunchCommand DispatchAndAcceptInitial(HostEffectLedgerProbe.InitialLaunchLedger ledger, InitialPairPlan target)
    {
        var command = Assert.IsType<InitialLaunchCommand>(ledger.Dispatch(target.Plan, target.Bytes, ledger.Observer.ReadAuthority()));
        Assert.True(ledger.Accept(command, InitialWorkerGeneration));
        return command;
    }

    private InitialLaunchRecord[] ReadInitialHistory() => Directory.GetFiles(InitialJournal, "*.json").Order(StringComparer.Ordinal)
        .Select(path => Assert.IsType<InitialLaunchRecord>(JsonSerializer.Deserialize(File.ReadAllBytes(path), InitialJson.InitialLaunchRecord)))
        .ToArray();

    private InitialLaunchRecord ReadInitialHead()
    {
        var head = Assert.IsType<InitialLaunchHead>(JsonSerializer.Deserialize(File.ReadAllBytes(InitialHeadPath), InitialJson.InitialLaunchHead));
        return ReadInitialHistory()[head.Revision];
    }

    private string InitialRecordPath(InitialLaunchPhase phase) =>
        Path.Combine(InitialJournal, ((int)phase).ToString(CultureInfo.InvariantCulture) + ".json");

    private void AssertLaunchDispatching(HostMutation operation)
    {
        using var checkpoint = HostEffectCheckpoint.Open(ReservationPath);
        Assert.Equal(HostCheckpointPhase.LaunchDispatching, checkpoint.State.Phase);
        Assert.Equal(operation, checkpoint.State.Transition?.Launch);
        Assert.Null(checkpoint.State.AppliedLaunch);
    }

    private void AssertInitialAttempts(HostMutation operation)
    {
        Assert.Equal(operation, ReadSingleAttempt("initial-create-attempts"));
        Assert.Equal(operation, ReadSingleAttempt("initial-start-attempts"));
    }

    private HostMutation? ReadSingleAttempt(string journal) => JsonSerializer.Deserialize(
        File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(_directory.FullName, journal)))), InitialJson.HostMutation);

    private void AssertNoInitialEffects(HostEffectLedgerProbe.InitialLaunchLedger ledger)
    {
        var observed = ledger.Observer.Observe();
        Assert.Null(observed.Create);
        Assert.Null(observed.Start);
        Assert.False(Directory.Exists(Path.Combine(_directory.FullName, "initial-create-attempts")));
        Assert.False(Directory.Exists(Path.Combine(_directory.FullName, "initial-start-attempts")));
    }
}
