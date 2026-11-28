using System.Text;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CombinedDispatchTriesLaterColdDomainsWhenWarmPreparationCannotStart(bool saturated)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var discovery = new FakeEngineHandler { LoadedModels = [] };
        using var unavailable = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        var ct = watchdog.Token;
        var warm = MakeManagedDomain("a", 19001);
        var busy = MakeManagedDomain("b", 19002);
        var available = MakeManagedDomain("z", 19003);
        var manager = new InProcessHostManager([warm.Enrollment, busy.Enrollment, available.Enrollment]);
        await using var router = MakeManagedRouter(manager, discovery, warm, busy, available);
        await EstablishResidentAsync(manager, warm, ct);

        // This domain has accepted a separate operation and cannot take the target until it settles.
        var parked = Assert.IsType<InProcessHostManager.Transition>(manager.TryAccept(busy.Engine.Name, "shared", "small",
            ticket: null, unavailable.Token));
        var attempts = 0;
        var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Admission.BeforePrepareForTests = (claim, _) =>
        {
            if (claim.Target.Engine != warm.Engine.Name) return ValueTask.CompletedTask;
            if (Interlocked.Increment(ref attempts) == 2) filled.TrySetResult();
            if (!saturated) throw new InvalidOperationException("Spend this warm preparation generation.");
            return ValueTask.CompletedTask;
        };
        var held = saturated ? new[] { DetachedPreparation(manager, warm, "first"), DetachedPreparation(manager, warm, "second") } : [];
        foreach (var claim in held) manager.Admission.Prepare(claim);
        if (saturated) await filled.Task.WaitAsync(ct);
        var offered = new TaskCompletionSource<VerifiedInstanceInvocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        available.Issuer.BeforeFirstWrite = invocation => offered.TrySetResult(invocation);
        _ = Task.Run(router.DispatchLoop, ct);
        try
        {
            var ticket = await router.EnqueueAsync("caller", ManagedRequest("shared", "original alternate body"), ct);
            await ticket.CompletionTask.WaitAsync(ct);
            var invocation = await offered.Task.WaitAsync(ct);
            Assert.Equal(available.Engine.Name, ticket.Engine);
            Assert.Equal(1, available.Issuer.FirstWriteAttempts);
            Assert.Contains("original alternate body", Encoding.UTF8.GetString(available.Issuer.Bytes(invocation)), StringComparison.Ordinal);
            Assert.Equal(saturated ? 2 : 1, Volatile.Read(ref attempts));
            Assert.Equal(saturated ? 2 : 0, manager.Snapshot(warm.Engine.Name)?.Preparations);
            Assert.Equal(0, warm.Issuer.FirstWriteAttempts);
            Assert.Equal(0, busy.Host.Creates);
            Assert.Equal(0, discovery.CompletionCalls);
        }
        finally
        {
            foreach (var claim in held) manager.Admission.Invalidate(claim.Ticket);
            await unavailable.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ExecuteAsync(parked));
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task CombinedDispatchRecoversClosedResidentAndRejectsTheSpentTransitionHandle()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var discovery = new FakeEngineHandler { LoadedModels = [] };
        var ct = watchdog.Token;
        var domain = MakeManagedDomain("a", 19001);
        var manager = new InProcessHostManager([domain.Enrollment]);
        await using var router = MakeManagedRouter(manager, discovery, domain);
        var transition = await EstablishResidentAsync(manager, domain, ct);
        var engine = router.Engines[domain.Engine.Name];
        var old = Assert.IsType<VerifiedDispatchResident>(domain.Resident);
        var originalResult = manager.ExecuteAsync(transition);
        Assert.Same(originalResult, manager.ExecuteAsync(transition));
        var refused = await engine.EnsureLoadedAsync(transition.Model, transition.Profile, Timeout.InfiniteTimeSpan,
            queue: null, ct, exactProfile: true, hostTransition: transition);
        Assert.Equal(SwitchReason.NoLifecycleOwner, refused.Result.Detail);
        Assert.Equal(1, domain.Host.Creates);
        Assert.Equal(1, domain.Host.Stops);

        Assert.True(manager.Admission.CloseVerified(old));
        Assert.Contains("shared", engine.Capability.ModelRoster);
        Assert.False(manager.IsResident(engine.Name));
        var offered = new TaskCompletionSource<VerifiedInstanceInvocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        domain.Issuer.BeforeFirstWrite = invocation => offered.TrySetResult(invocation);
        _ = Task.Run(router.DispatchLoop, ct);
        var ticket = await router.EnqueueAsync("caller", ManagedRequest("shared", "recovered exact body", named: false), ct);
        await ticket.CompletionTask.WaitAsync(ct);
        var exact = await offered.Task.WaitAsync(ct);
        Assert.True(exact.Route.RouteIncarnation > old.Route.RouteIncarnation);
        Assert.Equal(old.Route.ContainerId, exact.Route.ContainerId);
        Assert.False(manager.Admission.CloseVerified(old));
        Assert.True(manager.IsResident(engine.Name));
        Assert.Equal(1, domain.Issuer.FirstWriteAttempts);
        Assert.Equal(1, domain.Host.Creates);
        Assert.Equal(1, domain.Host.Stops);
        Assert.Equal(0, discovery.CompletionCalls);
    }

    [Fact(Timeout = 20_000)]
    public async Task CombinedDispatchKeepsCancelledOwnerFencedThroughControllerCleanup()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var drainCleanup = new AdmissionFixtureBarrier();
        using var discovery = new FakeEngineHandler { LoadedModels = [] };
        var ct = watchdog.Token;
        var entered = new TaskCompletionSource<HostClosedRouteStage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateReadiness = new TaskCompletionSource<HostStagedReadiness?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detached = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new HostStagedRuntimeFixture((stage, _) => entered.TrySetResult(stage) ? lateReadiness.Task
            : Task.FromResult<HostStagedReadiness?>(ReadyForManagedDomain(stage)),
            CaptureReadinessCleanup: cleanup => detached.TrySetResult(cleanup));
        var domain = MakeManagedDomain("a", 19001, fixture);
        var cleanups = 0;
        domain.Host.OnDrainDispose = () =>
        {
            if (Interlocked.Increment(ref cleanups) == 1) drainCleanup.HoldAsync(ct).GetAwaiter().GetResult();
        };
        var manager = new InProcessHostManager([domain.Enrollment]);
        await using var router = MakeManagedRouter(manager, discovery, domain);
        router.Start();
        var cancelled = await router.EnqueueAsync("owner", ManagedRequest("shared", "cancelled activation"), ct);
        try
        {
            var cancelledStage = await entered.Task.WaitAsync(ct);
            Assert.True(router.TryCancel(cancelled.Id));
            var refusal = await Assert.ThrowsAsync<RouterRequestException>(() => cancelled.CompletionTask.WaitAsync(ct));
            Assert.Equal("cancelled", refusal.Code);
            await drainCleanup.Reached.WaitAsync(ct);
            var lateCleanup = await detached.Task.WaitAsync(ct);
            var next = await router.EnqueueAsync("successor", ManagedRequest("shared", "fresh activation"), ct);
            Assert.True(manager.Snapshot(domain.Engine.Name)?.Transitioning);
            Assert.Equal(TicketState.Queued, next.State);
            Assert.Equal(0, domain.Issuer.FirstWriteAttempts);
            Assert.False(domain.Enrollment.Staging.CanMint(cancelledStage.Route));

            drainCleanup.Release();
            await next.CompletionTask.WaitAsync(ct);
            var successor = Assert.IsType<VerifiedDispatchResident>(domain.Resident);
            Assert.True(successor.Route.RouteIncarnation > cancelledStage.Route.RouteIncarnation);
            Assert.False(lateCleanup.IsCompleted);
            lateReadiness.TrySetResult(ReadyForManagedDomain(cancelledStage));
            await lateCleanup.WaitAsync(ct);
            Assert.True(domain.Enrollment.Staging.CanMint(successor.Route));
            Assert.Same(successor, domain.Resident);
            Assert.Equal(1, domain.Issuer.FirstWriteAttempts);
            Assert.Equal(1, domain.Host.Creates);
            Assert.Equal(0, discovery.CompletionCalls);
            Assert.Single(router.Events.Replay(0), row => row.TicketId == cancelled.Id && row.Kind == "request.cancelled");
            Assert.DoesNotContain(router.Events.Replay(0), row => row.TicketId == cancelled.Id && row.Kind == "request.selected");
        }
        finally
        {
            drainCleanup.Release();
            lateReadiness.TrySetResult(null);
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CombinedDispatchCancelsAtProofOpenOrBeforeVerifiedOffer(bool atProofOpen)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var boundary = new AdmissionFixtureBarrier();
        using var discovery = new FakeEngineHandler { LoadedModels = [] };
        var ct = watchdog.Token;
        var domain = MakeManagedDomain("a", 19001);
        var manager = new InProcessHostManager([domain.Enrollment]);
        if (atProofOpen)
            domain.Enrollment.Staging.BeforeOpenForTests = (_, _) => boundary.HoldAsync(ct).GetAwaiter().GetResult();
        else
            manager.Admission.BeforeSendForTests = _ => boundary.HoldAsync(ct);

        await using var router = MakeManagedRouter(manager, discovery, domain);
        router.Start();
        var ticket = await router.EnqueueAsync("owner", ManagedRequest("shared", "cancelled before first write"), ct);
        try
        {
            await boundary.Reached.WaitAsync(ct);
            Assert.True(router.TryCancel(ticket.Id));
            var ownership = Assert.IsType<ExactTicketAdmission.DomainSnapshot>(manager.Snapshot(domain.Engine.Name));
            Assert.Equal(atProofOpen, ownership.Transitioning);
            Assert.Equal(atProofOpen ? 0 : 1, ownership.PhysicalUses);
        }
        finally { boundary.Release(); }

        var refusal = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
        Assert.Equal("cancelled", refusal.Code);
        Assert.Equal(0, domain.Issuer.FirstWriteAttempts);
        Assert.Equal(0, discovery.CompletionCalls);
        Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.cancelled");
    }

    private static async Task<InProcessHostManager.Transition> EstablishResidentAsync(InProcessHostManager manager, ManagedDomain domain,
        CancellationToken ct)
    {
        var accepted = Assert.IsType<InProcessHostManager.Transition>(manager.TryAccept(domain.Engine.Name, "shared", "small", ticket: null, ct));
        Assert.True((await manager.ExecuteAsync(accepted).WaitAsync(ct)).IsReady);
        return accepted;
    }

    private static ExactTicketAdmission.Claim DetachedPreparation(InProcessHostManager manager, ManagedDomain domain, string id)
    {
        var ticket = new RouterTicket(id, id, "shared", DateTimeOffset.UtcNow);
        var priority = ThreadPriority.Normal();
        var request = new QueuedRequest
        {
            Ticket = ticket, Request = ManagedRequest("shared", "occupied preparation"),
            PriorityNode = new(ticket, priority, 1), Thread = new(id, priority, 1, null), EnqueuedSeq = 1, PromptChars = 20,
        };
        return Assert.IsType<ExactTicketAdmission.Claim>(manager.Admission.Capture(request, new(domain.Engine.Name, "shared", "small"),
            switchRequired: false, explicitContext: 0));
    }

    private static RouterCore MakeManagedRouter(InProcessHostManager manager, FakeEngineHandler discovery, params ManagedDomain[] domains) =>
        new(new RouterConfig
        {
            Engines = domains.ToDictionary(static domain => domain.Engine.Name, static domain => domain.Engine, StringComparer.Ordinal),
            HttpHandlerFactory = _ => discovery,
        }, null, null, TimeProvider.System, hostManager: manager);

    private ManagedDomain MakeManagedDomain(string name, int port, HostStagedRuntimeFixture? fixture = null)
    {
        var engine = ManagedEngine(name, "shared", name, port);
        var domain = new ExactTicketAdmission.DomainIdentity(name, 1);
        var host = new FakeHost(readinessTimeoutMs: 10_000);
        var staging = new HostClosedRouteStaging(host.FindLaunch, (launch, generation) =>
            new(domain.Name, domain.Generation, launch.Operation.Engine, launch.Operation.PlanDigest!, launch.InstanceId,
                launch.InstanceId + "-serving", launch.Operation.Epoch, generation), enrolledDomain: domain);
        var issuer = new CertifiedPairIssuer(staging);
        var reservation = Path.Combine(_directory.FullName, name + ".host.reservation");
        HostReservation.ProvisionNew(reservation);
        return new ManagedDomain(engine, host, issuer, staging, new HostLifecycleCoordinator(reservation, host, staging), domain,
            fixture ?? new HostStagedRuntimeFixture(static (stage, _) => Task.FromResult<HostStagedReadiness?>(ReadyForManagedDomain(stage))));
    }

    private static HostStagedReadiness ReadyForManagedDomain(HostClosedRouteStage stage) =>
        new(stage, stage.Launch.Plan.Model, new(stage.Launch.Plan.Model, 8_192, 1, "small"));

    private sealed class ManagedDomain
    {
        internal ManagedDomain(EngineDefinition engine, FakeHost host, CertifiedPairIssuer issuer, HostClosedRouteStaging staging,
            HostLifecycleCoordinator controller, ExactTicketAdmission.DomainIdentity domain, HostStagedRuntimeFixture fixture)
        {
            Engine = engine;
            Host = host;
            Issuer = issuer;
            Enrollment = new(domain, controller, staging, issuer,
                new Dictionary<string, HostStagedRuntimeFixture>(StringComparer.Ordinal) { [engine.Name] = fixture },
                stage => Resident = new(new(engine.Name, stage.Launch.Plan.Model, "small", stage.Route.EffectiveTarget, engine.Endpoint),
                    stage.Route, 8_192, 8_192, 1, engine.Endpoint, VerifiedDispatchCredential.Capture(engine, resolvedKey: null, epoch: 1), issuer));
        }

        internal EngineDefinition Engine { get; }
        internal FakeHost Host { get; }
        internal CertifiedPairIssuer Issuer { get; }
        internal HostManagerEnrollment Enrollment { get; }
        internal VerifiedDispatchResident? Resident { get; private set; }
    }
}
