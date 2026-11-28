using System.Text.Json;

namespace Arboreal.InferenceRouter.Cli;

internal static class AirTuneRun
{
    private const string Usage = "usage: air tune run [--config FILE] --engine ENGINE --model MODEL --profile TIER " +
        "--seed HEX --max-concurrency 1 --host-record FILE --out FILE";

    internal static async Task<int> RunAsync(string[] args, ITuneAdmission? admission, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        RunArguments? options;
        try
        {
            options = ParseArguments(args);
        }
        catch (RouterConfigException)
        {
            return Error(error, "error: could not locate the user home directory; specify --config FILE");
        }
        if (options is null) return Error(error, Usage, 2);

        using var proc = AirTuneFile.OpenProcDescriptors();
        if (proc is null) return Error(error, "error: unsupported platform or file admission facility");
        var workload = options.Workload;
        var configBytes = AirTuneFile.Read(proc, workload.Path);
        if (configBytes is null) return Error(error, "error: tuning config file could not be read");
        var config = AirTuneWorkloadPlan.ParseConfig(configBytes, workload.Path);
        if (config is null) return Error(error, "error: invalid tuning workload config");
        var manifest = AirTuneWorkloadPlan.Build(config, workload.Engine, workload.Model, workload.Profile, workload.Seed, workload.Cap);
        if (manifest is null) return Error(error, "error: invalid tuning workload target or tier");
        var host = ReadHost(AirTuneFile.Read(proc, options.HostPath));
        if (host is null) return Error(error, "error: invalid tuning host record");

        using var directory = AirTuneFile.PinOutputDirectory(options.OutputPath);
        if (directory is null) return Error(error, "error: tuning output directory could not be opened");
        var name = Path.GetFileName(options.OutputPath);
        var summaryName = Path.GetFileNameWithoutExtension(name) + ".summary.json";
        using var output = AirTuneFile.CreateOutput(directory, name);
        using var summary = name == summaryName ? null : AirTuneFile.CreateOutput(directory, summaryName);
        if (output is null || summary is null) return Error(error, "error: tuning output path must be new and supported");

        var result = await AirTuneSingleModel.MeasureAsync(admission, config.Engines[manifest.Engine], manifest,
            host.RecipeSha256, cancellationToken).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, AirTuneRunJsonContext.Default.TuneRunEvidence);
        if (bytes.Length >= AirTuneRecord.MaxBytes || !AirTuneFile.InstallOutput(proc, directory, output, name, bytes))
            return Error(error, "error: tuning run could not be saved");

        if (result.Summary is not null)
        {
            var summaryBytes = JsonSerializer.SerializeToUtf8Bytes(result.Summary, AirTuneRunJsonContext.Default.AirTuneRecord);
            if (summaryBytes.Length >= AirTuneRecord.MaxBytes || AirTuneRecord.Parse(summaryBytes) is null ||
                !AirTuneFile.InstallOutput(proc, directory, summary, summaryName, summaryBytes))
                return Error(error, "error: tuning summary could not be saved");
        }

        return result.Status == "passed" ? 0 : Error(error, "error: tuning run " + result.Status + " (" + result.Reason + ")");
    }

    private static TuneHostRecord? ReadHost(byte[]? bytes)
    {
        if (bytes is null || bytes.Length > AirTuneRecord.MaxBytes) return null;
        try
        {
            var record = JsonSerializer.Deserialize(bytes, AirTuneRunJsonContext.Default.TuneHostRecord);
            return record is { SchemaVersion: 1, RecipeSha256: { } digest } && AirTuneRecord.IsDigest(digest) ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RunArguments? ParseArguments(string[] args)
    {
        if (args.Length is not (14 or 16)) return null;
        string? host = null, output = null;
        var remaining = new List<string>();
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            var value = args[index + 1];
            if (name is "--host-record" or "--out")
            {
                if (!AirTuneWorkloadPlan.IsDirectPath(value) || value.AsSpan().Contains('\0')) return null;
                if (name == "--host-record" && host is null) host = value;
                else if (name == "--out" && output is null) output = value;
                else return null;
            }
            else
            {
                remaining.Add(name);
                remaining.Add(value);
            }
        }

        if (host is null || output is null || AirTuneWorkloadPlan.ParseArguments([.. remaining]) is not { Cap: 1, Profile: not null } workload)
            return null;
        return new(workload, host, output);
    }

    private static int Error(TextWriter error, string message, int status = 1)
    {
        error.WriteLine(message);
        return status;
    }

    private sealed record RunArguments(AirTuneWorkloadPlan.PlanArguments Workload, string HostPath, string OutputPath);
}
