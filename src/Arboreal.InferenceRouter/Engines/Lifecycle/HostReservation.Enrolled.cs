using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

// Commissioning data is a separate, trusted input to the slot. A restored reservation or journal
// cannot choose the catalog entry or the authority root. Host permissions are a deployment concern.
internal sealed record HostReservationScope([property: JsonRequired] string Slot, [property: JsonRequired] Guid Identity,
    [property: JsonRequired] HostEnrollmentScope? Enrollment)
{
    [JsonIgnore] public bool IsLegacy => Enrollment is null;
    [JsonIgnore] public string AuthorityDirectory => Path.Combine(CatalogDirectory(Slot), Identity.ToString("N"));

    public static HostReservationScope Commission(string path, HostEnrollmentScope? enrollment)
    {
        var slot = Path.GetFullPath(path);
        _ = Directory.CreateDirectory(CatalogDirectory(slot));
        var scope = new HostReservationScope(slot, Guid.NewGuid(), enrollment);
        using var file = new FileStream(CatalogEntry(slot), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, scope, HostReservationJsonContext.Default.HostReservationScope);
        file.Flush(flushToDisk: true);
        return scope;
    }

    public static HostReservationScope Read(string path)
    {
        var slot = Path.GetFullPath(path);
        using var file = new FileStream(CatalogEntry(slot), FileMode.Open, FileAccess.Read, FileShare.Read);
        HostReservationScope? scope;
        try { scope = JsonSerializer.Deserialize(file, HostReservationJsonContext.Default.HostReservationScope); }
        catch (JsonException error) { throw new IOException("The commissioned reservation scope is unreadable.", error); }
        if (scope is null || scope.Slot != slot || scope.Identity == Guid.Empty || scope.Enrollment?.IsValid == false)
            throw new IOException("The commissioned reservation scope is invalid.");
        return scope;
    }

    private static string CatalogDirectory(string slot) => Path.Combine(Path.GetDirectoryName(slot)!, "reservation-authority");
    private static string CatalogEntry(string slot) => Path.Combine(CatalogDirectory(slot), Path.GetFileName(slot) + ".scope");
}

internal sealed record HostEnrollmentScope([property: JsonRequired] string Group, [property: JsonRequired] long Generation)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Group) && Generation > 0;
}

internal sealed record HostEnrolledRecord([property: JsonRequired] HostReservationScope Scope, [property: JsonRequired] long Revision,
    [property: JsonRequired] string? Predecessor, [property: JsonRequired] HostReservationState State);
internal sealed record HostEnrolledSnapshot(HostEnrolledRecord Record, string Digest);

[JsonSerializable(typeof(HostReservationScope))]
[JsonSerializable(typeof(HostEnrolledRecord))]
internal sealed partial class HostReservationJsonContext;

internal sealed partial class HostReservation
{
    public HostEnrolledSnapshot? Enrolled { get; private set; }

    public static HostReservationScope ProvisionEnrolled(string path, HostEnrollmentScope enrollment)
    {
        if (File.Exists(path)) throw new IOException("The reservation already exists.");
        var scope = HostReservationScope.Commission(path, enrollment);
        using var stableLock = new FileStream(LockPath(scope), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        _ = InstallEnrolled(new(scope, 0, null, new(0, null, null)), replace: false);
        return scope;
    }

    public static HostReservation OpenEnrolled(HostReservationScope scope, Func<HostEnrolledSnapshot>? readInstalled = null)
    {
        var stableLock = new FileStream(LockPath(scope), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var snapshot = readInstalled is null ? ReadEnrolled(scope) : readInstalled();
            return new HostReservation(stableLock, snapshot.Record.State, snapshot);
        }
        catch
        {
            stableLock.Dispose();
            throw;
        }
    }

    // The enrolled coordinator and worker share an issue fence around installs and these reads.
    // The worker can read the state file while the coordinator exclusively holds the different lock file.
    public static HostEnrolledSnapshot ReadEnrolled(HostReservationScope scope)
    {
        if (scope.IsLegacy || HostReservationScope.Read(scope.Slot) != scope)
            throw new IOException("The current scope does not authorize this enrolled reservation.");
        using var file = new FileStream(scope.Slot, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var (record, digest) = ReadFrame(file, HostReservationJsonContext.Default.HostEnrolledRecord);
        if (record.Scope != scope || record.Revision < 0 ||
            (record.Revision == 0 ? record.Predecessor is not null : !HostMutation.IsPlanDigest(record.Predecessor)))
            throw new IOException("The installed enrolled reservation does not match its commissioned scope.");
        _ = Validate(record.State);
        return new(record, digest);
    }

    private static HostEnrolledSnapshot InstallEnrolled(HostEnrolledRecord record, bool replace = true)
    {
        _ = Validate(record.State);
        if (replace && ReadEnrolled(record.Scope).Digest != record.Predecessor)
            throw new IOException("The installed enrolled reservation changed.");
        var temporary = record.Scope.Slot + ".new";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            WriteFrame(file, record, HostReservationJsonContext.Default.HostEnrolledRecord);
        File.Move(temporary, record.Scope.Slot, overwrite: replace);
        return ReadEnrolled(record.Scope);
    }

    private static string LockPath(HostReservationScope scope) => scope.Slot + ".lock";
}
