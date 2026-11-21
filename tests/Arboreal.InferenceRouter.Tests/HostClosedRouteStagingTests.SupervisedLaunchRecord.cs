using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Tomlyn.Model;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    private const string SupervisedSocket = "/fixture/supervised.sock";
    private const string SupervisedApiVersion = "v1.43";

    [Theory(Timeout = 30_000)]
    [InlineData(DeepSeekEngineName)]
    [InlineData(QwenEngineName)]
    public async Task SupervisedLaunchRecordProducesAndReopensOneClosedReceipt(string engine)
    {
        var fx = ProvisionSupervisedLaunch(engine);
        var loseAcknowledgment = engine == QwenEngineName;
        fx.Head.LoseHeadReadAtRevision = loseAcknowledgment ? 5 : null;
        var first = await fx.Open().RunAsync(engine, fx.Text, fx.Claim, CancellationToken.None);
        var reopened = fx.Open();
        if (loseAcknowledgment)
        {
            Assert.Null(first);
            Assert.Equal(HostMutationPhase.Dispatching, HostReservation.ReadEnrolled(fx.Scope).Record.State.Phase);
            first = await reopened.RunAsync(engine, fx.Text, fx.Claim, CancellationToken.None);
        }
        var receipt = Assert.IsType<HostAppliedIngressClosed>(first).Receipt;
        var completed = fx.ReadJournal();
        Assert.Equal(5, completed.Head.Revision);
        var dispatch = Assert.IsType<HostInitialDispatch>(completed.Frame.History.Dispatch);
        Assert.Equal(ReceiptInstance, completed.Frame.StartIssue?.InstanceId);
        Assert.Equal(new HostInitialReceipt(fx.Scope, dispatch.Operation, fx.Approval.Intent, ReceiptInstance), receipt);
        Assert.Equal(dispatch.Operation, HostReservation.ReadEnrolled(fx.Scope).Record.State.Pending);
        Assert.Equal(HostMutationPhase.LaunchApplied, HostReservation.ReadEnrolled(fx.Scope).Record.State.Phase);
        Assert.Equal(1, fx.Port.Creates);
        Assert.Equal(1, fx.Port.Starts);

        var original = Assert.IsType<byte[]>(reopened.ReadReceipt(receipt.Operation, receipt.InstanceId));
        var applied = Assert.IsType<HostInitialApplied>(fx.ReadJournal().Frame.History.Applied);
        Assert.Equal(Convert.FromBase64String(applied.ReceiptBytes), original);
        Assert.Null(reopened.ReadReceipt(receipt.Operation, new string('F', 64)));
        Assert.Equal(new HostAppliedIngressClosed(receipt), await reopened.RunAsync(engine, fx.Text, fx.Claim,
            CancellationToken.None));
        Assert.Null(await reopened.RunAsync(CompetingMember(engine), fx.Text, fx.Claim, CancellationToken.None));
        await AssertReceiptIngressClosedAsync(fx.Staging, fx.Scope, receipt);

        using (fx.Journal.EnterFence())
        {
            if (engine == DeepSeekEngineName) fx.DriftTopologyAndApproval();
            else fx.Daemon.ForgetRunning();
        }
        var reservation = File.ReadAllBytes(fx.Scope.Slot);
        var journal = File.ReadAllBytes(fx.Journal.JournalPath);
        var head = fx.Head.Current;
        var topology = fx.Observer.Commission;
        var afterChange = fx.Open();
        Assert.Equal(original, afterChange.ReadReceipt(receipt.Operation, receipt.InstanceId));
        Assert.Null(await afterChange.RunAsync(engine, fx.Text, fx.Claim, CancellationToken.None));
        Assert.Equal(reservation, File.ReadAllBytes(fx.Scope.Slot));
        Assert.Equal(journal, File.ReadAllBytes(fx.Journal.JournalPath));
        Assert.Equal(head, fx.Head.Current);
        Assert.Equal(topology, fx.Observer.Commission);
        await AssertReceiptIngressClosedAsync(fx.Staging, fx.Scope, receipt);

        Assert.Equal(1, fx.Port.Creates);
        Assert.Equal(1, fx.Port.Starts);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("image")]
    [InlineData("socket")]
    [InlineData("api")]
    public async Task SupervisedLaunchRecordRequiresThePinnedImageAtTheCommissionedSocket(string wrong)
    {
        var fx = ProvisionSupervisedLaunch(DeepSeekEngineName, "inventory-" + wrong);
        var installed = File.ReadAllBytes(fx.Scope.Slot);
        var journal = File.ReadAllBytes(fx.Journal.JournalPath);
        var head = fx.Head.Current;

        Assert.Null(await fx.Open().RunAsync(DeepSeekEngineName, fx.Text, fx.Claim, CancellationToken.None));

        var state = HostReservation.ReadEnrolled(fx.Scope).Record.State;
        Assert.Equal(0, state.Epoch);
        Assert.Null(state.Pending);
        Assert.Equal(installed, File.ReadAllBytes(fx.Scope.Slot));
        Assert.Equal(journal, File.ReadAllBytes(fx.Journal.JournalPath));
        Assert.Equal(head, fx.Head.Current);
        Assert.Equal(0, fx.Port.Creates);
        Assert.Equal(0, fx.Port.Starts);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("effective", 0)]
    [InlineData("wire", 0)]
    [InlineData("running-id", 1)]
    [InlineData("observed-image", 0)]
    [InlineData("observed-socket", 0)]
    [InlineData("observed-create-api", 0)]
    [InlineData("observed-start-api", 1)]
    [InlineData("observed-route", 0)]
    [InlineData("observed-endpoint", 0)]
    [InlineData("observed-devices", 0)]
    [InlineData("missing-id", 0)]
    [InlineData("missing-settings", 0)]
    public async Task SupervisedLaunchRecordRequiresIndependentDaemonEvidence(string wrong, int starts)
    {
        var fx = ProvisionSupervisedLaunch(DeepSeekEngineName, wrong);
        Assert.Null(await fx.Open().RunAsync(DeepSeekEngineName, fx.Text, fx.Claim, CancellationToken.None));
        var history = fx.ReadJournal().Frame.History;
        var dispatch = Assert.IsType<HostInitialDispatch>(history.Issued);
        Assert.Equal(dispatch, history.Accepted);
        Assert.Null(history.Applied);
        await AssertSupervisedLaunchStillFencedAsync(fx, dispatch, expectedCreates: 1, expectedStarts: starts);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("owner")]
    [InlineData("head-revision")]
    [InlineData("competing-member-socket")]
    [InlineData("api")]
    public async Task SupervisedLaunchRecordRechecksCommissionAndPolicyAfterAcceptance(string changed)
    {
        var fx = ProvisionSupervisedLaunch(DeepSeekEngineName);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = fx.Open(ct =>
        {
            accepted.TrySetResult();
            return new ValueTask(continuation.Task.WaitAsync(ct));
        });
        var attempt = entry.RunAsync(DeepSeekEngineName, fx.Text, fx.Claim, CancellationToken.None).AsTask();
        HostInitialDispatch dispatch;
        try
        {
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var fence = fx.Journal.EnterFence();
            var history = Assert.IsType<SupervisedLaunchSnapshot>(fx.Journal.Read()).Frame.History;
            dispatch = Assert.IsType<HostInitialDispatch>(history.Accepted);
            Assert.Null(history.Issued);
            var before = fx.Observer.Commission;
            var policy = fx.Observer.Policy;
            if (changed == "owner") fx.Observer.Policy = policy with { OwnerGeneration = policy.OwnerGeneration + 1 };
            else if (changed == "api")
                fx.Observer.Commission = before with
                {
                    Approvals = before.Approvals.Replace(fx.Approval, fx.Approval with { ApiVersion = "v1.44" }),
                };
            else
            {
                var topology = before.Topology;
                if (changed == "head-revision")
                    topology = topology with { Head = topology.Head with { Revision = topology.Head.Revision + 1 } };
                else
                {
                    var competitor = Assert.Single(topology.Members, grant => grant.Member.Engine == QwenEngineName);
                    topology = topology with { Members = topology.Members.Replace(competitor,
                        competitor with { SocketPath = "/fixture/rebound.sock" }) };
                }
                fx.Observer.Commission = before with { Topology = topology };
                Assert.Equal(before.Topology.Head.Generation, topology.Head.Generation);
                Assert.Equal(policy, fx.Observer.Policy);
            }
        }
        finally { continuation.TrySetResult(); }

        Assert.Null(await attempt);
        Assert.Null(fx.ReadJournal().Frame.History.Issued);
        await AssertSupervisedLaunchStillFencedAsync(fx, dispatch, expectedCreates: 0, expectedStarts: 0);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupervisedLaunchRecordQuarantinesRefusedHeadAdvanceAndWrongPredecessor(bool wrongPredecessor)
    {
        var fx = ProvisionSupervisedLaunch(DeepSeekEngineName);
        fx.Head.RefuseRevision = 3;
        SupervisedLaunchSnapshot? accepted = null;
        byte[]? prefix = null;
        var entry = fx.Open(_ =>
        {
            using var fence = fx.Journal.EnterFence();
            accepted = fx.Journal.Read();
            prefix = File.ReadAllBytes(fx.Journal.JournalPath);
            return ValueTask.CompletedTask;
        });

        Assert.Null(await entry.RunAsync(DeepSeekEngineName, fx.Text, fx.Claim, CancellationToken.None));
        var original = Assert.IsType<SupervisedLaunchSnapshot>(accepted);
        var dispatch = Assert.IsType<HostInitialDispatch>(original.Frame.History.Accepted);
        Assert.Equal(2, original.Head.Revision);
        Assert.Equal(original.Head, fx.Head.Current);
        var priorBytes = Assert.IsType<byte[]>(prefix);
        var bytes = File.ReadAllBytes(fx.Journal.JournalPath);
        Assert.Equal(priorBytes, bytes[..priorBytes.Length]);
        var tail = ReadSupervisedFrame(bytes, priorBytes.Length);
        Assert.Equal(original.Head.Digest, tail.Predecessor);
        Assert.Equal(original.Frame.History with { Issued = dispatch }, tail.History);

        if (wrongPredecessor)
        {
            // Install a matching head for the complete final bytes. Only ancestry is wrong.
            using var fence = fx.Journal.EnterFence();
            var changed = tail with { Predecessor = new string('F', 64) };
            Assert.NotEqual(tail.Predecessor, changed.Predecessor);
            var frame = JsonSerializer.SerializeToUtf8Bytes(changed, SupervisedLaunchJsonContext.Default.SupervisedLaunchFrame);
            using (var journal = new FileStream(fx.Journal.JournalPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                journal.SetLength(priorBytes.Length);
                journal.Position = priorBytes.Length;
                var length = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(length, frame.Length);
                journal.Write(length);
                journal.Write(frame);
                journal.Flush(flushToDisk: true);
            }
            fx.Head.RefuseRevision = null;
            var matchingHead = new SupervisedLaunchJournalHead(fx.Scope, 3, Convert.ToHexString(SHA256.HashData(frame)));
            Assert.True(fx.Head.TryAdvance(original.Head, matchingHead));
            Assert.Equal(matchingHead, fx.Head.Current);
        }

        using (fx.Journal.EnterFence()) Assert.Null(fx.Journal.Read());
        await AssertSupervisedLaunchStillFencedAsync(fx, dispatch, expectedCreates: 0, expectedStarts: 0);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("lost start acknowledgment", 4, 1)]
    [InlineData("refused start head", 3, 0)]
    [InlineData("unreadable start head", 4, 0)]
    public async Task SupervisedLaunchRecordHoldsTheExactPreStartId(string fault, int revision, int starts)
    {
        var fx = ProvisionSupervisedLaunch(DeepSeekEngineName);
        if (fault == "lost start acknowledgment") fx.Port.LoseStartAcknowledgment = true;
        if (fault == "refused start head") fx.Head.RefuseRevision = 4;
        if (fault == "unreadable start head") fx.Head.LoseHeadReadAtRevision = 4;

        Assert.Null(await fx.Open().RunAsync(DeepSeekEngineName, fx.Text, fx.Claim, CancellationToken.None));
        var frames = ReadSupervisedFrames(fx.Journal.JournalPath);
        Assert.Equal(5, frames.Count);
        var tail = frames[^1];
        var dispatch = Assert.IsType<HostInitialDispatch>(tail.History.Issued);
        var issue = Assert.IsType<SupervisedStartIssue>(tail.StartIssue);
        Assert.Equal(new SupervisedStartIssue(fx.Observer.Commission.Command(dispatch), ReceiptInstance,
            fx.Approval.Intent.CreateDigest), issue);
        Assert.Equal(fx.Approval.Intent, dispatch.Intent);
        Assert.Null(tail.History.Applied);
        Assert.Equal(revision, fx.Head.Current?.Revision);
        if (starts == 1)
        {
            var observed = Assert.IsType<SupervisedObservedStart>(fx.Daemon.Running);
            Assert.Equal(new SupervisedObservedStart(issue.Command, issue.InstanceId, Running: true), observed);
        }
        else
            Assert.Null(fx.Daemon.Running);

        await AssertSupervisedLaunchStillFencedAsync(fx, dispatch, expectedCreates: 1, expectedStarts: starts);
        using var fence = fx.Journal.EnterFence();
        var anchored = fx.Journal.Read();
        if (revision == 3) Assert.Null(anchored);
        else Assert.Equal(issue, Assert.IsType<SupervisedLaunchSnapshot>(anchored).Frame.StartIssue);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("legacy applied")]
    [InlineData("legacy issued")]
    [InlineData("applied at revision 4")]
    [InlineData("wrong applied ID")]
    [InlineData("wrong receipt bytes")]
    public async Task SupervisedLaunchRecordDiscriminatesLegacyAndNewJournalShapes(string shape)
    {
        var fx = ProvisionSupervisedLaunch(DeepSeekEngineName);
        // Simulate loss of the coordinator-facing reply for held cases. The fixture
        // will reseed the journal below while the local reservation remains Dispatching.
        var legacyApplied = shape == "legacy applied";
        fx.Head.LoseHeadReadAtRevision = legacyApplied ? null : 5;
        var first = await fx.Open().RunAsync(DeepSeekEngineName, fx.Text, fx.Claim, CancellationToken.None);
        if (legacyApplied) Assert.IsType<HostAppliedIngressClosed>(first);
        else Assert.Null(first);

        var frames = ReadSupervisedFrames(fx.Journal.JournalPath);
        Assert.Equal(6, frames.Count);
        var applied = Assert.IsType<HostInitialApplied>(frames[^1].History.Applied);
        var dispatch = Assert.IsType<HostInitialDispatch>(frames[3].History.Issued);
        if (shape is "legacy applied" or "legacy issued")
        {
            frames.RemoveAt(4);
            if (!legacyApplied) frames.RemoveAt(4);
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                var commission = frame.Commission;
                frames[i] = frame with
                {
                    FormatVersion = 0,
                    StartIssue = null,
                    Commission = commission is null ? null : commission with
                    {
                        Approvals = [.. commission.Approvals.Select(member => member with { ApiVersion = null })],
                    },
                };
            }
        }
        else if (shape == "applied at revision 4")
        {
            frames.RemoveAt(4);
            frames[4] = frames[4] with { StartIssue = null };
        }
        else
        {
            var mismatchedReceipt = applied.Receipt with { InstanceId = new string('F', 64) };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(mismatchedReceipt, HostReservationJsonContext.Default.HostInitialReceipt);
            var changed = applied with { ReceiptBytes = Convert.ToBase64String(bytes) };
            if (shape == "wrong applied ID") changed = changed with { Receipt = mismatchedReceipt };
            frames[5] = frames[5] with { History = frames[5].History with { Applied = changed } };
        }

        fx = RewriteSupervisedFixtureJournal(fx, frames);
        if (legacyApplied)
        {
            Assert.Equal(4, fx.ReadJournal().Head.Revision);
            Assert.Null(fx.ReadJournal().Frame.StartIssue);
            Assert.Equal(Convert.FromBase64String(applied.ReceiptBytes),
                fx.Open().ReadReceipt(dispatch.Operation, ReceiptInstance));
            Assert.Null(await fx.Open().RunAsync(DeepSeekEngineName, fx.Text, fx.Claim, CancellationToken.None));
            Assert.Equal(1, fx.Port.Creates);
            Assert.Equal(1, fx.Port.Starts);
            await AssertReceiptIngressClosedAsync(fx.Staging, fx.Scope, applied.Receipt);
            return;
        }

        using (fx.Journal.EnterFence())
        {
            var installed = fx.Journal.Read();
            if (shape == "legacy issued")
            {
                var legacy = Assert.IsType<SupervisedLaunchSnapshot>(installed);
                Assert.Equal(3, legacy.Head.Revision);
                Assert.Null(legacy.Frame.StartIssue);
            }
            else
                Assert.Null(installed);
        }
        await AssertSupervisedLaunchStillFencedAsync(fx, dispatch, expectedCreates: 1, expectedStarts: 1);
    }

    private static List<SupervisedLaunchFrame> ReadSupervisedFrames(string path)
    {
        var result = new List<SupervisedLaunchFrame>();
        var bytes = File.ReadAllBytes(path);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
            offset += sizeof(int);
            result.Add(Assert.IsType<SupervisedLaunchFrame>(JsonSerializer.Deserialize(bytes.AsSpan(offset, length),
                SupervisedLaunchJsonContext.Default.SupervisedLaunchFrame)));
            offset += length;
        }
        return result;
    }

    private static SupervisedLaunchFixture RewriteSupervisedFixtureJournal(SupervisedLaunchFixture fixture,
        List<SupervisedLaunchFrame> frames)
    {
        var head = new SupervisedFixtureHead();
        using var fence = fixture.Journal.EnterFence();
        using var journal = new FileStream(fixture.Journal.JournalPath, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> length = stackalloc byte[sizeof(int)];
        for (var revision = 0; revision < frames.Count; revision++)
        {
            var prior = head.Current;
            var frame = frames[revision] with { Predecessor = prior?.Digest };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, SupervisedLaunchJsonContext.Default.SupervisedLaunchFrame);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            journal.Write(length);
            journal.Write(bytes);
            Assert.True(head.TryAdvance(prior, new(fixture.Scope, revision, Convert.ToHexString(SHA256.HashData(bytes)))));
        }
        journal.Flush(flushToDisk: true);
        return fixture with { Head = head, Journal = new(fixture.Scope, head) };
    }

    private static SupervisedLaunchFrame ReadSupervisedFrame(byte[] journal, int offset)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(journal.AsSpan(offset, sizeof(int)));
        Assert.Equal(offset + sizeof(int) + length, journal.Length);
        return Assert.IsType<SupervisedLaunchFrame>(JsonSerializer.Deserialize(journal.AsSpan(offset + sizeof(int), length),
            SupervisedLaunchJsonContext.Default.SupervisedLaunchFrame));
    }

    private static async Task AssertSupervisedLaunchStillFencedAsync(SupervisedLaunchFixture fx, HostInitialDispatch dispatch,
        int expectedCreates, int expectedStarts)
    {
        var installed = HostReservation.ReadEnrolled(fx.Scope);
        Assert.Equal(HostMutationPhase.Dispatching, installed.Record.State.Phase);
        Assert.Equal(1, installed.Record.State.Epoch);
        Assert.Equal(dispatch.Operation, installed.Record.State.Pending);
        Assert.Null(installed.Record.State.LaunchedContainerId);
        var journal = File.ReadAllBytes(fx.Journal.JournalPath);
        var head = fx.Head.Current;
        var reopened = fx.Open();
        Assert.Null(await reopened.RunAsync(DeepSeekEngineName, fx.Text, fx.Claim, CancellationToken.None));
        Assert.Null(await reopened.RunAsync(QwenEngineName, fx.Text, fx.Claim, CancellationToken.None));
        Assert.Null(reopened.ReadReceipt(dispatch.Operation, ReceiptInstance));
        Assert.Equal(installed, HostReservation.ReadEnrolled(fx.Scope));
        Assert.Equal(journal, File.ReadAllBytes(fx.Journal.JournalPath));
        Assert.Equal(head, fx.Head.Current);
        Assert.Equal(expectedCreates, fx.Port.Creates);
        Assert.Equal(expectedStarts, fx.Port.Starts);
        await AssertReceiptIngressClosedAsync(fx.Staging, fx.Scope, new(fx.Scope, dispatch.Operation, dispatch.Intent, ReceiptInstance));
    }

    private SupervisedLaunchFixture ProvisionSupervisedLaunch(string engine, string? wrong = null)
    {
        var (text, claim, authority) = EnrolledPairTopologyFixture(SupervisedSocket);
        var scope = HostReservation.ProvisionEnrolled(ReservationPath, new(authority.Head.Partition, authority.Head.Generation));
        var pair = RouterConfigLoader.PrepareEnrolledPair(text, authority.Head.Slot, claim, _ => authority);
        var commission = new SupervisedLaunchCommission(scope, authority, FixtureCommissionApprovals(pair));
        var approval = Assert.IsType<SupervisedLaunchApproval>(commission.Approval(engine));
        var daemon = SeedSupervisedDaemon(scope, engine, wrong);
        var observer = new SupervisedFixtureObserver(commission, daemon);
        var head = new SupervisedFixtureHead();
        var journal = new SupervisedLaunchJournal(scope, head);
        var port = new SupervisedFixturePort(journal, daemon, Convert.FromBase64String(approval.Intent.CreateBytes));
        var fixture = new SupervisedLaunchFixture(scope, text, claim, approval, head, journal, observer, daemon, port, ReceiptStaging());
        fixture.Open().ProvisionJournal();
        return fixture;
    }

    private static ImmutableArray<SupervisedLaunchApproval> FixtureCommissionApprovals(CheckedEnrolledPair pair)
    {
        using var stream = OpenPairResource("selector-reference.json");
        using var oracle = JsonDocument.Parse(stream);
        var approvals = ImmutableArray.CreateBuilder<SupervisedLaunchApproval>();
        foreach (var member in pair.Members)
        {
            var recipe = Assert.Single(oracle.RootElement.EnumerateArray(), row =>
                row.GetProperty("engine").GetString() == member.Plan.Engine &&
                row.GetProperty("model").GetString() == member.Plan.Model &&
                row.GetProperty("tier").GetString() == member.Plan.Tier).GetProperty("create");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(ReadPairCreate(recipe), DockerJsonContext.Default.DockerCreateRequest);
            approvals.Add(new(HostInitialLaunchIntent.Freeze(member.Plan, bytes), member.Image, SupervisedApiVersion));
        }
        return approvals.ToImmutable();
    }

    private static SupervisedFixtureDaemon SeedSupervisedDaemon(HostReservationScope scope, string engine, string? wrong)
    {
        var deepSeek = engine == DeepSeekEngineName;
        var model = deepSeek ? DeepSeekPairModel : QwenPairModel;
        var tier = deepSeek ? "32k" : "256k-4";
        using var stream = OpenPairResource("selector-reference.json");
        using var oracle = JsonDocument.Parse(stream);
        var recipe = Assert.Single(oracle.RootElement.EnumerateArray(), row =>
            row.GetProperty("engine").GetString() == engine && row.GetProperty("model").GetString() == model &&
            row.GetProperty("tier").GetString() == tier).GetProperty("create");
        var wire = JsonSerializer.SerializeToUtf8Bytes(ReadPairCreate(recipe), DockerJsonContext.Default.DockerCreateRequest);
        var effective = ReadPairCreate(recipe);
        var limits = Assert.IsType<DockerHostConfig>(effective.HostConfig);
        limits.NetworkMode = "bridge";
        limits.Init = false;
        if (wrong == "effective") limits.Memory = Assert.IsType<long>(limits.Memory) - 1024;

        // Daemon state is seeded before issuing the operation. It has its own endpoint, image and
        // device inventory and never reads the commission, the worker's bytes or the worker reply.
        var head = new EnrolledPairHead("fixture-slot", "fixture-partition", "fixture-daemon", "selector-fixture",
            Generation: 4, Revision: 8, Digest: new string('A', 64));
        var imageName = deepSeek ? "maya" : "strata";
        var image = new DockerImageIdentity(effective.Image,
            $"registry.invalid/selector-fixture/{imageName}@sha256:{new string(deepSeek ? '6' : '4', 64)}", "linux/amd64");
        var wrongImage = new DockerImageIdentity(image.Id,
            $"registry.invalid/selector-fixture/{imageName}@sha256:{new string('8', 64)}", image.Platform);
        var inventory = new SupervisedImageAtSocket(head.Daemon, wrong == "inventory-socket" ? "/fixture/other.sock" : SupervisedSocket,
            wrong == "inventory-api" ? "v1.44" : SupervisedApiVersion, image.Id, wrong == "inventory-image" ? wrongImage : image);
        var create = new SupervisedDaemonCreateSeed(
            wrong == "observed-socket" ? "/fixture/other.sock" : SupervisedSocket,
            wrong == "observed-create-api" ? "v1.44" : SupervisedApiVersion,
            wrong == "missing-id" ? null : ReceiptInstance,
            wrong == "wire" ? [.. wire, (byte)' '] : wire,
            wrong == "observed-image" ? wrongImage : image,
            wrong == "missing-settings" ? null : effective,
            wrong == "observed-route" ? "foreign-route" : engine + "-fixture-route",
            new Uri(wrong == "observed-endpoint" ? "http://127.0.0.1:28099/v1" : deepSeek
                ? "http://127.0.0.1:28060/v1" : "http://127.0.0.1:28021/v1"),
            wrong == "observed-devices" ? ["fixture-device-a", "foreign-device"] : ["fixture-device-a", "fixture-device-b"]);
        return new(scope, head, inventory, create, wrong == "running-id" ? new string('F', 64) : ReceiptInstance,
            wrong == "observed-start-api" ? "v1.44" : SupervisedApiVersion);
    }

    private static string CompetingMember(string engine) => engine == DeepSeekEngineName ? QwenEngineName : DeepSeekEngineName;

    private sealed record SupervisedLaunchFixture(HostReservationScope Scope, string Text, EnrolledPairClaim Claim,
        SupervisedLaunchApproval Approval, SupervisedFixtureHead Head, SupervisedLaunchJournal Journal,
        SupervisedFixtureObserver Observer, SupervisedFixtureDaemon Daemon, SupervisedFixturePort Port, HostClosedRouteStaging Staging)
    {
        internal SupervisedInitialLaunch Open(Func<CancellationToken, ValueTask>? afterAccept = null) =>
            new(Scope, Claim.Head.Slot, Head, Observer, Port, new ReceiptOnlyProvider(), Staging) { AfterAcceptForTests = afterAccept };

        internal SupervisedLaunchSnapshot ReadJournal()
        {
            using var fence = Journal.EnterFence();
            return Assert.IsType<SupervisedLaunchSnapshot>(Journal.Read());
        }

        internal void DriftTopologyAndApproval()
        {
            var commission = Observer.Commission;
            var prior = commission.Topology;
            var grant = prior.Members[0];
            var otherMember = grant.Member with { BackendRoute = "independent-rebound-route" };
            var otherGrant = grant with { Member = otherMember, SocketPath = "/fixture/rebound.sock" };
            var next = prior with
            {
                Head = prior.Head with { Revision = prior.Head.Revision + 1, Digest = new string('B', 64) },
                Members = prior.Members.SetItem(0, otherGrant),
            };
            var approved = Assert.IsType<SupervisedLaunchApproval>(commission.Approval(otherMember.Engine));
            var plan = approved.Intent.Plan with { BackendRoute = otherMember.BackendRoute };
            var replacement = approved with { Intent = approved.Intent with { Plan = plan } };
            next = next with { Members = next.Members.SetItem(0, otherGrant with { PlanDigest = plan.Digest() }) };
            var currentClaim = new EnrolledPairClaim(next.Head, [.. next.Members.Select(item => item.Member)], next.Devices);
            var document = ReadDeepSeekQwenDocument(Text);
            var docker = Assert.IsType<TomlTable>(DeepSeekQwenEngineTable(document, otherMember.Engine)["docker"]);
            docker["socket"] = otherGrant.SocketPath;
            var checkedCurrent = RouterConfigLoader.PrepareEnrolledPair(WriteDeepSeekQwenDocument(document),
                next.Head.Slot, currentClaim, _ => next);
            Assert.Equal(next.Head, checkedCurrent.Head);
            Observer.Commission = commission with { Topology = next, Approvals = commission.Approvals.Replace(approved, replacement) };
        }
    }

    private sealed class SupervisedFixtureHead : ISupervisedLaunchHead
    {
        internal SupervisedLaunchJournalHead? Current { get; private set; }
        internal int? LoseHeadReadAtRevision { get; set; }
        internal int? RefuseRevision { get; set; }

        public SupervisedLaunchJournalHead? Read(HostReservationScope scope)
        {
            if (Current is not { } current || current.Scope != scope) return null;
            if (current.Revision == LoseHeadReadAtRevision)
            {
                LoseHeadReadAtRevision = null;
                return null;
            }
            return current;
        }

        public bool TryAdvance(SupervisedLaunchJournalHead? predecessor, SupervisedLaunchJournalHead next)
        {
            if (Current != predecessor || next.Revision != (predecessor?.Revision ?? -1) + 1 ||
                (predecessor is not null && predecessor.Scope != next.Scope) || next.Revision == RefuseRevision) return false;
            Current = next;
            return true;
        }
    }

    private sealed class SupervisedFixtureObserver(SupervisedLaunchCommission commission, SupervisedFixtureDaemon daemon)
        : ISupervisedLaunchObserver
    {
        internal SupervisedLaunchCommission Commission { get; set; } = commission;
        internal HostInitialPolicy Policy { get; set; } = new(commission.Scope, 1, 1,
            CompletePair: true, ExclusiveWriter: true, IngressClosed: true, ForeignResident: false);

        public SupervisedLaunchCommission? ReadCommission(HostReservationScope scope) => scope == Commission.Scope ? Commission : null;
        public SupervisedLaunchPolicy? ReadPolicy(HostReservationScope scope) =>
            scope == Commission.Scope ? new(Policy, Vacant: daemon.Created is null) : null;
        public DockerImageIdentity? ReadImage(EnrolledPairHead head, string socketPath, string apiVersion, string imageId) =>
            daemon.ImageAt(head, socketPath, apiVersion, imageId);
        public SupervisedObservedCreate? ReadCreate(SupervisedLaunchCommand command) =>
            daemon.Created is { } created && created.Command.Operation == command.Operation ? created : null;
        public SupervisedObservedStart? ReadStart(SupervisedLaunchCommand command) =>
            daemon.Running is { } running && running.Command.Operation == command.Operation ? running : null;
    }

    private sealed record SupervisedImageAtSocket(string Daemon, string Socket, string ApiVersion, string RequestedId,
        DockerImageIdentity Image);

    private sealed record SupervisedDaemonCreateSeed(string Socket, string ApiVersion, string? Instance, byte[] Wire, DockerImageIdentity Image,
        DockerCreateRequest? Effective, string Route, Uri Endpoint, ImmutableArray<string> Devices);

    private sealed class SupervisedFixtureDaemon(HostReservationScope scope, EnrolledPairHead head, SupervisedImageAtSocket image,
        SupervisedDaemonCreateSeed create, string runningId, string runningApiVersion)
    {
        internal SupervisedObservedCreate? Created { get; private set; }
        internal SupervisedObservedStart? Running { get; private set; }

        internal DockerImageIdentity? ImageAt(EnrolledPairHead queried, string socket, string apiVersion, string id) =>
            queried.Daemon == image.Daemon && socket == image.Socket &&
            apiVersion == image.ApiVersion && id == image.RequestedId ? image.Image : null;

        internal void RecordCreate(HostMutation operation) => Created =
            new(new(scope, head, create.Socket, operation, create.ApiVersion), create.Instance, create.Wire,
                create.Image, create.Effective, create.Route, create.Endpoint, create.Devices);

        internal void RecordStart(HostMutation operation) =>
            Running = new(new(scope, head, SupervisedSocket, operation, runningApiVersion), runningId, Running: true);

        internal void ForgetRunning() => Running = null;
    }

    private sealed class SupervisedFixturePort(SupervisedLaunchJournal journal, SupervisedFixtureDaemon daemon,
        byte[] expectedWire) : ISupervisedLaunchPort
    {
        internal int Creates { get; private set; }
        internal int Starts { get; private set; }
        internal bool LoseStartAcknowledgment { get; set; }

        public ValueTask<string?> CreateAsync(SupervisedLaunchCommand command, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = Assert.IsType<SupervisedLaunchSnapshot>(journal.Read());
            var history = snapshot.Frame.History;
            var dispatch = Assert.IsType<HostInitialDispatch>(history.Dispatch);
            Assert.Equal(3, snapshot.Head.Revision);
            Assert.Equal(dispatch, history.Accepted);
            Assert.Equal(dispatch, history.Issued);
            Assert.Null(history.Applied);
            Assert.Equal(command.Operation, dispatch.Operation);
            var commission = Assert.IsType<SupervisedLaunchCommission>(snapshot.Frame.Commission);
            Assert.Equal(command.Head, commission.Topology.Head);
            Assert.Equal(command.SocketPath, commission.Grant(command.Operation.Engine).SocketPath);
            Assert.Equal(SupervisedApiVersion, command.ApiVersion);
            Assert.Equal(expectedWire, bytes.ToArray());
            Assert.True(dispatch.MatchesDispatching(HostReservation.ReadEnrolled(command.Scope)));
            AssertFencesHeld(command.Scope);
            Creates++;
            daemon.RecordCreate(command.Operation);
            return ValueTask.FromResult<string?>(ReceiptInstance);
        }

        public ValueTask<bool> StartAsync(SupervisedLaunchCommand command, string instance, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(ReceiptInstance, instance);
            var snapshot = Assert.IsType<SupervisedLaunchSnapshot>(journal.Read());
            var dispatch = Assert.IsType<HostInitialDispatch>(snapshot.Frame.History.Issued);
            Assert.Equal(4, snapshot.Head.Revision);
            Assert.Equal(command.Operation, dispatch.Operation);
            Assert.Equal(new SupervisedStartIssue(command, instance, dispatch.Intent.CreateDigest), snapshot.Frame.StartIssue);
            Assert.Null(snapshot.Frame.History.Applied);
            Assert.True(dispatch.MatchesDispatching(HostReservation.ReadEnrolled(command.Scope)));
            AssertFencesHeld(command.Scope);
            Starts++;
            daemon.RecordStart(command.Operation);
            if (LoseStartAcknowledgment) throw new IOException("The fake daemon lost its start acknowledgment.");
            return ValueTask.FromResult(true);
        }

        private void AssertFencesHeld(HostReservationScope scope)
        {
            Assert.Throws<IOException>(() =>
            {
                using var unexpected = HostReservation.OpenEnrolled(scope);
            });
            Assert.Throws<IOException>(() =>
            {
                using var unexpected = journal.EnterFence();
            });
        }
    }
}
