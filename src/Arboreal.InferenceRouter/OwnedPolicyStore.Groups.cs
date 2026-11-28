using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter;

/// <summary>A rollback-resistant compare-and-commit head for one owned policy group.</summary>
/// <remarks>Successful reads returning null confirm that this group has no journal. A group-specific
/// unknown result throws. Report a known loss of the common authority with
/// <see cref="OwnedPolicyAuthorityUnavailableException"/>. All heads belong to the deployment's
/// single-writer and restore boundary, including observers of a previous store version.</remarks>
public interface IOwnedPolicyGroupHeadAuthority
{
    ValueTask<OwnedPolicyHead?> ReadAsync(Guid store, string owner, string group, CancellationToken cancellationToken);
    ValueTask<bool> CompareAndCommitAsync(Guid store, string owner, string group, OwnedPolicyHead? previous,
        OwnedPolicyHead proposed, CancellationToken cancellationToken);
}

public sealed class OwnedPolicyAuthorityUnavailableException() : IOException("The policy authority is unavailable.");

internal sealed record OwnedSensitivityOutcome(string Operation, long Revision, bool Sensitive, long CurrentRevision,
    bool CurrentSensitive, int PossibleExposures);

internal sealed record OwnedEffectivePolicy(long Revision, bool Sensitive, string AdmissionState, int UnresolvedAttempts,
    int MaximumUnresolvedAttempts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RecoveryRequired);

public sealed partial class OwnedPolicyStore
{
    private const int MaxGroupOperations = 128;
    private const int MaxGroupIntents = 128;
    private readonly ConcurrentDictionary<PolicyGroupKey, GroupJournal> _journals = new();
    private readonly object _grantGate = new();
    private IOwnedPolicyGroupHeadAuthority? _groupAuthority;

    internal bool UsesGroupJournals => _groupAuthority is not null;
    private bool AuthorityAvailable => Volatile.Read(ref _unavailable) == 0;
    internal bool Available => AuthorityAvailable && !Volatile.Read(ref _disposing);

    // A prepared queued request owns this lease until its final retirement, even if the
    // router or its external store owner stops before the retirement worker runs.
    internal IDisposable? RetainWriterForRetirement() => EnterOperation();

    /// <summary>Opens an owned store with independently confirmed, mutable policy groups.</summary>
    public static Task<OwnedPolicyStore> OpenWithGroupAuthorityAsync(string path, IOwnedPolicyHeadAuthority rootAuthority,
        IOwnedPolicyGroupHeadAuthority groupAuthority, bool initialize = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupAuthority);
        return OpenCoreAsync(path, rootAuthority, groupAuthority, initialize, cancellationToken);
    }

    private async ValueTask<OwnedPolicyGroup?> ResolveJournalAsync(OwnedPolicyGroup root, CancellationToken cancellationToken)
    {
        if (_groupAuthority is null) return root;
        var journal = _journals.GetOrAdd(root.Key, static _ => new());
        if (Volatile.Read(ref journal.View) is { } existing) return existing.Policy;
        await journal.Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Available) return null;
            if (journal.View is { } loaded) return loaded.Policy;
            if (journal.Uncertain) return null;
            try
            {
                var store = Volatile.Read(ref _current).Head.Store;
                var head = await _groupAuthority.ReadAsync(store, root.Owner, root.Group, cancellationToken).ConfigureAwait(false);
                var snapshot = new GroupSnapshot(1, store, 0, root, [], []);
                var confirmedPath = GroupPath(root.Key, proposed: false);
                if (head is not null)
                {
                    if (head.Store != store) throw new IOException("The policy group authority returned a different store.");
                    var confirmed = await RecoverSnapshotAsync(confirmedPath, GroupPath(root.Key, proposed: true), head,
                        cancellationToken).ConfigureAwait(false);
                    snapshot = JsonSerializer.Deserialize(confirmed, OwnedPolicyJsonContext.Default.GroupSnapshot)
                        ?? throw new IOException("The policy group journal is invalid.");
                    if (!ValidGroupSnapshot(root, head, snapshot)) throw new IOException("The policy group journal is invalid.");
                }
                else if (File.Exists(confirmedPath)) throw new IOException("The policy group authority has lost its confirmed head.");
                lock (journal.Gate)
                {
                    journal.Head = head;
                    Volatile.Write(ref journal.View, snapshot);
                }
                return Available ? snapshot.Policy : null;
            }
            catch (Exception error) when (OwnedPrivatePolicy.IsRecoverableAuthorityFailure(error))
            {
                FailJournal(journal, error);
                return null;
            }
        }
        finally { journal.Writer.Release(); }
    }

    internal OwnedEffectivePolicy? ReadEffectivePolicy(PolicyGroupKey key)
    {
        if (!_journals.TryGetValue(key, out var journal) || Volatile.Read(ref journal.View) is not { } snapshot) return null;
        var full = snapshot.Intents.Length >= MaxGroupIntents;
        var state = !Available || journal.Uncertain ? "uncertain" : journal.Fenced ? "pending" : full ? "quarantined" : "confirmed";
        return new(snapshot.Policy.Revision, snapshot.Policy.Sensitive, state, snapshot.Intents.Length, MaxGroupIntents,
            full ? "external_attempt_reconciliation" : null);
    }

    private bool IsCurrentInJournal(OwnedPolicyGroup group) => _journals.TryGetValue(group.Key, out var journal) &&
        !journal.Fenced && !journal.Uncertain && ReferenceEquals(Volatile.Read(ref journal.View)?.Policy, group);

    internal async ValueTask<OwnedSensitivityOutcome> EscalateAsync(OwnedPolicyGroup group, string operation,
        long expectedRevision, CancellationToken cancellationToken)
    {
        if (_groupAuthority is null) throw new RouterRequestException("policy_capability_unavailable", "sensitivity changes are unavailable");
        using var storeOperation = EnterOperation() ?? throw GroupUnavailable();
        if (!_journals.TryGetValue(group.Key, out var journal)) throw GroupUnavailable();
        GroupSnapshot current;
        OwnedPolicyHead? prior;
        OwnedPrivateAttempt[] consumed;
        int exposures;
        lock (journal.Gate)
        {
            current = journal.View ?? throw GroupUnavailable();
            if (!Available || journal.Uncertain) throw GroupUnavailable();
            if (Array.Find(current.Operations, record => record.Operation == operation) is { } stored)
                return ReadOutcome(stored, current.Policy, expectedRevision);
            if (journal.Fenced) throw new RouterRequestException("policy_transition_pending", "a policy transition is pending");
            if (current.Policy.Revision != expectedRevision) throw OperationConflict();
            if (current.Operations.Length >= MaxGroupOperations)
                throw new RouterRequestException("policy_journal_full", "the policy operation journal is full");
            journal.Fenced = true;
            consumed = current.Policy.Sensitive ? [] :
                [.. journal.Active.Values.Where(static attempt => attempt.Consumed && !attempt.TerminalObserved)];
            // A persisted intent without a live witness may have been used by the prior process.
            exposures = current.Policy.Sensitive ? 0 : current.Intents.Count(intent =>
                !journal.Active.TryGetValue(intent.Attempt, out var active) || active.Consumed && !active.TerminalObserved);
        }

        try
        {
            foreach (var attempt in consumed) attempt.RequestCancellation();
            await journal.Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (journal.Gate)
                {
                    current = journal.View ?? throw GroupUnavailable();
                    prior = journal.Head;
                    if (!Available || journal.Uncertain) throw GroupUnavailable();
                }
                var effective = current.Policy.Sensitive ? current.Policy : current.Policy with
                {
                    Revision = checked(current.Policy.Revision + 1), Sensitive = true,
                };
                var record = new MutationRecord(operation, expectedRevision, effective.Revision, exposures);
                var proposed = current with { Sequence = checked(current.Sequence + 1), Policy = effective,
                    Operations = [.. current.Operations, record] };
                await CommitGroupAsync(journal, prior, proposed, retiring: false, cancellationToken).ConfigureAwait(false);
                return ReadOutcome(record, effective, expectedRevision);
            }
            finally { journal.Writer.Release(); }
        }
        finally
        {
            lock (journal.Gate) journal.Fenced = false;
        }
    }

    internal async ValueTask RecordIntentAsync(OwnedPrivateAttempt attempt, CancellationToken cancellationToken)
    {
        using var operation = EnterOperation() ?? throw GroupUnavailable();
        if (!_journals.TryGetValue(attempt.Key, out var journal)) throw GroupUnavailable();
        await journal.Writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GroupSnapshot current;
            OwnedPolicyHead? prior;
            lock (journal.Gate)
            {
                if (!IsCurrent(attempt.Policy.Group)) throw GroupUnavailable();
                current = journal.View!;
                prior = journal.Head;
                if (current.Intents.Length >= MaxGroupIntents)
                    throw new RouterRequestException("policy_journal_quarantined", "the owned attempt journal requires external reconciliation");
                journal.Active[attempt.Record.Attempt] = attempt;
            }
            var proposed = current with { Sequence = checked(current.Sequence + 1), Intents = [.. current.Intents, attempt.Record] };
            await CommitGroupAsync(journal, prior, proposed, retiring: false, cancellationToken).ConfigureAwait(false);
        }
        finally { journal.Writer.Release(); }
    }

    internal void ClaimIntent(OwnedPrivateAttempt attempt, CancellationToken cancellationToken)
    {
        if (!_journals.TryGetValue(attempt.Key, out var journal)) throw GroupUnavailable();
        lock (_grantGate)
        lock (journal.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(attempt.Policy.Group) || !ReferenceEquals(journal.Active.GetValueOrDefault(attempt.Record.Attempt), attempt) ||
                journal.View?.Intents.Any(intent => intent.Attempt == attempt.Record.Attempt) != true || attempt.Consumed)
                throw GroupUnavailable();
            attempt.Consume();
        }
    }

    internal async Task FinishIntentAsync(OwnedPrivateAttempt attempt)
    {
        if (!_journals.TryGetValue(attempt.Key, out var journal)) return;
        await journal.Writer.WaitAsync().ConfigureAwait(false);
        try
        {
            GroupSnapshot current;
            OwnedPolicyHead? prior;
            lock (journal.Gate)
            {
                if (!journal.Active.Remove(attempt.Record.Attempt) || !AuthorityAvailable || journal.Uncertain ||
                    journal.View is not { } view || attempt.Consumed && !attempt.TerminalObserved) return;
                current = view;
                prior = journal.Head;
            }
            if (!current.Intents.Any(intent => intent.Attempt == attempt.Record.Attempt)) return;
            var proposed = current with { Sequence = checked(current.Sequence + 1),
                Intents = Array.FindAll(current.Intents, intent => intent.Attempt != attempt.Record.Attempt) };
            try { await CommitGroupAsync(journal, prior, proposed, retiring: true, CancellationToken.None).ConfigureAwait(false); }
            catch (RouterRequestException) { /* Journal is fenced. Preserve the unresolved intent on disk. */ }
        }
        finally { journal.Writer.Release(); }
    }

    private async Task CommitGroupAsync(GroupJournal journal, OwnedPolicyHead? previous, GroupSnapshot proposed,
        bool retiring, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(proposed, OwnedPolicyJsonContext.Default.GroupSnapshot);
            if (bytes.Length > MaxBytes) throw new IOException("The policy group journal is full.");
            var key = proposed.Policy.Key;
            var pending = GroupPath(key, proposed: true);
            var confirmed = GroupPath(key, proposed: false);
            var head = new OwnedPolicyHead(proposed.Store, proposed.Sequence, Digest(bytes));
            await WriteSnapshotAsync(pending, bytes, overwrite: true, cancellationToken).ConfigureAwait(false);
            if (!await _groupAuthority!.CompareAndCommitAsync(proposed.Store, key.Owner, key.Group, previous, head, cancellationToken).ConfigureAwait(false))
                throw new IOException("The policy group authority rejected its predecessor.");
            if (!AuthorityAvailable || !retiring && !Available) throw GroupUnavailable();
            Promote(pending, confirmed);
            lock (journal.Gate)
            {
                if (!AuthorityAvailable || !retiring && !Available) throw GroupUnavailable();
                journal.Head = head;
                Volatile.Write(ref journal.View, proposed);
            }
        }
        catch (Exception error) when (OwnedPrivatePolicy.IsRecoverableAuthorityFailure(error))
        {
            FailJournal(journal, error);
            throw GroupUnavailable();
        }
    }

    private void FailJournal(GroupJournal journal, Exception error)
    {
        if (error is OwnedPolicyAuthorityUnavailableException) CloseGrantsGlobally();
        lock (journal.Gate) journal.Uncertain = true;
    }

    private void CloseGrantsGlobally()
    {
        lock (_grantGate) Volatile.Write(ref _unavailable, 1);
    }

    private string GroupPath(PolicyGroupKey key, bool proposed) => _path + "." + key.Group + (proposed ? ".proposed" : ".confirmed");

    private static async Task<byte[]?> ReadHeadBytesAsync(string path, OwnedPolicyHead head, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var bytes = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return Digest(bytes) == head.Sha256 ? bytes : null;
    }

    private static bool ValidGroupSnapshot(OwnedPolicyGroup root, OwnedPolicyHead head, GroupSnapshot snapshot) =>
        snapshot.Version == 1 && snapshot.Sequence > 0 && snapshot.Sequence == head.Revision && snapshot.Store == head.Store &&
        snapshot.Policy is { } policy && policy.Key == root.Key && policy.RootIntent == root.RootIntent &&
        policy.Revision >= root.Revision && policy.Deny is not null && policy.Deny.SequenceEqual(root.Deny) &&
        (root.Allow is null ? policy.Allow is null : policy.Allow?.SequenceEqual(root.Allow) == true) &&
        snapshot.Operations is { Length: <= MaxGroupOperations } && snapshot.Intents is { Length: <= MaxGroupIntents } &&
        snapshot.Intents.All(static record => record is not null && Guid.TryParseExact(record.Attempt, "N", out _)) &&
        snapshot.Operations.All(static record => record is not null && OwnedPrivatePolicy.IsIdentifier(record.Operation));

    private static OwnedSensitivityOutcome ReadOutcome(MutationRecord record, OwnedPolicyGroup current, long expectedRevision) =>
        record.ExpectedRevision != expectedRevision ? throw OperationConflict() :
        new(record.Operation, record.Revision, true, current.Revision, current.Sensitive, record.PossibleExposures);

    private static RouterRequestException OperationConflict() =>
        new("policy_revision_conflict", "the expected revision or operation does not match");

    private static RouterRequestException GroupUnavailable() =>
        new("policy_group_unavailable", "the authorized policy group is unavailable");

    private sealed class GroupJournal : IDisposable
    {
        internal readonly object Gate = new();
        internal readonly SemaphoreSlim Writer = new(1);
        internal readonly Dictionary<string, OwnedPrivateAttempt> Active = new(StringComparer.Ordinal);
        internal GroupSnapshot? View;
        internal OwnedPolicyHead? Head;
        internal volatile bool Fenced;
        internal volatile bool Uncertain;
        public void Dispose() => Writer.Dispose();
    }

    internal sealed record GroupSnapshot([property: JsonRequired] int Version, [property: JsonRequired] Guid Store,
        [property: JsonRequired] long Sequence, [property: JsonRequired] OwnedPolicyGroup Policy,
        [property: JsonRequired] MutationRecord[] Operations, [property: JsonRequired] AttemptRecord[] Intents);

    internal sealed record MutationRecord([property: JsonRequired] string Operation, [property: JsonRequired] long ExpectedRevision,
        [property: JsonRequired] long Revision, [property: JsonRequired] int PossibleExposures);

    internal sealed record AttemptRecord([property: JsonRequired] string Attempt, [property: JsonRequired] string Ticket,
        [property: JsonRequired] string Engine, [property: JsonRequired] long PolicyRevision, [property: JsonRequired] string Fingerprint);
}
