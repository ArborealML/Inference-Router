using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record SupervisedLaunchApproval(HostInitialLaunchIntent Intent, DockerImageIdentity Image);

internal sealed record SupervisedLaunchCommission(HostReservationScope Scope, EnrolledPairAuthority Topology,
    ImmutableArray<SupervisedLaunchApproval> Approvals)
{
    internal bool Matches(SupervisedLaunchCommission? other) => other is not null && Scope == other.Scope &&
        Topology.Head == other.Topology.Head && SameDevices(Topology.Devices, other.Topology.Devices) &&
        !Topology.Members.IsDefault && !other.Topology.Members.IsDefault &&
        OrderedGrants(Topology).SequenceEqual(OrderedGrants(other.Topology)) &&
        !Approvals.IsDefault && !other.Approvals.IsDefault &&
        Approvals.OrderBy(approval => approval.Intent.Plan.Engine, StringComparer.Ordinal).SequenceEqual(
            other.Approvals.OrderBy(approval => approval.Intent.Plan.Engine, StringComparer.Ordinal));

    internal static bool SameDevices(ImmutableArray<string> expected, ImmutableArray<string> observed) =>
        !expected.IsDefault && !observed.IsDefault &&
        expected.Order(StringComparer.Ordinal).SequenceEqual(observed.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    internal SupervisedLaunchApproval? Approval(string engine) =>
        Approvals.SingleOrDefault(approval => approval.Intent.Plan.Engine == engine);

    internal EnrolledPairMemberGrant Grant(string engine) => Topology.Members.Single(grant => grant.Member.Engine == engine);

    private static IEnumerable<(EnrolledPairMember Member, string Socket, string Endpoint, string PlanDigest)> OrderedGrants(
        EnrolledPairAuthority topology) => topology.Members.OrderBy(grant => grant.Member.Engine, StringComparer.Ordinal)
            .Select(grant => (grant.Member, grant.SocketPath, grant.Endpoint.AbsoluteUri, grant.PlanDigest));
}

internal sealed record SupervisedLaunchPolicy(HostInitialPolicy Policy, bool Vacant);
internal sealed record SupervisedLaunchCommand(HostReservationScope Scope, EnrolledPairHead Head, string SocketPath,
    HostMutation Operation);
internal sealed record SupervisedObservedCreate(SupervisedLaunchCommand Command, string InstanceId,
    ReadOnlyMemory<byte> WireBytes, DockerImageIdentity Image, DockerCreateRequest EffectiveSettings,
    string BackendRoute, Uri Endpoint, ImmutableArray<string> Devices);
internal sealed record SupervisedObservedStart(SupervisedLaunchCommand Command, string InstanceId, bool Running);

// These ports have no live implementation. Observation receives the operation identity, independently
// of the writer's response and create buffer. An adapter must withhold incomplete effective settings.
// Commission and policy updates share the issue fence.
internal interface ISupervisedLaunchObserver
{
    SupervisedLaunchCommission? ReadCommission(HostReservationScope scope);
    SupervisedLaunchPolicy? ReadPolicy(HostReservationScope scope);
    DockerImageIdentity? ReadImage(EnrolledPairHead head, string socketPath, string imageId);
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
    internal static HostInitialCreate? VerifyCreate(SupervisedObservedCreate? observed, SupervisedLaunchCommand command,
        SupervisedLaunchApproval approval, EnrolledPairMemberGrant grant, ImmutableArray<string> devices)
    {
        if (observed is null || observed.Command != command || observed.InstanceId.Length != 64 ||
            !observed.InstanceId.All(char.IsAsciiHexDigit) || observed.Image != approval.Image ||
            observed.BackendRoute != grant.Member.BackendRoute ||
            !VerifiedDispatchEnvelope.SameEffectiveUri(observed.Endpoint, grant.Endpoint) ||
            !SupervisedLaunchCommission.SameDevices(devices, observed.Devices)) return null;

        var bytes = Convert.FromBase64String(approval.Intent.CreateBytes);
        if (!observed.WireBytes.Span.SequenceEqual(bytes)) return null;
        var digest = Convert.ToHexString(SHA256.HashData(observed.WireBytes.Span));
        if (digest != approval.Intent.CreateDigest ||
            JsonSerializer.Deserialize(bytes, DockerJsonContext.Default.DockerCreateRequest) is not { } approved ||
            !EffectiveSettingsMatch(approved, observed.EffectiveSettings)) return null;

        return new(command.Operation, observed.InstanceId, digest, CompletePair: true);
    }

    private static bool EffectiveSettingsMatch(DockerCreateRequest approved, DockerCreateRequest observed)
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
        var expectedFields = expected.EnumerateObject().Where(field => !normalized.Contains(field.Name, StringComparer.Ordinal));
        var actualFields = actual.EnumerateObject().Count(field => !normalized.Contains(field.Name, StringComparer.Ordinal));
        return expectedFields.Count() == actualFields && expectedFields.All(field =>
            actual.TryGetProperty(field.Name, out var value) && JsonElement.DeepEquals(field.Value, value));
    }
}
