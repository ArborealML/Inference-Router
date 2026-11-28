using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.Lifecycle;

// The commissioned helper owns the handles through the final physical use. The expected
// identities come from the independent exclusion record and cannot be inferred from a port.
internal sealed class CommissionedServingPins : IDisposable
{
    private CommissionedServingPins(ServingHostPinnedProcess handler, ServingHostPinnedProcess worker,
        SafeFileHandle networkNamespace)
    {
        Handler = handler;
        Worker = worker;
        NamespaceHandle = networkNamespace;
    }

    internal ServingHostPinnedProcess Handler { get; }
    internal ServingHostPinnedProcess Worker { get; }
    internal SafeFileHandle NamespaceHandle { get; }

    internal static CommissionedServingPins? TryOpen(ServingHostExclusion exclusion)
    {
        if (!OperatingSystem.IsLinux() || !exclusion.IsValid() ||
            exclusion.Handler is not { ProcessId: > 0, StartTicks: > 0 } ||
            exclusion.Worker is not { ProcessId: > 0, StartTicks: > 0 }) return null;

        SafeFileHandle? handler = null;
        SafeFileHandle? worker = null;
        SafeFileHandle? ns = null;
        var transferred = false;
        try
        {
#pragma warning disable IDISP001, CA2000 // Pins owns both handles after transfer. The finally disposes them otherwise.
            handler = TryPin(exclusion.Handler);
            worker = TryPin(exclusion.Worker);
#pragma warning restore IDISP001, CA2000
            if (handler is null || worker is null) return null;
#pragma warning disable CA2000 // Pins owns the namespace handle after transfer. The finally disposes it otherwise.
            ns = File.OpenHandle(ProcessPath(exclusion.Handler.ProcessId, "ns/net"), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
#pragma warning restore CA2000
            if (!LinuxServingAcceptedSocket.MatchesNamespace(ns, exclusion.Namespace) ||
                !HasNamespace(exclusion.Handler, exclusion.Namespace) || !HasNamespace(exclusion.Worker, exclusion.Namespace) ||
                !MatchesLifetime(exclusion.Handler, handler) || !MatchesLifetime(exclusion.Worker, worker)) return null;

            var pins = new CommissionedServingPins(new(exclusion.Handler, handler), new(exclusion.Worker, worker), ns);
            transferred = true;
            return pins;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (!transferred)
            {
                ns?.Dispose();
                worker?.Dispose();
                handler?.Dispose();
            }
        }
    }

    internal CommissionedServingGeneration? BindGeneration(ServingHostExclusion exclusion, ServingBackendProfile profile,
        ServingBrokerProbePolicy probes) => MatchesCurrent(exclusion)
        ? new(exclusion.Binding, exclusion.Namespace, NamespaceHandle, Handler, Worker, exclusion.Endpoint, profile, probes)
        : null;

    // Use outside the short issuer gate. The gate consumes an already-published authority value.
    private bool MatchesCurrent(ServingHostExclusion exclusion)
    {
        try
        {
            return Handler.Identity == exclusion.Handler && Worker.Identity == exclusion.Worker &&
                LinuxServingAcceptedSocket.MatchesNamespace(NamespaceHandle, exclusion.Namespace) &&
                HasNamespace(Handler.Identity, exclusion.Namespace) && HasNamespace(Worker.Identity, exclusion.Namespace) &&
                MatchesLifetime(Handler.Identity, Handler.PidFd) && MatchesLifetime(Worker.Identity, Worker.PidFd);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return false;
        }
    }

    internal static SafeFileHandle? TryPin(ServingHostLifetime expected)
    {
        var fd = OpenPidFd(expected.ProcessId, 0);
        if (fd < 0) return null;
#pragma warning disable CA2000 // A matching pin transfers to the caller. The finally disposes any failed pin.
        var handle = new SafeFileHandle(fd, ownsHandle: true);
#pragma warning restore CA2000
        var matched = false;
        try
        {
            matched = MatchesLifetime(expected, handle);
            return matched ? handle : null;
        }
        finally
        {
            if (!matched) handle.Dispose();
        }
    }

    internal static bool HasNamespace(ServingHostLifetime process, ServingHostNamespace expected) =>
        new FileInfo(ProcessPath(process.ProcessId, "ns/net")).LinkTarget ==
            FormattableString.Invariant($"net:[{expected.Inode}]");

    internal static bool MatchesLifetime(ServingHostLifetime expected, SafeFileHandle handle)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = handle.DangerousGetHandle().ToInt32();
            var pidLine = FormattableString.Invariant($"Pid:\t{expected.ProcessId}");
            if (!File.ReadLines(FormattableString.Invariant($"/proc/self/fdinfo/{fd}")).Any(line => line == pidLine))
                return false;
            var stat = File.ReadAllText(ProcessPath(expected.ProcessId, "stat"));
            var close = stat.AsSpan().LastIndexOf(')');
            if (close < 0) return false;
            var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var poll = new PollDescriptor { File = fd, Events = 1, Returned = 0 };
            return fields.Length > 19 && fields[0] is not ("X" or "Z") &&
                ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var started) &&
                started == expected.StartTicks && Poll(ref poll, 1, 0) == 0;
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    private static string ProcessPath(int process, string name) => FormattableString.Invariant($"/proc/{process}/{name}");

    public void Dispose()
    {
        NamespaceHandle.Dispose();
        Worker.PidFd.Dispose();
        Handler.PidFd.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        internal int File;
        internal short Events;
        internal short Returned;
    }

    // Blittable Linux calls in the core AOT assembly.
#pragma warning disable SYSLIB1054
    [DllImport("libc.so.6", EntryPoint = "pidfd_open", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenPidFd(int pid, uint flags);

    [DllImport("libc.so.6", EntryPoint = "poll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);
#pragma warning restore SYSLIB1054
}
