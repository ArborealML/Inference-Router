using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Xunit.Abstractions;

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

    private sealed record Row(JsonElement Data)
    {
        public string Engine { get; } = Data.GetProperty("engine").GetString()!;
        public string Model { get; } = Data.GetProperty("model").GetString()!;
        public string Tier { get; } = Data.GetProperty("tier").GetString()!;
        public long Context { get; } = Data.GetProperty("context").GetInt64();
        public int Slots { get; } = Data.GetProperty("slots").GetInt32();
        public JsonElement Create { get; } = Data.GetProperty("create");
        public bool Parking => Data.TryGetProperty("parking", out _);
        public int SelectorLanes => Data.GetProperty("lanes").GetInt32();
        public int Lanes(Shape shape) => Tier == "parked" ? shape.Lanes : SelectorLanes;
        public string Key => $"{Engine}/{Model}/{Tier}";
    }

    [Fact]
    public void RedactedFixtureSmoke()
    {
        using var manifest = JsonDocument.Parse(Fixture("manifest.json"));
        using var reference = JsonDocument.Parse(Fixture(Reference));
        AssertDigest(Fixture(Reference), manifest.RootElement.GetProperty("oracle_sha256").GetString()!);
        var specimens = manifest.RootElement.GetProperty("specimens");
        Assert.Equal(6, specimens.GetPropertyCount());
        var targets = new HashSet<(string Engine, string Model, string Tier)>();
        var listeners = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in specimens.EnumerateObject())
        {
            var bytes = Fixture(entry.Name);
            AssertDigest(bytes, entry.Value.GetProperty("sha256").GetString()!);
            var config = RouterConfigLoader.Load(Encoding.UTF8.GetString(bytes), source: entry.Name);
            var engine = Assert.Single(config.Engines.Values);
            Assert.True(engine.HasExplicitRouterLifecycle);
            listeners.Add(engine.Endpoint.Authority);
            foreach (var (model, definition) in engine.Catalog)
            foreach (var tier in definition.ServingProfiles)
                Assert.True(targets.Add((engine.Name, model, tier.Name)));
        }

        Assert.Equal(3, listeners.Count);
        Assert.Equal(75, targets.Count);
        foreach (var data in reference.RootElement.EnumerateArray())
        {
            var row = new Row(data);
            Assert.True(targets.Remove((row.Engine, row.Model, row.Tier)), row.Key);
        }
        Assert.Empty(targets);
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
        using var reference = JsonDocument.Parse(Fixture(Reference));
        using var deny = new NoHttp();
        var rows = reference.RootElement.EnumerateArray().Select(data => new Row(data)).ToArray();
        var keys = new HashSet<CaseKey>();
        var defaultChecks = 0;
        var explicitChecks = 0;
        var parserChecks = 0;

        foreach (var group in rows.GroupBy(row => row.Engine, StringComparer.Ordinal))
        {
            var source = FixtureText(group.Key);
            var config = RouterConfigLoader.Load(source);
            var engine = config.Engines[group.Key];
            var raw = ParseToml(source);
            var jsonEngine = LoadJson(raw, group.Key);
            using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, deny);
            using var jsonProvider = new DockerLifecycleProvider(jsonEngine.Docker!, jsonEngine.Name, deny);
            await using var catalog = CatalogView(engine, deny);

            foreach (var row in group)
            {
                var definition = engine.Catalog[row.Model];
                var profile = FindTier(definition, row.Tier);
                CheckCapacity(row, profile, row.Slots);
                var expectedDefault = row.Data.GetProperty("default").GetBoolean();
                Assert.Equal(expectedDefault, definition.Profile == row.Tier);
                var rawTier = FindRawTier(raw, row);
                Assert.Equal(row.Data.GetProperty("declared_slots").GetBoolean(), rawTier.ContainsKey("max_slots"));
                var actual = Create(provider, engine, definition, row.Model, profile);
                Match(row.Create, actual, row.Key);
                if (row.Engine != HyperQwen)
                    Assert.True(keys.Add(new CaseKey(row.Engine, row.Model, row.Tier, 0, row.Slots, "all", "")));

                var jsonDefinition = jsonEngine.Catalog[row.Model];
                var jsonProfile = FindTier(jsonDefinition, row.Tier);
                CheckCapacity(row, jsonProfile, row.Slots);
                Assert.Equal(definition.Profile, jsonDefinition.Profile);
                Match(actual, Create(jsonProvider, jsonEngine, jsonDefinition, row.Model, jsonProfile), row.Key);
                parserChecks++;

                foreach (var spelling in row.Data.GetProperty("model_spellings").EnumerateArray())
                {
                    var name = spelling.GetString()!;
                    foreach (var tier in row.Data.GetProperty("tier_spellings").EnumerateArray())
                    {
                        CheckSpelling(engine, provider, catalog, row, name, tier.GetString()!);
                        explicitChecks++;
                    }
                    if (expectedDefault)
                    {
                        CheckSpelling(engine, provider, catalog, row, name, null);
                        defaultChecks++;
                    }
                }
            }
        }

        CheckHyperQwen(rows.Where(row => row.Engine == HyperQwen).ToArray(), keys, deny, out var mixedParserChecks);
        parserChecks += mixedParserChecks;
        var negatives = CheckRefusals();
        Assert.Equal(10_150, keys.Count);
        Assert.Equal(169, explicitChecks);
        Assert.Equal(22, defaultChecks);
        Assert.Equal(78, parserChecks);

        output.WriteLine(FormattableString.Invariant(
            $"AIR_OFFLINE_RESULT unique_positive={keys.Count} explicit_spelling={explicitChecks} omitted_profile={defaultChecks} parser_equivalence={parserChecks} air_refusals={negatives} wrong_shm=1 device_tiers=26 device_choices=52 selector_executions=0 elapsed_ms={elapsed.ElapsedMilliseconds}"));
        using var manifest = JsonDocument.Parse(Fixture("manifest.json"));
        output.WriteLine($"AIR_OFFLINE_INPUTS {manifest.RootElement.GetRawText()}");
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

    private static string FixtureText(string name) => Encoding.UTF8.GetString(Fixture(name + ".toml"));

    private static void AssertDigest(byte[] bytes, string expected)
    {
        // Use the committed LF form even if Git materialized CRLF in the checkout.
        var canonical = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The offline selector comparison must never send HTTP.");
    }
}
