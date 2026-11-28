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

            try
            {
                var started = Clock.GetTimestamp();
                ObserveKeepalive(await FinishKeepaliveAsync(Writer.WriteAsync(frame, ct), frame, started, ct).ConfigureAwait(false), ct);
                started = Clock.GetTimestamp();
                ObserveKeepalive(await FinishKeepaliveAsync(Writer.FlushAsync(ct), frame, started, ct).ConfigureAwait(false), ct);
            }
            catch
            {
                Ticket.MarkHttpOutputEnded();
                throw;
            }
        }

        private void ObserveKeepalive(FlushResult result, CancellationToken ct)
        {
            Output!.Observe(result);
            if (!result.IsCanceled && !result.IsCompleted) return;
            Ticket.MarkHttpOutputEnded();
            if (Ticket.HttpDelivery is not null) throw new OperationCanceledException(ct);
        }

        private async ValueTask<FlushResult> FinishKeepaliveAsync(ValueTask<FlushResult> write,
            ReadOnlyMemory<byte> frame, long started, CancellationToken ct)
        {
            if (write.IsCompletedSuccessfully) return write.Result;

            var pending = write.AsTask();
            if (!pending.IsCompleted && !Ticket.StreamReady.IsCompleted)
                await Task.WhenAny(pending, Ticket.StreamReady).ConfigureAwait(false);

            // A keepalive can start before selection. Delivery is installed before StreamReady
            // publishes a commissioned reader. Apply the original write's remaining budget.
            if (Ticket.HttpDelivery is not { } delivery) return await pending.ConfigureAwait(false);
            if (Ticket.HttpOutputEnded && !pending.IsCompleted)
            {
                _ = RetirePendingWriteAsync(Writer, pending, delivery, frame, charged: false);
                Writer.CancelPendingFlush();
                throw new OperationCanceledException(ct);
            }
            var remaining = WriteTimeout - Clock.GetElapsedTime(started);
            return await ObserveWriteAsync(delivery, new ValueTask<FlushResult>(pending), frame, charged: false,
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct).ConfigureAwait(false);
        }

        private async ValueTask WriteWatchedAsync(HttpStreamDelivery delivery, ReadOnlyMemory<byte> bytes,
            bool charged, CancellationToken ct)
        {
            ValueTask<FlushResult> write;
            try { write = Writer.WriteAsync(bytes, ct); }
            catch
            {
                if (charged) delivery.Release(bytes.Length);
                throw;
            }
            await ObserveWriteAsync(delivery, write, bytes, charged, WriteTimeout, ct).ConfigureAwait(false);
        }

        private async ValueTask<FlushResult> ObserveWriteAsync(HttpStreamDelivery delivery, ValueTask<FlushResult> write,
            ReadOnlyMemory<byte> bytes, bool charged, TimeSpan remaining, CancellationToken ct)
        {
            var releaseHere = charged;
            try
            {
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
                        result = await pending.WaitAsync(remaining, Clock, ct).ConfigureAwait(false);
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
                if (result.IsCanceled || result.IsCompleted) throw new OperationCanceledException(ct);
                return result;
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
