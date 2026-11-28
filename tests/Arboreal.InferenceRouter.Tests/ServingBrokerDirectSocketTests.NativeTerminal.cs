using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class ServingBrokerDirectSocketTests
{
    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeTerminalRequiresTheExactIssuedUseAndWorker(bool wrongWorker)
    {
        if (!OperatingSystem.IsLinux()) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var scratch = Directory.CreateTempSubdirectory("air-native-");
        try
        {
            var stat = await File.ReadAllTextAsync("/proc/self/stat", ct);
            var fields = stat[(stat.AsSpan().LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var lifetime = new ServingHostLifetime(Environment.ProcessId, ulong.Parse(fields[19], CultureInfo.InvariantCulture));
            var binding = FixtureBinding(Path.Combine(scratch.FullName, "reservation"));
            var epoch = Guid.NewGuid();
            var writer = Guid.NewGuid();
            var store = new MemoryStore(new(ServingBrokerRecord.CurrentFormat, binding.Scope, 0, null, epoch,
                binding, Open: true, Close: null, [], ApplicationWriter: writer));
            var ledger = new ServingBrokerLedger(binding.Scope, epoch, store);
            var invocation = new VerifiedInstanceInvocation(binding.Route, Guid.NewGuid(),
                new Uri("http://127.0.0.1/v1/chat/completions"), 1, writer);
            var reserved = Assert.IsType<ServingBrokerUse>(ledger.Reserve(binding, ServingBrokerUseKind.Application,
                invocation.Id, invocation));
            var issued = Assert.IsType<ServingBrokerUse>(ledger.Issue(reserved));
            var request = new ServingHostSocketRequest(ServingBrokerIssuer.ServingIdentity(binding), issued.Id,
                issued.Kind, invocation, new(1, 2), lifetime, lifetime, new("127.0.0.1", 1234), new("127.0.0.1", 5678));
            var exclusion = new ServingHostExclusion(request.Binding, request.Namespace, lifetime, lifetime, request.Listener,
                new(1, request.Listener, 3, 4), 1, new string('A', 64));
            var socket = new ServingHostAcceptedSocket(request, 15, 16, exclusion.Digest);
            var use = Assert.IsType<ServingBrokerUse>(ledger.BindAccepted(issued, socket));
            var source = new ServingNativeTerminalSource(ledger, () => exclusion, HeadReadV1Tests.EffectiveUid());

            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(Path.Combine(scratch.FullName, "terminal.sock"));
            listener.Bind(endpoint);
            listener.Listen(1);
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(endpoint, ct);
            using var accepted = await listener.AcceptAsync(ct);
            var serving = source.ServeAsync(accepted, ct);
            using var stream = new NetworkStream(client, ownsSocket: false);
            var requestFrame = new byte[18];
            requestFrame[0] = 1;
            requestFrame[1] = (byte)issued.Kind;
            BinaryPrimitives.WriteUInt64BigEndian(requestFrame.AsSpan(2), socket.AcceptedInode);
            BinaryPrimitives.WriteUInt64BigEndian(requestFrame.AsSpan(10), socket.AcceptedCookie);
            await stream.WriteAsync(requestFrame, ct);
            var completion = new byte[32];
            await stream.ReadExactlyAsync(completion.AsMemory(0, 16), ct);
            Assert.Equal(issued.Id, new Guid(completion.AsSpan(0, 16)));
            Assert.Null(await source.ReadTerminalAsync(socket with { AcceptedCookie = 17 }, ct));
            var proofTask = source.ReadTerminalAsync(socket, ct).AsTask();
            Assert.False(proofTask.IsCompleted);
            BinaryPrimitives.WriteInt32BigEndian(completion.AsSpan(16), lifetime.ProcessId);
            BinaryPrimitives.WriteUInt64BigEndian(completion.AsSpan(20), lifetime.StartTicks + (wrongWorker ? 1UL : 0UL));
            BinaryPrimitives.WriteUInt32BigEndian(completion.AsSpan(28), 2);
            await stream.WriteAsync(completion, ct);
            await serving;
            var proof = await proofTask;
            if (wrongWorker)
            {
                Assert.Null(proof);
                Assert.Equal(use, Assert.Single(store.Read().Record.Uses));
            }
            else
            {
                var terminal = Assert.IsType<ServingHostTerminalProof>(proof);
                Assert.Equal(new ServingHostTerminalProof(socket, store.Read().Record.Revision, store.Read().Digest,
                    ServingHostTerminalKind.WorkTerminal),
                    terminal);
                Assert.True(ledger.Retire(use, terminal));
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }
}
