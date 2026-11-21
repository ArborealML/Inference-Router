using System.Collections.Immutable;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

internal sealed record EnrolledPairHead(string Slot, string Partition, string Daemon, string RouterId,
    long Generation, long Revision, string Digest);

internal sealed record EnrolledPairMember(string Engine, string Model, string Tier, string Configuration, string BackendRoute);

internal sealed record EnrolledPairClaim(EnrolledPairHead Head, ImmutableArray<EnrolledPairMember> Members,
    ImmutableArray<string> Devices);

internal sealed record EnrolledPairMemberGrant(EnrolledPairMember Member, string SocketPath, Uri Endpoint, string PlanDigest);

internal sealed record EnrolledPairAuthority(EnrolledPairHead Head, ImmutableArray<EnrolledPairMemberGrant> Members,
    ImmutableArray<string> Devices);

internal sealed record CheckedEnrolledPair(EnrolledPairHead Head, ImmutableArray<string> Devices,
    ImmutableArray<CheckedEnrolledPairMember> Members);

internal sealed record CheckedEnrolledPairMember(HostCheckpointPlan Plan, string PlanDigest, string SocketPath,
    Uri Endpoint, DockerImageIdentity Image, string CreateJson);

internal sealed class EnrolledPairTopology
{
    private readonly RouterConfig _config;
    private readonly EnrolledPairClaim _claim;
    private readonly EnrolledPairAuthority _authority;
    private readonly Dictionary<string, EnrolledPairMemberGrant> _approved;

    private EnrolledPairTopology(RouterConfig config, EnrolledPairClaim claim, EnrolledPairAuthority authority,
        Dictionary<string, EnrolledPairMemberGrant> approved) =>
        (_config, _claim, _authority, _approved) = (config, claim, authority, approved);

    // The lookup key is selected by trusted bootstrap. The lookup returns one fresh snapshot
    // from outside the candidate's rollback domain. This check grants no ongoing authority.
    internal static CheckedEnrolledPair Check(RouterConfig config, string commissionedSlot, EnrolledPairClaim claim,
        Func<string, EnrolledPairAuthority?> readCurrent)
    {
        if (string.IsNullOrWhiteSpace(commissionedSlot) || claim.Head?.Slot != commissionedSlot ||
            readCurrent(commissionedSlot) is not { } authority)
            throw Refuse("current commissioned head mismatch");
        return Validate(config, commissionedSlot, claim, authority).Project();
    }

    internal static EnrolledPairTopology Validate(RouterConfig config, string commissionedSlot, EnrolledPairClaim claim,
        EnrolledPairAuthority authority)
    {
        if (string.IsNullOrWhiteSpace(commissionedSlot) || claim.Head is not { } head || head.Slot != commissionedSlot ||
            head != authority.Head ||
            string.IsNullOrWhiteSpace(authority.Head.Partition) || string.IsNullOrWhiteSpace(authority.Head.Daemon) ||
            string.IsNullOrWhiteSpace(authority.Head.RouterId) || authority.Head.Generation <= 0 ||
            authority.Head.Revision < 0 || !HostMutation.IsPlanDigest(authority.Head.Digest))
            throw Refuse("current commissioned head mismatch");

        if (claim.Devices.IsDefaultOrEmpty || authority.Devices.IsDefaultOrEmpty ||
            !IsDistinctPair(claim.Devices) || !IsDistinctPair(authority.Devices) ||
            !claim.Devices.ToHashSet(StringComparer.Ordinal).SetEquals(authority.Devices))
            throw Refuse("device coverage mismatch");

        if (config.Engines.Count != 2 || claim.Members.IsDefaultOrEmpty || authority.Members.IsDefaultOrEmpty ||
            !IsDistinctPair(claim.Members.Select(member => member?.Engine)) ||
            !IsDistinctPair(authority.Members.Select(grant => grant?.Member?.Engine)) ||
            !IsDistinctPair(claim.Members.Select(member => member.BackendRoute)) ||
            !IsDistinctPair(authority.Members.Select(grant => grant.Member.BackendRoute)))
            throw Refuse("member or route coverage mismatch");

        var approved = authority.Members.ToDictionary(grant => grant.Member.Engine, StringComparer.Ordinal);
        // Check both members before invoking the first Docker projection. Projection consumes the
        // authority's identities and therefore cannot detect a substituted candidate identity.
        foreach (var member in claim.Members)
        {
            if (!config.Engines.TryGetValue(member.Engine, out var engine) ||
                !approved.TryGetValue(member.Engine, out var grant) ||
                string.IsNullOrWhiteSpace(member.Model) || string.IsNullOrWhiteSpace(member.Tier) ||
                member.Model != grant.Member.Model || member.Tier != grant.Member.Tier)
                throw Refuse("member or selected target mismatch");
            if (member.Configuration != grant.Member.Configuration)
                throw Refuse("configuration identity mismatch");
            if (member.BackendRoute != grant.Member.BackendRoute)
                throw Refuse("backend route identity mismatch");
            if (string.IsNullOrWhiteSpace(grant.Member.Configuration) || !HostMutation.IsPlanDigest(grant.PlanDigest) ||
                !engine.HasExplicitRouterLifecycle || engine.Docker is not { } docker ||
                docker.RouterId != authority.Head.RouterId ||
                !DockerLifecycleProvider.TryParseUnixSocket(docker.Socket, out var path) ||
                path.Contains('\0', StringComparison.Ordinal) || !Path.IsPathFullyQualified(path) ||
                path != grant.SocketPath || Path.GetFullPath(path) != path || grant.Endpoint is not { IsAbsoluteUri: true } ||
                !VerifiedDispatchEnvelope.SameEffectiveUri(engine.Endpoint, grant.Endpoint))
                throw Refuse("engine ownership, socket or endpoint mismatch");
        }
        return new(config, claim, authority, approved);
    }

    internal CheckedEnrolledPair Project()
    {
        var members = ImmutableArray.CreateBuilder<CheckedEnrolledPairMember>(2);
        foreach (var member in _claim.Members)
        {
            var engine = _config.Engines[member.Engine];
            var grant = _approved[member.Engine];
            try
            {
                var resolver = new VerifiedDispatchTargetResolver(engine);
                var (target, plan, create) = resolver.ResolveProjection(member.Model, member.Tier, resident: null,
                    grant.Member.Configuration, grant.Member.BackendRoute);
                if (target.Model != member.Model || target.Tier != member.Tier || target.PlanDigest != grant.PlanDigest)
                    throw Refuse("effective launch plan mismatch");
                // Both selected targets request the entire commissioned two-device partition.
                // The independently sourced grant supplies its device identities.
                if (create.HostConfig is not { Devices: null, DeviceRequests: [{ Count: -1, DeviceIds: null }] })
                    throw Refuse("effective launch device coverage mismatch");

                var createJson = JsonSerializer.Serialize(create, DockerJsonContext.Default.DockerCreateRequest);
                members.Add(new(plan, target.PlanDigest, grant.SocketPath, target.Endpoint, engine.Docker!.ImageIdentity!, createJson));
            }
            catch (VerifiedDispatchBindingException)
            {
                throw Refuse("effective launch plan mismatch");
            }
        }

        return new(_authority.Head, _authority.Devices, members.MoveToImmutable());
    }

    private static bool IsDistinctPair(IEnumerable<string?> values)
    {
        var pair = values.ToArray();
        return pair is [var first, var second] && !string.IsNullOrWhiteSpace(first) &&
            !string.IsNullOrWhiteSpace(second) && first != second;
    }

    private static RouterConfigException Refuse(string reason) => new($"enrolled topology refused: {reason}");
}
