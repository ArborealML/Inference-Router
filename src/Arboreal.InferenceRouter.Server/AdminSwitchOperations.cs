using System.Diagnostics.CodeAnalysis;

namespace Arboreal.InferenceRouter.Server;

internal sealed class AdminSwitchOperations(TimeProvider clock)
{
    internal const int PendingLimit = 32;
    internal const int TotalLimit = 288;
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);
    private static readonly EngineRuntime.SwitchResult UnexpectedFailure = EngineRuntime.SwitchResult.Failed(SwitchReason.SwitchError);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Operation> _records = [];
    private int _pending;

    internal TimeProvider Clock => clock;
    internal Action? BeforeScheduleForTests;
    internal Action<Guid, bool>? ObserveWaiterForTests;

    internal sealed class Operation(Guid id)
    {
        private int _finalizing;

        internal Guid Id { get; } = id;
        // These three values are read and written under the registry gate.
        internal EngineRuntime.SwitchResult? Result { get; set; }
        internal bool AckClosed { get; set; }
        internal long? RetentionStarted { get; set; }

        internal bool TryClaimFinalization() => Interlocked.Exchange(ref _finalizing, 1) == 0;
    }

    internal bool TryReserve([NotNullWhen(true)] out Operation? operation)
    {
        operation = null;
        lock (_gate)
        {
            if (_pending == PendingLimit) return false;
            if (_records.Count == TotalLimit && !ReclaimExpired(clock.GetTimestamp())) return false;

            Guid id;
            do { id = Guid.NewGuid(); } while (_records.ContainsKey(id));
            operation = new Operation(id);
            _records.Add(id, operation);
            _pending++;
            return true;
        }
    }

    private bool ReclaimExpired(long now)
    {
        Guid? expired = null;
        foreach (var entry in _records)
        {
            if (!IsExpired(entry.Value, now)) continue;
            expired = entry.Key;
            break;
        }
        return expired is { } id && _records.Remove(id);
    }

    private bool IsExpired(Operation operation, long now) =>
        operation.RetentionStarted is { } start && clock.GetElapsedTime(start, now) >= Retention;

    internal SwitchOperationSnapshot? Lookup(Guid id)
    {
        EngineRuntime.SwitchResult? result;
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var operation) || IsExpired(operation, clock.GetTimestamp())) return null;
            result = operation.Result;
        }

        (string status, string? reason) = result?.Outcome switch
        {
            null => ("pending", null),
            EngineRuntime.SwitchOutcome.Ready => ("succeeded", null),
            EngineRuntime.SwitchOutcome.RouterStopped => ("cancelled", "cancelled"),
            _ => ("failed", result!.Detail),
        };
        return new SwitchOperationSnapshot(id.ToString("N"), status, reason);
    }

    internal void CloseAck(Operation operation)
    {
        lock (_gate)
        {
            if (operation.AckClosed) return;
            operation.AckClosed = true;
            if (operation.Result is not null) operation.RetentionStarted = clock.GetTimestamp();
        }
    }

    internal void RecordResult(Operation operation, EngineRuntime.SwitchResult result)
    {
        lock (_gate)
        {
            if (operation.Result is not null) return;
            operation.Result = Normalize(result);
            _pending--;
            if (operation.AckClosed) operation.RetentionStarted = clock.GetTimestamp();
        }
    }

    private static EngineRuntime.SwitchResult Normalize(EngineRuntime.SwitchResult result) => result.Outcome switch
    {
        EngineRuntime.SwitchOutcome.Ready or EngineRuntime.SwitchOutcome.RouterStopped => result,
        EngineRuntime.SwitchOutcome.Failed when result.Detail is { } reason && SwitchReason.All.Contains(reason) => result,
        _ => UnexpectedFailure,
    };

    internal void Schedule(Operation operation, RouterCore router, EngineRuntime engine, string model, ServingProfile? profile)
    {
        try
        {
            BeforeScheduleForTests?.Invoke();
#pragma warning disable IDISP013 // The accepted operation owns this intentionally detached worker.
            _ = Task.Run(() => RunAsync(operation, router, engine, model, profile));
#pragma warning restore IDISP013
        }
        catch (Exception)
        {
            FinalizeWorker(operation, router, UnexpectedFailure);
        }
    }

    private async Task RunAsync(Operation operation, RouterCore router, EngineRuntime engine, string model,
        ServingProfile? profile)
    {
        var result = UnexpectedFailure;
        try
        {
            var waiter = profile is null
                ? engine.WaitForModelAsync(model, Timeout.InfiniteTimeSpan, CancellationToken.None)
                : engine.WaitForProfileAsync(model, profile, Timeout.InfiniteTimeSpan, CancellationToken.None);
            EngineRuntime.SwitchResult observed;
            try { ObserveWaiterForTests?.Invoke(operation.Id, !waiter.IsCompleted); }
            finally { observed = await waiter.ConfigureAwait(false); }
            result = observed;
        }
        catch (Exception)
        {
            // Unexpected worker faults use the same bounded category as a submission fault.
        }
        finally
        {
            FinalizeWorker(operation, router, result);
        }
    }

    private void FinalizeWorker(Operation operation, RouterCore router, EngineRuntime.SwitchResult result)
    {
        if (!operation.TryClaimFinalization()) return;
        // A terminal lookup must not race a topology apply against a still-held engine use.
        router.ReleaseEngineAdmin();
        RecordResult(operation, result);
    }
}
