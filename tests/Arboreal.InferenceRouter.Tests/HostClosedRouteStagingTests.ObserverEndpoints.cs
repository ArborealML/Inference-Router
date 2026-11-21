using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    public enum ObserverIntakeCase { Current, Advanced, MixedGrants, OldApproval, RolledBackRecords, WrongHeadPeer, HeldHeadDeadline }

    [Theory(Timeout = 30_000)]
    [InlineData(ObserverIntakeCase.Current)]
    [InlineData(ObserverIntakeCase.Advanced)]
    [InlineData(ObserverIntakeCase.MixedGrants)]
    [InlineData(ObserverIntakeCase.OldApproval)]
    [InlineData(ObserverIntakeCase.RolledBackRecords)]
    [InlineData(ObserverIntakeCase.WrongHeadPeer)]
    [InlineData(ObserverIntakeCase.HeldHeadDeadline)]
    public async Task ObserverEndpointsBindClosedIntakeToTheIndependentHead(ObserverIntakeCase testCase)
    {
        if (!OperatingSystem.IsLinux()) return;

        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var observer = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var head = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var daemon = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var newDaemon = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deepSeek = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var qwen = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var scratch = new HeadReadV1Tests.HeadScratch();
        var observerPath = Path.Combine(_directory.FullName, "observer.sock");
        var headEndpoint = new UnixDomainSocketEndPoint(Path.Combine(_directory.FullName, "head.sock"));
        var daemonPath = Path.Combine(_directory.FullName, "daemon.sock");
        Listen(observer, new UnixDomainSocketEndPoint(observerPath));
        Listen(head, headEndpoint);
        Listen(daemon, new UnixDomainSocketEndPoint(daemonPath));
        Listen(deepSeek, new IPEndPoint(IPAddress.Loopback, 0));
        Listen(qwen, new IPEndPoint(IPAddress.Loopback, 0));

        using var observerKey = RSA.Create(2048);
        using var headKey = RSA.Create(2048);
        using var observerCertificate = CommissionCertificate(observerKey);
        using var headCertificate = CommissionCertificate(headKey);
        var observerPin = observerCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        var headPin = headCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        Assert.NotEqual(observerPin, headPin);

        var deepSeekEndpoint = new Uri($"http://{deepSeek.LocalEndPoint}/");
        var qwenEndpoint = new Uri($"http://{qwen.LocalEndPoint}/v1");
        var (text, claim, authority) = EnrolledPairTopologyFixture(daemonPath, deepSeekEndpoint, qwenEndpoint);
        var slot = authority.Head.Slot;
        var scope = new HostReservationScope(ReservationPath, Guid.NewGuid(),
            new(authority.Head.Partition, authority.Head.Generation));
        var expected = RouterConfigLoader.PrepareEnrolledPair(text, slot, claim, _ => authority);
        var original = new SupervisedLaunchCommission(scope, authority, FixtureCommissionApprovals(expected));
        var headScope = HeadReadScope.Topology(slot);
        var first = await scratch.AppendAsync(headScope, 0, null, new(HeadReadOutcome.Current, original));
        var current = original;
        var commission = original;
        var intakeClaim = claim;
        if (testCase is ObserverIntakeCase.Advanced or ObserverIntakeCase.MixedGrants or ObserverIntakeCase.OldApproval
            or ObserverIntakeCase.RolledBackRecords)
        {
            var oldRecords = await File.ReadAllBytesAsync(scratch.Records, lifetime.Token);
            var advanced = authority.Head with { Revision = authority.Head.Revision + 1, Digest = new string('B', 64) };
            intakeClaim = claim with { Head = advanced };
            current = original with { Topology = authority with { Head = advanced } };
            if (testCase == ObserverIntakeCase.MixedGrants)
            {
                var newDaemonPath = Path.Combine(_directory.FullName, "new-daemon.sock");
                Listen(newDaemon, new UnixDomainSocketEndPoint(newDaemonPath));
                var (currentText, _, currentTopology) = EnrolledPairTopologyFixture(newDaemonPath, deepSeekEndpoint, qwenEndpoint);
                var trustedTopology = currentTopology with { Head = advanced, Devices = authority.Devices };
                var currentPair = RouterConfigLoader.PrepareEnrolledPair(currentText, slot, intakeClaim, _ => trustedTopology);
                current = current with { Topology = trustedTopology };
                Assert.True(current.ApprovesPair(currentPair));
            }
            if (testCase == ObserverIntakeCase.OldApproval)
                current = current with { Approvals = current.Approvals.SetItem(0, current.Approvals[0] with { ApiVersion = "v1.44" }) };
            if (testCase is ObserverIntakeCase.MixedGrants or ObserverIntakeCase.OldApproval)
                commission = original with { Topology = authority with { Head = advanced } };
            await scratch.AppendAsync(headScope, 1, first, new(HeadReadOutcome.Current, current));
            if (testCase == ObserverIntakeCase.RolledBackRecords)
                await File.WriteAllBytesAsync(scratch.Records, oldRecords, lifetime.Token);
        }

        var pathsBefore = ObserverScratchPaths(scratch);
        var recordsBefore = await File.ReadAllBytesAsync(scratch.Records, lifetime.Token);
        Assert.InRange(BinaryPrimitives.ReadInt32BigEndian(recordsBefore), HeadReadStore.RecordPayloadLimit + 1,
            HeadReadV1.MaxReply);
        var floorBefore = await File.ReadAllBytesAsync(scratch.Floor, lifetime.Token);
        var commissionReads = 0;
        var headReads = 0;
        var parses = 0;
        var uid = HeadReadV1Tests.EffectiveUid();
        var replyWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObserver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerServer = new CommissionObserverEndpoint(scope, slot, uid, observerCertificate, () =>
        {
            Interlocked.Increment(ref commissionReads);
            return commission;
        })
        {
            AfterReplyForTests = ct =>
            {
                replyWritten.SetResult();
                return releaseObserver.Task.WaitAsync(ct);
            },
        };
        var store = scratch.Open(headScope);
        var headRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<HeadReadValue> ReadHeadAsync(HeadReadScope requested, CancellationToken ct)
        {
            Interlocked.Increment(ref headReads);
            if (testCase == ObserverIntakeCase.HeldHeadDeadline)
            {
                headRequest.SetResult();
                await releaseHead.Task.WaitAsync(lifetime.Token);
            }
            return await store.ReadAsync(requested, ct);
        }

        var bootstrap = new CommissionBootstrap(scope, slot, observerPath, observerPin);
        var expectedHeadPin = testCase == ObserverIntakeCase.WrongHeadPeer ? observerPin : headPin;
        var received = new ReceivedObserverHead(new CommissioningHeadV1Reader(slot, headEndpoint, expectedHeadPin));
        var clock = new ReadinessDeadlineTests.ManualClock();
        var intake = new CommissionIntake(bootstrap, received, clock)
        {
            BeforePairParseForTests = () => Interlocked.Increment(ref parses),
        };
        var servingObserver = ServeOneObserverRequestAsync(observer, observerServer.ServeAsync, lifetime.Token);
        var servingHead = ServeOneObserverRequestAsync(head, (socket, ct) =>
            HeadReadV1Endpoint.ServeAsync(socket, uid, headCertificate, headScope, ReadHeadAsync, ct), lifetime.Token);
        var reading = intake.ReadAsync(text, intakeClaim, lifetime.Token).AsTask();
        CheckedEnrolledPair? result;
        try
        {
            await replyWritten.Task.WaitAsync(lifetime.Token);
            if (testCase == ObserverIntakeCase.HeldHeadDeadline)
            {
                await headRequest.Task.WaitAsync(lifetime.Token);
                Assert.False(reading.IsCompleted);
                clock.AdvanceTo(5_000);
            }
            result = await reading.WaitAsync(lifetime.Token);
            Assert.False(lifetime.IsCancellationRequested);
            Assert.False(servingObserver.IsCompleted);
            if (testCase == ObserverIntakeCase.HeldHeadDeadline)
                Assert.False(servingHead.IsCompleted);
        }
        finally
        {
            releaseObserver.TrySetResult();
            releaseHead.TrySetResult();
            await lifetime.CancelAsync();
            await Task.WhenAll(servingObserver, servingHead, reading);
        }

        Assert.Equal(1, Volatile.Read(ref commissionReads));
        Assert.Equal(testCase == ObserverIntakeCase.WrongHeadPeer ? 0 : 1, Volatile.Read(ref headReads));
        if (testCase is ObserverIntakeCase.RolledBackRecords or ObserverIntakeCase.WrongHeadPeer or ObserverIntakeCase.HeldHeadDeadline)
            Assert.Null(received.Current);
        else
        {
            var trusted = Assert.IsType<SupervisedLaunchCommission>(received.Current);
            Assert.NotSame(current, trusted);
            Assert.True(current.Matches(trusted));
            Assert.Equal(intakeClaim.Head, trusted.Topology.Head);
            if (testCase is ObserverIntakeCase.MixedGrants or ObserverIntakeCase.OldApproval)
                Assert.Equal(commission.Topology.Head, trusted.Topology.Head);
            if (testCase == ObserverIntakeCase.MixedGrants)
            {
                Assert.Equal(commission.Topology.Devices.ToArray(), trusted.Topology.Devices.ToArray());
                Assert.NotSame(commission.Approvals[0], trusted.Approvals[0]);
                Assert.Equal(commission.Approvals.ToArray(), trusted.Approvals.ToArray());
                Assert.NotEqual(commission.Topology.Members[0].SocketPath, trusted.Topology.Members[0].SocketPath);
            }
        }
        Assert.Equal(testCase == ObserverIntakeCase.Current ? 1 : 0, Volatile.Read(ref parses));
        if (testCase != ObserverIntakeCase.Current) Assert.Null(result);
        else
        {
            var pair = Assert.IsType<CheckedEnrolledPair>(result);
            Assert.Equal(expected.Head, pair.Head);
            Assert.Equal(expected.Devices.ToArray(), pair.Devices.ToArray());
            Assert.Equal(expected.Members.ToArray(), pair.Members.ToArray());
        }

        Assert.Equal(pathsBefore, ObserverScratchPaths(scratch));
        Assert.Equal(recordsBefore, await File.ReadAllBytesAsync(scratch.Records, CancellationToken.None));
        Assert.Equal(floorBefore, await File.ReadAllBytesAsync(scratch.Floor, CancellationToken.None));
        Assert.False(File.Exists(scope.Slot));
        Assert.False(Directory.Exists(Path.Combine(_directory.FullName, "reservation-authority")));
        Assert.False(daemon.Poll(TimeSpan.Zero, SelectMode.SelectRead));
        if (testCase == ObserverIntakeCase.MixedGrants)
            Assert.False(newDaemon.Poll(TimeSpan.Zero, SelectMode.SelectRead));
        Assert.False(deepSeek.Poll(TimeSpan.Zero, SelectMode.SelectRead));
        Assert.False(qwen.Poll(TimeSpan.Zero, SelectMode.SelectRead));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(null, 0)]
    [InlineData("000000727B2256657273696F6E223A312C22536C6F74223A22666978747572652D736C6F74222C224368616C" +
        "6C656E6765223A22303030313032303330343035303630373038303930413042304330443045304631303131" +
        "31323133313431353136313731383139314131423143314431453146227D", 1)]
    [InlineData("000000707B2256657273696F6E223A312C22536C6F74223A2277726F6E672D736C6F74222C224368616C6C65" +
        "6E6765223A223030303130323033303430353036303730383039304130423043304430453046313031313132" +
        "3133313431353136313731383139314131423143314431453146227D", 0)]
    [InlineData("000000727B2256657273696F6E223A312C22536C6F74223A22666978747572652D736C6F74222C224368616C" +
        "6C656E6765223A225A3030313032303330343035303630373038303930413042304330443045304631303131" +
        "31323133313431353136313731383139314131423143314431453146227D", 0)]
    public async Task ObserverEndpointsValidateCommissionRequestFramesBeforeReadingTheCommission(string? frameHex, int readsExpected)
    {
        if (!OperatingSystem.IsLinux()) return;

        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(Path.Combine(_directory.FullName, "observer.sock"));
        Listen(listener, endpoint);
        using var key = RSA.Create(2048);
        using var certificate = CommissionCertificate(key);
        var scope = new HostReservationScope(ReservationPath, Guid.NewGuid(), new("group", 1));
        var uid = HeadReadV1Tests.EffectiveUid();
        var reads = 0;
        var server = new CommissionObserverEndpoint(scope, "fixture-slot", frameHex is null ? uid ^ 1u : uid,
            certificate, () =>
            {
                Interlocked.Increment(ref reads);
                return null;
            });
        var serving = ServeOneObserverRequestAsync(listener, server.ServeAsync, lifetime.Token);
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(endpoint, lifetime.Token);
            using var network = new NetworkStream(socket, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true,
                (_, remote, _, _) => remote is not null && string.Equals(remote.GetCertHashString(HashAlgorithmName.SHA256),
                    certificate.GetCertHashString(HashAlgorithmName.SHA256), StringComparison.Ordinal));
            if (frameHex is null)
            {
                var error = await Record.ExceptionAsync(() => tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = "observer" }, lifetime.Token));
                Assert.True(error is AuthenticationException or IOException or SocketException);
            }
            else
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "observer" }, lifetime.Token);
                await tls.WriteAsync(Convert.FromHexString(frameHex), lifetime.Token);
                Assert.Equal(0, await tls.ReadAsync(new byte[1], lifetime.Token));
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await serving;
        }

        Assert.Equal(readsExpected, Volatile.Read(ref reads));
    }

    private string[] ObserverScratchPaths(HeadReadV1Tests.HeadScratch scratch) =>
        Directory.EnumerateFileSystemEntries(_directory.FullName, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFileSystemEntries(scratch.Root, "*", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal).ToArray();

    private sealed class ReceivedObserverHead(CommissioningHeadV1Reader reader) : ICommissioningHeadReader
    {
        internal SupervisedLaunchCommission? Current { get; private set; }

        public async ValueTask<SupervisedLaunchCommission?> ReadCurrentAsync(string trustedSlot, CommissionReadBudget budget,
            CancellationToken ct)
        {
            Current = await reader.ReadCurrentAsync(trustedSlot, budget, ct);
            return Current;
        }
    }

    private static async Task ServeOneObserverRequestAsync(Socket listener, Func<Socket, CancellationToken, Task> handle,
        CancellationToken ct)
    {
        try
        {
            using var connection = await listener.AcceptAsync(ct);
            await handle(connection, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cleanup when intake refuses before reaching this endpoint.
        }
    }
}
