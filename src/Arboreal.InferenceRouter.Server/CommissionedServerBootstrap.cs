using System.Globalization;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arboreal.InferenceRouter.Lifecycle;
using Arboreal.InferenceRouter.Server.Commissioning;

namespace Arboreal.InferenceRouter.Server;

internal sealed record CommissionedServerBootstrap(CatalogAuthorityBootstrap Catalog, EnrolledCatalogClaim Claim,
    string CheckpointLock, SuccessorAuthorityPeer OperationObserver, SuccessorAuthorityPeer OperationHead,
    SuccessorAuthorityPeer BrokerHead, SuccessorAuthorityPeer HeldPartition, Guid IssuerEpoch, int DrainTimeoutMs,
    string TerminalSocket, uint HandlerUid, int TerminalTimeoutMs)
{
    internal bool IsValid() => Catalog is { Scope: { } scope, Observer: { } catalogObserver, Head: { } catalogHead } &&
        scope.IsValid() && Claim is not null && catalogObserver.IsValid() && catalogHead.IsValid() &&
        OperationObserver is not null && OperationHead is not null && BrokerHead == OperationHead &&
        OperationObserver.IsValid() && OperationHead.IsValid() && HeldPartition is not null && HeldPartition.IsValid() &&
        OperationObserver.SocketPath != OperationHead.SocketPath &&
        OperationObserver.LeafSha256 != OperationHead.LeafSha256 &&
        HeldPartition.SocketPath != OperationObserver.SocketPath && HeldPartition.SocketPath != OperationHead.SocketPath &&
        HeldPartition.LeafSha256 != OperationObserver.LeafSha256 && HeldPartition.LeafSha256 != OperationHead.LeafSha256 &&
        IssuerEpoch != Guid.Empty && IsExactPath(CheckpointLock) && IsExactPath(TerminalSocket) &&
        TerminalSocket != OperationObserver.SocketPath && TerminalSocket != OperationHead.SocketPath &&
        TerminalSocket != HeldPartition.SocketPath && DrainTimeoutMs > 0 && TerminalTimeoutMs > 0;

    private static bool IsExactPath(string path) => Path.IsPathFullyQualified(path) &&
        !path.Contains('\0', StringComparison.Ordinal) && Path.GetFullPath(path) == path;
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CommissionedServerBootstrap))]
internal sealed partial class CommissionedServerBootstrapJson : JsonSerializerContext;

internal sealed record CommissionedServerAuthority(CheckedEnrolledConfiguration Configured, EnrolledSuccessorScope Scope,
    CheckedEnrolledTarget InitialTarget, ServingBrokerBinding Binding, ServingBrokerLedger Ledger,
    string CheckpointLock, HeldServingGeneration InitialGeneration, Guid IssuerEpoch, Guid ApplicationWriter);

internal static class CommissionedServer
{
    private const int FileLimit = 2 * 1024 * 1024;

    internal static async Task<int> RunAsync(string[] args, string authorityDirectory, CancellationToken ct)
    {
        var (bootstrap, toml) = ReadBootstrap(authorityDirectory);
        using var listener = OpenTerminalListener(bootstrap);
        var intake = new CatalogAuthorityIntake(bootstrap.Catalog, TimeProvider.System);
        if (await intake.ReadStartupAsync(toml, bootstrap.Claim, ct).ConfigureAwait(false) is not { } startup)
            throw Unavailable();

        // Reuse the enrolled reservation's original holder fence until all serving work retires.
        // The successor controller still uses its separate checkpoint and issue fences.
        using var writer = HostReservation.OpenEnrolled(startup.Authority.Scope.Reservation);
        await using var native = new CommissionedNativeProgram(bootstrap, listener, ct);
        var app = await BuildAsync(args, bootstrap, startup, intake, native, native.StopToken).ConfigureAwait(false);
        try
        {
            await app.StartAsync(native.StopToken).ConfigureAwait(false);
            await app.WaitForShutdownAsync(native.StopToken).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Socket OpenTerminalListener(CommissionedServerBootstrap bootstrap)
    {
        if (bootstrap.HandlerUid == OperationHeadLinux.EffectiveUid() ||
            Environment.GetEnvironmentVariable("LISTEN_FDS") != "1" ||
            Environment.GetEnvironmentVariable("LISTEN_PID") != Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
            throw Unavailable();
#pragma warning disable IDISP001, CA2000 // Socket takes ownership on success. The catch disposes a failed transfer.
        var handle = new SafeSocketHandle(3, ownsHandle: true);
#pragma warning restore IDISP001, CA2000
        Socket listener;
        try { listener = new Socket(handle); }
        catch
        {
            handle.Dispose();
            throw;
        }
        if (listener is { AddressFamily: AddressFamily.Unix, SocketType: SocketType.Stream, IsBound: true,
                LocalEndPoint: UnixDomainSocketEndPoint endpoint } &&
            endpoint.ToString() == bootstrap.TerminalSocket &&
            listener.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.AcceptConnection) is 1)
            return listener;
        listener.Dispose();
        throw Unavailable();
    }

    private static (CommissionedServerBootstrap Bootstrap, string Toml) ReadBootstrap(string directoryPath)
    {
        if (!OperationHeadLinux.Supported || !Path.IsPathFullyQualified(directoryPath) ||
            directoryPath.Contains('\0', StringComparison.Ordinal) || Path.GetFullPath(directoryPath) != directoryPath)
            throw Unavailable();
        using var directory = OperationHeadLinux.OpenDirectory(directoryPath);
        var identity = OperationHeadLinux.DirectoryEntry(directory, OperationHeadLinux.EffectiveUid(), runtime: false);
        using var descriptors = OperationHeadLinux.OpenDescriptors();
        var bootstrap = JsonSerializer.Deserialize(ReadFixed("bootstrap.json"),
            CommissionedServerBootstrapJson.Default.CommissionedServerBootstrap);
        if (bootstrap is null || !bootstrap.IsValid()) throw Unavailable();
        var toml = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(ReadFixed("router.toml"));
        return (bootstrap, toml);

        byte[] ReadFixed(string name)
        {
            using var pinned = OperationHeadLinux.Pin(directory, name);
            var entry = OperationHeadLinux.CheckRegular(pinned, identity);
            using var data = OperationHeadLinux.ReopenRegular(descriptors, pinned, identity, entry);
            using var stream = new FileStream(data, FileAccess.Read);
            var bytes = new byte[FileLimit + 1];
            var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            using var current = OperationHeadLinux.Pin(directory, name);
            if (length == 0 || length > FileLimit || OperationHeadLinux.CheckRegular(data, identity) != entry ||
                OperationHeadLinux.CheckRegular(current, identity) != entry)
                throw Unavailable();
            return bytes[..length];
        }
    }

    private static async ValueTask<WebApplication> BuildAsync(string[] args, CommissionedServerBootstrap bootstrap,
        CheckedEnrolledStartup startup, CatalogAuthorityIntake intake, CommissionedNativeProgram native, CancellationToken ct)
    {
        var configured = startup.Configured;
        var trusted = startup.Authority;
        var scope = new EnrolledSuccessorScope(trusted.Scope.Reservation, trusted.Catalog);
        var checkpoint = HostEffectCheckpoint.ReadInstalledCommitment(bootstrap.CheckpointLock);
        if (checkpoint.State is not { Phase: HostCheckpointPhase.Resident, Epoch: 1, AppliedLaunch: { } launch } ||
            configured.Catalog.Resolve(launch.Plan.Engine, launch.Plan.Model, launch.Plan.Tier) is not { } target ||
            target.Plan != launch.Plan || !scope.Approves(target))
            throw Unavailable();

        var operationHead = new OperationHeadClient(new UnixDomainSocketEndPoint(bootstrap.OperationHead.SocketPath),
            bootstrap.OperationHead.LeafSha256, scope.Reservation, OperationHeadProtocol.SuccessorVersion);
        var journal = new EnrolledSuccessorFileJournal(scope.Reservation, scope.Commission.Head, operationHead);
        using (var fence = journal.TryEnter(scope.Reservation))
            if (fence?.ReadHead() is not { Revision: 0 } initial || initial.InitialResident != launch)
                throw Unavailable();

        var brokerHead = OperationHeadServingBrokerAnchor.ForWriter(scope.Reservation,
            new UnixDomainSocketEndPoint(bootstrap.OperationHead.SocketPath), bootstrap.OperationHead.LeafSha256);
        var store = ServingBrokerFileStore.OpenWriter(scope.Reservation, brokerHead);
        if (store.Read()?.Record is not { Close: null, Binding: { } binding } broker ||
            broker.IssuerEpoch != bootstrap.IssuerEpoch || binding.Scope != scope.Reservation || binding.CatalogHead != scope.Commission.Head ||
            binding.Applied != checkpoint || binding.AppliedJournalDigest is not null || !binding.IsValid() ||
            !target.Target.Matches(binding.Route))
            throw Unavailable();

        var ledger = new ServingBrokerLedger(scope.Reservation, bootstrap.IssuerEpoch, store);
        if (await SuccessorPhysicalWitnessClient.ReadHeldServingAsync(scope, bootstrap.HeldPartition, target, binding, broker.Open, ct)
                .ConfigureAwait(false) is not { } generation ||
            !await intake.IsCurrentAsync(trusted, ct).ConfigureAwait(false) ||
            HostEffectCheckpoint.ReadInstalledCommitment(bootstrap.CheckpointLock) != checkpoint ||
            ledger.BeginInitialProof(binding, broker.Open) is not { } applicationWriter)
            throw Unavailable();
        ct.ThrowIfCancellationRequested();

        var authority = new CommissionedServerAuthority(configured, scope, target, binding, ledger,
            bootstrap.CheckpointLock, generation, bootstrap.IssuerEpoch, applicationWriter);
        return await native.ConnectAsync(args, authority, token => intake.IsCurrentAsync(trusted, token), ct).ConfigureAwait(false);
    }

    private static InvalidOperationException Unavailable() =>
        new("The commissioned server could not bind its approved initial resident and serving authority.");
}
