using System.Security.Cryptography;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

/// <summary>Offline effect checkpoint for cooperating processes that retain a trusted local store.
/// The stable lock file supplies exclusion. Only the separately installed record supplies state.</summary>
internal sealed class HostEffectCheckpoint : IDisposable
{
    private const int HashLength = 32;
    private const int MaxRecordLength = 64 * 1024;
    private const string CheckpointSuffix = ".checkpoint";
    private const string TemporarySuffix = ".new";

    private readonly Lock _gate = new();
    private readonly FileStream _lockFile;
    private readonly string _path;
    private readonly Action<string, string>? _replaceForTests;
    private HostEffectCheckpointState _state;
    private bool _usable = true;

    private HostEffectCheckpoint(FileStream lockFile, string path, HostEffectCheckpointState state,
        Action<string, string>? replaceForTests)
    {
        _lockFile = lockFile;
        _path = path;
        _state = state;
        _replaceForTests = replaceForTests;
    }

    public HostEffectCheckpointState State
    {
        get
        {
            lock (_gate)
            {
                RequireUsable();
                return _state;
            }
        }
    }

    /// <summary>Explicit bootstrap of a never-used fixture. Reopen never creates either file.</summary>
    public static void ProvisionNew(string lockPath)
    {
        var fullPath = Path.GetFullPath(lockPath);
        var path = fullPath + CheckpointSuffix;
        if (File.Exists(path)) throw new IOException("The host effect checkpoint already exists.");
        using var lockFile = new FileStream(fullPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var temporary = path + TemporarySuffix;
        WriteTemporary(temporary, HostEffectCheckpointState.Genesis);
        File.Move(temporary, path);
    }

    public static HostEffectCheckpoint Open(string lockPath, Action<string, string>? replaceForTests = null)
    {
        var fullPath = Path.GetFullPath(lockPath);
        var path = fullPath + CheckpointSuffix;
        var lockFile = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            return new HostEffectCheckpoint(lockFile, path, ReadInstalled(path), replaceForTests);
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }
    }

    public HostMutation PrepareInitialLaunch(HostCheckpointPlan plan)
    {
        var current = RequirePhase(HostCheckpointPhase.Genesis);
        var launch = NewLaunch(plan, 1);
        Install(current, current with
        {
            Epoch = launch.Epoch, Phase = HostCheckpointPhase.LaunchPrepared,
            Transition = new HostCheckpointTransition(plan, Launch: launch),
        });
        return launch;
    }

    public HostMutation PrepareSuccessor(HostCheckpointPlan plan)
    {
        var current = State;
        if (current.Phase is not (HostCheckpointPhase.Resident or HostCheckpointPhase.RetainedBeforeDispatch or
            HostCheckpointPhase.RetainedAfterRejection) || current.AppliedLaunch is not { } incumbent || plan == incumbent.Plan)
            throw new InvalidOperationException("Only a different plan can supersede a retained launch.");

        var epoch = checked(current.Epoch + 1);
        var stop = new HostMutation(epoch, Guid.NewGuid(), HostMutationKind.Stop, incumbent.Operation.Engine,
            incumbent.InstanceId, null, null);
        Install(current, current with
        {
            Epoch = epoch, Phase = HostCheckpointPhase.StopPrepared, Transition = new HostCheckpointTransition(plan, stop),
        });
        return stop;
    }

    public void MarkStopDispatching(HostMutation stop)
    {
        var current = RequirePhase(HostCheckpointPhase.StopPrepared);
        RequireOperation(current.Transition!.Stop, stop);
        Install(current, current with { Phase = HostCheckpointPhase.StopDispatching });
    }

    public void AbortPreparedStop(HostMutation stop)
    {
        var current = RequirePhase(HostCheckpointPhase.StopPrepared);
        RequireOperation(current.Transition!.Stop, stop);
        Install(current, current with { Phase = HostCheckpointPhase.RetainedBeforeDispatch });
    }

    public void RetainAfterRejectedStop(HostMutationOutcome receipt, HostCheckpointBeforeIssueFence fence)
    {
        var current = RequirePhase(HostCheckpointPhase.StopDispatching);
        RequireOperation(current.Transition!.Stop, receipt.Operation);
        RequireOperation(current.Transition.Stop, fence.Operation);
        if (receipt.Status != HostMutationStatus.RejectedBeforeIssue || receipt.ContainerId is not null)
            throw new InvalidOperationException("Retaining the incumbent requires a certified stop rejection before issue.");

        Install(current, current with { Phase = HostCheckpointPhase.RetainedAfterRejection });
    }

    public void RecordAppliedStop(HostMutationOutcome receipt, HostCheckpointStopEffect effect)
    {
        var current = RequirePhase(HostCheckpointPhase.StopDispatching);
        var transition = current.Transition!;
        var stop = transition.Stop!;
        var incumbent = current.AppliedLaunch!;
        RequireAppliedReceipt(stop, receipt, incumbent.InstanceId);
        if (!effect.Matches(stop, incumbent))
            throw new InvalidOperationException("The stop effect does not match the exact incumbent and stop operation.");

        Install(current, current with
        {
            Phase = HostCheckpointPhase.StopApplied, Transition = transition with { StopEffect = effect },
        });
    }

    public HostMutation PrepareSuccessorLaunch(HostCheckpointPlan plan)
    {
        var current = RequirePhase(HostCheckpointPhase.StopApplied);
        var transition = current.Transition!;
        if (plan != transition.Plan) throw new InvalidOperationException("The successor plan changed after stop dispatch.");
        var launch = NewLaunch(transition.Plan, current.Epoch);
        Install(current, current with
        {
            Phase = HostCheckpointPhase.LaunchPrepared, Transition = transition with { Launch = launch },
        });
        return launch;
    }

    public void MarkLaunchDispatching(HostMutation launch)
    {
        var current = RequirePhase(HostCheckpointPhase.LaunchPrepared);
        RequireOperation(current.Transition!.Launch, launch);
        Install(current, current with { Phase = HostCheckpointPhase.LaunchDispatching });
    }

    public void RecordAppliedLaunch(HostMutationOutcome receipt, HostCheckpointLaunchEffect effect)
    {
        var current = RequirePhase(HostCheckpointPhase.LaunchDispatching);
        var transition = current.Transition!;
        var launch = transition.Launch!;
        RequireAppliedReceipt(launch, receipt, effect.InstanceId);
        RequireOperation(launch, effect.Operation);
        if (effect.Plan != transition.Plan || !effect.IsValid())
            throw new InvalidOperationException("The launch effect does not bind the frozen plan and immutable instance.");

        var stopped = transition.StopEffect is { } stopEffect
            ? new HostCheckpointStoppedResident(current.AppliedLaunch!, stopEffect) : null;
        Install(current, current with
        {
            Phase = HostCheckpointPhase.Resident, AppliedLaunch = effect, Transition = null, LastStoppedResident = stopped,
        });
    }

    private static HostMutation NewLaunch(HostCheckpointPlan plan, long epoch) =>
        new(epoch, Guid.NewGuid(), HostMutationKind.Launch, plan.Engine, plan.Model, plan.Tier, plan.Digest());

    private HostEffectCheckpointState RequirePhase(HostCheckpointPhase phase)
    {
        var state = State;
        return state.Phase == phase ? state : throw new InvalidOperationException("The host effect checkpoint is in a different phase.");
    }

    private static void RequireOperation(HostMutation? expected, HostMutation? operation)
    {
        if (expected is null || operation != expected)
            throw new InvalidOperationException("The host operation does not match the checkpoint intent.");
    }

    private static void RequireAppliedReceipt(HostMutation operation, HostMutationOutcome receipt, string instance)
    {
        RequireOperation(operation, receipt.Operation);
        if (receipt.Status != HostMutationStatus.Applied || receipt.ContainerId != instance || string.IsNullOrWhiteSpace(instance))
            throw new InvalidOperationException("The terminal receipt does not match the independent effect.");
    }

    private void Install(HostEffectCheckpointState predecessor, HostEffectCheckpointState next)
    {
        lock (_gate)
        {
            RequireUsable();
            if (_state != predecessor) throw new InvalidOperationException("The checkpoint transition has a stale predecessor.");
            next = next with { Revision = checked(predecessor.Revision + 1) };
            if (!next.IsValid()) throw new InvalidOperationException("The checkpoint transition contains invalid evidence.");

            // A failed reread or write can leave authority uncertain. Only a new opener may resolve it.
            _usable = false;
            if (ReadInstalled(_path) != predecessor) throw new IOException("The installed host effect checkpoint changed.");
            var temporary = _path + TemporarySuffix;
            WriteTemporary(temporary, next);
            if (_replaceForTests is { } replace) replace(temporary, _path);
            else File.Move(temporary, _path, overwrite: true);
            _state = next;
            _usable = true;
        }
    }

    private static HostEffectCheckpointState ReadInstalled(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = file.Length;
        if (length <= HashLength || length > MaxRecordLength)
            throw new IOException("The host effect checkpoint is incomplete or oversized.");
        var bytes = new byte[(int)length];
        file.ReadExactly(bytes);
        var body = bytes.AsSpan(HashLength);
        if (!CryptographicOperations.FixedTimeEquals(bytes.AsSpan(0, HashLength), SHA256.HashData(body)))
            throw new IOException("The host effect checkpoint checksum is invalid.");

        try
        {
            var state = JsonSerializer.Deserialize(body, HostEffectCheckpointJsonContext.Default.HostEffectCheckpointState);
            if (state?.IsValid() == true) return state;
            throw new IOException("The host effect checkpoint is inconsistent.");
        }
        catch (JsonException ex)
        {
            throw new IOException("The host effect checkpoint is unreadable.", ex);
        }
    }

    private static void WriteTemporary(string path, HostEffectCheckpointState state)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(state, HostEffectCheckpointJsonContext.Default.HostEffectCheckpointState);
        if (body.Length > MaxRecordLength - HashLength) throw new IOException("The host effect checkpoint is oversized.");
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        file.Write(SHA256.HashData(body));
        file.Write(body);
        file.Flush(flushToDisk: true);
    }

    private void RequireUsable()
    {
        if (!_usable) throw new IOException("The host effect checkpoint is closed or needs to be reopened.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _usable = false;
            _lockFile.Dispose();
        }
    }
}
