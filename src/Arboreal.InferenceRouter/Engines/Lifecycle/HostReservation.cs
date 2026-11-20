using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Arboreal.InferenceRouter.Lifecycle;

internal enum HostMutationKind { Stop, Launch }
internal enum HostMutationPhase { None, Prepared, Dispatching, LaunchApplied }

/// <summary>A stop names the immutable container ID. A launch binds the canonical model,
/// resolved profile, and complete provider plan digest to the epoch and operation ID.</summary>
internal sealed record HostMutation(
    long Epoch, Guid Id, HostMutationKind Kind, string Engine, string Target, string? Profile, string? PlanDigest)
{
    public static bool IsPlanDigest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HostReservationState([property: JsonRequired] long Epoch, [property: JsonRequired] string? Owner,
    [property: JsonRequired] HostMutation? Pending,
    HostMutationPhase Phase = HostMutationPhase.None, string? LaunchedContainerId = null);

[JsonSerializable(typeof(HostReservationState))]
internal sealed partial class HostReservationJsonContext : JsonSerializerContext;

/// <summary>The cooperating mutation authority for one physical host. Hold the exclusive handle
/// on the same pre-provisioned local file until the transaction and readiness check end. The file
/// starts with a checksummed genesis record and must stay in place across restarts.</summary>
internal sealed partial class HostReservation : IDisposable
{
    private const int DigestLength = 32;
    private const int MaxRecordLength = 16 * 1024;

    private readonly FileStream _file;
    private bool _usable = true;

    private HostReservation(FileStream file, HostReservationState state, HostEnrolledSnapshot? enrolled = null)
    {
        _file = file;
        State = state;
        Enrolled = enrolled;
    }

    public HostReservationState State { get; private set; }

    /// <summary>Commission a new reservation whose host has never used this authority. A running
    /// host coordinator always opens the existing file and has no provisioning fallback.</summary>
    public static void ProvisionNew(string path)
    {
        if (File.Exists(path)) throw new IOException("The reservation already exists.");
        HostReservationScope.Commission(path, enrollment: null);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        WriteState(file, new HostReservationState(0, null, null));
    }

    public static HostReservation Open(string path)
    {
        if (!HostReservationScope.Read(path).IsLegacy) throw new IOException("The reservation is not a commissioned legacy slot.");
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

    public HostMutation Begin(long epoch, HostMutationKind kind, string engine, string target,
        string? profile = null, string? planDigest = null)
    {
        RequireUsable();
        if (epoch <= 0 || epoch != State.Epoch || State.Pending is not null ||
            kind == HostMutationKind.Launch && !string.Equals(State.Owner, engine, StringComparison.Ordinal))
            throw new InvalidOperationException("The host reservation cannot authorize this operation.");

        var operation = new HostMutation(epoch, Guid.NewGuid(), kind, engine, target, profile, planDigest);
        Record(State with { Pending = operation, Phase = HostMutationPhase.Prepared });
        return operation;
    }

    public void MarkDispatching(HostMutation operation)
    {
        RequirePending(operation);
        if (State.Phase != HostMutationPhase.Prepared)
            throw new InvalidOperationException("Only a prepared host operation can begin dispatch.");
        Record(State with { Phase = HostMutationPhase.Dispatching });
    }

    public void RecordAppliedLaunch(HostMutation operation, string containerId)
    {
        RequirePending(operation);
        if (State.Phase != HostMutationPhase.Dispatching || operation.Kind != HostMutationKind.Launch)
            throw new InvalidOperationException("Only a dispatched launch can record an instance.");
        Record(State with { Phase = HostMutationPhase.LaunchApplied, LaunchedContainerId = containerId });
    }

    public void Settle(HostMutation operation)
    {
        RequirePending(operation);
        Record(State with { Pending = null, Phase = HostMutationPhase.None, LaunchedContainerId = null });
    }

    private void RequirePending(HostMutation operation)
    {
        RequireUsable();
        if (State.Pending != operation)
            throw new InvalidOperationException("The host operation does not match the durable intent.");
    }

    private static HostReservationState ReadState(FileStream file) =>
        Validate(ReadFrame(file, HostReservationJsonContext.Default.HostReservationState).Value);

    private static (T Value, string Digest) ReadFrame<T>(FileStream file, JsonTypeInfo<T> type) where T : class
    {
        if (file.Length <= DigestLength || file.Length > MaxRecordLength)
            throw new IOException("The host reservation record is incomplete or oversized.");

        var record = new byte[(int)file.Length];
        file.ReadExactly(record);
        var body = record.AsSpan(DigestLength);
        if (!CryptographicOperations.FixedTimeEquals(record.AsSpan(0, DigestLength), SHA256.HashData(body)))
            throw new IOException("The host reservation record has an invalid checksum.");

        try
        {
            var value = JsonSerializer.Deserialize(body, type) ?? throw new IOException("The host reservation record is empty.");
            return (value, Convert.ToHexString(record.AsSpan(0, DigestLength)));
        }
        catch (JsonException ex)
        {
            throw new IOException("The host reservation record is unreadable.", ex);
        }
    }

    private static HostReservationState Validate(HostReservationState? state)
    {
        if (state is { Epoch: 0, Owner: null, Pending: null, Phase: HostMutationPhase.None, LaunchedContainerId: null })
            return state;
        if (state is { Epoch: > 0 } && !string.IsNullOrWhiteSpace(state.Owner) && HasValidOperation(state))
            return state;

        throw new IOException("The host reservation record has an invalid owner or operation.");
    }

    private static bool HasValidOperation(HostReservationState state)
    {
        if (state.Pending is not { } operation)
            return state is { Phase: HostMutationPhase.None, LaunchedContainerId: null };
        if (operation.Epoch != state.Epoch || operation.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(operation.Engine) || string.IsNullOrWhiteSpace(operation.Target))
            return false;

        var validPlan = operation.Kind switch
        {
            HostMutationKind.Stop => operation is { Profile: null, PlanDigest: null },
            HostMutationKind.Launch => state.Owner == operation.Engine && HostMutation.IsPlanDigest(operation.PlanDigest),
            _ => false,
        };
        return validPlan && (state.Phase switch
        {
            HostMutationPhase.Prepared or HostMutationPhase.Dispatching => state.LaunchedContainerId is null,
            HostMutationPhase.LaunchApplied => operation.Kind == HostMutationKind.Launch &&
                !string.IsNullOrWhiteSpace(state.LaunchedContainerId),
            _ => false,
        });
    }

    private void Record(HostReservationState state)
    {
        // Invalidate this handle before writing so a flush failure cannot authorize a later action.
        _usable = false;
        if (Enrolled is { } enrolled)
            Enrolled = InstallEnrolled(enrolled.Record with
            {
                State = state, Revision = checked(enrolled.Record.Revision + 1), Predecessor = enrolled.Digest,
            });
        else
            WriteState(_file, state);
        State = state;
        _usable = true;
    }

    private static void WriteState(FileStream file, HostReservationState state) =>
        WriteFrame(file, Validate(state), HostReservationJsonContext.Default.HostReservationState);

    private static void WriteFrame<T>(FileStream file, T value, JsonTypeInfo<T> type) where T : class
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, type);
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
