using System.IO.Pipelines;

namespace Arboreal.InferenceRouter.Server;

public static partial class ServerApp
{
    private readonly record struct InferenceStreamSink(PipeWriter Writer,
        RouterTicket Ticket, RouterCore? Router, TimeProvider Clock, DefaultStreamOutput? Output)
    {
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

        internal async ValueTask WritePayloadAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            var delivery = Ticket.HttpDelivery;
            if (delivery is null)
            {
                var result = await Writer.WriteAsync(bytes, ct).ConfigureAwait(false);
                Output?.ObservePayload(result, bytes.Span);
                return;
            }

            await WriteWatchedAsync(delivery, bytes, charged: true, ct).ConfigureAwait(false);
        }

        internal ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct) =>
            Ticket.HttpDelivery is { } delivery
                ? WriteWatchedAsync(delivery, frame, charged: false, ct)
                : new ValueTask(AirJson.WriteFrameAsync(Writer, frame, ct));

        internal async ValueTask WriteKeepaliveAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            if (Output is null || Ticket.HttpDelivery is not null)
            {
                await WriteFrameAsync(frame, ct).ConfigureAwait(false);
                return;
            }

            Output.Observe(await Writer.WriteAsync(frame, ct).ConfigureAwait(false));
            Output.Observe(await Writer.FlushAsync(ct).ConfigureAwait(false));
        }

        private async ValueTask WriteWatchedAsync(HttpStreamDelivery delivery, ReadOnlyMemory<byte> bytes,
            bool charged, CancellationToken ct)
        {
            var releaseHere = charged;
            try
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
                        releaseHere = false;
                        _ = RetirePendingWriteAsync(Writer, pending, delivery, bytes, charged);
                        if (!ct.IsCancellationRequested)
                            Router?.FailSlowStream(Ticket, ct);
                        Writer.CancelPendingFlush();
                        throw new OperationCanceledException(ct);
                    }
                    catch (OperationCanceledException) when (!pending.IsCompleted)
                    {
                        releaseHere = false;
                        _ = RetirePendingWriteAsync(Writer, pending, delivery, bytes, charged);
                        Writer.CancelPendingFlush();
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
            finally
            {
                if (releaseHere) delivery.Release(bytes.Length);
            }
        }

        private static async Task RetirePendingWriteAsync(PipeWriter writer, Task<FlushResult> pending,
            HttpStreamDelivery delivery, ReadOnlyMemory<byte> bytes, bool charged)
        {
            // A pending task need not retain its writer. Keep the writer and slice alive until settlement.
            // The exchange can abort and discard its independent queued data while the write settles.
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (charged) delivery.Release(bytes.Length);
            GC.KeepAlive(writer);
            GC.KeepAlive(bytes);
        }
    }
}
