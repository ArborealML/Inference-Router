using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter;

public sealed record OwnedPolicyHead(Guid Store, long Revision, string Sha256);

/// <summary>The deployment supplies a rollback-resistant head outside the policy file's restore domain.</summary>
public interface IOwnedPolicyHeadAuthority
{
    ValueTask<OwnedPolicyHead?> ReadAsync(CancellationToken cancellationToken);
    ValueTask<bool> CompareAndCommitAsync(OwnedPolicyHead? previous, OwnedPolicyHead proposed, CancellationToken cancellationToken);
}

public sealed partial class OwnedPolicyStore : IDisposable
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int MaxGroups = 4_096;
    private readonly string _path;
    private readonly IOwnedPolicyHeadAuthority _authority;
    private readonly FileStream _writer;
    private readonly SemaphoreSlim _updates = new(1);
    private State _current;
    private int _unavailable;
    private readonly object _operationGate = new();
    private int _operations;
    private bool _disposing;

    private OwnedPolicyStore(string path, IOwnedPolicyHeadAuthority authority, FileStream writer, State current)
    {
        _path = path;
        _authority = authority;
        _writer = writer;
        _current = current;
    }

    public static Task<OwnedPolicyStore> OpenAsync(string path, IOwnedPolicyHeadAuthority authority,
        bool initialize = false, CancellationToken cancellationToken = default) =>
        OpenCoreAsync(path, authority, groupAuthority: null, initialize, cancellationToken);

    private static async Task<OwnedPolicyStore> OpenCoreAsync(string path, IOwnedPolicyHeadAuthority authority,
        IOwnedPolicyGroupHeadAuthority? groupAuthority, bool initialize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Owned private policy uses Linux durable file publication.");
        var fullPath = Path.GetFullPath(path);
        var writer = new FileStream(fullPath + ".writer", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var head = await authority.ReadAsync(cancellationToken).ConfigureAwait(false);
            State state;
            if (initialize)
            {
                if (head is not null || File.Exists(fullPath)) throw new IOException("A policy store already exists.");
                state = await PersistAsync(fullPath, authority, previous: null,
                    new Snapshot(1, Guid.NewGuid(), 1, [], GroupAuthorityRequired: groupAuthority is not null), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (head is null) throw new IOException("Current policy store evidence is unavailable.");
                var bytes = groupAuthority is null
                    ? await ReadAsync(fullPath, cancellationToken).ConfigureAwait(false)
                    : await RecoverSnapshotAsync(fullPath, RootProposedPath(fullPath), head, cancellationToken).ConfigureAwait(false);
                var snapshot = JsonSerializer.Deserialize(bytes, OwnedPolicyJsonContext.Default.Snapshot);
                if (!IsValid(snapshot) || head != Head(snapshot!, bytes) ||
                    snapshot!.GroupAuthorityRequired != (groupAuthority is not null))
                    throw new IOException("The stored policy does not match its current authority.");
                state = new State(snapshot!, head);
            }

            return new OwnedPolicyStore(fullPath, authority, writer, state) { _groupAuthority = groupAuthority };
        }
        catch (JsonException error)
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            throw new IOException("The owned policy store is invalid.", error);
        }
        catch
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask<OwnedPolicyGroup?> ResolveAsync(VerifiedInferenceScope scope, string? requestedGroup,
        bool mayCreate, CancellationToken cancellationToken)
    {
        using var operation = EnterOperation();
        if (operation is null) return null;
        var root = await ResolveRootAsync(scope, requestedGroup, mayCreate, cancellationToken).ConfigureAwait(false);
        return root is null ? null : await ResolveJournalAsync(root, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<OwnedPolicyGroup?> ResolveRootAsync(VerifiedInferenceScope scope, string? requestedGroup,
        bool mayCreate, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _unavailable) != 0) return null;
        var state = Volatile.Read(ref _current);
        if (scope.ExistingGroup is { } existing)
            return existing == requestedGroup ? state.Groups.GetValueOrDefault(new(scope.Owner, existing)) : null;
        if (scope.NewRootIntent is not { } intent || requestedGroup is not null) return null;
        if (state.Roots.TryGetValue((scope.Owner, intent), out var matched)) return matched;
        if (!mayCreate) return null;

        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_unavailable != 0) return null;
            state = _current;
            if (state.Roots.TryGetValue((scope.Owner, intent), out matched)) return matched;
            if (state.Snapshot.Groups.Length >= MaxGroups) return null;

            try
            {
                var bytes = await ReadAsync(_path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Digest(bytes), state.Head.Sha256, StringComparison.Ordinal))
                    throw new IOException("The stored policy has changed.");
                var revision = checked(state.Snapshot.Revision + 1);
                var group = new OwnedPolicyGroup(scope.Owner, Guid.NewGuid().ToString("N"), intent, false, revision,
                    scope.Allow, scope.Deny);
                var snapshot = state.Snapshot with { Revision = revision, Groups = [.. state.Snapshot.Groups, group] };
                var next = await PersistAsync(_path, _authority, state.Head, snapshot, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _current, next);
                return group;
            }
            catch (Exception error) when (OwnedPrivatePolicy.IsRecoverableAuthorityFailure(error))
            {
                // Once a write or its external confirmation is uncertain, this writer cannot grant admission.
                CloseGrantsGlobally();
                return null;
            }
        }
        finally
        {
            _updates.Release();
        }
    }

    internal bool IsCurrent(OwnedPolicyGroup group) => Available && (UsesGroupJournals ? IsCurrentInJournal(group) :
        ReferenceEquals(Volatile.Read(ref _current).Groups.GetValueOrDefault(group.Key), group));

    public void Dispose()
    {
        bool release;
        lock (_operationGate)
        {
            if (_disposing) return;
            _disposing = true;
            CloseGrantsGlobally();
            release = _operations == 0;
        }
        if (release) ReleaseWriter();
    }

    private StoreOperation? EnterOperation()
    {
        lock (_operationGate)
        {
            if (_disposing) return null;
            _operations++;
            return new(this);
        }
    }

    private void LeaveOperation()
    {
        bool release;
        lock (_operationGate) release = --_operations == 0 && _disposing;
        if (release) ReleaseWriter();
    }

    private void ReleaseWriter()
    {
        foreach (var journal in _journals.Values) journal.Dispose();
        _updates.Dispose();
        _writer.Dispose();
    }

    private sealed class StoreOperation(OwnedPolicyStore owner) : IDisposable
    {
        public void Dispose() => owner.LeaveOperation();
    }

    private static async Task<State> PersistAsync(string path, IOwnedPolicyHeadAuthority authority,
        OwnedPolicyHead? previous, Snapshot snapshot, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, OwnedPolicyJsonContext.Default.Snapshot);
        if (bytes.Length > MaxBytes) throw new IOException("The owned policy store is full.");
        var head = Head(snapshot, bytes);
        var target = snapshot.GroupAuthorityRequired ? RootProposedPath(path) : path;
        await WriteSnapshotAsync(target, bytes, overwrite: snapshot.GroupAuthorityRequired || previous is not null,
            cancellationToken).ConfigureAwait(false);
        if (!await authority.CompareAndCommitAsync(previous, head, cancellationToken).ConfigureAwait(false))
            throw new IOException("The policy authority did not confirm the new revision.");
        if (snapshot.GroupAuthorityRequired) Promote(target, path);
        return new State(snapshot, head);
    }

    private static async Task<byte[]> RecoverSnapshotAsync(string path, string proposed, OwnedPolicyHead head,
        CancellationToken cancellationToken)
    {
        if (await ReadHeadBytesAsync(path, head, cancellationToken).ConfigureAwait(false) is { } confirmed) return confirmed;
        var next = await ReadHeadBytesAsync(proposed, head, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The owned policy snapshot does not match its current authority.");
        Promote(proposed, path);
        return next;
    }

    private static string RootProposedPath(string path) => path + ".root-proposed";

    private static async Task WriteSnapshotAsync(string path, byte[] bytes, bool overwrite, CancellationToken cancellationToken)
    {
        var scratch = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            File.Move(scratch, path, overwrite);
            SyncParent(path);
        }
        finally { File.Delete(scratch); }
    }

    private static void Promote(string proposed, string confirmed)
    {
        File.Move(proposed, confirmed, overwrite: true);
        SyncParent(confirmed);
    }

    private static void SyncParent(string path)
    {
        using var directory = OpenDirectory(Path.GetDirectoryName(path)!);
        if (directory.IsInvalid || SyncDirectory(directory) != 0)
            throw new IOException("The owned policy directory could not be synchronized.");
    }

    private static async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, FileOptions.Asynchronous);
        if (file.Length is <= 0 or > MaxBytes) throw new IOException("The owned policy store has an invalid size.");
        var bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (file.ReadByte() != -1) throw new IOException("The owned policy store changed while being read.");
        return bytes;
    }

    private static OwnedPolicyHead Head(Snapshot snapshot, byte[] bytes) => new(snapshot.Store, snapshot.Revision, Digest(bytes));
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsValid(Snapshot? snapshot)
    {
        if (snapshot is not { SchemaVersion: 1, Revision: > 0, Groups.Length: <= MaxGroups } || snapshot.Store == Guid.Empty)
            return false;
        var groups = new HashSet<PolicyGroupKey>();
        var roots = new HashSet<(string, string)>();
        foreach (var group in snapshot.Groups)
            if (group is null || group.Owner is null || group.RootIntent is null || group.Deny is null ||
                !OwnedPrivatePolicy.IsIdentifier(group.Owner) || !OwnedPrivatePolicy.IsIdentifier(group.RootIntent) ||
                !Guid.TryParseExact(group.Group, "N", out _) || group.Revision <= 0 || group.Revision > snapshot.Revision ||
                !ValidNames(group.Allow) || !ValidNames(group.Deny) || !groups.Add(group.Key) ||
                !roots.Add((group.Owner, group.RootIntent))) return false;
        return true;

        static bool ValidNames(string[]? names) => names is null || names.Length <= 32 &&
            names.All(name => name is not null && OwnedPrivatePolicy.IsIdentifier(name)) &&
            names.Distinct(StringComparer.Ordinal).Count() == names.Length;
    }

    private static SafeFileHandle OpenDirectory(string path) => new((nint)Open(path, 0x90000), ownsHandle: true);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync")]
    private static partial int SyncDirectory(SafeFileHandle directory);

    internal sealed record Snapshot([property: JsonRequired] int SchemaVersion, [property: JsonRequired] Guid Store,
        [property: JsonRequired] long Revision, [property: JsonRequired] OwnedPolicyGroup[] Groups,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool GroupAuthorityRequired = false);

    private sealed class State(Snapshot snapshot, OwnedPolicyHead head)
    {
        internal Snapshot Snapshot { get; } = snapshot;
        internal OwnedPolicyHead Head { get; } = head;
        internal FrozenDictionary<PolicyGroupKey, OwnedPolicyGroup> Groups { get; } =
            snapshot.Groups.ToFrozenDictionary(static group => group.Key);
        internal FrozenDictionary<(string, string), OwnedPolicyGroup> Roots { get; } =
            snapshot.Groups.ToFrozenDictionary(static group => (group.Owner, group.RootIntent));
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(OwnedPolicyStore.Snapshot))]
[JsonSerializable(typeof(OwnedPolicyStore.GroupSnapshot))]
internal sealed partial class OwnedPolicyJsonContext : JsonSerializerContext;
