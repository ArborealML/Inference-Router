using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record ServingBackendIdentity(HostReservationScope Scope, EnrolledPairHead Head, HostMutation Launch,
    string AppliedDigest, VerifiedInstanceRoute Route, string PolicyDigest, string? AppliedJournalDigest);

internal sealed record ServingBackendProfile(string CanonicalModel, string Profile, long Context, int Slots);

internal sealed record ServingHostLifetime(int ProcessId, ulong StartTicks);
internal sealed record ServingHostNamespace(ulong Device, ulong Inode);
internal sealed record ServingHostAddress(string Address, int Port)
{
    internal static ServingHostAddress From(IPEndPoint endpoint) => new(endpoint.Address.ToString(), endpoint.Port);
}

internal sealed record ServingHostSocketRequest(ServingBackendIdentity Binding, Guid UseId, ServingBrokerUseKind Kind,
    VerifiedInstanceInvocation? Invocation, ServingHostNamespace Namespace, ServingHostLifetime Handler,
    ServingHostLifetime Worker, ServingHostAddress Client, ServingHostAddress Listener);

internal sealed record ServingHostAcceptedSocket(ServingHostSocketRequest Request, ulong AcceptedInode,
    ulong AcceptedCookie = 0, string? ExclusionDigest = null);

// The commissioned observer joins the accepted socket to the exact pinned handler FD before
// transfer and updates the state-only liveness witness under the issuer gate.
internal interface IServingHostSocketEvidence
{
    bool IsGenerationCurrent { get; }
    bool CanConnect(CancellationToken ct);
    ServingHostAcceptedSocket? JoinAccepted(ServingHostSocketRequest request, CancellationToken ct);
    bool IsLive(ServingHostAcceptedSocket accepted);
    ValueTask<ServingHostTerminalProof?> ConfirmTerminalAsync(ServingHostAcceptedSocket accepted, CancellationToken ct);
}

internal sealed record ServingBackendEndpoint(ServingHostAddress Listener, SafeFileHandle NamespaceHandle,
    ServingHostNamespace Namespace, ServingHostLifetime Handler, ServingHostLifetime Worker, ServingBackendProfile Profile,
    IServingHostSocketEvidence Evidence);

// Owns the exact client socket joined to the accepted handler before any HTTP bytes are sent.
internal sealed class ServingBackendConnection : IAsyncDisposable
{
    internal const int MaxResponse = 512 * 1024;
    private const int CloneNewNet = 0x40000000;
    private const string PrivateHost = "instance.invalid";

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private int _taken;

    private ServingBackendConnection(Socket socket, ServingHostAcceptedSocket accepted)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        Accepted = accepted;
    }

    internal ServingHostAcceptedSocket Accepted { get; }

    internal static Task<ServingBackendConnection> OpenAsync(ServingBackendEndpoint endpoint,
        ServingBackendIdentity expected, ServingBrokerUse issued, ReadinessDeadline? deadline, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || issued.Phase != ServingBrokerUsePhase.Issued || issued.Route != expected.Route ||
            issued.Kind is not (ServingBrokerUseKind.Models or ServingBrokerUseKind.State or ServingBrokerUseKind.Application) ||
            !IPAddress.TryParse(endpoint.Listener.Address, out var address) ||
            endpoint.Listener.Port is <= 0 or > IPEndPoint.MaxPort || endpoint.NamespaceHandle.IsInvalid ||
            endpoint.Namespace.Inode == 0 || endpoint.Handler is not { ProcessId: > 0, StartTicks: > 0 } ||
            endpoint.Worker is not { ProcessId: > 0, StartTicks: > 0 })
            throw new IOException("The broker did not issue an exact use of a pinned host route.");

        return ConnectInNamespaceAsync(endpoint, expected, issued, new IPEndPoint(address, endpoint.Listener.Port), deadline, ct);
    }

    private static ServingBackendConnection ConnectCertified(ServingBackendEndpoint endpoint, ServingBackendIdentity expected,
        ServingBrokerUse issued, IPEndPoint listener, CancellationToken ct)
    {
        var socket = ConnectSocket(listener, ct);
        try
        {
            if (socket.LocalEndPoint is not IPEndPoint local || socket.RemoteEndPoint is not IPEndPoint remote ||
                !remote.Equals(listener))
                throw new IOException("The direct socket did not connect to the approved listener.");

            var request = new ServingHostSocketRequest(expected, issued.Id, issued.Kind, issued.Invocation,
                endpoint.Namespace, endpoint.Handler, endpoint.Worker, ServingHostAddress.From(local),
                ServingHostAddress.From(remote));
            var accepted = endpoint.Evidence.JoinAccepted(request, ct);
            if (accepted is not { AcceptedInode: > 0, AcceptedCookie: > 0 } || accepted.Request != request || !endpoint.Evidence.IsLive(accepted))
                throw new IOException("No exact handler accepted the direct socket.");

            return new ServingBackendConnection(socket, accepted);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal async ValueTask<byte[]> ReadProbeAsync(ServingBrokerProbe probe, VerifiedDispatchCredential credential,
        CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = (context, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (context.DnsEndPoint.Port != 80 ||
                    !string.Equals(context.DnsEndPoint.Host, PrivateHost, StringComparison.Ordinal))
                    throw new HttpRequestException("The readiness request cannot use another destination.");
                return ValueTask.FromResult(TakeStream());
            },
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://" + PrivateHost + probe.Uri.PathAndQuery))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Host = probe.Uri.Authority;
        request.Headers.ConnectionClose = true;
        if (credential.Authorization is { } authorization)
            request.Headers.Add("Authorization", authorization);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is null && response.Headers.TransferEncodingChunked != true)
            throw new IOException("The readiness response needs an explicit body boundary.");
        if (response.Content.Headers.ContentLength is { } length && length > probe.ResponseLimit)
            throw new IOException("The readiness body exceeded the frozen limit.");

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var captured = new MemoryStream();
        var buffer = new byte[Math.Min(8192, probe.ResponseLimit)];
        for (;;)
        {
            var read = await body.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) return captured.ToArray();
            if (captured.Length + read > probe.ResponseLimit)
                throw new IOException("The readiness body exceeded the frozen limit.");
            captured.Write(buffer.AsSpan(0, read));
        }
    }

    internal Stream TakeApplicationStream()
    {
        if (Accepted.Request.Kind != ServingBrokerUseKind.Application)
            throw new IOException("Only an application use may lend this stream.");
        return TakeStream();
    }

    private Stream TakeStream() =>
        Interlocked.Exchange(ref _taken, 1) == 0 ? _stream :
            throw new IOException("The exact direct socket has already been lent.");

    internal async ValueTask<ServingHostTerminalProof?> CloseAndConfirmTerminalAsync(IServingHostSocketEvidence evidence, TimeSpan cleanupTimeout)
    {
        var accepted = Accepted;
        await DisposeAsync().ConfigureAwait(false);
        using var cleanup = new CancellationTokenSource(cleanupTimeout);
        return await evidence.ConfirmTerminalAsync(accepted, cleanup.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        finally { _socket.Dispose(); }
    }

    private static Task<ServingBackendConnection> ConnectInNamespaceAsync(ServingBackendEndpoint endpoint,
        ServingBackendIdentity expected, ServingBrokerUse issued, IPEndPoint listener, ReadinessDeadline? deadline,
        CancellationToken ct)
    {
        var connected = new TaskCompletionSource<ServingBackendConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only this dedicated thread enters the namespace. The socket remains in it after
        // transfer. This thread exits without ever returning to the router thread pool.
        var worker = new Thread(() =>
        {
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(5));
                connect.Token.ThrowIfCancellationRequested();
                if (!LinuxServingAcceptedSocket.MatchesNamespace(endpoint.NamespaceHandle, endpoint.Namespace) ||
                    EnterHeldNamespace(endpoint.NamespaceHandle) != 0 ||
                    new FileInfo("/proc/thread-self/ns/net").LinkTarget !=
                        FormattableString.Invariant($"net:[{endpoint.Namespace.Inode}]"))
                    throw new IOException("The dedicated connector could not enter the held network namespace.");

                if (!endpoint.Evidence.CanConnect(connect.Token) || deadline?.OutcomeForSettlement is
                    (ReadinessStepOutcome.Expired or ReadinessStepOutcome.Stopped or ReadinessStepOutcome.StepExpired))
                    throw new IOException("The exact generation no longer permits a connection.");
                connected.SetResult(ConnectCertified(endpoint, expected, issued, listener, connect.Token));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                connected.SetCanceled(ct);
            }
            catch (Exception error)
            {
                connected.SetException(error);
            }
        }) { IsBackground = true };
        worker.Start();
        return connected.Task;
    }

    // The successful return hands the original socket to the waiting caller. A failed
    // connect has no owner outside this method.
    private static Socket ConnectSocket(IPEndPoint listener, CancellationToken ct)
    {
        var socket = new Socket(listener.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.ConnectAsync(listener, ct).AsTask().GetAwaiter().GetResult();
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static int EnterHeldNamespace(SafeFileHandle heldNamespace)
    {
        var added = false;
        try
        {
            heldNamespace.DangerousAddRef(ref added);
            return SetNetworkNamespace(heldNamespace.DangerousGetHandle().ToInt32(), CloneNewNet);
        }
        finally
        {
            if (added) heldNamespace.DangerousRelease();
        }
    }

    // This Linux syscall takes only blittable scalars, so it needs no generated marshaling
    // stub or unsafe setting in the core assembly. The caller holds the original descriptor.
#pragma warning disable SYSLIB1054
    [DllImport("libc.so.6", EntryPoint = "setns", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SetNetworkNamespace(int fd, int flags);
#pragma warning restore SYSLIB1054
}
