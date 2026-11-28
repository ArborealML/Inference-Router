using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record SupervisedLaunchApproval(HostInitialLaunchIntent Intent, DockerImageIdentity Image,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ApiVersion = null)
{
    internal bool HasPinnedApiVersion() => IsPinnedApiVersion(ApiVersion);

    internal static bool IsPinnedApiVersion(string? version)
    {
        if (version is not { Length: >= 4 } || version[0] != 'v') return false;
        var dot = version.IndexOf('.', StringComparison.Ordinal);
        return dot > 1 && dot < version.Length - 1 &&
            version.AsSpan(1, dot - 1).IndexOfAnyExceptInRange('0', '9') < 0 &&
            version.AsSpan(dot + 1).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}

internal sealed record SupervisedLaunchCommission(HostReservationScope Scope, EnrolledPairAuthority Topology,
    ImmutableArray<SupervisedLaunchApproval> Approvals)
{
    internal bool AuthorizesScope(HostReservationScope scope, string commissionedSlot) =>
        Scope == scope && scope.Enrollment is { } enrollment &&
        Topology?.Head is { } head && head.Slot == commissionedSlot &&
        head.Partition == enrollment.Group && head.Generation == enrollment.Generation;

    internal bool ApprovesPair(CheckedEnrolledPair pair) =>
        !Approvals.IsDefault && Approvals.Length == pair.Members.Length &&
        Approvals.All(approval => approval is { Intent.Plan: not null } && approval.HasPinnedApiVersion()) &&
        Approvals.Select(approval => approval.Intent.Plan.Engine).Distinct(StringComparer.Ordinal).Count() == pair.Members.Length &&
        pair.Members.All(member =>
            Approval(member.Plan.Engine) is { } approval && member.Image == approval.Image &&
            member.PlanDigest == approval.Intent.Plan.Digest() &&
            HostInitialLaunchIntent.Freeze(member.Plan, Encoding.UTF8.GetBytes(member.CreateJson)) == approval.Intent);

    internal bool Matches(SupervisedLaunchCommission? other) =>
        other is not null && HasComparableContents() && other.HasComparableContents() && Scope == other.Scope &&
        Topology.Head == other.Topology.Head && SameDevices(Topology.Devices, other.Topology.Devices) &&
        OrderedGrants(Topology).SequenceEqual(OrderedGrants(other.Topology)) &&
        Approvals.OrderBy(approval => approval.Intent.Plan.Engine, StringComparer.Ordinal).SequenceEqual(
            other.Approvals.OrderBy(approval => approval.Intent.Plan.Engine, StringComparer.Ordinal));

    private bool HasComparableContents() =>
        Scope is not null && Topology is { Head: not null } && !Topology.Members.IsDefault &&
        Topology.Members.All(grant => grant is { Member: not null, Endpoint.IsAbsoluteUri: true }) &&
        !Approvals.IsDefault && Approvals.All(approval => approval is { Intent.Plan: not null, Image: not null });

    internal static bool SameDevices(ImmutableArray<string> expected, ImmutableArray<string> observed) =>
        !expected.IsDefault && !observed.IsDefault &&
        expected.Order(StringComparer.Ordinal).SequenceEqual(observed.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    internal SupervisedLaunchApproval? Approval(string engine) =>
        Approvals.SingleOrDefault(approval => approval.Intent.Plan.Engine == engine);

    internal EnrolledPairMemberGrant Grant(string engine) => Topology.Members.Single(grant => grant.Member.Engine == engine);

    internal SupervisedLaunchCommand Command(HostInitialDispatch dispatch) =>
        new(Scope, Topology.Head, Grant(dispatch.Operation.Engine).SocketPath, dispatch.Operation,
            Approval(dispatch.Operation.Engine)!.ApiVersion!);

    private static IEnumerable<(EnrolledPairMember Member, string Socket, string Endpoint, string PlanDigest)> OrderedGrants(
        EnrolledPairAuthority topology) => topology.Members.OrderBy(grant => grant.Member.Engine, StringComparer.Ordinal)
            .Select(grant => (grant.Member, grant.SocketPath, grant.Endpoint.AbsoluteUri, grant.PlanDigest));
}

internal sealed record SupervisedLaunchPolicy(HostInitialPolicy Policy, bool Vacant);
internal sealed record SupervisedLaunchCommand(HostReservationScope Scope, EnrolledPairHead Head, string SocketPath,
    HostMutation Operation, string ApiVersion);

internal sealed record SupervisedStartIssue(SupervisedLaunchCommand Command, string InstanceId, string CreateDigest)
{
    internal bool Matches(SupervisedLaunchCommission commission, HostInitialDispatch dispatch) =>
        Command == commission.Command(dispatch) && SupervisedLaunchEvidence.IsFullContainerId(InstanceId) &&
        HostMutation.IsPlanDigest(CreateDigest) && CreateDigest == dispatch.Intent.CreateDigest;
}

internal sealed record SupervisedObservedCreate(SupervisedLaunchCommand Command, string? InstanceId,
    ReadOnlyMemory<byte> WireBytes, DockerImageIdentity Image, DockerCreateRequest? EffectiveSettings,
    string BackendRoute, Uri Endpoint, ImmutableArray<string> Devices);
internal sealed record SupervisedObservedStart(SupervisedLaunchCommand Command, string InstanceId, bool Running);

// The observer receives the operation identity independently
// of the writer's response and create buffer. An adapter must withhold incomplete effective settings.
// The observer supplies the commissioned API pin with the launch approval.
// Commission and policy updates share the issue fence.
internal interface ISupervisedLaunchObserver
{
    SupervisedLaunchCommission? ReadCommission(HostReservationScope scope);
    SupervisedLaunchPolicy? ReadPolicy(HostReservationScope scope);
    DockerImageIdentity? ReadImage(EnrolledPairHead head, string socketPath, string apiVersion, string imageId);
    SupervisedObservedCreate? ReadCreate(SupervisedLaunchCommand command);
    SupervisedObservedStart? ReadStart(SupervisedLaunchCommand command);
}

internal interface ISupervisedLaunchPort
{
    ValueTask<string?> CreateAsync(SupervisedLaunchCommand command, ReadOnlyMemory<byte> bytes, CancellationToken ct);
    ValueTask<bool> StartAsync(SupervisedLaunchCommand command, string instance, CancellationToken ct);
}

internal static class SupervisedLaunchEvidence
{
    internal static bool IsFullContainerId(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigit);

    internal static HostInitialCreate? VerifyCreate(SupervisedObservedCreate? observed, SupervisedLaunchCommand command,
        SupervisedLaunchApproval approval, EnrolledPairMemberGrant grant, ImmutableArray<string> devices)
        => VerifyCreate(observed, command, approval, grant.Member.BackendRoute, grant.Endpoint, devices);

    internal static HostInitialCreate? VerifyCreate(SupervisedObservedCreate? observed, SupervisedLaunchCommand command,
        SupervisedLaunchApproval approval, string backendRoute, Uri endpoint, ImmutableArray<string> devices)
    {
        if (observed is not { InstanceId: { } instance, EffectiveSettings: { } effective } ||
            observed.Command != command || !IsFullContainerId(instance) || observed.Image != approval.Image ||
            observed.BackendRoute != backendRoute ||
            !VerifiedDispatchEnvelope.SameEffectiveUri(observed.Endpoint, endpoint) ||
            !SupervisedLaunchCommission.SameDevices(devices, observed.Devices)) return null;

        var bytes = Convert.FromBase64String(approval.Intent.CreateBytes);
        if (!observed.WireBytes.Span.SequenceEqual(bytes)) return null;
        var digest = Convert.ToHexString(SHA256.HashData(observed.WireBytes.Span));
        if (digest != approval.Intent.CreateDigest ||
            JsonSerializer.Deserialize(bytes, DockerJsonContext.Default.DockerCreateRequest) is not { } approved ||
            !EffectiveSettingsMatch(approved, effective)) return null;

        return new(command.Operation, instance, digest, CompletePair: true);
    }

    internal static bool EffectiveSettingsMatch(DockerCreateRequest approved, DockerCreateRequest observed)
    {
        if (approved.HostConfig is not { } expectedHost || observed.HostConfig is not { } actualHost ||
            NetworkMode(expectedHost.NetworkMode) != NetworkMode(actualHost.NetworkMode) ||
            (expectedHost.Init ?? false) != (actualHost.Init ?? false)) return false;

        var expected = JsonSerializer.SerializeToElement(approved, DockerJsonContext.Default.DockerCreateRequest);
        var actual = JsonSerializer.SerializeToElement(observed, DockerJsonContext.Default.DockerCreateRequest);
        return SameFields(expected, actual, "HostConfig") &&
            SameFields(expected.GetProperty("HostConfig"), actual.GetProperty("HostConfig"), "NetworkMode", "Init");
    }

    // Only the recognized defaults in this evidence contract are interchangeable. Other fields,
    // including resource limits, process arguments, mounts and port bindings, must match exactly.
    private static string NetworkMode(string? value) => value is null or "" or "default" ? "bridge" : value;

    private static bool SameFields(JsonElement expected, JsonElement actual, params string[] normalized)
    {
        var expectedCount = 0;
        foreach (var field in expected.EnumerateObject())
        {
            if (normalized.Contains(field.Name, StringComparer.Ordinal)) continue;
            expectedCount++;
            if (!actual.TryGetProperty(field.Name, out var value) || !JsonElement.DeepEquals(field.Value, value))
                return false;
        }

        var actualCount = 0;
        foreach (var field in actual.EnumerateObject())
        {
            if (!normalized.Contains(field.Name, StringComparer.Ordinal)) actualCount++;
        }
        return expectedCount == actualCount;
    }
}
