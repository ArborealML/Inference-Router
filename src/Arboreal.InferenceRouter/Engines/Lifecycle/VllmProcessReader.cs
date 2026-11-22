using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Arboreal.InferenceRouter.Lifecycle;

internal readonly record struct VllmProcessStamp(int InitPid, ulong InitStart, int Pid, ulong Start);
internal readonly record struct VllmProcessEvidence(VllmProcessStamp Process, int Slots);

// The supported image execs one server under docker-init. A different process tree leaves capacity unknown.
internal static class VllmProcessReader
{
    private const int ProcCeiling = 4096;
    private const int ArgvCeiling = 64 * 1024;
    private const string SlotsFlag = "--max-num-seqs";
    private readonly record struct ProcessInfo(string Name, int Parent, ulong Start);

    internal static VllmProcessEvidence? Read(string procRoot, int initPid, string containerId, CancellationToken ct)
    {
        if (initPid <= 0) return null;
        try
        {
            var init = ProcessPath(procRoot, initPid);
            if (ReadStat(init, initPid, ct) is not { Name: "docker-init" } initBefore ||
                !InContainer(init, containerId, ct) || ReadOnlyChild(init, initPid, ct) is not { } pid || pid == initPid)
                return null;
            var child = ProcessPath(procRoot, pid);
            if (ReadStat(child, pid, ct) is not { Name: "vllm" } childBefore ||
                childBefore.Parent != initPid || !InContainer(child, containerId, ct))
                return null;
            var argv = ReadBounded(Path.Combine(child, "cmdline"), ArgvCeiling, ct);
            var slots = ParseServingArgv(argv);
            if (slots is null || ReadOnlyChild(init, initPid, ct) != pid ||
                ReadStat(child, pid, ct) != childBefore || ReadStat(init, initPid, ct) != initBefore ||
                !InContainer(child, containerId, ct) || !InContainer(init, containerId, ct))
                return null;
            return new(new(initPid, initBefore.Start, pid, childBefore.Start), slots.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ProcessPath(string root, int pid) => Path.Combine(root, pid.ToString(CultureInfo.InvariantCulture));

    private static int? ReadOnlyChild(string init, int initPid, CancellationToken ct)
    {
        var data = ReadBounded(Path.Combine(init, "task", initPid.ToString(CultureInfo.InvariantCulture), "children"),
            ProcCeiling, ct);
        return TryPositiveInt(data.AsSpan().Trim(" \t\n\r"u8), out var pid) ? pid : null;
    }

    private static ProcessInfo? ReadStat(string process, int pid, CancellationToken ct)
    {
        var stat = Encoding.UTF8.GetString(ReadBounded(Path.Combine(process, "stat"), ProcCeiling, ct));
        var open = stat.IndexOf('(', StringComparison.Ordinal);
        var close = stat.AsSpan().LastIndexOf(')');
        if (open <= 0 || close <= open || !int.TryParse(stat.AsSpan(0, open).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var reported) || reported != pid) return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 19 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) &&
            ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start > 0
            ? new(stat[(open + 1)..close], parent, start) : null;
    }

    private static bool InContainer(string process, string id, CancellationToken ct)
    {
        var cgroup = Encoding.UTF8.GetString(ReadBounded(Path.Combine(process, "cgroup"), ProcCeiling, ct)).TrimEnd('\n');
        return cgroup == $"0::/system.slice/docker-{id}.scope" || cgroup == $"0::/docker/{id}";
    }

    private static byte[] ReadBounded(string path, int ceiling, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var file = File.OpenRead(path);
        var buffer = new byte[ceiling + 1];
        var length = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        ct.ThrowIfCancellationRequested();
        return length > ceiling ? [] : buffer[..length];
    }

    // Null means this is not an identified serving invocation. Zero means its sequence evidence is unknown.
    internal static int? ParseServingArgv(ReadOnlySpan<byte> remaining)
    {
        if (remaining.IsEmpty || remaining[^1] != 0 || !ReadToken(ref remaining, out var executable)) return null;
        if (IsLauncherInterpreter(executable))
        {
            if (!ReadToken(ref remaining, out executable)) return null;
        }
        if (!executable.SequenceEqual("/app/venv/bin/vllm"u8) || !ReadToken(ref remaining, out var serve) ||
            !serve.SequenceEqual("serve"u8) || !ReadToken(ref remaining, out var model) || model.IsEmpty ||
            model.StartsWith("-"u8)) return null;

        var seen = false;
        var slots = 0;
        while (ReadToken(ref remaining, out var arg))
        {
            if (arg.SequenceEqual("--"u8)) return 0;
            if (!arg.StartsWith("--"u8)) continue;
            var equals = arg.IndexOf((byte)'=');
            var option = equals < 0 ? arg : arg[..equals];
            if (!option.SequenceEqual("--max-num-seqs"u8))
            {
                if (IsUnsupportedOverride(Encoding.UTF8.GetString(option), includeCanonicalSlots: false)) return 0;
                continue;
            }
            if (seen) return 0;
            seen = true;
            if (equals >= 0)
            {
                if (!TryPositiveInt(arg[(equals + 1)..], out slots)) return 0;
            }
            else if (!ReadToken(ref remaining, out var value) || !TryPositiveInt(value, out slots)) return 0;
        }
        return slots;
    }

    internal static bool IsUnsupportedOverride(string argument) => IsUnsupportedOverride(argument, includeCanonicalSlots: true);

    private static bool IsUnsupportedOverride(string argument, bool includeCanonicalSlots)
    {
        var equals = argument.IndexOf('=', StringComparison.Ordinal);
        var option = equals < 0 ? argument : argument[..equals];
        if (option == "--") return true;
        if (!option.StartsWith("--", StringComparison.Ordinal)) return false;
        var normalized = option.Replace('_', '-');
        return (includeCanonicalSlots || option != SlotsFlag) && SlotsFlag.StartsWith(normalized, StringComparison.Ordinal) ||
            "--config".StartsWith(normalized, StringComparison.Ordinal) ||
            includeCanonicalSlots && "--api-server-count".StartsWith(normalized, StringComparison.Ordinal);
    }

    private static bool IsLauncherInterpreter(ReadOnlySpan<byte> path)
    {
        if (path.SequenceEqual("/app/venv/bin/python"u8) || path.SequenceEqual("/app/venv/bin/python3"u8)) return true;
        var prefix = "/app/venv/bin/python3."u8;
        return path.StartsWith(prefix) && path.Length > prefix.Length &&
            path[prefix.Length..].IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0;
    }

    private static bool ReadToken(ref ReadOnlySpan<byte> remaining, out ReadOnlySpan<byte> token)
    {
        var end = remaining.IndexOf((byte)0);
        token = end < 0 ? default : remaining[..end];
        if (end < 0) return false;
        remaining = remaining[(end + 1)..];
        return true;
    }

    private static bool TryPositiveInt(ReadOnlySpan<byte> text, out int value) =>
        Utf8Parser.TryParse(text, out value, out var consumed) && consumed == text.Length && value > 0 &&
        text[0] is >= (byte)'0' and <= (byte)'9';
}
