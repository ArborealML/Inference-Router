using System.Collections.Immutable;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal readonly record struct EnrolledPlanKey(string Engine, string Model, string Profile);

internal sealed record EnrolledCatalogEngineGrant(string Engine, string Daemon, string SocketPath);

internal sealed record EnrolledCatalogPlan(EnrolledPlanKey Key, string Configuration, string BackendRoute,
    Uri Endpoint, string PlanDigest);

internal sealed record EnrolledCatalogClaim(EnrolledPairHead Head, ImmutableArray<EnrolledCatalogEngineGrant> Engines,
    ImmutableArray<EnrolledCatalogPlan> Plans, ImmutableArray<string> Devices);

internal sealed record EnrolledCatalogApproval(EnrolledCatalogPlan Plan, SupervisedLaunchApproval Launch);

// Already authenticated by the caller. This pure check does not establish current host authority.
internal sealed record EnrolledCatalogAuthority(EnrolledPairHead Head, ImmutableArray<EnrolledCatalogEngineGrant> Engines,
    ImmutableArray<EnrolledCatalogApproval> Approvals, ImmutableArray<string> Devices, ImmutableArray<string> SequentialRoutes);

internal sealed record CheckedEnrolledTarget(EnrolledPlanKey Key, VerifiedDispatchTarget Target,
    EnrolledCatalogEngineGrant EngineGrant, HostInitialLaunchIntent Intent, DockerImageIdentity Image, string ApiVersion)
{
    internal HostCheckpointPlan Plan => Intent.Plan;
    internal string PlanDigest => Target.PlanDigest;
    internal Uri Endpoint => Target.Endpoint;
}

internal sealed record CheckedEnrolledConfiguration(RouterConfig Configuration, CheckedEnrolledCatalog Catalog);

internal sealed class CheckedEnrolledCatalog(EnrolledPairHead head, ImmutableArray<string> devices, ImmutableArray<CheckedEnrolledTarget> targets,
    ImmutableDictionary<string, VerifiedDispatchTargetResolver> resolvers,
    ImmutableDictionary<string, EngineDefinition> configuredEngines)
{
    internal EnrolledPairHead Head => head;
    internal ImmutableArray<string> Devices => devices;
    internal ImmutableArray<CheckedEnrolledTarget> Targets => targets;

    internal bool OwnsConfiguredEngine(EngineDefinition engine) =>
        ReferenceEquals(configuredEngines.GetValueOrDefault(engine.Name), engine);

    internal VerifiedDispatchTargetResolver? ConfiguredResolver(string engine) => resolvers.GetValueOrDefault(engine);

    internal CheckedEnrolledTarget? Resolve(string engine, string model, string? profile = null, long requiredTokens = 0)
    {
        if (!resolvers.TryGetValue(engine, out var resolver)) return null;
        try
        {
            var (canonical, effectiveProfile) = resolver.SelectConfiguredTier(model, profile, requiredTokens);
            var key = new EnrolledPlanKey(engine, canonical, effectiveProfile);
            return targets.FirstOrDefault(target => target.Key == key);
        }
        catch (Exception error) when (error is VerifiedDispatchBindingException or RouterConfigException)
        {
            return null;
        }
    }
}

internal static class EnrolledCatalog
{
    internal static CheckedEnrolledCatalog Check(RouterConfig config, string commissionedSlot,
        EnrolledCatalogClaim claim, EnrolledCatalogAuthority current)
    {
        if (claim.Head is not { } head || head != current.Head || head.Slot != commissionedSlot ||
            string.IsNullOrWhiteSpace(commissionedSlot) || string.IsNullOrWhiteSpace(head.Partition) ||
            string.IsNullOrWhiteSpace(head.Daemon) || string.IsNullOrWhiteSpace(head.RouterId) || head.Generation <= 0 ||
            head.Revision < 0 || !HostMutation.IsPlanDigest(head.Digest))
            throw Refuse("current commissioned head mismatch");

        var devices = NameSet(current.Devices);
        if (devices.Count == 0 || !devices.SetEquals(NameSet(claim.Devices)))
            throw Refuse("device coverage mismatch");

        if (config.Engines.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Engines.Count)
            throw Refuse("configured engine names collide");
        var engines = config.Engines.Values.Where(engine => engine.HasExplicitRouterLifecycle)
            .ToDictionary(engine => engine.Name, StringComparer.Ordinal);
        var grants = Index(current.Engines, grant => grant.Engine);
        var offeredGrants = Index(claim.Engines, grant => grant.Engine);
        if (engines.Count == 0 || !grants.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(engines.Keys) ||
            !offeredGrants.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(engines.Keys))
            throw Refuse("managed engine coverage mismatch");

        var keys = new HashSet<EnrolledPlanKey>();
        var resolvers = ImmutableDictionary.CreateBuilder<string, VerifiedDispatchTargetResolver>(StringComparer.Ordinal);
        foreach (var (name, engine) in engines)
        {
            var grant = grants[name];
            if (engine.Lifecycle != EngineLifecycleKind.Docker || engine.Docker is not { } docker ||
                grant != offeredGrants[name] || grant.Daemon != head.Daemon || docker.RouterId != head.RouterId ||
                !DockerLifecycleProvider.TryParseUnixSocket(docker.Socket, out var path) ||
                path.Contains('\0', StringComparison.Ordinal) || !Path.IsPathFullyQualified(path) ||
                path != grant.SocketPath || Path.GetFullPath(path) != path)
                throw Refuse("managed engine grant mismatch");

            if (engine.Catalog.Count == 0) throw Refuse("managed engine has no configured targets");
            try { resolvers.Add(name, new VerifiedDispatchTargetResolver(engine)); }
            catch (VerifiedDispatchBindingException) { throw Refuse("managed engine cannot project its catalog"); }

            foreach (var (model, entry) in engine.Catalog)
            {
                if (entry.ServingProfiles.Count == 0) throw Refuse("managed model has no serving ladder");
                foreach (var profile in entry.ServingProfiles)
                    if (!keys.Add(new(name, model, profile.Name))) throw Refuse("duplicate configured target");
            }
        }

        var claims = Index(claim.Plans, plan => plan.Key);
        var approvals = Index(current.Approvals, approval =>
            approval.Plan?.Key ?? throw Refuse("approved target is missing"));
        if (!keys.SetEquals(claims.Keys) || !keys.SetEquals(approvals.Keys))
            throw Refuse("configured plan coverage mismatch");

        var routes = new Dictionary<string, Uri>(StringComparer.Ordinal);
        var endpoints = new Dictionary<string, string>(StringComparer.Ordinal);
        var sequential = NameSet(current.SequentialRoutes);
        var ordered = keys.OrderBy(key => key.Engine, StringComparer.Ordinal)
            .ThenBy(key => key.Model, StringComparer.Ordinal).ThenBy(key => key.Profile, StringComparer.Ordinal).ToArray();
        foreach (var key in ordered)
        {
            var offered = claims[key];
            var approved = approvals[key];
            var engine = engines[key.Engine];
            if (string.IsNullOrWhiteSpace(offered.Configuration) || string.IsNullOrWhiteSpace(offered.BackendRoute) ||
                offered.Configuration != approved.Plan.Configuration || offered.BackendRoute != approved.Plan.BackendRoute ||
                !HostMutation.IsPlanDigest(offered.PlanDigest) || offered.PlanDigest != approved.Plan.PlanDigest ||
                !VerifiedDispatchEnvelope.SameEffectiveUri(engine.Endpoint, offered.Endpoint) ||
                !VerifiedDispatchEnvelope.SameEffectiveUri(engine.Endpoint, approved.Plan.Endpoint) ||
                approved.Launch is not { Intent.Plan: { } plan } || !approved.Launch.HasPinnedApiVersion() ||
                approved.Launch.ApiVersion != engine.Docker!.ApiVersion || approved.Launch.Image != engine.Docker.ImageIdentity ||
                plan.Engine != key.Engine || plan.Model != key.Model || plan.Tier != key.Profile ||
                plan.Configuration != offered.Configuration || plan.BackendRoute != offered.BackendRoute ||
                plan.ReadinessTimeoutMs != engine.Docker.WaitTimeoutMs)
                throw Refuse("configured target binding mismatch");

            var route = offered.BackendRoute;
            if ((endpoints.TryGetValue(engine.Endpoint.AbsoluteUri, out var priorRoute) && priorRoute != route) ||
                (!routes.TryAdd(route, engine.Endpoint) &&
                 (!sequential.Contains(route) || !VerifiedDispatchEnvelope.SameEffectiveUri(routes[route], engine.Endpoint))))
                throw Refuse("shared route lacks exact sequential approval");
            endpoints[engine.Endpoint.AbsoluteUri] = route;
        }

        var targets = ImmutableArray.CreateBuilder<CheckedEnrolledTarget>(ordered.Length);
        foreach (var key in ordered)
        {
            var approved = approvals[key];
            try
            {
                var (target, plan, create) = resolvers[key.Engine].ResolveProjection(key.Model, key.Profile, resident: null,
                    approved.Plan.Configuration, approved.Plan.BackendRoute);
                var payload = JsonSerializer.SerializeToUtf8Bytes(create, DockerJsonContext.Default.DockerCreateRequest);
                var intent = HostInitialLaunchIntent.Freeze(plan, payload);
                if (target.Model != key.Model || target.Tier != key.Profile || target.PlanDigest != approved.Plan.PlanDigest ||
                    intent != approved.Launch.Intent) throw Refuse("approved launch payload mismatch");
                targets.Add(new(key, target, grants[key.Engine], intent, approved.Launch.Image, approved.Launch.ApiVersion!));
            }
            catch (VerifiedDispatchBindingException) { throw Refuse("managed target cannot project"); }
        }
        return new(head, [.. devices.Order(StringComparer.Ordinal)], targets.MoveToImmutable(), resolvers.ToImmutable(),
            engines.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private static HashSet<string> NameSet(ImmutableArray<string> entries)
    {
        if (entries.IsDefault) throw Refuse("missing authority set");
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (string.IsNullOrWhiteSpace(entry) || !set.Add(entry)) throw Refuse("invalid or duplicate authority name");
        return set;
    }

    private static Dictionary<TKey, T> Index<T, TKey>(ImmutableArray<T> entries, Func<T, TKey> keyOf)
        where T : class where TKey : notnull
    {
        if (entries.IsDefault) throw Refuse("missing catalog entries");
        var result = new Dictionary<TKey, T>();
        foreach (var entry in entries)
            if (entry is null || keyOf(entry) is not { } key || !result.TryAdd(key, entry))
                throw Refuse("invalid or duplicate catalog entry");
        return result;
    }

    private static RouterConfigException Refuse(string reason) => new($"enrolled catalog refused: {reason}");
}
