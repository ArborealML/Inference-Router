namespace Arboreal.InferenceRouter;

public sealed partial class RouterCore
{
    private const int DirectChatDefaultStreamLimitBytes = 64 * 1024;

    private async Task RunControlledDirectChatAsync(QueuedRequest candidate, EngineRuntime engine, CancellationToken ct)
    {
        var ticket = candidate.Ticket;
        var provider = engine.DirectChat!;
        if (_directChat is null || !provider.Models.TryGetValue(ticket.Model, out var model)) throw ControlledDirectChat.Unavailable();
        var body = DirectChatProtocol.Project(candidate.Request, provider, model, out var choices);
        if (!engine.TryBeginForward(ct)) throw new OperationCanceledException(ct);
        using var validation = new DirectChatResponse(provider, model, ResponseTimeoutClockForTests, choices, ct);
        using var watchStop = new CancellationTokenSource();
        var watch = ObserveDirectDeadlineAsync();
        try
        {
            await using var use = await _directChat.SendAsync(provider, model, ticket, body, validation.Token).ConfigureAwait(false);
            var response = use.Response;
            validation.Bind(response);
            if (!response.IsSuccessStatusCode || !ticket.IsStreamingRequest)
            {
                if (response.IsSuccessStatusCode && !string.Equals(response.Content.Headers.ContentType?.MediaType,
                    "application/json", StringComparison.OrdinalIgnoreCase)) throw DirectChatProtocol.BadResponse();
                var complete = await validation.ReadBufferedAsync(response).ConfigureAwait(false);
                ticket.CommercialEvidence = validation.Evidence();
                Complete(ticket, complete);
                return;
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw DirectChatProtocol.BadResponse();
            var observer = StartStreamObservation(ticket, model.ServedId, choices);
            validation.Observe(observer);
            try
            {
                var streamError = await PumpStreamAsync(engine, ticket, response.Content, ticket.StreamChannel!, observer,
                    gate: null, ct, directChat: validation).ConfigureAwait(false);
                if (streamError is not null) throw DirectChatProtocol.BadResponse();
                ticket.CommercialEvidence = validation.Evidence();
                if (ticket.HttpDelivery is { } delivery) delivery.Seal(HttpStreamOutcome.Acknowledged);
                else Complete(ticket);
            }
            catch
            {
                observer.End(StreamInputEnd.Fault);
                throw;
            }
        }
        catch (Exception error) when (!ct.IsCancellationRequested && validation.TimedOut &&
            OwnedPrivatePolicy.IsRecoverableAuthorityFailure(error))
        {
            throw validation.Timeout();
        }
        finally
        {
            if (validation.Metrics is not null) ticket.CommercialEvidence = validation.Evidence();
            engine.EndForward();
            await watchStop.CancelAsync().ConfigureAwait(false);
            await watch.ConfigureAwait(false);
        }

        async Task ObserveDirectDeadlineAsync()
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(validation.DeadlineToken, ct, watchStop.Token);
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
            if (!ct.IsCancellationRequested && validation.TimedOut)
            {
                var failure = validation.Timeout();
                if (validation.Metrics is not null) ticket.CommercialEvidence = validation.Evidence();
                Fail(ticket, failure.Code, failure.Message);
            }
        }
    }
}
