using System.Security.Cryptography;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessorFileJournalExtendsTheSameAnchoredInitialReceipt(bool refuseIssueAdvance)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var fx = ProvisionSupervisedLaunch(DeepSeekEngineName);
        var receipt = Assert.IsType<HostAppliedIngressClosed>(await fx.Open().RunAsync(DeepSeekEngineName, fx.Text,
            fx.Claim, lifetime.Token)).Receipt;
        var initialBytes = File.ReadAllBytes(fx.Journal.JournalPath);
        var initialHead = Assert.IsType<SupervisedLaunchJournalHead>(fx.Head.Current);
        var commission = fx.Observer.Commission;
        var journal = new EnrolledSuccessorFileJournal(fx.Scope, commission.Topology.Head, fx.Head);
        var checkpointPath = Path.Combine(fx.Scope.AuthorityDirectory, "successor-checkpoint.lock");
        HostEffectCheckpoint.ProvisionNew(checkpointPath);

        Assert.False(File.Exists(journal.TailPath));
        Assert.Equal(HostEffectCheckpointState.Genesis, HostEffectCheckpoint.ReadInstalledState(checkpointPath));
        using (new FileStream(journal.TailPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

        var resident = new HostCheckpointLaunchEffect(receipt.Operation, receipt.Intent.Plan, receipt.InstanceId);
        var installedReceipt = new HostEffectCheckpointState(HostEffectCheckpointState.FormatVersion, 1,
            receipt.Operation.Epoch, HostCheckpointPhase.Resident, resident);
        var body = JsonSerializer.SerializeToUtf8Bytes(installedReceipt, HostEffectCheckpointJsonContext.Default.HostEffectCheckpointState);
        File.WriteAllBytes(checkpointPath + ".checkpoint", [.. SHA256.HashData(body), .. body]);
        using var checkpoint = HostEffectCheckpoint.Open(checkpointPath);
        Assert.Equal(resident, checkpoint.State.AppliedLaunch);

        var next = Assert.Single(commission.Approvals, approval => approval.Intent.Plan != receipt.Intent.Plan).Intent;
        var installedResident = HostEffectCheckpoint.ReadInstalledCommitment(checkpointPath);
        var stop = checkpoint.ProposeSuccessorStop(next.Plan);
        Assert.Equal(installedResident, HostEffectCheckpoint.ReadInstalledCommitment(checkpointPath));
        checkpoint.PrepareSuccessor(next.Plan, stop);
        checkpoint.MarkStopDispatching(stop);
        var installed = HostEffectCheckpoint.ReadInstalledCommitment(checkpointPath);
        var command = new SupervisedLaunchCommand(fx.Scope, commission.Topology.Head,
            commission.Grant(stop.Engine).SocketPath, stop, SupervisedApiVersion);
        var enrollment = Assert.IsType<HostEnrollmentScope>(fx.Scope.Enrollment);
        var sealedRoute = new VerifiedInstanceRoute(enrollment.Group, enrollment.Generation, resident.Plan.Engine,
            resident.Operation.PlanDigest!, resident.InstanceId, "fixture-process", resident.Operation.Epoch, 1);
        var query = Guid.NewGuid();
        var binding = new SuccessorIssueBinding(command, new(Guid.NewGuid(), 1), installed,
            new(query, Guid.NewGuid(), resident, stop, 1, 2, sealedRoute,
                new(next.Plan.Engine, next.Plan.Model, next.Plan.Tier!), next.Plan.Digest(), next.CreateDigest),
            next, query, 1);
        var acceptance = new SuccessorIssueRecord(binding, SuccessorIssuePhase.Accepted);
        var issue = acceptance with { Phase = SuccessorIssuePhase.MayHaveIssued };
        long acceptedLength;
        SupervisedLaunchJournalHead acceptedHead;
        using (var fence = journal.TryEnter(fx.Scope))
        {
            Assert.NotNull(fence);
            var genesis = Assert.IsType<SuccessorIssueHead>(fence.ReadHead());
            Assert.Equal(new SuccessorIssueHead(fx.Scope, commission.Topology.Head, 0, initialHead.Digest, resident), genesis);
            var wrongRecipe = acceptance with { Binding = binding with
            {
                Retirement = binding.Retirement with { CreateDigest = new string('B', 64) },
            }};
            Assert.False(fence.TryAppend(genesis, wrongRecipe));
            Assert.True(fence.TryAppend(genesis, acceptance));
            var accepted = Assert.IsType<SuccessorIssueHead>(fence.ReadHead());
            Assert.Equal(1, accepted.Revision);
            Assert.Equal(acceptance, accepted.Current);
            acceptedLength = new FileInfo(journal.TailPath).Length;
            acceptedHead = Assert.IsType<SupervisedLaunchJournalHead>(fx.Head.Current);
            Assert.Equal(initialHead.Revision + 1, acceptedHead.Revision);
            fx.Head.RefuseRevision = refuseIssueAdvance ? acceptedHead.Revision + 1 : null;
            Assert.Equal(!refuseIssueAdvance, fence.TryAppend(accepted, issue));
        }

        var reopened = new EnrolledSuccessorFileJournal(fx.Scope, commission.Topology.Head, fx.Head);
        using var readback = reopened.TryEnter(fx.Scope);
        Assert.NotNull(readback);
        var tailLength = new FileInfo(journal.TailPath).Length;
        Assert.True(tailLength > acceptedLength);
        Assert.Equal(initialBytes, File.ReadAllBytes(fx.Journal.JournalPath));
        Assert.Equal(installed, HostEffectCheckpoint.ReadInstalledCommitment(checkpointPath));
        if (refuseIssueAdvance)
        {
            fx.Head.RefuseRevision = null;
            Assert.Equal(acceptedHead, fx.Head.Current);
            Assert.Null(readback.ReadHead());
        }
        else
        {
            var prior = Assert.IsType<SuccessorIssueHead>(readback.ReadHead());
            Assert.Equal(2, prior.Revision);
            Assert.Equal(issue, prior.Current);
            Assert.False(readback.TryAppend(prior, acceptance));
        }
        Assert.Equal(tailLength, new FileInfo(journal.TailPath).Length);
        Assert.Equal(1, fx.Port.Creates);
        Assert.Equal(1, fx.Port.Starts);
    }
}
