using System.Security.Cryptography;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed record VerifiedDispatchTarget(string Engine, string Model, string Tier, string PlanDigest)
{
    internal bool Matches(VerifiedInstanceRoute route) =>
        string.Equals(Engine, route.Engine, StringComparison.Ordinal) &&
        HostMutation.IsPlanDigest(PlanDigest) &&
        string.Equals(PlanDigest, route.EffectiveTarget, StringComparison.Ordinal) &&
        route.EnrollmentGeneration > 0 && route.LaunchEpoch > 0 && route.RouteIncarnation > 0 &&
        !string.IsNullOrWhiteSpace(route.HostGroup) && !string.IsNullOrWhiteSpace(route.ContainerId) &&
        !string.IsNullOrWhiteSpace(route.ServingIncarnation);
}

/// <summary>Resolves offline launch intent using one captured parsed catalog and its pure Docker projection.</summary>
internal sealed class VerifiedDispatchTargetResolver
{
    private readonly string _engine;
    private readonly DockerTargetPlans _plans;
    private readonly DockerImageIdentity _image;
    private readonly int _readinessTimeoutMs;
    private readonly DockerLifecycleProvider _projection;
    private readonly Dictionary<string, EngineModelDefinition> _spellings = new(StringComparer.OrdinalIgnoreCase);

    internal VerifiedDispatchTargetResolver(EngineDefinition engine, DockerLifecycleProvider projection)
    {
        _engine = engine.Name;
        _projection = projection;
        if (engine.Docker is not { TargetPlans: { } plans, ImageIdentity: { } image, WaitTimeoutMs: > 0 } docker)
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);
        _plans = plans;
        _image = image;
        _readinessTimeoutMs = docker.WaitTimeoutMs;

        foreach (var (canonical, entry) in engine.Catalog)
        {
            var tiers = entry.ServingProfiles.ToArray();
            if (tiers.Select(static tier => tier.Name).Distinct(StringComparer.Ordinal).Count() != tiers.Length)
                throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);
            var model = new EngineModelDefinition
            {
                Name = canonical,
                Profile = entry.Profile,
                ServingProfiles = Array.AsReadOnly(tiers),
            };
            AddSpelling(canonical, model);
            foreach (var alias in entry.Aliases) AddSpelling(alias, model);
        }
    }

    private void AddSpelling(string spelling, EngineModelDefinition model)
    {
        if (string.IsNullOrWhiteSpace(spelling) ||
            (!_spellings.TryAdd(spelling, model) && !ReferenceEquals(_spellings[spelling], model)))
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);
    }

    // Use the resident tier only for ordinary reuse. A null tier with no resident selects the launch default.
    internal VerifiedDispatchTarget Resolve(
        string requestedModel, string? selectedTier, string? residentTier, string configuration, string backendRoute)
    {
        if (!_spellings.TryGetValue(requestedModel, out var model) ||
            string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(backendRoute))
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);

        var tierName = selectedTier ?? residentTier;
        var tier = tierName is null ? null : model.ServingProfiles
            .FirstOrDefault(profile => string.Equals(profile.Name, tierName, StringComparison.Ordinal));
        if (tierName is not null && tier is null)
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);

        var requested = new LifecycleTarget(_engine, requestedModel, tier, model);
        DockerTargetLaunch launch;
        DockerCreateRequest create;
        try
        {
            launch = _plans.Resolve(requested)
                ?? throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);
            create = _projection.BuildCreateRequest(requested, _image);
        }
        catch (RouterConfigException)
        {
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);
        }
        if (!string.Equals(launch.Model, model.Name, StringComparison.Ordinal) ||
            !MatchesProjection(create, launch))
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);

        var plan = new HostCheckpointPlan(_engine, launch.Model, launch.Profile, configuration, _readinessTimeoutMs,
            backendRoute, DigestSettings(create, _image));
        return new VerifiedDispatchTarget(_engine, launch.Model, launch.Profile, plan.Digest());
    }

    private bool MatchesProjection(DockerCreateRequest create, DockerTargetLaunch launch) =>
        string.Equals(create.Image, _image.Id, StringComparison.Ordinal) &&
        string.Equals(create.Labels.GetValueOrDefault(DockerLifecycleProvider.LabelEngine), _engine, StringComparison.Ordinal) &&
        string.Equals(create.Labels.GetValueOrDefault(DockerLifecycleProvider.LabelModel), launch.Model, StringComparison.Ordinal) &&
        string.Equals(create.Labels.GetValueOrDefault(DockerLifecycleProvider.LabelProfile), launch.Profile, StringComparison.Ordinal);

    private static string DigestSettings(DockerCreateRequest create, DockerImageIdentity image)
    {
        // Freeze the mutable projection as a single structured payload before computing its identity.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            JsonSerializer.Serialize(writer, create, DockerJsonContext.Default.DockerCreateRequest);
            writer.WriteStringValue(image.RepoDigest);
            writer.WriteStringValue(image.Platform);
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }
}
