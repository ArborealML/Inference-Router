using System.Collections.Immutable;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Tomlyn.Model;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    [Theory(Timeout = 30_000)]
    [InlineData(DeepSeekEngineName)]
    [InlineData(QwenEngineName)]
    public async Task SupervisedLaunchRecordProducesAndReopensOneClosedReceipt(string engine)
    {
        var fx = ProvisionSupervisedLaunch(engine);
        var loseAcknowledgment = engine == QwenEngineName;
        fx.Head.LoseAppliedHeadRead = loseAcknowledgment;
        var first = await fx.Open().RunAsync(engine, fx.Text, fx.Claim, CancellationToken.None);
        var reopened = fx.Open();
        if (loseAcknowledgment)
        {
            Assert.Null(first);
            Assert.Equal(HostMutationPhase.Dispatching, HostReservation.ReadEnrolled(fx.Scope).Record.State.Phase);
            first = await reopened.RunAsync(engine, fx.Text, fx.Claim, CancellationToken.None);
        }
        var receipt = Assert.IsType<HostAppliedIngressClosed>(first).Receipt;
        var dispatch = Assert.IsType<HostInitialDispatch>(fx.ReadJournal().Frame.History.Dispatch);
        Assert.Equal(new HostInitialReceipt(fx.Scope, dispatch.Operation, fx.Approval.Intent, ReceiptInstance), receipt);
        Assert.Equal(dispatch.Operation, HostReservation.ReadEnrolled(fx.Scope).Record.State.Pending);
        Assert.Equal(HostMutationPhase.LaunchApplied, HostReservation.ReadEnrolled(fx.Scope).Record.State.Phase);
        Assert.Equal(1, fx.Port.Creates);
        Assert.Equal(1, fx.Port.Starts);

        var original = Assert.IsType<byte[]>(reopened.ReadReceipt(receipt.Operation, receipt.InstanceId));
        var applied = Assert.IsType<HostInitialApplied>(fx.ReadJournal().Frame.History.Applied);
        Assert.Equal(Convert.FromBase64String(applied.ReceiptBytes), original);
        Assert.Null(reopened.ReadReceipt(receipt.Operation, new string('F', 64)));
        Assert.Equal(new HostAppliedIngressClosed(receipt), await reopened.RunAsync(engine, fx.Text, fx.Claim,
            CancellationToken.None));
        Assert.Null(await reopened.RunAsync(CompetingMember(engine), fx.Text, fx.Claim, CancellationToken.None));
        await AssertReceiptIngressClosedAsync(fx.Staging, fx.Scope, receipt);

        if (engine == DeepSeekEngineName)
        {
            using (fx.Journal.EnterFence()) fx.DriftTopologyAndApproval();
            var reservation = File.ReadAllBytes(fx.Scope.Slot);
            var journal = File.ReadAllBytes(fx.Journal.JournalPath);
            var head = fx.Head.Current;
            var topology = fx.Observer.Commission;
            var afterChange = fx.Open();
            Assert.Equal(original, afterChange.ReadReceipt(receipt.Operation, receipt.InstanceId));
            Assert.Null(await afterChange.RunAsync(engine, fx.Text, fx.Claim, CancellationToken.None));
            Assert.Equal(reservation, File.ReadAllBytes(fx.Scope.Slot));
            Assert.Equal(journal, File.ReadAllBytes(fx.Journal.JournalPath));
            Assert.Equal(head, fx.Head.Current);
            Assert.Equal(topology, fx.Observer.Commission);
            await AssertReceiptIngressClosedAsync(fx.Staging, fx.Scope, receipt);
        }

        Assert.Equal(1, fx.Port.Creates);
        Assert.Equal(1, fx.Port.Starts);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("effective", 0)]
    [InlineData("wire", 0)]
    [InlineData("running-id", 1)]
    public async Task SupervisedLaunchRecordRequiresIndependentWireEffectiveAndRunningEvidence(string wrong, int starts)
    {
        var fx = ProvisionSupervisedLaunch(DeepSeekEngineName, wrong);
        Assert.Null(await fx.Open().RunAsync(DeepSeekEngineName, fx.Text, fx.Claim, CancellationToken.None));
        var history = fx.ReadJournal().Frame.History;
        var dispatch = Assert.IsType<HostInitialDispatch>(history.Issued);
        Assert.Equal(dispatch, history.Accepted);
        Assert.Null(history.Applied);
        await AssertSupervisedLaunchStillFencedAsync(fx, dispatch, expectedCreates: 1, expectedStarts: starts);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupervisedLaunchRecordRechecksCommissionAndPolicyAfterAcceptance(bool topologyChanges)
    {
        var fx = ProvisionSupervisedLaunch(DeepSeekEngineName);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = fx.Open(ct =>
        {
            accepted.TrySetResult();
            return new ValueTask(continuation.Task.WaitAsync(ct));
        });
        var attempt = entry.RunAsync(DeepSeekEngineName, fx.Text, fx.Claim, CancellationToken.None).AsTask();
        HostInitialDispatch dispatch;
        try
        {
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var fence = fx.Journal.EnterFence();
            var history = Assert.IsType<SupervisedLaunchSnapshot>(fx.Journal.Read()).Frame.History;
            dispatch = Assert.IsType<HostInitialDispatch>(history.Accepted);
            Assert.Null(history.Issued);
            if (topologyChanges)
            {
                var policy = fx.Observer.Policy;
                var generation = fx.Observer.Commission.Topology.Head.Generation;
                fx.DriftTopologyAndApproval();
                Assert.Equal(generation, fx.Observer.Commission.Topology.Head.Generation);
                Assert.Equal(policy, fx.Observer.Policy);
            }
            else fx.Observer.Policy = fx.Observer.Policy with { OwnerGeneration = fx.Observer.Policy.OwnerGeneration + 1 };
        }
        finally { continuation.TrySetResult(); }

        Assert.Null(await attempt);
        Assert.Null(fx.ReadJournal().Frame.History.Issued);
        await AssertSupervisedLaunchStillFencedAsync(fx, dispatch, expectedCreates: 0, expectedStarts: 0);
    }

    private static async Task AssertSupervisedLaunchStillFencedAsync(SupervisedLaunchFixture fx, HostInitialDispatch dispatch,
        int expectedCreates, int expectedStarts)
    {
        var installed = HostReservation.ReadEnrolled(fx.Scope);
        Assert.Equal(HostMutationPhase.Dispatching, installed.Record.State.Phase);
        Assert.Equal(1, installed.Record.State.Epoch);
        Assert.Equal(dispatch.Operation, installed.Record.State.Pending);
        Assert.Null(installed.Record.State.LaunchedContainerId);
        var journal = File.ReadAllBytes(fx.Journal.JournalPath);
        var head = fx.Head.Current;
        var reopened = fx.Open();
        Assert.Null(await reopened.RunAsync(DeepSeekEngineName, fx.Text, fx.Claim, CancellationToken.None));
        Assert.Null(await reopened.RunAsync(QwenEngineName, fx.Text, fx.Claim, CancellationToken.None));
        Assert.Null(reopened.ReadReceipt(dispatch.Operation, ReceiptInstance));
        Assert.Equal(installed, HostReservation.ReadEnrolled(fx.Scope));
        Assert.Equal(journal, File.ReadAllBytes(fx.Journal.JournalPath));
        Assert.Equal(head, fx.Head.Current);
        Assert.Equal(expectedCreates, fx.Port.Creates);
        Assert.Equal(expectedStarts, fx.Port.Starts);
        await AssertReceiptIngressClosedAsync(fx.Staging, fx.Scope, new(fx.Scope, dispatch.Operation, dispatch.Intent, ReceiptInstance));
    }

    private SupervisedLaunchFixture ProvisionSupervisedLaunch(string engine, string? wrong = null)
    {
        var (text, claim, authority) = EnrolledPairTopologyFixture("/fixture/supervised.sock");
        var scope = HostReservation.ProvisionEnrolled(ReservationPath, new(authority.Head.Partition, authority.Head.Generation));
        var pair = RouterConfigLoader.PrepareEnrolledPair(text, authority.Head.Slot, claim, _ => authority);
        using var stream = OpenPairResource("selector-reference.json");
        using var oracle = JsonDocument.Parse(stream);
        var approvals = ImmutableArray.CreateBuilder<SupervisedLaunchApproval>();
        DockerCreateRequest? selectedEffective = null;
        foreach (var member in pair.Members)
        {
            var recipe = Assert.Single(oracle.RootElement.EnumerateArray(), row =>
                row.GetProperty("engine").GetString() == member.Plan.Engine &&
                row.GetProperty("model").GetString() == member.Plan.Model &&
                row.GetProperty("tier").GetString() == member.Plan.Tier).GetProperty("create");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(ReadPairCreate(recipe), DockerJsonContext.Default.DockerCreateRequest);
            approvals.Add(new(HostInitialLaunchIntent.Freeze(member.Plan, bytes), member.Image));
            if (member.Plan.Engine == engine) selectedEffective = ReadPairCreate(recipe);
        }
        var commission = new SupervisedLaunchCommission(scope, authority, approvals.ToImmutable());
        var approval = Assert.IsType<SupervisedLaunchApproval>(commission.Approval(engine));
        var effective = Assert.IsType<DockerCreateRequest>(selectedEffective);
        var limits = Assert.IsType<DockerHostConfig>(effective.HostConfig);
        limits.NetworkMode = "bridge";
        limits.Init = false;
        if (wrong == "effective") limits.Memory = Assert.IsType<long>(limits.Memory) - 1024;
        var wire = Convert.FromBase64String(approval.Intent.CreateBytes);
        byte[] observedWire = wrong == "wire" ? [.. wire, (byte)' '] : wire.ToArray();
        var observer = new SupervisedFixtureObserver(commission, observedWire, effective,
            wrong == "running-id" ? new string('F', 64) : ReceiptInstance);
        var head = new SupervisedFixtureHead();
        var journal = new SupervisedLaunchJournal(scope, head);
        var port = new SupervisedFixturePort(journal, observer, wire);
        var fixture = new SupervisedLaunchFixture(scope, text, claim, approval, head, journal, observer, port, ReceiptStaging());
        fixture.Open().ProvisionJournal();
        return fixture;
    }

    private static string CompetingMember(string engine) => engine == DeepSeekEngineName ? QwenEngineName : DeepSeekEngineName;

    private sealed record SupervisedLaunchFixture(HostReservationScope Scope, string Text, EnrolledPairClaim Claim,
        SupervisedLaunchApproval Approval, SupervisedFixtureHead Head, SupervisedLaunchJournal Journal,
        SupervisedFixtureObserver Observer, SupervisedFixturePort Port, HostClosedRouteStaging Staging)
    {
        internal SupervisedInitialLaunch Open(Func<CancellationToken, ValueTask>? afterAccept = null) =>
            new(Scope, Claim.Head.Slot, Head, Observer, Port, new ReceiptOnlyProvider(), Staging) { AfterAcceptForTests = afterAccept };

        internal SupervisedLaunchSnapshot ReadJournal()
        {
            using var fence = Journal.EnterFence();
            return Assert.IsType<SupervisedLaunchSnapshot>(Journal.Read());
        }

        internal void DriftTopologyAndApproval()
        {
            var commission = Observer.Commission;
            var prior = commission.Topology;
            var grant = prior.Members[0];
            var otherMember = grant.Member with { BackendRoute = "independent-rebound-route" };
            var otherGrant = grant with { Member = otherMember, SocketPath = "/fixture/rebound.sock" };
            var next = prior with
            {
                Head = prior.Head with { Revision = prior.Head.Revision + 1, Digest = new string('B', 64) },
                Members = prior.Members.SetItem(0, otherGrant),
            };
            var approved = Assert.IsType<SupervisedLaunchApproval>(commission.Approval(otherMember.Engine));
            var plan = approved.Intent.Plan with { BackendRoute = otherMember.BackendRoute };
            var replacement = approved with { Intent = approved.Intent with { Plan = plan } };
            next = next with { Members = next.Members.SetItem(0, otherGrant with { PlanDigest = plan.Digest() }) };
            var currentClaim = new EnrolledPairClaim(next.Head, [.. next.Members.Select(item => item.Member)], next.Devices);
            var document = ReadDeepSeekQwenDocument(Text);
            var docker = Assert.IsType<TomlTable>(DeepSeekQwenEngineTable(document, otherMember.Engine)["docker"]);
            docker["socket"] = otherGrant.SocketPath;
            var checkedCurrent = RouterConfigLoader.PrepareEnrolledPair(WriteDeepSeekQwenDocument(document),
                next.Head.Slot, currentClaim, _ => next);
            Assert.Equal(next.Head, checkedCurrent.Head);
            Observer.Commission = commission with { Topology = next, Approvals = commission.Approvals.Replace(approved, replacement) };
        }
    }

    private sealed class SupervisedFixtureHead : ISupervisedLaunchHead
    {
        internal SupervisedLaunchJournalHead? Current { get; private set; }
        internal bool LoseAppliedHeadRead { get; set; }

        public SupervisedLaunchJournalHead? Read(HostReservationScope scope)
        {
            if (Current is not { } current || current.Scope != scope) return null;
            if (current.Revision == 4 && LoseAppliedHeadRead)
            {
                LoseAppliedHeadRead = false;
                return null;
            }
            return current;
        }

        public bool TryAdvance(SupervisedLaunchJournalHead? predecessor, SupervisedLaunchJournalHead next)
        {
            if (Current != predecessor || next.Revision != (predecessor?.Revision ?? -1) + 1 ||
                (predecessor is not null && predecessor.Scope != next.Scope)) return false;
            Current = next;
            return true;
        }
    }

    private sealed class SupervisedFixtureObserver(SupervisedLaunchCommission commission, byte[] observedWire,
        DockerCreateRequest effective, string runningId) : ISupervisedLaunchObserver
    {
        private SupervisedObservedCreate? _create;
        private SupervisedObservedStart? _start;
        internal SupervisedLaunchCommission Commission { get; set; } = commission;
        internal HostInitialPolicy Policy { get; set; } = new(commission.Scope, 1, 1,
            CompletePair: true, ExclusiveWriter: true, IngressClosed: true, ForeignResident: false);

        public SupervisedLaunchCommission? ReadCommission(HostReservationScope scope) => scope == Commission.Scope ? Commission : null;
        public SupervisedLaunchPolicy? ReadPolicy(HostReservationScope scope) =>
            scope == Commission.Scope ? new(Policy, Vacant: _create is null) : null;
        public DockerImageIdentity? ReadImage(EnrolledPairHead head, string socketPath, string imageId) =>
            head == Commission.Topology.Head && Commission.Topology.Members.Any(grant => grant.SocketPath == socketPath)
                ? Commission.Approvals.SingleOrDefault(item => item.Image.Id == imageId)?.Image : null;
        public SupervisedObservedCreate? ReadCreate(SupervisedLaunchCommand command) => _create?.Command == command ? _create : null;
        public SupervisedObservedStart? ReadStart(SupervisedLaunchCommand command) => _start?.Command == command ? _start : null;

        // The daemon fixture emits effect identities. Neither wire bytes nor winning settings come from the worker reply.
        internal void RecordCreate(SupervisedLaunchCommand command)
        {
            var approval = Assert.IsType<SupervisedLaunchApproval>(Commission.Approval(command.Operation.Engine));
            var grant = Commission.Grant(command.Operation.Engine);
            _create = new(command, ReceiptInstance, observedWire, approval.Image, effective,
                grant.Member.BackendRoute, grant.Endpoint, Commission.Topology.Devices);
        }

        internal void RecordStart(SupervisedLaunchCommand command) => _start = new(command, runningId, Running: true);
    }

    private sealed class SupervisedFixturePort(SupervisedLaunchJournal journal, SupervisedFixtureObserver observer,
        byte[] expectedWire) : ISupervisedLaunchPort
    {
        internal int Creates { get; private set; }
        internal int Starts { get; private set; }

        public ValueTask<string?> CreateAsync(SupervisedLaunchCommand command, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = Assert.IsType<SupervisedLaunchSnapshot>(journal.Read());
            var history = snapshot.Frame.History;
            var dispatch = Assert.IsType<HostInitialDispatch>(history.Dispatch);
            Assert.Equal(3, snapshot.Head.Revision);
            Assert.Equal(dispatch, history.Accepted);
            Assert.Equal(dispatch, history.Issued);
            Assert.Null(history.Applied);
            Assert.Equal(command.Operation, dispatch.Operation);
            var commission = Assert.IsType<SupervisedLaunchCommission>(snapshot.Frame.Commission);
            Assert.Equal(command.Head, commission.Topology.Head);
            Assert.Equal(command.SocketPath, commission.Grant(command.Operation.Engine).SocketPath);
            Assert.Equal(expectedWire, bytes.ToArray());
            Assert.True(dispatch.MatchesDispatching(HostReservation.ReadEnrolled(command.Scope)));
            Assert.Throws<IOException>(() =>
            {
                using var unexpected = HostReservation.OpenEnrolled(command.Scope);
            });
            Creates++;
            observer.RecordCreate(command);
            return ValueTask.FromResult<string?>(ReceiptInstance);
        }

        public ValueTask<bool> StartAsync(SupervisedLaunchCommand command, string instance, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(ReceiptInstance, instance);
            Assert.Equal(command.Operation, journal.Read()?.Frame.History.Issued?.Operation);
            Starts++;
            observer.RecordStart(command);
            return ValueTask.FromResult(true);
        }
    }
}
