using System.Security.Cryptography;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

/// <summary>
/// Reads a bounded commissioned record chain and a separately held committed floor. Both paths
/// are trusted bootstrap inputs. The committed floor must be protected from rollback of AIR and
/// the record path. Writers preserve published frame bytes and can stage an append or replace the path.
/// </summary>
internal sealed class HeadReadStore(HeadReadScope scope, string recordsPath, string floorPath)
{
    internal const int RecordPayloadLimit = 4096;
    internal const int TopologyRecordPayloadLimit = HeadReadV1.MaxReply;
    private const int MaxFrames = 128;
    private const int JournalChainByteLimit = MaxFrames * (RecordPayloadLimit + sizeof(int));
    private const int TopologyChainByteLimit = 8 * 1024 * 1024;
    private static readonly string ReplySizeNonce = new('0', HeadReadV1.NonceBytes * 2);

    internal Func<CancellationToken, Task>? AfterRecordsReadForTests { private get; init; }

    internal async ValueTask<HeadReadValue> ReadAsync(HeadReadScope requested, CancellationToken ct)
    {
        if (requested != scope || !scope.IsValid()) return HeadReadValue.Unavailable;
        try
        {
            var floor = await ReadFloorAsync(ct).ConfigureAwait(false);
            if (floor is null || floor.Scope != scope || floor.Revision is < 0 or >= MaxFrames ||
                !HostMutation.IsPlanDigest(floor.Digest)) return HeadReadValue.Unavailable;

            await using var stream = OpenRead(recordsPath);
            if (await ReadRecordsAsync(stream, floor, ct).ConfigureAwait(false) is null)
                return HeadReadValue.Unavailable;

            if (AfterRecordsReadForTests is { } afterRecordsRead)
                await afterRecordsRead(ct).ConfigureAwait(false);

            // The final pathname open is the snapshot point. An unchanged independently held
            // floor covers that open, and validating its immutable published bytes avoids using
            // the old inode after a rename. Even a valid staged tail is a split with this floor.
            await using var currentStream = OpenRead(recordsPath);
            var current = await ReadRecordsAsync(currentStream, floor, ct).ConfigureAwait(false);
            if (current is null) return HeadReadValue.Unavailable;

            // Measure the encoded reply with the same caps and fixed-width nonce as the endpoint.
            // A raw record can fit on disk while its normalized reply or larger envelope cannot.
            if (scope.Kind == HeadReadKind.Topology)
                await HeadReadV1.WriteReplyAsync(Stream.Null,
                    new(HeadReadV1.Operation, HeadReadV1.Version, scope, ReplySizeNonce, current), ct).ConfigureAwait(false);

            if (await ReadFloorAsync(ct).ConfigureAwait(false) != floor) return HeadReadValue.Unavailable;
            ct.ThrowIfCancellationRequested();
            return current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return HeadReadValue.Unavailable;
        }
    }

    private async ValueTask<HeadReadValue?> ReadRecordsAsync(FileStream stream, HeadReadFloor floor, CancellationToken ct)
    {
        var chainLimit = scope.Kind == HeadReadKind.Topology ? TopologyChainByteLimit : JournalChainByteLimit;
        if (stream.Length > chainLimit) return null;
        var payloadLimit = scope.Kind == HeadReadKind.Topology ? TopologyRecordPayloadLimit : RecordPayloadLimit;
        string? previousDigest = null;
        HeadReadValue? previous = null;

        for (var revision = 0; revision <= floor.Revision; revision++)
        {
            var payload = await BoundedJsonFrame.ReadPayloadAsync(stream, payloadLimit, ct).ConfigureAwait(false);
            var frame = JsonSerializer.Deserialize(payload, HeadReadJsonContext.Default.HeadReadFrame);
            if (frame?.Value is not { } value || frame.Scope != scope || frame.Revision != revision ||
                frame.Predecessor != previousDigest || !value.IsEvidenceFor(scope) ||
                (previous is not null && !value.Extends(previous))) return null;

            previousDigest = Convert.ToHexString(SHA256.HashData(payload));
            previous = value;
        }

        return stream.Position == stream.Length && previousDigest == floor.Digest ? previous : null;
    }

    private async ValueTask<HeadReadFloor?> ReadFloorAsync(CancellationToken ct)
    {
        await using var stream = OpenRead(floorPath);
        var floor = await BoundedJsonFrame.ReadAsync(stream, HeadReadJsonContext.Default.HeadReadFloor, RecordPayloadLimit, ct)
            .ConfigureAwait(false);
        return stream.Position == stream.Length ? floor : null;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
