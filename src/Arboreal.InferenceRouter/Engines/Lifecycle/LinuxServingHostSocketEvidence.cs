using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record ServingHostPinnedProcess(ServingHostLifetime Identity, SafeFileHandle PidFd);

internal sealed record ServingHostListenerDescriptor(int Number, ServingHostAddress Bound, ulong Inode, ulong Cookie);

// A published record from the independently commissioned controller that enforces exclusive
// listener ownership and no FD delegation, inheritance or rebinding for this exact generation.
// Kernel observations corroborate the listener and accepted FD. They cannot mint this record.
internal sealed record ServingHostExclusion(ServingBackendIdentity Binding, ServingHostNamespace Namespace,
    ServingHostLifetime Handler, ServingHostLifetime Worker, ServingHostAddress Endpoint,
    ServingHostListenerDescriptor Listener, long Revision, string Digest)
{
    internal bool IsValid() => Revision > 0 && HostMutation.IsPlanDigest(Digest) &&
        Listener is { Number: >= 0, Inode: > 0, Cookie: > 0 and < ulong.MaxValue } &&
        Listener.Bound.Port == Endpoint.Port;

    internal bool Binds(ServingHostSocketRequest request) =>
        Binding == request.Binding && Namespace == request.Namespace && Handler == request.Handler &&
        Worker == request.Worker && Endpoint == request.Listener;
}

internal enum ServingHostTerminalKind { WorkTerminal = 1, BothProcessesExited }

internal sealed record ServingHostTerminalProof(ServingHostAcceptedSocket Socket, long AuthorityRevision,
    string AuthorityDigest, ServingHostTerminalKind Kind)
{
    internal bool Binds(ServingHostAcceptedSocket expected) =>
        Socket == expected && AuthorityRevision > 0 && HostMutation.IsPlanDigest(AuthorityDigest) && Enum.IsDefined(Kind);
}

internal sealed class LinuxServingHostSocketEvidence(ServingHostPinnedProcess handler, ServingHostPinnedProcess worker,
    ServingHostExclusion exclusion, Func<ServingHostExclusion?> readPublishedExclusion,
    Func<ServingHostAcceptedSocket, CancellationToken, ValueTask<ServingHostTerminalProof?>> readTerminal) : IServingHostSocketEvidence
{
    private static readonly TimeSpan RetryAcceptance = TimeSpan.FromMilliseconds(10);

    // Called only in the held network namespace and outside the short state gates.
    public bool CanConnect(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return OperatingSystem.IsLinux() && IsGenerationCurrent && exclusion.Handler == handler.Identity &&
                exclusion.Worker == worker.Identity && MatchesLifetimes(exclusion.Namespace) &&
                IPAddress.TryParse(exclusion.Endpoint.Address, out var endpoint) &&
                IPAddress.TryParse(exclusion.Listener.Bound.Address, out var bound) &&
                bound.AddressFamily == endpoint.AddressFamily &&
                (bound.Equals(endpoint) || bound.Equals(IPAddress.Any) || bound.Equals(IPAddress.IPv6Any)) &&
                LinuxServingAcceptedSocket.MatchesListener(exclusion.Listener, handler.PidFd) && IsGenerationCurrent;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public ServingHostAcceptedSocket? JoinAccepted(ServingHostSocketRequest request, CancellationToken ct)
    {
        if (!exclusion.Binds(request) || !CanConnect(ct) ||
            !IPAddress.TryParse(request.Listener.Address, out var listener) ||
            !IPAddress.TryParse(request.Client.Address, out var client) ||
            listener.AddressFamily != client.AddressFamily ||
            (listener.AddressFamily == AddressFamily.InterNetworkV6 &&
                (listener.ScopeId != 0 || client.ScopeId != 0 || listener.IsIPv4MappedToIPv6 || client.IsIPv4MappedToIPv6)))
            return null;

        var descriptors = Path.Combine(ProcessPath(request.Handler.ProcessId), "fd");
        try
        {
            while (IsGenerationCurrent && MatchesLifetimes(request.Namespace))
            {
                ct.ThrowIfCancellationRequested();
                var match = LinuxServingAcceptedSocket.Find(listener, request.Listener.Port, client, request.Client.Port,
                    descriptors, handler.PidFd);
                if (match is { } accepted && CanConnect(ct))
                {
                    ct.ThrowIfCancellationRequested();
                    return new(request, accepted.Inode, accepted.Cookie, exclusion.Digest);
                }

                // Connect can finish while the approved process has yet to accept.
                _ = ct.WaitHandle.WaitOne(RetryAcceptance);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    // The grant already joined this accepted socket. At first write, also verify the
    // original pidfds have not reported exit since the asynchronous authority read.
    public bool IsLive(ServingHostAcceptedSocket accepted) => Binds(accepted) && IsGenerationCurrent &&
        PollPinned(handler.PidFd, exited: false) && PollPinned(worker.PidFd, exited: false);

    public bool IsGenerationCurrent => exclusion.IsValid() && readPublishedExclusion() == exclusion;

    private bool Binds(ServingHostAcceptedSocket accepted) =>
        exclusion.Digest == accepted.ExclusionDigest && exclusion.Binds(accepted.Request);

    public async ValueTask<ServingHostTerminalProof?> ConfirmTerminalAsync(ServingHostAcceptedSocket accepted, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var request = accepted.Request;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Binds(accepted) || request.Handler != handler.Identity || request.Worker != worker.Identity ||
                await readTerminal(accepted, ct).ConfigureAwait(false) is not { } terminal || !terminal.Binds(accepted))
                return null;
            ct.ThrowIfCancellationRequested();
            // This outcome requires the independent authority's no-inheritor witness as
            // well as a kernel exit observation on both original pidfds.
            if (terminal.Kind == ServingHostTerminalKind.BothProcessesExited)
                return HasExactPidFdState(handler, exited: true) && HasExactPidFdState(worker, exited: true) ? terminal : null;

            // FD absence is necessary but cannot establish completion of model work.
            while (MatchesLifetimes(request.Namespace))
            {
                ct.ThrowIfCancellationRequested();
                if (!HoldsSocket(ProcessPath(request.Handler.ProcessId), accepted.AcceptedInode))
                    return MatchesLifetimes(request.Namespace) ? terminal : null;
                await Task.Delay(RetryAcceptance, ct).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HoldsSocket(string processPath, ulong inode)
    {
        var target = FormattableString.Invariant($"socket:[{inode}]");
        foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(processPath, "fd")))
            if (new FileInfo(fd).LinkTarget == target) return true;
        return false;
    }

    private bool MatchesLifetimes(ServingHostNamespace expected) =>
        IsPinnedAndAlive(handler) && IsPinnedAndAlive(worker) &&
        MatchesLifetime(handler.Identity) && MatchesLifetime(worker.Identity) &&
        new FileInfo(Path.Combine(ProcessPath(handler.Identity.ProcessId), "ns", "net")).LinkTarget ==
            FormattableString.Invariant($"net:[{expected.Inode}]") &&
        IsPinnedAndAlive(handler) && IsPinnedAndAlive(worker);

    private static bool IsPinnedAndAlive(ServingHostPinnedProcess process) => HasExactPidFdState(process, exited: false);

    private static bool HasExactPidFdState(ServingHostPinnedProcess process, bool exited)
    {
        if (process.PidFd.IsClosed || process.PidFd.IsInvalid) return false;
        var added = false;
        try
        {
            process.PidFd.DangerousAddRef(ref added);
            var fd = process.PidFd.DangerousGetHandle().ToInt32();
            var exactPid = FormattableString.Invariant($"Pid:\t{process.Identity.ProcessId}");
            var fdInfo = File.ReadLines(FormattableString.Invariant($"/proc/self/fdinfo/{fd}"));
            if (!fdInfo.Any(line => line == exactPid || (exited && line == "Pid:\t-1")))
                return false;

            return PollState(fd, exited);
        }
        finally
        {
            if (added) process.PidFd.DangerousRelease();
        }
    }

    private static bool PollPinned(SafeFileHandle pin, bool exited)
    {
        if (pin.IsClosed || pin.IsInvalid) return false;
        var added = false;
        try
        {
            pin.DangerousAddRef(ref added);
            return PollState(pin.DangerousGetHandle().ToInt32(), exited);
        }
        catch (ObjectDisposedException) { return false; }
        finally { if (added) pin.DangerousRelease(); }
    }

    private static bool PollState(int descriptor, bool exited)
    {
        var poll = new PollDescriptor { FileDescriptor = descriptor, Events = 1, Returned = 0 };
        var result = Poll(ref poll, 1, 0);
        return exited ? result == 1 && (poll.Returned & 1) != 0 : result == 0;
    }

    private static bool MatchesLifetime(ServingHostLifetime lifetime)
    {
        var stat = File.ReadAllText(Path.Combine(ProcessPath(lifetime.ProcessId), "stat"));
        var close = stat.AsSpan().LastIndexOf(')');
        if (close < 0) return false;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The suffix starts at stat field 3. Process start time is field 22.
        return fields.Length > 19 && fields[0] is not ("Z" or "X") &&
            ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var started) &&
            started == lifetime.StartTicks;
    }

    private static string ProcessPath(int pid) => FormattableString.Invariant($"/proc/{pid}");

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        internal int FileDescriptor;
        internal short Events;
        internal short Returned;
    }

    // pollfd is blittable. Keep the core AOT assembly free of an unsafe setting.
#pragma warning disable SYSLIB1054
    [DllImport("libc.so.6", EntryPoint = "poll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);
#pragma warning restore SYSLIB1054
}
