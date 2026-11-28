using System.Threading.Channels;

namespace Arboreal.InferenceRouter;

/// <summary>One producer reserves output bytes. The HTTP writer returns them after flush or discard.</summary>
internal sealed class HttpStreamDelivery(int limit, CancellationToken caller = default, CancellationToken shutdown = default)
{
    internal const int CommissionedDefaultLimit = 64 * 1024;

    private readonly object _gate = new();
    private readonly object _outcomeGate = new();
    private readonly TaskCompletionSource<HttpStreamOutcome> _sealed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<HttpStreamOutcome> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _space;
    private int _reserved;
    private bool _stopped;

    internal Task Sealed => _sealed.Task;
    internal Task<HttpStreamOutcome> Outcome => _outcome.Task;
    internal void Seal(HttpStreamOutcome outcome) => _sealed.TrySetResult(outcome);

    // Called only after the writer observes the producer's seal and acknowledges all bytes.
    internal void AcknowledgeFlush(CancellationTokenSource? execution, CancellationToken caller) =>
        Decide(_sealed.Task.Result, execution, caller);

    internal HttpStreamOutcome Decide(HttpStreamOutcome proposal, CancellationTokenSource? execution = null,
        CancellationToken observedCaller = default)
    {
        lock (_outcomeGate)
        {
            if (_outcome.Task.IsCompletedSuccessfully) return _outcome.Task.Result;
            // Observe cancellation at the same decision point used by flush acknowledgment and the
            // watchdog. A later callback cannot reinterpret an already selected outcome.
            if (proposal.Code != "cancelled" && (caller.IsCancellationRequested ||
                observedCaller.IsCancellationRequested || shutdown.IsCancellationRequested ||
                execution?.IsCancellationRequested == true))
                proposal = HttpStreamOutcome.Cancelled;
            _outcome.SetResult(proposal);
            return proposal;
        }
    }

    internal int ReservedBytes
    {
        get { lock (_gate) return _reserved; }
    }

    internal ValueTask WaitForSpaceAsync(CancellationToken cancellationToken) => WaitForCreditAsync(1, cancellationToken);

    private async ValueTask WaitForCreditAsync(int minimum, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                ThrowIfStopped();
                if (limit - _reserved >= minimum) return;
                wait = (_space ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal ValueTask PublishAsync(ChannelWriter<ReadOnlyMemory<byte>> writer,
        ReadOnlyMemory<byte> batch, CancellationToken cancellationToken) =>
        PublishAsync(writer, batch, fullFirstSlice: false, cancellationToken);

    internal async ValueTask PublishAsync(ChannelWriter<ReadOnlyMemory<byte>> writer,
        ReadOnlyMemory<byte> batch, bool fullFirstSlice, CancellationToken cancellationToken)
    {
        if (fullFirstSlice) await WaitForCreditAsync(Math.Min(batch.Length, limit), cancellationToken).ConfigureAwait(false);
        while (!batch.IsEmpty)
        {
            await WaitForSpaceAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfStopped();
                // Only this producer reserves credit. The writer can only increase available credit.
                var count = Math.Min(batch.Length, limit - _reserved);
                _reserved += count;
                if (!writer.TryWrite(batch[..count].ToArray()))
                {
                    _reserved -= count;
                    throw new OperationCanceledException();
                }
                batch = batch[count..];
            }
        }
    }

    internal void Release(int count)
    {
        lock (_gate)
        {
            _reserved -= count;
            _space?.TrySetResult();
            _space = null;
        }
    }

    internal void StopAndDiscard(ChannelReader<ReadOnlyMemory<byte>>? reader)
    {
        lock (_gate)
        {
            _stopped = true;
            _space?.TrySetResult();
            _space = null;
        }
        if (reader is null) return;
        while (reader.TryRead(out var chunk))
            Release(chunk.Length);
    }

    private void ThrowIfStopped()
    {
        if (_stopped) throw new OperationCanceledException();
    }
}

internal readonly record struct HttpStreamOutcome(string? Code, string Message, bool SuppressTerminalFrame = false)
{
    internal static HttpStreamOutcome Acknowledged => new(null, "");
    internal static HttpStreamOutcome UpstreamReportedError => new("engine_error", SemanticStreamObserver.ReportedError,
        SuppressTerminalFrame: true);
    internal static HttpStreamOutcome Cancelled => new("cancelled", "cancelled");
    internal static HttpStreamOutcome SlowConsumer => new("slow_consumer", "slow_consumer: downstream write made no progress for 30 seconds");
}
