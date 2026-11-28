using System.Security.Cryptography;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

// The paths come from trusted v2 bootstrap. The floor is protected independently of the
// records and AIR. This reader does not provision, migrate, or advance either file.
internal sealed class CatalogAuthorityStore(CatalogAuthorityScope scope, string recordsPath, string floorPath)
{
    internal const int MaxRecord = 512 * 1024;
    internal const int MaxFrames = 128;
    internal const int MaxChain = 8 * 1024 * 1024;
    internal const int MaxFloor = 4096;
    private static readonly string SizeCheckNonce = new('0', CatalogAuthorityV2.NonceBytes * 2);

    internal Func<CancellationToken, Task>? AfterRecordsReadForTests { private get; init; }

    internal async ValueTask<CatalogAuthoritySnapshot?> ReadAsync(CancellationToken ct)
    {
        if (!scope.IsValid()) return null;
        try
        {
            var floor = await ReadFloorAsync(ct).ConfigureAwait(false);
            if (floor is not { Ordinal: >= 0 and < MaxFrames } ||
                floor.Operation != CatalogAuthorityV2.ReadTopology || floor.Version != CatalogAuthorityV2.TopologyVersion ||
                floor.Scope != scope || !HostMutation.IsPlanDigest(floor.Digest))
                return null;

            await using var records = OpenRead(recordsPath);
            if (await ReadRecordsAsync(records, floor, ct).ConfigureAwait(false) is null) return null;

            if (AfterRecordsReadForTests is { } afterRead)
                await afterRead(ct).ConfigureAwait(false);

            // The reopened live pathname is the records snapshot point. Its raw installed
            // bytes must still match the same independently held floor.
            await using var currentRecords = OpenRead(recordsPath);
            var current = await ReadRecordsAsync(currentRecords, floor, ct).ConfigureAwait(false);
            if (current is null) return null;

            await CatalogAuthorityV2.WriteReplyAsync(Stream.Null, new(CatalogAuthorityV2.ReadTopology,
                CatalogAuthorityV2.TopologyVersion, scope, SizeCheckNonce, current), ct).ConfigureAwait(false);

            var finalFloor = await ReadFloorAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return finalFloor == floor ? current : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private async ValueTask<CatalogAuthoritySnapshot?> ReadRecordsAsync(FileStream stream, CatalogAuthorityFloor floor,
        CancellationToken ct)
    {
        if (stream.Length > MaxChain) return null;
        string? digest = null;
        CatalogAuthoritySnapshot? previous = null;
        for (var ordinal = 0; ordinal <= floor.Ordinal; ordinal++)
        {
            var bytes = await BoundedJsonFrame.ReadPayloadAsync(stream, MaxRecord, ct).ConfigureAwait(false);
            if (stream.Position > MaxChain) return null;
            var frame = JsonSerializer.Deserialize(bytes, CatalogAuthorityJsonContext.Default.CatalogAuthorityFrame);
            if (frame?.Snapshot is not { } snapshot || frame.Operation != CatalogAuthorityV2.ReadTopology ||
                frame.Version != CatalogAuthorityV2.TopologyVersion || frame.Scope != scope || frame.Ordinal != ordinal ||
                frame.Predecessor != digest || !snapshot.IsEvidenceFor(scope) ||
                (previous is not null && !snapshot.Extends(previous)))
                return null;

            digest = Convert.ToHexString(SHA256.HashData(bytes));
            previous = snapshot;
        }

        return stream.Position == stream.Length && digest == floor.Digest ? previous : null;
    }

    private async ValueTask<CatalogAuthorityFloor?> ReadFloorAsync(CancellationToken ct)
    {
        await using var stream = OpenRead(floorPath);
        var floor = await BoundedJsonFrame.ReadAsync(stream, CatalogAuthorityJsonContext.Default.CatalogAuthorityFloor,
            MaxFloor, ct).ConfigureAwait(false);
        return stream.Position == stream.Length ? floor : null;
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
}
