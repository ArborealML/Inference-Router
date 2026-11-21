using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HeadReadV1Tests
{
    [Fact(Timeout = 20_000)]
    public async Task ScratchEndpointReturnsTheIndependentCurrentHeadAfterCandidateRollback()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scratch = new HeadScratch();
        using var certificate = FixtureCertificate();
        var scope = HeadReadScope.Topology("primary");
        var older = Topology(4);
        var current = Topology(6);
        var restoredCandidate = Path.Combine(scratch.Candidate, "topology");
        await File.WriteAllTextAsync(restoredCandidate, older.Topology!.Topology.Head.Digest, CancellationToken.None);
        var candidateBeforeAdvance = await File.ReadAllBytesAsync(restoredCandidate, CancellationToken.None);
        var predecessor = await scratch.AppendAsync(scope, 0, null, older);
        AssertHeadValue(older, await scratch.Open(scope).ReadAsync(scope, CancellationToken.None));
        var next = await scratch.StageRecordAsync(scope, 1, predecessor, current);
        Assert.Equal(HeadReadValue.Unavailable, await scratch.Open(scope).ReadAsync(scope, CancellationToken.None));
        await scratch.InstallFloorAsync(scope, 1, next);
        await File.WriteAllTextAsync(restoredCandidate, current.Topology!.Topology.Head.Digest, CancellationToken.None);

        // This untrusted restore is separate from both authority files.
        await File.WriteAllBytesAsync(restoredCandidate, candidateBeforeAdvance, CancellationToken.None);
        var reopened = scratch.Open(scope);
        AssertHeadValue(current, await scratch.ExchangeAsync(certificate, scope, scope, reopened.ReadAsync));
        var journal = HeadReadScope.ForJournal(JournalScope(scratch));
        Assert.Equal(HeadReadValue.Unavailable,
            await scratch.ExchangeAsync(certificate, journal, scope, reopened.ReadAsync));
    }

    [Fact(Timeout = 20_000)]
    public async Task ContinuityBindsTheWrittenPayloadEvenWhenJsonValuesAreEquivalent()
    {
        using var scratch = new HeadScratch();
        var scope = HeadReadScope.Topology("primary");
        var first = new HeadReadFrame(scope, 0, null, Topology(4));
        var rawDigest = await scratch.StageRecordAsync(scope, 0, null, first.Value, indented: true);
        var compactDigest = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(first, HeadReadJsonContext.Default.HeadReadFrame)));
        Assert.NotEqual(compactDigest, rawDigest);

        var store = scratch.Open(scope);
        await scratch.InstallFloorAsync(scope, 0, rawDigest);
        AssertHeadValue(first.Value, await store.ReadAsync(scope, CancellationToken.None));
        await scratch.InstallFloorAsync(scope, 0, compactDigest);
        Assert.Equal(HeadReadValue.Unavailable, await store.ReadAsync(scope, CancellationToken.None));

        await scratch.InstallFloorAsync(scope, 0, rawDigest);
        var current = Topology(5);
        await scratch.AppendAsync(scope, 1, rawDigest, current);
        AssertHeadValue(current, await store.ReadAsync(scope, CancellationToken.None));

        // A whitespace-only change leaves the decoded record valid but breaks the successor's raw digest link.
        var chain = await File.ReadAllBytesAsync(scratch.Records, CancellationToken.None);
        var length = BinaryPrimitives.ReadInt32BigEndian(chain);
        var whitespace = Array.IndexOf(chain, (byte)'\n', sizeof(int), length);
        Assert.InRange(whitespace, sizeof(int), sizeof(int) + length - 1);
        chain[whitespace] = (byte)' ';
        var changed = Assert.IsType<HeadReadFrame>(JsonSerializer.Deserialize(
            chain.AsSpan(sizeof(int), length), HeadReadJsonContext.Default.HeadReadFrame));
        Assert.Equal(first.Scope, changed.Scope);
        Assert.Equal(first.Revision, changed.Revision);
        Assert.Equal(first.Predecessor, changed.Predecessor);
        AssertHeadValue(first.Value, changed.Value);
        await File.WriteAllBytesAsync(scratch.Records, chain, CancellationToken.None);
        Assert.Equal(HeadReadValue.Unavailable, await store.ReadAsync(scope, CancellationToken.None));
    }

    [Fact(Timeout = 20_000)]
    public async Task TopologyRecordThatCannotFitTheWireReplyIsUnavailable()
    {
        using var scratch = new HeadScratch();
        var scope = HeadReadScope.Topology("primary");
        var value = Topology(4);
        var original = new HeadReadFrame(scope, 0, null, value);
        var originalSize = JsonSerializer.SerializeToUtf8Bytes(original, HeadReadJsonContext.Default.HeadReadFrame).Length;
        var commission = value.Topology!;
        var originalApproval = commission.Approvals[0];
        var create = new byte[(HeadReadStore.TopologyRecordPayloadLimit - originalSize) / 4 * 3];
        Array.Fill(create, (byte)' ');
        create[0] = (byte)'{';
        create[^1] = (byte)'}';
        var approval = originalApproval with { Intent = HostInitialLaunchIntent.Freeze(originalApproval.Intent.Plan, create) };
        Assert.True(approval.Intent.IsValid());
        var nearLimit = value with { Topology = commission with { Approvals = commission.Approvals.SetItem(0, approval) } };

        // The store frame fits as written. The same snapshot with a fixed-width reply nonce does not fit.
        await scratch.AppendAsync(scope, 0, null, nearLimit);
        var reply = new HeadReadV1Reply(HeadReadV1.Operation, HeadReadV1.Version, scope, Digest(1), nearLimit);
        var encodedSize = JsonSerializer.SerializeToUtf8Bytes(reply, HeadReadJsonContext.Default.HeadReadV1Reply).Length;
        Assert.True(encodedSize > HeadReadV1.MaxReply);
        Assert.Equal(HeadReadValue.Unavailable, await scratch.Open(scope).ReadAsync(scope, CancellationToken.None));
    }

    [Theory(Timeout = 20_000)]
    [InlineData("floor_behind")]
    [InlineData("records_behind")]
    [InlineData("missing_floor")]
    [InlineData("same_revision_wrong_digest")]
    [InlineData("wrong_predecessor")]
    public async Task OpeningSplitOrReplayedContinuityCannotReturnCurrentOrAbsence(string fault)
    {
        using var scratch = new HeadScratch();
        var scope = HeadReadScope.Topology("primary");
        var predecessor = await scratch.AppendAsync(scope, 0, null, Topology(4));
        var oldRecords = await File.ReadAllBytesAsync(scratch.Records, CancellationToken.None);
        var oldFloor = await File.ReadAllBytesAsync(scratch.Floor, CancellationToken.None);
        var current = Topology(5);
        var digest = await scratch.AppendAsync(scope, 1, predecessor, current);
        AssertHeadValue(current, await scratch.Open(scope).ReadAsync(scope, CancellationToken.None));

        switch (fault)
        {
            case "floor_behind":
                await File.WriteAllBytesAsync(scratch.Floor, oldFloor, CancellationToken.None);
                break;
            case "records_behind":
                await File.WriteAllBytesAsync(scratch.Records, oldRecords, CancellationToken.None);
                break;
            case "missing_floor":
                File.Delete(scratch.Floor);
                break;
            case "same_revision_wrong_digest":
                await scratch.InstallFloorAsync(scope, 1, predecessor);
                break;
            case "wrong_predecessor":
                await File.WriteAllBytesAsync(scratch.Records, oldRecords, CancellationToken.None);
                await scratch.AppendAsync(scope, 1, digest, current);
                break;
        }

        Assert.Equal(HeadReadValue.Unavailable, await scratch.Open(scope).ReadAsync(scope, CancellationToken.None));
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordsPathReplacementInvalidatesTheHeldSnapshot(bool journal)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scratch = new HeadScratch();
        using var certificate = FixtureCertificate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var scope = journal ? HeadReadScope.ForJournal(JournalScope(scratch)) : HeadReadScope.Topology("primary");
        var original = journal ? HeadReadValue.Absent : Topology(4);
        await scratch.AppendAsync(scope, 0, null, original);
        AssertHeadValue(original, await scratch.Open(scope).ReadAsync(scope, timeout.Token));
        var floor = await File.ReadAllBytesAsync(scratch.Floor, timeout.Token);
        var replacement = Path.Combine(Path.GetDirectoryName(scratch.Records)!, "replacement");
        await File.WriteAllTextAsync(replacement, "invalid", timeout.Token);

        var originalRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new HeadReadStore(scope, scratch.Records, scratch.Floor)
        {
            AfterRecordsReadForTests = ct =>
            {
                originalRead.SetResult();
                return release.Task.WaitAsync(ct);
            },
        };
        var reading = scratch.ExchangeAsync(certificate, scope, scope, store.ReadAsync);
        try
        {
            await originalRead.Task.WaitAsync(timeout.Token);
            File.Move(replacement, scratch.Records, overwrite: true);
            Assert.Equal(floor, await File.ReadAllBytesAsync(scratch.Floor, timeout.Token));
        }
        finally
        {
            release.TrySetResult();
            await reading;
        }

        Assert.Equal(HeadReadValue.Unavailable, await reading);
    }

    [Fact(Timeout = 20_000)]
    public async Task OnlyAnchoredNeverInitializedJournalReturnsAuthenticatedAbsence()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scratch = new HeadScratch();
        using var certificate = FixtureCertificate();
        var journalScope = JournalScope(scratch);
        var scope = HeadReadScope.ForJournal(journalScope);
        var predecessor = await scratch.AppendAsync(scope, 0, null, HeadReadValue.Absent);
        Assert.Equal(HeadReadValue.Absent,
            await scratch.ExchangeAsync(certificate, scope, scope, scratch.Open(scope).ReadAsync));

        var journal = new HeadReadValue(HeadReadOutcome.Current, Journal: new(journalScope, 0, Digest(6)));
        var initialized = await scratch.AppendAsync(scope, 1, predecessor, journal);
        Assert.Equal(journal, await scratch.ExchangeAsync(certificate, scope, scope, scratch.Open(scope).ReadAsync));
        await scratch.AppendAsync(scope, 2, initialized, HeadReadValue.Absent);
        Assert.Equal(HeadReadValue.Unavailable,
            await scratch.ExchangeAsync(certificate, scope, scope, scratch.Open(scope).ReadAsync));

        File.Delete(scratch.Records);
        Assert.Equal(HeadReadValue.Unavailable,
            await scratch.ExchangeAsync(certificate, scope, scope, scratch.Open(scope).ReadAsync));
    }

    [Theory(Timeout = 20_000)]
    [InlineData("leaf")]
    [InlineData("uid")]
    [InlineData("slot")]
    public async Task WrongPeerOrScopeCannotConsultTheRecordSource(string fault)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scratch = new HeadScratch();
        using var certificate = FixtureCertificate();
        using var wrongCertificate = FixtureCertificate();
        var scope = HeadReadScope.Topology("primary");
        var queried = 0;

        ValueTask<HeadReadValue> Read(HeadReadScope _, CancellationToken __)
        {
            Interlocked.Increment(ref queried);
            return ValueTask.FromResult(Topology(4));
        }

        var requester = fault == "uid" ? EffectiveUid() ^ 1U : EffectiveUid();
        var requested = fault == "slot" ? HeadReadScope.Topology("other") : scope;
        var pinned = (fault == "leaf" ? wrongCertificate : certificate).GetCertHashString(HashAlgorithmName.SHA256);
        Assert.Equal(HeadReadValue.Unavailable,
            await scratch.ExchangeAsync(requested, pinned,
                (socket, ct) => HeadReadV1Endpoint.ServeAsync(socket, requester, certificate, scope, Read, ct)));
        Assert.Equal(0, Volatile.Read(ref queried));
    }

    [Theory(Timeout = 20_000)]
    [InlineData("valid")]
    [InlineData("nonce")]
    [InlineData("scope")]
    [InlineData("absence")]
    [InlineData("header_only")]
    [InlineData("cutoff")]
    public async Task ReplyBindingIsCheckedWithoutWaitingForEof(string change)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scratch = new HeadScratch();
        using var certificate = FixtureCertificate();
        var scope = HeadReadScope.Topology("primary");
        var head = Topology(4);
        var clock = new ReadinessDeadlineTests.ManualClock();
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Serve(Socket socket, CancellationToken ct)
        {
            using var network = new NetworkStream(socket, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct);
            var request = await BoundedJsonFrame.ReadAsync(tls,
                HeadReadJsonContext.Default.HeadReadV1Request, HeadReadV1.MaxRequest, ct);
            Assert.True(request.IsValid());
            var reply = new HeadReadV1Reply(request.Operation, request.Version,
                change == "scope" ? HeadReadScope.Topology("other") : scope,
                change == "nonce" ? (request.Nonce == Digest(7) ? Digest(8) : Digest(7)) : request.Nonce,
                change == "absence" ? HeadReadValue.Absent : head);
            if (change == "cutoff") clock.AdvanceTo(5_000, fireTimers: false);
            if (change == "header_only")
            {
                // The old wire put the header directly in Topology, without a commission snapshot.
                var payload = Encoding.UTF8.GetBytes($$$"""
                    {"Operation":"HeadReadV1","Version":1,"Scope":{"Kind":1,"Slot":"primary","Journal":null},
                     "Nonce":"{{{request.Nonce}}}","Value":{"Outcome":1,"Topology":{"Slot":"primary",
                     "Partition":"partition","Daemon":"daemon","RouterId":"router","Generation":3,
                     "Revision":4,"Digest":"{{{Digest(4)}}}"},"Journal":null}}
                    """);
                var frame = new byte[sizeof(int) + payload.Length];
                BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
                payload.AsSpan().CopyTo(frame.AsSpan(sizeof(int)));
                await tls.WriteAsync(frame, ct);
            }
            else
            {
                await BoundedJsonFrame.WriteAsync(tls, reply, HeadReadJsonContext.Default.HeadReadV1Reply, HeadReadV1.MaxReply, ct);
            }
            await close.Task.WaitAsync(ct);
        }

        var result = await scratch.ExchangeAsync(scope, certificate.GetCertHashString(HashAlgorithmName.SHA256),
            Serve, () => close.TrySetResult(), clock);
        AssertHeadValue(change == "valid" ? head : HeadReadValue.Unavailable, result);
    }

    private static HostReservationScope JournalScope(HeadScratch scratch) =>
        new(Path.Combine(scratch.Candidate, "reservation"), Guid.NewGuid(), new("partition", 3));

    private static HeadReadValue Topology(long revision)
    {
        var head = new EnrolledPairHead("primary", "partition", "daemon", "router", 3, revision, Digest(revision));
        var scope = new HostReservationScope("/fixture/reservation", new Guid("00000000-0000-0000-0000-000000000001"),
            new(head.Partition, head.Generation));
        var image = new DockerImageIdentity("sha256:" + Digest(9), "fixture@sha256:" + Digest(8), "linux/amd64");
        (EnrolledPairMemberGrant Grant, SupervisedLaunchApproval Approval) Member(string engine)
        {
            var plan = new HostCheckpointPlan(engine, "model", "tier", "config", 5000, engine, Digest(7));
            var grant = new EnrolledPairMemberGrant(new(engine, plan.Model, "tier", plan.Configuration, plan.BackendRoute),
                "/fixture/daemon.sock", new Uri($"http://{engine}.invalid/v1"), plan.Digest());
            return (grant, new(HostInitialLaunchIntent.Freeze(plan, "{}"u8), image, "v1.43"));
        }
        var first = Member("first");
        var second = Member("second");
        var authority = new EnrolledPairAuthority(head, [first.Grant, second.Grant], ["left", "right"]);
        return new(HeadReadOutcome.Current, new(scope, authority, [first.Approval, second.Approval]));
    }

    private static void AssertHeadValue(HeadReadValue expected, HeadReadValue actual)
    {
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.Journal, actual.Journal);
        if (expected.Topology is { } commission) Assert.True(commission.Matches(actual.Topology));
        else Assert.Null(actual.Topology);
    }

    private static string Digest(long value) => value.ToString("X64", System.Globalization.CultureInfo.InvariantCulture);

    private static X509Certificate2 FixtureCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=head", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddMinutes(5));
    }

    [LibraryImport("libc.so.6", EntryPoint = "geteuid")]
    internal static partial uint EffectiveUid();

    internal sealed class HeadScratch : IDisposable
    {
        private static readonly HeadReadJsonContext Indented = new(new JsonSerializerOptions { WriteIndented = true });
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("airhead-");

        internal HeadScratch()
        {
            Candidate = Directory.CreateDirectory(Path.Combine(_root.FullName, "air")).FullName;
            Records = Path.Combine(Directory.CreateDirectory(Path.Combine(_root.FullName, "authority")).FullName, "records");
            Floor = Path.Combine(Directory.CreateDirectory(Path.Combine(_root.FullName, "continuity")).FullName, "floor");
        }

        internal string Root => _root.FullName;
        internal string Candidate { get; }
        internal string Records { get; }
        internal string Floor { get; }

        internal HeadReadStore Open(HeadReadScope scope) => new(scope, Records, Floor);

        internal async Task<string> AppendAsync(HeadReadScope scope, int revision, string? predecessor, HeadReadValue value)
        {
            var digest = await StageRecordAsync(scope, revision, predecessor, value);
            await InstallFloorAsync(scope, revision, digest);
            return digest;
        }

        internal async Task<string> StageRecordAsync(HeadReadScope scope, int revision, string? predecessor, HeadReadValue value,
            bool indented = false)
        {
            var frame = new HeadReadFrame(scope, revision, predecessor, value);
            var context = indented ? Indented : HeadReadJsonContext.Default;
            using var serialized = new MemoryStream();
            var fixtureLimit = scope.Kind == HeadReadKind.Topology ? HeadReadV1.MaxReply : HeadReadStore.RecordPayloadLimit;
            await BoundedJsonFrame.WriteAsync(serialized, frame, context.HeadReadFrame, fixtureLimit, CancellationToken.None);
            var bytes = serialized.ToArray();
            await using var stream = new FileStream(Records, FileMode.Append, FileAccess.Write);
            await stream.WriteAsync(bytes, CancellationToken.None);
            stream.Flush(flushToDisk: true);
            return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(sizeof(int))));
        }

        internal async Task InstallFloorAsync(HeadReadScope scope, int revision, string digest)
        {
            await using var stream = new FileStream(Floor, FileMode.Create, FileAccess.Write);
            await BoundedJsonFrame.WriteAsync(stream, new HeadReadFloor(scope, revision, digest),
                HeadReadJsonContext.Default.HeadReadFloor, HeadReadStore.RecordPayloadLimit, CancellationToken.None);
            stream.Flush(flushToDisk: true);
        }

        internal Task<HeadReadValue> ExchangeAsync(X509Certificate2 certificate, HeadReadScope requested,
            HeadReadScope allowed, Func<HeadReadScope, CancellationToken, ValueTask<HeadReadValue>> read) =>
            ExchangeAsync(requested, certificate.GetCertHashString(HashAlgorithmName.SHA256),
                (socket, ct) => HeadReadV1Endpoint.ServeAsync(socket, EffectiveUid(), certificate, allowed, read, ct));

        internal async Task<HeadReadValue> ExchangeAsync(HeadReadScope requested, string pin,
            Func<Socket, CancellationToken, Task> serve, Action? afterRead = null, TimeProvider? time = null)
        {
            using var lifetime = new CancellationTokenSource(HeadReadV1.Timeout);
            var path = Path.Combine(_root.FullName, "head");
            var endpoint = new UnixDomainSocketEndPoint(path);
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(endpoint);
            listener.Listen(1);
            var serving = AcceptAsync();
            try
            {
                var clock = time ?? TimeProvider.System;
                return await HeadReadV1Endpoint.ReadAsync(endpoint, pin, requested, clock, clock.GetTimestamp(),
                    HeadReadV1.Timeout, lifetime.Token);
            }
            finally
            {
                afterRead?.Invoke();
                await serving;
                File.Delete(path);
            }

            async Task AcceptAsync()
            {
                using var accepted = await listener.AcceptAsync(lifetime.Token);
                await serve(accepted, lifetime.Token);
            }
        }

        public void Dispose() => _root.Delete(recursive: true);
    }
}
