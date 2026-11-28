using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class ServingBrokerDirectSocketTests
{
    [Fact(Timeout = 20_000)]
    public async Task JoinsTheRealAcceptedSocketBeforeBytesAndRequiresTerminalWorkAfterClose()
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accepting = listener.AcceptAsync(ct);
        await client.ConnectAsync(Assert.IsType<IPEndPoint>(listener.LocalEndPoint), ct);
        using var acceptedSocket = await accepting;

        var pid = Environment.ProcessId;
        var stat = await File.ReadAllTextAsync("/proc/self/stat", ct);
        var fields = stat[(stat.AsSpan().LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lifetime = new ServingHostLifetime(pid, ulong.Parse(fields[19], CultureInfo.InvariantCulture));
        var fd = OpenPidFd(pid, 0);
        Assert.True(fd >= 0);
        using var pin = new SafeFileHandle((nint)fd, ownsHandle: true);
        var pinned = new ServingHostPinnedProcess(lifetime, pin);
        var ns = new FileInfo("/proc/self/ns/net").LinkTarget;
        Assert.NotNull(ns);
        var inode = ulong.Parse(ns.AsSpan(5, ns.Length - 6), CultureInfo.InvariantCulture);
        var binding = FixtureBinding("/fixture/direct-reservation");
        var request = new ServingHostSocketRequest(ServingBrokerIssuer.ServingIdentity(binding), Guid.NewGuid(),
            ServingBrokerUseKind.Models, null, new(0, inode), lifetime, lifetime,
            ServingHostAddress.From(Assert.IsType<IPEndPoint>(client.LocalEndPoint)),
            ServingHostAddress.From(Assert.IsType<IPEndPoint>(client.RemoteEndPoint)));

        using var listenerFd = new SafeFileHandle(listener.SafeHandle.DangerousGetHandle(), ownsHandle: false);
        var (listenerInode, listenerCookie) = LinuxServingAcceptedSocket.ReadIdentity(listenerFd) ??
            throw new InvalidOperationException("The scratch listener must expose a socket cookie and inode.");
        var exclusion = new ServingHostExclusion(request.Binding, request.Namespace, lifetime, lifetime, request.Listener,
            new(listenerFd.DangerousGetHandle().ToInt32(), request.Listener, listenerInode, listenerCookie),
            1, new string('A', 64));
        ServingHostExclusion? published = null;
        var acceptedFd = acceptedSocket.SafeHandle.DangerousGetHandle().ToInt32();
        ServingHostTerminalProof? terminal = null;
        var evidence = new LinuxServingHostSocketEvidence(pinned, pinned, exclusion, () => published,
            (_, _) => ValueTask.FromResult(terminal));
        Assert.False(evidence.CanConnect(ct));
        Assert.Null(evidence.JoinAccepted(request, ct));
        published = exclusion with { Revision = 2 };
        Assert.Null(evidence.JoinAccepted(request, ct));
        Assert.False(acceptedSocket.Poll(TimeSpan.Zero, SelectMode.SelectRead));
        published = exclusion;

        var staleLifetime = lifetime with { StartTicks = lifetime.StartTicks + 1 };
        var staleExclusion = exclusion with { Handler = staleLifetime };
        var staleEvidence = new LinuxServingHostSocketEvidence(pinned with { Identity = staleLifetime }, pinned,
            staleExclusion, () => staleExclusion, (_, _) => ValueTask.FromResult<ServingHostTerminalProof?>(null));
        Assert.False(staleEvidence.CanConnect(ct));

        if (!CanDuplicateInScratch(pin, acceptedFd))
        {
            Assert.NotEqual("1", Environment.GetEnvironmentVariable("AIR_REQUIRE_HOST_DIRECT_PROOF"));
            Assert.False(evidence.CanConnect(ct));
            Assert.Null(evidence.JoinAccepted(request, ct));
            Assert.False(acceptedSocket.Poll(TimeSpan.Zero, SelectMode.SelectRead));
            return;
        }

        Assert.True(evidence.CanConnect(ct));
        var replacedListener = exclusion with { Listener = exclusion.Listener with { Number = acceptedFd } };
        var replacementEvidence = new LinuxServingHostSocketEvidence(pinned, pinned, replacedListener,
            () => replacedListener, (_, _) => ValueTask.FromResult<ServingHostTerminalProof?>(null));
        Assert.False(replacementEvidence.CanConnect(ct));
        var accepted = Assert.IsType<ServingHostAcceptedSocket>(evidence.JoinAccepted(request, ct));
        Assert.NotEqual(0UL, accepted.AcceptedCookie);
        Assert.Equal(exclusion.Digest, accepted.ExclusionDigest);
        Assert.Equal(FormattableString.Invariant($"socket:[{accepted.AcceptedInode}]"),
            new FileInfo(FormattableString.Invariant($"/proc/self/fd/{acceptedFd}")).LinkTarget);
        Assert.False(acceptedSocket.Poll(TimeSpan.Zero, SelectMode.SelectRead));

        using var clientStream = new NetworkStream(client, ownsSocket: false);
        using var handlerStream = new NetworkStream(acceptedSocket, ownsSocket: false);
        var probe = "GET /v1/models HTTP/1.1\r\nConnection: close\r\n\r\n"u8.ToArray();
        await clientStream.WriteAsync(probe, ct);
        var captured = new byte[probe.Length];
        await handlerStream.ReadExactlyAsync(captured, ct);
        Assert.Equal(probe, captured);
        var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray();
        await handlerStream.WriteAsync(response, ct);
        var received = new byte[response.Length];
        await clientStream.ReadExactlyAsync(received, ct);
        Assert.Equal(response, received);
        acceptedSocket.Dispose();
        client.Dispose();

        published = null;
        Assert.False(evidence.IsLive(accepted));
        Assert.Null(await evidence.ConfirmTerminalAsync(accepted, ct));
        terminal = new(accepted with { AcceptedCookie = accepted.AcceptedCookie + 1 }, 1, new string('A', 64),
            ServingHostTerminalKind.WorkTerminal);
        Assert.Null(await evidence.ConfirmTerminalAsync(accepted, ct));
        terminal = terminal with { Socket = accepted };
        Assert.Equal(terminal, await evidence.ConfirmTerminalAsync(accepted, ct));
    }

    private static bool CanDuplicateInScratch(SafeFileHandle pidFd, int fd)
    {
        const int pidFdGetFd = 438;
        using var duplicate = new SafeFileHandle(ScratchDuplicate(pidFdGetFd, pidFd, fd, 0), ownsHandle: true);
        return !duplicate.IsInvalid;
    }

    [Fact]
    public void OpenDelegationRequiresDistinctOrderedAcceptedUsesAndExactTerminalProofs()
    {
        var binding = FixtureBinding("/fixture/direct-reservation");
        var epoch = Guid.NewGuid();
        var store = new MemoryStore(new(ServingBrokerRecord.CurrentFormat, binding.Scope, 0, null, epoch,
            binding, Open: false, Close: null, []));
        var ledger = new ServingBrokerLedger(binding.Scope, epoch, store);
        var exchange = Guid.NewGuid();
        var models = IssueProbe(ServingBrokerUseKind.Models, inode: 1, cookie: 2);
        Assert.Null(ledger.Reserve(binding, ServingBrokerUseKind.State, exchange, invocation: null));
        Assert.False(ledger.CompleteProbe(models, Terminal(models.Accepted! with { AcceptedCookie = 3 })));
        Assert.False(ledger.TryDelegateOpen(binding, exchange));
        Assert.True(ledger.CompleteProbe(models, Terminal(models.Accepted!)));
        var state = IssueProbe(ServingBrokerUseKind.State, inode: 4, cookie: 5);
        Assert.False(ledger.CompleteProbe(state, Terminal(models.Accepted!)));
        Assert.False(ledger.TryDelegateOpen(binding, exchange));
        Assert.True(ledger.CompleteProbe(state, Terminal(state.Accepted!)));
        Assert.False(ledger.TryDelegateOpen(binding, Guid.NewGuid()));
        Assert.True(ledger.TryDelegateOpen(binding, exchange));
        Assert.True(ledger.CanAttachInitial(binding));
        Assert.False(ledger.TryDelegateOpen(binding, exchange));

        ServingBrokerUse IssueProbe(ServingBrokerUseKind kind, ulong inode, ulong cookie)
        {
            var reserved = Assert.IsType<ServingBrokerUse>(ledger.Reserve(binding, kind, exchange, invocation: null));
            var issued = Assert.IsType<ServingBrokerUse>(ledger.Issue(reserved));
            var request = new ServingHostSocketRequest(ServingBrokerIssuer.ServingIdentity(binding), issued.Id,
                kind, null, new(1, 2), new(10, 12), new(11, 13), new("127.0.0.1", 1234), new("127.0.0.1", 5678));
            return Assert.IsType<ServingBrokerUse>(ledger.BindAccepted(issued, new(request, inode, cookie, new string('A', 64))));
        }

        static ServingHostTerminalProof Terminal(ServingHostAcceptedSocket socket) =>
            new(socket, 1, new string('A', 64), ServingHostTerminalKind.WorkTerminal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosedCutoffPrecedesStopIntentAndSurvivesMissingReadbackAcrossRestart(bool issueUse)
    {
        var scratch = Directory.CreateTempSubdirectory("air-serving-");
        try
        {
            var root = scratch.FullName;
            var binding = FixtureBinding(Path.Combine(root, "reservation"));
            var authority = Directory.CreateDirectory(binding.Scope.AuthorityDirectory);
            var path = Path.Combine(authority.FullName, ServingBrokerFileStore.RecordsName);
            var lockPath = Path.Combine(authority.FullName, ServingBrokerFileStore.FenceName);
            var epoch = Guid.NewGuid();
            var writer = Guid.NewGuid();
            var initial = new ServingBrokerRecord(ServingBrokerRecord.CurrentFormat, binding.Scope, 0, null, epoch,
                binding, Open: true, Close: null, [], ApplicationWriter: writer);
            var body = JsonSerializer.SerializeToUtf8Bytes(initial,
                ServingBrokerRecordJsonContext.Default.ServingBrokerRecord);
            var framed = new byte[sizeof(int) + body.Length];
            BinaryPrimitives.WriteInt32BigEndian(framed, body.Length);
            body.CopyTo(framed, sizeof(int));
            await File.WriteAllBytesAsync(path, framed, CancellationToken.None);
            await File.WriteAllBytesAsync(lockPath, Array.Empty<byte>(), CancellationToken.None);
            var anchor = new Anchor(new(binding.Scope, 0, Convert.ToHexString(SHA256.HashData(body))));
            var ledger = new ServingBrokerLedger(binding.Scope, epoch, OpenStore());
            var invocation = new VerifiedInstanceInvocation(binding.Route, Guid.NewGuid(),
                new Uri("http://127.0.0.1/v1/chat/completions"), 1, writer);
            ServingBrokerUse? issued = null;
            if (issueUse)
            {
                var reserved = Assert.IsType<ServingBrokerUse>(ledger.Reserve(binding, ServingBrokerUseKind.Application,
                    invocation.Id, invocation));
                issued = Assert.IsType<ServingBrokerUse>(ledger.Issue(reserved));
            }

            var incumbent = Assert.IsType<HostCheckpointLaunchEffect>(binding.Applied.State.AppliedLaunch);
            var successor = incumbent.Plan with { Tier = "other-tier" };
            var stop = new HostMutation(incumbent.Operation.Epoch + 1, Guid.NewGuid(), HostMutationKind.Stop,
                incumbent.Plan.Engine, incumbent.InstanceId, null, null);
            var target = new CheckedEnrolledTarget(new(successor.Engine, successor.Model, "other-tier"),
                new(successor.Engine, successor.Model, "other-tier", successor.Digest(), new Uri("http://127.0.0.1/v1")),
                new(successor.Engine, binding.CatalogHead.Daemon, "/fixture/docker.sock"),
                HostInitialLaunchIntent.Freeze(successor, "{}"u8),
                new("sha256:" + new string('1', 64), "fixture/managed@sha256:" + new string('2', 64), "linux/amd64"), "v1.45");
            var approval = new EnrolledCatalogApproval(
                new(target.Key, successor.Configuration, successor.BackendRoute, target.Endpoint, target.PlanDigest),
                new(target.Intent, target.Image, target.ApiVersion));
            var query = new EnrolledSuccessorQuery(Guid.NewGuid(),
                new(binding.Scope, new(binding.CatalogHead, [target.EngineGrant], [approval], [], [])),
                binding.Applied.State, target, binding.Route, ProposedStop: stop);
            var commitment = binding.Applied;
            var close = Assert.IsType<ServingBrokerClose>(ledger.Seal(query, commitment, 1, 2));
            Assert.Equal(HostCheckpointPhase.Resident, commitment.State.Phase);
            Assert.Null(commitment.State.Transition);
            Assert.Equal(stop, close.Retirement.Stop);
            Assert.True(close.Retirement.Approves(target));
            Assert.Null(ledger.Seal(query with { ProposedStop = stop with { Id = Guid.NewGuid() } }, commitment, 1, 2));
            Assert.Null(ledger.Seal(query, commitment, 1, 3));
            var foreignEpoch = new ServingBrokerLedger(binding.Scope, Guid.NewGuid(), OpenStore());
            Assert.Null(foreignEpoch.Seal(query, commitment, 1, 2));
            Assert.Null(foreignEpoch.CompleteClose(close, binding.Route));
            var observerRead = ServingBrokerFileStore.OpenRetirementReader(binding.Scope, anchor);
            Assert.Null(observerRead(close.Retirement.CloseId));
            if (!issueUse)
            {
                Assert.Equal(close.Retirement, ledger.CompleteClose(close, binding.Route));
                Assert.Equal(close.Retirement, observerRead(close.Retirement.CloseId));
            }

            var reopenedStore = OpenStore();
            var restarted = new ServingBrokerLedger(binding.Scope, Guid.NewGuid(), reopenedStore);
            var durable = Assert.IsType<ServingBrokerSnapshot>(reopenedStore.Read());
            Assert.False(durable.Record.Open);
            Assert.Equal(close.Retirement.CloseId, Assert.IsType<ServingBrokerClose>(durable.Record.Close).Retirement.CloseId);
            if (issueUse)
            {
                Assert.Equal(issued, Assert.Single(durable.Record.Uses));
                Assert.Null(restarted.CompleteClose(close, binding.Route));
                Assert.Null(observerRead(close.Retirement.CloseId));
                Assert.Null(restarted.ReadRetirement(close.Retirement.CloseId));

                var socketRequest = new ServingHostSocketRequest(ServingBrokerIssuer.ServingIdentity(binding),
                    issued!.Id, issued.Kind, invocation, new(1, 2), new(10, 12), new(11, 13),
                    new("127.0.0.1", 1234), new("127.0.0.1", 5678));
                var accepted = new ServingHostAcceptedSocket(socketRequest, 1, 2, new string('A', 64));
                Assert.Null(restarted.BindAccepted(issued, accepted));
                Assert.Null(ledger.Reserve(binding, ServingBrokerUseKind.Application, Guid.NewGuid(), invocation));
                var bound = Assert.IsType<ServingBrokerUse>(ledger.BindAccepted(issued, accepted));
                Assert.Null(ledger.CompleteClose(close, binding.Route));
                Assert.True(ledger.Retire(bound, new(accepted, 1, new string('A', 64), ServingHostTerminalKind.WorkTerminal)));
                Assert.Equal(close.Retirement, ledger.CompleteClose(close, binding.Route));
                Assert.Equal(close.Retirement, observerRead(close.Retirement.CloseId));
            }
            else
            {
                Assert.Empty(durable.Record.Uses);
                Assert.Equal(close.Retirement, restarted.ReadRetirement(close.Retirement.CloseId));
            }

            Assert.Null(observerRead(Guid.NewGuid()));
            Assert.Null(restarted.Reserve(binding, ServingBrokerUseKind.Application, invocation.Id, invocation));

            ServingBrokerFileStore OpenStore() => ServingBrokerFileStore.OpenWriter(binding.Scope, anchor);
        }
        finally { scratch.Delete(recursive: true); }
    }

    private static ServingBrokerBinding FixtureBinding(string path)
    {
        var scope = new HostReservationScope(path, Guid.NewGuid(), new("partition", 1));
        var head = new EnrolledPairHead("slot", "partition", "daemon", "router", 1, 0, new string('A', 64));
        var plan = new HostCheckpointPlan("managed", "model", "tier", "config", 1_000, "route", new string('B', 64));
        var launch = new HostMutation(1, Guid.NewGuid(), HostMutationKind.Launch, plan.Engine, plan.Model,
            plan.Tier, plan.Digest());
        var effect = new HostCheckpointLaunchEffect(launch, plan, new string('C', 64));
        var resident = new HostEffectCheckpointState(HostEffectCheckpointState.FormatVersion, 3, 1,
            HostCheckpointPhase.Resident, effect);
        return new(scope, head, new(resident, new string('E', 64)),
            new("partition", 1, launch.Engine, plan.Digest(), effect.InstanceId, "serving-lifetime", 1, 1),
            new string('F', 64), TotalSlots: 1);
    }

    private sealed class Anchor(ServingBrokerHead initial) : IServingBrokerAnchor
    {
        private ServingBrokerHead _current = initial;
        internal bool RefuseNext { private get; set; }
        internal bool LoseNextReply { private get; set; }
        internal bool LoseReadback { get; set; }
        internal bool LoseReadbackOnCommit { private get; set; }
        public ServingBrokerHead? Read(HostReservationScope scope) => !LoseReadback && _current.Scope == scope ? _current : null;

        public bool TryAdvance(ServingBrokerHead predecessor, ServingBrokerHead next)
        {
            if (RefuseNext)
            {
                RefuseNext = false;
                return false;
            }
            if (_current != predecessor) return false;
            _current = next;
            if (LoseReadbackOnCommit)
            {
                LoseReadbackOnCommit = false;
                LoseReadback = true;
            }
            if (LoseNextReply)
            {
                LoseNextReply = false;
                throw new IOException("fixture lost CAS reply");
            }
            return true;
        }
    }

    private sealed class MemoryStore(ServingBrokerRecord initial) : IServingBrokerStore
    {
        private ServingBrokerSnapshot _current = Snapshot(initial);
        public ServingBrokerSnapshot Read() => _current;

        public bool TryAdvance(ServingBrokerSnapshot predecessor, ServingBrokerRecord next)
        {
            if (_current != predecessor) return false;
            _current = Snapshot(next);
            return true;
        }

        private static ServingBrokerSnapshot Snapshot(ServingBrokerRecord record) => new(record,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record,
                ServingBrokerRecordJsonContext.Default.ServingBrokerRecord))));
    }

    [LibraryImport("libc.so.6", EntryPoint = "pidfd_open", SetLastError = true)]
    private static partial int OpenPidFd(int pid, uint flags);

    [LibraryImport("libc.so.6", EntryPoint = "syscall")]
    private static partial nint ScratchDuplicate(nint number, SafeFileHandle pidFd, int targetFd, uint flags);
}
