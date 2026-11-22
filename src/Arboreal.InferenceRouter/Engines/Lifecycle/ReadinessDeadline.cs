namespace Arboreal.InferenceRouter.Lifecycle;

internal enum ReadinessStepOutcome { Completed, Expired, Stopped, StepExpired }

internal readonly record struct ReadinessStep<T>(ReadinessStepOutcome Outcome, T? Observation = default);

/// <summary>One monotonic budget for the readiness reads of a switch attempt. Its owner runs one
/// step at a time and rechecks <see cref="OutcomeForSettlement"/> inside the publication guard.
/// Observers own their I/O and return data without publishing it. A shorter step cap
/// retires the attempt before a cancellation-ignoring read can overlap another step.</summary>
internal sealed class ReadinessDeadline : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly long _started;
    private readonly CancellationToken _engineStop;
    private readonly CancellationTokenSource _timerLifetime = new();
    private readonly CancellationTokenRegistration _stopRegistration;
    private readonly Task<Task> _interrupted;
    private bool _retiredByStepCap;
    private bool _disposed;

    private ReadinessDeadline(TimeProvider clock, long started, TimeSpan timeout, CancellationToken engineStop)
    {
        _clock = clock;
        _timeout = timeout;
        _started = started;
        _engineStop = engineStop;

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopRegistration = engineStop.UnsafeRegister(static state => ((TaskCompletionSource)state!).TrySetResult(), stopped);
        _interrupted = Task.WhenAny(WaitUntilAsync(_started, _timeout, _timerLifetime.Token), stopped.Task);
    }

    /// <summary>Nonpositive post-launch timeouts are exhausted at entry.</summary>
    internal static ReadinessDeadline Start(TimeProvider clock, TimeSpan timeout, CancellationToken engineStop) =>
        new(clock, clock.GetTimestamp(), timeout, engineStop);

    internal ReadinessDeadline LimitFrom(long started, TimeSpan cap)
    {
        var remaining = RemainingAt(_started, _timeout, started);
        return new(_clock, started, cap < remaining ? cap : remaining, _engineStop);
    }

    internal TimeSpan Remaining => RemainingAt(_started, _timeout);
    internal bool IsExpired => Remaining == TimeSpan.Zero;

    // Stop and absolute expiry can supersede a step cap until the owner commits its verdict.
    internal ReadinessStepOutcome OutcomeForSettlement =>
        WithStepRetirement(CurrentOutcome(_started, upperBound: null));

    /// <summary>Race the whole observation, including any synchronous processing before its first
    /// await. An in-budget observer exception is propagated unchanged.</summary>
    internal async ValueTask<ReadinessStep<T>> ObserveAsync<T>(
        Func<CancellationToken, Task<T>> read, TimeSpan? upperBound = null,
        Action<Task>? captureCleanupForTests = null)
    {
        var stepStarted = _clock.GetTimestamp();
        var status = ObserveOutcome(stepStarted, upperBound);
        if (status != ReadinessStepOutcome.Completed) return new(status);

        // An equal deadline belongs to the attempt. A shorter local cap never spends or replaces it.
        var remaining = RemainingAt(_started, _timeout, stepStarted);
        var limit = upperBound.GetValueOrDefault();
        var shorter = upperBound.HasValue && limit < remaining;
        using var capLifetime = shorter ? new CancellationTokenSource() : null;
        var cap = capLifetime is null ? null : WaitUntilAsync(stepStarted, limit, capLifetime.Token);

        var retire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = RunObservationAsync(read, stepStarted, upperBound, retire.Task);
        try
        {
            var winner = cap is null
                ? await Task.WhenAny(observation, _interrupted).ConfigureAwait(false)
                : await Task.WhenAny(observation, _interrupted, cap).ConfigureAwait(false);

            status = ObserveOutcome(stepStarted, upperBound);
            if (status != ReadinessStepOutcome.Completed) return new(status);
            if (winner == observation) return await observation.ConfigureAwait(false);

            await AwaitSignalAsync(winner).ConfigureAwait(false);
            throw new InvalidOperationException("The readiness clock woke without reaching its deadline.");
        }
        finally
        {
            capLifetime?.Cancel();
            retire.TrySetResult();
            var cleanup = ObserveRetiredAsync(observation);
            captureCleanupForTests?.Invoke(cleanup);
        }
    }

    private async Task<ReadinessStep<T>> RunObservationAsync<T>(
        Func<CancellationToken, Task<T>> read, long stepStarted, TimeSpan? upperBound, Task retired)
    {
        // The worker owns the source from construction through the read and any asynchronous
        // cancellation callbacks. The readiness owner only signals retirement and can leave.
        using var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        var operation = Task.Run(async () =>
        {
            var beforeRead = CurrentOutcome(stepStarted, upperBound);
            if (beforeRead != ReadinessStepOutcome.Completed) return new ReadinessStep<T>(beforeRead);
            return new ReadinessStep<T>(ReadinessStepOutcome.Completed, await read(token).ConfigureAwait(false));
        }, CancellationToken.None);

        if (await Task.WhenAny(operation, retired).ConfigureAwait(false) == retired)
            await Task.WhenAll(lifetime.CancelAsync(), operation).ConfigureAwait(false);
        return await operation.ConfigureAwait(false);
    }

    internal async ValueTask<ReadinessStepOutcome> DelayAsync(TimeSpan interval)
    {
        var now = _clock.GetTimestamp();
        var outcome = OutcomeForSettlement;
        if (outcome != ReadinessStepOutcome.Completed || interval == TimeSpan.Zero) return outcome;
        var remaining = RemainingAt(_started, _timeout, now);
        if (interval == Timeout.InfiniteTimeSpan || interval >= remaining)
        {
            await _interrupted.ConfigureAwait(false);
            outcome = CurrentOutcome(now, null);
            if (outcome != ReadinessStepOutcome.Completed) return outcome;

            await AwaitSignalAsync(_interrupted).ConfigureAwait(false);
            throw new InvalidOperationException("The readiness clock woke without reaching its deadline.");
        }

        using var delayLifetime = new CancellationTokenSource();
        var delay = WaitUntilAsync(now, interval, delayLifetime.Token);
        try
        {
            var winner = await Task.WhenAny(delay, _interrupted).ConfigureAwait(false);
            outcome = CurrentOutcome(now, null);
            if (outcome == ReadinessStepOutcome.Completed)
                await AwaitSignalAsync(winner).ConfigureAwait(false);
            return outcome;
        }
        finally { delayLifetime.Cancel(); }
    }

    private ReadinessStepOutcome ObserveOutcome(long stepStarted, TimeSpan? upperBound)
    {
        var outcome = CurrentOutcome(stepStarted, upperBound);
        if (outcome == ReadinessStepOutcome.StepExpired) _retiredByStepCap = true;
        return WithStepRetirement(outcome);
    }

    private ReadinessStepOutcome WithStepRetirement(ReadinessStepOutcome outcome) =>
        outcome == ReadinessStepOutcome.Completed && _retiredByStepCap ? ReadinessStepOutcome.StepExpired : outcome;

    private ReadinessStepOutcome CurrentOutcome(long stepStarted, TimeSpan? upperBound)
    {
        if (_engineStop.IsCancellationRequested) return ReadinessStepOutcome.Stopped;
        var now = _clock.GetTimestamp();
        if (RemainingAt(_started, _timeout, now) == TimeSpan.Zero) return ReadinessStepOutcome.Expired;
        if (upperBound is { } limit && RemainingAt(stepStarted, limit, now) == TimeSpan.Zero)
            return ReadinessStepOutcome.StepExpired;
        return ReadinessStepOutcome.Completed;
    }

    private TimeSpan RemainingAt(long started, TimeSpan duration, long? now = null)
    {
        var elapsed = _clock.GetElapsedTime(started, now ?? _clock.GetTimestamp());
        return elapsed >= duration ? TimeSpan.Zero : duration - elapsed;
    }

    private async Task WaitUntilAsync(long started, TimeSpan duration, CancellationToken ct)
    {
        for (var remaining = RemainingAt(started, duration); remaining != TimeSpan.Zero;
             remaining = RemainingAt(started, duration))
        {
            // Delay has millisecond timer resolution. Round up and recheck the absolute
            // monotonic boundary if the timer or the injected clock wakes early.
            var due = TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds));
            await Task.Delay(due, _clock, ct).ConfigureAwait(false);
        }
    }

    private async Task AwaitSignalAsync(Task signal)
    {
        if (signal == _interrupted) signal = await _interrupted.ConfigureAwait(false);
        await signal.ConfigureAwait(false);
    }

    private static async Task ObserveRetiredAsync(Task observation)
    {
        try { await observation.ConfigureAwait(false); }
        catch (Exception) { /* An in-budget fault was propagated. Observe any fault during late cleanup. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopRegistration.Dispose();
        _timerLifetime.Cancel();
        _timerLifetime.Dispose();
    }
}
