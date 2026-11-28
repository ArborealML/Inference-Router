using System.Text;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    [Fact]
    public void StrataEqualCapacityCacheRecipesProjectDifferentExactDigests()
    {
        var engine = LoadDeepSeekQwenEngine(ReadDeepSeekQwenConfig("strata.toml"), QwenEngineName);
        var model = engine.Catalog[QwenPairModel];
        var concurrent = Assert.Single(model.ServingProfiles, tier => tier.Name == "concurrent");
        var cache = Assert.Single(model.ServingProfiles, tier => tier.Name == "64k-4");
        Assert.Equal((65_536L, 4), (concurrent.MaxContext, concurrent.MaxSlots));
        Assert.Equal((concurrent.MaxContext, concurrent.MaxSlots), (cache.MaxContext, cache.MaxSlots));
        var resolver = new VerifiedDispatchTargetResolver(engine);
        var (first, firstPlan, firstCreate) = resolver.ResolveProjection(QwenPairModel, concurrent.Name, null, "fixture-config", "fixture-route");
        var (second, secondPlan, secondCreate) = resolver.ResolveProjection("flash-next", cache.Name, null, "fixture-config", "fixture-route");
        Assert.Contains("PRESET=defiant-qwen-concurrent", firstCreate.Env!);
        Assert.Contains("PRESET=defiant-qwen-64k-4", secondCreate.Env!);
        Assert.Contains(firstCreate.Env!, value => value.Contains("conversation-cache-slots=4", StringComparison.Ordinal));
        Assert.Contains(secondCreate.Env!, value => value.Contains("conversation-cache-slots=8", StringComparison.Ordinal));
        Assert.NotEqual(firstPlan.LaunchSettingsDigest, secondPlan.LaunchSettingsDigest);
        Assert.NotEqual(first.PlanDigest, second.PlanDigest);
        Assert.Equal(second, resolver.Resolve(QwenPairModel, cache.Name, second, "fixture-config", "fixture-route"));
        Assert.Equal(resolver.Resolve(QwenPairModel, model.Profile, null, "fixture-config", "fixture-route"),
            resolver.Resolve("flash-next", null, null, "fixture-config", "fixture-route"));
    }

    [Fact(Timeout = 20_000)]
    public async Task CombinedRecipeColdNumericDemandSkipsUnsupportedTiersAndKeepsSupportedOrder()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var discovery = new FakeEngineHandler { LoadedModels = [] };
        var ct = watchdog.Token;
        var domain = MakeManagedDomain("a", 19001, equalRecipes: true);
        var manager = new InProcessHostManager([domain.Enrollment]);
        await using var router = MakeManagedRouter(manager, discovery, domain);
        _ = Task.Run(router.DispatchLoop, ct);
        var ticket = await router.EnqueueAsync("numeric", RecipeRequest(profile: null), ct);
        await ticket.CompletionTask.WaitAsync(ct);
        Assert.Equal("small", domain.Resident?.Target.Tier);
        Assert.Equal(1, domain.Host.Creates);
        Assert.Equal(1, domain.Issuer.FirstWriteAttempts);
    }

    [Fact(Timeout = 20_000)]
    public async Task CombinedRecipeSwitchDoesNotTreatEqualGaugesAsExactIdentity()
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var discovery = new FakeEngineHandler { LoadedModels = ["shared"], NCtx = 8_192, TotalSlots = 1 };
        var ct = watchdog.Token;
        var domain = MakeManagedDomain("a", 19001, equalRecipes: true);
        var manager = new InProcessHostManager([domain.Enrollment]);
        await using var router = MakeManagedRouter(manager, discovery, domain);
        await EstablishResidentAsync(manager, domain, ct);
        var resident = Assert.IsType<VerifiedDispatchResident>(domain.Resident);
        var runtime = router.Engines[domain.Engine.Name];
        Assert.True(await runtime.ProbeHealthOnceAsync(ct));
        Assert.Null(runtime.ActiveProfile);
        Assert.Equal(8_192, runtime.ActiveContextWindow);
        Assert.Equal(1, runtime.ActiveSlots);

        var unauthorized = await Assert.ThrowsAsync<RouterRequestException>(async () =>
            await router.EnqueueAsync("refused", RecipeRequest("unsupported"), ct));
        Assert.Equal("invalid_profile", unauthorized.Code);
        Assert.Same(resident, manager.Admission.ReadManagedResident(domain.Engine.Name));
        Assert.Equal(1, domain.Host.Creates);
        Assert.Equal(1, domain.Host.Stops);
        Assert.Equal(0, domain.Issuer.CertifiedCheckouts);

        _ = Task.Run(router.DispatchLoop, ct);
        var named = await router.EnqueueAsync("same", RecipeRequest("small"), ct);
        var numeric = await router.EnqueueAsync("numeric", RecipeRequest(profile: null), ct);
        await Task.WhenAll(named.CompletionTask, numeric.CompletionTask).WaitAsync(ct);
        Assert.Equal(1, domain.Host.Creates);
        Assert.Same(resident, domain.Resident);
        Assert.Equal(2, domain.Issuer.FirstWriteAttempts);

        // The fixture settles the already verified launch so its single host can accept a new recipe.
        using (var reservation = HostReservation.Open(domain.ReservationPath))
            reservation.Settle(Assert.IsType<HostMutation>(reservation.State.Pending));
        var alternate = await router.EnqueueAsync("different", RecipeRequest("alternate"), ct);
        await alternate.CompletionTask.WaitAsync(ct);
        var replacement = Assert.IsType<VerifiedDispatchResident>(domain.Resident);
        Assert.Equal("alternate", replacement.Target.Tier);
        Assert.NotEqual(resident.Target.PlanDigest, replacement.Target.PlanDigest);
        Assert.Equal(resident.MeasuredWindow, replacement.MeasuredWindow);
        Assert.Equal(resident.TotalSlots, replacement.TotalSlots);
        Assert.Equal(2, domain.Host.Creates);
        Assert.Equal(2, domain.Host.Stops);
        Assert.Equal(3, domain.Issuer.FirstWriteAttempts);
        Assert.Equal(0, discovery.CompletionCalls);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("offer", false)]
    [InlineData("first-write", true)]
    public async Task CombinedRecipeWithdrawalNeverWritesToTheRetiredInstance(string cut, bool stream)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var discovery = new FakeEngineHandler { LoadedModels = ["shared"], NCtx = 8_192, TotalSlots = 1 };
        var ct = watchdog.Token;
        var domain = MakeManagedDomain("a", 19001, equalRecipes: true);
        var manager = new InProcessHostManager([domain.Enrollment]);
        await using var router = MakeManagedRouter(manager, discovery, domain);
        await EstablishResidentAsync(manager, domain, ct);
        var resident = Assert.IsType<VerifiedDispatchResident>(domain.Resident);
        using var withdrawal = domain.Enrollment.Staging.Begin(new HostStagedPublication(Guid.NewGuid(), _ => false, _ => { }));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        VerifiedInstanceInvocation? offered = null;
        var ticket = await router.EnqueueAsync("withdrawal", RecipeRequest("small", stream), ct);
        if (cut == "offer") manager.Admission.BeforeVerifiedOfferForTests = _ => Withdraw();
        if (cut == "first-write") domain.Issuer.BeforeFirstWrite = invocation => { offered = invocation; Withdraw(); };
        _ = Task.Run(router.DispatchLoop, ct);

        await reached.Task.WaitAsync(ct);
        await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
        Assert.Equal(cut == "first-write" ? 1 : 0, domain.Issuer.FirstWriteAttempts);
        if (offered is { } wrong) Assert.Empty(domain.Issuer.Bytes(wrong));
        Assert.Equal(0, domain.Issuer.Traffic.Permits);
        Assert.Equal(0, discovery.CompletionCalls);

        void Withdraw()
        {
            if (reached.Task.IsCompleted) return;
            withdrawal.Close();
            Assert.True(manager.Admission.CloseVerified(resident));
            var capability = router.Engines[domain.Engine.Name].Capability;
            Assert.Equal(8_192, capability.Serving.ContextWindow);
            Assert.Equal(1, capability.Serving.TotalSlots);
            reached.TrySetResult();
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task CombinedRecipeReplacesEqualGaugeInstanceWhileOriginalTicketIsHeld(bool atTake, bool sameRecipe)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var boundary = new AdmissionFixtureBarrier();
        using var discovery = new FakeEngineHandler { LoadedModels = ["shared"], NCtx = 8_192, TotalSlots = 1 };
        var ct = watchdog.Token;
        var domain = MakeManagedDomain("a", 19001, equalRecipes: true);
        var manager = new InProcessHostManager([domain.Enrollment]);
        await using var router = MakeManagedRouter(manager, discovery, domain);
        await EstablishResidentAsync(manager, domain, ct);
        var incumbent = Assert.IsType<VerifiedDispatchResident>(domain.Resident);
        var ticket = await router.EnqueueAsync("original", RecipeRequest("small", stream: atTake && !sameRecipe), ct);
        var cleanup = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWrite = new TaskCompletionSource<VerifiedInstanceInvocation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        manager.Admission.AfterPreparationCleanupForTests = (claim, error) =>
        {
            if (ReferenceEquals(ticket, claim.Ticket) && ReferenceEquals(incumbent, claim.Resident)) cleanup.TrySetResult(error);
        };
        domain.Issuer.BeforeFirstWrite = invocation => firstWrite.TrySetResult(invocation);
        if (atTake) router.BeforeAdmissionCommitForTests = HoldOnce;
        else router.BeforeAdmissionPreparationForTests = HoldOnce;
        _ = Task.Run(router.DispatchLoop, ct);

        VerifiedDispatchResident successor;
        try
        {
            await boundary.Reached.WaitAsync(ct);
            Assert.Equal(TicketState.Queued, ticket.State);
            Assert.Equal(atTake ? 1 : 0, manager.Snapshot(domain.Engine.Name)?.Preparations);
            using var withdrawal = domain.Enrollment.Staging.Begin(new HostStagedPublication(Guid.NewGuid(), _ => false, _ => { }));
            withdrawal.Close();
            Assert.True(manager.Admission.CloseVerified(incumbent));
            if (atTake) Assert.Null(await cleanup.Task.WaitAsync(ct));
            using (var reservation = HostReservation.Open(domain.ReservationPath))
                reservation.Settle(Assert.IsType<HostMutation>(reservation.State.Pending));
            var nextTier = sameRecipe ? "small" : "alternate";
            var transition = Assert.IsType<InProcessHostManager.Transition>(manager.TryAccept(domain.Engine.Name, "shared", nextTier, ticket: null, ct));
            Assert.True((await manager.ExecuteAsync(transition).WaitAsync(ct)).IsReady);

            successor = Assert.IsType<VerifiedDispatchResident>(domain.Resident);
            Assert.Equal(nextTier, successor.Target.Tier);
            Assert.Equal(sameRecipe, successor.Target.PlanDigest == incumbent.Target.PlanDigest);
            Assert.True(successor.Route.RouteIncarnation > incumbent.Route.RouteIncarnation);
            Assert.NotEqual(incumbent.Route.ContainerId, successor.Route.ContainerId);
            Assert.Equal(incumbent.MeasuredWindow, successor.MeasuredWindow);
            Assert.Equal(incumbent.TotalSlots, successor.TotalSlots);
            Assert.True(domain.Enrollment.Staging.CanMint(successor.Route));
            Assert.False(domain.Enrollment.Staging.CanMint(incumbent.Route));
            Assert.Equal(TicketState.Queued, ticket.State);
            Assert.Equal(0, domain.Issuer.CertifiedCheckouts);
        }
        finally { boundary.Release(); }

        if (sameRecipe)
        {
            await ticket.CompletionTask.WaitAsync(ct);
            var invocation = await firstWrite.Task.WaitAsync(ct);
            Assert.Equal(successor.Route, invocation.Route);
            Assert.Contains("\"model\":\"shared\"", Encoding.UTF8.GetString(domain.Issuer.Bytes(invocation)), StringComparison.Ordinal);
            Assert.Equal(1, domain.Issuer.CertifiedCheckouts);
            Assert.Equal(1, domain.Issuer.Traffic.Permits);
            Assert.Single(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.selected");
        }
        else
        {
            // The still-held successor journal refuses another recipe until its applied launch is settled.
            var refusal = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(ct));
            Assert.Equal("switch_failed", refusal.Code);
            Assert.Equal(0, domain.Issuer.CertifiedCheckouts);
            Assert.Equal(0, domain.Issuer.Traffic.Permits);
            Assert.DoesNotContain(router.Events.Replay(0), row => row.TicketId == ticket.Id && row.Kind == "request.selected");
        }
        Assert.Equal(2, domain.Host.Creates);
        Assert.Equal(0, discovery.CompletionCalls);

        void HoldOnce(RouterTicket candidate)
        {
            if (ReferenceEquals(candidate, ticket) && Interlocked.Exchange(ref held, 1) == 0)
                boundary.HoldAsync(ct).GetAwaiter().GetResult();
        }
    }

    private static InferenceRequest RecipeRequest(string? profile, bool stream = false) => new()
    {
        Model = "model-alias", Stream = stream,
        Options = new RouterRequestOptions { Profile = profile, RequiredContext = 8_192 },
        Body = stream ? """{"model":"model-alias","messages":[],"stream":true}"""u8.ToArray() :
            """{"model":"model-alias","messages":[]}"""u8.ToArray(),
    };

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
        return Assert.IsType<ExactTicketAdmission.Claim>(manager.Admission.Capture(request, new(domain.Engine.Name, "shared", "small",
            manager.Recipe(domain.Engine.Name, "shared", "small")),
            switchRequired: false, explicitContext: 0));
    }

    private static RouterCore MakeManagedRouter(InProcessHostManager manager, FakeEngineHandler discovery, params ManagedDomain[] domains) =>
        new(new RouterConfig
        {
            Engines = domains.ToDictionary(static domain => domain.Engine.Name, static domain => domain.Engine, StringComparer.Ordinal),
            HttpHandlerFactory = _ => discovery,
        }, null, null, TimeProvider.System, hostManager: manager);

    private ManagedDomain MakeManagedDomain(string name, int port, HostStagedRuntimeFixture? fixture = null, bool equalRecipes = false)
    {
        var engine = ManagedEngine(name, "shared", name, port, equalRecipes);
        var domain = new ExactTicketAdmission.DomainIdentity(name, 1);
        var host = new FakeHost(readinessTimeoutMs: 10_000)
        {
            FrozenPlan = equalRecipes ? target => new(target.EngineName, target.Model, target.EffectiveProfile, target.Definition.Name,
                10_000, "fixture-private-route", new string(target.EffectiveProfile == "small" ? 'A' : 'B', 64)) : null,
        };
        var staging = new HostClosedRouteStaging(host.FindLaunch, (launch, generation) =>
            new(domain.Name, domain.Generation, launch.Operation.Engine, launch.Operation.PlanDigest!, launch.InstanceId,
                launch.InstanceId + "-serving", launch.Operation.Epoch, generation), enrolledDomain: domain);
        var issuer = new CertifiedPairIssuer(staging);
        var reservation = Path.Combine(_directory.FullName, name + ".host.reservation");
        HostReservation.ProvisionNew(reservation);
        return new ManagedDomain(engine, host, issuer, staging, new HostLifecycleCoordinator(reservation, host, staging), domain, reservation,
            fixture ?? new HostStagedRuntimeFixture(static (stage, _) => Task.FromResult<HostStagedReadiness?>(ReadyForManagedDomain(stage))));
    }

    private static HostStagedReadiness ReadyForManagedDomain(HostClosedRouteStage stage) =>
        new(stage, stage.Launch.Plan.Model, new(stage.Launch.Plan.Model, 8_192, 1, stage.Launch.Plan.Tier));

    private sealed class ManagedDomain
    {
        internal ManagedDomain(EngineDefinition engine, FakeHost host, CertifiedPairIssuer issuer, HostClosedRouteStaging staging,
            HostLifecycleCoordinator controller, ExactTicketAdmission.DomainIdentity domain, string reservationPath,
            HostStagedRuntimeFixture fixture)
        {
            Engine = engine;
            Host = host;
            Issuer = issuer;
            ReservationPath = reservationPath;
            Enrollment = new(domain, controller, staging, issuer,
                new Dictionary<string, HostStagedRuntimeFixture>(StringComparer.Ordinal) { [engine.Name] = fixture },
                stage => Resident = new(new(engine.Name, stage.Launch.Plan.Model, stage.Launch.Plan.Tier!, stage.Route.EffectiveTarget, engine.Endpoint),
                    stage.Route, 8_192, 8_192, 1, engine.Endpoint, VerifiedDispatchCredential.Capture(engine, resolvedKey: null, epoch: 1), issuer),
                (definition, target) => target.EffectiveProfile == "unsupported" ? null :
                    new(definition.Name, target.Model, target.EffectiveProfile, host.GetLaunchPlanDigest(target), definition.Endpoint));
        }

        internal EngineDefinition Engine { get; }
        internal FakeHost Host { get; }
        internal CertifiedPairIssuer Issuer { get; }
        internal HostManagerEnrollment Enrollment { get; }
        internal string ReservationPath { get; }
        internal VerifiedDispatchResident? Resident { get; private set; }
    }
}
