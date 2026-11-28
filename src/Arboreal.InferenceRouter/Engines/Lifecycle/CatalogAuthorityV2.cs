using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record CatalogAuthorityScope(HostReservationScope Reservation, string Slot)
{
    internal bool IsValid() => HeadReadScope.IsName(Slot) &&
        Reservation is { Identity: var identity, Enrollment.IsValid: true } &&
        identity != Guid.Empty && !string.IsNullOrWhiteSpace(Reservation.Slot);
}

internal sealed record CatalogAuthoritySnapshot(CatalogAuthorityScope Scope, EnrolledCatalogAuthority Catalog)
{
    internal bool IsEvidenceFor(CatalogAuthorityScope scope)
    {
        if (Scope != scope || !scope.IsValid() || scope.Reservation.Enrollment is not { } enrollment ||
            Catalog is not { Head: { } head } || head.Slot != scope.Slot || head.Partition != enrollment.Group ||
            head.Generation != enrollment.Generation || head.Revision < 0 ||
            !HeadReadScope.IsName(head.Daemon) || !HeadReadScope.IsName(head.RouterId) ||
            !HostMutation.IsPlanDigest(head.Digest) ||
            Catalog.Engines.IsDefaultOrEmpty || Catalog.Engines.Length > CatalogAuthorityV2.MaxEngines ||
            Catalog.Approvals.IsDefaultOrEmpty || Catalog.Approvals.Length > CatalogAuthorityV2.MaxApprovals ||
            Catalog.Devices.IsDefaultOrEmpty || Catalog.Devices.Length > CatalogAuthorityV2.MaxNames ||
            Catalog.SequentialRoutes.IsDefault || Catalog.SequentialRoutes.Length > CatalogAuthorityV2.MaxNames ||
            !UniqueNames(Catalog.Devices) || !UniqueNames(Catalog.SequentialRoutes))
            return false;

        var engines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in Catalog.Engines)
        {
            if (grant is null || !HeadReadScope.IsName(grant.Engine) || grant.Daemon != head.Daemon ||
                string.IsNullOrWhiteSpace(grant.SocketPath) ||
                !Path.IsPathFullyQualified(grant.SocketPath) || grant.SocketPath.Contains('\0', StringComparison.Ordinal) ||
                Path.GetFullPath(grant.SocketPath) != grant.SocketPath || !engines.Add(grant.Engine))
                return false;
        }

        var approvedEngines = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<EnrolledPlanKey>();
        foreach (var approval in Catalog.Approvals)
        {
            if (approval is not { Plan: { } binding, Launch: { Intent: { Plan: { } plan }, Image: not null } launch } ||
                !engines.Contains(binding.Key.Engine) || !HeadReadScope.IsName(binding.Key.Model) ||
                !HeadReadScope.IsName(binding.Key.Profile) || !keys.Add(binding.Key) ||
                binding.Key != new EnrolledPlanKey(plan.Engine, plan.Model, plan.Tier ?? string.Empty) ||
                !VerifiedDispatchEnvelope.IsHttpEndpoint(binding.Endpoint) ||
                binding.Configuration != plan.Configuration || binding.BackendRoute != plan.BackendRoute ||
                binding.PlanDigest != plan.Digest() || !launch.HasPinnedApiVersion() || !launch.Intent.IsValid())
                return false;
            approvedEngines.Add(binding.Key.Engine);
        }

        return approvedEngines.SetEquals(engines);
    }

    // Callers first establish valid, unique keys in both authenticated snapshots.
    internal bool Matches(CatalogAuthoritySnapshot other) =>
        Scope == other.Scope && Catalog.Head == other.Catalog.Head &&
        Catalog.Engines.OrderBy(grant => grant.Engine, StringComparer.Ordinal)
            .SequenceEqual(other.Catalog.Engines.OrderBy(grant => grant.Engine, StringComparer.Ordinal)) &&
        OrderedApprovals(Catalog).SequenceEqual(OrderedApprovals(other.Catalog)) &&
        SupervisedLaunchCommission.SameDevices(Catalog.Devices, other.Catalog.Devices) &&
        SupervisedLaunchCommission.SameDevices(Catalog.SequentialRoutes, other.Catalog.SequentialRoutes);

    internal bool Extends(CatalogAuthoritySnapshot previous) =>
        Scope == previous.Scope && Catalog.Head.Daemon == previous.Catalog.Head.Daemon &&
        Catalog.Head.RouterId == previous.Catalog.Head.RouterId && Catalog.Head.Revision > previous.Catalog.Head.Revision;

    private static bool UniqueNames(ImmutableArray<string> names) =>
        names.All(HeadReadScope.IsName) && names.Distinct(StringComparer.Ordinal).Count() == names.Length;

    private static IEnumerable<(EnrolledPlanKey Key, string Configuration, string Route, string Endpoint, string Digest,
        SupervisedLaunchApproval Approval)> OrderedApprovals(EnrolledCatalogAuthority value) =>
        value.Approvals.OrderBy(approval => approval.Plan.Key.Engine, StringComparer.Ordinal)
            .ThenBy(approval => approval.Plan.Key.Model, StringComparer.Ordinal)
            .ThenBy(approval => approval.Plan.Key.Profile, StringComparer.Ordinal)
            .Select(approval => (approval.Plan.Key, approval.Plan.Configuration, approval.Plan.BackendRoute,
                approval.Plan.Endpoint.AbsoluteUri, approval.Plan.PlanDigest, approval.Launch));
}

internal sealed record CatalogAuthorityRequest(string Operation, int Version, CatalogAuthorityScope Scope, string Nonce)
{
    internal bool IsFor(string operation, int version, CatalogAuthorityScope scope) =>
        Operation == operation && Version == version && Scope == scope &&
        HostMutation.IsPlanDigest(Nonce);
}

internal sealed record CatalogAuthorityReply(string Operation, int Version, CatalogAuthorityScope Scope, string Nonce,
    CatalogAuthoritySnapshot Snapshot)
{
    internal bool Matches(CatalogAuthorityRequest request) =>
        Operation == request.Operation && Version == request.Version && Scope == request.Scope && Nonce == request.Nonce &&
        Snapshot is not null && Snapshot.IsEvidenceFor(request.Scope);
}

internal sealed record CatalogAuthorityFrame(string Operation, int Version, CatalogAuthorityScope Scope, int Ordinal,
    string? Predecessor, CatalogAuthoritySnapshot Snapshot);

internal sealed record CatalogAuthorityFloor(string Operation, int Version, CatalogAuthorityScope Scope, int Ordinal, string Digest);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CatalogAuthorityRequest))]
[JsonSerializable(typeof(CatalogAuthorityReply))]
[JsonSerializable(typeof(CatalogAuthorityFrame))]
[JsonSerializable(typeof(CatalogAuthorityFloor))]
internal sealed partial class CatalogAuthorityJsonContext : JsonSerializerContext;

internal static class CatalogAuthorityV2
{
    internal const string ReadTopology = "CatalogTopologyRead";
    internal const int TopologyVersion = 2;
    internal const string ReadCommission = "CatalogCommissionRead";
    internal const int CommissionVersion = 2;
    internal const int MaxEngines = 128;
    internal const int MaxApprovals = 256;
    internal const int MaxNames = 256;
    internal const int MaxRequest = 1024;
    internal const int MaxReply = 512 * 1024;
    internal const int NonceBytes = 32;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static ValueTask WriteReplyAsync(Stream stream, CatalogAuthorityReply reply, CancellationToken ct) =>
        BoundedJsonFrame.WriteAsync(stream, reply, CatalogAuthorityJsonContext.Default.CatalogAuthorityReply, MaxReply, ct);
}
