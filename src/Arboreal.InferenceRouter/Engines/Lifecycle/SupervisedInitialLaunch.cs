using System.Text;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

// Internal composition for one previously commissioned slot. No public router registration.
internal sealed class SupervisedInitialLaunch(HostReservationScope scope, string commissionedSlot,
    ISupervisedLaunchHead head, ISupervisedLaunchObserver observer, ISupervisedLaunchPort port,
    IHostLifecycleProvider legacy, HostClosedRouteStaging? stagingForTests = null)
{
    private readonly SupervisedLaunchJournal _journal = new(scope, head);
    internal Func<CancellationToken, ValueTask>? AfterAcceptForTests { private get; init; }

    internal void ProvisionJournal() => _journal.Provision();

    // The configuration and claim come from the supervisor's bootstrap. An incoming launch only selects the member.
    internal async ValueTask<HostAppliedIngressClosed?> RunAsync(string engine, string config, EnrolledPairClaim claim,
        CancellationToken ct)
    {
        try
        {
            SupervisedLaunchCommission commission;
            HostInitialLaunchIntent intent;
            using (_journal.EnterFence())
            {
                if (_journal.Read()?.Frame is not { } frame) return null;
                if (frame.History.Dispatch is { } original)
                {
                    if (original.Operation.Engine != engine || frame.Commission is not { } recorded) return null;
                    commission = recorded;
                    intent = original.Intent;
                }
                else
                {
                    if (ReadCommission() is not { } current) return null;
                    var pair = RouterConfigLoader.PrepareEnrolledPair(config, commissionedSlot, claim, _ => current.Topology);
                    if (current.Approvals.IsDefault || current.Approvals.Length != pair.Members.Length ||
                        current.Approvals.Select(item => item.Intent.Plan.Engine).Distinct(StringComparer.Ordinal).Count() != pair.Members.Length ||
                        !pair.Members.All(member => MatchesProjection(member, current.Approval(member.Plan.Engine))) ||
                        current.Approval(engine) is not { } approved) return null;
                    commission = current;
                    intent = approved.Intent;
                }
            }

            return await Coordinator(commission).LaunchInitialClosedAsync(intent, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException or RouterConfigException)
        {
            return null;
        }
    }

    internal byte[]? ReadReceipt(HostMutation operation, string instance) =>
        Coordinator(commission: null).ReadInitialLaunchReceipt(operation, instance);

    private HostLifecycleCoordinator Coordinator(SupervisedLaunchCommission? commission) => new(scope.Slot,
        legacy, stagingForTests, new Authority(scope, _journal, commission, ReadCommission, observer, port, AfterAcceptForTests));

    private SupervisedLaunchCommission? ReadCommission()
    {
        var current = observer.ReadCommission(scope);
        return current is not null && current.Scope == scope && scope.Enrollment is { } enrollment &&
            current.Topology.Head.Slot == commissionedSlot && current.Topology.Head.Partition == enrollment.Group &&
            current.Topology.Head.Generation == enrollment.Generation ? current : null;
    }

    private static bool MatchesProjection(CheckedEnrolledPairMember member, SupervisedLaunchApproval? approved) =>
        approved is not null && member.Image == approved.Image && member.PlanDigest == approved.Intent.Plan.Digest() &&
        HostInitialLaunchIntent.Freeze(member.Plan, Encoding.UTF8.GetBytes(member.CreateJson)) == approved.Intent;

    private sealed class Authority(HostReservationScope scope, SupervisedLaunchJournal journal,
        SupervisedLaunchCommission? commission, Func<SupervisedLaunchCommission?> readCommission,
        ISupervisedLaunchObserver observer, ISupervisedLaunchPort port,
        Func<CancellationToken, ValueTask>? afterAccept) : IHostInitialLaunchAuthority
    {
        public IDisposable EnterIssueFence(HostReservationScope selected) => selected == scope
            ? journal.EnterFence() : throw new IOException("The launch writer belongs to a different scope.");

        public HostInitialHistory? ReadHistory(HostReservationScope selected) =>
            selected == scope ? journal.Read()?.Frame.History : null;

        public bool Approves(HostReservationScope selected, HostInitialLaunchIntent intent)
        {
            if (selected != scope || commission is null || !commission.Matches(readCommission()) ||
                commission.Approval(intent.Plan.Engine) is not { } approved || approved.Intent != intent) return false;
            var grant = commission.Grant(intent.Plan.Engine);
            return observer.ReadImage(commission.Topology.Head, grant.SocketPath, approved.Image.Id) == approved.Image;
        }

        public HostInitialObservation? Observe(HostReservationScope selected)
        {
            if (selected != scope || commission is null || !commission.Matches(readCommission()) ||
                observer.ReadPolicy(scope) is not { } observed || !observed.Policy.Authorizes(scope) ||
                journal.Read()?.Frame is not { } frame) return null;
            if (frame.History.Dispatch is null)
                return observed.Vacant ? new(observed.Policy) : null;

            if (!commission.Matches(frame.Commission) || frame.History.Dispatch is not { } dispatch ||
                dispatch.Policy != observed.Policy || observed.Vacant ||
                frame.History.Applied is not { Receipt: var receipt } ||
                !Approves(scope, dispatch.Intent)) return null;

            var command = Command(commission, dispatch);
            var create = ReadCreate(commission, dispatch, command);
            var start = ReadStart(command, receipt.InstanceId);
            return create is not null && start is not null ? new(observed.Policy, create, start) : null;
        }

        public void AnchorDispatch(HostInitialDispatch dispatch)
        {
            if (commission is null || journal.Read() is not { Frame.History.Dispatch: null } original ||
                !CurrentForDispatch(dispatch, vacant: true))
                throw new IOException("The supervised launch cannot anchor the installed dispatch.");
            journal.Advance(original, original.Frame.History with { Dispatch = dispatch }, commission);
        }

        public async ValueTask<HostMutationOutcome> LaunchAsync(HostInitialDispatch dispatch, CancellationToken ct)
        {
            if (commission is null) return default;
            using (EnterIssueFence(scope))
            {
                if (journal.Read() is not { Frame.History: { Accepted: null, Issued: null } history } original ||
                    history.Dispatch != dispatch || !commission.Matches(original.Frame.Commission) ||
                    !CurrentForDispatch(dispatch, vacant: true)) return default;
                ct.ThrowIfCancellationRequested();
                journal.Advance(original, history with { Accepted = dispatch }, commission);
            }

            if (afterAccept is not null) await afterAccept(ct).ConfigureAwait(false);
            using var fence = EnterIssueFence(scope);
            if (journal.Read() is not { Frame.History: { Issued: null } accepted } installed ||
                accepted.Dispatch != dispatch || accepted.Accepted != dispatch || !commission.Matches(installed.Frame.Commission) ||
                !CurrentForDispatch(dispatch, vacant: true)) return default;

            ct.ThrowIfCancellationRequested();
            journal.Advance(installed, accepted with { Issued = dispatch }, commission);
            if (!CurrentForDispatch(dispatch, vacant: true)) return default;

            var command = Command(commission, dispatch);
            var bytes = Convert.FromBase64String(dispatch.Intent.CreateBytes);
            ct.ThrowIfCancellationRequested();
            var instance = await port.CreateAsync(command, bytes, ct).ConfigureAwait(false);
            var create = ReadCreate(commission, dispatch, command);
            if (instance is null || create?.InstanceId != instance || !CurrentForDispatch(dispatch, vacant: false)) return default;

            ct.ThrowIfCancellationRequested();
            if (!await port.StartAsync(command, instance, ct).ConfigureAwait(false) || ReadStart(command, instance) is null ||
                ReadCreate(commission, dispatch, command)?.InstanceId != instance ||
                !CurrentForDispatch(dispatch, vacant: false) ||
                journal.Read() is not { Frame.History: { Applied: null } issued } issuedSnapshot ||
                issued.Issued != dispatch) return default;

            var receipt = new HostInitialReceipt(scope, dispatch.Operation, dispatch.Intent, instance);
            var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, HostReservationJsonContext.Default.HostInitialReceipt);
            journal.Advance(issuedSnapshot, issued with { Applied = new(receipt, Convert.ToBase64String(receiptBytes)) }, commission);
            return new(dispatch.Operation, HostMutationStatus.Applied, instance);
        }

        public ValueTask<HostMutationOutcome> ReconcileAsync(HostInitialDispatch dispatch, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var fence = EnterIssueFence(scope);
            var history = journal.Read()?.Frame.History;
            return ValueTask.FromResult(history is { Applied.Receipt: var receipt } && history.Dispatch == dispatch
                ? new HostMutationOutcome(receipt.Operation, HostMutationStatus.Applied, receipt.InstanceId) : default);
        }

        private bool CurrentForDispatch(HostInitialDispatch dispatch, bool vacant) =>
            dispatch.MatchesDispatching(HostReservation.ReadEnrolled(scope)) && Approves(scope, dispatch.Intent) &&
            dispatch.Policy.Authorizes(scope) && observer.ReadPolicy(scope) == new SupervisedLaunchPolicy(dispatch.Policy, vacant);

        private static SupervisedLaunchCommand Command(SupervisedLaunchCommission original, HostInitialDispatch dispatch) =>
            new(original.Scope, original.Topology.Head, original.Grant(dispatch.Operation.Engine).SocketPath, dispatch.Operation);

        private HostInitialCreate? ReadCreate(SupervisedLaunchCommission original, HostInitialDispatch dispatch,
            SupervisedLaunchCommand command) => SupervisedLaunchEvidence.VerifyCreate(observer.ReadCreate(command), command,
                original.Approval(dispatch.Operation.Engine)!, original.Grant(dispatch.Operation.Engine), original.Topology.Devices);

        private HostInitialStart? ReadStart(SupervisedLaunchCommand command, string instance) =>
            observer.ReadStart(command) == new SupervisedObservedStart(command, instance, Running: true)
                ? new(command.Operation, instance) : null;
    }
}
