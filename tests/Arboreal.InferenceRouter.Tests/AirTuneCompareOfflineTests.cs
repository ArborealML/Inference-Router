using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Arboreal.InferenceRouter.Cli;
using Microsoft.Win32.SafeHandles;
using static Arboreal.InferenceRouter.Tests.CliCompletionProcessTests;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class AirTuneCompareOfflineTests : IDisposable
{
    private const string InvalidCandidate = "error: invalid candidate tuning summary";
    private const string BadCandidatePath = "error: candidate input file could not be read";
    private const string UnsupportedFacility = "error: unsupported platform or file admission facility";
    private const string TokenCanary = "redaction/canary:secret+observation";
    private const string PathCanary = "private-path-canary";
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(8);
    private static readonly string Baseline = Fixture("baseline.json");
    private static readonly string Candidate = Fixture("candidate.json");
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("air-tune-");

    private static bool LinuxX64 => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [Fact(Timeout = 30_000)]
    public async Task GoldenShowsMatchedDeltaIndependentPeaksAndUntested()
    {
        if (!LinuxX64)
            return;

        var result = await RunPairAsync();
        var report = AssertSuccess(result);
        Assert.Contains("unverified, operator-authored observations", report, StringComparison.Ordinal);
        Assert.Contains("baseline run 11111111111111111111111111111111", report, StringComparison.Ordinal);
        Assert.Contains("candidate run 22222222222222222222222222222222", report, StringComparison.Ordinal);
        Assert.Contains("physical RAM bytes=68719476736", report, StringComparison.Ordinal);
        Assert.Contains("physical RAM bytes=137438953472", report, StringComparison.Ordinal);
        Assert.Contains("effective RAM bytes=57982058496; RAM-plus-swap bytes=60129542144; swap-only bytes=2147483648",
            report, StringComparison.Ordinal);
        Assert.Contains("driver: different", report, StringComparison.Ordinal);
        Assert.Contains("highest reported demonstrated C=2; peak median 8 tokens/s at C=2", report, StringComparison.Ordinal);
        Assert.Contains("highest reported demonstrated C=4; search capped; peak median 14 tokens/s at C=4", report, StringComparison.Ordinal);
        Assert.Contains("body_sha256 different", Row(report, 1), StringComparison.Ordinal);
        Assert.Contains("baseline passed (8 tokens/s); candidate passed (9 tokens/s); observational delta +1 tokens/s",
            Row(report, 2), StringComparison.Ordinal);
        Assert.Contains("baseline failed", Row(report, 3), StringComparison.Ordinal);
        Assert.Contains("baseline untested", Row(report, 4), StringComparison.Ordinal);
        Assert.Single(report.Split('\n'), line => line.Contains("observational delta", StringComparison.Ordinal));
    }

    [Theory(Timeout = 30_000)]
    [InlineData("outside")]
    [InlineData("tie")]
    [InlineData("untested")]
    public async Task CoverageAndTieControls(string mutation)
    {
        if (!LinuxX64)
            return;

        var left = Parse(Baseline);
        var right = Parse(Candidate);
        var levels = left["levels"]!.AsArray();
        switch (mutation)
        {
            case "outside":
                left["cap"] = 2;
                levels.RemoveAt(3);
                levels.RemoveAt(2);
                break;
            case "tie":
                right["levels"]![1]!["wave_output_tokens_per_second"] = JsonNode.Parse("[15,13,14]");
                break;
            default:
                left["target"]!["observed_slots"]!["value"] = 0;
                foreach (var item in levels)
                {
                    var row = item!.AsObject();
                    row["status"] = "untested";
                    row["reason"] = "preflight_refusal";
                    row["corpus_sha256"] = Unknown();
                    row["body_sha256"] = Unknown();
                    row.Remove("wave_output_tokens_per_second");
                }
                break;
        }

        var report = AssertSuccess(await RunPairAsync(left.ToJsonString(), right.ToJsonString()));
        switch (mutation)
        {
            case "outside":
                Assert.Contains("baseline outside cap; candidate passed", Row(report, 3), StringComparison.Ordinal);
                Assert.Contains("baseline outside cap; candidate passed", Row(report, 4), StringComparison.Ordinal);
                Assert.Contains("delta unavailable", Row(report, 4), StringComparison.Ordinal);
                break;
            case "tie":
                Assert.Contains("search capped; peak median 14 tokens/s at C=2", report, StringComparison.Ordinal);
                break;
            default:
                Assert.Contains("cap=4; no reported demonstrated level", report, StringComparison.Ordinal);
                Assert.Contains("baseline untested (preflight_refusal)", Row(report, 1), StringComparison.Ordinal);
                Assert.DoesNotContain("observational delta", report, StringComparison.Ordinal);
                break;
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData("finite")]
    [InlineData("unlimited-unknown")]
    [InlineData("equal-unlimited")]
    [InlineData("equal-unknown")]
    public async Task LimitAndIdentityEvidenceAreMatchedExactly(string mutation)
    {
        if (!LinuxX64)
            return;

        var left = Parse(Baseline);
        var right = Parse(Candidate);
        switch (mutation)
        {
            case "finite":
                right["hardware"]!["ram_limit_bytes"]!["value"] = 60_000_000_000L;
                break;
            case "unlimited-unknown":
                left["hardware"]!["ram_limit_bytes"] = Unlimited();
                right["hardware"]!["ram_limit_bytes"] = Unknown();
                break;
            case "equal-unlimited":
                left["hardware"]!["ram_limit_bytes"] = Unlimited();
                right["hardware"]!["ram_limit_bytes"] = Unlimited();
                break;
            default:
                left["target"]!["runtime_build"] = Unknown();
                right["target"]!["runtime_build"] = Unknown();
                break;
        }

        var report = AssertSuccess(await RunPairAsync(left.ToJsonString(), right.ToJsonString()));
        Assert.Contains("baseline passed (8 tokens/s); candidate passed (9 tokens/s)", Row(report, 2), StringComparison.Ordinal);
        Assert.Contains("peak median 14 tokens/s at C=4", report, StringComparison.Ordinal);
        Assert.Contains("body_sha256 different", Row(report, 1), StringComparison.Ordinal);
        if (mutation == "equal-unlimited")
        {
            Assert.Contains("observational delta +1 tokens/s", Row(report, 2), StringComparison.Ordinal);
            return;
        }

        var evidence = mutation switch
        {
            "finite" => "hardware.ram_limit_bytes: different (deployment difference)",
            "unlimited-unknown" => "hardware.ram_limit_bytes: unknown",
            _ => "target.runtime_build: unknown",
        };
        Assert.Contains(evidence, report, StringComparison.Ordinal);
        Assert.DoesNotContain("observational delta", report, StringComparison.Ordinal);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("unmapped")]
    [InlineData("case-shadow")]
    [InlineData("required")]
    [InlineData("row")]
    [InlineData("after-failure")]
    [InlineData("wave-count")]
    [InlineData("zero-rate")]
    [InlineData("size")]
    [InlineData("unknown-payload")]
    [InlineData("unlimited-payload")]
    [InlineData("null")]
    [InlineData("raw-rate")]
    [InlineData("raw-integer")]
    [InlineData("utf8")]
    [InlineData("bom")]
    public async Task InvalidCandidateLeavesNoPartialReport(string mutation)
    {
        if (!LinuxX64)
            return;

        var bytes = Malformed(mutation);
        var result = await RunPairAsync(candidateBytes: bytes);
        AssertError(result, 1, InvalidCandidate);
    }

    [Fact(Timeout = 30_000)]
    public async Task ValidReportAndFailureRedactPathsAndRecordTokens()
    {
        if (!LinuxX64)
            return;

        var right = Parse(Candidate);
        right["hardware"]!["driver"]!["value"] = TokenCanary;
        var valid = await RunPairAsync(candidate: right.ToJsonString(), candidateName: PathCanary);
        Assert.Contains("driver: different", AssertSuccess(valid), StringComparison.Ordinal);
        AssertRedacted(valid);

        right["schema_version"] = 2;
        var invalid = await RunPairAsync(candidate: right.ToJsonString(), candidateName: PathCanary);
        AssertError(invalid, 1, InvalidCandidate);
        AssertRedacted(invalid);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("fifo")]
    [InlineData("symlink")]
    [InlineData("proc")]
    [InlineData("device")]
    public async Task SpecialInputFailsWithoutPartialOutputOrWaitingForAWriter(string kind)
    {
        if (!LinuxX64)
            return;

        var path = Path.Combine(_directory.FullName, "fifo");
        if (kind is "fifo" or "symlink")
            Assert.Equal(0, MakeFifo(path, 0x180));
        if (kind == "symlink")
        {
            var link = Path.Combine(_directory.FullName, "alias");
            File.CreateSymbolicLink(link, path);
            path = link;
        }
        else if (kind == "proc")
        {
            path = FormattableString.Invariant($"/proc/{Environment.ProcessId}/status");
        }
        else if (kind == "device")
        {
            path = "/dev/null";
        }

        var baselinePath = Path.Combine(_directory.FullName, "baseline.json");
        using var deadline = new CancellationTokenSource(RunLimit);
        await File.WriteAllTextAsync(baselinePath, Baseline, deadline.Token);
        AssertError(await RunCliAsync("tune", ["compare", "--baseline", baselinePath, "--candidate", path],
            null, deadline.Token), 1, BadCandidatePath);
    }

    [Fact]
    public void DescriptorAdmissionUsesPathOnlyAndReopensTheRenamedInode()
    {
        if (!LinuxX64)
            return;

        using var proc = AirTuneFile.OpenProcDescriptors();
        Assert.NotNull(proc);
        var path = Path.Combine(_directory.FullName, "original");
        File.WriteAllText(path, "original bytes A");
        using var metadata = AirTuneFile.PinPath(path);
        Assert.NotNull(metadata);
        AssertPathOnly(metadata);
        var identity = AirTuneFile.InspectRegular(metadata);
        Assert.NotNull(identity);
        File.Move(path, Path.Combine(_directory.FullName, "renamed"));
        File.WriteAllText(path, "replacement bytes B");
        using (var data = AirTuneFile.Reopen(proc, metadata, identity.Value))
        {
            Assert.NotNull(data);
            Assert.Equal(identity, AirTuneFile.InspectRegular(data));
            using var stream = new FileStream(data, FileAccess.Read, bufferSize: 1);
            using var reader = new StreamReader(stream);
            Assert.Equal("original bytes A", reader.ReadToEnd());
        }

        using var removed = AirTuneFile.PinPath(path);
        Assert.NotNull(removed);
        var removedIdentity = AirTuneFile.InspectRegular(removed);
        Assert.NotNull(removedIdentity);
        File.Delete(path);
        using var refused = AirTuneFile.Reopen(proc, removed, removedIdentity.Value);
        Assert.Null(refused);

        Assert.Equal(0, MakeFifo(path, 0x180));
        using var fifo = AirTuneFile.PinPath(path);
        Assert.NotNull(fifo);
        AssertPathOnly(fifo);
        Assert.Null(AirTuneFile.InspectRegular(fifo));
    }

    [Fact]
    public void UnavailableAdmissionFacilityFailsBeforeOpeningEitherInput()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(1, AirTuneCompare.CompareFiles(null, PathCanary, PathCanary, output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal(UnsupportedFacility + Environment.NewLine, error.ToString());
    }

    [Fact(Timeout = 30_000)]
    public async Task UnsupportedPlatformProcessReportsFacilityWithoutFileAccess()
    {
        if (LinuxX64)
            return;

        using var deadline = new CancellationTokenSource(RunLimit);
        var result = await RunCliAsync("tune", ["compare", "--baseline", PathCanary, "--candidate", PathCanary],
            null, deadline.Token);
        AssertError(result, 1, UnsupportedFacility);
    }

    [Fact]
    public void Utf8ReportLimitIsCheckedBeforeWriting()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var atLimit = new string('\u00e9', 8192);
        Assert.Equal(1, AirTuneCompare.WriteReport(atLimit + "x", output, error));
        Assert.Equal("", output.ToString());
        Assert.Contains("exceeds its size limit", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, AirTuneCompare.WriteReport(atLimit, output, error));
        Assert.Equal(atLimit, output.ToString());
    }

    [Theory(Timeout = 30_000)]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("stdin")]
    [InlineData("url")]
    public async Task StrictArgumentsFailBeforeFileAdmission(string kind)
    {
        string[] args = kind switch
        {
            "duplicate" => ["compare", "--baseline", PathCanary, "--baseline", PathCanary],
            "unknown" => ["compare", "--baseline", PathCanary, "--other", PathCanary],
            "missing" => ["compare", "--baseline", PathCanary, "--candidate"],
            "extra" => ["compare", "--baseline", PathCanary, "--candidate", PathCanary, "extra"],
            "stdin" => ["compare", "--baseline", PathCanary, "--candidate", "-"],
            _ => ["compare", "--baseline", PathCanary, "--candidate", "https://fixture.invalid/"],
        };
        using var deadline = new CancellationTokenSource(RunLimit);
        AssertError(await RunCliAsync("tune", args, null, deadline.Token), 2,
            "usage: air tune compare --baseline FILE --candidate FILE");
    }

    private async Task<ProcessResult> RunPairAsync(string? baseline = null, string? candidate = null,
        byte[]? candidateBytes = null, string candidateName = "candidate.json")
    {
        var left = Path.Combine(_directory.FullName, "baseline.json");
        var right = Path.Combine(_directory.FullName, candidateName);
        using var deadline = new CancellationTokenSource(RunLimit);
        await File.WriteAllTextAsync(left, baseline ?? Baseline, deadline.Token);
        await File.WriteAllBytesAsync(right,
            candidateBytes ?? Encoding.UTF8.GetBytes(candidate ?? Candidate), deadline.Token);
        return await RunCliAsync("tune", ["compare", "--candidate", right, "--baseline", left], null, deadline.Token);
    }

    private static byte[] Malformed(string mutation)
    {
        var document = Parse(Candidate);
        var hardware = document["hardware"]!.AsObject();
        var levels = document["levels"]!.AsArray();
        switch (mutation)
        {
            case "duplicate":
                return Encoding.UTF8.GetBytes(Candidate.Replace("\"cpu_model\": {",
                    "\"cpu_mo\\u0064el\": {\"kind\":\"unknown\"}, \"cpu_model\": {", StringComparison.Ordinal));
            case "raw-rate":
                return Encoding.UTF8.GetBytes(Candidate.Replace("        10,\n        8,", "        1e0,\n        8,", StringComparison.Ordinal));
            case "raw-integer":
                return Encoding.UTF8.GetBytes(Candidate.Replace("\"value\": 8589934592", "\"value\": -0", StringComparison.Ordinal));
            case "size":
                return Encoding.UTF8.GetBytes(Candidate.PadRight(AirTuneRecord.MaxBytes + 1));
            case "utf8":
                return [.. Encoding.UTF8.GetBytes(Candidate), 0xff];
            case "bom":
                return [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(Candidate)];
            case "version":
                document["schema_version"] = 2;
                break;
            case "unmapped":
                hardware["unmapped"] = 1;
                break;
            case "case-shadow":
                hardware["Cpu_Model"] = Unknown();
                break;
            case "required":
                hardware.Remove("cpu_model");
                break;
            case "row":
                levels.RemoveAt(1);
                break;
            case "after-failure":
                levels[0]!["status"] = "failed";
                levels[0]!["reason"] = "workload_failure";
                levels[0]!.AsObject().Remove("wave_output_tokens_per_second");
                break;
            case "wave-count":
                levels[0]!["wave_output_tokens_per_second"] = JsonNode.Parse("[6,7]");
                break;
            case "zero-rate":
                levels[0]!["wave_output_tokens_per_second"] = JsonNode.Parse("[6,7,0]");
                break;
            case "unknown-payload":
                hardware["ram_limit_bytes"] = JsonNode.Parse("""{"kind":"unknown","value":1}""");
                break;
            case "unlimited-payload":
                hardware["ram_limit_bytes"] = JsonNode.Parse("""{"kind":"unlimited","value":1}""");
                break;
            case "null":
                hardware["cpu_model"]!["value"] = null;
                break;
        }
        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
    private static JsonObject Unknown() => new() { ["kind"] = "unknown" };
    private static JsonObject Unlimited() => new() { ["kind"] = "unlimited" };

    private static string AssertSuccess(ProcessResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        return Encoding.UTF8.GetString(result.Stdout);
    }

    private static void AssertError(ProcessResult result, int exit, string diagnostic)
    {
        Assert.Equal(exit, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(diagnostic + Environment.NewLine, Encoding.UTF8.GetString(result.Stderr));
    }

    private static string Row(string report, int level) => Assert.Single(report.Split('\n'),
        line => line.StartsWith(FormattableString.Invariant($"C={level}: "), StringComparison.Ordinal));

    private static void AssertRedacted(ProcessResult result)
    {
        var stdout = Encoding.UTF8.GetString(result.Stdout);
        var stderr = Encoding.UTF8.GetString(result.Stderr);
        Assert.DoesNotContain(TokenCanary, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenCanary, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(PathCanary, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(PathCanary, stderr, StringComparison.Ordinal);
    }

    private static void AssertPathOnly(SafeFileHandle handle)
    {
        const int getFlags = 3;
        const int pathOnly = 0x200000;
        var flags = GetFlags(handle, getFlags);
        Assert.True(flags >= 0);
        Assert.Equal(pathOnly, flags & pathOnly);
    }

    private static string Fixture(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AirTuneFixtures", file));

    [LibraryImport("libc.so.6", EntryPoint = "mkfifo", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeFifo(string path, uint mode);

    [LibraryImport("libc.so.6", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int GetFlags(SafeFileHandle fd, int command);

    public void Dispose() => _directory.Delete(recursive: true);
}
