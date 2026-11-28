using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using Arboreal.InferenceRouter.Lifecycle;
using Arboreal.InferenceRouter.OperationHead;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.SuccessorObserver;

// Linux x64 producer-side lifetime. The independently installed supervisor owns the
// dedicated cgroup policy and namespace. Keep its lifetime and the actual kernel objects
// pinned instead of treating the commissioned JSON or a successful daemon census as exclusion.
internal sealed class CommissionedPartitionKernelGuard : IDisposable
{
    private readonly CommissionedPartitionKernelPolicy _installed;
    private readonly SafeFileHandle _enforcer;
    private readonly SafeFileHandle _network;
    private readonly SafeFileHandle _cgroup;

    private CommissionedPartitionKernelGuard(CommissionedPartitionKernelPolicy installed, SafeFileHandle enforcer,
        SafeFileHandle network, SafeFileHandle cgroup)
    {
        _installed = installed;
        _enforcer = enforcer;
        _network = network;
        _cgroup = cgroup;
    }

    private const string CgroupRoot = "/sys/fs/cgroup";
    private const int BpfSyscall = 321;
    private const int BpfProgramQuery = 16;
    private const uint QueryEffective = 1;
    private const uint IngressAttach = 0;
    private const uint EgressAttach = 1;
    private const uint DeviceAttach = 6;
    private const int MaxPrograms = 16;

    internal static CommissionedPartitionKernelGuard? Open(CommissionedPartitionKernelPolicy policy)
    {
        if (!OperationHeadLinux.Supported || !IsValid(policy) ||
            !Guid.TryParse(File.ReadAllText("/proc/sys/kernel/random/boot_id"), out var boot) || boot != policy.BootId)
            return null;

        SafeFileHandle? process = null;
        SafeFileHandle? network = null;
        SafeFileHandle? cgroup = null;
        var transferred = false;
        try
        {
#pragma warning disable CA2000, IDISP001 // The returned guard owns all three handles. The finally disposes failed acquisitions.
            process = CommissionedServingPins.TryPin(policy.Enforcer);
            if (process is null) return null;
            network = File.OpenHandle(FormattableString.Invariant($"/proc/{policy.Enforcer.ProcessId}/ns/net"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            cgroup = OperationHeadLinux.OpenDirectory(policy.Cgroup);
#pragma warning restore CA2000, IDISP001
            if (!MatchesObjects(policy, process, network, cgroup)) return null;
            var guard = new CommissionedPartitionKernelGuard(policy, process, network, cgroup);
            transferred = true;
            return guard;
        }
        finally
        {
            if (!transferred)
            {
                cgroup?.Dispose();
                network?.Dispose();
                process?.Dispose();
            }
        }
    }

    internal bool IsCurrent(CommissionedPartitionPolicy policy)
    {
        var current = policy.Kernel;
        return current is not null && current.BootId == _installed.BootId && current.Enforcer == _installed.Enforcer &&
            current.NetworkNamespace == _installed.NetworkNamespace && current.Cgroup == _installed.Cgroup &&
            current.CgroupIdentity == _installed.CgroupIdentity && IsValid(current) &&
            current.DevicePrograms.SequenceEqual(_installed.DevicePrograms) &&
            current.IngressPrograms.SequenceEqual(_installed.IngressPrograms) &&
            current.EgressPrograms.SequenceEqual(_installed.EgressPrograms) &&
            MatchesObjects(_installed, _enforcer, _network, _cgroup);
    }

    private static bool MatchesObjects(CommissionedPartitionKernelPolicy policy, SafeFileHandle enforcer,
        SafeFileHandle network, SafeFileHandle cgroup)
    {
        if (!CommissionedServingPins.MatchesLifetime(policy.Enforcer, enforcer) ||
            !CommissionedServingPins.HasNamespace(policy.Enforcer, policy.NetworkNamespace) ||
            !LinuxServingAcceptedSocket.MatchesNamespace(network, policy.NetworkNamespace) ||
            !LinuxServingAcceptedSocket.MatchesNamespace(cgroup, policy.CgroupIdentity) ||
            !MatchesMember(policy.Enforcer, policy))
            return false;
        using var current = OperationHeadLinux.OpenDirectory(policy.Cgroup);
        return LinuxServingAcceptedSocket.MatchesNamespace(current, policy.CgroupIdentity) &&
            QueryPrograms(cgroup, DeviceAttach, policy.DevicePrograms) &&
            QueryPrograms(cgroup, IngressAttach, policy.IngressPrograms) &&
            QueryPrograms(cgroup, EgressAttach, policy.EgressPrograms) &&
            CommissionedServingPins.MatchesLifetime(policy.Enforcer, enforcer);
    }

    internal bool IsServingCurrent(HeldServingGeneration generation) =>
        generation.Exclusion.Namespace == _installed.NetworkNamespace &&
        MatchesMember(generation.Exclusion.Handler, _installed) && MatchesMember(generation.Exclusion.Worker, _installed);

    internal bool TryGetMemberCgroup(ServingHostLifetime process, out string path)
    {
        path = "";
        if (ReadCgroupPath(process) is not { } before || !MatchesMember(process, _installed) ||
            ReadCgroupPath(process) != before) return false;
        path = before;
        return true;
    }

    private static bool MatchesMember(ServingHostLifetime process, CommissionedPartitionKernelPolicy policy)
    {
        using var lifetime = CommissionedServingPins.TryPin(process);
        var path = lifetime is null ? null : ReadCgroupPath(process);
        if (lifetime is null || path is null ||
            (path != policy.Cgroup && !path.StartsWith(policy.Cgroup + "/", StringComparison.Ordinal)) ||
            !CommissionedServingPins.HasNamespace(process, policy.NetworkNamespace))
            return false;

        using var member = OperationHeadLinux.OpenDirectory(path);
        return QueryPrograms(member, DeviceAttach, policy.DevicePrograms) &&
            QueryPrograms(member, IngressAttach, policy.IngressPrograms) &&
            QueryPrograms(member, EgressAttach, policy.EgressPrograms) &&
            ReadCgroupPath(process) == path && CommissionedServingPins.MatchesLifetime(process, lifetime);
    }

    private static string? ReadCgroupPath(ServingHostLifetime process)
    {
        var line = File.ReadLines(FormattableString.Invariant($"/proc/{process.ProcessId}/cgroup"))
            .SingleOrDefault(static value => value.StartsWith("0::/", StringComparison.Ordinal));
        if (line is null) return null;
        var path = CgroupRoot + line[3..];
        return Path.GetFullPath(path) == path ? path : null;
    }

    private static bool IsValid(CommissionedPartitionKernelPolicy policy) =>
        policy.BootId != Guid.Empty && policy.Enforcer is { ProcessId: > 0, StartTicks: > 0 } &&
        policy.NetworkNamespace is { Inode: > 0 } && policy.CgroupIdentity is { Inode: > 0 } &&
        policy.Cgroup.StartsWith(CgroupRoot + "/", StringComparison.Ordinal) && Path.GetFullPath(policy.Cgroup) == policy.Cgroup &&
        policy.Cgroup is [.., not '/'] && ValidPrograms(policy.DevicePrograms) &&
        ValidPrograms(policy.IngressPrograms) && ValidPrograms(policy.EgressPrograms);

    private static bool ValidPrograms(ImmutableArray<uint> programs) =>
        !programs.IsDefaultOrEmpty && programs.Length <= MaxPrograms &&
        programs.All(static id => id != 0) && programs.Distinct().Count() == programs.Length;

    private static unsafe bool QueryPrograms(SafeFileHandle cgroup, uint attachType, ImmutableArray<uint> expected)
    {
        Span<uint> actual = stackalloc uint[MaxPrograms];
        var added = false;
        try
        {
            cgroup.DangerousAddRef(ref added);
            fixed (uint* buffer = actual)
            {
                var query = new BpfQuery
                {
                    Target = unchecked((uint)cgroup.DangerousGetHandle().ToInt32()),
                    AttachType = attachType,
                    Flags = QueryEffective,
                    ProgramIds = (ulong)buffer,
                    Count = MaxPrograms,
                };
                return Bpf(BpfSyscall, BpfProgramQuery, ref query, (uint)sizeof(BpfQuery)) == 0 &&
                    query.Count == expected.Length && actual[..expected.Length].SequenceEqual(expected.AsSpan());
            }
        }
        finally
        {
            if (added) cgroup.DangerousRelease();
        }
    }

    public void Dispose()
    {
        _cgroup.Dispose();
        _network.Dispose();
        _enforcer.Dispose();
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct BpfQuery
    {
        [FieldOffset(0)] internal uint Target;
        [FieldOffset(4)] internal uint AttachType;
        [FieldOffset(8)] internal uint Flags;
        [FieldOffset(16)] internal ulong ProgramIds;
        [FieldOffset(24)] internal uint Count;
    }

#pragma warning disable SYSLIB1054 // Fixed Linux x64 syscall ABI and blittable query fields.
    [DllImport("libc.so.6", EntryPoint = "syscall", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long Bpf(long syscall, int command, ref BpfQuery query, uint size);
#pragma warning restore SYSLIB1054
}
