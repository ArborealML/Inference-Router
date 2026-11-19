using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Tomlyn.Model;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class SelectorDifferentialTests(ITestOutputHelper output)
{
    private const string OptIn = "ARBOREAL_RUN_SELECTOR_DIFFERENTIAL";
    private const string Reference = "selector-reference.json";
    private const string HyperQwen = "hyperqwen";
    private const string DefaultUtilization = "0.90";
    private const long GiB = 1L << 30;
    private static readonly string[] Utilizations = [DefaultUtilization, "0.5", "0.9", "0.95"];

    private readonly record struct Shape(int Cache, int Lanes, bool ReverseDevices, string Utilization);
    private readonly record struct CaseKey(string Engine, string Model, string Tier, int Cache, int Lanes,
        string Devices, string Utilization);

    private sealed record Specimen(string Text, EngineDefinition Engine);

    private sealed record Row(JsonElement Data)
    {
        public string Engine { get; } = Data.GetProperty("engine").GetString()!;
        public string Model { get; } = Data.GetProperty("model").GetString()!;
        public string Tier { get; } = Data.GetProperty("tier").GetString()!;
        public long Context { get; } = Data.GetProperty("context").GetInt64();
        public int Slots { get; } = Data.GetProperty("slots").GetInt32();
        public JsonElement Create { get; } = Data.GetProperty("create");
        public string[] ModelSpellings { get; } = Spellings(Data, "model_spellings");
        public string[] TierSpellings { get; } = Spellings(Data, "tier_spellings");
        public bool Parking => Data.TryGetProperty("parking", out _);
        public int SelectorLanes => Data.GetProperty("lanes").GetInt32();
        public int Lanes(Shape shape) => Tier == "parked" ? shape.Lanes : SelectorLanes;
        public string Key => $"{Engine}/{Model}/{Tier}";

        private static string[] Spellings(JsonElement data, string property) =>
            data.GetProperty(property).EnumerateArray().Select(spelling => spelling.GetString()!).ToArray();
    }

    [Fact]
    public void RedactedFixtureSmoke()
    {
        using var manifest = JsonDocument.Parse(Fixture("manifest.json"));
        var reference = Fixture(Reference);
        ValidateFixtures(manifest.RootElement, reference);

        // Change one whitespace byte. The contents still parse, so only integrity should fail.
        var newline = Array.IndexOf(reference, (byte)'\n');
        Assert.True(newline >= 0);
        reference[newline] = (byte)' ';
        var rejection = Assert.Throws<TrueException>(() => ValidateFixtures(manifest.RootElement, reference));
        Assert.Contains($"SHA-256 mismatch for {Reference}.", rejection.Message, StringComparison.Ordinal);
    }

    private static (Row[] Rows, Dictionary<string, Specimen> Specimens) ValidateFixtures(
        JsonElement manifest, byte[] referenceBytes)
    {
        AssertDigest(Reference, referenceBytes, manifest.GetProperty("oracle_sha256").GetString()!);
        using var reference = JsonDocument.Parse(referenceBytes);
        var rows = reference.RootElement.Clone().EnumerateArray().Select(data => new Row(data)).ToArray();
        var entries = manifest.GetProperty("specimens");
        Assert.Equal(6, entries.GetPropertyCount());
        var specimens = new Dictionary<string, Specimen>(StringComparer.Ordinal);
        var targets = new HashSet<(string Engine, string Model, string Tier)>();
        var listeners = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries.EnumerateObject())
        {
            var bytes = Fixture(entry.Name);
            AssertDigest(entry.Name, bytes, entry.Value.GetProperty("sha256").GetString()!);
            var source = Encoding.UTF8.GetString(bytes);
            var config = RouterConfigLoader.Load(source, source: entry.Name);
            var engine = Assert.Single(config.Engines.Values);
            Assert.True(engine.HasExplicitRouterLifecycle);
            Assert.Equal(engine.Name + ".toml", entry.Name);
            Assert.True(specimens.TryAdd(engine.Name, new Specimen(source, engine)));
            listeners.Add(engine.Endpoint.Authority);
            foreach (var (model, definition) in engine.Catalog)
            foreach (var tier in definition.ServingProfiles)
                Assert.True(targets.Add((engine.Name, model, tier.Name)));
        }

        Assert.Equal(3, listeners.Count);
        Assert.Equal(9, targets.Select(target => target.Model).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(75, targets.Count);
        foreach (var row in rows)
            Assert.True(targets.Remove((row.Engine, row.Model, row.Tier)), row.Key);
        Assert.Empty(targets);
        return (rows, specimens);
    }

    [Fact]
    public async Task OfflineSelectorSweep()
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
        {
            output.WriteLine($"NOT_RUN: set {OptIn}=1 to run the offline selector sweep.");
            return;
        }

        var elapsed = Stopwatch.StartNew();
        using var manifest = JsonDocument.Parse(Fixture("manifest.json"));
        var (rows, specimens) = ValidateFixtures(manifest.RootElement, Fixture(Reference));
        using var deny = new NoHttp();
        var keys = new HashSet<CaseKey>();
        var explicitKeys = new HashSet<(string Engine, string Model, string Tier)>();
        var omittedKeys = new HashSet<(string Engine, string Model)>();
        var parserChecks = 0;
        var parserSpellingChecks = 0;

        foreach (var group in rows.GroupBy(row => row.Engine, StringComparer.Ordinal))
        {
            var specimen = specimens[group.Key];
            var engine = specimen.Engine;
            var raw = ParseToml(specimen.Text);
            var jsonEngine = LoadJson(raw, group.Key);
            using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, deny);
            using var jsonProvider = new DockerLifecycleProvider(jsonEngine.Docker!, jsonEngine.Name, deny);
            await using var catalog = CatalogView(engine, deny);
            await using var jsonCatalog = CatalogView(jsonEngine, deny);

            foreach (var row in group)
            {
                var definition = engine.Catalog[row.Model];
                var profile = FindTier(definition, row.Tier);
                CheckCapacity(row, profile, row.Slots);
                var expectedDefault = row.Data.GetProperty("default").GetBoolean();
                Assert.Equal(expectedDefault, definition.Profile == row.Tier);
                var rawTier = FindRawTier(raw, row);
                Assert.Equal(row.Data.GetProperty("declared_slots").GetBoolean(), rawTier.ContainsKey("max_slots"));
                CheckTierAliases(row, rawTier);
                var expectedAliases = row.ModelSpellings.Where(name => name != row.Model).Order(StringComparer.Ordinal).ToArray();
                Assert.Equal(expectedAliases, definition.Aliases.Order(StringComparer.Ordinal).ToArray());
                var actual = Create(provider, engine, definition, row.Model, profile);
                Match(row.Create, actual, row.Key);
                if (row.Engine != HyperQwen)
                    Assert.True(keys.Add(new CaseKey(row.Engine, row.Model, row.Tier, 0, row.Slots, "all", "")));

                var jsonDefinition = jsonEngine.Catalog[row.Model];
                var jsonProfile = FindTier(jsonDefinition, row.Tier);
                CheckCapacity(row, jsonProfile, row.Slots);
                Assert.Equal(definition.Profile, jsonDefinition.Profile);
                Assert.Equal(expectedAliases, jsonDefinition.Aliases.Order(StringComparer.Ordinal).ToArray());
                Match(actual, Create(jsonProvider, jsonEngine, jsonDefinition, row.Model, jsonProfile), row.Key);
                parserChecks++;

                foreach (var name in row.ModelSpellings)
                {
                    foreach (var tier in row.TierSpellings)
                    {
                        Assert.True(explicitKeys.Add((row.Engine, name, tier)), $"Duplicate spelling {row.Engine}/{name}/{tier}");
                        CheckSpelling(engine, provider, catalog, row, name, tier);
                    }
                    if (expectedDefault)
                    {
                        Assert.True(omittedKeys.Add((row.Engine, name)), $"Duplicate omitted profile {row.Engine}/{name}");
                        CheckSpelling(engine, provider, catalog, row, name, null);
                    }
                }

                // The TOML spelling cases above use the same reference for these JSON readbacks.
                if (row.Engine == "strata" && row.Model == "qwen3.8-flash-next-iq3_s")
                {
                    if (row.Tier == "256k-4")
                    {
                        Assert.Contains("flash-next", row.ModelSpellings);
                        Assert.Contains("max-4", row.TierSpellings);
                        CheckSpelling(jsonEngine, jsonProvider, jsonCatalog, row, "flash-next", "max-4");
                        parserSpellingChecks++;
                    }
                    if (expectedDefault)
                    {
                        Assert.Contains("flash-next", row.ModelSpellings);
                        CheckSpelling(jsonEngine, jsonProvider, jsonCatalog, row, "flash-next", null);
                        parserSpellingChecks++;
                    }
                }
            }
        }

        var (mixedParserChecks, wrongShm) = CheckHyperQwen(
            rows.Where(row => row.Engine == HyperQwen).ToArray(), keys, deny, specimens[HyperQwen]);
        parserChecks += mixedParserChecks;
        var negatives = CheckRefusals(specimens);
        Assert.Equal(10_150, keys.Count);
        Assert.Equal(169, explicitKeys.Count);
        Assert.Equal(22, omittedKeys.Count);
        Assert.Equal(78, parserChecks);
        Assert.Equal(2, parserSpellingChecks);
        Assert.Equal(5, negatives);
        Assert.Equal(1, wrongShm);

        output.WriteLine(FormattableString.Invariant(
            $"AIR_OFFLINE_RESULT unique_positive={keys.Count} explicit_spelling={explicitKeys.Count} omitted_profile={omittedKeys.Count} parser_equivalence={parserChecks} parser_spelling={parserSpellingChecks} air_refusals={negatives} wrong_shm={wrongShm} device_tiers=26 device_choices=52 selector_executions=0 elapsed_ms={elapsed.ElapsedMilliseconds}"));
        output.WriteLine($"AIR_OFFLINE_INPUTS {manifest.RootElement.GetRawText()}");
    }

    private static void CheckTierAliases(Row row, TomlTable rawTier)
    {
        var expected = row.TierSpellings.Where(name => name != row.Tier).Order(StringComparer.Ordinal).ToArray();
        var declared = rawTier.TryGetValue("aliases", out var aliases)
            ? ((TomlArray)aliases!).Cast<string>().Order(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        Assert.Equal(expected, declared);
    }

    private static EngineRuntime CatalogView(EngineDefinition engine, NoHttp deny) =>
        new(new EngineDefinition
        {
            Name = engine.Name,
            Endpoint = engine.Endpoint,
            Kind = engine.Kind,
            LifecycleOwner = EngineLifecycleOwner.External,
            Catalog = engine.Catalog,
        }, new RouterConfig(), new EventRing(), handler: deny);

    private static void CheckSpelling(EngineDefinition engine, DockerLifecycleProvider provider, EngineRuntime catalog,
        Row row, string spelling, string? tier)
    {
        if (engine.Docker!.TargetPlans is { } plans)
        {
            // Preserve both original strings through the production plan resolver.
            var launch = plans.Resolve(engine.Name, spelling, tier);
            Assert.NotNull(launch);
            Assert.Equal(row.Model, launch.Model);
            Assert.Equal(row.Tier, launch.Profile);
            var definition = engine.Catalog[launch.Model];
            var profile = FindTier(definition, launch.Profile);
            CheckCapacity(row, profile, row.Slots);
            var targetProfile = tier is null ? null : profile with { Name = tier };
            Match(row.Create, Create(provider, engine, definition, spelling, targetProfile), row.Key);
            return;
        }

        Assert.Equal("minimax", engine.Name);
        var canonical = catalog.CanonicalModel(spelling);
        Assert.Equal(row.Model, canonical);
        var native = engine.Catalog[canonical!];
        Assert.Equal("32k", native.Profile);
        if (tier is null) Assert.Null(catalog.GetProfile(canonical!, null));
        var explicitProfile = catalog.GetProfile(canonical!, tier ?? native.Profile);
        Assert.NotNull(explicitProfile);
        CheckCapacity(row, explicitProfile, 1);
        Assert.Equal(32768L, explicitProfile.MaxContext);
        // The catalog view only resolves. Native common Docker settings produce both payloads.
        Match(row.Create, Create(provider, engine, native, canonical!, tier is null ? null : explicitProfile), row.Key);
    }

    private static ServingProfile FindTier(EngineModelDefinition definition, string tier) =>
        definition.ServingProfiles.Single(profile => profile.Name == tier);

    private static void CheckCapacity(Row row, ServingProfile profile, int slots)
    {
        Assert.Equal(row.Context, profile.MaxContext);
        Assert.Equal(slots, profile.MaxSlots);
    }

    private static JsonElement Create(DockerLifecycleProvider provider, EngineDefinition engine,
        EngineModelDefinition definition, string model, ServingProfile? profile)
    {
        var target = new LifecycleTarget(engine.Name, model, profile, definition);
        return JsonSerializer.SerializeToElement(provider.BuildCreateRequest(target, engine.Docker!.ImageIdentity!),
            DockerJsonContext.Default.DockerCreateRequest);
    }

    private static void Match(JsonElement expected, JsonElement actual, string key)
    {
        if (JsonElement.DeepEquals(expected, actual)) return;
        var difference = Difference(expected, actual, "$");
        Assert.Fail($"{key}: mismatch at {difference}. Expected {expected.GetRawText()}, actual {actual.GetRawText()}");
    }

    private static string? Difference(JsonElement expected, JsonElement actual, string path)
    {
        if (JsonElement.DeepEquals(expected, actual)) return null;
        if (expected.ValueKind == JsonValueKind.Object && actual.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in expected.EnumerateObject())
            {
                var child = path + "." + property.Name;
                if (!actual.TryGetProperty(property.Name, out var value)) return child;
                if (Difference(property.Value, value, child) is { } difference) return difference;
            }
            foreach (var property in actual.EnumerateObject())
                if (!expected.TryGetProperty(property.Name, out _)) return path + "." + property.Name;
        }
        if (expected.ValueKind == JsonValueKind.Array && actual.ValueKind == JsonValueKind.Array)
        {
            for (var index = 0; index < Math.Min(expected.GetArrayLength(), actual.GetArrayLength()); index++)
                if (Difference(expected[index], actual[index], FormattableString.Invariant($"{path}[{index}]")) is { } difference)
                    return difference;
        }
        return path;
    }

    private static byte[] Fixture(string name)
    {
        using var stream = typeof(SelectorDifferentialTests).Assembly.GetManifestResourceStream("SelectorFixtures/" + name)
            ?? throw new InvalidOperationException($"Missing selector fixture {name}");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void AssertDigest(string name, byte[] bytes, string expected)
    {
        // Use the committed LF form even if Git materialized CRLF in the checkout.
        var canonical = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        Assert.True(expected == actual, $"SHA-256 mismatch for {name}. Expected {expected}, actual {actual}.");
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The offline selector comparison must never send HTTP.");
    }
}
