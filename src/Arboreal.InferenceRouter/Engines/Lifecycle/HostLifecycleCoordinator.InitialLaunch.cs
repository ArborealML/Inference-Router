using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record HostInitialLaunchIntent(HostCheckpointPlan Plan, string CreateBytes, string CreateDigest)
{
    public static HostInitialLaunchIntent Freeze(HostCheckpointPlan plan, ReadOnlySpan<byte> bytes) =>
        new(plan, Convert.ToBase64String(bytes), Convert.ToHexString(SHA256.HashData(bytes)));

    public bool IsValid() => Plan.IsValid() && CreateBytes.Length > 0 &&
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(CreateBytes))) == CreateDigest;
}

internal sealed record HostInitialPolicy(HostReservationScope Scope, long OwnerGeneration, long PolicyGeneration,
    bool CompletePair, bool ExclusiveWriter, bool IngressClosed, bool ForeignResident)
{
    public bool Authorizes(HostReservationScope scope) => Scope == scope && OwnerGeneration > 0 && PolicyGeneration > 0 &&
        CompletePair && ExclusiveWriter && IngressClosed && !ForeignResident;
}

internal sealed record HostInitialCreate(HostMutation Operation, string InstanceId, string EffectiveCreateDigest, bool CompletePair);
internal sealed record HostInitialStart(HostMutation Operation, string InstanceId);
internal sealed record HostInitialObservation(HostInitialPolicy Policy, HostInitialCreate? Create = null, HostInitialStart? Start = null);

internal sealed record HostInitialDispatch(HostInitialPolicy Policy, HostMutation Operation, HostInitialLaunchIntent Intent,
    long LocalRevision, string LocalDigest)
{
    public bool MatchesDispatching(HostEnrolledSnapshot installed) =>
        installed.Record.Scope == Policy.Scope && installed.Record.Revision == LocalRevision && installed.Digest == LocalDigest &&
        installed.Record.State.Phase == HostMutationPhase.Dispatching && installed.Record.State.Pending == Operation &&
        Intent.Plan.Matches(Operation, installed.Record.State.Epoch) && Intent.IsValid();
}

internal sealed record HostInitialReceipt(HostReservationScope Scope, HostMutation Operation, HostInitialLaunchIntent Intent, string InstanceId);
internal sealed record HostInitialApplied(HostInitialReceipt Receipt, string ReceiptBytes);
internal sealed record HostInitialHistory(HostReservationScope Scope, string GenesisDigest, HostInitialDispatch? Dispatch = null,
    HostInitialDispatch? Accepted = null, HostInitialDispatch? Issued = null, HostInitialApplied? Applied = null);
internal sealed record HostAppliedIngressClosed(HostInitialReceipt Receipt);

// The authority provides installed, anchored history and a distinct read-only observation. Its
// issue fence also serializes reservation installs. No production implementation is registered.
internal interface IHostInitialLaunchAuthority
{
    IDisposable EnterIssueFence(HostReservationScope scope);
    HostInitialHistory? ReadHistory(HostReservationScope scope);
    HostInitialObservation? Observe(HostReservationScope scope);
    bool Approves(HostReservationScope scope, HostInitialLaunchIntent intent);
    void AnchorDispatch(HostInitialDispatch dispatch);
    ValueTask<HostMutationOutcome> LaunchAsync(HostInitialDispatch dispatch, CancellationToken ct);
    ValueTask<HostMutationOutcome> ReconcileAsync(HostInitialDispatch dispatch, CancellationToken ct);
}

[JsonSerializable(typeof(HostInitialReceipt))]
internal sealed partial class HostReservationJsonContext;

internal sealed partial class HostLifecycleCoordinator
{
    internal Action? AfterInitialGrantForTests { private get; init; }

    internal async ValueTask<HostAppliedIngressClosed?> LaunchInitialClosedAsync(HostInitialLaunchIntent intent, CancellationToken ct)
    {
        if (_initialLaunch is not { } authority) return null;
        try
        {
            var scope = HostReservationScope.Read(_reservationPath);
            if (scope.IsLegacy) return null;
            using var reservation = HostReservation.OpenEnrolled(scope, () => ReadEnrolledInitialSlot(authority, scope));
            var installed = reservation.Enrolled!;
            var state = installed.Record.State;
            var isNew = state.Epoch == 0;
            HostInitialDispatch dispatch;
            using (authority.EnterIssueFence(scope))
            {
                if (HostReservation.ReadEnrolled(scope) != installed || authority.ReadHistory(scope) is not { } history ||
                    history.Scope != scope || !intent.IsValid() || !authority.Approves(scope, intent)) return null;

                if (state.Phase == HostMutationPhase.LaunchApplied)
                    return TryRecordInitialOutcome(reservation, history, scope, intent, authority.Observe(scope), reply: null);

                if (isNew)
                {
                    if (history != new HostInitialHistory(scope, installed.Digest) || installed.Record.Revision != 0 ||
                        authority.Observe(scope) is not { Create: null, Start: null } vacant || !vacant.Policy.Authorizes(scope))
                        return null;
                    ct.ThrowIfCancellationRequested();
                    var epoch = reservation.Grant(intent.Plan.Engine);
                    AfterInitialGrantForTests?.Invoke();
                    var operation = reservation.Begin(epoch, HostMutationKind.Launch, intent.Plan.Engine,
                        intent.Plan.Model, intent.Plan.Tier, intent.Plan.Digest());
                    reservation.MarkDispatching(operation);
                    var placed = reservation.Enrolled!;
                    dispatch = new(vacant.Policy, operation, intent, placed.Record.Revision, placed.Digest);
                    authority.AnchorDispatch(dispatch);
                }
                else
                {
                    // A valid epoch with no operation, or a partial dispatch install, cannot become genesis.
                    if (history.Dispatch is not { } pending || pending.Intent != intent || !pending.MatchesDispatching(installed))
                        return null;
                    dispatch = pending;
                }
            }

            var reply = await ReadOutcomeAsync(() => isNew ? authority.LaunchAsync(dispatch, ct)
                : authority.ReconcileAsync(dispatch, ct)).ConfigureAwait(false);
            using var fence = authority.EnterIssueFence(scope);
            if (authority.ReadHistory(scope) is not { } applied) return null;
            return TryRecordInitialOutcome(reservation, applied, scope, intent, authority.Observe(scope), reply);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    private static HostEnrolledSnapshot ReadEnrolledInitialSlot(IHostInitialLaunchAuthority authority, HostReservationScope scope)
    {
        using var fence = authority.EnterIssueFence(scope);
        if (authority.ReadHistory(scope)?.Scope != scope) throw new IOException("The enrolled slot has no current anchored scope.");
        return HostReservation.ReadEnrolled(scope);
    }

    internal byte[]? ReadInitialLaunchReceipt(HostMutation operation, string instance)
    {
        if (_initialLaunch is not { } authority) return null;
        try
        {
            var scope = HostReservationScope.Read(_reservationPath);
            if (scope.IsLegacy) return null;
            using var reservation = HostReservation.OpenEnrolled(scope);
            using var fence = authority.EnterIssueFence(scope);
            var installed = HostReservation.ReadEnrolled(scope);
            if (installed != reservation.Enrolled || installed.Record.State.Phase != HostMutationPhase.LaunchApplied ||
                installed.Record.State.Pending != operation || installed.Record.State.LaunchedContainerId != instance ||
                authority.ReadHistory(scope) is not { } history || MatchInitialApplied(installed, history) is not { } applied)
                return null;

            return Convert.FromBase64String(applied.ReceiptBytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    private static HostAppliedIngressClosed? TryRecordInitialOutcome(HostReservation reservation, HostInitialHistory history,
        HostReservationScope scope, HostInitialLaunchIntent intent, HostInitialObservation? observed, HostMutationOutcome? reply)
    {
        var installed = HostReservation.ReadEnrolled(scope);
        if (installed != reservation.Enrolled || MatchInitialApplied(installed, history) is not { Receipt: var receipt } ||
            receipt.Intent != intent || history.Dispatch is not { } dispatch || observed is not { } observation ||
            observation.Policy != dispatch.Policy || observation.Create is not { CompletePair: true } create ||
            observation.Start is not { } start ||
            create.Operation != receipt.Operation || start.Operation != receipt.Operation ||
            create.InstanceId != receipt.InstanceId || start.InstanceId != receipt.InstanceId ||
            create.EffectiveCreateDigest != intent.CreateDigest) return null;

        if (installed.Record.State.Phase == HostMutationPhase.Dispatching)
        {
            if (reply != new HostMutationOutcome(receipt.Operation, HostMutationStatus.Applied, receipt.InstanceId)) return null;
            reservation.RecordAppliedLaunch(receipt.Operation, receipt.InstanceId);
        }
        return new HostAppliedIngressClosed(receipt);
    }

    private static HostInitialApplied? MatchInitialApplied(HostEnrolledSnapshot installed, HostInitialHistory history)
    {
        var record = installed.Record;
        var state = record.State;
        if (history.Scope != record.Scope || history.Dispatch is not { } dispatch || history.Accepted != dispatch ||
            history.Issued != dispatch || history.Applied is not { Receipt: var receipt } applied ||
            !dispatch.Policy.Authorizes(record.Scope) || !dispatch.Intent.IsValid() ||
            !dispatch.Intent.Plan.Matches(dispatch.Operation, state.Epoch) || state.Pending != dispatch.Operation ||
            receipt.Scope != record.Scope || receipt.Operation != dispatch.Operation || receipt.Intent != dispatch.Intent ||
            string.IsNullOrWhiteSpace(receipt.InstanceId)) return null;

        var matchesLocal = state.Phase == HostMutationPhase.LaunchApplied
            ? record.Revision == dispatch.LocalRevision + 1 && record.Predecessor == dispatch.LocalDigest &&
                state.LaunchedContainerId == receipt.InstanceId
            : dispatch.MatchesDispatching(installed);
        if (!matchesLocal) return null;

        var decoded = JsonSerializer.Deserialize(Convert.FromBase64String(applied.ReceiptBytes),
            HostReservationJsonContext.Default.HostInitialReceipt);
        return decoded == receipt ? applied : null;
    }
}
