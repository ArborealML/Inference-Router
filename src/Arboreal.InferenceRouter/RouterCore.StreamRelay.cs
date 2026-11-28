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
                    Fail(ticket, code, outcome.Message, outcome.SuppressTerminalFrame);
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

    private async Task ExecuteCommissionedAsync(QueuedRequest candidate, EngineRuntime engine,
        ExactTicketAdmission.Invocation invocation, CancellationToken execution)
    {
        var ticket = candidate.Ticket;
        var physical = Task.Run(RunAsync, CancellationToken.None);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, execution, candidate.CallerToken);
        try
        {
            await Task.WhenAny(ticket.CompletionTask, Task.Delay(Timeout.InfiniteTimeSpan, wait.Token)).ConfigureAwait(false);
            if (!ticket.IsTerminal && wait.IsCancellationRequested) Fail(ticket, "cancelled", "cancelled");
        }
        finally
        {
            await wait.CancelAsync().ConfigureAwait(false);
            _ = ObserveOptInCleanupAsync(physical, ticket);
        }

        async Task RunAsync()
        {
            try
            {
                using var transport = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, execution, candidate.CallerToken);
                using var gate = engine.ResponseTimeout is { } ceiling ?
                    new ResponseGate(ceiling, ResponseTimeoutClockForTests) : null;
                var body = ModelRewriter.Rewrite(candidate.Request.Body, ticket.Model, candidate.Request.Stream, out var expectedChoices);
                var suffix = LooksLikeCompletion(candidate.Request.Body) ? "completions" : "chat/completions";
                using var request = MakeUpstreamRequest(engine, body, suffix, ticket);
                var watchdog = WatchCommissionedAnswerAsync(ticket, engine, transport, gate);
                try
                {
                    await invocation.RunAsync(request, response =>
                        FinishWithResponseAsync(engine, candidate, response, ticket.Model, expectedChoices, gate,
                            defaultExecutionToken: null, transport.Token),
                        error =>
                        {
                            if (gate?.Tripped == true && !transport.IsCancellationRequested)
                                FailCommissionedTimeout(ticket, engine);
                            else ReportFailure(error);
                        }, transport.Token, invocation.VerifiedCredentialEpoch).ConfigureAwait(false);
                }
                finally
                {
                    if (gate?.Tripped != true) gate?.Pass();
                    await watchdog.ConfigureAwait(false);
                }
            }
            catch (Exception error) { ReportFailure(error); }
            finally
            {
                await AbortUnstartedInvocationAsync(ticket, invocation).ConfigureAwait(false);
                engine.RecordPrefix(candidate.Messages());
            }
        }

        void ReportFailure(Exception error)
        {
            if (error is OperationCanceledException) Fail(ticket, "cancelled", "cancelled");
            else if (error is RouterRequestException refusal) Fail(ticket, refusal.Code, refusal.Message);
            else Fail(ticket, "engine_unreachable", "The verified request could not be delivered.");
        }
    }

    private async Task WatchCommissionedAnswerAsync(RouterTicket ticket, EngineRuntime engine,
        CancellationTokenSource transport, ResponseGate? gate)
    {
        if (gate is null) return;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(transport.Token, gate.Token);
        await Task.Delay(Timeout.InfiniteTimeSpan, wait.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!gate.Tripped || ticket.IsTerminal || transport.IsCancellationRequested) return;
        FailCommissionedTimeout(ticket, engine);
        await transport.CancelAsync().ConfigureAwait(false);
    }

    private void FailCommissionedTimeout(RouterTicket ticket, EngineRuntime engine)
    {
        ticket.ObserveStreamRefusal();
        Fail(ticket, "engine_timeout",
            $"engine {WireText.Ident(engine.Name)} sent no response to {WireText.Ident(ticket.Model)} within " +
            $"{engine.ResponseTimeout!.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
    }

    private async Task RunOptInStreamTransportAsync(QueuedRequest candidate, EngineRuntime engine,
        SwitchServingProof? servingProof, ExactTicketAdmission.Invocation? invocation, CancellationToken execution)
    {
        using var transport = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownToken, execution, candidate.CallerToken);
        var ticket = candidate.Ticket;
        var ct = transport.Token;
        var attempted = false;
        var upstreamModel = engine.CanonicalModel(ticket.Model) ?? ticket.Model;
        ulong expectedChoices = 0;
        try
        {
            if (BeforeOptInTransportStartForTests is { } before)
                await before(ticket.Id).ConfigureAwait(false);
            if (ticket.IsTerminal || invocation?.IsCommissioned == true && ticket.HttpOutputEnded) return;
            var body = ModelRewriter.Rewrite(candidate.Request.Body, upstreamModel, stream: true, out expectedChoices);
            var suffix = CompletionSuffix(candidate);
            if (invocation is not null)
            {
                using var request = MakeUpstreamRequest(engine, body, suffix, ticket);
                using var verifiedGate = invocation.IsCommissioned && engine.ResponseTimeout is { } ceiling ?
                    new ResponseGate(ceiling, ResponseTimeoutClockForTests) : null;
                var watchdog = WatchCommissionedAnswerAsync(ticket, engine, transport, verifiedGate);
                try
                {
                    await invocation.RunAsync(request, async response =>
                    {
                        // A borrowed response belongs to the upstream exchange through the sink's decision.
                        attempted = true;
                        await ConsumeAsync(response, verifiedGate).ConfigureAwait(false);
                        if (invocation.IsCommissioned && ticket.HttpDelivery is { } delivery)
                            await delivery.Outcome.ConfigureAwait(false);
                    }, error => ReportFailure(error, verifiedGate), ct, invocation.VerifiedCredentialEpoch).ConfigureAwait(false);
                }
                finally
                {
                    if (verifiedGate?.Tripped != true) verifiedGate?.Pass();
                    await watchdog.ConfigureAwait(false);
                }
                return;
            }

            ct.ThrowIfCancellationRequested();
            if (!engine.TryBeginForward(ct, candidate.Request.Options?.Profile is not null))
            {
                Fail(ticket, "cancelled", "cancelled");
                return;
            }
            try
            {
                if (engine.AfterForwardAdmissionForTests is { } afterAdmission)
                    await afterAdmission().ConfigureAwait(false);
                await engine.VerifyRequestedProfileAsync(candidate, ct).ConfigureAwait(false);
                using var gate = engine.ResponseTimeout is { } ceiling
                    ? new ResponseGate(ceiling, ResponseTimeoutClockForTests) : null;
                using var headerWait = gate is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, gate.Token);
                ct.ThrowIfCancellationRequested();
                attempted = true;
                var send = SendAsync(engine, body, suffix, candidate, servingProof, ct);
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
                engine.EndForward(candidate.Request.Options?.Profile is not null);
            }
        }
        catch (Exception error) when (!ticket.IsTerminal && ticket.HttpDelivery?.Sealed.IsCompletedSuccessfully != true)
        {
            ReportFailure(error, gate: null);
        }
        finally
        {
            await AbortUnstartedInvocationAsync(ticket, invocation).ConfigureAwait(false);
            candidate.LocalLease?.Release();
            if (attempted && !OwnedPolicyEnabled) engine.RecordPrefix(candidate.Messages());
            await FinishOwnedPrivateAttemptAsync(ticket).ConfigureAwait(false);
        }

        async Task ConsumeAsync(HttpResponseMessage response, ResponseGate? gate)
        {
            ct.ThrowIfCancellationRequested();
            if (ticket.IsTerminal) return;
            var lifetime = new StreamReadLifetime(engine.StreamIdleTimeout, StreamIdleClockForTests);
            SemanticStreamObserver? observer = null;
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    await FinishWithResponseAsync(engine, candidate, response, upstreamModel, expectedChoices, gate,
                        defaultExecutionToken: null, ct).ConfigureAwait(false);
                    return;
                }
                observer = StartStreamObservation(ticket, upstreamModel, expectedChoices);
                var error = await PumpStreamAsync(engine, ticket, response.Content, ticket.StreamChannel!, observer, gate,
                    ct, lifetime).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (error is null && observer.SawDone && !observer.SawError) ticket.PrivateAttempt?.ObserveTerminal();
                gate?.Pass(); // Clean empty EOF settles the first-byte phase before output drain.
                if (error is not null)
                    Fail(ticket, "engine_error", error);
                else if (ticket.HttpDelivery is { } delivery)
                    delivery.Seal(observer.SawError ? HttpStreamOutcome.UpstreamReportedError : HttpStreamOutcome.Acknowledged);
                else if (observer.SawError)
                    Fail(ticket, "engine_error", SemanticStreamObserver.ReportedError, suppressTerminalFrame: true);
                else
                    Complete(ticket);
            }
            catch (StreamIdleException) when (!ct.IsCancellationRequested)
            {
                Fail(ticket, "engine_timeout",
                    $"upstream stream was idle for {engine.StreamIdleTimeout!.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms",
                    suppressTerminalFrame: observer?.SawDone == true);
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
            {
                ticket.ObserveStreamRefusal();
                Fail(ticket, "engine_timeout",
                    $"engine {WireText.Ident(engine.Name)} sent no response to {WireText.Ident(ticket.Model)} within " +
                    $"{engine.ResponseTimeout!.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
            }
            else if (error is OperationCanceledException)
                Fail(ticket, "cancelled", "cancelled");
            else if (error is RouterRequestException refusal)
                Fail(ticket, refusal.Code, refusal.Message);
            else
                Fail(ticket, "engine_unreachable", OwnedPolicyEnabled ? "the private upstream exchange failed" : error.Message);
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
                ThreadId = ticket.ThreadId, OwnerGroup = ticket.OwnedPolicy?.Key,
                TicketId = ticket.Id,
                Engine = ticket.Engine,
                Model = ticket.Model,
                Reason = "transport cleanup failed",
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
    }
}
