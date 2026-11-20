using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record SupervisedLaunchJournalHead(HostReservationScope Scope, int Revision, string Digest);

// The head authority belongs outside the journal's restore boundary. The producer cannot reset it.
internal interface ISupervisedLaunchHead
{
    SupervisedLaunchJournalHead? Read(HostReservationScope scope);
    bool TryAdvance(SupervisedLaunchJournalHead? predecessor, SupervisedLaunchJournalHead next);
}

internal sealed record SupervisedLaunchFrame(string? Predecessor, [property: JsonRequired] HostInitialHistory History,
    SupervisedLaunchCommission? Commission,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int FormatVersion = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SupervisedStartIssue? StartIssue = null);

internal sealed record SupervisedLaunchSnapshot(SupervisedLaunchJournalHead Head, SupervisedLaunchFrame Frame);

[JsonSerializable(typeof(SupervisedLaunchFrame))]
internal sealed partial class SupervisedLaunchJsonContext : JsonSerializerContext;

internal sealed class SupervisedLaunchJournal(HostReservationScope scope, ISupervisedLaunchHead head)
{
    internal const int CurrentFormat = 1;
    private const int LastRevision = 5;
    private const int MaxFrameLength = 512 * 1024;
    private string FencePath => Path.Combine(scope.AuthorityDirectory, "supervised-launch.lock");
    internal string JournalPath => Path.Combine(scope.AuthorityDirectory, "supervised-launch.journal");

    internal void Provision()
    {
        var installed = HostReservation.ReadEnrolled(scope);
        if (installed.Record.Revision != 0 || installed.Record.State.Epoch != 0 || head.Read(scope) is not null)
            throw new IOException("Only a newly commissioned launch slot can provision a journal.");

        Directory.CreateDirectory(scope.AuthorityDirectory);
        using var fence = new FileStream(FencePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using (var journal = new FileStream(JournalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        Append(null, new(null, new HostInitialHistory(scope, installed.Digest), null, CurrentFormat));
    }

    internal IDisposable EnterFence() => new FileStream(FencePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    // Caller holds the stable issue fence. Read exactly the authenticated chain, including its EOF.
    internal SupervisedLaunchSnapshot? Read()
    {
        if (head.Read(scope) is not { } current || current.Scope != scope ||
            current.Revision is < 0 or > LastRevision || !HostMutation.IsPlanDigest(current.Digest)) return null;

        using var journal = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string? digest = null;
        SupervisedLaunchFrame? previous = null;
        Span<byte> length = stackalloc byte[sizeof(int)];
        for (var revision = 0; revision <= current.Revision; revision++)
        {
            journal.ReadExactly(length);
            var size = BinaryPrimitives.ReadInt32LittleEndian(length);
            if (size is <= 0 or > MaxFrameLength) return null;
            var bytes = new byte[size];
            journal.ReadExactly(bytes);
            var frame = JsonSerializer.Deserialize(bytes, SupervisedLaunchJsonContext.Default.SupervisedLaunchFrame);
            if (frame?.History is not { } history || history.Scope != scope || frame.Predecessor != digest ||
                !Extends(previous, frame, revision)) return null;
            digest = Convert.ToHexString(SHA256.HashData(bytes));
            previous = frame;
        }

        // An unanchored tail is quarantined even if all of its bytes parse. A reopen cannot
        // distinguish a refused advance from an uncertain install and never trims the file.
        return journal.Position == journal.Length && digest == current.Digest && head.Read(scope) == current
            ? new(current, previous!) : null;
    }

    internal void Advance(SupervisedLaunchSnapshot previous, HostInitialHistory history,
        SupervisedLaunchCommission commission, SupervisedStartIssue? startIssue = null)
    {
        if (previous.Frame.FormatVersion != CurrentFormat)
            throw new IOException("The legacy supervised launch journal is read only.");
        Append(previous, new(previous.Head.Digest, history, commission, CurrentFormat,
            startIssue ?? previous.Frame.StartIssue));
    }

    private void Append(SupervisedLaunchSnapshot? previous, SupervisedLaunchFrame next)
    {
        var revision = (previous?.Head.Revision ?? -1) + 1;
        if (head.Read(scope) != previous?.Head || !Extends(previous?.Frame, next, revision))
            throw new IOException("The supervised launch journal predecessor changed.");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, SupervisedLaunchJsonContext.Default.SupervisedLaunchFrame);
        if (bytes.Length > MaxFrameLength) throw new IOException("The supervised launch journal frame is oversized.");
        using (var journal = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            journal.Position = journal.Length;
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            journal.Write(length);
            journal.Write(bytes);
            journal.Flush(flushToDisk: true);
        }

        var placed = new SupervisedLaunchJournalHead(scope, revision, Convert.ToHexString(SHA256.HashData(bytes)));
        // Preserve the flushed tail if the head refuses. Retrying or truncating here would hide
        // the split from subsequent holders of the slot.
        if (!head.TryAdvance(previous?.Head, placed) || Read()?.Head != placed)
            throw new IOException("The supervised launch journal did not reach its authenticated head.");
    }

    private static bool Extends(SupervisedLaunchFrame? previous, SupervisedLaunchFrame frame, int revision)
    {
        var history = frame.History;
        var legacy = frame.FormatVersion == 0;
        if (revision == 0)
            return (legacy || frame.FormatVersion == CurrentFormat) &&
                frame.Commission is null && frame.StartIssue is null && HostMutation.IsPlanDigest(history.GenesisDigest) &&
                history == new HostInitialHistory(history.Scope, history.GenesisDigest);

        if (previous is null || previous.FormatVersion != frame.FormatVersion ||
            frame.Commission is not { } commission || commission.Scope != history.Scope) return false;
        var prior = previous.History;
        if (revision == 1)
            return frame.StartIssue is null && !commission.Approvals.IsDefaultOrEmpty &&
                commission.Approvals.All(approval => legacy ? approval.ApiVersion is null : approval.HasPinnedApiVersion()) &&
                history.Dispatch is { } first && first.Policy.Authorizes(history.Scope) &&
                commission.Approval(first.Operation.Engine)?.Intent == first.Intent &&
                history == (prior with { Dispatch = first });

        if (!commission.Matches(previous.Commission) || prior.Dispatch is not { } dispatch) return false;
        return revision switch
        {
            2 => frame.StartIssue is null && history == (prior with { Accepted = dispatch }),
            3 => frame.StartIssue is null && history == (prior with { Issued = dispatch }),
            4 when legacy => frame.StartIssue is null && history.Applied is { } applied &&
                history == (prior with { Applied = applied }),
            4 => frame.StartIssue is { } issue && issue.Matches(commission, dispatch) && history == prior,
            5 when !legacy => previous.StartIssue is { } issued && frame.StartIssue == issued &&
                history.Applied is { } completed && history == (prior with { Applied = completed }) &&
                MatchesApplied(issued, dispatch, completed),
            _ => false,
        };
    }

    private static bool MatchesApplied(SupervisedStartIssue issue, HostInitialDispatch dispatch, HostInitialApplied applied) =>
        applied.Receipt == new HostInitialReceipt(issue.Command.Scope, dispatch.Operation, dispatch.Intent, issue.InstanceId) &&
        JsonSerializer.Deserialize(Convert.FromBase64String(applied.ReceiptBytes),
            HostReservationJsonContext.Default.HostInitialReceipt) == applied.Receipt;
}
