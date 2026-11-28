using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using System.Text;
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
    IServingBrokerAnchor anchor, Func<IDisposable> enterBrokerFence, bool readOnly = false) : IServingBrokerStore
{
    internal const string RecordsName = "serving-broker.records";
    internal const string FenceName = "serving-broker.lock";
    private const int MaxPayload = 1024 * 1024;
    private const UnixFileMode SnapshotMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
    private readonly Lock _access = new();
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
            () => new FileStream(fencePath, FileMode.Open, access, FileShare.None), readOnly: access == FileAccess.Read);
    }

    public ServingBrokerSnapshot? Read()
    {
        lock (_access)
        {
            try
            {
                // The authenticated pointer and checked immutable frame suffice for read-only
                // peers. They do not contend with the single writer's append fence.
                using var fence = readOnly ? null : enterBrokerFence();
                return ReadAnchored();
            }
            catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
            {
                return null;
            }
        }
    }

    public bool TryAdvance(ServingBrokerSnapshot predecessor, ServingBrokerRecord next)
    {
        if (readOnly || next.FormatVersion != ServingBrokerRecord.CurrentFormat || next.Scope != scope ||
            predecessor.Record.Revision is < 0 or long.MaxValue ||
            next.Revision != predecessor.Record.Revision + 1 ||
            next.Predecessor != predecessor.Digest)
            return false;

        var payload = JsonSerializer.SerializeToUtf8Bytes(next, Json.ServingBrokerRecord);
        if (payload.Length > MaxPayload) return false;
        var prior = new ServingBrokerHead(scope, predecessor.Record.Revision, predecessor.Digest);
        var nextHead = new ServingBrokerHead(scope, next.Revision, Convert.ToHexString(SHA256.HashData(payload)));
        lock (_access)
        {
            try
            {
                using var fence = enterBrokerFence();
                if (!Matches(ReadAnchored(), prior)) return false;
                WriteAlternate(nextHead, payload);

                // Only the independent anchor selects the current slot. A refused CAS leaves
                // the preceding slot readable. A lost reply requires fresh exact readback.
                try { _ = anchor.TryAdvance(prior, nextHead); }
                catch (Exception error) when (OperationHeadTransport.IsUnavailable(error)) { }

                return Matches(ReadAnchored(), nextHead);
            }
            catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
            {
                return false;
            }
        }
    }

    private void WriteAlternate(ServingBrokerHead next, byte[] payload)
    {
        var path = SlotPath(next.Revision);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (OperatingSystem.IsLinux())
            options.UnixCreateMode = File.GetUnixFileMode(recordsPath) & SnapshotMode;
        using (var slot = new FileStream(path, options))
        {
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(slot.SafeFileHandle, options.UnixCreateMode!.Value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
            slot.Write(length);
            slot.Write(payload);
            slot.Flush(flushToDisk: true);
        }

        // First use of either slot installs a directory entry. Persist it before the head
        // can refer to it. The inactive slot can be overwritten in place without a rename.
        if (OperatingSystem.IsLinux()) FlushDirectory(Path.GetDirectoryName(path)!);
        if (ReadSlot(next) is null) throw new IOException("The broker snapshot could not be read before anchoring.");
    }

    private ServingBrokerSnapshot? ReadAnchored()
    {
        if (anchor.Read(scope) is not { Revision: >= 0 } current ||
            current.Scope != scope || !OperationHeadProtocol.IsCanonicalDigest(current.Digest))
            return null;

        var snapshot = ReadSlot(current) ?? ReadLegacyPrefix(current);
        return snapshot is not null && anchor.Read(scope) == current ? snapshot : null;
    }

    private string SlotPath(long revision) => recordsPath + (revision % 2 == 0 ? ".0" : ".1");

    private ServingBrokerSnapshot? ReadSlot(ServingBrokerHead head)
    {
        try
        {
            using var slot = new FileStream(SlotPath(head.Revision), FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = ReadFrame(slot, head.Revision);
            return Matches(read, head) && slot.Position == slot.Length ? read : null;
        }
        catch (FileNotFoundException) { return null; }
    }

    // Existing commissioning installs a framed genesis or append-only predecessor journal.
    // Unanchored suffix bytes have no authority. Leave them untouched as evidence.
    private ServingBrokerSnapshot? ReadLegacyPrefix(ServingBrokerHead head)
    {
        using var records = new FileStream(recordsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (head.Revision >= records.Length / (sizeof(int) + 1)) return null;

        ServingBrokerSnapshot? latest = null;
        for (long revision = 0; revision <= head.Revision; revision++)
        {
            var current = ReadFrame(records, revision);
            if (current is null || current.Record.Predecessor != latest?.Digest) return null;
            latest = current;
        }
        return Matches(latest, head) ? latest : null;
    }

    private ServingBrokerSnapshot? ReadFrame(FileStream source, long revision)
    {
        Span<byte> header = stackalloc byte[sizeof(int)];
        if (source.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length) return null;
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is <= 0 or > MaxPayload || size > source.Length - source.Position) return null;
        var payload = new byte[size];
        source.ReadExactly(payload);
        var record = JsonSerializer.Deserialize(payload, Json.ServingBrokerRecord);
        return record is not null && record.FormatVersion == ServingBrokerRecord.CurrentFormat &&
            record.Scope == scope && record.Revision == revision
            ? new(record, Convert.ToHexString(SHA256.HashData(payload))) : null;
    }

    private static bool Matches(ServingBrokerSnapshot? snapshot, ServingBrokerHead head) =>
        snapshot?.Record.Revision == head.Revision && snapshot.Digest == head.Digest;

    private static void FlushDirectory(string directory)
    {
        const int directoryOnlyCloseOnExec = 0x90000;
        using var descriptor = OpenDirectory(Encoding.UTF8.GetBytes(directory + "\0"), directoryOnlyCloseOnExec);
        if (descriptor.IsInvalid || Sync(descriptor) != 0)
            throw new IOException("The broker snapshot directory could not be synchronized.");
    }

    // The fixed Linux ABI uses only the UTF-8 path and a descriptor.
#pragma warning disable SYSLIB1054
    [DllImport("libc.so.6", EntryPoint = "open", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern SafeFileHandle OpenDirectory([In] byte[] path, int flags);

    [DllImport("libc.so.6", EntryPoint = "fsync", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Sync(SafeFileHandle descriptor);
#pragma warning restore SYSLIB1054
}
