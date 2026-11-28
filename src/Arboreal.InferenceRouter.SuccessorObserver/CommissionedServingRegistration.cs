using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Arboreal.InferenceRouter.Lifecycle;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.SuccessorObserver;

// The serving registration is derived from the actual daemon-owned container, the
// commissioned exact process recipes, and the kernel listener in the held scope.
internal sealed class CommissionedServingRegistration(EnrolledSuccessorScope scope,
    DockerLifecycleProvider.SuccessorCensus census, EnrolledSuccessorFileJournal journal,
    CommissionedPartitionKernelGuard kernel)
{
    private const int MaxProcesses = 128;
    private const int MaxDescriptors = 1024;
    private const int MaxArguments = 64 * 1024;
    private const int MaxExecutable = 128 * 1024 * 1024;
    private const int PidFdGetFd = 438;

    internal async ValueTask<HeldServingGeneration?> CaptureAsync(CommissionedPartitionPolicyRead policyRead,
        HostEffectCheckpointCommitment installed, ServingBrokerRecord broker, HostCheckpointLaunchEffect resident,
        DockerImageIdentity image, CancellationToken ct)
    {
        var current = broker.Binding;
        if (current.Applied.State.AppliedLaunch == resident)
            return await CaptureBindingAsync(policyRead, current.Applied, resident, image, current.Route.RouteIncarnation,
                current.AppliedJournalDigest, current.Route, ct).ConfigureAwait(false);

        if (installed.State is not { Phase: HostCheckpointPhase.Resident, Epoch: > 1 } ||
            broker is not { Open: false, Close.Conclusive: true } ||
            journal.ReadAppliedDigestForServing(resident) is not { } appliedDigest)
            return null;

        var retained = HostClosedRouteStageFile.Read(scope.Reservation.Slot)?.Route.RouteIncarnation ?? 0;
        var prior = Math.Max(current.Route.RouteIncarnation, retained);
        if (prior >= long.MaxValue) return null;
        return await CaptureBindingAsync(policyRead, installed, resident, image, prior + 1,
            appliedDigest, expectedRoute: null, ct).ConfigureAwait(false);
    }

    internal async ValueTask<HeldServingGeneration?> CaptureBindingAsync(CommissionedPartitionPolicyRead policyRead,
        HostEffectCheckpointCommitment applied, HostCheckpointLaunchEffect resident, DockerImageIdentity image,
        long routeGeneration, string? journalDigest, VerifiedInstanceRoute? expectedRoute, CancellationToken ct)
    {
        var policy = policyRead.Policy;
        if (!kernel.IsCurrent(policy) || policy.ServingRecipes.IsDefaultOrEmpty ||
            !scope.Commission.Approvals.Any(approval => approval.Launch.Intent.Plan == resident.Plan && approval.Launch.Image == image))
            return null;

        var key = new EnrolledPlanKey(resident.Plan.Engine, resident.Plan.Model, resident.Plan.Tier ?? "");
        var recipes = policy.ServingRecipes.Where(candidate => candidate.Target == key &&
            candidate.PlanDigest == resident.Operation.PlanDigest).Take(2).ToArray();
        if (recipes is not [var recipe] || !HostMutation.IsPlanDigest(recipe.PolicyDigest) ||
            !HostMutation.IsPlanDigest(recipe.Handler.ExecutableSha256) || !HostMutation.IsPlanDigest(recipe.Worker.ExecutableSha256) ||
            recipe.Profile.CanonicalModel != resident.Plan.Model || recipe.Profile.Profile != resident.Plan.Tier ||
            recipe.Profile.Context <= 0 || recipe.Profile.Slots <= 0 ||
            await census.ReadRunningInitPidAsync(scope.Commission.Head.RouterId, resident, image, ct).ConfigureAwait(false)
                is not { } init)
            return null;

        if (!ReadLifetime(init, out var initLifetime)) return null;
        using var initPin = CommissionedServingPins.TryPin(initLifetime);
        if (initPin is null || !kernel.TryGetMemberCgroup(initLifetime, out var containerCgroup))
            return null;

        var members = await ReadMembersAsync(Path.Combine(containerCgroup, "cgroup.procs"), ct).ConfigureAwait(false);
        if (members is null) return null;

        ServingHostLifetime? handler = null;
        ServingHostLifetime? worker = null;
        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            if (!int.TryParse(member, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                !ReadLifetime(pid, out var lifetime)) return null;
            if (MatchesProcess(lifetime, recipe.Handler))
            {
                if (handler is not null) return null;
                handler = lifetime;
            }
            if (MatchesProcess(lifetime, recipe.Worker))
            {
                if (worker is not null) return null;
                worker = lifetime;
            }
        }

        if (handler is not { } exactHandler || worker is not { } exactWorker ||
            !kernel.TryGetMemberCgroup(exactHandler, out var handlerCgroup) || handlerCgroup != containerCgroup ||
            !kernel.TryGetMemberCgroup(exactWorker, out var workerCgroup) || workerCgroup != containerCgroup)
            return null;

        using var handlerPin = CommissionedServingPins.TryPin(exactHandler);
        using var workerPin = CommissionedServingPins.TryPin(exactWorker);
        if (handlerPin is null || workerPin is null ||
            ReadListener(exactHandler, handlerPin, recipe.Listener, ct) is not { } listener ||
            !SoleListener(members, exactHandler, listener, ct) ||
            await census.ReadRunningInitPidAsync(scope.Commission.Head.RouterId, resident, image, ct).ConfigureAwait(false) != init ||
            !CommissionedServingPins.MatchesLifetime(initLifetime, initPin) ||
            !CommissionedServingPins.MatchesLifetime(exactHandler, handlerPin) ||
            !CommissionedServingPins.MatchesLifetime(exactWorker, workerPin) ||
            !LinuxServingAcceptedSocket.MatchesListener(listener, handlerPin) || !kernel.IsCurrent(policy))
            return null;

        var kernelPolicy = policy.Kernel;
        var servingIncarnation = Digest(FormattableString.Invariant(
            $"{kernelPolicy.BootId:N}/{kernelPolicy.NetworkNamespace.Inode}/{resident.InstanceId}/{exactHandler.ProcessId}/{exactHandler.StartTicks}/{exactWorker.ProcessId}/{exactWorker.StartTicks}/{listener.Inode}/{listener.Cookie}"));
        var route = new VerifiedInstanceRoute(scope.Commission.Head.Partition, scope.Commission.Head.Generation,
            resident.Plan.Engine, recipe.PlanDigest, resident.InstanceId, servingIncarnation, resident.Operation.Epoch, routeGeneration);
        if (expectedRoute is { } expected && route != expected) return null;

        var identity = new ServingBackendIdentity(scope.Reservation, scope.Commission.Head, resident.Operation,
            applied.Digest, route, recipe.PolicyDigest, journalDigest);
        var exclusionDigest = Digest(FormattableString.Invariant(
            $"{policyRead.Digest}/{resident.Operation.Id:N}/{applied.Digest}/{journalDigest}/{servingIncarnation}/{routeGeneration}/{listener.Number}"));
        return new(new(identity, kernelPolicy.NetworkNamespace, exactHandler, exactWorker, recipe.Endpoint, listener,
            applied.State.Revision, exclusionDigest), recipe.Profile);
    }

    private static async Task<string[]?> ReadMembersAsync(string path, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        var members = new List<string>();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } member)
        {
            if (members.Count == MaxProcesses) return null;
            members.Add(member);
        }
        return members.Count == 0 ? null : [.. members];
    }

    private static bool SoleListener(string[] members, ServingHostLifetime handler, ServingHostListenerDescriptor listener,
        CancellationToken ct)
    {
        var expected = FormattableString.Invariant($"socket:[{listener.Inode}]");
        foreach (var member in members)
        {
            if (!int.TryParse(member, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return false;
            var files = Directory.EnumerateFileSystemEntries(ProcFile(pid, "fd")).Take(MaxDescriptors + 1).ToArray();
            if (files.Length > MaxDescriptors) return false;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (new FileInfo(file).LinkTarget == expected && (pid != handler.ProcessId ||
                        Path.GetFileName(file) != listener.Number.ToString(CultureInfo.InvariantCulture)))
                    return false;
            }
        }
        return true;
    }

    private static ServingHostListenerDescriptor? ReadListener(ServingHostLifetime handler, SafeFileHandle pidfd,
        ServingHostAddress expected, CancellationToken ct)
    {
        ServingHostListenerDescriptor? found = null;
        var files = Directory.EnumerateFileSystemEntries(ProcFile(handler.ProcessId, "fd")).Take(MaxDescriptors + 1).ToArray();
        if (files.Length > MaxDescriptors) return null;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (new FileInfo(file).LinkTarget?.StartsWith("socket:[", StringComparison.Ordinal) != true ||
                !int.TryParse(Path.GetFileName(file), NumberStyles.None, CultureInfo.InvariantCulture, out var fd))
                continue;
            using var duplicate = new SafeFileHandle((nint)Duplicate(PidFdGetFd, pidfd, fd, 0), ownsHandle: true);
            if (duplicate.IsInvalid || LinuxServingAcceptedSocket.ReadIdentity(duplicate) is not { } actual)
                continue;

            var listener = new ServingHostListenerDescriptor(fd, expected, actual.Inode, actual.Cookie);
            if (!LinuxServingAcceptedSocket.MatchesListener(listener, pidfd)) continue;
            if (found is not null) return null;
            found = listener;
        }
        return found;
    }

    private static bool MatchesProcess(ServingHostLifetime lifetime, CommissionedServingProcessRecipe recipe)
    {
        if (recipe.Arguments.IsDefaultOrEmpty) return false;
        var uid = File.ReadLines(ProcFile(lifetime.ProcessId, "status"))
            .FirstOrDefault(static line => line.StartsWith("Uid:", StringComparison.Ordinal))?
            .Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (uid is not { Length: >= 3 } ||
            !uint.TryParse(uid[2], NumberStyles.None, CultureInfo.InvariantCulture, out var effective) || effective != recipe.Uid)
            return false;

        using var cmdline = File.OpenRead(ProcFile(lifetime.ProcessId, "cmdline"));
        var bytes = new byte[MaxArguments + 1];
        var length = cmdline.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (length == 0 || length > MaxArguments || bytes[length - 1] != 0 ||
            !Encoding.UTF8.GetString(bytes, 0, length - 1).Split('\0').SequenceEqual(recipe.Arguments, StringComparer.Ordinal))
            return false;

        using var executable = File.OpenRead(ProcFile(lifetime.ProcessId, "exe"));
        return executable.Length is > 0 and <= MaxExecutable &&
            Convert.ToHexString(SHA256.HashData(executable)) == recipe.ExecutableSha256;
    }

    private static bool ReadLifetime(int pid, out ServingHostLifetime lifetime)
    {
        lifetime = new(pid, 0);
        if (pid <= 0) return false;
        var stat = File.ReadAllText(ProcFile(pid, "stat"));
        var end = stat.AsSpan().LastIndexOf(')');
        if (end < 0) return false;
        var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || fields[0] is "Z" or "X" ||
            !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var started) || started == 0)
            return false;

        lifetime = new(pid, started);
        return true;
    }

    private static string ProcFile(int pid, string child) => FormattableString.Invariant($"/proc/{pid}/{child}");
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

#pragma warning disable SYSLIB1054 // Fixed Linux x64 pidfd_getfd ABI with a pinned pidfd argument.
    [DllImport("libc.so.6", EntryPoint = "syscall", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long Duplicate(long syscall, SafeFileHandle pidfd, int fd, uint flags);
#pragma warning restore SYSLIB1054
}
