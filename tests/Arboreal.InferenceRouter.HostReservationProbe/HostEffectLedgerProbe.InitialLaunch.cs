using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed partial class HostEffectLedgerProbe
{
    public InitialLaunchLedger InitialLaunch(string authorityRoot, IReadOnlyDictionary<HostCheckpointPlan, byte[]> approvedCreate) =>
        new(this, authorityRoot, approvedCreate);

    // The modeled head and observer facts are outside the journal restore boundary. The fixture's
    // shared worker fence orders policy changes, decisions and daemon entry. All roles are synthetic.
    internal sealed class InitialLaunchLedger(HostEffectLedgerProbe storage, string authorityRoot,
        IReadOnlyDictionary<HostCheckpointPlan, byte[]> approvedCreate)
    {
        private const string Mode = "initial-launch";
        private const string PairDevices = "0,1";
        private readonly InitialLaunchObserver _observer = new(storage._root, authorityRoot);
        private readonly InitialLaunchAnchor _anchor = new(authorityRoot);
        private readonly string _policyPath = Path.Combine(authorityRoot, "policy.json");
        private string Journal => Path.Combine(storage._root, "initial-journal");
        private string DaemonSeedPath => Path.Combine(storage._root, "initial-daemon-seed.json");
        public InitialLaunchObserver Observer => _observer;

        public void Provision(InitialLaunchDaemonSeed daemon)
        {
            HostEffectCheckpoint.ProvisionNew(storage._checkpointPath);
            _ = storage.RunCommand("initialize", "");
            using var fence = storage.OpenFence();
            var record = new InitialLaunchRecord(0, "", InitialLaunchPhase.Genesis, 0);
            var head = AppendRecord(record);
            _anchor.Provision(head);
            Publish(DaemonSeedPath, daemon, Json.InitialLaunchDaemonSeed, "daemon-seed", Mode);
            InstallAuthority(new(EnrolledScope, 1, 1, PartitionCoverage.Complete, PairDevices,
                ExclusiveWriter: true, ForeignResident: false, IngressClosed: true));
        }

        public void ChangeAuthority(InitialLaunchAuthority authority)
        {
            using var fence = storage.OpenFence();
            var current = _observer.ReadAuthority();
            if (authority.PolicyGeneration < current.PolicyGeneration || authority.OwnerGeneration < current.OwnerGeneration ||
                (authority.PolicyGeneration == current.PolicyGeneration && authority.OwnerGeneration == current.OwnerGeneration))
                throw new InvalidOperationException("A policy change must advance its authority generation.");
            InstallAuthority(authority);
        }

        public InitialLaunchCommand? Dispatch(HostCheckpointPlan plan, ReadOnlySpan<byte> bytes, InitialLaunchAuthority? witness)
        {
            using var fence = storage.OpenFence();
            using var checkpoint = HostEffectCheckpoint.Open(storage._checkpointPath);
            var (head, record) = ReadCurrent(checkpoint.State);
            if (record.Phase != InitialLaunchPhase.Genesis || !MatchesApprovedCreate(plan, bytes) || !CurrentVacancy(witness))
                return null;

            var operation = checkpoint.PrepareInitialLaunch(plan);
            checkpoint.MarkLaunchDispatching(operation);
            var command = new InitialLaunchCommand(witness!, operation, plan, Convert.ToBase64String(bytes), Digest(bytes));
            Advance(head, record with { Phase = InitialLaunchPhase.Dispatched, CheckpointRevision = checkpoint.State.Revision,
                Command = command });
            return command;
        }

        public bool Accept(InitialLaunchCommand command, long workerGeneration)
        {
            using var fence = storage.OpenFence();
            using var checkpoint = HostEffectCheckpoint.Open(storage._checkpointPath);
            var (head, record) = ReadCurrent(checkpoint.State);
            if (record.Phase != InitialLaunchPhase.Dispatched || record.Command != command ||
                workerGeneration != EnrolledScope.WorkerGeneration || !CurrentVacancy(command.Authority)) return false;

            Advance(head, record with { Phase = InitialLaunchPhase.Accepted });
            return true;
        }

        public string? Issue(InitialLaunchCommand command, long workerGeneration, bool interruptBeforeHead = false)
        {
            using var fence = storage.OpenFence();
            using var checkpoint = HostEffectCheckpoint.Open(storage._checkpointPath);
            var (head, record) = ReadCurrent(checkpoint.State);
            if (record.Phase != InitialLaunchPhase.Accepted || record.Command is not { } stored || stored != command ||
                workerGeneration != EnrolledScope.WorkerGeneration ||
                !CurrentVacancy(command.Authority)) return null;

            var payload = Convert.FromBase64String(stored.CreateBytes);
            Advance(head, record with { Phase = InitialLaunchPhase.MayHaveIssued }, interruptBeforeHead);
            // The same fence is still held. No different authority, effect or dispatch can enter here.
            var daemon = Read(DaemonSeedPath, Json.InitialLaunchDaemonSeed) ?? throw new IOException("Missing fake daemon state.");
            var instance = FakeCreate(command.Operation, payload, daemon);
            FakeStart(command.Operation, instance, daemon);
            return instance;
        }

        public byte[]? Complete(InitialLaunchCommand command, string workerInstance,
            InitialLaunchCompletionCut cut = InitialLaunchCompletionCut.None)
        {
            using var fence = storage.OpenFence();
            using var checkpoint = HostEffectCheckpoint.Open(storage._checkpointPath);
            var (head, record) = ReadCurrent(checkpoint.State);
            var observed = _observer.Observe();
            if (record.Phase != InitialLaunchPhase.MayHaveIssued || record.Command != command ||
                observed.Authority != command.Authority ||
                observed.Create is not { } created || observed.Start is not { } started ||
                created.Operation != command.Operation || started.Operation != command.Operation ||
                created.InstanceId != workerInstance || started.InstanceId != workerInstance ||
                !MatchesApprovedCreate(command.Plan, Convert.FromBase64String(created.EffectiveCreate)) ||
                created.Devices != PairDevices) return null;

            var applied = new HostCheckpointLaunchEffect(command.Operation, command.Plan, created.InstanceId);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new InitialLaunchReceipt(command.Authority.Scope, applied, command.CreateDigest), Json.InitialLaunchReceipt);
            checkpoint.RecordAppliedLaunch(new HostMutationOutcome(command.Operation, HostMutationStatus.Applied, created.InstanceId), applied);
            if (cut == InitialLaunchCompletionCut.AfterCheckpoint)
                throw new IOException("The applied checkpoint was installed without its journal record.");
            Advance(head, record with { Phase = InitialLaunchPhase.Applied, CheckpointRevision = checkpoint.State.Revision,
                ReceiptBytes = Convert.ToBase64String(bytes) }, cut == InitialLaunchCompletionCut.AfterJournal);
            return bytes;
        }

        public byte[]? Lookup(HostMutation exactOperation, string exactInstance)
        {
            using var fence = storage.OpenFence();
            using var checkpoint = HostEffectCheckpoint.Open(storage._checkpointPath);
            var record = ReadApplied(checkpoint.State, exactOperation, exactInstance);
            return record?.ReceiptBytes is { } bytes ? Convert.FromBase64String(bytes) : null;
        }

        public InitialLaunchIngress? ReadIngress(HostMutation exactOperation, string exactInstance)
        {
            try
            {
                using var fence = storage.OpenFence();
                using var checkpoint = HostEffectCheckpoint.Open(storage._checkpointPath);
                var state = checkpoint.State;
                if (ReadApplied(state, exactOperation, exactInstance)?.Command is not { } command) return null;

                var policy = _observer.ReadAuthority();
                if (!CoversPair(policy) || policy.Scope != command.Authority.Scope ||
                    policy.OwnerGeneration != command.Authority.OwnerGeneration ||
                    policy.PolicyGeneration < command.Authority.PolicyGeneration) return null;
                return new InitialLaunchIngress(state.AppliedLaunch!, policy);
            }
            catch (Exception error) when (error is IOException or JsonException or FormatException)
            {
                // An unverified or split store has no fixture ingress authority.
                return null;
            }
        }

        private InitialLaunchRecord? ReadApplied(HostEffectCheckpointState checkpoint, HostMutation exactOperation, string exactInstance)
        {
            var (_, record) = ReadCurrent(checkpoint);
            return record.Phase == InitialLaunchPhase.Applied && checkpoint.AppliedLaunch is { } applied &&
                applied.Operation == exactOperation && applied.InstanceId == exactInstance ? record : null;
        }

        private bool MatchesApprovedCreate(HostCheckpointPlan plan, ReadOnlySpan<byte> bytes) =>
            approvedCreate.TryGetValue(plan, out var approved) && bytes.SequenceEqual(approved);

        private bool CurrentVacancy(InitialLaunchAuthority? observed) =>
            observed is { IngressClosed: true } && CoversPair(observed) &&
            _observer.Observe() == new InitialLaunchObservation(observed, null, null);

        private static bool CoversPair(InitialLaunchAuthority authority) =>
            authority is { Coverage: PartitionCoverage.Complete, Devices: PairDevices, ExclusiveWriter: true,
                ForeignResident: false, PolicyGeneration: > 0, OwnerGeneration: > 0 } && authority.Scope == EnrolledScope;

        private void InstallAuthority(InitialLaunchAuthority authority) =>
            Publish(_policyPath, authority, Json.InitialLaunchAuthority, "policy", Mode, replace: true);

        private string FakeCreate(HostMutation operation, byte[] bytes, InitialLaunchDaemonSeed daemon)
        {
            var attempt = new InitialLaunchCreateAttempt(operation, Convert.ToBase64String(bytes));
            Publish(AttemptPath("initial-create-attempts", operation), attempt, Json.InitialLaunchCreateAttempt, "attempt", Mode);
            // The daemon's installed settings and GPU assignment come from a separate fixture seed.
            var created = new InitialLaunchCreate(operation, daemon.CreatedInstance, daemon.EffectiveCreate, daemon.Devices);
            Publish(Path.Combine(storage._root, "initial-created.json"), created, Json.InitialLaunchCreate, "create", Mode);
            return created.InstanceId;
        }

        private void FakeStart(HostMutation operation, string instance, InitialLaunchDaemonSeed daemon)
        {
            Publish(AttemptPath("initial-start-attempts", operation), operation, Json.HostMutation, "attempt", Mode);
            var created = Read(Path.Combine(storage._root, "initial-created.json"), Json.InitialLaunchCreate);
            if (created is null || created.Operation != operation || created.InstanceId != instance)
                throw new IOException("The fake daemon can start only the created instance.");
            if (daemon.RunningInstance is { } runningInstance)
                Publish(Path.Combine(storage._root, "initial-started.json"), new InitialLaunchStart(operation, runningInstance),
                    Json.InitialLaunchStart, "start", Mode);
        }

        private string AttemptPath(string journal, HostMutation operation) =>
            Path.Combine(storage._root, journal, $"{operation.Id:N}.{Guid.NewGuid():N}.json");

        private void Advance(InitialLaunchHead predecessor, InitialLaunchRecord record, bool interruptBeforeHead = false)
        {
            var next = AppendRecord(record with { Revision = predecessor.Revision + 1, Predecessor = predecessor.Digest });
            if (interruptBeforeHead) throw new IOException("The journal advanced without its independent head.");
            _anchor.Advance(predecessor, next);
            if (_anchor.ReadCurrent() != next) throw new IOException("The independent head did not advance.");
        }

        private InitialLaunchHead AppendRecord(InitialLaunchRecord record)
        {
            Publish(RecordPath(record.Revision), record, Json.InitialLaunchRecord, "initial-record", Mode);
            return new(EnrolledScope, record.Revision, record.Predecessor, Digest(File.ReadAllBytes(RecordPath(record.Revision))));
        }

        private (InitialLaunchHead Head, InitialLaunchRecord Record) ReadCurrent(HostEffectCheckpointState checkpoint)
        {
            var head = _anchor.ReadCurrent();
            if (head.Scope != EnrolledScope || head.Revision < 0 || head.Revision > (int)InitialLaunchPhase.Applied ||
                Directory.GetFiles(Journal, "*.json").Length != head.Revision + 1)
                throw new IOException("The installed initial launch journal and head disagree.");

            var parent = "";
            InitialLaunchRecord? record = null;
            for (var revision = 0; revision <= head.Revision; revision++)
            {
                var bytes = File.ReadAllBytes(RecordPath(revision));
                var next = JsonSerializer.Deserialize(bytes, Json.InitialLaunchRecord);
                if (next is null || next.Revision != revision || (int)next.Phase != revision || next.Predecessor != parent ||
                    (revision > 1 && next.Command != record!.Command))
                    throw new IOException("The initial launch journal has contradictory ancestry.");
                parent = Digest(bytes);
                record = next;
            }

            if (record is null || parent != head.Digest || record.Predecessor != head.Predecessor || !MatchesCheckpoint(record, checkpoint))
                throw new IOException("The initial launch journal cannot authorize this checkpoint.");
            return (head, record);
        }

        private bool MatchesCheckpoint(InitialLaunchRecord record, HostEffectCheckpointState checkpoint)
        {
            if (checkpoint.Revision != record.CheckpointRevision) return false;
            if (record.Phase == InitialLaunchPhase.Genesis) return checkpoint == HostEffectCheckpointState.Genesis;
            if (record.Command is not { } command || !command.Plan.Matches(command.Operation, checkpoint.Epoch)) return false;
            var createBytes = Convert.FromBase64String(command.CreateBytes);
            if (!MatchesApprovedCreate(command.Plan, createBytes) || Digest(createBytes) != command.CreateDigest) return false;
            if (record.Phase != InitialLaunchPhase.Applied)
                return checkpoint.Phase == HostCheckpointPhase.LaunchDispatching &&
                    checkpoint.Transition is { } transition && transition.Launch == command.Operation && transition.Plan == command.Plan;
            if (checkpoint.AppliedLaunch is not { } launch || launch.Operation != command.Operation || launch.Plan != command.Plan ||
                record.ReceiptBytes is null) return false;
            var receipt = JsonSerializer.Deserialize(Convert.FromBase64String(record.ReceiptBytes), Json.InitialLaunchReceipt);
            return receipt == new InitialLaunchReceipt(command.Authority.Scope, launch, command.CreateDigest);
        }

        private string RecordPath(int revision) => Path.Combine(Journal, revision.ToString(CultureInfo.InvariantCulture) + ".json");
        private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }

    // A separate modeled authority. The writer may only extend the installed head under the fence.
    private sealed class InitialLaunchAnchor(string authorityRoot)
    {
        private string PathToHead => Path.Combine(authorityRoot, "head.json");

        public void Provision(InitialLaunchHead genesis) => Publish(PathToHead, genesis, Json.InitialLaunchHead, "head", "genesis");

        public InitialLaunchHead ReadCurrent() => Read(PathToHead, Json.InitialLaunchHead) ??
            throw new IOException("The independent initial launch head is absent.");

        public void Advance(InitialLaunchHead expected, InitialLaunchHead next)
        {
            if (ReadCurrent() != expected || next.Scope != expected.Scope || next.Revision != expected.Revision + 1 ||
                next.Predecessor != expected.Digest) throw new IOException("The independent head cannot accept this successor.");
            Publish(PathToHead, next, Json.InitialLaunchHead, "head", "advance", replace: true);
        }
    }

    // This role reads independently authored fake daemon and host facts. It has no journal,
    // anchor or mutation interface and does not construct evidence from the launch request.
    internal sealed class InitialLaunchObserver(string daemonRoot, string authorityRoot)
    {
        public InitialLaunchAuthority ReadAuthority() => Read(Path.Combine(authorityRoot, "policy.json"), Json.InitialLaunchAuthority)
            ?? throw new IOException("Initial launch policy is unavailable.");

        public InitialLaunchObservation Observe() => new(ReadAuthority(),
            Read(Path.Combine(daemonRoot, "initial-created.json"), Json.InitialLaunchCreate),
            Read(Path.Combine(daemonRoot, "initial-started.json"), Json.InitialLaunchStart));
    }
}

internal enum InitialLaunchPhase { Genesis, Dispatched, Accepted, MayHaveIssued, Applied }
internal enum InitialLaunchCompletionCut { None, AfterCheckpoint, AfterJournal }
internal sealed record InitialLaunchAuthority(PartitionScope Scope, long PolicyGeneration, long OwnerGeneration,
    PartitionCoverage Coverage, string Devices, bool ExclusiveWriter, bool ForeignResident, bool IngressClosed);
internal sealed record InitialLaunchCommand(InitialLaunchAuthority Authority, HostMutation Operation, HostCheckpointPlan Plan,
    string CreateBytes, string CreateDigest);
internal sealed record InitialLaunchDaemonSeed(string CreatedInstance, string? RunningInstance, string EffectiveCreate, string Devices);
internal sealed record InitialLaunchCreateAttempt(HostMutation Operation, string CreateBytes);
internal sealed record InitialLaunchCreate(HostMutation Operation, string InstanceId, string EffectiveCreate, string Devices);
internal sealed record InitialLaunchStart(HostMutation Operation, string InstanceId);
internal sealed record InitialLaunchObservation(InitialLaunchAuthority Authority, InitialLaunchCreate? Create, InitialLaunchStart? Start);
internal sealed record InitialLaunchReceipt(PartitionScope Scope, HostCheckpointLaunchEffect Launch, string CreateDigest);
internal sealed record InitialLaunchIngress(HostCheckpointLaunchEffect Launch, InitialLaunchAuthority Authority);
internal sealed record InitialLaunchRecord(int Revision, string Predecessor, InitialLaunchPhase Phase, long CheckpointRevision,
    InitialLaunchCommand? Command = null, string? ReceiptBytes = null);
internal sealed record InitialLaunchHead(PartitionScope Scope, int Revision, string Predecessor, string Digest);

[JsonSerializable(typeof(InitialLaunchAuthority))]
[JsonSerializable(typeof(InitialLaunchDaemonSeed))]
[JsonSerializable(typeof(InitialLaunchCreateAttempt))]
[JsonSerializable(typeof(InitialLaunchCreate))]
[JsonSerializable(typeof(InitialLaunchStart))]
[JsonSerializable(typeof(InitialLaunchReceipt))]
[JsonSerializable(typeof(InitialLaunchRecord))]
[JsonSerializable(typeof(InitialLaunchHead))]
[JsonSerializable(typeof(HostMutation))]
internal sealed partial class HostEffectLedgerJsonContext;
