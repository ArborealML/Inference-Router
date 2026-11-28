namespace Arboreal.InferenceRouter;

// A preparation belongs to one queued ticket, policy revision and engine instance.
// It never reserves a thread, engine request count or physical sender.
internal sealed class OwnedPrivatePreparation(EngineRuntime engine, HashSet<EngineRuntime> tried,
    IDisposable storeWriter, CancellationToken shutdown, CancellationToken caller, CancellationToken execution) : IDisposable
{
    internal EngineRuntime Engine { get; } = engine;
    internal CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown, caller, execution);
    internal HashSet<EngineRuntime> Tried { get; } = tried;
    internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal OwnedPrivateAttempt? Attempt { get; private set; }

    internal OwnedPrivateAttempt SetAttempt(OwnedPrivatePolicy policy, RouterTicket ticket, Func<HttpRequestMessage> project,
        byte[] body) => Attempt = policy.FreezeAttempt(ticket, Engine, project, body);
    internal ulong ExpectedChoices { get; set; }
    internal volatile bool Ready;
    internal void ReleaseRequest()
    {
        Attempt?.Dispose();
        Attempt = null;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected",
        Justification = "The preparing ticket exclusively owns this helper and its transferred writer lease.")]
    public void Dispose()
    {
        ReleaseRequest();
        Stop.Dispose();
        storeWriter.Dispose();
    }
}

public sealed partial class RouterCore
{
    private bool UsesOwnedJournal(RouterTicket ticket) => ticket.OwnedPolicy is not null && _ownedPolicy?.Store.UsesGroupJournals == true;

    private bool OwnedPreparedFor(RouterTicket ticket, EngineRuntime engine) => !UsesOwnedJournal(ticket) ||
        Volatile.Read(ref ticket.PrivatePreparation) is { Ready: true } preparation && ReferenceEquals(preparation.Engine, engine);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The queued ticket owns the preparation immediately. Queue retirement or the physical sender disposes it.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created",
        Justification = "The queued ticket owns the preparation immediately. Queue retirement or the physical sender disposes it.")]
    private AdmissionTake PrepareOwnedDispatch(ModelQueue queue, QueuedRequest candidate, EngineRuntime engine,
        ConfigurationGeneration generation)
    {
        var ticket = candidate.Ticket;
        OwnedPrivatePreparation preparation;
        OwnedPrivatePreparation? previous;
        lock (_gate)
        lock (queue.Gate)
        {
            if (!IsCurrentQueue(queue, candidate, generation) || !queue.Contains(candidate) ||
                !IsPendingAndThreadAdmissible(candidate) || !AllowsOwnedRoute(ticket, engine.Name) || ticket.ExecCts is not { } execution)
                return new(TakeResult.Ineligible);
            previous = ticket.PrivatePreparation;
            if (previous is { Ready: false }) return new(TakeResult.Preparing);
            var tried = previous?.Tried ?? [];
            if (!tried.Add(engine)) return new(TakeResult.PreparationExhausted);
            var writer = _ownedPolicy!.Store.RetainWriterForRetirement();
            if (writer is null) return new(TakeResult.Ineligible);
            preparation = new(engine, tried, writer, _shutdownToken, candidate.CallerToken, execution.Token);
            Volatile.Write(ref ticket.PrivatePreparation, preparation);
        }
        if (previous is not null) ScheduleOwnedRetirement(ticket, previous, queued: true);
        // Adapter entry itself may block. Even that does not run on the dispatcher or under its gates.
        _ = Task.Run(() => PrepareOwnedDispatchAsync(candidate, preparation), CancellationToken.None);
        return new(TakeResult.Preparing);
    }

    private async Task PrepareOwnedDispatchAsync(QueuedRequest candidate, OwnedPrivatePreparation preparation)
    {
        var ticket = candidate.Ticket;
        var engine = preparation.Engine;
        try
        {
            var ct = preparation.Stop.Token;
            ct.ThrowIfCancellationRequested();
            var model = engine.CanonicalModel(ticket.Model) ?? ticket.Model;
            var body = ModelRewriter.Rewrite(candidate.Request.Body, model, candidate.Request.Stream, out var expectedChoices);
            // Rewrite returns a fresh byte array. The content gets exclusive ownership of it.
            var attempt = preparation.SetAttempt(_ownedPolicy!, ticket,
                () => MakeUpstreamRequest(engine, body, CompletionSuffix(candidate), ticket, freezeBody: true), body);
            preparation.ExpectedChoices = expectedChoices;
            Volatile.Write(ref ticket.PrivateAttempt, attempt);
            Volatile.Write(ref ticket.PrivateAttemptDisposition, 1);
            await _ownedPolicy!.Store.RecordIntentAsync(attempt, ct).ConfigureAwait(false);
            attempt.Request.Options.Set(OwnedPrivateAttempt.RequestOption, attempt);
            ct.ThrowIfCancellationRequested();
            lock (_gate)
                if (!ticket.IsTerminal && ReferenceEquals(ticket.PrivatePreparation, preparation)) preparation.Ready = true;
        }
        catch (Exception error) when (OwnedPrivatePolicy.IsRecoverableAuthorityFailure(error))
        {
            var code = error is OperationCanceledException ? "cancelled" :
                (error as RouterRequestException)?.Code ?? "policy_group_unavailable";
            if (ClaimQueuedEnd(ticket, code) == QueueEndClaim.Removed) SettleQueueEnd(ticket, code, null);
        }
        finally
        {
            preparation.Finished.TrySetResult();
            SignalDispatch();
        }
    }

    private void RetireOwnedPreparation(RouterTicket ticket, bool queued = false)
    {
        if (Interlocked.Exchange(ref ticket.PrivatePreparation, null) is not { } preparation) return;
        ScheduleOwnedRetirement(ticket, preparation, queued);
    }

    internal Func<RouterTicket, Task>? BeforeOwnedRetirementForTests { get; set; }

    private void ScheduleOwnedRetirement(RouterTicket ticket, OwnedPrivatePreparation preparation, bool queued) =>
        _ = Task.Run(() => RetireOwnedPreparationAsync(ticket, preparation, queued), CancellationToken.None);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected",
        Justification = "Detaching a preparation from its ticket transfers exclusive ownership of its CTS and HTTP request to retirement.")]
    private async Task RetireOwnedPreparationAsync(RouterTicket ticket, OwnedPrivatePreparation preparation, bool queued)
    {
        try
        {
            using (preparation)
            {
                if (BeforeOwnedRetirementForTests is { } before) await before(ticket).ConfigureAwait(false);
                if (queued) await preparation.Stop.CancelAsync().ConfigureAwait(false);
                await preparation.Finished.Task.ConfigureAwait(false);
                if (preparation.Attempt is { } attempt)
                {
                    Interlocked.CompareExchange(ref ticket.PrivateAttempt, null, attempt);
                    preparation.ReleaseRequest();
                    await _ownedPolicy!.Store.FinishIntentAsync(attempt).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (OwnedPrivatePolicy.IsRecoverableAuthorityFailure(error))
        {
            _events.Append(new RouterEvent
            {
                Kind = "request.cleanup_failed", TicketId = ticket.Id, ThreadId = ticket.ThreadId,
                OwnerGroup = ticket.OwnedPolicy?.Key, Reason = "owned policy intent cleanup was not confirmed",
            });
        }
    }
}
