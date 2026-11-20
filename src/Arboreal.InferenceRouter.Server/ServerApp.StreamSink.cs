using System.IO.Pipelines;

namespace Arboreal.InferenceRouter.Server;

public static partial class ServerApp
{
    private readonly record struct InferenceStreamSink(HttpContext Context, PipeWriter Writer,
        RouterTicket Ticket, RouterCore? Router, TimeProvider Clock)
    {
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

        internal async ValueTask WritePayloadAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var delivery = Ticket.HttpDelivery;
            if (delivery is null)
            {
                await Writer.WriteAsync(bytes, ct).ConfigureAwait(false);
                return;
            }

            try { await WriteWatchedAsync(bytes, ct).ConfigureAwait(false); }
            finally { delivery.Release(bytes.Length); }
        }

        internal ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) =>
            Ticket.HttpDelivery is null
                ? new ValueTask(AirJson.WriteFrameAsync(Writer, frame, ct))
                : WriteWatchedAsync(frame, ct);

        private async ValueTask WriteWatchedAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var write = Writer.WriteAsync(bytes, ct);
            FlushResult result;
            if (write.IsCompletedSuccessfully)
            {
                result = write.Result;
            }
            else
            {
                var pending = write.AsTask();
                try
                {
                    result = await pending.WaitAsync(WriteTimeout, Clock, ct).ConfigureAwait(false);
                }
                catch (TimeoutException) when (!pending.IsCompleted)
                {
                    if (!ct.IsCancellationRequested)
                        Router?.FailSlowStream(Ticket, ct);
                    await AbortPendingWriteAsync(pending).ConfigureAwait(false);
                    throw new OperationCanceledException(ct);
                }
                catch (OperationCanceledException) when (!pending.IsCompleted)
                {
                    await AbortPendingWriteAsync(pending).ConfigureAwait(false);
                    throw;
                }
                catch (TimeoutException)
                {
                    // A completed write wins a race with its own deadline.
                    result = await pending.ConfigureAwait(false);
                }
            }
            if (result.IsCanceled || result.IsCompleted)
                throw new OperationCanceledException(ct);
        }

        private async Task AbortPendingWriteAsync(Task<FlushResult> pending)
        {
            Context.Abort();
            Writer.CancelPendingFlush();
            // The writer may still own the bytes. Return their credit only after it lets go.
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
