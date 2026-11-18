using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal enum HostMutationKind { Stop, Launch }

/// <summary>A stop names the immutable container ID. A launch names the canonical model and
/// resolved profile, and the provider binds its launch to this operation ID.</summary>
internal sealed record HostMutation(
    long Epoch, Guid Id, HostMutationKind Kind, string Engine, string Target, string? Profile);

internal sealed record HostReservationState(long Epoch, string? Owner, HostMutation? Pending);

[JsonSerializable(typeof(HostReservationState))]
internal sealed partial class HostReservationJsonContext : JsonSerializerContext;

/// <summary>The cooperating mutation authority for one physical host. Hold the exclusive handle
/// on the same pre-provisioned local file until the transaction and readiness check end. The file
/// starts with a checksummed genesis record and must stay in place across restarts.</summary>
internal sealed class HostReservation : IDisposable
{
    private const int DigestLength = 32;
    private const int MaxRecordLength = 16 * 1024;

    private readonly FileStream _file;
    private bool _usable = true;

    private HostReservation(FileStream file, HostReservationState state)
    {
        _file = file;
        State = state;
    }

    public HostReservationState State { get; private set; }

    /// <summary>Commission a new reservation whose host has never used this authority. A running
    /// host coordinator always opens the existing file and has no provisioning fallback.</summary>
    public static void ProvisionNew(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        WriteState(file, new HostReservationState(0, null, null));
    }

    public static HostReservation Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            return new HostReservation(file, ReadState(file));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public long Grant(string owner)
    {
        RequireUsable();
        if (State.Pending is not null)
            throw new InvalidOperationException("The previous host operation has not settled.");

        var epoch = checked(State.Epoch + 1);
        Record(new HostReservationState(epoch, owner, null));
        return epoch;
    }

    public HostMutation Begin(long epoch, HostMutationKind kind, string engine, string target, string? profile = null)
    {
        RequireUsable();
        if (epoch <= 0 || epoch != State.Epoch || State.Pending is not null ||
            kind == HostMutationKind.Launch && !string.Equals(State.Owner, engine, StringComparison.Ordinal))
            throw new InvalidOperationException("The host reservation cannot authorize this operation.");

        var operation = new HostMutation(epoch, Guid.NewGuid(), kind, engine, target, profile);
        Record(State with { Pending = operation });
        return operation;
    }

    public void Settle(HostMutation operation)
    {
        RequireUsable();
        if (State.Pending != operation)
            throw new InvalidOperationException("The host operation does not match the durable intent.");

        Record(State with { Pending = null });
    }

    private static HostReservationState ReadState(FileStream file)
    {
        if (file.Length <= DigestLength || file.Length > MaxRecordLength)
            throw new IOException("The host reservation record is incomplete or oversized.");

        var record = new byte[(int)file.Length];
        file.ReadExactly(record);
        var body = record.AsSpan(DigestLength);
        if (!CryptographicOperations.FixedTimeEquals(record.AsSpan(0, DigestLength), SHA256.HashData(body)))
            throw new IOException("The host reservation record has an invalid checksum.");

        HostReservationState? state;
        try
        {
            state = JsonSerializer.Deserialize(body, HostReservationJsonContext.Default.HostReservationState);
        }
        catch (JsonException ex)
        {
            throw new IOException("The host reservation record is unreadable.", ex);
        }
        return Validate(state);
    }

    private static HostReservationState Validate(HostReservationState? state)
    {
        if (state is { Epoch: 0, Owner: null, Pending: null }) return state;

        if (state is null || state.Epoch <= 0 || string.IsNullOrWhiteSpace(state.Owner) ||
            state.Pending is { } operation && (operation.Epoch != state.Epoch || operation.Id == Guid.Empty ||
                operation.Kind is not HostMutationKind.Stop and not HostMutationKind.Launch ||
                string.IsNullOrWhiteSpace(operation.Engine) ||
                string.IsNullOrWhiteSpace(operation.Target) ||
                operation.Kind == HostMutationKind.Launch && !string.Equals(state.Owner, operation.Engine, StringComparison.Ordinal)))
            throw new IOException("The host reservation record has an invalid owner or operation.");

        return state;
    }

    private void Record(HostReservationState state)
    {
        // Invalidate this handle before writing so a flush failure cannot authorize a later action.
        _usable = false;
        WriteState(_file, state);
        State = state;
        _usable = true;
    }

    private static void WriteState(FileStream file, HostReservationState state)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(Validate(state), HostReservationJsonContext.Default.HostReservationState);
        var length = DigestLength + body.Length;
        if (length > MaxRecordLength) throw new IOException("The host reservation record is oversized.");

        // Keep the same file and lock. A torn overwrite is detected on reopen and blocks mutation.
        file.Position = 0;
        file.Write(SHA256.HashData(body));
        file.Write(body);
        file.SetLength(length);
        file.Flush(flushToDisk: true);
    }

    private void RequireUsable()
    {
        if (!_usable) throw new IOException("The host reservation is closed or its last write failed.");
    }

    public void Dispose()
    {
        _usable = false;
        _file.Dispose();
    }
}
