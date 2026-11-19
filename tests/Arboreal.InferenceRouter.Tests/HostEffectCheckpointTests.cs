using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class HostEffectCheckpointTests : IDisposable
{
    private static HostCheckpointPlan PlanA { get; } = Plan("a");
    private static HostCheckpointPlan PlanB { get; } = Plan("b");
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("air-checkpoint-");
    private string LockPath => Path.Combine(_directory.FullName, "host.lock");
    private string InstalledPath => LockPath + ".checkpoint";

    [Fact]
    public void OnlyExplicitProvisionCreatesGenesisAndAnOldSnapshotCannotReseed()
    {
        Assert.ThrowsAny<IOException>(() => Read());
        Assert.False(File.Exists(LockPath));
        Assert.False(File.Exists(InstalledPath));

        HostEffectCheckpoint.ProvisionNew(LockPath);
        Assert.Equal(HostEffectCheckpointState.Genesis, Read());
        var a = InstallA();
        File.Copy(InstalledPath, InstalledPath + ".new");
        File.Copy(InstalledPath, InstalledPath + ".stage");
        Assert.Equal(a, Read().AppliedLaunch);

        File.Delete(InstalledPath);
        Assert.ThrowsAny<IOException>(() => Read());
        Assert.ThrowsAny<IOException>(() => HostEffectCheckpoint.ProvisionNew(LockPath));
        Assert.False(File.Exists(InstalledPath));

        File.WriteAllBytes(InstalledPath, new byte[80]);
        Assert.ThrowsAny<IOException>(() => Read());
        Assert.ThrowsAny<IOException>(() => HostEffectCheckpoint.ProvisionNew(LockPath));
        Assert.Equal(new byte[80], File.ReadAllBytes(InstalledPath));
    }

    [Fact]
    public void ReopenRejectsChecksummedResidentWithAnOlderLaunchEpoch()
    {
        HostEffectCheckpoint.ProvisionNew(LockPath);
        var launch = InstallA();
        var resident = Read();
        Assert.Equal(launch.Operation.Epoch, resident.Epoch);
        WriteChecksummedState(resident with { Epoch = resident.Epoch + 1, Revision = resident.Revision + 1 });

        Assert.Throws<IOException>(() => Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReopenRejectsChecksummedSuccessorWithoutMatchingPredecessorStop(bool wrongStopEpoch)
    {
        HostEffectCheckpoint.ProvisionNew(LockPath);
        var a = InstallA();
        var stop = Change(checkpoint => checkpoint.PrepareSuccessor(PlanB));
        CompleteHandoff(a, stop, PlanB);
        var resident = Read();
        var history = Assert.IsType<HostCheckpointStoppedResident>(resident.LastStoppedResident);
        var wrongStop = history.Effect with { Operation = stop with { Epoch = stop.Epoch + 1 } };
        WriteChecksummedState(resident with
        {
            LastStoppedResident = wrongStopEpoch ? history with { Effect = wrongStop } : null,
        });

        Assert.Throws<IOException>(() => Read());
    }

    [Fact]
    public void EveryInstalledHandoffPhaseRecoversTheExactStopAndFrozenSuccessor()
    {
        HostEffectCheckpoint.ProvisionNew(LockPath);
        var a = InstallA();
        File.Copy(InstalledPath, InstalledPath + ".stage");
        File.Copy(InstalledPath, InstalledPath + ".new");
        var stop = Change(checkpoint => checkpoint.PrepareSuccessor(PlanB));
        var prepared = Read();
        Assert.Equal(HostCheckpointPhase.StopPrepared, prepared.Phase);
        Assert.Equal(a, prepared.AppliedLaunch);
        var transition = Assert.IsType<HostCheckpointTransition>(prepared.Transition);
        Assert.Equal(PlanB, transition.Plan);
        Assert.Equal(stop, transition.Stop);
        Assert.Equal(a.Operation.Epoch + 1, stop.Epoch);
        Assert.Equal(a.Operation.Engine, stop.Engine);
        Assert.Equal(a.InstanceId, stop.Target);

        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.MarkStopDispatching(stop with
        {
            Target = "sibling-id",
        })));
        Advance(checkpoint => checkpoint.MarkStopDispatching(stop));
        Assert.Equal(prepared with { Revision = prepared.Revision + 1, Phase = HostCheckpointPhase.StopDispatching }, Read());
        var stopEffect = new HostCheckpointStopEffect(stop, a.Operation, a.InstanceId);
        var stopReceipt = Applied(stop, a.InstanceId);
        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.RecordAppliedStop(
            Applied(stop, "sibling-id"), stopEffect)));
        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.RecordAppliedStop(
            stopReceipt, stopEffect with { IncumbentLaunch = a.Operation with { Epoch = stop.Epoch } })));
        Assert.Equal(HostCheckpointPhase.StopDispatching, Read().Phase);
        Advance(checkpoint => checkpoint.RecordAppliedStop(stopReceipt, stopEffect));
        var stopped = Read();
        Assert.Equal(HostCheckpointPhase.StopApplied, stopped.Phase);
        Assert.Equal(stopEffect, stopped.Transition!.StopEffect);

        Assert.Throws<InvalidOperationException>(() => Change(checkpoint => checkpoint.PrepareSuccessorLaunch(Plan("c"))));
        Assert.Throws<InvalidOperationException>(() => Change(checkpoint => checkpoint.PrepareSuccessorLaunch(PlanB with
        {
            ReadinessTimeoutMs = PlanB.ReadinessTimeoutMs + 1,
        })));
        Assert.Equal(stopped, Read());

        var launch = Change(checkpoint => checkpoint.PrepareSuccessorLaunch(PlanB));
        Assert.Equal(stop.Epoch, launch.Epoch);
        Assert.NotEqual(stop.Id, launch.Id);
        Assert.Equal(PlanB.Digest(), launch.PlanDigest);
        var launchPrepared = Read();
        Assert.Equal(HostCheckpointPhase.LaunchPrepared, launchPrepared.Phase);
        Assert.Equal(launch, launchPrepared.Transition!.Launch);
        Advance(checkpoint => checkpoint.MarkLaunchDispatching(launch));
        var dispatching = Read();
        Assert.Equal(HostCheckpointPhase.LaunchDispatching, dispatching.Phase);
        var b = new HostCheckpointLaunchEffect(launch, PlanB, "b-exact-id");
        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.RecordAppliedLaunch(
            Applied(launch, "false-id"), b)));
        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.RecordAppliedLaunch(
            Applied(launch, b.InstanceId), b with { Plan = PlanB with { BackendRoute = "another-route" } })));
        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.MarkLaunchDispatching(launch)));
        Assert.Equal(dispatching, Read());

        Advance(checkpoint => checkpoint.RecordAppliedLaunch(Applied(launch, b.InstanceId), b));
        var resident = Read();
        Assert.Equal(HostCheckpointPhase.Resident, resident.Phase);
        Assert.Equal(b, resident.AppliedLaunch);
        Assert.Equal(new HostCheckpointStoppedResident(a, stopEffect), resident.LastStoppedResident);
        Assert.Null(resident.Transition);

        var planC = Plan("c");
        var stopB = Change(checkpoint => checkpoint.PrepareSuccessor(planC));
        var preparedC = Read();
        Assert.Equal(b, preparedC.AppliedLaunch);
        Assert.Equal(resident.LastStoppedResident, preparedC.LastStoppedResident);
        Assert.Equal(b.InstanceId, stopB.Target);
        Assert.Equal(b.Operation.Epoch + 1, stopB.Epoch);
        Assert.NotEqual(stop.Id, stopB.Id);
        var c = CompleteHandoff(b, stopB, planC);
        var residentC = Read();
        Assert.Equal(c, residentC.AppliedLaunch);
        Assert.Equal(stopB.Epoch, residentC.Epoch);
        Assert.Equal(new HostCheckpointStoppedResident(b, new HostCheckpointStopEffect(stopB, b.Operation, b.InstanceId)),
            residentC.LastStoppedResident);
    }

    [Fact]
    public void RetainARequiresUnsentPreparedStopOrMatchingBeforeIssueFence()
    {
        HostEffectCheckpoint.ProvisionNew(LockPath);
        var a = InstallA();
        var prepared = Change(checkpoint => checkpoint.PrepareSuccessor(PlanB));
        Advance(checkpoint => checkpoint.AbortPreparedStop(prepared));
        var retainedBeforeDispatch = Read();
        Assert.Equal(HostCheckpointPhase.RetainedBeforeDispatch, retainedBeforeDispatch.Phase);
        Assert.Equal(prepared.Epoch, retainedBeforeDispatch.Epoch);
        Assert.Equal(a, retainedBeforeDispatch.AppliedLaunch);
        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.MarkStopDispatching(prepared)));

        var dispatched = Change(checkpoint => checkpoint.PrepareSuccessor(PlanB));
        Advance(checkpoint => checkpoint.MarkStopDispatching(dispatched));
        var unresolved = Read();
        Assert.Equal(prepared.Epoch + 1, unresolved.Epoch);
        var rejection = new HostMutationOutcome(dispatched, HostMutationStatus.RejectedBeforeIssue);
        var fence = new HostCheckpointBeforeIssueFence(dispatched);
        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.AbortPreparedStop(dispatched)));
        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.RetainAfterRejectedStop(
            new HostMutationOutcome(dispatched, HostMutationStatus.UnknownAfterIssue), fence)));
        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.RetainAfterRejectedStop(rejection, default)));
        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.RetainAfterRejectedStop(
            rejection with { ContainerId = a.InstanceId }, fence)));
        Assert.Throws<InvalidOperationException>(() => Advance(checkpoint => checkpoint.RetainAfterRejectedStop(
            rejection, new HostCheckpointBeforeIssueFence(prepared))));
        Assert.Throws<InvalidOperationException>(() => Change(checkpoint => checkpoint.PrepareSuccessor(Plan("c"))));
        Assert.Throws<InvalidOperationException>(() => Change(checkpoint => checkpoint.PrepareSuccessorLaunch(PlanB)));
        Assert.Equal(unresolved, Read());

        Advance(checkpoint => checkpoint.RetainAfterRejectedStop(rejection, fence));
        var retained = Read();
        Assert.Equal(HostCheckpointPhase.RetainedAfterRejection, retained.Phase);
        Assert.Equal(dispatched.Epoch, retained.Epoch);
        Assert.Equal(dispatched, retained.Transition!.Stop);
        Assert.Equal(a, retained.AppliedLaunch);

        var nextStop = Change(checkpoint => checkpoint.PrepareSuccessor(PlanB));
        Assert.Equal(retained.Epoch + 1, nextStop.Epoch);
        Assert.NotEqual(dispatched.Id, nextStop.Id);
        var b = CompleteHandoff(a, nextStop, PlanB);
        var resident = Read();
        Assert.Equal(b, resident.AppliedLaunch);
        Assert.Equal(nextStop.Epoch, resident.Epoch);
        Assert.Equal(new HostCheckpointStoppedResident(a, new HostCheckpointStopEffect(nextStop, a.Operation, a.InstanceId)),
            resident.LastStoppedResident);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbiguousReplacementFencesOldHandleAndReopenUsesOnlyInstalledVersion(bool installedBeforeError)
    {
        HostEffectCheckpoint.ProvisionNew(LockPath);
        var a = InstallA();
        var predecessor = Read();
        using (var checkpoint = HostEffectCheckpoint.Open(LockPath, (temporary, installed) =>
        {
            if (installedBeforeError) File.Move(temporary, installed, overwrite: true);
            throw new IOException("The simulated replacement result is unavailable.");
        }))
        {
            Assert.Throws<IOException>(() => checkpoint.PrepareSuccessor(PlanB));
            Assert.Throws<IOException>(() => checkpoint.PrepareSuccessor(Plan("c")));
            Assert.Throws<IOException>(() => checkpoint.State);
            Assert.ThrowsAny<IOException>(() => Read());
        }

        var reopened = Read();
        Assert.Equal(a, reopened.AppliedLaunch);
        if (!installedBeforeError)
        {
            Assert.Equal(predecessor, reopened);
            Assert.True(File.Exists(InstalledPath + ".new"));
            return;
        }

        Assert.Equal(HostCheckpointPhase.StopPrepared, reopened.Phase);
        Assert.Equal(predecessor.Revision + 1, reopened.Revision);
        var transition = Assert.IsType<HostCheckpointTransition>(reopened.Transition);
        Assert.Equal(PlanB, transition.Plan);
        Assert.Equal(a.InstanceId, transition.Stop!.Target);
    }

    [Fact]
    public void TransitionRereadsInstalledPredecessorEvenUnderTheStableLock()
    {
        HostEffectCheckpoint.ProvisionNew(LockPath);
        var a = InstallA();
        var resident = Read();
        var differentResident = resident with { AppliedLaunch = a with { InstanceId = "different-instance" } };
        using (var checkpoint = HostEffectCheckpoint.Open(LockPath))
        {
            // Bypass the lock contract to test a different installed predecessor with the same phase and revision.
            WriteChecksummedState(differentResident);
            Assert.Throws<IOException>(() => checkpoint.PrepareSuccessor(PlanB));
            Assert.Throws<IOException>(() => checkpoint.State);
        }
        Assert.Equal(differentResident, Read());
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompetingOpenerCannotEnterAcrossReplacementAndRecoversAfterChildDeath(bool dieAfterReplacement)
    {
        HostEffectCheckpoint.ProvisionNew(LockPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Arboreal.InferenceRouter.HostReservationProbe.dll"));
        start.ArgumentList.Add("--checkpoint-replace");
        start.ArgumentList.Add(LockPath);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the checkpoint probe.");
        try
        {
            Assert.Equal("before-replacement", await child.StandardOutput.ReadLineAsync(timeout.Token));
            Assert.ThrowsAny<IOException>(() => Read());
            if (dieAfterReplacement)
            {
                await child.StandardInput.WriteLineAsync("continue");
                Assert.Equal("after-replacement", await child.StandardOutput.ReadLineAsync(timeout.Token));
                Assert.ThrowsAny<IOException>(() => Read());
            }
            Assert.False(child.HasExited);
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        var state = Read();
        if (!dieAfterReplacement)
        {
            Assert.Equal(HostEffectCheckpointState.Genesis, state);
            Assert.True(File.Exists(InstalledPath + ".new"));
            return;
        }

        Assert.Equal(HostCheckpointPhase.LaunchPrepared, state.Phase);
        Assert.Equal(1L, state.Revision);
        Assert.Equal("a.model", state.Transition!.Launch!.Target);
    }

    private static HostCheckpointPlan Plan(string engine) =>
        new(engine, $"{engine}.model", "named", $"{engine}-config", 1000, $"{engine}-route", new string('A', 64));

    private HostCheckpointLaunchEffect InstallA()
    {
        var launch = Change(checkpoint => checkpoint.PrepareInitialLaunch(PlanA));
        Assert.Equal(HostCheckpointPhase.LaunchPrepared, Read().Phase);
        Advance(checkpoint => checkpoint.MarkLaunchDispatching(launch));
        Assert.Equal(HostCheckpointPhase.LaunchDispatching, Read().Phase);
        var effect = new HostCheckpointLaunchEffect(launch, PlanA, "a-exact-id");
        Advance(checkpoint => checkpoint.RecordAppliedLaunch(Applied(launch, effect.InstanceId), effect));
        return effect;
    }

    private static HostMutationOutcome Applied(HostMutation operation, string id) => new(operation, HostMutationStatus.Applied, id);

    private HostCheckpointLaunchEffect CompleteHandoff(HostCheckpointLaunchEffect incumbent, HostMutation stop,
        HostCheckpointPlan successor)
    {
        var stopEffect = new HostCheckpointStopEffect(stop, incumbent.Operation, incumbent.InstanceId);
        Advance(checkpoint => checkpoint.MarkStopDispatching(stop));
        Advance(checkpoint => checkpoint.RecordAppliedStop(Applied(stop, incumbent.InstanceId), stopEffect));
        var launch = Change(checkpoint => checkpoint.PrepareSuccessorLaunch(successor));
        Advance(checkpoint => checkpoint.MarkLaunchDispatching(launch));
        var next = new HostCheckpointLaunchEffect(launch, successor, $"{successor.Engine}-exact-id");
        Advance(checkpoint => checkpoint.RecordAppliedLaunch(Applied(launch, next.InstanceId), next));
        return next;
    }

    private void WriteChecksummedState(HostEffectCheckpointState state)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(state, HostEffectCheckpointJsonContext.Default.HostEffectCheckpointState);
        using var file = File.Create(InstalledPath);
        file.Write(SHA256.HashData(body));
        file.Write(body);
        file.Flush(flushToDisk: true);
    }

    private HostEffectCheckpointState Read() => Change(checkpoint => checkpoint.State);

    private T Change<T>(Func<HostEffectCheckpoint, T> change)
    {
        using var checkpoint = HostEffectCheckpoint.Open(LockPath);
        return change(checkpoint);
    }

    private void Advance(Action<HostEffectCheckpoint> change)
    {
        using var checkpoint = HostEffectCheckpoint.Open(LockPath);
        change(checkpoint);
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
