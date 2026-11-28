using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record SuccessorPhysicalWitnessRequest(int Version, HostReservationScope Scope,
    EnrolledPairHead Catalog, Guid Nonce, Guid? CloseId);

internal sealed record SuccessorPhysicalWitnessReply(SuccessorPhysicalWitnessRequest Request,
    SuccessorPhysicalSnapshot? Partition, EnrolledSuccessorRetirement? Retirement)
{
    internal bool Matches(SuccessorPhysicalWitnessRequest expected) => Request == expected &&
        (expected.CloseId is { } close
            ? Partition is null && Retirement?.CloseId == close
            : Retirement is null && Partition is { } partition &&
              partition.Scope == expected.Scope && partition.Catalog == expected.Catalog);
}

internal sealed record SuccessorHeldPartitionReply(SuccessorPhysicalWitnessRequest Request, SuccessorHeldPartition Partition)
{
    internal bool Matches(SuccessorPhysicalWitnessRequest expected) =>
        Request == expected && Partition is not null && Partition.Scope == expected.Scope &&
        Partition.Catalog == expected.Catalog;
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SuccessorPhysicalWitnessRequest))]
[JsonSerializable(typeof(SuccessorPhysicalWitnessReply))]
[JsonSerializable(typeof(SuccessorPhysicalSnapshot))]
[JsonSerializable(typeof(SuccessorHeldPartitionReply))]
internal sealed partial class SuccessorPhysicalWitnessJson : JsonSerializerContext;

// Neither operation can amend the partition, broker or worker record. The two peers are
// provisioned independently and never derive authority from values sent by the observer.
internal sealed class SuccessorPhysicalWitnessClient(EnrolledSuccessorScope scope,
    SuccessorAuthorityPeer partitionPeer, SuccessorAuthorityPeer brokerPeer)
{
    internal async ValueTask<SuccessorPhysicalSnapshot?> ReadPartitionAsync(CancellationToken ct) =>
        (await ReadAsync(partitionPeer, closeId: null, ct).ConfigureAwait(false))?.Partition;

    internal async ValueTask<EnrolledSuccessorRetirement?> ReadRetirementAsync(Guid closeId, CancellationToken ct) =>
        (await ReadAsync(brokerPeer, closeId, ct).ConfigureAwait(false))?.Retirement;

    private async ValueTask<SuccessorPhysicalWitnessReply?> ReadAsync(SuccessorAuthorityPeer peer, Guid? closeId,
        CancellationToken ct)
    {
        var request = NewRequest(scope, SuccessorPhysicalWitnessEndpoint.Version, closeId);
        var reply = await ExchangeAsync(peer, request, SuccessorPhysicalWitnessJson.Default.SuccessorPhysicalWitnessReply, ct)
            .ConfigureAwait(false);
        return reply?.Matches(request) == true ? reply : null;
    }

    // This peer is the installed exclusion guard. It reads the currently held partition
    // independently of Docker and does not receive writer effect bytes.
    internal static async ValueTask<SuccessorHeldPartition?> ReadHeldPartitionAsync(EnrolledSuccessorScope scope,
        SuccessorAuthorityPeer peer, CancellationToken ct)
    {
        var request = NewRequest(scope, SuccessorPhysicalWitnessEndpoint.HeldPartitionVersion, closeId: null);
        var reply = await ExchangeAsync(peer, request, SuccessorPhysicalWitnessJson.Default.SuccessorHeldPartitionReply, ct)
            .ConfigureAwait(false);
        return reply?.Matches(request) == true ? reply.Partition : null;
    }

    internal static async ValueTask<HeldServingGeneration?> ReadHeldServingAsync(EnrolledSuccessorScope scope,
        SuccessorAuthorityPeer peer, CheckedEnrolledTarget target, ServingBrokerBinding binding, bool expectedOpen, CancellationToken ct)
    {
        if (!binding.IsValid() || binding.Applied.State.AppliedLaunch is not { } applied ||
            applied.Plan != target.Plan) return null;
        var partition = await ReadHeldPartitionAsync(scope, peer, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var identity = ServingBrokerIssuer.ServingIdentity(binding);
        return partition is { ResidentRunning: true, Serving: { } serving } && partition.IngressClosed != expectedOpen &&
            partition.Revision > 0 && partition.OwnerGeneration > 0 && partition.CoverageGeneration > 0 &&
            partition.IngressGeneration > 0 && partition.Resident == applied && partition.ResidentRoute == binding.Route &&
            partition.ResidentImage == target.Image && partition.ResidentBackendRoute == target.Plan.BackendRoute &&
            VerifiedDispatchEnvelope.SameEffectiveUri(target.Endpoint, partition.ResidentEndpoint) &&
            SupervisedLaunchCommission.SameDevices(scope.Commission.Devices, partition.CoveredDevices) &&
            serving.Matches(scope, target, identity) && serving.Profile.Slots == binding.TotalSlots
                ? serving : null;
    }

    private static SuccessorPhysicalWitnessRequest NewRequest(EnrolledSuccessorScope scope, int version, Guid? closeId) =>
        new(version, scope.Reservation, scope.Commission.Head, Guid.NewGuid(), closeId);

    private static async ValueTask<T?> ExchangeAsync<T>(SuccessorAuthorityPeer peer,
        SuccessorPhysicalWitnessRequest request, JsonTypeInfo<T> type, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(OperationHeadProtocol.Timeout);
        var token = lifetime.Token;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(peer.SocketPath), token).ConfigureAwait(false);
            using var network = new NetworkStream(socket, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true, (_, certificate, _, _) =>
                string.Equals(certificate?.GetCertHashString(HashAlgorithmName.SHA256), peer.LeafSha256, StringComparison.Ordinal));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "successor-witness" }, token)
                .ConfigureAwait(false);
            await BoundedJsonFrame.WriteAsync(tls, request, SuccessorPhysicalWitnessJson.Default.SuccessorPhysicalWitnessRequest,
                SuccessorObservationExchange.RequestLimit, token).ConfigureAwait(false);
            var reply = await BoundedJsonFrame.ReadAsync(tls, type, SuccessorObservationExchange.ReplyLimit, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return reply;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }
}

internal static class SuccessorPhysicalWitnessEndpoint
{
    internal const int Version = 1;
    internal const int HeldPartitionVersion = 2;

    internal static Task ServePartitionAsync(Socket accepted, uint observerUid, X509Certificate2 certificate,
        EnrolledSuccessorScope scope, Func<CancellationToken, ValueTask<SuccessorPhysicalSnapshot?>> readPartition,
        CancellationToken ct) => ServeAsync(accepted, observerUid, certificate, scope, Version, retirementOnly: false,
        async (request, token) => new SuccessorPhysicalWitnessReply(request,
            await readPartition(token).ConfigureAwait(false), Retirement: null),
        static (request, reply) => reply.Matches(request), SuccessorPhysicalWitnessJson.Default.SuccessorPhysicalWitnessReply, ct);

    internal static Task ServeRetirementAsync(Socket accepted, uint observerUid, X509Certificate2 certificate,
        EnrolledSuccessorScope scope, Func<Guid, EnrolledSuccessorRetirement?> readRetirement, CancellationToken ct) =>
        ServeAsync(accepted, observerUid, certificate, scope, Version, retirementOnly: true,
            (request, _) => ValueTask.FromResult<SuccessorPhysicalWitnessReply?>(new SuccessorPhysicalWitnessReply(request, Partition: null,
                readRetirement(request.CloseId!.Value))),
            static (request, reply) => reply.Matches(request), SuccessorPhysicalWitnessJson.Default.SuccessorPhysicalWitnessReply, ct);

    internal static Task ServeHeldPartitionAsync(Socket accepted, uint ingressUid, X509Certificate2 certificate,
        EnrolledSuccessorScope scope, Func<CancellationToken, ValueTask<SuccessorHeldPartition?>> readHeldPartition,
        CancellationToken ct, uint? servingUid = null) =>
        ServeAsync(accepted, ingressUid, certificate, scope, HeldPartitionVersion, retirementOnly: false,
            async (request, token) => await readHeldPartition(token).ConfigureAwait(false) is { } held
                ? new(request, held) : null,
            static (request, reply) => reply.Matches(request),
            SuccessorPhysicalWitnessJson.Default.SuccessorHeldPartitionReply, ct, servingUid);

    private static async Task ServeAsync<T>(Socket accepted, uint observerUid, X509Certificate2 certificate,
        EnrolledSuccessorScope scope, int version, bool retirementOnly,
        Func<SuccessorPhysicalWitnessRequest, CancellationToken, ValueTask<T?>> read,
        Func<SuccessorPhysicalWitnessRequest, T, bool> matches, JsonTypeInfo<T> type, CancellationToken ct,
        uint? additionalReadUid = null) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var budget = new OperationHeadBudget(TimeProvider.System.GetTimestamp(), OperationHeadProtocol.Timeout, timeout.Token);
        timeout.CancelAfter(OperationHeadProtocol.Timeout);
        try
        {
            if (!HeadReadV1Endpoint.IsAuthorizedRequester(accepted, observerUid) &&
                (additionalReadUid is not { } uid || !HeadReadV1Endpoint.IsAuthorizedRequester(accepted, uid))) return;
            using var network = new NetworkStream(accepted, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, budget.Token)
                .ConfigureAwait(false);
            var request = await BoundedJsonFrame.ReadAsync(tls, SuccessorPhysicalWitnessJson.Default.SuccessorPhysicalWitnessRequest,
                SuccessorObservationExchange.RequestLimit, budget.Token).ConfigureAwait(false);
            if (request.Version != version || request.Scope != scope.Reservation || request.Catalog != scope.Commission.Head ||
                request.Nonce == Guid.Empty || request.CloseId == Guid.Empty ||
                (request.CloseId is not null) != retirementOnly) return;
            budget.Check();
            var reply = await read(request, budget.Token).ConfigureAwait(false);
            budget.Check();
            if (reply is null || !matches(request, reply)) return;
            await BoundedJsonFrame.WriteAsync(tls, reply, type,
                SuccessorObservationExchange.ReplyLimit, budget.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            // The observer receives no witness.
        }
    }
}
