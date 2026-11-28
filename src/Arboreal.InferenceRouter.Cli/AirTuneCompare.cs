using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.Cli;

internal static class AirTuneCompare
{
    private const int MaxReportBytes = 16_384;
    private const string FileError = "error: unsupported platform or file admission facility";
    private static readonly string[] StableHardwareFields =
        ["ram_limit_bytes", "ram_plus_swap_limit_bytes", "swap_only_limit_bytes", "cpu_model",
         "cpu_affinity_sha256", "cpu_quota", "gpus"];
    internal static ReadOnlySpan<string> HardwareMatchFields => StableHardwareFields;
    private static readonly string[] EnvironmentFields = ["os", "driver", "power", "thermals", "background", "limit_source"];
    private static readonly string[] FullyMatchedGroups = ["target", "workload"];

    internal static int Run(ReadOnlySpan<string> args, TextWriter output, TextWriter error)
    {
        var (baselinePath, candidatePath) = ParseArguments(args);
        if (baselinePath is null || candidatePath is null)
        {
            error.WriteLine("usage: air tune compare --baseline FILE --candidate FILE");
            return 2;
        }

        using var proc = AirTuneFile.OpenProcDescriptors();
        return CompareFiles(proc, baselinePath, candidatePath, output, error);
    }

    internal static int CompareFiles(SafeFileHandle? proc, string baselinePath, string candidatePath,
        TextWriter output, TextWriter error)
    {
        if (proc is null)
        {
            error.WriteLine(FileError);
            return 1;
        }

        var baselineBytes = AirTuneFile.Read(proc, baselinePath);
        if (baselineBytes is null)
        {
            error.WriteLine("error: baseline input file could not be read");
            return 1;
        }
        var baseline = AirTuneRecord.Parse(baselineBytes);
        var standaloneBaseline = baseline is not null;
        baseline ??= TuneRunEvidence.ReadSummary(baselineBytes);
        if (baseline is null)
        {
            error.WriteLine("error: invalid baseline tuning summary");
            return 1;
        }

        var candidateBytes = AirTuneFile.Read(proc, candidatePath);
        if (candidateBytes is null)
        {
            error.WriteLine("error: candidate input file could not be read");
            return 1;
        }
        var candidate = AirTuneRecord.Parse(candidateBytes);
        var standaloneCandidate = candidate is not null;
        candidate ??= TuneRunEvidence.ReadSummary(candidateBytes);
        if (candidate is null)
        {
            error.WriteLine("error: invalid candidate tuning summary");
            return 1;
        }

        return WriteReport(BuildReport(baseline, candidate, standaloneBaseline && standaloneCandidate), output, error);
    }

    private static (string? Baseline, string? Candidate) ParseArguments(ReadOnlySpan<string> args)
    {
        if (args.Length != 5 || args[0] != "compare" || !PlainPath(args[2]) || !PlainPath(args[4]))
            return (null, null);
        return (args[1], args[3]) switch
        {
            ("--baseline", "--candidate") => (args[2], args[4]),
            ("--candidate", "--baseline") => (args[4], args[2]),
            _ => (null, null),
        };
    }

    private static bool PlainPath(string path)
    {
        if (path.Length == 0 || path[0] is '-' or '\\' ||
            path.StartsWith("//", StringComparison.Ordinal) || path.AsSpan().Contains('\0'))
        {
            return false;
        }

        var colon = path.AsSpan().IndexOf(':');
        var slash = path.AsSpan().IndexOf('/');
        return (colon < 0 || slash >= 0 && slash < colon) &&
            path is not ("/dev/stdin" or "/dev/fd/0" or "/proc/self/fd/0" or "/proc/thread-self/fd/0");
    }

    internal static int WriteReport(string report, TextWriter output, TextWriter error)
    {
        if (Encoding.UTF8.GetByteCount(report) > MaxReportBytes)
        {
            error.WriteLine("error: comparison report exceeds its size limit");
            return 1;
        }

        output.Write(report);
        return 0;
    }

    private static string BuildReport(AirTuneRecord baseline, AirTuneRecord candidate, bool standalone)
    {
        var report = new StringBuilder(standalone
            ? "Short-grid comparison (unverified, operator-authored observations)\n"
            : "Short-grid comparison (unverified observations)\n");
        AppendSummary(report, "baseline", baseline);
        AppendSummary(report, "candidate", candidate);

        var left = baseline.Evidence.GetProperty("hardware");
        var right = candidate.Evidence.GetProperty("hardware");
        foreach (var field in EnvironmentFields)
        {
            var relation = CompareEvidence(left.GetProperty(field), right.GetProperty(field));
            report.Append(CultureInfo.InvariantCulture, $"  {field}: {relation}\n");
        }

        var sharedMismatch = AppendDifferences(report, baseline.Evidence, candidate.Evidence);
        for (var index = 0; index < Math.Max(baseline.Cap, candidate.Cap); index++)
        {
            var before = index < baseline.Cap ? baseline.Levels[index] : null;
            var after = index < candidate.Cap ? candidate.Levels[index] : null;
            report.Append(CultureInfo.InvariantCulture, $"C={index + 1}: baseline {FormatLevel(before)}; candidate {FormatLevel(after)}");
            AppendDelta(report, before, after, sharedMismatch);
            report.Append('\n');
        }
        return report.ToString();
    }

    private static void AppendSummary(StringBuilder report, string label, AirTuneRecord record)
    {
        var instance = record.InstanceId.Value ?? "unknown";
        var hardware = record.Hardware;
        report.Append(CultureInfo.InvariantCulture, $"{label} run {record.RunId}, instance {instance}\n");
        report.Append(CultureInfo.InvariantCulture,
            $"  physical RAM bytes={FormatBytes(hardware.PhysicalRamBytes)}; physical swap bytes={FormatBytes(hardware.PhysicalSwapBytes)}\n");
        report.Append(CultureInfo.InvariantCulture,
            $"  effective RAM bytes={FormatLimit(hardware.RamLimitBytes)}; RAM-plus-swap bytes={FormatLimit(hardware.RamPlusSwapLimitBytes)}; swap-only bytes={FormatLimit(hardware.SwapOnlyLimitBytes)}\n");

        var highest = 0;
        var winner = 0;
        decimal peak = 0;
        foreach (var level in record.Levels)
        {
            if (level.Median() is not { } median)
                break;
            highest = level.Concurrency;
            if (median > peak)
            {
                peak = median;
                winner = highest;
            }
        }

        if (highest == 0)
        {
            report.Append(CultureInfo.InvariantCulture, $"  cap={record.Cap}; no reported demonstrated level\n");
            return;
        }

        var capped = highest == record.Cap ? "; search capped" : "";
        report.Append(CultureInfo.InvariantCulture,
            $"  cap={record.Cap}; highest reported demonstrated C={highest}{capped}; peak median {Rate(peak)} tokens/s at C={winner}\n");
    }

    private static string FormatBytes(TuneFact<long?> fact) =>
        fact.Value is long count ? count.ToString(CultureInfo.InvariantCulture) : "unknown";

    private static string FormatLimit(TuneLimit<long?> limit) =>
        limit.Value is long count ? count.ToString(CultureInfo.InvariantCulture) : limit.Kind;

    private static string Rate(decimal value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    private static string FormatLevel(TuneLevel? level)
    {
        if (level is null)
            return "outside cap";
        return level.Median() is { } median
            ? FormattableString.Invariant($"passed ({Rate(median)} tokens/s)")
            : FormattableString.Invariant($"{level.Status} ({level.Reason})");
    }

    private static bool AppendDifferences(StringBuilder report, JsonElement baseline, JsonElement candidate)
    {
        var leftHardware = baseline.GetProperty("hardware");
        var rightHardware = candidate.GetProperty("hardware");
        var mismatch = false;
        foreach (var field in HardwareMatchFields)
        {
            var relation = CompareEvidence(leftHardware.GetProperty(field), rightHardware.GetProperty(field));
            if (relation == "equal")
                continue;

            mismatch = true;
            var deployment = relation == "different" && field is not "cpu_model" ? " (deployment difference)" : "";
            report.Append(CultureInfo.InvariantCulture, $"  incomparable hardware.{field}: {relation}{deployment}\n");
        }

        foreach (var group in FullyMatchedGroups)
        {
            var rightGroup = candidate.GetProperty(group);
            foreach (var property in baseline.GetProperty(group).EnumerateObject())
            {
                var relation = CompareEvidence(property.Value, rightGroup.GetProperty(property.Name));
                if (relation == "equal")
                    continue;

                mismatch = true;
                report.Append(CultureInfo.InvariantCulture, $"  incomparable {group}.{property.Name}: {relation}\n");
            }
        }

        return mismatch;
    }

    private static string CompareEvidence(JsonElement before, JsonElement after)
    {
        if (HasUnknown(before) || HasUnknown(after))
            return "unknown";
        return JsonElement.DeepEquals(before, after) ? "equal" : "different";
    }

    private static bool HasUnknown(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("kind", out var kind) && kind.ValueEquals("unknown"))
                return true;
            foreach (var property in element.EnumerateObject())
            {
                if (HasUnknown(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasUnknown(item))
                    return true;
            }
        }
        return false;
    }

    private static void AppendDelta(StringBuilder report, TuneLevel? before, TuneLevel? after, bool sharedMismatch)
    {
        if (before is null || after is null || before.Median() is not { } baseline || after.Median() is not { } candidate)
        {
            report.Append("; delta unavailable (requires two passing rows)");
            return;
        }

        var corpus = CompareFingerprint(before.CorpusSha256, after.CorpusSha256);
        var body = CompareFingerprint(before.BodySha256, after.BodySha256);
        if (sharedMismatch || corpus != "equal" || body != "equal")
        {
            report.Append("; delta unavailable (incomparable");
            if (sharedMismatch)
                report.Append("; shared evidence");
            if (corpus != "equal")
                report.Append(CultureInfo.InvariantCulture, $"; corpus_sha256 {corpus}");
            if (body != "equal")
                report.Append(CultureInfo.InvariantCulture, $"; body_sha256 {body}");
            report.Append(')');
            return;
        }

        var delta = candidate - baseline;
        var sign = delta > 0 ? "+" : "";
        report.Append(CultureInfo.InvariantCulture, $"; observational delta {sign}{Rate(delta)} tokens/s");
    }

    private static string CompareFingerprint(TuneFact<string> before, TuneFact<string> after) =>
        before.Value is null || after.Value is null ? "unknown" : before.Value == after.Value ? "equal" : "different";
}
