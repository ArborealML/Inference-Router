using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Arboreal.InferenceRouter.Server;
using Head = Arboreal.InferenceRouter.OperationHead;
using Observer = Arboreal.InferenceRouter.SuccessorObserver;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    private static readonly UnixFileMode ScratchMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode StockSocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
    private static readonly string[] StockExpectedEffects = ["stop", "vacant", "create", "start"];
    private const uint BackendUid = 65534;
    private const uint HeadUid = 4;
    private const uint IngressUid = 1;
    private const uint ObserverUid = 2;
    private const uint RegistrarUid = 3;
    private const uint SharedGroup = 65534;

    [StockProcessTheory(Timeout = 150_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StockProcessRoutesTheOriginalQueuedSuccessor(bool standalone)
    {
        Assert.True(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64);
        Assert.Equal(0U, HeadReadV1Tests.EffectiveUid());
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = guard.Token;
        var artifacts = Environment.GetEnvironmentVariable("AIR_STOCK_PROCESS_BINARIES") ??
            throw new InvalidOperationException("The required stock proof must point to published executables.");
        var binary = Path.Combine(artifacts, standalone ? "Arboreal.InferenceRouter.Server" : "air");
        var terminalSocket = StockPath("terminal.sock");
        var ingressSocket = StockPath("ingress.sock");
        // The independently running retirement reader needs group access to newly created
        // broker slots, including the inactive slot written after this genesis.
        await SetStockOwnerAsync(_directory.FullName, 0, ct);
        StockSetMode(_directory.FullName, ScratchMode | UnixFileMode.SetGroup);
        var seed = await SeedStockProcessAsync(ingressSocket, ct);
        var a = Assert.Single(seed.Configuration.Catalog.Targets, target => target.Key.Engine == DeepSeekEngineName);
        var b = Assert.Single(seed.Configuration.Catalog.Targets, target => target.Key.Engine == QwenEngineName);
        var issuer = Guid.NewGuid();
        var release = StockPath("release-a");
        var releaseSuccessor = StockPath("release-b");
        var receiptsA = await PrepareReceiptAsync("writes-a", ct);
        var receiptsB = await PrepareReceiptAsync("writes-b", ct);
        await using var initial = await StockBackend.StartAsync(a, 1, terminalSocket, release, receiptsA, ct);
        initial.RequireKernelWitness(BackendUid);

        var opened = initial.Binding(seed.Scope, seed.Checkpoint, a, 1, 1, journal: null);
        await initial.RequireConnectorAsync(opened, ct);
        var genesis = new ServingBrokerRecord(ServingBrokerRecord.CurrentFormat, seed.Scope.Reservation, 0, null, issuer,
            opened, Open: false, Close: null, []);
        WriteFrame(Path.Combine(seed.Scope.Reservation.AuthorityDirectory, ServingBrokerFileStore.RecordsName),
            genesis, ServingBrokerRecordJsonContext.Default.ServingBrokerRecord);
        var payload = JsonSerializer.SerializeToUtf8Bytes(genesis, ServingBrokerRecordJsonContext.Default.ServingBrokerRecord);
        var genesisHead = new ServingBrokerHead(seed.Scope.Reservation, 0, Convert.ToHexString(SHA256.HashData(payload)));
        await File.WriteAllBytesAsync(Path.Combine(seed.Scope.Reservation.AuthorityDirectory, ServingBrokerFileStore.FenceName), [], ct);

        using var headKey = new StockPeerKey("operation-head");
        var headAuthority = await PrepareRoleAsync("head", HeadUid, headKey, ct, operationHead: true);
        var headRuntime = Directory.CreateDirectory(StockPath("head-run")).FullName;
        StockSetMode(headRuntime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        await SetStockOwnerAsync(headRuntime, HeadUid, ct);
        var headSocket = Path.Combine(headRuntime, Head.Program.SocketName);
        var headPeer = new SuccessorAuthorityPeer(headSocket, headKey.Pin);
        await WritePrivateAsync(headAuthority, Head.OperationHeadStore.StateName,
            JsonSerializer.SerializeToUtf8Bytes(OperationHeadSnapshot.Current(seed.OperationHead), OperationHeadJsonContext.Default.OperationHeadSnapshot), HeadUid, ct);
        await WritePrivateAsync(headAuthority, Head.OperationHeadStore.BrokerStateName,
            JsonSerializer.SerializeToUtf8Bytes(BrokerAnchorSnapshot.FromHead(new(genesisHead.Scope, genesisHead.Revision, genesisHead.Digest)),
                BrokerAnchorJsonContext.Default.BrokerAnchorSnapshot), HeadUid, ct);
        await WritePrivateAsync(headAuthority, Head.OperationHeadStore.LockName, [], HeadUid, ct);

        var operation = new OperationHeadClient(new UnixDomainSocketEndPoint(headSocket), headKey.Pin, seed.Scope.Reservation,
            OperationHeadProtocol.SuccessorVersion);
        var headBootstrap = new Head.OperationHeadBootstrap(2, seed.Scope.Reservation, ProducerUid: 0,
            BrokerWriterUid: 0, BrokerReaderUid: RegistrarUid, SuccessorReaderUid: IngressUid);
        await WriteHeadBootstrapAsync(headBootstrap);
        await using (var legacyHead = StockChild.StartRole(HeadUid, Path.Combine(artifacts, "air-operation-head"), headAuthority, headRuntime))
        {
            await WaitForHeadAsync(legacyHead);
            await CheckReadRoleAsync(IngressUid, "read-only");
            await CheckReadRoleAsync(RegistrarUid, "denied");
        }
        File.Delete(headSocket);
        await WriteHeadBootstrapAsync(headBootstrap with { RegistrarReaderUid = RegistrarUid });
        await using var head = StockChild.StartRole(HeadUid, Path.Combine(artifacts, "air-operation-head"), headAuthority, headRuntime);
        await WaitForHeadAsync(head);
        await CheckReadRoleAsync(IngressUid, "read-only");
        await CheckReadRoleAsync(RegistrarUid, "read-only");
        Assert.Equal(seed.OperationHead, operation.Read(seed.Scope.Reservation));

        var anchor = OperationHeadServingBrokerAnchor.ForReader(seed.Scope.Reservation, new(headSocket), headKey.Pin);
        var readBroker = ServingBrokerFileStore.OpenSnapshotReader(seed.Scope.Reservation, anchor);
        var journal = new EnrolledSuccessorFileJournal(seed.Scope.Reservation, seed.Scope.Commission.Head, operation);
        await using var daemon = new StockDaemon(seed, initial, a, b, terminalSocket, releaseSuccessor, receiptsB, readBroker, journal);
        await daemon.StartAsync(StockPath("docker-write"), StockPath("docker-get"), ct);
        await using var held = new StockSocketPeer(StockPath("held.sock"), (accepted, cert, token) =>
            SuccessorPhysicalWitnessEndpoint.ServeHeldPartitionAsync(accepted, IngressUid, cert, seed.Scope, daemon.ReadHeldAsync, token, 0));

        var catalogRecords = StockPath("catalog.records");
        var catalogFloor = StockPath("catalog.floor");
        var catalogFrame = new CatalogAuthorityFrame(CatalogAuthorityV2.ReadTopology, CatalogAuthorityV2.TopologyVersion,
            seed.Catalog.Scope, 0, null, seed.Catalog);
        WriteFrame(catalogRecords, catalogFrame, CatalogAuthorityJsonContext.Default.CatalogAuthorityFrame);
        var catalogDigest = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(catalogFrame, CatalogAuthorityJsonContext.Default.CatalogAuthorityFrame)));
        WriteFrame(catalogFloor, new CatalogAuthorityFloor(CatalogAuthorityV2.ReadTopology, CatalogAuthorityV2.TopologyVersion,
            seed.Catalog.Scope, 0, catalogDigest), CatalogAuthorityJsonContext.Default.CatalogAuthorityFloor);
        var topology = new CatalogAuthorityStore(seed.Catalog.Scope, catalogRecords, catalogFloor);
        await using var catalogSource = new StockSocketPeer(StockPath("catalog-source.sock"), (accepted, cert, token) =>
            CatalogAuthorityEndpoint.ServeCommissionAsync(accepted, 0, cert, seed.Catalog.Scope, _ => ValueTask.FromResult<CatalogAuthoritySnapshot?>(seed.Catalog), token));
        await using var catalogHead = new StockSocketPeer(StockPath("catalog-head.sock"), (accepted, cert, token) =>
            CatalogAuthorityEndpoint.ServeTopologyAsync(accepted, 0, cert, seed.Catalog.Scope, topology, token));

        var observerBinary = Path.Combine(artifacts, "air-successor-observer");
        using var ingressKey = new StockPeerKey("ingress");
        using var retirementKey = new StockPeerKey("retirement");
        using var observerKey = new StockPeerKey("observer");
        var ingressRole = await PrepareRoleAsync("ingress-role", IngressUid, ingressKey, ct);
        var retirementRole = await PrepareRoleAsync("retirement-role", RegistrarUid, retirementKey, ct);
        var observerRole = await PrepareRoleAsync("observation-role", ObserverUid, observerKey, ct);
        var auditRoot = await PrepareRoleAsync("audit", IngressUid, null, ct);
        await WritePrivateAsync(auditRoot, "daemon-ingress.audit", [], IngressUid, ct);
        var partitionSocket = StockPath("partition.sock");
        var retirementSocket = StockPath("retirement.sock");
        var observationSocket = StockPath("observe.sock");

        await WritePrivateAsync(ingressRole, "bootstrap.json", JsonSerializer.SerializeToUtf8Bytes(
            new Observer.IngressBootstrap(seed.Scope, 0, ingressSocket, StockPath("docker-write"), seed.CheckpointPath,
                headPeer, 10000, auditRoot, ObserverUid, partitionSocket, held.SuccessorPeer),
            Observer.ObserverBootstrapJson.Default.IngressBootstrap), IngressUid, ct);
        await WritePrivateAsync(retirementRole, "bootstrap.json", JsonSerializer.SerializeToUtf8Bytes(
            new Observer.RetirementBootstrap(seed.Scope, ObserverUid, headPeer),
            Observer.ObserverBootstrapJson.Default.RetirementBootstrap), RegistrarUid, ct);
        await WritePrivateAsync(observerRole, "bootstrap.json", JsonSerializer.SerializeToUtf8Bytes(
            new Observer.ObserverBootstrap(seed.Scope, 0, seed.CheckpointPath, StockPath("docker-get"), b.ApiVersion,
                new(partitionSocket, ingressKey.Pin), new(retirementSocket, retirementKey.Pin)),
            Observer.ObserverBootstrapJson.Default.ObserverBootstrap), ObserverUid, ct);

        await using var ingress = StockChild.Activate(IngressUid, [ingressSocket, partitionSocket], observerBinary, "ingress", ingressRole);
        await using var retirement = StockChild.Activate(RegistrarUid, [retirementSocket], observerBinary, "retirement", retirementRole);
        await using var observer = StockChild.Activate(ObserverUid, [observationSocket], observerBinary, "observe", observerRole);
        await WaitAsync(() => File.Exists(ingressSocket) && File.Exists(retirementSocket) && File.Exists(observationSocket),
            "observer role sockets", ct, ingress, retirement, observer);

        var routerAuthority = await PrepareRoleAsync("router-role", 0, null, ct);
        var startup = new CommissionedServerBootstrap(new(seed.Catalog.Scope, catalogSource.CatalogPeer, catalogHead.CatalogPeer),
            seed.Claim, seed.CheckpointPath, new(observationSocket, observerKey.Pin), headPeer, headPeer, held.SuccessorPeer,
            issuer, 15000, terminalSocket, BackendUid, 10000);
        await WritePrivateAsync(routerAuthority, "bootstrap.json",
            JsonSerializer.SerializeToUtf8Bytes(startup, CommissionedServerBootstrapJson.Default.CommissionedServerBootstrap), 0, ct);
        await WritePrivateAsync(routerAuthority, "router.toml", Encoding.UTF8.GetBytes(seed.Toml), 0, ct);
        var routeUrl = "http://127.0.0.1:28992";
        await using var router = StockChild.Activate(0, [terminalSocket], binary, "serve", "--commissioned", routerAuthority, "--urls", routeUrl);
        using var http = new HttpClient { BaseAddress = new Uri(routeUrl), Timeout = Timeout.InfiniteTimeSpan };
        await WaitForRouterAsync(http, router, ct);
        Assert.Empty(daemon.Effects);
        Assert.Equal(2, Assert.IsType<ServingBrokerSnapshot>(readBroker()).Record.Uses.Count(use => use.Phase == ServingBrokerUsePhase.ProbeCompleted));

        var bodyA = StockBody(a, "held-original");
        using var replyA = await PostStockAsync(http, a, bodyA, ct);
        Assert.Equal(HttpStatusCode.OK, replyA.StatusCode);
        await ValidateStockBufferedAsync(replyA, a.Key.Model, ct);
        var ticketA = Assert.Single(replyA.Headers.GetValues("X-Inference-Ticket"));
        var aUse = Assert.Single(Assert.IsType<ServingBrokerSnapshot>(readBroker()).Record.Uses,
            use => use.Kind == ServingBrokerUseKind.Application && use.Phase == ServingBrokerUsePhase.Issued);
        Assert.NotNull(aUse.Accepted);

        var bodyB = StockBody(b, "queued-original", stream: true);
        using var replyB = await PostStockAsync(http, b, bodyB, ct);
        Assert.Equal(HttpStatusCode.OK, replyB.StatusCode);
        Assert.Equal("text/event-stream", replyB.Content.Headers.ContentType?.MediaType);
        var firstPayload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamed = ValidateStockStreamAsync(replyB, b.Key.Model, firstPayload, ct);
        var ticketB = await ReadQueuedTicketAsync(http, b, ct);
        Assert.Equal(ticketB, Assert.Single(replyB.Headers.GetValues("X-Inference-Ticket")));
        await WaitAsync(() => readBroker()?.Record.Close is { Conclusive: false }, "sealed cutoff with A still held", ct, router);
        Assert.Empty(daemon.Effects);
        Assert.False(firstPayload.Task.IsCompleted);
        Assert.False(streamed.IsCompleted);
        Assert.Empty(File.ReadAllLines(receiptsB));
        await File.WriteAllBytesAsync(release, [], ct);

        var firstResult = await Task.WhenAny(firstPayload.Task, streamed).WaitAsync(ct);
        if (firstResult == streamed) await streamed;
        Assert.Equal(StockExpectedEffects, daemon.Effects);
        Assert.Equal(seed.Scope.Commission.Approvals.Single(approval => approval.Plan.Key == b.Key).Launch.Intent.CreateDigest,
            daemon.CreatedBytesDigest);
        ValidateReceipt(receiptsA, bodyA, initial, aUse);
        var finalBackend = Assert.IsType<StockBackend>(daemon.Successor);
        var bUse = Assert.Single(Assert.IsType<ServingBrokerSnapshot>(readBroker()).Record.Uses,
            use => use.Kind == ServingBrokerUseKind.Application && use.Phase == ServingBrokerUsePhase.Issued);
        ValidateReceipt(receiptsB, bodyB, finalBackend, bUse);
        await File.WriteAllBytesAsync(releaseSuccessor, [], ct);
        await streamed.WaitAsync(ct);
        Assert.True(operation.Read(seed.Scope.Reservation)?.Revision > seed.OperationHead.Revision);
        await WaitAsync(() => readBroker()?.Record.Uses.All(use => use.Kind != ServingBrokerUseKind.Application) == true,
            "successor terminal retirement", ct, router);
        var terminalCutoff = await AssertRequestDispositionsAsync(http, ticketA, ticketB, ct);
        var ready = Assert.IsType<ServingBrokerSnapshot>(readBroker()).Record.Binding;
        Assert.Equal(finalBackend.Identity.Listener.Bound.Port, b.Endpoint.Port);
        using var oldHandler = CommissionedServingPins.TryPin(finalBackend.Identity.Handler);
        Assert.NotNull(oldHandler);
        Assert.True(LinuxServingAcceptedSocket.MatchesListener(finalBackend.Identity.Listener, oldHandler));

        await finalBackend.StopAsync();
        Assert.False(LinuxServingAcceptedSocket.MatchesListener(finalBackend.Identity.Listener, oldHandler));
        var staleBytes = await PrepareReceiptAsync("stale-bytes", ct);
        await using var replacement = StockChild.StartRole(BackendUid, "python3", StockChild.SocketScript, "stale",
            b.Endpoint.Port.ToString(CultureInfo.InvariantCulture), staleBytes);
        _ = await replacement.ReadReadyAsync(ct);
        using (var reachable = new TcpClient())
            await reachable.ConnectAsync(IPAddress.Loopback, b.Endpoint.Port, ct);
        using var stale = await PostStockAsync(http, b, StockBody(b, "must-not-reach-replacement"), ct);
        Assert.Equal(HttpStatusCode.BadGateway, stale.StatusCode);
        using var refusal = JsonDocument.Parse(await stale.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal("engine_unreachable", refusal.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(File.ReadAllLines(staleBytes));
        Assert.Equal(StockExpectedEffects, daemon.Effects);
        var anchored = Assert.IsType<ServingBrokerSnapshot>(readBroker());
        Assert.Equal(ready, anchored.Record.Binding);
        var refusedCheckout = Assert.Single(anchored.Record.Uses, use => use.Kind == ServingBrokerUseKind.Application);
        Assert.Equal(ready.Route, refusedCheckout.Route);
        Assert.Equal(ServingBrokerUsePhase.Issued, refusedCheckout.Phase);
        Assert.Null(refusedCheckout.Accepted);
        await File.WriteAllLinesAsync(Path.Combine(artifacts, standalone ? "standalone.proof" : "air.proof"),
        [
            "router=" + Path.GetFileName(binary) + " " + router.Identity(0),
            "operation_head " + head.Identity(HeadUid) + " pin=" + headKey.Pin,
            "ingress " + ingress.Identity(IngressUid),
            "observer " + observer.Identity(ObserverUid),
            "retirement " + retirement.Identity(RegistrarUid),
            FormattableString.Invariant($"initial uid={BackendUid} handler={initial.Identity.Handler} worker={initial.Identity.Worker} listener={initial.Identity.Listener.Inode}/{initial.Identity.Listener.Cookie}"),
            FormattableString.Invariant($"successor uid={BackendUid} handler={finalBackend.Identity.Handler} worker={finalBackend.Identity.Worker} listener={finalBackend.Identity.Listener.Inode}/{finalBackend.Identity.Listener.Cookie}"),
            "replacement " + replacement.Identity(BackendUid),
            FormattableString.Invariant($"before={seed.OperationHead.Revision} after={operation.Read(seed.Scope.Reservation)!.Revision} broker={anchored.Record.Revision} route={anchored.Record.Binding.Route.RouteIncarnation}"),
            FormattableString.Invariant($"ticket_a={ticketA} ticket_b={ticketB} sealed_use={aUse.Id} successor_use={bUse.Id} cutoff_stop={daemon.StopOperation} terminal_seq={terminalCutoff} refused_use={refusedCheckout.Id}"),
            "initial " + Assert.Single(File.ReadAllLines(receiptsA)),
            "successor " + Assert.Single(File.ReadAllLines(receiptsB)),
            "effects=" + string.Join(',', daemon.Effects) + " stale_bytes=0 workers_reaped=2",
        ], ct);

        Task WriteHeadBootstrapAsync(Head.OperationHeadBootstrap boot) => WritePrivateAsync(headAuthority, Head.Program.BootstrapName,
            JsonSerializer.SerializeToUtf8Bytes(boot, Head.OperationHeadBootstrapJson.Default.OperationHeadBootstrap), HeadUid, ct);

        async Task WaitForHeadAsync(StockChild process)
        {
            await WaitAsync(() =>
            {
                try { return operation.Read(seed.Scope.Reservation) == seed.OperationHead; }
                catch (IOException) { return false; }
            }, "protected operation head", ct, process);
        }

        async Task CheckReadRoleAsync(uint uid, string access)
        {
            var probe = Path.Combine(artifacts, "Arboreal.InferenceRouter.HostReservationProbe.dll");
            await using var role = StockChild.StartRole(uid, Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", probe, "--commissioned-head-read",
                headSocket, headKey.Pin, seed.Scope.Reservation.Slot, access);
            var line = await role.ReadReadyAsync(ct);
            Assert.Equal(access == "denied" ? "read refused" : "read accepted; advance refused", line);
            Assert.Equal(0, await role.WaitForExitAsync(ct));
        }
    }

    private string StockPath(string name) => Path.Combine(_directory.FullName, name);
}


public sealed class StockProcessTheoryAttribute : TheoryAttribute
{
    public StockProcessTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("AIR_REQUIRE_STOCK_PROCESS_PROOF") != "1")
            Skip = "Stock process proof requires the isolated Linux fixture job.";
    }
}
