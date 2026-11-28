using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Server;

internal sealed class CommissionedDirectStage : IDisposable
{
    private readonly CommissionedServerAuthority _authority;
    private readonly Func<CancellationToken, ValueTask<SuccessorHeldPartition?>> _readPartition;
    private readonly Func<CancellationToken, Task<bool>> _readCatalog;
    private readonly Func<ServingHostAcceptedSocket, CancellationToken, ValueTask<ServingHostTerminalProof?>> _readTerminal;
    private readonly Func<ServingHostExclusion?> _readExclusion;
    private readonly Action<CommissionedGeneration> _publish;
    private readonly IReadOnlyDictionary<string, VerifiedDispatchCredential> _credentials;
    private CommissionedServingPins? _pins;
    private CommissionedGeneration? _preparing;

    internal CommissionedDirectStage(CommissionedServerAuthority authority,
        Func<CancellationToken, ValueTask<SuccessorHeldPartition?>> readPartition, Func<CancellationToken, Task<bool>> readCatalog,
        Func<ServingHostAcceptedSocket, CancellationToken, ValueTask<ServingHostTerminalProof?>> readTerminal,
        Func<ServingHostExclusion?> readExclusion, Action<CommissionedGeneration> publish)
    {
        _authority = authority;
        _readPartition = readPartition;
        _readCatalog = readCatalog;
        _readTerminal = readTerminal;
        _readExclusion = readExclusion;
        _publish = publish;
        _credentials = authority.Configured.Configuration.Engines.Values.Where(authority.Configured.Catalog.OwnsConfiguredEngine)
            .ToDictionary(engine => engine.Name,
                engine => VerifiedDispatchCredential.Capture(engine,
                    engine.ApiKey ?? (engine.ApiKeyFile is { } path ? File.ReadAllText(path).Trim() : null), 1), StringComparer.Ordinal);
        var scope = authority.Scope;
        Staging = new HostClosedRouteStaging(static _ => null, PrepareRoute,
            enrolledDomain: new(scope.Commission.Head.Partition, scope.Commission.Head.Generation));
    }

    internal HostClosedRouteStaging Staging { get; }
    internal ServingHostExclusion? PendingExclusion => Volatile.Read(ref _preparing)?.Evidence.Exclusion;

    internal ServingBrokerProbePolicy ProbePolicy(EngineDefinition engine, CheckedEnrolledTarget target, string digest) =>
        new(digest, _credentials[engine.Name],
            new(EngineRuntime.Combine(target.Endpoint, engine.ModelsEndpoint), ServingBackendConnection.MaxResponse),
            new(EngineRuntime.Combine(target.Endpoint, engine.Health?.Path ?? "/props"), ServingBackendConnection.MaxResponse));

    internal async ValueTask<HostStagedSwitchResult> RunAsync(CommissionedBrokerBootstrap broker,
        HostStagedDeadlinePublication publication, EnrolledSuccessorApplied applied, EnrolledSuccessorAppliedWitness witness,
        CancellationToken ct)
    {
        var authority = _authority;
        var target = applied.Installed.Offer.Successor;
        if (!ReferenceEquals(broker.Ledger, authority.Ledger) || !authority.Scope.Matches(applied.Scope) ||
            !authority.Scope.Approves(target) || witness.Checkpoint.State.AppliedLaunch != applied.Launch ||
            !publication.MatchesLaunch(applied.Launch.Plan) ||
            !authority.Configured.Configuration.Engines.TryGetValue(target.Key.Engine, out var definition) ||
            !definition.Catalog.TryGetValue(target.Key.Model, out var model) ||
            EngineRuntime.UniqueNamedProfile(model.ServingProfiles, target.Key.Profile) is not { } profile)
            return Refused();

        using var budget = ReadinessDeadline.Start(publication.Clock, TimeSpan.FromMilliseconds(target.Plan.ReadinessTimeoutMs), ct);
        var observed = await budget.ObserveAsync(WaitForGenerationAsync).ConfigureAwait(false);
        if (observed is not { Outcome: ReadinessStepOutcome.Completed, Observation: { Serving: { } serving } held } ||
            !held.IngressClosed || held.OwnerGeneration != applied.Retirement.OwnerGeneration ||
            held.IngressGeneration != applied.Retirement.IngressGeneration ||
            !SupervisedLaunchCommission.SameDevices(applied.Scope.Commission.Devices, held.CoveredDevices))
            return Refused();

        var identity = serving.Exclusion.Binding;
        var binding = new ServingBrokerBinding(authority.Scope.Reservation, authority.Scope.Commission.Head,
            witness.Checkpoint, identity.Route, identity.PolicyDigest, serving.Profile.Slots, witness.Journal.Digest);
        if (!binding.IsValid() || !serving.Matches(authority.Scope, target, ServingBrokerIssuer.ServingIdentity(binding)) ||
            binding.Route.RouteIncarnation <= applied.Retirement.SealedRoute.RouteIncarnation ||
            !CommissionedNativeProgram.TryBind(held, binding, target, serving, out var generation) ||
            HostEffectCheckpoint.ReadInstalledCommitment(authority.CheckpointLock) != witness.Checkpoint ||
            budget.OutcomeForSettlement != ReadinessStepOutcome.Completed) return Refused();

#pragma warning disable CA2000, IDISP001 // A staged success transfers pins to this adapter. Other paths dispose them in finally.
        var pins = CommissionedServingPins.TryOpen(serving.Exclusion);
#pragma warning restore CA2000, IDISP001
        if (pins is null) return Refused();
        var transferred = false;
        try
        {
            if (pins.BindGeneration(serving.Exclusion, serving.Profile,
                    ProbePolicy(definition, target, binding.PolicyDigest)) is not { } pinned ||
                budget.OutcomeForSettlement != ReadinessStepOutcome.Completed ||
                !authority.Ledger.InstallClosedSuccessor(binding, applied, witness)) return Refused();

            Volatile.Write(ref _preparing, generation);
            if (broker.Issuer.Prepare(binding, target, pinned, profile.MaxContext, _readExclusion, _readTerminal) is not { } prepared)
                return Refused();

            CommissionedServingIssuer.OpenDelegation? delegation = null;
            using var staged = Staging.Begin(publication with { ReadAsync = ReadBoundAsync, TryOpen = TryOpen });
            staged.Close();
            var result = await staged.VerifyEnrolledPublicationAsync(authority.Scope.Reservation.Slot, applied.Launch,
                VerifyAsync, budget).ConfigureAwait(false);
            if (!result.Succeeded) return new(result, staged.Decision);
            if (budget.OutcomeForSettlement != ReadinessStepOutcome.Completed || !staged.Open(ct))
                return Refused();

            var predecessorPins = _pins;
            _pins = pins;
            transferred = true;
            predecessorPins?.Dispose();
            publication.Opened();
            return new(result, staged.Decision, Opened: true);

            async Task<HostStagedReadiness?> ReadBoundAsync(HostClosedRouteStage stage, CancellationToken token)
            {
                if (!applied.MatchesStage(stage)) return null;
                if (await broker.Issuer.ReadStagedAsync(prepared, stage, budget,
                        (kind, bytes) => CommissionedServingProbeValidation.Matches(definition, serving.Profile, kind, bytes),
                        token).ConfigureAwait(false) is not { } pair ||
                    !await VerifyAsync(token).ConfigureAwait(false) ||
                    budget.OutcomeForSettlement != ReadinessStepOutcome.Completed ||
                    (delegation = broker.Issuer.DelegateOpen(prepared, pair)) is null)
                    return null;
                return new(stage, pair.Profile.CanonicalModel,
                    new(pair.Profile.CanonicalModel, pair.Profile.Context, pair.Profile.Slots, pair.Profile.Profile));
            }

            bool TryOpen(HostClosedRouteStage stage, SwitchServingProof proof, Func<bool> expose) =>
                delegation is { } allowed && applied.MatchesStage(stage) && broker.Issuer.CanCommitOpen(allowed, applied.Retirement.SealedRoute) &&
                publication.TryOpen(stage, proof, () =>
                {
                    if (!expose()) return false;
                    broker.Issuer.CommitOpen(allowed);
                    _publish(generation);
                    return true;
                });

            async Task<bool> VerifyAsync(CancellationToken token)
            {
                if (!await _readCatalog(token).ConfigureAwait(false) ||
                    HostEffectCheckpoint.ReadInstalledCommitment(authority.CheckpointLock) != witness.Checkpoint) return false;
                var current = await _readPartition(token).ConfigureAwait(false);
                return current is { IngressClosed: true } &&
                    CommissionedNativeProgram.TryBind(current, binding, target, serving, out var fresh) && fresh == generation &&
                    HostEffectCheckpoint.ReadInstalledCommitment(authority.CheckpointLock) == witness.Checkpoint;
            }
        }
        finally
        {
            Volatile.Write(ref _preparing, null);
            if (!transferred) pins.Dispose();
        }

        async Task<SuccessorHeldPartition?> WaitForGenerationAsync(CancellationToken token)
        {
            while (true)
            {
                var current = await _readPartition(token).ConfigureAwait(false);
                if (current is not null && (!current.IngressClosed || current.Resident != applied.Launch || current.Serving is not null))
                    return current;
                await Task.Delay(TimeSpan.FromMilliseconds(75), token).ConfigureAwait(false);
            }
        }
    }

    private VerifiedInstanceRoute PrepareRoute(HostCheckpointLaunchEffect launch, long generation) =>
        Volatile.Read(ref _preparing) is { } pending && pending.Binding.Applied.State.AppliedLaunch == launch &&
        pending.Binding.Route.RouteIncarnation == generation ? pending.Binding.Route : default;

    private static HostStagedSwitchResult Refused() => new(default(HostStagedDecision).Result, default);

    public void Dispose() => _pins?.Dispose();
}
