using System.Globalization;
using System.Net.Sockets;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Server;

internal sealed class CommissionedNativeProgram(CommissionedServerBootstrap bootstrap, Socket listener, CancellationToken ct)
    : IAsyncDisposable, ICommissionedServingAuthority, ICommissionedSwitchDriver
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
    private CommissionedServerAuthority? _authority;
    private CommissionedBrokerBootstrap? _broker;
    private CommissionedDirectStage? _stage;
    private Task? _terminalLoop;
    private EnrolledSuccessorRuntime? _runtime;
    private Func<CancellationToken, Task<bool>>? _checkCatalog;
    private CommissionedGeneration? _current;
    private long _epoch;
    private CommissionedHostCut? _activeCut;
    private EnrolledSuccessorQuery? _closeQuery;
    private (CommissionedServingRetirement Cutoff, EnrolledSuccessorRetirement Retirement)? _closed;

    private CommissionedServerAuthority Authority => _authority ?? throw Unavailable();
    private CommissionedBrokerBootstrap Broker => _broker ?? throw Unavailable();
    private HostClosedRouteStaging Staging => _stage?.Staging ?? throw Unavailable();
    internal CancellationToken StopToken => _stop.Token;

    public HostReservationScope Scope => Authority.Scope.Reservation;
    public string Daemon => Authority.Scope.Commission.Head.Daemon;
    public string Partition => Authority.Scope.Commission.Head.Partition;
    public long ResourceEpoch => Interlocked.Read(ref _epoch);
    public bool Approves(VerifiedDispatchTarget target) =>
        Authority.Configured.Catalog.Resolve(target.Engine, target.Model, target.Tier)?.Target == target;
    public bool IsDisjointFromLocalController(EngineDefinition engine) => false;
    public bool IsKnownVacant(long resourceEpoch) => false; // The transition worker establishes vacancy independently of retirement.

    internal async ValueTask<WebApplication> ConnectAsync(string[] args, CommissionedServerAuthority authority,
        Func<CancellationToken, Task<bool>> checkCatalog, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _authority, authority, null) is not null) throw Unavailable();
        _checkCatalog = checkCatalog;
        var scope = authority.Scope;
        var configured = authority.Configured;
        var initialTarget = authority.InitialTarget;
        var config = configured.Configuration;
        if (!config.Engines.TryGetValue(initialTarget.Key.Engine, out var engine) ||
            await ReadPartitionAsync(token).ConfigureAwait(false) is not { } held ||
            !TryBind(held, authority.Binding, initialTarget, authority.InitialGeneration, out var generation))
            throw Unavailable();
        Interlocked.Exchange(ref _epoch, generation.Stamp.ResourceEpoch);
        Volatile.Write(ref _current, generation);

        var terminal = new ServingNativeTerminalSource(authority.Ledger, ReadPublishedExclusion, bootstrap.HandlerUid);
        var stage = new CommissionedDirectStage(authority, ReadPartitionAsync, CheckCatalogAsync, terminal.ReadTerminalAsync,
            ReadPublishedExclusion, PublishGeneration);
#pragma warning disable IDISP003 // CompareExchange above makes bootstrap single-use. DisposeAsync owns the only stage.
        _stage = stage;
#pragma warning restore IDISP003
        var initial = new CommissionedBrokerInitialPolicy(initialTarget.Key, authority.IssuerEpoch, generation.Evidence.Profile,
            stage.ProbePolicy(engine, initialTarget, authority.Binding.PolicyDigest), TimeSpan.FromMilliseconds(bootstrap.TerminalTimeoutMs));
        var broker = CommissionedBrokerBootstrap.BindInitial(configured, scope, authority.CheckpointLock,
            initialTarget, authority.Binding, authority.Ledger, initial, stage.Staging.IssuerGate, stage.Staging.CanMint,
            invocation => ReadServing(invocation.Route) is not null, ReadPublishedExclusion, terminal.ReadTerminalAsync, token)
            ?? throw Unavailable();
#pragma warning disable IDISP003 // The same one-shot bootstrap owns the broker through DisposeAsync.
        _broker = broker;
#pragma warning restore IDISP003

        _terminalLoop = RunTerminalAsync(terminal);
        var worker = CommissionedSuccessorWorker.Connect(authority.CheckpointLock, scope, config, configured.Catalog,
            bootstrap.OperationObserver, bootstrap.OperationHead, RetireThroughCutAsync);
        var drain = TimeSpan.FromMilliseconds(bootstrap.DrainTimeoutMs);
        var runtime = worker.BindRuntime(scope,
            (publication, applied, witness, cancellation) => stage.RunAsync(broker, publication, applied, witness, cancellation));
        _runtime = runtime;
        var identity = new ExactTicketAdmission.DomainIdentity(Partition, scope.Reservation.Enrollment!.Generation);
        var controller = new HostLifecycleCoordinator(scope.Reservation.Slot, provider: null, stage.Staging, commissioned: this);
        var members = config.Engines.Values.Where(configured.Catalog.OwnsConfiguredEngine).ToDictionary(def => def.Name,
            static _ => (HostStagedRuntimeFixture?)null, StringComparer.Ordinal);
        var enrollment = new HostManagerEnrollment(identity, controller, stage.Staging, this, members,
            binding => broker.Issuer.ReadResident(binding.Route) is { } resident ? resident with { Issuer = this } : null,
            (_, target) => Resolve(target)?.Target);
        var manager = new InProcessHostManager([enrollment], drain);
        var app = ServerApp.Build(args, null, config, TimeProvider.System, hostManager: manager, startDispatcher: false);
        try
        {
            var router = app.Services.GetRequiredService<RouterCore>();
            var resident = broker.Startup.Resident with { Issuer = this };
            var attached = await runtime.Controller.VerifyInitialResidentAsync(resident, ReadStartupAsync,
                observed => manager.TryAttachInitial(resident, observed,
                    () => broker.Startup.CanPublishInitial(resident.Route), token),
                worker.MatchesInitialAnchor, token).ConfigureAwait(false);
            if (!attached) throw Unavailable();
            router.Start();
            return app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        async Task<EnrolledInitialReadiness?> ReadStartupAsync(VerifiedInstanceRoute route, CancellationToken cancellation)
        {
            var observed = await broker.Startup.ObserveReadinessAsync(route, cancellation).ConfigureAwait(false);
            return observed is not null && await CheckServingAsync(generation, cancellation).ConfigureAwait(false) ? observed : null;
        }
    }

    internal static bool TryBind(SuccessorHeldPartition held, ServingBrokerBinding binding, CheckedEnrolledTarget target,
        HeldServingGeneration serving, out CommissionedGeneration generation)
    {
        generation = null!;
        if (held.Scope != binding.Scope || held.Catalog != binding.CatalogHead || held.OwnerGeneration <= 0 ||
            held.CoverageGeneration <= 0 || held.IngressGeneration <= 0 || !held.ResidentRunning ||
            held.Resident != binding.Applied.State.AppliedLaunch || held.ResidentRoute != binding.Route ||
            held.ResidentImage != target.Image || held.ResidentBackendRoute != target.Plan.BackendRoute ||
            !VerifiedDispatchEnvelope.SameEffectiveUri(target.Endpoint, held.ResidentEndpoint) || held.Serving != serving ||
            serving.Exclusion.Binding != ServingBrokerIssuer.ServingIdentity(binding) || serving.Profile.Slots != binding.TotalSlots)
            return false;

        var stamp = new CommissionedServingStamp(binding.Scope, binding.Route, held.OwnerGeneration, held.IngressGeneration,
            Lifetime(serving.Exclusion.Handler), Lifetime(serving.Exclusion.Worker));
        generation = new(binding, target, serving, held.CoverageGeneration, stamp);
        return true;

        static string Lifetime(ServingHostLifetime process) =>
            string.Create(CultureInfo.InvariantCulture, $"{process.ProcessId}/{process.StartTicks}");
    }

    private void PublishGeneration(CommissionedGeneration generation) => Volatile.Write(ref _current, generation);

    private ServingHostExclusion? ReadPublishedExclusion() =>
        _stage?.PendingExclusion ?? Volatile.Read(ref _current)?.Evidence.Exclusion;

    public CommissionedServingStamp? ReadServing(VerifiedInstanceRoute route) =>
        Volatile.Read(ref _current) is { } current && current.Stamp.Route == route ? current.Stamp : null;

    private CheckedEnrolledTarget? Resolve(LifecycleTarget target) =>
        Authority.Configured.Catalog.Resolve(target.EngineName, target.Model, target.EffectiveProfile);

    public string? ReadPlanDigest(LifecycleTarget target) => Resolve(target)?.PlanDigest;

    public async ValueTask<HostStagedSwitchResult> SwitchAsync(LifecycleTarget target, HostStagedDeadlinePublication publication,
        CancellationToken token)
    {
        var runtime = _runtime ?? throw Unavailable();
        var cut = publication.CommissionedCut;
        if (cut is null || Volatile.Read(ref _current) is not { } current ||
            Resolve(target) is not { } exact || exact.PlanDigest != publication.ExpectedPlanDigest ||
            !await CheckServingAsync(current, token).ConfigureAwait(false) ||
            await runtime.Controller.ObserveOfferAsync(target.EngineName, target.Model, exact.Key.Profile, token)
                .ConfigureAwait(false) is not { } offer || offer.IncumbentRoute != current.Stamp.Route ||
            Interlocked.CompareExchange(ref _activeCut, cut, null) is not null)
            return new(default(HostStagedDecision).Result, default);
        try
        {
            var installed = new InstalledEnrolledSuccessor(offer, publication.ExpectedAttempt, publication.ExpectedTarget,
                target.Profile?.MaxContext ?? 0);
            return await runtime.Controller.ActivateAsync(installed, (applied, currentToken) =>
                runtime.StageAsync(publication, applied, currentToken), token).ConfigureAwait(false);
        }
        finally
        {
            lock (Staging.IssuerGate)
            {
                Volatile.Write(ref _closeQuery, null);
                _closed = null;
                _ = Interlocked.CompareExchange(ref _activeCut, null, cut);
            }
        }
    }

    private async ValueTask<EnrolledSuccessorRetirement?> RetireThroughCutAsync(EnrolledSuccessorQuery query, CancellationToken token)
    {
        var cut = Volatile.Read(ref _activeCut);
        if (cut is null || !Authority.Scope.Matches(query.Scope) ||
            Volatile.Read(ref _current) is not { } current || current.Stamp.Route != query.ExpectedIncumbentRoute)
            return null;
        Volatile.Write(ref _closeQuery, query);
        using var budget = cut.StartBudget(token);
        if (!await cut.SealAndDrainAsync(budget).ConfigureAwait(false) ||
            budget.OutcomeForSettlement != ReadinessStepOutcome.Completed || _closed is not { } closure)
            return null;
        var fresh = await budget.ObserveAsync(async cancellation =>
            await CheckCatalogAsync(cancellation).ConfigureAwait(false) &&
            await ReadPartitionAsync(cancellation).ConfigureAwait(false) is { IngressClosed: true } held &&
            MatchesResident(held, current, closure.Retirement.IngressGeneration) &&
            HostEffectCheckpoint.ReadInstalledCommitment(Authority.CheckpointLock) == current.Binding.Applied &&
            Authority.Ledger.ReadRetirement(closure.Retirement.CloseId) == closure.Retirement).ConfigureAwait(false);
        if (fresh is not { Outcome: ReadinessStepOutcome.Completed, Observation: true } ||
            budget.OutcomeForSettlement != ReadinessStepOutcome.Completed)
        {
            cut.MarkUnproven();
            return null;
        }
        using var permit = cut.TryReserveStop(new HostResident(current.Stamp.Route.Engine, current.Stamp.Route.ContainerId));
        return permit is not null ? closure.Retirement : null;
    }

    public async ValueTask<CommissionedServingRetirement?> SealAndDrainAsync(CommissionedServingStamp stamp, CancellationToken token)
    {
        if (Volatile.Read(ref _closeQuery) is not { } query || Volatile.Read(ref _current) is not { } current || current.Stamp != stamp)
            return null;
        lock (Staging.IssuerGate)
            if (!ReferenceEquals(_current, current) || !ReferenceEquals(_closeQuery, query) || !Broker.Issuer.Seal(stamp.Route))
                return null;
        var installed = HostEffectCheckpoint.ReadInstalledCommitment(Authority.CheckpointLock);
        if (installed.State != query.Checkpoint || stamp.IngressGeneration == long.MaxValue ||
            !await CheckServingAsync(current, token).ConfigureAwait(false) ||
            HostEffectCheckpoint.ReadInstalledCommitment(Authority.CheckpointLock) != installed)
            return null;
        var nextIngress = stamp.IngressGeneration + 1;
        if (Authority.Ledger.Seal(query, installed, stamp.ResourceEpoch, nextIngress) is not { } close)
            return null;

        // The ledger is now closed. Readback failure, timeout or cancellation never reopens it.
        while (!token.IsCancellationRequested)
        {
            if (await ReadPartitionAsync(token).ConfigureAwait(false) is not { IngressClosed: true } held ||
                !MatchesResident(held, current, nextIngress) ||
                HostEffectCheckpoint.ReadInstalledCommitment(Authority.CheckpointLock) != installed)
                return null;

            if (Authority.Ledger.CompleteClose(close, stamp.Route) is { } retirement)
            {
                if (!await CheckCatalogAsync(token).ConfigureAwait(false)) return null;
                var cutoff = new CommissionedServingRetirement(stamp, retirement.CloseId);
                lock (Staging.IssuerGate)
                {
                    if (!ReferenceEquals(_current, current) || !ReferenceEquals(_closeQuery, query) || token.IsCancellationRequested)
                        return null;
                    _closed = (cutoff, retirement);
                }
                return cutoff;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(75), token).ConfigureAwait(false);
        }
        return null;
    }

    public ICommissionedStopPermit? TryReserveStop(CommissionedServingRetirement retirement) =>
        _closed is { } closure && closure.Cutoff == retirement && ReadServing(retirement.Stamp.Route) == retirement.Stamp
            ? new WorkerStopReservation() : null;

    // This path only authorizes the existing enrolled worker to consume its close identifier.
    // The worker independently reads the frontier and owns the durable dispatch and effects.
    private sealed class WorkerStopReservation : ICommissionedStopPermit
    {
        public ValueTask<HostMutationOutcome> StopAsync(HostMutation operation, CancellationToken token) =>
            ValueTask.FromResult(new HostMutationOutcome(operation, HostMutationStatus.RejectedBeforeIssue));
        public void Dispose() { }
    }

    public ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken token) =>
        ValueTask.FromException<VerifiedInstanceGrant?>(new InvalidOperationException("Certified checkout is required."));

    public async ValueTask<VerifiedCheckout> CheckoutVerifiedAsync(VerifiedInstanceInvocation invocation, CancellationToken token)
    {
        if (Volatile.Read(ref _current) is not { } current || current.Stamp.Route != invocation.Route ||
            !await CheckServingAsync(current, token).ConfigureAwait(false))
            return new(VerifiedCheckoutDisposition.NoTransfer);
        return await Broker.Issuer.CheckoutVerifiedAsync(invocation, token).ConfigureAwait(false);
    }

    public ValueTask<bool> CheckBeforeFirstWriteAsync(VerifiedInstanceGrant grant, CancellationToken token) =>
        Volatile.Read(ref _current) is { } current && current.Stamp.Route == grant.Invocation.Route
            ? new(CheckServingAsync(current, token)) : ValueTask.FromResult(false);

    public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant) =>
        ReadServing(grant.Invocation.Route) is not null ? Broker.Issuer.TryAuthorizeFirstWrite(grant) : null;

    private async Task<bool> CheckServingAsync(CommissionedGeneration current, CancellationToken token)
    {
        try
        {
            if (await CheckCatalogAsync(token).ConfigureAwait(false) &&
                await ReadPartitionAsync(token).ConfigureAwait(false) is { IngressClosed: false } held &&
                MatchesResident(held, current, current.Stamp.IngressGeneration) &&
                HostEffectCheckpoint.ReadInstalledCommitment(Authority.CheckpointLock) == current.Binding.Applied)
                return ReferenceEquals(Volatile.Read(ref _current), current);
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            // A missing observation cannot authorize a new physical checkout.
        }

        token.ThrowIfCancellationRequested();
        lock (Staging.IssuerGate)
            if (ReferenceEquals(_current, current) &&
                (_closeQuery?.ExpectedIncumbentRoute != current.Stamp.Route || Staging.CanMint(current.Stamp.Route)))
                Volatile.Write(ref _current, null);
        return false;
    }

    private bool MatchesResident(SuccessorHeldPartition held, CommissionedGeneration current, long ingress)
    {
        var stamp = current.Stamp;
        return held.OwnerGeneration == stamp.ResourceEpoch && held.CoverageGeneration == current.CoverageGeneration &&
            held.IngressGeneration == ingress && held.ResidentRunning && held.ResidentRoute == stamp.Route &&
            held.Resident == current.Binding.Applied.State.AppliedLaunch && held.Serving == current.Evidence &&
            SupervisedLaunchCommission.SameDevices(Authority.Scope.Commission.Devices, held.CoveredDevices);
    }

    private ValueTask<SuccessorHeldPartition?> ReadPartitionAsync(CancellationToken token) =>
        SuccessorPhysicalWitnessClient.ReadHeldPartitionAsync(Authority.Scope, bootstrap.HeldPartition, token);

    private Task<bool> CheckCatalogAsync(CancellationToken token) => _checkCatalog?.Invoke(token) ?? Task.FromResult(false);

    private async Task RunTerminalAsync(ServingNativeTerminalSource terminal)
    {
        try { await terminal.RunAsync(listener, StopToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { await _stop.CancelAsync().ConfigureAwait(false); }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            if (_terminalLoop is { } loop) await loop.ConfigureAwait(false);
        }
        finally
        {
            _stage?.Dispose();
            _broker?.Dispose();
            _stop.Dispose();
        }
    }

    private static InvalidOperationException Unavailable() =>
        new("The commissioned serving port could not corroborate its exact resident.");
}

internal sealed record CommissionedGeneration(ServingBrokerBinding Binding, CheckedEnrolledTarget Target,
    HeldServingGeneration Evidence, long CoverageGeneration, CommissionedServingStamp Stamp);
