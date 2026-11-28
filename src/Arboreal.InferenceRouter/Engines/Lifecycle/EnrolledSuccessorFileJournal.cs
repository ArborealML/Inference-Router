using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SuccessorJournalFrame([property: JsonRequired] string Predecessor,
    [property: JsonRequired] SuccessorIssueRecord Record);

[JsonSerializable(typeof(SuccessorJournalFrame))]
internal sealed partial class SupervisedLaunchJsonContext;

// The initial file stays byte-for-byte intact. Its verified revision 5 is the predecessor of
// the first successor frame; both files use the same commissioned head and stable writer fence.
internal sealed class EnrolledSuccessorFileJournal(HostReservationScope scope, EnrolledPairHead commission,
    ISupervisedLaunchHead head) : IEnrolledSuccessorOperationJournal
{
    private const int LastInitialRevision = SupervisedLaunchJournal.LastRevision;
    private const int MaxFrameLength = 512 * 1024;
    private readonly SupervisedLaunchJournal _initial = new(scope, head);

    internal string TailPath => Path.Combine(scope.AuthorityDirectory, "supervised-launch.successors");

    public IEnrolledSuccessorIssueFence? TryEnter(HostReservationScope requestedScope)
    {
        if (requestedScope != scope || scope.Enrollment is not { IsValid: true } enrollment ||
            scope.Identity == Guid.Empty || enrollment.Group != commission.Partition ||
            enrollment.Generation != commission.Generation) return null;

        return new Fence(this);
    }

    // Read-only verification for the commissioned daemon ingress, while the queued writer
    // holds the distinct issue fence. A concurrent write or split tail refuses the read.
    internal SuccessorIssueHead? ReadIssuedForIngress()
    {
        try
        {
            var committed = ReadHead();
            return committed is { Current.Phase: SuccessorIssuePhase.MayHaveIssued or SuccessorIssuePhase.StartAnchored }
                ? committed : null;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error)) { return null; }
    }

    internal bool MatchesInitialResidentForServing(HostCheckpointLaunchEffect launch)
    {
        try
        {
            return ReadHead() is { Revision: 0, Current: null } initial && initial.InitialResident == launch;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error)) { return false; }
    }

    // Read-only observer. The controller may still hold its distinct checkpoint lock.
    // ReadHead authenticates the complete journal and checks the installed head twice.
    internal string? ReadAppliedDigestForServing(HostCheckpointLaunchEffect launch)
    {
        try
        {
            return ReadHead() is { Current: { Phase: SuccessorIssuePhase.Applied } applied } current &&
                applied.Binding.Command.Operation == launch.Operation && applied.Binding.Successor.Plan == launch.Plan &&
                applied.Start == new SupervisedStartIssue(applied.Binding.Command, launch.InstanceId,
                    applied.Binding.Successor.CreateDigest) &&
                applied.Outcome == new HostMutationOutcome(launch.Operation, HostMutationStatus.Applied, launch.InstanceId)
                ? current.Digest : null;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error)) { return null; }
    }

    private SuccessorIssueHead? ReadHead()
    {
        if (head.Read(scope) is not { } committed || committed.Scope != scope ||
            committed.Revision < LastInitialRevision || !HostMutation.IsPlanDigest(committed.Digest) ||
            _initial.ReadInitialTerminalPrefix() is not
                { Frame: { FormatVersion: SupervisedLaunchJournal.CurrentFormat, History.Applied.Receipt: var receipt } } baseline ||
            receipt.Scope != scope || !SupervisedLaunchEvidence.IsFullContainerId(receipt.InstanceId)) return null;

        var resident = new HostCheckpointLaunchEffect(receipt.Operation, receipt.Intent.Plan, receipt.InstanceId);
        if (!resident.IsValid() || HostReservationScope.Read(scope.Slot) != scope ||
            HostReservation.ReadEnrolled(scope).Record.State is not { Phase: HostMutationPhase.LaunchApplied } installed ||
            installed.Pending != receipt.Operation || installed.LaunchedContainerId != receipt.InstanceId) return null;
        var verified = new SuccessorIssueHead(scope, commission, 0, baseline.Head.Digest, resident);
        var count = committed.Revision - LastInitialRevision;

        using var stream = new FileStream(TailPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < (long)count * (sizeof(int) + 1)) return null;
        Span<byte> length = stackalloc byte[sizeof(int)];
        for (var index = 0; index < count; index++)
        {
            stream.ReadExactly(length);
            var size = BinaryPrimitives.ReadInt32LittleEndian(length);
            if (size is <= 0 or > MaxFrameLength || size > stream.Length - stream.Position) return null;
            var bytes = new byte[size];
            stream.ReadExactly(bytes);
            var frame = JsonSerializer.Deserialize(bytes, SupervisedLaunchJsonContext.Default.SuccessorJournalFrame);
            if (frame is not { Record: { } record } || frame.Predecessor != verified.Digest ||
                !Extends(verified, record)) return null;
            verified = verified with { Revision = verified.Revision + 1, Digest = Digest(bytes), Current = record };
        }

        // An older valid prefix or a flushed but not committed tail still refuses. Never truncate it.
        return stream.Position == stream.Length && verified.Digest == committed.Digest && head.Read(scope) == committed
            ? verified : null;
    }

    private bool TryAppend(SuccessorIssueHead previous, SuccessorIssueRecord record)
    {
        if (previous.Revision is < 0 or >= int.MaxValue - LastInitialRevision || ReadHead() != previous || !Extends(previous, record))
            return false;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SuccessorJournalFrame(previous.Digest, record),
            SupervisedLaunchJsonContext.Default.SuccessorJournalFrame);
        if (bytes.Length > MaxFrameLength) return false;
        var prior = new SupervisedLaunchJournalHead(scope, checked((int)previous.Revision + LastInitialRevision), previous.Digest);
        if (head.Read(scope) != prior) return false;

        using (var stream = new FileStream(TailPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = stream.Length;
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        var next = new SupervisedLaunchJournalHead(scope, prior.Revision + 1, Digest(bytes));
        return head.TryAdvance(prior, next) && ReadHead() == (previous with
        {
            Revision = previous.Revision + 1, Digest = next.Digest, Current = record,
        });
    }

    private static bool Extends(SuccessorIssueHead previous, SuccessorIssueRecord next)
    {
        if (next.Binding is not { Command: { Operation: { } operation } command, Checkpoint.State: not null,
                Retirement.Incumbent: not null, Successor: { Plan: not null } } binding ||
            command.Scope != previous.Scope || command.Head != previous.Commission ||
            !SupervisedLaunchApproval.IsPinnedApiVersion(command.ApiVersion) ||
            !HostMutation.IsPlanDigest(binding.Checkpoint.Digest) || !binding.Checkpoint.State.IsValid() ||
            binding.Checkpoint.State.Transition is not { } transition || transition.Plan != binding.Successor.Plan ||
            binding.Checkpoint.State.AppliedLaunch != binding.Retirement.Incumbent || transition.Stop != binding.Retirement.Stop ||
            !EnrolledSuccessorScope.BindsResidentRoute(previous.Scope, binding.Retirement.SealedRoute, binding.Retirement.Incumbent) ||
            binding.Attempt.RuntimeEpoch == Guid.Empty || binding.Attempt.Sequence <= 0 || binding.QueryId == Guid.Empty ||
            binding.CoverageGeneration <= 0 || !binding.Successor.IsValid()) return false;

        var isStop = operation.Kind == HostMutationKind.Stop;
        if (isStop ? binding.Checkpoint.State.Phase != HostCheckpointPhase.StopDispatching || transition.Stop != operation
            : binding.Checkpoint.State.Phase != HostCheckpointPhase.LaunchDispatching || transition.Launch != operation)
            return false;

        if (next.Phase == SuccessorIssuePhase.Accepted)
            return next is { Start: null, Outcome: null } && EnrolledDockerSuccessorWorker.CanAccept(previous, binding);

        if (previous.Current is not { } prior || prior.Binding != binding) return false;
        return next.Phase switch
        {
            SuccessorIssuePhase.MayHaveIssued => prior.Phase == SuccessorIssuePhase.Accepted &&
                next == (prior with { Phase = SuccessorIssuePhase.MayHaveIssued }),
            SuccessorIssuePhase.StartAnchored => !isStop && prior.Phase == SuccessorIssuePhase.MayHaveIssued &&
                next is { Start: { } start, Outcome: null } && start.Command == command &&
                SupervisedLaunchEvidence.IsFullContainerId(start.InstanceId) && start.CreateDigest == binding.Successor.CreateDigest,
            SuccessorIssuePhase.Applied =>
                prior.Phase == (isStop ? SuccessorIssuePhase.MayHaveIssued : SuccessorIssuePhase.StartAnchored) &&
                next == (prior with
                {
                    Phase = SuccessorIssuePhase.Applied,
                    Outcome = new HostMutationOutcome(operation, HostMutationStatus.Applied,
                        isStop ? binding.Retirement.Incumbent.InstanceId : prior.Start!.InstanceId),
                }),
            _ => false,
        };
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class Fence(EnrolledSuccessorFileJournal journal) : IEnrolledSuccessorIssueFence
    {
        private readonly IDisposable _handle = journal._initial.EnterFence();
        private bool _disposed;

        public SuccessorIssueHead? ReadHead() => _disposed ? null : journal.ReadHead();

        public bool TryAppend(SuccessorIssueHead predecessor, SuccessorIssueRecord next) =>
            !_disposed && journal.TryAppend(predecessor, next);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
        }
    }
}
