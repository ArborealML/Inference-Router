using System.Net;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class ReadinessDeadlineTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldObservationUsesTheRemainingAttemptBudget(bool holdModels)
    {
        var clock = new ManualClock();
        using var deadline = Start(clock);
        var held = new HeldRead<string>(holdModels ? "late models" : "late state");
        try
        {
            clock.AdvanceTo(70);
            var first = await deadline.ObserveAsync(_ => Task.FromResult(holdModels ? "state" : "models"))
                .AsTask().WaitAsync(Watchdog);
            Assert.Equal(ReadinessStepOutcome.Completed, first.Outcome);
            Assert.Equal(Ms(30), deadline.Remaining);

            var second = deadline.ObserveAsync(held.ReadAsync).AsTask();
            await held.Entered.Task.WaitAsync(Watchdog);
            clock.AdvanceTo(99);
            Assert.False(second.IsCompleted);

            clock.AdvanceTo(100);
            var result = await second.WaitAsync(Watchdog);
            Assert.Equal(ReadinessStepOutcome.Expired, result.Outcome);
            Assert.Null(result.Observation);
            await held.Cancelled.Task.WaitAsync(Watchdog);
            Assert.False(held.Released.Task.IsCompleted);
            Assert.True(deadline.IsExpired);

            var nextStarted = false;
            var next = await deadline.ObserveAsync(_ =>
            {
                nextStarted = true;
                return Task.FromResult("another poll");
            });
            Assert.Equal(ReadinessStepOutcome.Expired, next.Outcome);
            Assert.False(nextStarted);
        }
        finally
        {
            held.Release.TrySetResult();
        }
        await held.Released.Task.WaitAsync(Watchdog);
    }

    [Fact]
    public async Task HeldResponseBodyCannotKeepHeadersOnlyReadinessAlive()
    {
        var clock = new ManualClock();
        using var deadline = Start(clock);
        var held = new HeldRead<byte[]>("""{"window":32768}"""u8.ToArray());
        using var content = new HeldJsonContent(held);
        using var handler = new ReplyWithContent(content);
        using var http = new HttpClient(handler, disposeHandler: false);

        async Task<long> ReadAsync(CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/state");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.GetProperty("window").GetInt64();
        }

        try
        {
            var read = deadline.ObserveAsync(ReadAsync).AsTask();
            await held.Entered.Task.WaitAsync(Watchdog);
            clock.AdvanceTo(99);
            Assert.False(read.IsCompleted);
            clock.AdvanceTo(100);
            Assert.Equal(ReadinessStepOutcome.Expired, (await read.WaitAsync(Watchdog)).Outcome);
            Assert.False(held.Released.Task.IsCompleted);
        }
        finally
        {
            held.Release.TrySetResult();
            await content.Disposed.Task.WaitAsync(Watchdog);
        }
    }

    [Fact]
    public async Task SynchronousObservationWorkCanFinishOnlyAfterTheAttemptRetires()
    {
        var clock = new ManualClock();
        using var deadline = Start(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            // A handler with buffered bytes can perform parsing synchronously before returning a task.
            // Schedule the test caller too so a regression that blocks it is visible to the fixture.
            var read = Task.Run(async () => await deadline.ObserveAsync(_ =>
            {
                entered.TrySetResult();
                try
                {
                    release.Task.WaitAsync(Watchdog, CancellationToken.None).GetAwaiter().GetResult();
                    using var document = JsonDocument.Parse("""{"window":32768}""");
                    return Task.FromResult(document.RootElement.GetProperty("window").GetInt64());
                }
                finally { exited.TrySetResult(); }
            }));
            await entered.Task.WaitAsync(Watchdog);
            clock.AdvanceTo(100);
            Assert.Equal(ReadinessStepOutcome.Expired, (await read.WaitAsync(Watchdog)).Outcome);
            Assert.False(exited.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await exited.Task.WaitAsync(Watchdog);
        }
    }

    [Fact]
    public async Task AShorterStateCapRetiresTheAttemptWhileItsReadIsStillHeld()
    {
        var clock = new ManualClock();
        using var stop = new CancellationTokenSource();
        using var deadline = Start(clock, stop.Token);
        var held = new HeldRead<int>(7);
        try
        {
            clock.AdvanceTo(10);
            var state = deadline.ObserveAsync(held.ReadAsync, upperBound: Ms(30)).AsTask();
            await held.Entered.Task.WaitAsync(Watchdog);

            clock.AdvanceTo(39);
            Assert.False(state.IsCompleted);
            clock.AdvanceTo(40);
            Assert.Equal(ReadinessStepOutcome.StepExpired, (await state.WaitAsync(Watchdog)).Outcome);
            Assert.False(deadline.IsExpired);
            Assert.Equal(ReadinessStepOutcome.StepExpired, deadline.OutcomeForSettlement);
            Assert.Equal(Ms(60), deadline.Remaining);
            await held.Cancelled.Task.WaitAsync(Watchdog);

            var nextStarted = false;
            var next = await deadline.ObserveAsync(_ =>
            {
                nextStarted = true;
                return Task.FromResult("another read");
            }).AsTask().WaitAsync(Watchdog);
            Assert.Equal(ReadinessStepOutcome.StepExpired, next.Outcome);
            Assert.False(nextStarted);
            Assert.False(held.Released.Task.IsCompleted);
            Assert.Equal(ReadinessStepOutcome.StepExpired, await deadline.DelayAsync(Ms(20)));

            clock.AdvanceTo(100, fireTimers: false);
            Assert.Equal(ReadinessStepOutcome.Expired, deadline.OutcomeForSettlement);
            Assert.Equal(ReadinessStepOutcome.Expired, await deadline.DelayAsync(Ms(20)));
            Assert.Equal(ReadinessStepOutcome.Expired, (await deadline.ObserveAsync<int>(
                _ => throw new InvalidOperationException("read started after the deadline"))).Outcome);

            await stop.CancelAsync();
            Assert.Equal(ReadinessStepOutcome.Stopped, deadline.OutcomeForSettlement);
            Assert.Equal(ReadinessStepOutcome.Stopped, await deadline.DelayAsync(Ms(20)));
            Assert.Equal(ReadinessStepOutcome.Stopped, (await deadline.ObserveAsync<int>(
                _ => throw new InvalidOperationException("read started after stop"))).Outcome);
            Assert.False(held.Released.Task.IsCompleted);
        }
        finally
        {
            held.Release.TrySetResult();
        }
        await held.Released.Task.WaitAsync(Watchdog);
    }

    [Fact]
    public async Task PollDelaysShareTheAttemptDeadline()
    {
        var clock = new ManualClock();
        using var deadline = Start(clock);
        clock.AdvanceTo(40);
        var shortPoll = deadline.DelayAsync(Ms(20)).AsTask();
        clock.AdvanceTo(60);
        Assert.Equal(ReadinessStepOutcome.Completed, await shortPoll.WaitAsync(Watchdog));
        Assert.Equal(Ms(40), deadline.Remaining);

        var lastPoll = deadline.DelayAsync(Ms(80)).AsTask();
        clock.AdvanceTo(99);
        Assert.False(lastPoll.IsCompleted);
        clock.AdvanceTo(100);
        Assert.Equal(ReadinessStepOutcome.Expired, await lastPoll.WaitAsync(Watchdog));
    }

    [Fact]
    public async Task EngineStopBeatsCoincidentExpiryAndReleasesAnIgnoringRead()
    {
        var clock = new ManualClock();
        using var stop = new CancellationTokenSource();
        using var deadline = Start(clock, stop.Token);
        var held = new HeldRead<string>("late");
        try
        {
            var read = deadline.ObserveAsync(held.ReadAsync).AsTask();
            await held.Entered.Task.WaitAsync(Watchdog);

            clock.AdvanceTo(100, fireTimers: false);
            await stop.CancelAsync();
            Assert.Equal(ReadinessStepOutcome.Stopped, (await read.WaitAsync(Watchdog)).Outcome);
            Assert.True(deadline.IsExpired);
            await held.Cancelled.Task.WaitAsync(Watchdog);
            Assert.False(held.Released.Task.IsCompleted);
        }
        finally
        {
            held.Release.TrySetResult();
        }
        await held.Released.Task.WaitAsync(Watchdog);
    }

    [Fact]
    public async Task ABlockedCancellationCallbackCannotHoldTheDeadlineOwner()
    {
        var clock = new ManualClock();
        using var deadline = Start(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> ReadAsync(CancellationToken token)
        {
            try
            {
                using var registration = token.Register(() =>
                {
                    cancelEntered.TrySetResult();
                    releaseCancel.Task.WaitAsync(Watchdog, CancellationToken.None).GetAwaiter().GetResult();
                });
                entered.TrySetResult();
                await releaseRead.Task;
                return 1;
            }
            finally { released.TrySetResult(); }
        }

        try
        {
            var read = deadline.ObserveAsync(ReadAsync).AsTask();
            await entered.Task.WaitAsync(Watchdog);
            clock.AdvanceTo(100);
            await cancelEntered.Task.WaitAsync(Watchdog);
            Assert.Equal(ReadinessStepOutcome.Expired, (await read.WaitAsync(Watchdog)).Outcome);
            Assert.False(released.Task.IsCompleted);
        }
        finally
        {
            releaseRead.TrySetResult();
            releaseCancel.TrySetResult();
        }
        await released.Task.WaitAsync(Watchdog);
    }

    [Fact]
    public async Task PrecommitClockCheckRejectsAnEarlierObservationEvenBeforeTimerDelivery()
    {
        var clock = new ManualClock();
        using var deadline = Start(clock);

        clock.AdvanceTo(99);
        var observation = await deadline.ObserveAsync(_ => Task.FromResult("in-budget proof"))
            .AsTask().WaitAsync(Watchdog);
        Assert.Equal(ReadinessStepOutcome.Completed, observation.Outcome);
        Assert.Equal("in-budget proof", observation.Observation);
        Assert.False(deadline.IsExpired);

        // Simulate the gap between the observation returning and the publication guard.
        clock.AdvanceTo(100, fireTimers: false);
        Assert.Equal(1, clock.ActiveTimers);
        Assert.True(deadline.IsExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
    }

    [Fact]
    public async Task UnavailableStateAndAnObserver503KeepTheirOriginalMeaning()
    {
        var clock = new ManualClock();
        using var deadline = Start(clock);

        var unavailable = await deadline.ObserveAsync(_ => Task.FromResult<string?>(null))
            .AsTask().WaitAsync(Watchdog);
        Assert.Equal(ReadinessStepOutcome.Completed, unavailable.Outcome);
        Assert.Null(unavailable.Observation);

        var unavailableEndpoint = new HttpRequestException("models day-off", null, HttpStatusCode.ServiceUnavailable);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => deadline
            .ObserveAsync(_ => Task.FromException<string>(unavailableEndpoint)).AsTask().WaitAsync(Watchdog));
        Assert.Same(unavailableEndpoint, error);
        Assert.False(deadline.IsExpired);
    }

    [Fact]
    public async Task DisposalDisarmsUnusedDeadlineAndShorterStepTimers()
    {
        var clock = new ManualClock();
        using (var deadline = Start(clock))
        {
            var read = await deadline.ObserveAsync(_ => Task.FromResult("done"), upperBound: Ms(20))
                .AsTask().WaitAsync(Watchdog);
            Assert.Equal(ReadinessStepOutcome.Completed, read.Outcome);
            Assert.Equal(1, clock.ActiveTimers);
        }
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task EqualStepAndAttemptLimitsReportOverallExpiry()
    {
        var clock = new ManualClock();
        using var deadline = Start(clock);
        var held = new HeldRead<int>(7);
        try
        {
            var read = deadline.ObserveAsync(held.ReadAsync, upperBound: Ms(100)).AsTask();
            await held.Entered.Task.WaitAsync(Watchdog);
            Assert.Equal(1, clock.ActiveTimers);
            clock.AdvanceTo(100);
            Assert.Equal(ReadinessStepOutcome.Expired, (await read.WaitAsync(Watchdog)).Outcome);
        }
        finally
        {
            held.Release.TrySetResult();
        }
        await held.Released.Task.WaitAsync(Watchdog);
    }

    [Fact]
    public async Task AnIndependentlyCancelledObserverDoesNotReportEngineStop()
    {
        var clock = new ManualClock();
        using var deadline = Start(clock);
        using var observer = new CancellationTokenSource();
        await observer.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deadline
            .ObserveAsync(_ => Task.FromCanceled<int>(observer.Token)).AsTask().WaitAsync(Watchdog));
        Assert.Equal(observer.Token, error.CancellationToken);
        Assert.False(deadline.IsExpired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public async Task NonpositiveBudgetStartsNoReadsOrTimers(int timeoutMs)
    {
        var clock = new ManualClock();
        using var deadline = ReadinessDeadline.Start(clock, Ms(timeoutMs), CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
        Assert.True(deadline.IsExpired);
        var result = await deadline.ObserveAsync<int>(_ => throw new InvalidOperationException("read started"));
        Assert.Equal(ReadinessStepOutcome.Expired, result.Outcome);
        Assert.Equal(ReadinessStepOutcome.Expired, await deadline.DelayAsync(Ms(10)));
        Assert.Equal(0, clock.ActiveTimers);
    }

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static ReadinessDeadline Start(ManualClock clock, CancellationToken stop = default) =>
        ReadinessDeadline.Start(clock, Ms(100), stop);

    private sealed class HeldRead<T>(T value)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<T> ReadAsync(CancellationToken token)
        {
            try
            {
                using var registration = token.Register(() => Cancelled.TrySetResult());
                Entered.TrySetResult();
                await Release.Task;
                return value;
            }
            finally { Released.TrySetResult(); }
        }
    }

    private sealed class ReplyWithContent(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class HeldJsonContent(HeldRead<byte[]> held) : HttpContent
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            var bytes = await held.ReadAsync(cancellationToken);
            // Model a reader that ignores cancellation once it has accepted the body read.
            await stream.WriteAsync(bytes, CancellationToken.None);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) Disposed.TrySetResult();
        }
    }

    internal sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ScheduledTimer> _timers = [];
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public int ActiveTimers
        {
            get { lock (_gate) return _timers.Count; }
        }

        public void AdvanceTo(int milliseconds, bool fireTimers = true)
        {
            ScheduledTimer[] due;
            lock (_gate)
            {
                var target = Ms(milliseconds).Ticks;
                Assert.True(target >= _timestamp);
                Interlocked.Exchange(ref _timestamp, target);
                if (!fireTimers) return;
                due = _timers.Where(timer => timer.Deadline <= target).ToArray();
            }
            foreach (var timer in due) timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            lock (_gate)
            {
                var timer = new ScheduledTimer(this, _timestamp + dueTime.Ticks, callback, state);
                _timers.Add(timer);
                return timer;
            }
        }

        private void Remove(ScheduledTimer timer)
        {
            lock (_gate) _timers.Remove(timer);
        }

        private sealed class ScheduledTimer(
            ManualClock owner, long deadline, TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            public long Deadline { get; } = deadline;

            public void Fire()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                owner.Remove(this);
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
