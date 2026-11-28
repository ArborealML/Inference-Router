using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter;

/// <summary>Advisory evidence from one dispatcher pass. A ready observation grants no capacity.</summary>
public sealed record QueueDispatchObservation
{
    public required long ObservationId { get; init; }
    public required string Reason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AcceptedDrainScope { get; init; }
    public required IReadOnlyList<DispatchAlternative> Alternatives { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AlternativesTruncated { get; init; }
}

public sealed record DispatchAlternative(string Engine, string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope);

public sealed partial class RouterCore
{
    private const int DispatchAlternativeLimit = 8;
    private long _nextDispatchObservation;
    internal Action<RouterTicket>? BeforeDispatchWaitPublicationForTests;

    private sealed class DispatchEvaluation(QueuedRequest request, ConfigurationGeneration generation, long wakeRevision)
    {
        private readonly int _threadInflight = request.Thread.Inflight;
        private readonly int _threadLimit = request.Thread.MaxInflight;
        private readonly PrioritizedRequest _priority = request.PriorityNode;
        private List<DispatchAlternative>? _alternatives;
        private bool _truncated;
        private LocalLifecycleGroup? _acceptedGroup;
        private long _acceptedRevision;
        internal EngineRuntimeInfo[]? Routes { get; set; }
        internal bool HasThreadCapacity => _threadInflight < _threadLimit;

        internal void Record(string engine, string state, Blocker? blocker = null)
        {
            if (_alternatives?.Count == DispatchAlternativeLimit)
            {
                _truncated = true;
                return;
            }
            (_alternatives ??= []).Add(new(WireText.Ident(engine), state, blocker?.Reason, ReportScope(blocker?.Scope)));
        }

        internal void RecordUnevaluated(IEnumerable<string> engines)
        {
            foreach (var engine in engines)
            {
                Record(engine, "not_evaluated");
                if (_truncated) break;
            }
        }

        internal void AcceptDrain(LocalLifecycleGroup group, long revision, string ownerEngine)
        {
            _acceptedGroup = group;
            _acceptedRevision = revision;
            if (Routes is null || _alternatives is null) return;
            foreach (var sibling in Routes)
            {
                if (!ReferenceEquals(sibling.DispatchGroup, group) || sibling.Name == ownerEngine) continue;
                var reported = WireText.Ident(sibling.Name);
                var index = _alternatives.FindIndex(route => route.Engine == reported);
                if (index >= 0) _alternatives[index] = new(reported, "not_evaluated", null, null);
            }
        }

        internal bool IsCurrent(RouterCore router)
        {
            if (!ReferenceEquals(generation, router.CurrentGeneration) || wakeRevision != router._dispatchWake.Revision ||
                _threadInflight != request.Thread.Inflight || _threadLimit != request.Thread.MaxInflight ||
                !ReferenceEquals(_priority, request.PriorityNode) || request.CallerToken.IsCancellationRequested)
                return false;
            if (Routes is null) return true;
            foreach (var info in Routes)
            {
                var engine = generation.Engines[info.Name];
                if (!engine.IsDispatchObservationCurrent(info.DispatchObservation) ||
                    info.DispatchObservation.AdmissionClaims != engine.AdmissionClaims ||
                    info.NamedPhysicalUse != engine.HasNamedUse || info.DispatchInferenceUses != engine.InferenceUses ||
                    !ReferenceEquals(info.ManagedResident, router._hostManager?.Admission.ReadManagedResident(info.Name)))
                    return false;
                if (info.DispatchGroup is not { } group) continue;
                var expected = info.DispatchGroupRevision;
                // Only this dispatcher's accepted interest may advance an observed group.
                if (ReferenceEquals(group, _acceptedGroup) && _acceptedRevision - expected == 1)
                    expected = _acceptedRevision;
                if (group.Revision != expected) return false;
            }
            return true;
        }

        internal QueueDispatchObservation ToReport(long id, Blocker blocker, LocalLifecycleGroup? drain) => new()
        {
            ObservationId = id,
            Reason = blocker.Reason,
            Scope = ReportScope(blocker.Scope),
            AcceptedDrainScope = ReportScope(drain?.Name),
            Alternatives = _alternatives?.AsReadOnly() ?? (IReadOnlyList<DispatchAlternative>)[],
            AlternativesTruncated = _truncated,
        };
    }

    private static string? ReportScope(string? name) => name is null ? null : WireText.Ident(name);

    private void PublishDispatchWait(ModelQueue queue, QueuedRequest request, Blocker blocker, DispatchEvaluation evaluation)
    {
        var ticket = request.Ticket;
        BeforeDispatchWaitPublicationForTests?.Invoke(ticket);
        QueueDispatchObservation report;
        lock (_gate)
        {
            if (_closing || ticket.IsTerminal || !ReferenceEquals(_queues.GetValueOrDefault(ticket.Model), queue) ||
                !queue.Contains(request) || !evaluation.IsCurrent(this) || ReadQueueEnd(request) is not null) return;
            var drain = ticket.LocalDrain;
            if (drain is not null && !ReferenceEquals(drain.ReadDrainInterest().Owner, ticket)) return;
            report = evaluation.ToReport(_nextDispatchObservation + 1, blocker, drain);
            if (ticket.DispatchWait is { } previous && previous.Reason == report.Reason && previous.Scope == report.Scope &&
                previous.AcceptedDrainScope == report.AcceptedDrainScope &&
                previous.AlternativesTruncated == report.AlternativesTruncated &&
                previous.Alternatives.SequenceEqual(report.Alternatives)) return;

            _nextDispatchObservation = report.ObservationId;
            Volatile.Write(ref ticket.DispatchWait, report);
        }

        // The feed guard does not enter the router gate. Exact take and terminal paths clear the reference.
        _events.AppendIf(new RouterEvent
        {
            Kind = "request.waiting",
            ThreadId = ticket.ThreadId,
            TicketId = ticket.Id,
            Model = ticket.Model,
            Reason = FormatBlocker(new(report.Reason, report.Scope)),
            QueueDispatch = report,
        }, () =>
        {
            if (!ReferenceEquals(Volatile.Read(ref ticket.DispatchWait), report) || ticket.IsTerminal) return false;
            if (evaluation.IsCurrent(this)) return true;
            Interlocked.CompareExchange(ref ticket.DispatchWait, null, report);
            return false;
        });
    }
}
