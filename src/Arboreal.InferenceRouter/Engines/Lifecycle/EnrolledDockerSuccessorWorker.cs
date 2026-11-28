using System.Collections.Immutable;

namespace Arboreal.InferenceRouter.Lifecycle;

internal enum SuccessorIssuePhase { Accepted, MayHaveIssued, StartAnchored, Applied }

internal sealed record SuccessorIssueBinding(SupervisedLaunchCommand Command, SwitchAttemptId Attempt,
    HostEffectCheckpointCommitment Checkpoint, EnrolledSuccessorRetirement Retirement,
    HostInitialLaunchIntent Successor, Guid QueryId, long CoverageGeneration);

internal sealed record SuccessorIssueRecord(SuccessorIssueBinding Binding, SuccessorIssuePhase Phase,
    SupervisedStartIssue? Start = null, HostMutationOutcome? Outcome = null);

internal sealed record SuccessorIssueHead(HostReservationScope Scope, EnrolledPairHead Commission, long Revision,
    string Digest, HostCheckpointLaunchEffect InitialResident, SuccessorIssueRecord? Current = null);

internal sealed record EnrolledSuccessorAppliedWitness(HostEffectCheckpointCommitment Checkpoint, SuccessorIssueHead Journal);

// A commissioned implementation owns the single writer fence, checks the independently protected
// successor head and persists/reads exact predecessor transitions. A missing or uncertain head must
// refuse entry or advancement. It must forbid acceptance of a repeated or previously unresolved issue.
internal interface IEnrolledSuccessorOperationJournal
{
    IEnrolledSuccessorIssueFence? TryEnter(HostReservationScope scope);
}

internal interface IEnrolledSuccessorIssueFence : IDisposable
{
    // Null is unavailable evidence. Genesis is an authenticated revision-zero head with an exact resident.
    SuccessorIssueHead? ReadHead();
    bool TryAppend(SuccessorIssueHead predecessor, SuccessorIssueRecord next);
}

internal sealed record EnrolledSuccessorWorkerObservation(EnrolledSuccessorObservation Host,
    EnrolledSuccessorRetirement Retirement, SupervisedObservedCreate? Create = null, SupervisedObservedStart? Running = null);

internal interface IEnrolledSuccessorWorkerObserver : IEnrolledSuccessorObserver
{
    ValueTask<EnrolledSuccessorWorkerObservation?> ObserveWorkerAsync(EnrolledSuccessorIssue issue, CancellationToken ct);
}

// Used at the controller's installed-owner handoff. There is no registration on public startup.
// Neither the observer nor the journal can be supplied by the queued caller.
internal sealed class EnrolledDockerSuccessorWorker(string checkpointLock, HostReservationScope scope,
    RouterConfig configuration, CheckedEnrolledCatalog catalog, IEnrolledSuccessorOperationJournal journal,
    IEnrolledSuccessorWorkerObserver observer, HttpMessageHandler? daemonForTests = null) : IEnrolledSuccessorHost
{
    private readonly ImmutableDictionary<string, DockerLifecycleConfig> _docker = configuration.Engines.Values
        .Where(engine => catalog.OwnsConfiguredEngine(engine) && engine.Docker is not null)
        .ToImmutableDictionary(engine => engine.Name, engine => engine.Docker!, StringComparer.Ordinal);

    // The startup controller owns the checkpoint before invoking this journal read.
    internal bool MatchesInitialAnchor(HostCheckpointLaunchEffect launch)
    {
        try
        {
            using var fence = journal.TryEnter(scope);
            return fence?.ReadHead() is { Revision: 0, Current: null } head && head.Scope == scope &&
                head.Commission == catalog.Head && head.InitialResident == launch;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            return false;
        }
    }

    internal EnrolledSuccessorRuntime BindRuntime(EnrolledSuccessorScope commissionedScope, Func<HostStagedDeadlinePublication, EnrolledSuccessorApplied, EnrolledSuccessorAppliedWitness,
            CancellationToken, ValueTask<HostStagedSwitchResult>> stage)
    {
        if (commissionedScope.Reservation != scope || commissionedScope.Commission.Head != catalog.Head ||
            !SupervisedLaunchCommission.SameDevices(commissionedScope.Commission.Devices, catalog.Devices) ||
            catalog.Targets.IsDefaultOrEmpty || !catalog.Targets.All(commissionedScope.Approves))
            throw new ArgumentException("The worker and runtime must use the same commissioned catalog.", nameof(commissionedScope));

        var controller = new EnrolledSuccessorController(checkpointLock, commissionedScope, catalog, this);
        return new(controller, (publication, applied, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var witness = ReferenceEquals(applied.Installed.Offer.Controller, controller) &&
                ReferenceEquals(applied.Scope, commissionedScope) ? ReadAppliedWitness(applied) : null;
            ct.ThrowIfCancellationRequested();
            return witness is not null
                ? stage(publication, applied, witness, ct)
                : ValueTask.FromResult(new HostStagedSwitchResult(
                    LifecycleResult.Fail("host_successor_receipt_unavailable",
                        "the protected Applied receipt does not match the installed successor"), default));
        });
    }

    // The controller holds the checkpoint reservation. Release the journal fence before
    // entering the broker so it can arbitrate its closed observation under its own guard.
    private EnrolledSuccessorAppliedWitness? ReadAppliedWitness(EnrolledSuccessorApplied applied)
    {
        try
        {
            using var fence = journal.TryEnter(scope);
            if (fence?.ReadHead() is not { Revision: > 0, Current: { Phase: SuccessorIssuePhase.Applied } record } head ||
                head.Scope != scope || head.Commission != catalog.Head || !HostMutation.IsPlanDigest(head.Digest))
                return null;

            var binding = record.Binding;
            var command = binding.Command;
            var successor = applied.Installed.Offer.Successor;
            var launch = applied.Launch;
            var dispatch = binding.Checkpoint.State;
            if (applied.Scope.Reservation != scope || applied.Scope.Commission.Head != catalog.Head ||
                !catalog.Targets.Contains(successor) || binding.Attempt != applied.Installed.Attempt ||
                binding.Retirement != applied.Retirement ||
                binding.Successor != successor.Intent || launch.Plan != successor.Plan ||
                command != new SupervisedLaunchCommand(scope, catalog.Head, successor.EngineGrant.SocketPath,
                    launch.Operation, successor.ApiVersion) ||
                record.Start != new SupervisedStartIssue(command, launch.InstanceId, successor.Intent.CreateDigest) ||
                record.Outcome != new HostMutationOutcome(launch.Operation, HostMutationStatus.Applied, launch.InstanceId) ||
                dispatch is not { Phase: HostCheckpointPhase.LaunchDispatching, Revision: < long.MaxValue,
                    Transition: { } transition } || transition.Launch != launch.Operation ||
                dispatch.AppliedLaunch != applied.Stopped.Incumbent || transition.StopEffect != applied.Stopped.Effect)
                return null;

            var checkpoint = HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock);
            var expected = dispatch with
            {
                Phase = HostCheckpointPhase.Resident,
                Revision = dispatch.Revision + 1,
                AppliedLaunch = launch,
                LastStoppedResident = applied.Stopped,
                Transition = null,
            };
            return checkpoint.State == expected && fence.ReadHead() == head ? new(checkpoint, head) : null;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            return null;
        }
    }

    public ValueTask<EnrolledSuccessorObservation?> ObserveAsync(EnrolledSuccessorQuery query, CancellationToken ct) =>
        observer.ObserveAsync(query, ct);

    public ValueTask<bool> PreflightAsync(EnrolledSuccessorQuery query, CancellationToken ct) =>
        catalog.Targets.Contains(query.Successor) && query.Scope.Approves(query.Successor)
            ? observer.PreflightAsync(query, ct) : ValueTask.FromResult(false);

    public ValueTask<EnrolledSuccessorRetirement?> CloseAndDrainAsync(EnrolledSuccessorQuery query, CancellationToken ct) =>
        observer.CloseAndDrainAsync(query, ct);

    public ValueTask<HostCheckpointStopEffect?> ObserveStopAsync(EnrolledSuccessorIssue issue, CancellationToken ct) =>
        observer.ObserveStopAsync(issue, ct);

    public ValueTask<HostCheckpointLaunchEffect?> ObserveLaunchAsync(EnrolledSuccessorIssue issue, CancellationToken ct) =>
        observer.ObserveLaunchAsync(issue, ct);

    public ValueTask<HostMutationOutcome> StopAsync(EnrolledSuccessorIssue issue, CancellationToken ct) =>
        ExecuteAsync(issue, HostCheckpointPhase.StopDispatching, ct);

    public ValueTask<HostMutationOutcome> LaunchAsync(EnrolledSuccessorIssue issue, CancellationToken ct) =>
        ExecuteAsync(issue, HostCheckpointPhase.LaunchDispatching, ct);

    private async ValueTask<HostMutationOutcome> ExecuteAsync(EnrolledSuccessorIssue issue, HostCheckpointPhase phase,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var successor = issue.Query.Successor;
        if (issue.Query.Scope.Reservation != scope || issue.Attempt.RuntimeEpoch == Guid.Empty || issue.Attempt.Sequence <= 0 ||
            issue.Dispatching.Phase != phase || issue.Dispatching.Transition is not { } transition ||
            transition.Plan != successor.Plan || !catalog.Targets.Contains(successor) || catalog.Head != issue.Query.Scope.Commission.Head ||
            !SupervisedLaunchCommission.SameDevices(catalog.Devices, issue.Query.Scope.Commission.Devices))
            return default;

        var isStop = phase == HostCheckpointPhase.StopDispatching;
        var operation = isStop ? transition.Stop : transition.Launch;
        var target = isStop ? Target(issue.Retirement.Incumbent.Plan) : successor;
        if (operation is null || target is null || !issue.Query.Scope.Approves(successor) ||
            !issue.Query.Scope.Approves(target) || !_docker.TryGetValue(target.Key.Engine, out var docker) ||
            docker.ApiVersion != target.ApiVersion ||
            !DockerLifecycleProvider.TryParseUnixSocket(docker.Socket, out var socketPath) ||
            socketPath != target.EngineGrant.SocketPath || target.EngineGrant.Daemon != catalog.Head.Daemon) return default;

        var unresolved = new HostMutationOutcome(operation, HostMutationStatus.UnknownAfterIssue);
        try
        {
            using var fence = journal.TryEnter(scope);
            if (fence is null) return unresolved;
            var checkpoint = HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock);
            if (!await CanEnterAsync(issue, checkpoint, ct).ConfigureAwait(false)) return unresolved;

            var command = new SupervisedLaunchCommand(scope, catalog.Head, socketPath, operation, target.ApiVersion);
            var accepted = new SuccessorIssueRecord(new(command, issue.Attempt, checkpoint, issue.Retirement,
                successor.Intent, issue.Query.Id, issue.Observation.CoverageGeneration), SuccessorIssuePhase.Accepted);
            if (fence.ReadHead() is not { } head || !CanAccept(head, accepted.Binding) ||
                Append(fence, head, accepted) is not { } acceptedHead ||
                !await CanEnterAsync(issue, checkpoint, ct).ConfigureAwait(false)) return unresolved;

            var issuing = accepted with { Phase = SuccessorIssuePhase.MayHaveIssued };
            if (Append(fence, acceptedHead, issuing) is not { } issueHead ||
                !await CanEnterAsync(issue, checkpoint, ct).ConfigureAwait(false) || fence.ReadHead() != issueHead) return unresolved;

            ct.ThrowIfCancellationRequested();
            var authority = new CommandAuthority(command);
            using var port = new DockerLifecycleProvider.SupervisedCommands(scope, catalog.Head, socketPath,
                target.ApiVersion, docker.CallTimeoutMs, authority, daemonForTests);
            if (isStop)
            {
                authority.Stop = new(scope, catalog.Head, socketPath, target.ApiVersion, operation,
                    issue.Retirement.Incumbent, Math.Max(1, docker.StopTimeoutMs / 1000));
                if (await port.StopAsync(operation, ct).ConfigureAwait(false) is not (NativeStopDisposition.Stopped or
                    NativeStopDisposition.AlreadyStopped or NativeStopDisposition.Absent)) return unresolved;
                if (await observer.ObserveStopAsync(issue, ct).ConfigureAwait(false) is not { } stop ||
                    !stop.Matches(operation, issue.Retirement.Incumbent) ||
                    !CurrentAuthority(issue, await observer.ObserveWorkerAsync(issue, ct).ConfigureAwait(false)) ||
                    !CheckpointMatches(checkpoint)) return unresolved;
                ct.ThrowIfCancellationRequested();
                return RecordApplied(fence, issueHead, issuing, stop.InstanceId, unresolved);
            }

            authority.Create = successor.Intent;
            var bytes = Convert.FromBase64String(successor.Intent.CreateBytes);
            var id = await port.CreateAsync(command, bytes, ct).ConfigureAwait(false);
            if (id is null || await ReadCreateAsync(issue, command, successor, checkpoint, ct).ConfigureAwait(false)
                    is not { } created || created.InstanceId != id) return unresolved;

            var start = new SupervisedStartIssue(command, id, created.EffectiveCreateDigest);
            var anchored = issuing with { Phase = SuccessorIssuePhase.StartAnchored, Start = start };
            if (Append(fence, issueHead, anchored) is not { } startHead ||
                await ReadCreateAsync(issue, command, successor, checkpoint, ct).ConfigureAwait(false) != created ||
                fence.ReadHead() != startHead)
                return unresolved;

            authority.Start = new(successor.Intent, created, start);
            if (!await port.StartAsync(command, id, ct).ConfigureAwait(false)) return unresolved;
            var running = await observer.ObserveWorkerAsync(issue, ct).ConfigureAwait(false);
            if (running is null || !CurrentAuthority(issue, running) || running.Running != new SupervisedObservedStart(command, id, Running: true) ||
                VerifyCreate(command, successor, running) != created ||
                await observer.ObserveLaunchAsync(issue, ct).ConfigureAwait(false) != new HostCheckpointLaunchEffect(operation,
                    successor.Intent.Plan, id) || !CheckpointMatches(checkpoint)) return unresolved;
            ct.ThrowIfCancellationRequested();
            return RecordApplied(fence, startHead, anchored, id, unresolved);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            ct.ThrowIfCancellationRequested();
            return unresolved;
        }
    }

    private CheckedEnrolledTarget? Target(HostCheckpointPlan plan) =>
        catalog.Targets.FirstOrDefault(target => target.Intent.Plan == plan);

    private bool CheckpointMatches(HostEffectCheckpointCommitment checkpoint) =>
        HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock) == checkpoint;

    private async ValueTask<bool> CanEnterAsync(EnrolledSuccessorIssue issue, HostEffectCheckpointCommitment checkpoint,
        CancellationToken ct)
    {
        var current = await observer.ObserveWorkerAsync(issue, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return CheckpointMatches(checkpoint) && issue.CanIssue(checkpoint.State, current?.Host, current?.Retirement);
    }

    private async ValueTask<HostInitialCreate?> ReadCreateAsync(EnrolledSuccessorIssue issue, SupervisedLaunchCommand command,
        CheckedEnrolledTarget target, HostEffectCheckpointCommitment checkpoint, CancellationToken ct)
    {
        var current = await observer.ObserveWorkerAsync(issue, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return current is not null && CurrentAuthority(issue, current) && CheckpointMatches(checkpoint)
            ? VerifyCreate(command, target, current) : null;
    }

    private static bool CurrentAuthority(EnrolledSuccessorIssue issue, EnrolledSuccessorWorkerObservation? current) =>
        current is { Host: { IngressClosed: true, CompleteCoverage: true } host } &&
        host.Occupancy is HostOccupancyKind.Owned or HostOccupancyKind.Vacant &&
        current.Retirement == issue.Retirement && host.QueryId == issue.Query.Id &&
        host.OwnerGeneration == issue.Observation.OwnerGeneration && host.CoverageGeneration == issue.Observation.CoverageGeneration &&
        host.IngressGeneration == issue.Observation.IngressGeneration && issue.Query.Scope.Matches(host.Scope);

    private HostInitialCreate? VerifyCreate(SupervisedLaunchCommand command, CheckedEnrolledTarget target,
        EnrolledSuccessorWorkerObservation observation)
    {
        var verified = SupervisedLaunchEvidence.VerifyCreate(observation.Create, command,
            new(target.Intent, target.Image, target.ApiVersion), target.Intent.Plan.BackendRoute, target.Target.Endpoint, catalog.Devices);
        return verified is not null && observation.Host.Occupancy == HostOccupancyKind.Owned &&
            observation.Host.Resident == new HostCheckpointLaunchEffect(command.Operation,
            target.Intent.Plan, verified.InstanceId) ? verified : null;
    }

    internal static bool CanAccept(SuccessorIssueHead head, SuccessorIssueBinding requested)
    {
        var command = requested.Command;
        var incumbent = requested.Retirement.Incumbent;
        if (head.Scope != command.Scope || head.Commission != command.Head || !HostMutation.IsPlanDigest(head.Digest) ||
            head.Revision < 0 || head.Revision == long.MaxValue || !head.InitialResident.IsValid()) return false;

        if (head.Revision == 0)
            return head.Current is null && command.Operation.Kind == HostMutationKind.Stop && head.InitialResident == incumbent;

        if (head.Current is not { Phase: SuccessorIssuePhase.Applied } prior || prior.Binding.Command.Scope != head.Scope ||
            prior.Binding.Command.Head != head.Commission || !HostMutation.IsPlanDigest(prior.Binding.Checkpoint.Digest)) return false;

        if (command.Operation.Kind == HostMutationKind.Stop)
            return prior.Binding.Command.Operation == incumbent.Operation && prior.Binding.Successor.Plan == incumbent.Plan &&
                prior.Start == new SupervisedStartIssue(prior.Binding.Command, incumbent.InstanceId, prior.Binding.Successor.CreateDigest) &&
                prior.Outcome == new HostMutationOutcome(incumbent.Operation, HostMutationStatus.Applied, incumbent.InstanceId);

        var stop = requested.Retirement.Stop;
        var installed = requested.Checkpoint.State;
        return prior.Binding.Command.Operation == stop && prior.Binding.Attempt == requested.Attempt &&
            prior.Binding.Retirement == requested.Retirement && prior.Binding.Successor == requested.Successor &&
            prior.Binding.Checkpoint.State.Phase == HostCheckpointPhase.StopDispatching &&
            prior.Binding.Checkpoint.State.Transition?.Stop == stop && prior.Start is null &&
            prior.Outcome == new HostMutationOutcome(stop, HostMutationStatus.Applied, incumbent.InstanceId) &&
            installed.Transition?.StopEffect?.Matches(stop, incumbent) == true;
    }

    private static SuccessorIssueHead? Append(IEnrolledSuccessorIssueFence fence, SuccessorIssueHead predecessor,
        SuccessorIssueRecord next)
    {
        if (predecessor.Revision == long.MaxValue || fence.ReadHead() != predecessor ||
            !fence.TryAppend(predecessor, next) || fence.ReadHead() is not { } installed ||
            !HostMutation.IsPlanDigest(installed.Digest) || installed.Digest == predecessor.Digest) return null;

        return installed == (predecessor with { Revision = predecessor.Revision + 1, Digest = installed.Digest, Current = next })
            ? installed : null;
    }

    private static HostMutationOutcome RecordApplied(IEnrolledSuccessorIssueFence fence, SuccessorIssueHead head,
        SuccessorIssueRecord predecessor, string id, HostMutationOutcome unresolved)
    {
        var terminal = new HostMutationOutcome(predecessor.Binding.Command.Operation, HostMutationStatus.Applied, id);
        return Append(fence, head, predecessor with { Phase = SuccessorIssuePhase.Applied, Outcome = terminal }) is not null
            ? terminal : unresolved;
    }

    private sealed record StartAuthority(HostInitialLaunchIntent Intent, HostInitialCreate Observed, SupervisedStartIssue Issue);

    // Grants are local to one invocation while the protected successor writer fence is held.
    private sealed class CommandAuthority(SupervisedLaunchCommand command) : ISupervisedDockerCommandAuthority
    {
        internal SupervisedDockerStopIssue? Stop;
        internal HostInitialLaunchIntent? Create;
        internal StartAuthority? Start;

        public SupervisedDockerStopIssue? TakeStopUnderIssueFence(HostMutation operation) => operation == command.Operation
            ? Interlocked.Exchange(ref Stop, null) : null;

        public (SupervisedLaunchCommand Command, HostInitialLaunchIntent Intent)?
            TakeCreateUnderIssueFence(SupervisedLaunchCommand proposed) => proposed == command &&
            Interlocked.Exchange(ref Create, null) is { } intent ? (command, intent) : null;

        public (HostInitialLaunchIntent Intent, HostInitialCreate Observed, SupervisedStartIssue Anchored)?
            TakeStartUnderIssueFence(SupervisedLaunchCommand proposed) => proposed == command &&
            Interlocked.Exchange(ref Start, null) is { } start ? (start.Intent, start.Observed, start.Issue) : null;
    }
}
