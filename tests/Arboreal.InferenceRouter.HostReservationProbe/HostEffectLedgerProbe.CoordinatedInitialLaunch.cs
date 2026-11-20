using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed partial class HostEffectLedgerProbe
{
    internal sealed class CoordinatedInitialLaunch(HostReservationScope scope, IReadOnlyList<HostInitialLaunchIntent> approved)
        : IHostInitialLaunchAuthority
    {
        private const string Mode = "coordinated-initial-launch";
        private string Root => scope.AuthorityDirectory;
        private string Fence => Path.Combine(Root, "issue.lock");
        private string Head => Path.Combine(Root, "head.json");
        private string Journal => Path.Combine(Root, "journal");
        private string Policy => Path.Combine(Root, "policy.json");
        private string Seed => Path.Combine(Root, "daemon-seed.json");
        private string Created => Path.Combine(Root, "created.json");
        private string Started => Path.Combine(Root, "started.json");

        public InitialLaunchPhase? InterruptBefore { get; init; }
        public bool LoseReply { get; init; }
        public Action? BeforeSend { get; set; }
        public int Sends { get; private set; }
        public int Reconciliations { get; private set; }
        public int WorkerReads { get; private set; }
        public HostReservationScope? LastHistoryScope { get; private set; }
        public int Creates => CountAttempts("create-attempts");
        public int Starts => CountAttempts("start-attempts");

        public void Provision(CoordinatedDaemonSeed seed)
        {
            _ = Directory.CreateDirectory(Root);
            using (var file = new FileStream(Fence, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using var fence = EnterIssueFence(scope);
            var genesis = HostReservation.ReadEnrolled(scope);
            Append(new HostInitialHistory(scope, genesis.Digest), null);
            Publish(Policy, new HostInitialPolicy(scope, 1, 1, CompletePair: true, ExclusiveWriter: true,
                IngressClosed: true, ForeignResident: false), Json.HostInitialPolicy, "policy", Mode);
            Publish(Seed, seed, Json.CoordinatedDaemonSeed, "seed", Mode);
        }

        public IDisposable EnterIssueFence(HostReservationScope selected)
        {
            if (selected != scope) throw new IOException("The worker fence belongs to a different slot.");
            return new FileStream(Fence, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        public bool Approves(HostReservationScope selected, HostInitialLaunchIntent intent) => selected == scope && approved.Contains(intent);

        public HostInitialObservation? Observe(HostReservationScope selected)
        {
            if (selected != scope || Read(Policy, Json.HostInitialPolicy) is not { } policy) return null;
            return new HostInitialObservation(policy, Read(Created, Json.HostInitialCreate), Read(Started, Json.HostInitialStart));
        }

        public void ChangePolicy(Func<HostInitialPolicy, HostInitialPolicy> change)
        {
            using var fence = EnterIssueFence(scope);
            Publish(Policy, change(Observe(scope)!.Policy), Json.HostInitialPolicy, "policy", Mode, replace: true);
        }

        public HostInitialHistory? ReadHistory(HostReservationScope selected)
        {
            LastHistoryScope = selected;
            if (selected != scope || Read(Head, Json.CoordinatedHead) is not { } head || head.Revision is < 0 or > 4 ||
                Directory.GetFiles(Journal, "*.json").Length != head.Revision + 1) return null;
            string? digest = null;
            CoordinatedRecord? record = null;
            for (var revision = 0; revision <= head.Revision; revision++)
            {
                var bytes = File.ReadAllBytes(RecordPath(revision));
                var next = JsonSerializer.Deserialize(bytes, Json.CoordinatedRecord);
                if (next is null || next.Predecessor != digest || next.History.Scope != scope ||
                    !Extends(record?.History, next.History, revision)) return null;
                digest = Digest(bytes);
                record = next;
            }
            return digest == head.Digest ? record?.History : null;
        }

        public void AnchorDispatch(HostInitialDispatch dispatch)
        {
            var history = ReadHistory(scope) ?? throw new IOException("The fixture has no coherent head.");
            if (history.Dispatch is not null || !Approves(scope, dispatch.Intent) ||
                !dispatch.MatchesDispatching(HostReservation.ReadEnrolled(scope)))
                throw new IOException("The fixture cannot anchor this dispatch.");
            Advance(history with { Dispatch = dispatch });
        }

        public ValueTask<HostMutationOutcome> LaunchAsync(HostInitialDispatch dispatch, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Sends++;
            BeforeSend?.Invoke();
            if (InterruptBefore == InitialLaunchPhase.Accepted || !Accept(dispatch) ||
                InterruptBefore == InitialLaunchPhase.MayHaveIssued) return ValueTask.FromResult(default(HostMutationOutcome));

            var reply = Issue(dispatch);
            return ValueTask.FromResult(LoseReply ? default : reply);
        }

        public ValueTask<HostMutationOutcome> ReconcileAsync(HostInitialDispatch dispatch, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reconciliations++;
            using var fence = EnterIssueFence(scope);
            if (ReadHistory(scope) is { Applied.Receipt: var receipt } history && history.Dispatch == dispatch)
                return ValueTask.FromResult(new HostMutationOutcome(receipt.Operation, HostMutationStatus.Applied, receipt.InstanceId));
            return ValueTask.FromResult(default(HostMutationOutcome));
        }

        public bool Accept(HostInitialDispatch dispatch)
        {
            using var fence = EnterIssueFence(scope);
            if (ReadHistory(scope) is not { Accepted: null, Issued: null, Applied: null } history ||
                history.Dispatch != dispatch || !CurrentForIssue(dispatch)) return false;
            Advance(history with { Accepted = dispatch });
            return true;
        }

        public HostMutationOutcome Issue(HostInitialDispatch dispatch)
        {
            using var fence = EnterIssueFence(scope);
            if (ReadHistory(scope) is not { Issued: null, Applied: null } history || history.Dispatch != dispatch ||
                history.Accepted != dispatch || !CurrentForIssue(dispatch)) return default;
            history = history with { Issued = dispatch };
            Advance(history);

            // Separate fixture inputs model the daemon's installed settings and running process.
            // The attempt ledger records the exact submitted operation and bytes independently.
            var seed = Read(Seed, Json.CoordinatedDaemonSeed) ?? throw new IOException("Missing fake daemon seed.");
            Attempt("create-attempts", dispatch);
            Publish(Created, new HostInitialCreate(dispatch.Operation, seed.ContainerId, seed.EffectiveCreateDigest, seed.CompletePair),
                Json.HostInitialCreate, "create", Mode);
            Attempt("start-attempts", dispatch);
            if (seed.RunningId is { } running)
                Publish(Started, new HostInitialStart(dispatch.Operation, running), Json.HostInitialStart, "start", Mode);

            var receipt = new HostInitialReceipt(scope, dispatch.Operation, dispatch.Intent, seed.ContainerId);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, HostReservationJsonContext.Default.HostInitialReceipt);
            Advance(history with { Applied = new(receipt, Convert.ToBase64String(bytes)) });
            return new HostMutationOutcome(dispatch.Operation, HostMutationStatus.Applied, seed.ContainerId);
        }

        private bool CurrentForIssue(HostInitialDispatch dispatch)
        {
            WorkerReads++;
            return dispatch.MatchesDispatching(HostReservation.ReadEnrolled(scope)) && Approves(scope, dispatch.Intent) &&
                dispatch.Policy.Authorizes(scope) && Observe(scope) == new HostInitialObservation(dispatch.Policy);
        }

        private void Advance(HostInitialHistory history) => Append(history,
            Read(Head, Json.CoordinatedHead) ?? throw new IOException("Missing fixture head."));

        private void Append(HostInitialHistory history, CoordinatedHead? previous)
        {
            var revision = previous is null ? 0 : previous.Revision + 1;
            var path = RecordPath(revision);
            Publish(path, new CoordinatedRecord(previous?.Digest, history), Json.CoordinatedRecord, "record", Mode);
            if (Read(Head, Json.CoordinatedHead) != previous) throw new IOException("The fixture head changed.");
            var next = new CoordinatedHead(revision, Digest(File.ReadAllBytes(path)));
            Publish(Head, next, Json.CoordinatedHead, "head", Mode, replace: previous is not null);
            if (Read(Head, Json.CoordinatedHead) != next) throw new IOException("The fixture head failed to advance.");
        }

        private static bool Extends(HostInitialHistory? prior, HostInitialHistory next, int revision) =>
            revision switch
            {
                0 => HostMutation.IsPlanDigest(next.GenesisDigest) && next == new HostInitialHistory(next.Scope, next.GenesisDigest),
                1 => prior is not null && next.Dispatch is not null && next == (prior with { Dispatch = next.Dispatch }),
                2 => prior is { Dispatch: { } dispatch } && next == (prior with { Accepted = dispatch }),
                3 => prior is { Dispatch: { } dispatch } && next == (prior with { Issued = dispatch }),
                4 => prior is not null && next.Applied is not null && next == (prior with { Applied = next.Applied }),
                _ => false,
            };

        private void Attempt(string journal, HostInitialDispatch dispatch) => Publish(
            Path.Combine(Root, journal, $"{dispatch.Operation.Id:N}.{Guid.NewGuid():N}.json"), dispatch,
            Json.HostInitialDispatch, "attempt", Mode);

        private int CountAttempts(string journal)
        {
            var path = Path.Combine(Root, journal);
            return Directory.Exists(path) ? Directory.GetFiles(path, "*.json").Length : 0;
        }

        private string RecordPath(int revision) => Path.Combine(Journal, revision.ToString(CultureInfo.InvariantCulture) + ".json");
        private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }
}

internal sealed record CoordinatedDaemonSeed(string ContainerId, string EffectiveCreateDigest, bool CompletePair, string? RunningId);
internal sealed record CoordinatedRecord(string? Predecessor, HostInitialHistory History);
internal sealed record CoordinatedHead(int Revision, string Digest);

[JsonSerializable(typeof(HostInitialPolicy))]
[JsonSerializable(typeof(HostInitialCreate))]
[JsonSerializable(typeof(HostInitialStart))]
[JsonSerializable(typeof(HostInitialDispatch))]
[JsonSerializable(typeof(CoordinatedDaemonSeed))]
[JsonSerializable(typeof(CoordinatedRecord))]
[JsonSerializable(typeof(CoordinatedHead))]
internal sealed partial class HostEffectLedgerJsonContext;
