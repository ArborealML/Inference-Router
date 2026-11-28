namespace Arboreal.InferenceRouter.Lifecycle;

// These values come from protected commissioning for this process, separately from the
// authenticated catalog and from the broker log. In particular, startup never adopts the
// writer epoch found in a reopened broker log.
internal sealed record CommissionedBrokerInitialPolicy(EnrolledPlanKey Target, Guid IssuerEpoch,
    ServingBackendProfile Profile, ServingBrokerProbePolicy Probes, TimeSpan TerminalTimeout);

internal sealed class CommissionedBrokerBootstrap : IDisposable
{
    private readonly CommissionedServingPins _pins;
    private readonly ReadinessDeadline _initialBudget;

    private CommissionedBrokerBootstrap(CommissionedServingPins pins, ReadinessDeadline initialBudget,
        ServingBrokerLedger ledger, CommissionedServingIssuer issuer, CommissionedServingStartupPort startup)
    {
        _pins = pins;
        _initialBudget = initialBudget;
        Ledger = ledger;
        Issuer = issuer;
        Startup = startup;
    }

    internal ServingBrokerLedger Ledger { get; }
    internal CommissionedServingIssuer Issuer { get; }
    internal CommissionedServingStartupPort Startup { get; }

    internal static CommissionedBrokerBootstrap? BindInitial(CheckedEnrolledConfiguration configured,
        EnrolledSuccessorScope scope, string checkpointLock, CheckedEnrolledTarget target, ServingBrokerBinding binding,
        ServingBrokerLedger ledger, CommissionedBrokerInitialPolicy policy, Lock issuerGate, Func<VerifiedInstanceRoute, bool> canMint,
        Func<VerifiedInstanceInvocation, bool> firstWriteAllowed, Func<ServingHostExclusion?> readPublishedExclusion,
        Func<ServingHostAcceptedSocket, CancellationToken, ValueTask<ServingHostTerminalProof?>> readTerminal,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = policy.Target;
        if (!OperatingSystem.IsLinux() || policy.IssuerEpoch == Guid.Empty || policy.TerminalTimeout <= TimeSpan.Zero ||
            !OperationHeadProtocol.IsScopeValid(scope.Reservation) || !Path.IsPathFullyQualified(checkpointLock) ||
            checkpointLock != Path.GetFullPath(checkpointLock) || target.Key != key ||
            configured.Configuration.HttpHandlerFactory is not null || configured.Catalog.Head != scope.Commission.Head ||
            !SupervisedLaunchCommission.SameDevices(configured.Catalog.Devices, scope.Commission.Devices) ||
            !ReferenceEquals(configured.Catalog.Resolve(key.Engine, key.Model, key.Profile), target) ||
            !scope.Approves(target) || !configured.Configuration.Engines.TryGetValue(key.Engine, out var engine) ||
            !configured.Catalog.OwnsConfiguredEngine(engine) || !engine.Catalog.TryGetValue(key.Model, out var model) ||
            model.ServingProfiles.SingleOrDefault(profile => profile.Name == key.Profile) is not { } tier ||
            !VerifiedDispatchEnvelope.SameEffectiveUri(policy.Probes.Models.Uri, EngineRuntime.Combine(target.Endpoint, engine.ModelsEndpoint)) ||
            !VerifiedDispatchEnvelope.SameEffectiveUri(policy.Probes.State.Uri, EngineRuntime.Combine(target.Endpoint, engine.Health?.Path ?? "/props")))
            return null;

        ReadinessDeadline? budget = null;
        CommissionedServingPins? pins = null;
        var transferred = false;
        try
        {
            budget = ReadinessDeadline.Start(TimeProvider.System, TimeSpan.FromMilliseconds(target.Plan.ReadinessTimeoutMs), ct);
            if (budget.OutcomeForSettlement != ReadinessStepOutcome.Completed) return null;
            if (binding.Scope != scope.Reservation || binding.CatalogHead != scope.Commission.Head ||
                binding.AppliedJournalDigest is not null ||
                binding.Applied.State is not { Phase: HostCheckpointPhase.Resident, Epoch: 1, AppliedLaunch: { } launch } ||
                launch.Plan != target.Plan || !binding.IsValid() || !target.Target.Matches(binding.Route) ||
                !ledger.OwnsClosedInitial(binding, policy.IssuerEpoch) ||
                HostEffectCheckpoint.ReadInstalledCommitment(checkpointLock) != binding.Applied ||
                readPublishedExclusion() is not { } exclusion || exclusion.Binding != ServingBrokerIssuer.ServingIdentity(binding) ||
                budget.OutcomeForSettlement != ReadinessStepOutcome.Completed)
                return null;

#pragma warning disable CA2000, IDISP001 // Bootstrap takes the pins on success. The finally disposes every refusal.
            pins = CommissionedServingPins.TryOpen(exclusion);
#pragma warning restore CA2000, IDISP001
            if (pins is null || pins.BindGeneration(exclusion, policy.Profile, policy.Probes) is not { } generation)
                return null;

            var issuer = new CommissionedServingIssuer(scope, ledger, issuerGate, canMint, firstWriteAllowed,
                policy.TerminalTimeout);
            if (issuer.Prepare(binding, target, generation, tier.MaxContext, readPublishedExclusion, readTerminal)
                    is not { } prepared || issuer.PrepareStartup(prepared, budget,
                    (kind, body) => CommissionedServingProbeValidation.Matches(engine, policy.Profile, kind, body)) is not { } startup)
                return null;

            var result = new CommissionedBrokerBootstrap(pins, budget, ledger, issuer, startup);
            budget = null;
            transferred = true;
            return result;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            try { if (!transferred) pins?.Dispose(); }
            finally { budget?.Dispose(); }
        }
    }

    public void Dispose()
    {
        _initialBudget.Dispose();
        _pins.Dispose();
    }
}
