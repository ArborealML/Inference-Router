using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Cli;
using static Arboreal.InferenceRouter.Tests.CliCompletionProcessTests;

namespace Arboreal.InferenceRouter.Tests;

public sealed class AirTuneWorkloadPlanOfflineTests : IDisposable
{
    private const string Seed = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string GeneratorDigest = "d98b0ad601e8466b478a5bb92a9033567e5b05a8874b10465e0795c0d6e46ee8";
    private const string GoldenDigest = "69682ac5cda290a65818fc8715836c89e85eec84a327da3397f21d8c395d1444";
    private const string Usage =
        "usage: air tune plan --config FILE --engine ENGINE --model MODEL --seed HEX --max-concurrency N [--profile TIER]";
    private const string BadTarget = "error: invalid tuning workload target or tier";
    private const string BadConfig = "error: invalid tuning workload config";
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(8);
    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "AirTuneFixtures", "workload-golden.toml");
    private static readonly string[] Canaries =
        ["endpoint-canary", "key-canary", "selector-canary", "private-arg-canary", "input-path-canary"];
    private static readonly long[] Hy4Windows = [32_768, 65_536, 98_304, 131_072, 262_144, 524_288, 1_048_576];
    private static readonly string[] Hy4Profiles = ["32k", "64k", "96k", "128k", "256k", "512k", "1024k"];
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("air-tune-plan-");

    private static bool LinuxX64 => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [Fact(Timeout = 30_000)]
    public async Task GoldenUsesCanonicalTargetsAndStableUtf8AcrossFormats()
    {
        if (!LinuxX64)
            return;

        var toml = await RunAsync(Arguments(GoldenPath));
        Assert.Equal(GoldenDigest, Digest(toml.Stdout));
        using var manifest = Parse(toml);
        var root = manifest.RootElement;
        Assert.Equal("air_tune_workload_plan", root.GetProperty("kind").GetString());
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("test.model", root.GetProperty("model").GetString());
        Assert.Equal("64k", root.GetProperty("default_profile").GetString());
        Assert.Equal(GeneratorDigest, root.GetProperty("generator_sha256").GetString());
        Assert.Equal(512, root.GetProperty("policy").GetProperty("target_output_tokens").GetInt32());
        var tiers = root.GetProperty("tiers");
        Assert.Equal(2, tiers.GetArrayLength());
        AssertTier(tiers[0], "32k", 32_768, 1, 1);
        AssertTier(tiers[1], "64k", 65_536, 4, 4);

        var json = Write("equivalent.JSON", """
            {
              "engine": {
                "tune": {
                  "kind": "openai-compatible",
                  "endpoint": "http://endpoint-canary.invalid/v1/",
                  "api_key_file": "/private/key-canary",
                  "lifecycle_owner": "delegated",
                  "catalog": {
                    "test.model": {
                      "aliases": ["model-alias"],
                      "default_profile": "64k",
                      "serving": [
                        {"name": "32k", "max_context": 32768, "max_slots": 1},
                        {"name": "64k", "max_context": 65536, "max_slots": 4}
                      ],
                      "select": {"exec": "/private/selector-canary", "args": ["private-arg-canary"]}
                    }
                  }
                }
              }
            }
            """);
        var equivalent = await RunAsync(Arguments(json, model: "TEST.MODEL"));
        using var other = Parse(equivalent);
        Assert.Equal(root.GetProperty("model").GetString(), other.RootElement.GetProperty("model").GetString());
        Assert.Equal(toml.Stdout, equivalent.Stdout);

        var selected = await RunAsync(Arguments(GoldenPath, profile: "64k"));
        using var single = Parse(selected);
        var selectedTiers = single.RootElement.GetProperty("tiers");
        Assert.Equal(tiers[1].GetRawText(), Assert.Single(selectedTiers.EnumerateArray()).GetRawText());
    }

    [Fact]
    public void GeneratorPinsExactWarmupGridTailAndBounds()
    {
        Assert.Equal(GeneratorDigest, AirTuneTextGenerator.DefinitionSha256);
        var warmup = new AirTuneTextGenerator(Seed, "32k", 1, 0, 1);
        Assert.Equal("57339427527a711cbeed258a455f10849c47f246af085ce713b29fd12d329cec", warmup.Nonce);
        Assert.Equal("25b660087a8b0d7bb9147b845d271da237158448b84f147367bda1648552a74f",
            Digest(warmup.Candidate(1, 0)));

        var grid = new AirTuneTextGenerator(Seed, "64k", 2, 2, 1);
        Assert.Equal("cf9177296d0c564a8800161dc4480b7d69dff616b65933b569b221a81c4e6f14", grid.Nonce);
        var prefix = grid.Candidate(2, 0);
        var withTail = grid.Candidate(2, 11);
        Assert.Equal("ae494964d19962db4b7dddcea673132489620996ac106fcd05c0888109c28296", Digest(prefix));
        Assert.Equal("a69aaac1268953fdcf3ad6672053783915a07d153e13072d33bd8b43dc9c0d6b", Digest(withTail));
        Assert.Equal(prefix, withTail[..prefix.Length]);
        Assert.Equal("3 94db2c1a1", Encoding.ASCII.GetString(withTail.AsSpan(prefix.Length)));
        var fullTail = grid.Candidate(2, 67);
        Assert.Equal("7119ea1a79c436c340fb37e14f803a279775dd793cfeb92d7d95d2d3927e7f1a", Digest(fullTail));
        Assert.Equal(grid.Candidate(3, 0), fullTail);
        Assert.Equal(113, grid.Candidate(0, 0).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Candidate(2, 68));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Candidate(AirTuneTextGenerator.MaxRecordCount, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Candidate(int.MaxValue, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AirTuneTextGenerator(Seed, "64k", 2, 0, 1));
    }

    [Fact]
    public void ShippedHy4HasSevenPlannedOneSlotTiers()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "samples", "router.toml")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var config = RouterConfigLoader.LoadFile(Path.Combine(dir.FullName, "samples", "router.toml"));
        var plan = AirTuneWorkloadPlan.Build(config, "glm", "hy4-flash-maya-xs", null, Seed, 8);
        Assert.NotNull(plan);
        Assert.Equal("hy4-flash", plan.Model);
        Assert.Equal("96k", plan.DefaultProfile);
        Assert.Equal(Hy4Profiles, plan.Tiers.Select(tier => tier.Profile));
        Assert.Equal(Hy4Windows, plan.Tiers.Select(tier => tier.ConfiguredContext));
        Assert.All(plan.Tiers, tier =>
        {
            Assert.Equal(1, tier.ConfiguredSlots);
            Assert.Equal(1, tier.PlannedCap);
            Assert.Equal(4, tier.Requests.Length);
        });
    }

    [Fact(Timeout = 30_000)]
    public async Task LongerLadderRequiresSelectionAndWindowHasExactBoundary()
    {
        if (!LinuxX64)
            return;

        var tiers = Enumerable.Range(0, 17).Select(i => FormattableString.Invariant($"row-{i:D2}")).ToArray();
        var path = Write("longer.toml", Ladder("tune", "test.model", tiers, 672, 4));
        AssertError(await RunAsync(Arguments(path)), 1, BadTarget);

        using var document = Parse(await RunAsync(Arguments(path, profile: "row-16", cap: "2")));
        AssertTier(Assert.Single(document.RootElement.GetProperty("tiers").EnumerateArray()), "row-16", 672, 4, 2);

        var tooSmall = Write("smaller.toml", Ladder("tune", "test.model", tiers, 671, 4));
        AssertError(await RunAsync(Arguments(tooSmall, profile: "row-16")), 1, BadTarget);
    }

    [Fact(Timeout = 30_000)]
    public async Task WorstCaseValidManifestFitsTheActualWriter()
    {
        if (!LinuxX64)
            return;

        var engine = "e" + new string('+', 127);
        var model = "m" + new string('+', 127);
        var names = Enumerable.Range(0, 16)
            .Select(i => "p" + new string('+', 125) + i.ToString("D2", CultureInfo.InvariantCulture)).ToArray();
        string[] descendingAfterDefault = [names[0], .. names.Skip(1).Reverse()];
        var path = Write("max.toml", Ladder(engine, model, descendingAfterDefault, long.MaxValue, int.MaxValue));
        var result = await RunAsync(Arguments(path, engine: engine));
        using var document = Parse(result);
        var root = document.RootElement;
        Assert.Equal(engine, root.GetProperty("engine").GetString());
        Assert.Equal(model, root.GetProperty("model").GetString());
        Assert.Equal(names[0], root.GetProperty("default_profile").GetString());
        Assert.Contains(@"\u002B", Encoding.UTF8.GetString(result.Stdout), StringComparison.Ordinal);
        var tiers = root.GetProperty("tiers");
        Assert.Equal(16, tiers.GetArrayLength());
        Assert.Equal(1_744, tiers.EnumerateArray().Sum(tier => tier.GetProperty("requests").GetArrayLength()));
        for (var i = 0; i < names.Length; i++)
            AssertTier(tiers[i], names[i], long.MaxValue, int.MaxValue, 8);
        Assert.InRange(result.Stdout.Length, 1, 516_096);
    }

    [Fact]
    public void WriterRefusesOversizeBeforeWritingAnyByte()
    {
        using var output = new MemoryStream();
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        // The JSON plus its required LF would be 524,289 UTF-8 bytes.
        var size = AirTuneWorkloadPlan.MaxManifestBytes;
        Assert.Equal(1, AirTuneWorkloadPlan.WriteManifest(new byte[size], output, error));
        Assert.Equal(0, output.Length);
        Assert.Equal("error: tuning workload manifest exceeds its size limit" + Environment.NewLine, error.ToString());
    }

    [Theory(Timeout = 30_000)]
    [InlineData("ambiguous-model")]
    [InlineData("case-twin-engine")]
    [InlineData("exact-engine")]
    [InlineData("engine-local-model")]
    [InlineData("duplicate-tier")]
    [InlineData("unsafe-identity")]
    [InlineData("missing-ladder")]
    [InlineData("ordinal-tier")]
    public async Task InvalidTargetsAreRedacted(string mutation)
    {
        if (!LinuxX64)
            return;

        using var deadline = new CancellationTokenSource(RunLimit);
        var config = await File.ReadAllTextAsync(GoldenPath, deadline.Token);
        var engine = "tune";
        var model = "model-alias";
        string? profile = null;
        switch (mutation)
        {
            case "ambiguous-model":
                config += "\n[engine.tune.catalog.other]\naliases = [\"MODEL-ALIAS\"]\n";
                break;
            case "case-twin-engine":
                config += "\n[engine.TUNE]\nkind=\"openai-compatible\"\nendpoint=\"http://twin.invalid\"\n";
                break;
            case "exact-engine":
                engine = "TUNE";
                break;
            case "engine-local-model":
                config += "\n[engine.peer]\nkind=\"openai-compatible\"\nendpoint=\"http://peer.invalid\"\n" +
                    "[engine.peer.catalog.remote]\nserving=[{name=\"32k\",max_context=32768}]\n";
                model = "remote";
                break;
            case "duplicate-tier":
                config = config.Replace("name = \"32k\"", "name = \"64k\"", StringComparison.Ordinal);
                profile = "64k";
                break;
            case "unsafe-identity":
                config = config.Replace("test.model", "test model", StringComparison.Ordinal);
                break;
            case "missing-ladder":
                // Keep the selector required by the model's delegated engine.
                var ladderStart = config.IndexOf("default_profile =", StringComparison.Ordinal);
                var selectorStart = config.IndexOf("select =", ladderStart, StringComparison.Ordinal);
                config = config.Remove(ladderStart, selectorStart - ladderStart);
                break;
            default:
                profile = "64K";
                break;
        }

        var result = await RunAsync(Arguments(Write("target.toml", config), engine: engine, model: model, profile: profile));
        AssertError(result, 1, BadTarget);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("large")]
    [InlineData("bom")]
    [InlineData("utf8")]
    public async Task AdmittedConfigBytesAreBoundedAndStrictUtf8(string mutation)
    {
        if (!LinuxX64)
            return;

        var path = Path.Combine(_directory.FullName, "input-path-canary.toml");
        using var deadline = new CancellationTokenSource(RunLimit);
        var golden = await File.ReadAllBytesAsync(GoldenPath, deadline.Token);
        byte[] input = mutation switch
        {
            "large" => [.. golden, .. Encoding.ASCII.GetBytes("\n#" + new string('a', 65_537 - golden.Length - 2))],
            "bom" => [0xef, 0xbb, 0xbf, .. golden],
            _ => [.. golden, (byte)'\n', (byte)'#', 0xff],
        };
        await File.WriteAllBytesAsync(path, input, deadline.Token);
        AssertError(await RunAsync(Arguments(path)), 1, BadConfig);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("""{"engine":{"endpoint-canary":null}}""")]
    [InlineData("""
        {"threads":{"key-canary":null},"engine":{"tune":{"kind":"openai-compatible","endpoint":"http://endpoint-canary.invalid"}}}
        """)]
    [InlineData("""
        {"engine":{"tune":{"kind":"openai-compatible","endpoint":"http://endpoint-canary.invalid","catalog":{"key-canary":null}}}}
        """)]
    public async Task NullJsonConfigMapEntriesAreRedacted(string json)
    {
        if (!LinuxX64)
            return;

        var result = await RunAsync(Arguments(Write("input-path-canary.json", json)));
        AssertError(result, 1, BadConfig);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("seed")]
    [InlineData("cap")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    public async Task StrictArgumentsFailBeforeFileAdmission(string mutation)
    {
        // This input never reaches the reader, including on hosts without Linux descriptor admission.
        var args = Arguments("input-path-canary.toml");
        switch (mutation)
        {
            case "seed":
                args[8] = Seed.ToUpperInvariant();
                break;
            case "cap":
                args[10] = "9";
                break;
            case "duplicate":
                args = [.. args, "--model", "model-alias"];
                break;
            case "unknown":
                args = [.. args, "--mystery", "64k"];
                break;
            default:
                args = args[..^1];
                break;
        }

        AssertError(await RunAsync(args), 2, Usage);
    }

    private static void AssertTier(JsonElement tier, string name, long window, int slots, int cap)
    {
        Assert.Equal(name, tier.GetProperty("profile").GetString());
        Assert.Equal(window, tier.GetProperty("configured_context").GetInt64());
        Assert.Equal(slots, tier.GetProperty("configured_slots").GetInt32());
        Assert.Equal(window - 671, tier.GetProperty("nominal_prompt_min").GetInt64());
        Assert.Equal(window - 640, tier.GetProperty("nominal_prompt_max").GetInt64());
        Assert.Equal(cap, tier.GetProperty("planned_cap").GetInt32());

        var requests = tier.GetProperty("requests");
        Assert.Equal(1 + 3 * cap * (cap + 1) / 2, requests.GetArrayLength());
        AssertCoordinate(requests[0], "warmup", 1, 0, 1);
        var index = 1;
        for (var c = 1; c <= cap; c++)
        for (var wave = 1; wave <= 3; wave++)
        for (var worker = 1; worker <= c; worker++)
            AssertCoordinate(requests[index++], "grid", c, wave, worker);
        Assert.Equal(index, requests.EnumerateArray().Select(row => row.GetProperty("nonce").GetString()).Distinct().Count());
    }

    private static void AssertCoordinate(JsonElement row, string kind, int c, int wave, int worker)
    {
        Assert.Equal(kind, row.GetProperty("kind").GetString());
        Assert.Equal(c, row.GetProperty("concurrency").GetInt32());
        Assert.Equal(wave, row.GetProperty("wave").GetInt32());
        Assert.Equal(worker, row.GetProperty("worker").GetInt32());
    }

    private static string Ladder(string engine, string model, string[] tiers, long window, int slots)
    {
        var rows = tiers.Select(tier => FormattableString.Invariant(
            $"{{ name = \"{tier}\", max_context = {window}, max_slots = {slots} }}"));
        return $"""
            [engine."{engine}"]
            kind = "openai-compatible"
            endpoint = "http://endpoint-canary.invalid/v1/"
            [engine."{engine}".catalog."{model}"]
            aliases = ["model-alias"]
            default_profile = "{tiers[0]}"
            serving = [{string.Join(", ", rows)}]
            """;
    }

    private static string[] Arguments(string path, string engine = "tune", string model = "model-alias",
        string cap = "8", string? profile = null)
    {
        string[] required = ["plan", "--config", path, "--engine", engine, "--model", model, "--seed", Seed, "--max-concurrency", cap];
        return profile is null ? required : [.. required, "--profile", profile];
    }

    private static async Task<ProcessResult> RunAsync(string[] args)
    {
        using var deadline = new CancellationTokenSource(RunLimit);
        return await RunCliAsync("tune", args, null, deadline.Token);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory.FullName, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static JsonDocument Parse(ProcessResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Equal((byte)'\n', result.Stdout[^1]);
        AssertRedacted(result);
        return JsonDocument.Parse(result.Stdout);
    }

    private static void AssertError(ProcessResult result, int status, string message)
    {
        Assert.Equal(status, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(message + Environment.NewLine, Encoding.UTF8.GetString(result.Stderr));
        AssertRedacted(result);
    }

    private static void AssertRedacted(ProcessResult result)
    {
        var output = Encoding.UTF8.GetString(result.Stdout);
        var error = Encoding.UTF8.GetString(result.Stderr);
        foreach (var canary in Canaries)
        {
            Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, error, StringComparison.Ordinal);
        }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose() => _directory.Delete(recursive: true);
}
