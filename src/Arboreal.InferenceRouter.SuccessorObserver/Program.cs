using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arboreal.InferenceRouter.Lifecycle;
using Arboreal.InferenceRouter.OperationHead;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.SuccessorObserver;

internal sealed record ObserverBootstrap(EnrolledSuccessorScope Scope, uint RouterUid, string CheckpointLock,
    string DockerGetSocket, string DockerApiVersion, SuccessorAuthorityPeer Partition, SuccessorAuthorityPeer Broker)
{
    internal bool IsValid() => Scope is { Reservation.Enrollment: { } enrollment, Commission.Head: { } head } &&
        OperationHeadProtocol.IsScopeValid(Scope.Reservation) && head.Generation == enrollment.Generation &&
        head.Partition == enrollment.Group && HeadReadScope.IsName(head.RouterId) &&
        OperationHeadProtocol.IsCanonicalDigest(head.Digest) &&
        IsCanonicalPath(CheckpointLock) && IsCanonicalPath(DockerGetSocket) &&
        SupervisedLaunchApproval.IsPinnedApiVersion(DockerApiVersion) &&
        Partition is not null && Broker is not null && Partition.IsValid() && Broker.IsValid() &&
        Partition.SocketPath != Broker.SocketPath && Partition.LeafSha256 != Broker.LeafSha256 &&
        Scope.Commission.Engines is { IsDefaultOrEmpty: false } &&
        Scope.Commission.Approvals is { IsDefaultOrEmpty: false } &&
        Scope.Commission.Devices is { IsDefaultOrEmpty: false } &&
        Scope.Commission.Approvals.All(approval => approval is { Plan.Endpoint.IsAbsoluteUri: true, Launch.Intent.Plan: not null } &&
            approval.Launch.HasPinnedApiVersion()) &&
        Scope.Commission.Approvals.Select(approval => approval.Plan.Key).Distinct().Count() == Scope.Commission.Approvals.Length &&
        Scope.Commission.Engines.All(grant => grant is not null && grant.SocketPath != DockerGetSocket &&
            grant.SocketPath != Partition.SocketPath && grant.SocketPath != Broker.SocketPath) &&
        Scope.Commission.Engines.Select(grant => grant.Engine).Distinct(StringComparer.Ordinal).Count() ==
            Scope.Commission.Engines.Length;

    internal static bool IsCanonicalPath(string path) => Path.IsPathFullyQualified(path) &&
        !path.Contains('\0', StringComparison.Ordinal) && Path.GetFullPath(path) == path;
}

internal sealed record IngressBootstrap(EnrolledSuccessorScope Scope, uint WriterUid, string WriterSocket,
    string DockerSocket, string CheckpointLock, SuccessorAuthorityPeer OperationHead, int RequestTimeoutMs,
    string AuditDirectory, uint ObserverUid, string PartitionSocket, SuccessorAuthorityPeer HeldPartition,
    bool InitialLaunch = false)
{
    internal bool IsValid(uint ownerUid) => Scope is { Commission.Head: { } head, Reservation.Enrollment: { } enrollment } &&
        OperationHeadProtocol.IsScopeValid(Scope.Reservation) && head.Partition == enrollment.Group &&
        head.Generation == enrollment.Generation && WriterUid != ownerUid && ObserverUid != ownerUid &&
        WriterUid != ObserverUid && RequestTimeoutMs > 0 &&
        new CatalogAuthoritySnapshot(new(Scope.Reservation, head.Slot), Scope.Commission).IsEvidenceFor(new(Scope.Reservation, head.Slot)) &&
        ObserverBootstrap.IsCanonicalPath(WriterSocket) && ObserverBootstrap.IsCanonicalPath(DockerSocket) &&
        ObserverBootstrap.IsCanonicalPath(CheckpointLock) && ObserverBootstrap.IsCanonicalPath(AuditDirectory) &&
        ObserverBootstrap.IsCanonicalPath(PartitionSocket) && OperationHead is not null && OperationHead.IsValid() &&
        HeldPartition is not null && HeldPartition.IsValid() && HeldPartition.LeafSha256 != OperationHead.LeafSha256 &&
        new[] { WriterSocket, DockerSocket, OperationHead.SocketPath, PartitionSocket, HeldPartition.SocketPath }
            .Distinct(StringComparer.Ordinal).Count() == 5 &&
        Scope.Commission.Engines is { IsDefaultOrEmpty: false } &&
        Scope.Commission.Engines.All(grant => grant.SocketPath != DockerSocket &&
            grant.SocketPath != OperationHead.SocketPath && grant.SocketPath != PartitionSocket &&
            grant.SocketPath != HeldPartition.SocketPath) &&
        Scope.Commission.Engines.Any(grant => grant.SocketPath == WriterSocket);

    internal ValueTask<SuccessorHeldPartition?> ReadHeldPartitionAsync(CancellationToken ct) =>
        SuccessorPhysicalWitnessClient.ReadHeldPartitionAsync(Scope, HeldPartition, ct);
}

internal sealed record RetirementBootstrap(EnrolledSuccessorScope Scope, uint ObserverUid, SuccessorAuthorityPeer BrokerHead)
{
    internal bool IsValid(uint ownerUid) => Scope is { Commission.Head: { } head, Reservation.Enrollment: { } enrollment } &&
        OperationHeadProtocol.IsScopeValid(Scope.Reservation) && head.Partition == enrollment.Group &&
        head.Generation == enrollment.Generation && ObserverUid != ownerUid && BrokerHead is not null && BrokerHead.IsValid();
}

internal sealed record HeldPartitionBootstrap(EnrolledSuccessorScope Scope, uint IngressUid, uint ServingUid,
    string CheckpointLock, string DockerGetSocket, string DockerApiVersion, SuccessorAuthorityPeer BrokerHead,
    bool InitialLaunch = false)
{
    internal bool IsValid(uint ownerUid) => Scope is { Commission.Head: { } head } &&
        OperationHeadProtocol.IsScopeValid(Scope.Reservation) &&
        new CatalogAuthoritySnapshot(new(Scope.Reservation, head.Slot), Scope.Commission).IsEvidenceFor(new(Scope.Reservation, head.Slot)) &&
        IngressUid != ownerUid && ServingUid != ownerUid && ServingUid != IngressUid &&
        ObserverBootstrap.IsCanonicalPath(CheckpointLock) && ObserverBootstrap.IsCanonicalPath(DockerGetSocket) &&
        SupervisedLaunchApproval.IsPinnedApiVersion(DockerApiVersion) && BrokerHead is not null && BrokerHead.IsValid() &&
        BrokerHead.SocketPath != DockerGetSocket &&
        Scope.Commission.Engines.All(grant => grant.SocketPath != DockerGetSocket && grant.SocketPath != BrokerHead.SocketPath);
}

internal sealed record CatalogReaderBootstrap(CatalogAuthorityScope Scope, uint RouterUid, string SocketPath,
    string? Records = null, string? Floor = null)
{
    internal bool IsValid(uint ownerUid, bool topology) => Scope is not null && Scope.IsValid() &&
        RouterUid != ownerUid && ObserverBootstrap.IsCanonicalPath(SocketPath) &&
        (topology
            ? Records is not null && Floor is not null && Records != Floor &&
                ObserverBootstrap.IsCanonicalPath(Records) && ObserverBootstrap.IsCanonicalPath(Floor)
            : Records is null && Floor is null);
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ObserverBootstrap))]
[JsonSerializable(typeof(IngressBootstrap))]
[JsonSerializable(typeof(RetirementBootstrap))]
[JsonSerializable(typeof(HeldPartitionBootstrap))]
[JsonSerializable(typeof(CommissionedPartitionPolicy))]
[JsonSerializable(typeof(CatalogReaderBootstrap))]
[JsonSerializable(typeof(CatalogAuthoritySnapshot))]
[JsonSerializable(typeof(HeldServingGeneration))]
internal sealed partial class ObserverBootstrapJson : JsonSerializerContext;

internal static class Program
{
    private const int ActivationDescriptor = 3;
    private const int BootstrapLimit = 2 * 1024 * 1024;
    private const int PemLimit = 64 * 1024;

    private static async Task<int> Main(string[] args)
    {
        var mode = args.Length == 1 ? "observe" : args.Length == 2 ? args[0] : "";
        if (!OperationHeadLinux.Supported || mode is not ("observe" or "ingress" or "retirement" or "held" or
                "catalog-topology" or "catalog-commission") || !ObserverBootstrap.IsCanonicalPath(args[^1]) ||
            Environment.GetEnvironmentVariable("LISTEN_FDS") != (mode == "ingress" ? "2" : "1") ||
            Environment.GetEnvironmentVariable("LISTEN_PID") != Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
        {
            Console.Error.WriteLine("Usage: air-successor-observer [observe|ingress|retirement|held|catalog-topology|catalog-commission] <private-authority-directory>.");
            return 2;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += Cancel;
        try
        {
            await RunAsync(mode, args[^1], stop.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error) ||
            error is RouterConfigException or VerifiedDispatchBindingException ||
            error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.Error.WriteLine("The commissioned successor observer could not serve.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }

        void Cancel(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            stop.Cancel();
        }
    }

    private static async Task RunAsync(string mode, string authorityPath, CancellationToken ct)
    {
        using var directory = OperationHeadLinux.OpenDirectory(authorityPath);
        var identity = OperationHeadLinux.DirectoryEntry(directory, OperationHeadLinux.EffectiveUid(), runtime: false);
        using var descriptors = OperationHeadLinux.OpenDescriptors();
        var settings = ReadFixed("bootstrap.json", BootstrapLimit);
        using var listener = AdoptListener(ActivationDescriptor);
        if (mode is "catalog-topology" or "catalog-commission")
        {
            var topology = mode == "catalog-topology";
            var catalog = JsonSerializer.Deserialize(settings, ObserverBootstrapJson.Default.CatalogReaderBootstrap);
            if (catalog is null || !catalog.IsValid(identity.Uid, topology) ||
                listener.LocalEndPoint is not UnixDomainSocketEndPoint endpoint ||
                endpoint.ToString() != catalog.SocketPath) throw OperationHeadLinux.InvalidObject();

            using var catalogCertificate = ReadCertificate();
            if (topology)
            {
                var store = new CatalogAuthorityStore(catalog.Scope, catalog.Records!, catalog.Floor!);
                await ListenAsync(listener, (accepted, token) => CatalogAuthorityEndpoint.ServeTopologyAsync(
                    accepted, catalog.RouterUid, catalogCertificate, catalog.Scope, store, token), ct).ConfigureAwait(false);
            }
            else
            {
                await ListenAsync(listener, (accepted, token) => CatalogAuthorityEndpoint.ServeCommissionAsync(
                    accepted, catalog.RouterUid, catalogCertificate, catalog.Scope, ReadCommission, token), ct).ConfigureAwait(false);
            }
            return;

            ValueTask<CatalogAuthoritySnapshot?> ReadCommission(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(JsonSerializer.Deserialize(ReadFixed("commission.json", BootstrapLimit),
                    ObserverBootstrapJson.Default.CatalogAuthoritySnapshot));
            }
        }

        if (mode == "ingress")
        {
            var ingressSettings = JsonSerializer.Deserialize(settings, ObserverBootstrapJson.Default.IngressBootstrap);
            if (ingressSettings is null || ingressSettings.InitialLaunch || !ingressSettings.IsValid(identity.Uid) ||
                listener.LocalEndPoint is not UnixDomainSocketEndPoint unix ||
                unix.ToString() != ingressSettings.WriterSocket)
                throw OperationHeadLinux.InvalidObject();

            using var partitionListener = AdoptListener(ActivationDescriptor + 1);
            if (partitionListener.LocalEndPoint is not UnixDomainSocketEndPoint partitionSocket ||
                partitionSocket.ToString() != ingressSettings.PartitionSocket) throw OperationHeadLinux.InvalidObject();
            const string auditName = "daemon-ingress.audit";
            using var auditDirectory = OperationHeadLinux.OpenDirectory(ingressSettings.AuditDirectory);
            var auditIdentity = OperationHeadLinux.DirectoryEntry(auditDirectory, identity.Uid, runtime: false);
            using var auditPin = OperationHeadLinux.Pin(auditDirectory, auditName);
            _ = OperationHeadLinux.CheckRegular(auditPin, auditIdentity);
            var anchor = new OperationHeadClient(new UnixDomainSocketEndPoint(ingressSettings.OperationHead.SocketPath),
                ingressSettings.OperationHead.LeafSha256, ingressSettings.Scope.Reservation, OperationHeadProtocol.SuccessorVersion);
            var journal = new EnrolledSuccessorFileJournal(ingressSettings.Scope.Reservation,
                ingressSettings.Scope.Commission.Head, anchor);
            var audit = new ProtectedSuccessorDaemonAudit(ingressSettings.Scope.Reservation,
                Path.Combine(ingressSettings.AuditDirectory, auditName));
            using var ingress = new DockerLifecycleProvider.SuccessorIngress(ingressSettings.Scope, ingressSettings.WriterSocket,
                ingressSettings.DockerSocket, ingressSettings.CheckpointLock, journal.ReadIssuedForIngress, audit,
                ingressSettings.ReadHeldPartitionAsync);
            var allowance = TimeSpan.FromMilliseconds(ingressSettings.RequestTimeoutMs);
            var physical = new ProtectedSuccessorPhysicalPublisher(ingressSettings.Scope, ingressSettings.CheckpointLock,
                audit, ingressSettings.ReadHeldPartitionAsync);
            using var partitionCertificate = ReadCertificate();
            using var listeners = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = listeners.Token;
            var receive = ListenAsync(listener, (accepted, requestToken) =>
                SuccessorIngressEndpoint.ServeAsync(accepted, ingressSettings.WriterUid, ingress, allowance, requestToken), token);
            var publish = ListenAsync(partitionListener, (accepted, requestToken) =>
                SuccessorPhysicalWitnessEndpoint.ServePartitionAsync(accepted,
                    ingressSettings.ObserverUid, partitionCertificate,
                    ingressSettings.Scope, physical.ReadAsync, requestToken), token);
            await Task.WhenAny(receive, publish).ConfigureAwait(false);
            await listeners.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(receive, publish).ConfigureAwait(false);
            return;
        }

        using var certificate = ReadCertificate();
        if (mode == "held")
        {
            var held = JsonSerializer.Deserialize(settings, ObserverBootstrapJson.Default.HeldPartitionBootstrap);
            if (held is null || held.InitialLaunch || !held.IsValid(identity.Uid)) throw OperationHeadLinux.InvalidObject();
            using var lockName = OperationHeadLinux.Pin(directory, "partition.guard.lock");
            var lockEntry = OperationHeadLinux.CheckRegular(lockName, identity);
            using var gate = OperationHeadLinux.ReopenRegular(descriptors, lockName, identity, lockEntry, write: true);
            OperationHeadLinux.Lock(gate);
            using var kernel = CommissionedPartitionKernelGuard.Open(ReadPartitionPolicy().Policy.Kernel);
            if (kernel is null) throw OperationHeadLinux.InvalidObject();

            var headEndpoint = new UnixDomainSocketEndPoint(held.BrokerHead.SocketPath);
            using var daemon = new DockerLifecycleProvider.SuccessorCensus(held.DockerGetSocket, held.DockerApiVersion);
            var operation = new OperationHeadClient(headEndpoint, held.BrokerHead.LeafSha256, held.Scope.Reservation,
                OperationHeadProtocol.SuccessorVersion);
            var anchor = OperationHeadServingBrokerAnchor.ForReader(held.Scope.Reservation, headEndpoint, held.BrokerHead.LeafSha256);
            var readBroker = ServingBrokerFileStore.OpenSnapshotReader(held.Scope.Reservation, anchor);
            var journal = new EnrolledSuccessorFileJournal(held.Scope.Reservation, held.Scope.Commission.Head, operation);
            var register = new CommissionedServingRegistration(held.Scope, daemon, journal, kernel);
            var producer = new CommissionedHeldPartitionProducer(held.Scope, held.CheckpointLock, daemon, readBroker,
                ReadGuardedPolicy, kernel.IsServingCurrent, register.CaptureAsync);

            CommissionedPartitionPolicyRead ReadGuardedPolicy()
            {
                var policy = ReadPartitionPolicy();
                return kernel.IsCurrent(policy.Policy) ? policy : throw OperationHeadLinux.InvalidObject();
            }
            await ListenHeldAsync(listener, held, certificate, producer.ReadAsync, ct).ConfigureAwait(false);
            return;
        }

        if (mode == "retirement")
        {
            var retirement = JsonSerializer.Deserialize(settings, ObserverBootstrapJson.Default.RetirementBootstrap);
            if (retirement is null || !retirement.IsValid(identity.Uid))
                throw OperationHeadLinux.InvalidObject();

            var anchor = OperationHeadServingBrokerAnchor.ForReader(retirement.Scope.Reservation,
                new UnixDomainSocketEndPoint(retirement.BrokerHead.SocketPath), retirement.BrokerHead.LeafSha256);
            var read = ServingBrokerFileStore.OpenRetirementReader(retirement.Scope.Reservation, anchor);
            await ListenAsync(listener, (accepted, token) => SuccessorPhysicalWitnessEndpoint.ServeRetirementAsync(
                accepted, retirement.ObserverUid, certificate, retirement.Scope, read, token), ct).ConfigureAwait(false);
            return;
        }

        var bootstrap = JsonSerializer.Deserialize(settings, ObserverBootstrapJson.Default.ObserverBootstrap);
        if (bootstrap is null || !bootstrap.IsValid() || bootstrap.RouterUid == identity.Uid)
            throw OperationHeadLinux.InvalidObject();
        using var census = new DockerLifecycleProvider.SuccessorCensus(bootstrap.DockerGetSocket, bootstrap.DockerApiVersion);
        var witnesses = new SuccessorPhysicalWitnessClient(bootstrap.Scope, bootstrap.Partition, bootstrap.Broker);
        var observer = new CommissionedPhysicalSuccessorObserver(bootstrap.Scope, bootstrap.CheckpointLock, census,
            witnesses.ReadPartitionAsync, witnesses.ReadRetirementAsync);
        await observer.RunAsync(listener, bootstrap.RouterUid, certificate, ct).ConfigureAwait(false);

        CommissionedPartitionPolicyRead ReadPartitionPolicy()
        {
            var bytes = ReadFixed("partition-policy.json", BootstrapLimit);
            var policy = JsonSerializer.Deserialize(bytes, ObserverBootstrapJson.Default.CommissionedPartitionPolicy) ??
                throw OperationHeadLinux.InvalidObject();
            return new(policy, Convert.ToHexString(SHA256.HashData(bytes)));
        }

        X509Certificate2 ReadCertificate() => X509Certificate2.CreateFromPem(
            Encoding.UTF8.GetString(ReadFixed("certificate.pem", PemLimit)),
            Encoding.UTF8.GetString(ReadFixed("key.pem", PemLimit)));

        byte[] ReadFixed(string name, int limit)
        {
            using var pinned = OperationHeadLinux.Pin(directory, name);
            var entry = OperationHeadLinux.CheckRegular(pinned, identity);
            using var data = OperationHeadLinux.ReopenRegular(descriptors, pinned, identity, entry);
            using var stream = new FileStream(data, FileAccess.Read);
            var bytes = new byte[limit + 1];
            var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            using var current = OperationHeadLinux.Pin(directory, name);
            if (length == 0 || length > limit || OperationHeadLinux.CheckRegular(data, identity) != entry ||
                OperationHeadLinux.CheckRegular(current, identity) != entry)
                throw OperationHeadLinux.InvalidObject();
            return bytes[..length];
        }
    }

    private static Task ListenHeldAsync(Socket listener, HeldPartitionBootstrap bootstrap, X509Certificate2 certificate,

        Func<CancellationToken, ValueTask<SuccessorHeldPartition?>> read, CancellationToken ct) =>
        ListenAsync(listener, (accepted, token) => SuccessorPhysicalWitnessEndpoint.ServeHeldPartitionAsync(
            accepted, bootstrap.IngressUid, certificate, bootstrap.Scope, read, token, bootstrap.ServingUid), ct);

    private static async Task ListenAsync(Socket listener, Func<Socket, CancellationToken, Task> handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var accepted = await listener.AcceptAsync(ct).ConfigureAwait(false);
            await handle(accepted, ct).ConfigureAwait(false);
        }
    }

    private static Socket AdoptListener(int descriptor)
    {
#pragma warning disable IDISP001, CA2000 // Socket takes ownership on success. The catch disposes this handle if construction fails.
        var handle = new SafeSocketHandle(descriptor, ownsHandle: true);
#pragma warning restore IDISP001, CA2000
        Socket listener;
        try { listener = new Socket(handle); }
        catch
        {
            handle.Dispose();
            throw;
        }
        if (listener is { AddressFamily: AddressFamily.Unix, SocketType: SocketType.Stream, IsBound: true } &&
            listener.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.AcceptConnection) is 1)
            return listener;
        listener.Dispose();
        throw OperationHeadLinux.InvalidObject();
    }
}
