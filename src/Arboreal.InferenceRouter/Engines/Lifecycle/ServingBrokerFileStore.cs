using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record ServingBrokerHead(HostReservationScope Scope, long Revision, string Digest);

// The serving record has its own CAS key in the commissioned authority. Its digest is not
// interchangeable with the installed digest of the successor operation journal.
internal interface IServingBrokerAnchor
{
    ServingBrokerHead? Read(HostReservationScope scope);
    bool TryAdvance(ServingBrokerHead predecessor, ServingBrokerHead next);
}

internal sealed class ServingBrokerFileStore(HostReservationScope scope, string recordsPath,
    IServingBrokerAnchor anchor, Func<IDisposable> enterBrokerFence) : IServingBrokerStore
{
    internal const string RecordsName = "serving-broker.records";
    internal const string FenceName = "serving-broker.lock";
    private const int MaxPayload = 1024 * 1024;
    private static readonly ServingBrokerRecordJsonContext Json = ServingBrokerRecordJsonContext.Default;

    internal static ServingBrokerFileStore OpenWriter(HostReservationScope scope, IServingBrokerAnchor anchor) =>
        Open(scope, anchor, FileAccess.ReadWrite);

    // The commissioned peer is read-only at the protected authority. This callable opens
    // the existing broker files with read access and cannot append or create an artifact.
    internal static Func<Guid, EnrolledSuccessorRetirement?> OpenRetirementReader(HostReservationScope scope,
        IServingBrokerAnchor anchor)
    {
        var reader = Open(scope, anchor, FileAccess.Read);
        return closeId => ServingBrokerLedger.ReadRetirement(scope, reader, closeId);
    }

    internal static Func<ServingBrokerSnapshot?> OpenSnapshotReader(HostReservationScope scope, IServingBrokerAnchor anchor)
    {
        var reader = Open(scope, anchor, FileAccess.Read);
        return reader.Read;
    }

    private static ServingBrokerFileStore Open(HostReservationScope scope, IServingBrokerAnchor anchor, FileAccess access)
    {
        if (!OperationHeadProtocol.IsScopeValid(scope))
            throw new ArgumentException("The broker store requires the commissioned reservation.", nameof(scope));

        var root = scope.AuthorityDirectory;
        var fencePath = Path.Combine(root, FenceName);
        return new(scope, Path.Combine(root, RecordsName), anchor,
            () => new FileStream(fencePath, FileMode.Open, access, FileShare.None));
    }

    public ServingBrokerSnapshot? Read()
    {
        try
        {
            using var fence = enterBrokerFence();
            return ReadUnderFence();
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            return null;
        }
    }

    public bool TryAdvance(ServingBrokerSnapshot predecessor, ServingBrokerRecord next)
    {
        using var fence = enterBrokerFence();
        if (next.FormatVersion != ServingBrokerRecord.CurrentFormat || next.Scope != scope ||
            predecessor.Record.Revision is < 0 or long.MaxValue ||
            next.Revision != predecessor.Record.Revision + 1 ||
            next.Predecessor != predecessor.Digest)
            return false;

        var payload = JsonSerializer.SerializeToUtf8Bytes(next, Json.ServingBrokerRecord);
        if (payload.Length > MaxPayload) return false;
        var prior = new ServingBrokerHead(scope, predecessor.Record.Revision, predecessor.Digest);
        var nextHead = new ServingBrokerHead(scope, next.Revision, Convert.ToHexString(SHA256.HashData(payload)));
        using (var records = new FileStream(recordsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Verify and append on the same open file under the commissioned broker fence.
            if (anchor.Read(scope) != prior || ReadRecords(records, prior) is null || anchor.Read(scope) != prior)
                return false;

            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
            records.Write(length);
            records.Write(payload);
            records.Flush(flushToDisk: true);
        }

        // An unacknowledged anchor advance leaves the flushed tail in place. Neither this
        // writer nor a restarted reader can use an older head against that file.
        using (var installed = new FileStream(recordsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            if (ReadRecords(installed, nextHead) is null) return false;

        return anchor.TryAdvance(prior, nextHead) &&
            ReadUnderFence() is { } committed && committed.Record.Revision == nextHead.Revision &&
            committed.Digest == nextHead.Digest;
    }

    private ServingBrokerSnapshot? ReadUnderFence()
    {
        if (anchor.Read(scope) is not { Revision: >= 0 } current ||
            current.Scope != scope || !OperationHeadProtocol.IsCanonicalDigest(current.Digest))
            return null;

        using var records = new FileStream(recordsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var snapshot = ReadRecords(records, current);
        return snapshot is not null && anchor.Read(scope) == current ? snapshot : null;
    }

    private ServingBrokerSnapshot? ReadRecords(FileStream records, ServingBrokerHead head)
    {
        // Every record has a length and nonempty payload. Reject an impossible head before scanning.
        if (head.Scope != scope || head.Revision < 0 || head.Revision >= records.Length / (sizeof(int) + 1))
            return null;

        Span<byte> header = stackalloc byte[sizeof(int)];
        string? digest = null;
        ServingBrokerRecord? latest = null;
        for (long revision = 0; revision <= head.Revision; revision++)
        {
            records.ReadExactly(header);
            var size = BinaryPrimitives.ReadInt32BigEndian(header);
            if (size is <= 0 or > MaxPayload) return null;
            var bytes = new byte[size];
            records.ReadExactly(bytes);
            var record = JsonSerializer.Deserialize(bytes, Json.ServingBrokerRecord);
            if (record is null || record.FormatVersion != ServingBrokerRecord.CurrentFormat ||
                record.Scope != scope || record.Revision != revision || record.Predecessor != digest)
                return null;

            latest = record;
            digest = Convert.ToHexString(SHA256.HashData(bytes));
        }

        return latest is not null && digest == head.Digest && records.Position == records.Length
            ? new(latest, head.Digest) : null;
    }
}
