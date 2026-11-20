namespace Arboreal.InferenceRouter;

public sealed partial class RouterCore
{
    internal enum DefaultStreamReadObservation
    {
        Armed,
        CoreCancellation,
        OtherCancellation,
        QualifiedResult,
    }

    internal Action<string, long, DefaultStreamReadObservation>? DefaultStreamReadObservedForTests;

    // One observation window per pending raw read. Physical read completion and token
    // callbacks can run concurrently, so only the source observed at this cut is attributable.
    internal sealed class DefaultStreamShutdown(CancellationToken linked, CancellationToken core,
        CancellationToken execution, CancellationToken caller,
        Action<long, DefaultStreamReadObservation>? observe = null)
    {
        private enum Source { None, Core, Other }

        private readonly Lock _gate = new();
        private long _readNumber;
        private long _armed;
        private Source _source;

        internal bool PublishedBlock { get; set; }
        internal bool Qualified { get; private set; }

        internal CancellationTokenRegistration Register() =>
            linked.UnsafeRegister(static state => ((DefaultStreamShutdown)state!).ObserveLinkedCancellation(), this);

        internal long TryArm()
        {
            long arm;
            lock (_gate)
            {
                if (_armed != 0 || linked.IsCancellationRequested || core.IsCancellationRequested ||
                    execution.IsCancellationRequested || caller.IsCancellationRequested)
                    return 0;

                _source = Source.None;
                arm = _armed = ++_readNumber;
            }
            observe?.Invoke(arm, DefaultStreamReadObservation.Armed);
            return arm;
        }

        internal void ObserveLinkedCancellation()
        {
            long arm;
            Source source;
            lock (_gate)
            {
                if (_armed == 0 || _source != Source.None || !linked.IsCancellationRequested) return;
                arm = _armed;
                source = _source = IsCoreOnly() ? Source.Core : Source.Other;
            }
            observe?.Invoke(arm, source == Source.Core
                ? DefaultStreamReadObservation.CoreCancellation : DefaultStreamReadObservation.OtherCancellation);
        }

        internal bool ObserveResult(long arm, int bytes, Exception? error = null)
        {
            bool qualified;
            lock (_gate)
            {
                if (arm == 0 || _armed != arm) return false;
                var matchingCancellation = error is OperationCanceledException cancellation &&
                    cancellation.CancellationToken == linked && linked.IsCancellationRequested;
                var fromCore = _source == Source.Core ||
                    (_source == Source.None && matchingCancellation && IsCoreOnly());
                qualified = fromCore && (bytes > 0 || matchingCancellation);
                _armed = 0;
                if (qualified) Qualified = true;
            }
            if (qualified) observe?.Invoke(arm, DefaultStreamReadObservation.QualifiedResult);
            return qualified;
        }

        private bool IsCoreOnly() => core.IsCancellationRequested &&
            !execution.IsCancellationRequested && !caller.IsCancellationRequested;
    }
}
