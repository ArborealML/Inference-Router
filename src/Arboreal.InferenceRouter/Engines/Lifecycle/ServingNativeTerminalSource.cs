using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Arboreal.InferenceRouter.Lifecycle;

// Runs in the supervised helper. Only the attested HTTP handler may report completion,
// and only for a socket already joined to an issued use by the protected broker.
internal sealed class ServingNativeTerminalSource(ServingBrokerLedger ledger,
    Func<ServingHostExclusion?> readExclusion, uint handlerUid)
{
    private const byte Version = 1;
    private const int HeaderLength = 2 + 2 * sizeof(ulong);
    private const int CompletionLength = 16 + sizeof(int) + sizeof(ulong) + sizeof(uint);
    private const int ConnectionLimit = 256;
    private const int LinuxSocketLevel = 1;
    private const int LinuxPeerCredentials = 17;
    private static readonly TimeSpan BeginTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<Guid, PendingCompletion> _pending = new();

    // The supervisor binds and owns the listener and cancels it with the helper lifetime.
    internal async Task RunAsync(Socket listener, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = lifetime.Token;
        var active = new HashSet<Task>();
        try
        {
            while (true)
            {
                while (active.Count >= ConnectionLimit)
                {
                    var finished = await Task.WhenAny(active).WaitAsync(token).ConfigureAwait(false);
                    _ = active.Remove(finished);
                    await finished.ConfigureAwait(false);
                }
                active.RemoveWhere(static task => task.IsCompletedSuccessfully);
                active.Add(ServeOwnedAsync(await listener.AcceptAsync(token).ConfigureAwait(false), token));
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(active).ConfigureAwait(false);
        }
    }

    private async Task ServeOwnedAsync(Socket accepted, CancellationToken ct)
    {
#pragma warning disable IDISP007 // RunAsync transfers each newly accepted socket to this task; the listener stays borrowed.
        using (accepted)
#pragma warning restore IDISP007
            await ServeAsync(accepted, ct).ConfigureAwait(false);
    }

    // The listener owns this Unix socket. The connection never carries serving traffic.
    internal async Task ServeAsync(Socket accepted, CancellationToken ct)
    {
        ServingHostAcceptedSocket? socket = null;
        PendingCompletion? pending = null;
        var published = false;
        try
        {
            if (readExclusion() is not { } exclusion || !exclusion.IsValid() ||
                !IsHandlerPeer(accepted, exclusion.Handler) || !HasLifetime(exclusion.Worker))
                return;

            await using var stream = new NetworkStream(accepted, ownsSocket: false);
            var header = new byte[HeaderLength];
            using (var begin = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                begin.CancelAfter(BeginTimeout);
                await stream.ReadExactlyAsync(header, begin.Token).ConfigureAwait(false);
                if (header[0] != Version ||
                    ledger.ReadIssuedSocket(BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(2)),
                        BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(10))) is not { } issued ||
                    (byte)issued.Socket.Request.Kind != header[1] ||
                    !Binds(exclusion, issued.Socket))
                    return;

                socket = issued.Socket;
                var candidate = new PendingCompletion(socket);
                if (!_pending.TryAdd(socket.Request.UseId, candidate)) return;
                pending = candidate;
                await stream.WriteAsync(socket.Request.UseId.ToByteArray(), begin.Token).ConfigureAwait(false);
            }

            var completion = new byte[CompletionLength];
            await stream.ReadExactlyAsync(completion, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (new Guid(completion.AsSpan(0, 16)) != socket.Request.UseId ||
                !MatchesWorker(exclusion.Worker, completion.AsSpan(16), socket.Request.Kind) ||
                ledger.ReadIssuedSocket(socket.AcceptedInode, socket.AcceptedCookie) is not { } current ||
                current.Socket != socket || current.Head.Revision <= 0 || !Binds(exclusion, socket) ||
                !HasLifetime(exclusion.Handler) || !HasLifetime(exclusion.Worker))
                return;

            ct.ThrowIfCancellationRequested();
            published = pending.Proof.TrySetResult(new(socket, current.Head.Revision, current.Head.Digest,
                ServingHostTerminalKind.WorkTerminal));
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            return;
        }
        finally
        {
            if (pending is not null && !published)
            {
                pending.Proof.TrySetResult(null);
                _pending.TryRemove(pending.Socket.Request.UseId, out _);
            }
        }
    }

    internal async ValueTask<ServingHostTerminalProof?> ReadTerminalAsync(ServingHostAcceptedSocket socket,
        CancellationToken ct)
    {
        if (!_pending.TryGetValue(socket.Request.UseId, out var pending) || pending.Socket != socket)
            return null;

        try
        {
            return await pending.Proof.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // Removal after a cancelled read does not clear the durable issued use.
            _pending.TryRemove(socket.Request.UseId, out _);
        }
    }

    private bool Binds(ServingHostExclusion exclusion, ServingHostAcceptedSocket socket) =>
        exclusion.Digest == socket.ExclusionDigest && exclusion.Binds(socket.Request) && readExclusion() == exclusion;

    private bool IsHandlerPeer(Socket accepted, ServingHostLifetime handler)
    {
        if (!OperatingSystem.IsLinux() || accepted.AddressFamily != AddressFamily.Unix) return false;
        Span<byte> credentials = stackalloc byte[3 * sizeof(int)];
        return accepted.GetRawSocketOption(LinuxSocketLevel, LinuxPeerCredentials, credentials) == credentials.Length &&
            MemoryMarshal.Read<int>(credentials) == handler.ProcessId &&
            MemoryMarshal.Read<uint>(credentials[sizeof(int)..]) == handlerUid && HasLifetime(handler);
    }

    private static bool MatchesWorker(ServingHostLifetime worker, ReadOnlySpan<byte> completion, ServingBrokerUseKind kind)
    {
        var localPid = BinaryPrimitives.ReadInt32BigEndian(completion);
        var started = BinaryPrimitives.ReadUInt64BigEndian(completion[sizeof(int)..]);
        var legs = BinaryPrimitives.ReadUInt32BigEndian(completion[(sizeof(int) + sizeof(ulong))..]);
        return kind switch
        {
            ServingBrokerUseKind.Models or ServingBrokerUseKind.State => localPid == 0 && started == 0 && legs == 0,
            ServingBrokerUseKind.Application => localPid > 0 && started == worker.StartTicks && legs > 0 &&
                ReadsSameWorker(worker, localPid),
            _ => false,
        };
    }

    private static bool HasLifetime(ServingHostLifetime process)
    {
        var stat = File.ReadAllText(ProcessFile(process, "stat"));
        var close = stat.AsSpan().LastIndexOf(')');
        if (close < 0) return false;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 19 && fields[0] is not ("X" or "Z") &&
            ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var started) &&
            started == process.StartTicks;
    }

    private static bool ReadsSameWorker(ServingHostLifetime worker, int localPid)
    {
        var namespaceIds = File.ReadLines(ProcessFile(worker, "status"))
            .FirstOrDefault(line => line.StartsWith("NSpid:", StringComparison.Ordinal));
        return namespaceIds is not null &&
            int.TryParse(namespaceIds.AsSpan(namespaceIds.AsSpan().LastIndexOf('\t') + 1),
                NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid == localPid;
    }

    private static string ProcessFile(ServingHostLifetime process, string name) =>
        FormattableString.Invariant($"/proc/{process.ProcessId}/{name}");

    private sealed record PendingCompletion(ServingHostAcceptedSocket Socket)
    {
        internal TaskCompletionSource<ServingHostTerminalProof?> Proof { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
