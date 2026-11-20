using System.Globalization;

namespace Arboreal.InferenceRouter;

public sealed partial class RouterCore
{
    internal TimeProvider StreamIdleClockForTests = TimeProvider.System;
    internal Action<string>? AfterOptInTransportTaskForTests;
    internal Action? BeforeSlowStreamDecisionForTests;
    internal Func<string, Task>? BeforeOptInStreamSettlementForTests;
    internal Func<string, Task>? BeforeOptInTransportStartForTests;

    private async Task ExecuteOptInStreamAsync(QueuedRequest candidate, EngineRuntime engine,
        SwitchServingProof? servingProof, ExactTicketAdmission.Invocation? invocation, CancellationToken execution)
    {
        var ticket = candidate.Ticket;
        // The delegate owns transport cancellation and must enter cleanup even if the caller already stopped.
        var physical = Task.Run(() => RunOptInStreamTransportAsync(candidate, engine, servingProof, invocation, execution),
            CancellationToken.None);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownToken, execution, candidate.CallerToken);
        var completion = ticket.CompletionTask;
        var delivery = ticket.HttpDelivery;
        try
        {
            if (delivery is not null)
            {
                using var cancellation = wait.Token.UnsafeRegister(static state =>
                {
                    ((HttpStreamDelivery)state!).Decide(HttpStreamOutcome.Cancelled);
                }, delivery);
                var outcome = await delivery.Outcome.ConfigureAwait(false);
                if (BeforeOptInStreamSettlementForTests is { } before)
                    await before(ticket.Id).ConfigureAwait(false);
                if (outcome.Code is { } code)
                    Fail(ticket, code, outcome.Message, outcome.SuppressIdleTerminal);
                else
                    Complete(ticket);
                return;
            }

            await Task.WhenAny(completion, Task.Delay(Timeout.InfiniteTimeSpan, wait.Token)).ConfigureAwait(false);
            if (!ticket.IsTerminal && wait.IsCancellationRequested)
                Fail(ticket, "cancelled", "cancelled");
        }
        finally
        {
            await wait.CancelAsync().ConfigureAwait(false);
            // The transport task is the sole owner through conclusive cleanup. Faults are observed
            // without retaining the finished logical execution or delaying its capacity release.
            _ = ObserveOptInCleanupAsync(physical, ticket);
        }
    }

    private async Task RunOptInStreamTransportAsync(QueuedRequest candidate, EngineRuntime engine,
        SwitchServingProof? servingProof, ExactTicketAdmission.Invocation? invocation, CancellationToken execution)
    {
        using var transport = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownToken, execution, candidate.CallerToken);
        var ticket = candidate.Ticket;
        var ct = transport.Token;
        var attempted = false;
        try
        {
            if (BeforeOptInTransportStartForTests is { } before)
                await before(ticket.Id).ConfigureAwait(false);
            if (ticket.IsTerminal) return;
            var body = ModelRewriter.Rewrite(candidate.Request.Body, engine.CanonicalModel(ticket.Model) ?? ticket.Model);
            var suffix = LooksLikeCompletion(candidate.Request.Body) ? "completions" : "chat/completions";
            if (invocation is not null)
            {
                using var request = MakeUpstreamRequest(engine, body, suffix);
                await invocation.RunAsync(request, response =>
                {
                    // A borrowed response is evidence of an actual send. Returning from abort
                    // or a failed offer alone says nothing about whether inference was attempted.
                    attempted = true;
                    return ConsumeAsync(response, gate: null);
                },
                    error => ReportFailure(error, gate: null), ct, invocation.VerifiedCredentialEpoch).ConfigureAwait(false);
                return;
            }

            ct.ThrowIfCancellationRequested();
            if (!engine.TryBeginForward(ct))
            {
                Fail(ticket, "cancelled", "cancelled");
                return;
            }
            try
            {
                if (engine.AfterForwardAdmissionForTests is { } afterAdmission)
                    await afterAdmission().ConfigureAwait(false);
                using var gate = engine.ResponseTimeout is { } ceiling
                    ? new ResponseGate(ceiling, ResponseTimeoutClockForTests) : null;
                using var headerWait = gate is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, gate.Token);
                ct.ThrowIfCancellationRequested();
                attempted = true;
                var send = SendAsync(engine, body, suffix, ticket.Model, servingProof, ct);
                HttpResponseMessage response;
                try
                {
                    response = await send.WaitAsync(headerWait?.Token ?? ct).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    ReportFailure(error, gate);
                    var cancel = transport.CancelAsync();
                    await ((Task)send).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    if (send.IsCompletedSuccessfully)
                        send.Result.Dispose();
                    await cancel.ConfigureAwait(false);
                    return;
                }
                using (response)
                    await ConsumeAsync(response, gate).ConfigureAwait(false);
            }
            finally
            {
                engine.EndForward();
            }
        }
        catch (Exception error) when (!ticket.IsTerminal && ticket.HttpDelivery?.Sealed.IsCompletedSuccessfully != true)
        {
            ReportFailure(error, gate: null);
        }
        finally
        {
            await AbortUnstartedInvocationAsync(ticket, invocation).ConfigureAwait(false);
            if (attempted) engine.RecordPrefix(candidate.Messages());
        }

        async Task ConsumeAsync(HttpResponseMessage response, ResponseGate? gate)
        {
            ct.ThrowIfCancellationRequested();
            if (ticket.IsTerminal) return;
            var lifetime = new StreamReadLifetime(engine.StreamIdleTimeout, StreamIdleClockForTests);
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    await FinishWithResponseAsync(engine, ticket, response, candidate, gate, ct).ConfigureAwait(false);
                    return;
                }
                var error = await PumpStreamAsync(engine, ticket, response.Content, ticket.StreamChannel!, gate, ct,
                    lifetime).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                gate?.Pass(); // Clean empty EOF settles the first-byte phase before output drain.
                if (error is not null)
                    Fail(ticket, "engine_error", error);
                else if (ticket.HttpDelivery is { } delivery)
                    delivery.Seal();
                else
                    Complete(ticket);
            }
            catch (StreamIdleException) when (!ct.IsCancellationRequested)
            {
                Fail(ticket, "engine_timeout",
                    $"upstream stream was idle for {engine.StreamIdleTimeout!.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms",
                    suppressIdleTerminal: lifetime.SawDone);
            }
            catch (Exception error)
            {
                ReportFailure(error, gate);
            }
            finally
            {
                if (lifetime.Pending is { } pending)
                {
                    var cancel = transport.CancelAsync();
                    // For verified forwarding, keep the response borrowed until this operation
                    // settles. The pump has unwound, dropping its frame stage and validated batch.
                    await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    await cancel.ConfigureAwait(false);
                }
            }
        }

        void ReportFailure(Exception error, ResponseGate? gate)
        {
            if (ticket.IsTerminal) return;
            if (ct.IsCancellationRequested)
                Fail(ticket, "cancelled", "cancelled");
            else if (gate?.Tripped == true)
                Fail(ticket, "engine_timeout",
                    $"engine {WireText.Ident(engine.Name)} sent no response to {WireText.Ident(ticket.Model)} within " +
                    $"{engine.ResponseTimeout!.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
            else if (error is OperationCanceledException)
                Fail(ticket, "cancelled", "cancelled");
            else
                Fail(ticket, "engine_unreachable", error.Message);
        }
    }

    private async Task ObserveOptInCleanupAsync(Task physical, RouterTicket ticket)
    {
        try { await physical.ConfigureAwait(false); }
        catch (Exception)
        {
            _events.Append(new RouterEvent
            {
                Kind = "request.cleanup_failed",
                ThreadId = ticket.ThreadId,
                TicketId = ticket.Id,
                Engine = ticket.Engine,
                Model = ticket.Model,
                Reason = "stream transport cleanup failed",
            });
        }
        finally
        {
            // Observation starts only after logical settlement. Disposal thus follows both
            // the HTTP delivery decision and the last physical user of the execution token.
            ReleaseExecutionCancellation(ticket);
            AfterOptInTransportTaskForTests?.Invoke(ticket.Id);
        }
    }

    private sealed class StreamIdleException : Exception;

    private sealed class StreamReadLifetime(TimeSpan? idle, TimeProvider clock)
    {
#pragma warning disable IDISP006, IDISP003 // The stored task is only awaited; it never creates a disposable wait handle.
        internal Task? Pending { get; private set; }
        internal bool SawDone { get; private set; }

        internal Task<Stream> AcquireAsync(HttpContent content, CancellationToken ct) =>
            WaitAsync(content.ReadAsStreamAsync(ct), null, ct);

        internal ValueTask<int> ReadAsync(Stream stream, Memory<byte> scratch, bool afterFirstByte, CancellationToken ct)
        {
            var read = stream.ReadAsync(scratch, ct);
            return read.IsCompletedSuccessfully
                ? read
                : new ValueTask<int>(WaitAsync(read.AsTask(), afterFirstByte ? idle : null, ct));
        }

        private async Task<T> WaitAsync<T>(Task<T> operation, TimeSpan? deadline, CancellationToken ct)
        {
            Pending = operation;
            try
            {
                if (deadline is not { } maxWait)
                    return await operation.WaitAsync(ct).ConfigureAwait(false);
                try { return await operation.WaitAsync(maxWait, clock, ct).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    // A completed read wins its deadline tie. Let upstream exceptions keep their own mapping.
                    if (operation.IsCompleted) return await operation.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    throw new StreamIdleException();
                }
            }
            finally
            {
                if (operation.IsCompleted)
                {
                    _ = operation.Exception;
                    Pending = null;
                }
            }
        }
#pragma warning restore IDISP006, IDISP003

        internal void ObservePublished(ReadOnlySpan<byte> complete)
        {
            if (idle is null || SawDone || complete.IndexOf("[DONE]"u8) < 0) return;
            var dataLines = 0;
            var sentinel = false;
            while (!complete.IsEmpty)
            {
                var end = complete.IndexOfAny((byte)'\r', (byte)'\n');
                if (end < 0) break;
                var line = complete[..end];
                var ending = complete[end];
                complete = complete[(end + 1)..];
                if (ending == (byte)'\r' && !complete.IsEmpty && complete[0] == (byte)'\n')
                    complete = complete[1..];
                if (line.IsEmpty)
                {
                    if (sentinel && dataLines == 1)
                    {
                        SawDone = true;
                        return;
                    }
                    dataLines = 0;
                    sentinel = false;
                }
                else if (line.StartsWith("data:"u8) || line.SequenceEqual("data"u8))
                {
                    dataLines++;
                    if (line.Length <= 5) continue;
                    var value = line[5..];
                    if (value[0] == (byte)' ') value = value[1..];
                    sentinel |= value.SequenceEqual("[DONE]"u8);
                }
            }
        }
    }
}
