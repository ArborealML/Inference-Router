using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arboreal.InferenceRouter.Cli;
using CliProgram = Arboreal.InferenceRouter.Cli.Program;
using static Arboreal.InferenceRouter.Tests.CliCompletionProcessTests;

namespace Arboreal.InferenceRouter.Tests;

public sealed class AirTuneSingleModelTests : IDisposable
{
    private const string Seed = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Model = "illustrative-model";
    private const string BadRecipe = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private static readonly string BaselinePath = Path.Combine(AppContext.BaseDirectory, "AirTuneFixtures", "baseline.json");
    private readonly DirectoryInfo _output = Directory.CreateTempSubdirectory("air-tune-run-");

    [Fact(Timeout = 30_000)]
    public async Task NormalCommandCollectsFourIdenticalCountedBodiesAndMakesComparableSummaries()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = new Capture("success");
        var first = await Run("first", fixture);
        Assert.Equal(0, first.Exit);
        Assert.NotNull(first.Evidence);
        using var document = JsonDocument.Parse(first.Evidence);
        var record = document.RootElement;
        Assert.Equal("passed", record.GetProperty("status").GetString());
        Assert.Equal("unavailable", record.GetProperty("cold_load").GetString());
        Assert.Equal("untested", record.GetProperty("long_decode").GetString());
        var samples = record.GetProperty("samples");
        var frozen = record.GetProperty("workload");
        Assert.Equal(4, fixture.Completed.Count);
        Assert.Equal(4, samples.GetArrayLength());
        Assert.Equal(4, frozen.GetArrayLength());
        for (var wave = 0; wave < 4; wave++)
        {
            Assert.Equal(Digest(fixture.Completed[wave]), frozen[wave].GetProperty("body_sha256").GetString());
            Assert.Equal(32_128, frozen[wave].GetProperty("prompt_tokens").GetInt32());
            Assert.Equal(512, samples[wave].GetProperty("completion_tokens").GetInt32());
            Assert.Equal(wave, samples[wave].GetProperty("coordinate").GetProperty("wave").GetInt32());
            Assert.True(samples[wave].GetProperty("ttft_ticks").GetInt64() <= samples[wave].GetProperty("terminal_ticks").GetInt64());
        }
        Assert.DoesNotContain("backend-canary", first.Evidence, StringComparison.Ordinal);

        await using var candidate = new Capture("success");
        var second = await Run("second", candidate);
        Assert.Equal(0, second.Exit);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var comparison = await RunCliAsync("tune", ["compare", "--baseline", FilePath("first.summary.json"),
            "--candidate", FilePath("second.summary.json")], null, deadline.Token);
        Assert.Equal(0, comparison.ExitCode);
        Assert.Contains("delta", Encoding.UTF8.GetString(comparison.Stdout), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("incomparable", Encoding.UTF8.GetString(comparison.Stdout), StringComparison.OrdinalIgnoreCase);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("busy", "refused", "admission_refused", 0)]
    [InlineData("wrong-recipe", "refused", "preflight_refusal", 0)]
    [InlineData("stale", "refused", "preflight_refusal", 0)]
    [InlineData("fit-refused", "refused", "invalid_workload", 0)]
    [InlineData("retry", "failed", "native_workload_mismatch", 1)]
    [InlineData("reused", "failed", "native_workload_mismatch", 1)]
    [InlineData("clamped", "failed", "native_workload_mismatch", 1)]
    [InlineData("not-admitted", "inconclusive", "native_evidence_unavailable", 1)]
    [InlineData("missing-native", "inconclusive", "native_evidence_unavailable", 1)]
    [InlineData("early-end", "failed", "stream_workload_mismatch", 1)]
    [InlineData("duplicate-done", "failed", "stream_workload_mismatch", 1)]
    [InlineData("count-mismatch", "failed", "stream_workload_mismatch", 1)]
    [InlineData("truncated", "failed", "stream_workload_mismatch", 1)]
    [InlineData("missing-usage", "failed", "stream_workload_mismatch", 1)]
    [InlineData("unsettled", "inconclusive", "settlement_unproved", 1)]
    [InlineData("drift", "inconclusive", "settlement_unproved", 1)]
    public async Task MissingOrAdverseEvidenceStopsTheRun(string mode, string status, string reason, int starts)
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = new Capture(mode);
        var result = await Run("result", fixture);
        Assert.Equal(1, result.Exit);
        Assert.NotNull(result.Evidence);
        using var document = JsonDocument.Parse(result.Evidence);
        Assert.Equal(status, document.RootElement.GetProperty("status").GetString());
        Assert.Equal(reason, document.RootElement.GetProperty("reason").GetString());
        Assert.Equal(starts, fixture.Completed.Count);
        if (mode is "busy" or "wrong-recipe" or "stale") Assert.Empty(fixture.Counted);
    }

    [Fact(Timeout = 30_000)]
    public async Task StockBinarySavesAnAdmissionRefusalAndWillNotReplaceAnExistingRecord()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var args = Arguments("refusal", BadRecipe);
        var process = await RunCliAsync("tune", args[1..], null, deadline.Token);
        Assert.Equal(1, process.ExitCode);
        Assert.Empty(process.Stdout);
        var path = FilePath("refusal.json");
        var refusal = await File.ReadAllBytesAsync(path, deadline.Token);
        using var document = JsonDocument.Parse(refusal);
        Assert.Equal("admission_unavailable", document.RootElement.GetProperty("reason").GetString());
        Assert.Empty(document.RootElement.GetProperty("samples").EnumerateArray());

        await using var capture = new Capture("success");
        Assert.Equal(1, await CliProgram.RunAsync(args, capture));
        Assert.Equal(refusal, File.ReadAllBytes(path));
        Assert.Empty(capture.Counted);
        Assert.Empty(capture.Completed);

        var symlink = FilePath("symlink.json");
        File.CreateSymbolicLink(symlink, path);
        Assert.Equal(1, await CliProgram.RunAsync(Arguments("symlink", capture.ExpectedRecipe), capture));
        Assert.Equal(refusal, File.ReadAllBytes(path));
        Assert.Empty(capture.Counted);
        Assert.Empty(capture.Completed);
    }

    private async Task<(int Exit, string? Evidence)> Run(string name, Capture fixture)
    {
        var exit = await CliProgram.RunAsync(Arguments(name, fixture.ExpectedRecipe), fixture);
        var path = FilePath(name + ".json");
        return (exit, File.Exists(path) ? File.ReadAllText(path) : null);
    }

    private string[] Arguments(string name, string recipe)
    {
        var config = FilePath("router.toml");
        var host = FilePath("host.json");
        File.WriteAllText(config, """
            [engine.fixture]
            kind = "openai-compatible"
            endpoint = "http://backend-canary.invalid/v1"

            [engine.fixture.catalog.illustrative-model]
            aliases = ["requested-model"]
            serving = [{ name = "32k", max_context = 32768, max_slots = 1 }]
            """);
        File.WriteAllText(host, $$"""{"schema_version":1,"recipe_sha256":"{{recipe}}"}""");
        return ["tune", "run", "--config", config, "--out", FilePath(name + ".json"), "--host-record", host,
            "--engine", "fixture", "--model", "requested-model", "--profile", "32k", "--seed", Seed, "--max-concurrency", "1"];
    }

    private static string Digest(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));
    private string FilePath(string name) => Path.Combine(_output.FullName, name);

    public void Dispose() => _output.Delete(recursive: true);

    private sealed class Capture : ITuneAdmission, ITuneResident
    {
        private readonly AirTuneRecord _baseline = AirTuneRecord.Parse(File.ReadAllBytes(BaselinePath))!;
        private readonly string _mode;
        private TuneNativeSample? _last;
        private bool _owned;

        internal Capture(string mode)
        {
            _mode = mode;
            var node = JsonNode.Parse(File.ReadAllText(BaselinePath))!["target"]!;
            node["tier"]!["value"] = "32k";
            node["nominal_context"]!["value"] = 32_768;
            node["observed_context"]!["value"] = 32_768;
            node["usable_context"]!["value"] = 32_760;
            node["configured_slots"]!["value"] = 1;
            node["observed_slots"]!["value"] = 1;
            Target = node.Deserialize(AirTuneRunJsonContext.Default.TuneTarget)!;
        }

        internal string ExpectedRecipe => _mode == "wrong-recipe" ? BadRecipe : Target.RecipeSha256.Value!;
        internal TuneTarget Target { get; }
        internal List<byte[]> Completed { get; } = [];
        internal HashSet<byte[]> Counted { get; } = new(ReferenceEqualityComparer.Instance);

        public ValueTask<ITuneResident?> AcquireAsync(EngineDefinition engine, string model, string profile,
            string expectedRecipeSha256, CancellationToken cancellationToken)
        {
            Assert.Equal("fixture", engine.Name);
            Assert.Equal(Model, model);
            Assert.Equal("32k", profile);
            Assert.Equal(ExpectedRecipe, expectedRecipeSha256);
            if (_owned || _mode == "busy") return ValueTask.FromResult<ITuneResident?>(null);
            _owned = true;
            return ValueTask.FromResult<ITuneResident?>(this);
        }

        public ValueTask<TuneWitness?> ObserveAsync(Guid challenge, Guid? request, CancellationToken cancellationToken)
        {
            Assert.True(_owned);
            var generation = request is not null && _mode == "drift" ? 2 : 1;
            return ValueTask.FromResult<TuneWitness?>(new(_mode == "stale" ? Guid.Empty : challenge,
                _baseline.InstanceId.Value!, generation, true, 0, 0, 8, true, Target, _baseline.Hardware,
                request is null ? null : _last));
        }

        public Task<int> CountAsync(byte[] body, CancellationToken cancellationToken)
        {
            Assert.True(_owned);
            Assert.True(Counted.Add(body));
            return Task.FromResult(_mode == "fit-refused" ? 32_129 : Count(body));
        }

        public async Task<HttpResponseMessage> StartAsync(Guid request, byte[] body, CancellationToken cancellationToken)
        {
            Assert.True(_owned);
            Assert.Contains(Counted, candidate => ReferenceEquals(body, candidate));
            Completed.Add(body);
            var count = Count(body);
            int? reused = _mode == "missing-native" ? null : _mode == "reused" ? 65 : 0;
            _last = new(request, _mode != "not-admitted", _mode != "unsettled", _mode == "retry" ? 2 : 1, 0,
                count, _mode == "clamped" ? 511 : 512, reused, 512, 2_000, 4_000);

            // The private fixture owns both transport and native evidence. The delay keeps the
            // synthetic wall rate in the same finite numeric format as saved comparisons.
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            const string delta = "data: {\"model\":\"illustrative-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"answer\"}}]}\r\n\r\n";
            var finish = _mode == "early-end" ? "stop" : "length";
            var output = _mode == "early-end" ? 511 : 512;
            var prompt = _mode == "count-mismatch" ? count + 1 : count;
            var usage = _mode == "missing-usage" ? string.Empty : string.Create(CultureInfo.InvariantCulture,
                $",\"usage\":{{\"prompt_tokens\":{prompt},\"completion_tokens\":{output}}}");
            var terminal = $"data: {{\"model\":\"{Model}\",\"choices\":[{{\"index\":0,\"finish_reason\":\"{finish}\"}}]{usage}}}\n\n";
            var done = _mode switch
            {
                "truncated" => "data: [DONE]\n",
                "duplicate-done" => "data: [DONE]\n\ndata: [DONE]\n\n",
                _ => "data: [DONE]\n\n",
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(": comment\n\n" + delta + terminal + done, new MediaTypeHeaderValue("text/event-stream")),
            };
        }

        private static int Count(byte[] body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal(Model, root.GetProperty("model").GetString());
            Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
            return root.GetProperty("messages")[0].GetProperty("content").GetString()!.Length / 2 + 8;
        }

        public ValueTask DisposeAsync()
        {
            _owned = false;
            return ValueTask.CompletedTask;
        }
    }
}
