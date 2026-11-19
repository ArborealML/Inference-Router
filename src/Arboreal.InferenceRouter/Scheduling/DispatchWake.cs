namespace Arboreal.InferenceRouter;

/// <summary>Coalesces dispatch signals without losing a publication between scan and park.</summary>
internal sealed class DispatchWake
{
    private readonly object _gate = new();
    private TaskCompletionSource? _waiter;
    private long _revision;

    internal long Revision => Interlocked.Read(ref _revision);

    internal Task Arm()
    {
        lock (_gate)
            return (_waiter ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    internal void Signal()
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            Interlocked.Increment(ref _revision);
            waiter = _waiter;
            _waiter = null;
        }
        waiter?.TrySetResult();
    }
}
