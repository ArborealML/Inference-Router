using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

// Internal enrollment is supplied by a trusted composition root. Public configuration cannot
// create a controller, a verified issuer, or evidence that they own the physical domain.
internal sealed record HostManagerEnrollment(ExactTicketAdmission.DomainIdentity Domain, HostLifecycleCoordinator Controller,
    HostClosedRouteStaging Staging, IVerifiedDispatchIssuer Issuer, IReadOnlyDictionary<string, HostStagedRuntimeFixture> Members,
    Func<HostClosedRouteStage, VerifiedDispatchResident?> ObserveResident,
    Func<EngineDefinition, LifecycleTarget, VerifiedDispatchTarget?> ResolveRecipe);

internal sealed class InProcessHostManager
{
    private readonly Dictionary<string, HostManagerEnrollment> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<ExactTicketAdmission.DomainIdentity, Transition> _active = [];
    private readonly Dictionary<(string Engine, string Model, string Tier), VerifiedDispatchTarget> _recipes = [];
    private readonly Dictionary<(string Engine, string Model), IReadOnlyList<ServingProfile>> _supportedProfiles = [];
    private IReadOnlyDictionary<string, EngineRuntime>? _engines;

    internal InProcessHostManager(IEnumerable<HostManagerEnrollment> enrollments)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var controllers = new HashSet<HostLifecycleCoordinator>();
        var issuers = new HashSet<IVerifiedDispatchIssuer>(ReferenceEqualityComparer.Instance);
        foreach (var enrollment in enrollments)
        {
            if (string.IsNullOrWhiteSpace(enrollment.Domain.Name) || enrollment.Domain.Generation <= 0 ||
                !names.Add(enrollment.Domain.Name) || !controllers.Add(enrollment.Controller) || !issuers.Add(enrollment.Issuer) ||
                !enrollment.Controller.OwnsStaging(enrollment.Staging) || !enrollment.Staging.MatchesEnrollment(enrollment.Domain) ||
                enrollment.Members.Count == 0)
                throw InvalidEnrollment();
            foreach (var engine in enrollment.Members.Keys)
                if (!_members.TryAdd(engine, enrollment)) throw InvalidEnrollment();
        }
    }

    internal ExactTicketAdmission Admission { get; } = new(
        static (_, _) => throw new InvalidOperationException("Managed admission requires a verified resident."),
        static (_, _) => throw new InvalidOperationException("Managed admission requires the enrolled issuer."),
        static (claim, _) => ValueTask.FromResult<VerifiedPreparedSender?>(VerifiedPreparedSender.CreateFromResident(claim)));

    internal bool Owns(string engine) => _members.ContainsKey(engine);

    internal bool IsResident(string engine) => Admission.IsManagedResident(engine);

    internal VerifiedDispatchTarget? Recipe(string engine, string model, string tier) => _recipes.GetValueOrDefault((engine, model, tier));

    internal IReadOnlyList<ServingProfile> SupportedProfiles(string engine, string model) =>
        _supportedProfiles.GetValueOrDefault((engine, model)) ?? [];

    internal bool MatchesResident(string engine, string model, string tier) =>
        Recipe(engine, model, tier) is { } expected && Admission.ReadManagedResident(engine)?.Target == expected;

    internal string? Scope(string engine) => _members.GetValueOrDefault(engine)?.Domain.Name;

    internal IReadOnlyDictionary<string, ExactTicketAdmission.DomainIdentity> Validate(RouterConfig config)
    {
        if (_engines is not null || _members.Count == 0) throw InvalidEnrollment();
        foreach (var (engine, enrollment) in _members)
            if (!config.Engines.TryGetValue(engine, out var definition) || definition.Name != engine ||
                definition.LifecycleGroup is null || !definition.HasExplicitRouterLifecycle ||
                definition.Lifecycle != EngineLifecycleKind.Docker || definition.Docker?.Resources is not null ||
                !enrollment.Members.ContainsKey(engine)) throw InvalidEnrollment();
        foreach (var group in config.Engines.Values.Where(static engine => engine.LifecycleGroup is not null)
                     .GroupBy(static engine => engine.LifecycleGroup, StringComparer.Ordinal))
        {
            if (!_members.TryGetValue(group.First().Name, out var enrollment) ||
                group.Any(engine => !ReferenceEquals(_members.GetValueOrDefault(engine.Name), enrollment)))
                throw InvalidEnrollment();
        }
        foreach (var (name, enrollment) in _members)
        {
            var definition = config.Engines[name];
            foreach (var (model, entry) in definition.Catalog)
            {
                var supported = new List<ServingProfile>();
                foreach (var tier in entry.ServingProfiles)
                {
                    if (EngineRuntime.UniqueNamedProfile(entry.ServingProfiles, tier.Name) is null) throw InvalidEnrollment();
                    var target = new LifecycleTarget(name, model, tier, entry);
                    if (enrollment.ResolveRecipe(definition, target) is not { } recipe) continue;
                    if (recipe.Engine != name || recipe.Model != model || recipe.Tier != tier.Name ||
                        !HostMutation.IsPlanDigest(recipe.PlanDigest) || recipe.Endpoint != definition.Endpoint ||
                        !enrollment.Controller.MatchesRecipe(target, recipe.PlanDigest)) throw InvalidEnrollment();
                    _recipes.Add((name, model, tier.Name), recipe);
                    supported.Add(tier);
                }
                _supportedProfiles.Add((name, model), supported.AsReadOnly());
            }
        }
        return _members.ToDictionary(static entry => entry.Key, static entry => entry.Value.Domain, StringComparer.Ordinal);
    }

    internal HostEngineEnrollment? Bind(EngineDefinition definition)
    {
        if (!_members.TryGetValue(definition.Name, out var enrollment)) return null;
        return new(definition.Docker!.RouterId, definition.Name, enrollment.Controller, enrollment.Members[definition.Name],
            Manager: this, RouteOpened: (stage, proof) => Publish(definition, enrollment, stage, proof));
    }

    internal void Attach(IReadOnlyDictionary<string, EngineRuntime> engines) => _engines = engines;

    internal ExactTicketAdmission.DomainSnapshot? Snapshot(string engine) =>
        _members.TryGetValue(engine, out var enrollment) ? Admission.Snapshot(enrollment.Domain) : null;

    internal Transition? TryAccept(string engine, string model, string profile, RouterTicket? ticket, CancellationToken ct,
        VerifiedDispatchTarget? expected = null)
    {
        if (ct.IsCancellationRequested || _engines?.GetValueOrDefault(engine) is not { } runtime ||
            !_members.TryGetValue(engine, out var enrollment) || runtime.CanonicalModel(model) is not { } canonical ||
            runtime.GetProfile(canonical, profile) is not { } tier || Recipe(engine, canonical, tier.Name) is not { } recipe ||
            expected is not null && expected != recipe) return null;
        lock (Admission.Gate)
        {
            if (!Admission.TryBeginTransition(enrollment.Domain, ticket)) return null;
            var transition = new Transition(this, runtime, canonical, tier, enrollment.Domain, recipe, ct);
            _active.Add(enrollment.Domain, transition);
            return transition;
        }
    }

    internal Task<EngineRuntime.SwitchResult> ExecuteAsync(Transition transition)
    {
        lock (Admission.Gate)
        {
            if (transition.BelongsTo(this) && transition.Started) return transition.Completion.Task;
            if (!ReferenceEquals(_active.GetValueOrDefault(transition.Domain), transition))
                return Task.FromResult(EngineRuntime.SwitchResult.Failed(SwitchReason.NoLifecycleOwner));
            transition.Started = true;
        }
        _ = ExecuteAcceptedAsync(transition);
        return transition.Completion.Task;
    }

    internal bool TryClaimRuntime(Transition transition, EngineRuntime engine, string model, ServingProfile? profile)
    {
        lock (Admission.Gate)
        {
            if (!ReferenceEquals(_active.GetValueOrDefault(transition.Domain), transition) || !transition.Started ||
                transition.RuntimeClaimed || !transition.Matches(engine, model, profile)) return false;
            transition.RuntimeClaimed = true;
            return true;
        }
    }

    private async Task ExecuteAcceptedAsync(Transition transition)
    {
        EngineRuntime.SwitchResult result;
        Exception? failure = null;
        bool published;
        try
        {
            var observed = await transition.Runtime.EnsureLoadedAsync(transition.Model, transition.Profile, Timeout.InfiniteTimeSpan,
                queue: null, transition.Cancellation,
                exactProfile: true, hostTransition: transition).ConfigureAwait(false);
            result = observed.Result;
        }
        catch (Exception error)
        {
            result = EngineRuntime.SwitchResult.Failed(SwitchReason.LaunchFailed);
            failure = error;
        }
        finally
        {
            lock (Admission.Gate) _active.Remove(transition.Domain);
            published = Admission.EndTransition(transition.Domain);
        }
        if (result.IsReady && !published) result = EngineRuntime.SwitchResult.Failed(SwitchReason.LaunchFailed);
        if (failure is OperationCanceledException cancellation) transition.Completion.TrySetCanceled(cancellation.CancellationToken);
        else if (failure is not null) transition.Completion.TrySetException(failure);
        else transition.Completion.TrySetResult(result);
    }

    private void Publish(EngineDefinition definition, HostManagerEnrollment enrollment, HostClosedRouteStage stage,
        SwitchServingProof proof)
    {
        if (_engines?.GetValueOrDefault(definition.Name) is not { } runtime ||
            !ReferenceEquals(runtime.Capability.SwitchProof, proof) || !enrollment.Staging.CanMint(stage.Route) ||
            enrollment.ObserveResident(stage) is not { } resident || resident.Route != stage.Route ||
            !ReferenceEquals(resident.Issuer, enrollment.Issuer) || resident.Target.Model != stage.Launch.Plan.Model ||
            resident.Target.Tier != stage.Launch.Plan.Tier || resident.Endpoint != definition.Endpoint ||
            runtime.GetProfile(resident.Target.Model, resident.Target.Tier) is not { } tier ||
            resident.TierWindow != tier.MaxContext || resident.MeasuredWindow != runtime.Capability.Serving.ContextWindow ||
            resident.TotalSlots != runtime.Capability.Serving.TotalSlots) return;
        lock (Admission.Gate)
        {
            if (_active.GetValueOrDefault(enrollment.Domain)?.Recipe != resident.Target) return;
        }
        Admission.PublishVerified(resident);
    }

    private static RouterConfigException InvalidEnrollment() =>
        new("Managed lifecycle requires current enrollment for every physical domain and its engines.");

    internal sealed class Transition(InProcessHostManager manager, EngineRuntime runtime, string model, ServingProfile tier,
        ExactTicketAdmission.DomainIdentity domain, VerifiedDispatchTarget recipe, CancellationToken cancellation)
    {
        internal bool Started;
        internal bool RuntimeClaimed;
        internal TaskCompletionSource<EngineRuntime.SwitchResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal EngineRuntime Runtime => runtime;
        internal string Model => model;
        internal ServingProfile Profile => tier;
        internal ExactTicketAdmission.DomainIdentity Domain => domain;
        internal VerifiedDispatchTarget Recipe => recipe;
        internal CancellationToken Cancellation => cancellation;

        internal bool BelongsTo(InProcessHostManager owner) => ReferenceEquals(owner, manager);

        internal bool Matches(EngineRuntime engine, string target, ServingProfile? profile) =>
            ReferenceEquals(engine, runtime) && target == model && ReferenceEquals(profile, tier);
    }
}
