using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    public enum CommissionCase
    {
        Current, ReboundOld, MixedContent, ApprovalOnly, WrongPeer, WrongChallenge, WrongSlot, PartialFrame, LateReply, StalledHead,
    }

    [Theory(Timeout = 30_000)]
    [InlineData(CommissionCase.Current)]
    [InlineData(CommissionCase.ReboundOld)]
    [InlineData(CommissionCase.MixedContent)]
    [InlineData(CommissionCase.ApprovalOnly)]
    [InlineData(CommissionCase.WrongPeer)]
    [InlineData(CommissionCase.WrongChallenge)]
    [InlineData(CommissionCase.WrongSlot)]
    [InlineData(CommissionCase.PartialFrame)]
    [InlineData(CommissionCase.LateReply)]
    [InlineData(CommissionCase.StalledHead)]
    public async Task CommissionIntakeUsesOneAuthenticatedBoundedClosedExchange(CommissionCase testCase)
    {
        if (!OperatingSystem.IsLinux()) return;

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var observer = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var daemon = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deepSeek = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var qwen = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var observerPath = Path.Combine(_directory.FullName, "observer.sock");
        var daemonPath = Path.Combine(_directory.FullName, "daemon.sock");
        Listen(observer, new UnixDomainSocketEndPoint(observerPath));
        Listen(daemon, new UnixDomainSocketEndPoint(daemonPath));
        Listen(deepSeek, new IPEndPoint(IPAddress.Loopback, 0));
        Listen(qwen, new IPEndPoint(IPAddress.Loopback, 0));

        using var trustedKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        using var trusted = CommissionCertificate(trustedKey);
        using var other = CommissionCertificate(otherKey);
        var (text, claim, authority) = EnrolledPairTopologyFixture(daemonPath,
            new Uri($"http://{deepSeek.LocalEndPoint}/"), new Uri($"http://{qwen.LocalEndPoint}/v1"));
        var scope = new HostReservationScope(ReservationPath, Guid.NewGuid(),
            new(authority.Head.Partition, authority.Head.Generation));
        var expected = RouterConfigLoader.PrepareEnrolledPair(text, authority.Head.Slot, claim, _ => authority);
        var commission = new SupervisedLaunchCommission(scope, authority, FixtureCommissionApprovals(expected));
        var bootstrap = new CommissionBootstrap(scope, authority.Head.Slot, observerPath,
            Convert.ToHexString(trusted.GetCertHash(HashAlgorithmName.SHA256)));
        var candidate = new CommissionCandidate(text, claim, commission, bootstrap, expected);
        var scratch = CaptureCommissionScratch(observerPath, daemonPath);

        if (testCase == CommissionCase.ReboundOld)
        {
            await RunCommissionExchangeAsync(observer, trusted, candidate, commission,
                new string('A', 64), CommissionCase.Current, watchdog.Token);
            Assert.Equal(scratch, CaptureCommissionScratch(observerPath, daemonPath));
            var advanced = authority.Head with { Revision = authority.Head.Revision + 1, Digest = new string('B', 64) };
            var current = commission with { Topology = authority with { Head = advanced } };
            await RunCommissionExchangeAsync(observer, trusted, candidate with { Claim = claim with { Head = advanced } }, current,
                new string('B', 64), testCase, watchdog.Token);
        }
        else if (testCase is CommissionCase.MixedContent or CommissionCase.ApprovalOnly)
        {
            var advanced = authority.Head with { Revision = authority.Head.Revision + 1, Digest = new string('B', 64) };
            var rebound = commission with { Topology = authority with { Head = advanced } };
            var current = rebound with
            {
                Approvals = rebound.Approvals.SetItem(0, rebound.Approvals[0] with { ApiVersion = "v1.45" }),
            };
            if (testCase == CommissionCase.MixedContent)
            {
                var grants = current.Topology.Members;
                current = current with
                {
                    Topology = current.Topology with
                    {
                        Members = grants.SetItem(0, grants[0] with { SocketPath = Path.Combine(_directory.FullName, "current.sock") }),
                    },
                };
            }
            await RunCommissionExchangeAsync(observer, trusted,
                candidate with { Claim = claim with { Head = advanced }, Commission = rebound }, current,
                new string('B', 64), testCase, watchdog.Token);
        }
        else
        {
            await RunCommissionExchangeAsync(observer, testCase == CommissionCase.WrongPeer ? other : trusted,
                candidate, commission, new string('A', 64), testCase, watchdog.Token);
        }

        Assert.Equal(scratch, CaptureCommissionScratch(observerPath, daemonPath));
        Assert.False(daemon.Poll(TimeSpan.Zero, SelectMode.SelectRead));
        Assert.False(deepSeek.Poll(TimeSpan.Zero, SelectMode.SelectRead));
        Assert.False(qwen.Poll(TimeSpan.Zero, SelectMode.SelectRead));
    }

    private static async Task RunCommissionExchangeAsync(Socket listener, X509Certificate2 certificate,
        CommissionCandidate candidate, SupervisedLaunchCommission current, string challenge, CommissionCase testCase,
        CancellationToken ct)
    {
        var clock = new ReadinessDeadlineTests.ManualClock();
        var barriers = new CommissionExchangeBarriers();
        var request = new CommissionRequest(CommissionCodec.Version, candidate.Bootstrap.CommissionedSlot, challenge);
        var independentBytes = JsonSerializer.SerializeToUtf8Bytes(
            new CommissionReply(request.Version, request.Slot, request.Challenge, current),
            CommissionJsonContext.Default.CommissionReply);
        var decoded = Assert.IsType<CommissionReply>(
            JsonSerializer.Deserialize(independentBytes, CommissionJsonContext.Default.CommissionReply)).Commission;
        Assert.NotSame(current, decoded);
        var head = new CommissionHead(decoded, testCase == CommissionCase.StalledHead);
        var parses = 0;
        var intake = new CommissionIntake(candidate.Bootstrap, head, clock)
        {
            ChallengeForTests = challenge,
            BeforePairParseForTests = () => Interlocked.Increment(ref parses),
            PayloadReadPendingForTests = (offset, readToken) =>
            {
                var expectedOffset = testCase == CommissionCase.PartialFrame ? CommissionExchangeBarriers.PayloadPrefix : 0;
                if (offset == expectedOffset) barriers.PendingPayload.TrySetResult(readToken);
            },
        };
        var server = ServeCommissionFixtureAsync(listener, certificate, request, candidate.Commission, testCase, barriers, ct);
        var read = intake.ReadAsync(candidate.Text, candidate.Claim, ct).AsTask();
        try
        {
            if (testCase != CommissionCase.WrongPeer)
                Assert.Equal(request, await barriers.Request.Task.WaitAsync(ct));

            if (testCase is CommissionCase.PartialFrame or CommissionCase.LateReply or CommissionCase.StalledHead)
            {
                var readToken = await barriers.PendingPayload.Task.WaitAsync(ct);
                Assert.False(read.IsCompleted);
                Assert.False(server.IsCompleted);
                Assert.Equal(0, Volatile.Read(ref parses));
                Assert.False(head.Entered.Task.IsCompleted);

                if (testCase == CommissionCase.LateReply) clock.AdvanceTo(5000, fireTimers: false);
                if (testCase == CommissionCase.StalledHead) clock.AdvanceTo(4000, fireTimers: false);
                barriers.ReleasePayload.TrySetResult();

                if (testCase == CommissionCase.StalledHead)
                {
                    var observed = await head.Entered.Task.WaitAsync(ct);
                    Assert.Equal(request.Slot, observed.Slot);
                    Assert.Same(clock, observed.Budget.Clock);
                    Assert.Equal(0L, observed.Budget.StartedAt);
                    Assert.Equal(TimeSpan.FromSeconds(5), observed.Budget.Duration);
                    Assert.Equal(TimeSpan.FromSeconds(1), observed.Remaining);
                    Assert.Equal(readToken, observed.Token);
                    Assert.Equal(1, clock.ActiveTimers);
                    Assert.False(head.Data.Task.IsCompleted);
                    clock.AdvanceTo(5000);
                    Assert.Equal(readToken, await head.Cancelled.Task.WaitAsync(ct));
                }
            }

            var result = await read.WaitAsync(ct);
            if (testCase == CommissionCase.Current)
            {
                Assert.False(server.IsCompleted);
                var pair = Assert.IsType<CheckedEnrolledPair>(result);
                Assert.Equal(candidate.Expected.Head, pair.Head);
                Assert.Equal(candidate.Expected.Devices.ToArray(), pair.Devices.ToArray());
                Assert.Equal(candidate.Expected.Members.ToArray(), pair.Members.ToArray());
                Assert.Equal(1, Volatile.Read(ref parses));
            }
            else
            {
                Assert.Null(result);
                Assert.Equal(0, Volatile.Read(ref parses));
            }
            var expectedReads = testCase is CommissionCase.Current or CommissionCase.ReboundOld or CommissionCase.MixedContent
                or CommissionCase.ApprovalOnly or CommissionCase.StalledHead ? 1 : 0;
            Assert.Equal(expectedReads, head.Reads);
            Assert.Equal(0, clock.ActiveTimers);
            if (testCase == CommissionCase.LateReply)
                Assert.False((await barriers.PendingPayload.Task.WaitAsync(ct)).IsCancellationRequested);
            if (testCase == CommissionCase.StalledHead)
            {
                Assert.False(head.Data.Task.IsCompleted);
                Assert.True(Assert.IsAssignableFrom<Task>(head.Operation).IsCanceled);
                head.Data.SetResult(decoded);
                Assert.Null(await read);
                Assert.Equal(0, Volatile.Read(ref parses));
            }
            if (testCase is not (CommissionCase.WrongPeer or CommissionCase.PartialFrame))
            {
                barriers.InspectClientEnd.TrySetResult();
                await barriers.ClientEnd.Task.WaitAsync(ct);
            }
        }
        finally
        {
            barriers.ReleasePayload.TrySetResult();
            barriers.InspectClientEnd.TrySetResult();
            barriers.ReleasePeer.TrySetResult();
            await server.WaitAsync(ct);
        }
        if (testCase == CommissionCase.WrongPeer)
            Assert.False(barriers.Request.Task.IsCompleted);
        Assert.False(listener.Poll(TimeSpan.Zero, SelectMode.SelectRead));
    }

    private static async Task ServeCommissionFixtureAsync(Socket listener, X509Certificate2 certificate,
        CommissionRequest request, SupervisedLaunchCommission commission, CommissionCase testCase,
        CommissionExchangeBarriers barriers, CancellationToken ct)
    {
        using var socket = await listener.AcceptAsync(ct);
        using var network = new NetworkStream(socket, ownsSocket: false);
        using var tls = new SslStream(network, leaveInnerStreamOpen: true);
        var reply = new CommissionReply(request.Version, request.Slot, request.Challenge, commission);
        if (testCase == CommissionCase.WrongChallenge) reply = reply with { Challenge = new string('C', 64) };
        if (testCase == CommissionCase.WrongSlot) reply = reply with { Slot = "wrong-slot" };
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct);
            barriers.Request.SetResult(await CommissionCodec.ReadRequestAsync(tls, ct));
        }
        catch (Exception error) when (testCase == CommissionCase.WrongPeer && error is AuthenticationException or IOException)
        {
            return;
        }

        if (testCase is CommissionCase.PartialFrame or CommissionCase.LateReply or CommissionCase.StalledHead)
        {
            using var frame = new MemoryStream();
            await CommissionCodec.WriteReplyAsync(frame, reply, ct);
            var bytes = frame.ToArray();
            var cut = sizeof(int) + (testCase == CommissionCase.PartialFrame ? CommissionExchangeBarriers.PayloadPrefix : 0);
            await tls.WriteAsync(bytes.AsMemory(0, cut), ct);
            await barriers.ReleasePayload.Task.WaitAsync(ct);
            if (testCase == CommissionCase.PartialFrame) return;
            await tls.WriteAsync(bytes.AsMemory(cut), ct);
        }
        else
        {
            await CommissionCodec.WriteReplyAsync(tls, reply, ct);
        }

        await barriers.InspectClientEnd.Task.WaitAsync(ct);
        var trailing = new byte[1];
        Assert.Equal(0, await tls.ReadAsync(trailing, ct));
        barriers.ClientEnd.SetResult();
        await barriers.ReleasePeer.Task.WaitAsync(ct);
    }

    private CommissionScratchEntry[] CaptureCommissionScratch(string observerPath, string daemonPath) =>
        Directory.EnumerateFileSystemEntries(_directory.FullName, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var name = Path.GetRelativePath(_directory.FullName, path);
                if (path == observerPath || path == daemonPath) return new CommissionScratchEntry(name, "socket", null);
                if (Directory.Exists(path)) return new CommissionScratchEntry(name, "directory", null);
                return new CommissionScratchEntry(name, "file", Convert.ToBase64String(File.ReadAllBytes(path)));
            }).ToArray();

    private sealed record CommissionScratchEntry(string Name, string Kind, string? Bytes);

    private static void Listen(Socket socket, EndPoint endpoint)
    {
        socket.Bind(endpoint);
        socket.Listen(1);
    }

    private static X509Certificate2 CommissionCertificate(RSA key)
    {
        var request = new CertificateRequest("CN=observer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddMinutes(5));
    }

    private sealed record CommissionCandidate(string Text, EnrolledPairClaim Claim, SupervisedLaunchCommission Commission,
        CommissionBootstrap Bootstrap, CheckedEnrolledPair Expected);

    private sealed class CommissionExchangeBarriers
    {
        internal const int PayloadPrefix = 8;
        internal TaskCompletionSource<CommissionRequest> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<CancellationToken> PendingPayload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleasePayload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource InspectClientEnd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ClientEnd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleasePeer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CommissionHead(SupervisedLaunchCommission current, bool stalled) : ICommissioningHeadReader
    {
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        internal TaskCompletionSource<(string Slot, CommissionReadBudget Budget, TimeSpan Remaining, CancellationToken Token)> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<CancellationToken> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<SupervisedLaunchCommission> Data { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<SupervisedLaunchCommission?>? Operation { get; private set; }

        public ValueTask<SupervisedLaunchCommission?> ReadCurrentAsync(string trustedSlot, CommissionReadBudget budget, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            var operation = stalled ? AwaitHeadAsync(ct) : Task.FromResult<SupervisedLaunchCommission?>(current);
            Operation = operation;
            Entered.SetResult((trustedSlot, budget, budget.Remaining, ct));
            return new(operation);
        }

        private async Task<SupervisedLaunchCommission?> AwaitHeadAsync(CancellationToken ct)
        {
            try { return await Data.Task.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Cancelled.SetResult(ct);
                throw;
            }
        }
    }
}
