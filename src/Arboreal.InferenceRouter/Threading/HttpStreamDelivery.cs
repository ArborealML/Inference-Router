using System.Threading.Channels;

namespace Arboreal.InferenceRouter;

/// <summary>One producer reserves output bytes. The HTTP writer returns them after flush or discard.</summary>
internal sealed class HttpStreamDelivery(int limit)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _sealed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _space;
    private int _reserved;
    private bool _stopped;

    internal Task Sealed => _sealed.Task;
    internal Task Acknowledged => _acknowledged.Task;
    internal void Seal() => _sealed.TrySetResult();
    internal void Acknowledge() => _acknowledged.TrySetResult();

    internal int ReservedBytes
    {
        get { lock (_gate) return _reserved; }
    }

    internal async ValueTask WaitForSpaceAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (_gate)
        {
            ThrowIfStopped();
            if (_reserved < limit) return;
            wait = (_space ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate) ThrowIfStopped();
    }

    internal async ValueTask PublishAsync(ChannelWriter<ReadOnlyMemory<byte>> writer,
        ReadOnlyMemory<byte> batch, CancellationToken cancellationToken)
    {
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
