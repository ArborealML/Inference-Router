using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class DockerTargetProjectionTests
{
    private sealed record StrataAliasModel(string Canonical, string Alias, string DefaultTier, string[] Tiers);

    private const string AliasTier = "max-4";
    private const string CanonicalTier = "256k-4";
    private const string AliasOnlyJson = """{"name":"tier","max_context":100,"aliases":["max-4"]}""";

    private static readonly string[] CommonStrataTiers =
        ["max", "max-2", "128k-4", "32k", "pilot", "concurrent", "64k-4", "64k-6", "64k-8", "96k-4", CanonicalTier];

    private static readonly StrataAliasModel[] StrataAliasModels =
    [
        new("swift-1.5-flash-next", "swift-flash-next", "max", CommonStrataTiers),
        new("qwen-3.8-flash-next", "flash-next", "512k", [.. CommonStrataTiers, "512k", "768k", "1m"]),
        new("qwen-3.8-flash-next-coder", "flash-next-coder", "max", CommonStrataTiers),
    ];

    [Fact]
    public void ProfileAliasResolvesAllSixSpellingsWithoutAddingTargetsOrChangingDefaults()
    {
        var toml = LoadToml(string.Join("\n", StrataAliasModels.Select(AliasModelToml)), """
            command = ["serve", "{{model}}", "{{profile}}"]
            env = ["PROFILE={{profile}}"]
            """);
        var json = LoadJson(string.Join(",", StrataAliasModels.Select(AliasModelJson)),
            ""","command":["serve","{{model}}","{{profile}}"],"env":["PROFILE={{profile}}"]""");
        Assert.Equal(36, toml.Catalog.Values.Sum(model => model.ServingProfiles.Count));
        Assert.Equal(36, json.Catalog.Values.Sum(model => model.ServingProfiles.Count));

        using var daemon = new FakeDockerDaemonHandler();
        using var tomlProvider = new DockerLifecycleProvider(toml.Docker!, toml.Name, daemon);
        using var jsonProvider = new DockerLifecycleProvider(json.Docker!, json.Name, daemon);
        foreach (var model in StrataAliasModels)
        {
            var tomlEntry = toml.Catalog[model.Canonical];
            var jsonEntry = json.Catalog[model.Canonical];
            Assert.Equal(model.Alias, Assert.Single(tomlEntry.Aliases));
            Assert.Equal(model.Alias, Assert.Single(jsonEntry.Aliases));
            Assert.Equal(model.Tiers, tomlEntry.ServingProfiles.Select(tier => tier.Name));
            Assert.Equal(model.Tiers, jsonEntry.ServingProfiles.Select(tier => tier.Name));
            foreach (var spelling in tomlEntry.Aliases.Prepend(model.Canonical))
            {
                var tomlBytes = AssertAliasProjection(tomlProvider, toml, model, spelling);
                var jsonBytes = AssertAliasProjection(jsonProvider, json, model, spelling);
                Assert.Equal(tomlBytes, jsonBytes);
            }
        }
        Assert.Empty(daemon.Requests);
    }

    private static byte[] AssertAliasProjection(
        DockerLifecycleProvider provider, EngineDefinition engine, StrataAliasModel model, string spelling)
    {
        var entry = engine.Catalog[model.Canonical];
        Assert.Equal(model.DefaultTier, entry.Profile);
        var plans = engine.Docker!.TargetPlans!;
        var alias = plans.Resolve(engine.Name, spelling, AliasTier);
        var canonical = Assert.IsType<DockerTargetLaunch>(plans.Resolve(engine.Name, spelling, CanonicalTier));
        Assert.Same(canonical, alias);
        Assert.Equal(model.Canonical, canonical.Model);
        Assert.Equal(CanonicalTier, canonical.Profile);
        Assert.Same(plans.Resolve(engine.Name, spelling, model.DefaultTier), plans.Resolve(engine.Name, spelling, null));
        Assert.Equal(model.DefaultTier, plans.Resolve(engine.Name, spelling, null)?.Profile);

        var canonicalProfile = entry.ServingProfiles.Single(tier => tier.Name == CanonicalTier);
        var alternate = entry.ServingProfiles.Single(tier => tier.Name == "max-2");
        Assert.Equal(canonicalProfile.MaxContext, alternate.MaxContext);
        Assert.Equal(canonicalProfile.MaxSlots, alternate.MaxSlots);
        Assert.NotEqual(canonical.Env[^1], plans.Resolve(engine.Name, spelling, alternate.Name)!.Env[^1]);

        var request = provider.BuildCreateRequest(
            new LifecycleTarget(engine.Name, spelling, canonicalProfile with { Name = AliasTier }, entry),
            engine.Docker.ImageIdentity!);
        Assert.Equal(model.Canonical, request.Labels[DockerLifecycleProvider.LabelModel]);
        Assert.Equal(CanonicalTier, request.Labels[DockerLifecycleProvider.LabelProfile]);
        Assert.Collection(request.Cmd!, arg => Assert.Equal("serve", arg), arg => Assert.Equal(model.Canonical, arg),
            arg => Assert.Equal(CanonicalTier, arg));
        Assert.Equal("PROFILE=256k-4", request.Env![0]);
        var projected = JsonSerializer.SerializeToUtf8Bytes(request, DockerJsonContext.Default.DockerCreateRequest);
        Assert.Equal(Project(provider, engine, spelling, CanonicalTier), projected);
        return projected;
    }

    private static string AliasModelToml(StrataAliasModel model)
    {
        var table = $"engine.strata.catalog.\"{model.Canonical}\"";
        var rows = model.Tiers.Select(tier => $$$"""
            [[{{{table}}}.serving]]
            name = "{{{tier}}}"
            max_context = 256000
            max_slots = 4
            {{{(tier == CanonicalTier ? "aliases = [\"max-4\"]" : "")}}}
            docker = { env = ["TIER_SETTING={{{tier}}}"] }
            """);
        return $"""
            [{table}]
            aliases = ["{model.Alias}"]
            default_profile = "{model.DefaultTier}"
            {string.Join("\n", rows)}
            """;
    }

    private static string AliasModelJson(StrataAliasModel model)
    {
        var rows = model.Tiers.Select(tier => $$$$"""
            {"name":"{{{{tier}}}}","max_context":256000,"max_slots":4,
             {{{{(tier == CanonicalTier ? "\"aliases\":[\"max-4\"]," : "")}}}}
             "docker":{"env":["TIER_SETTING={{{{tier}}}}"]}}
            """);
        return $$$$"""
            "{{{{model.Canonical}}}}":{"aliases":["{{{{model.Alias}}}}"],"default_profile":"{{{{model.DefaultTier}}}}",
            "serving":[{{{{string.Join(",", rows)}}}}]}
            """;
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("Max-4")]
    [InlineData(" max-4 ")]
    public void ProfileAliasExplicitUnknownTierCannotSelectTheDefault(string spelling)
    {
        var engine = LoadToml("""
            [engine.strata.catalog.qwen]
            serving = [{ name = "tier", max_context = 100, Aliases = ["max-4"], Docker = { env = ["TARGET=tier"] } }]
            """);
        var plans = engine.Docker!.TargetPlans!;
        Assert.Throws<RouterConfigException>(() => plans.Resolve(engine.Name, "qwen", spelling));
        Assert.Same(plans.Resolve(engine.Name, "qwen", "max-4"), plans.Resolve(engine.Name, "qwen", null));
        Assert.Equal("tier", plans.Resolve(engine.Name, "qwen", null)?.Profile);
    }

    [Theory]
    [InlineData("tier", "max-4", "max-4", "other")]
    [InlineData("max-4", "tier", "other", "max-4")]
    [InlineData("tier", "other", "shared", "shared")]
    public void ProfileAliasRefusesCanonicalAndCrossTierCollisions(string first, string second, string firstAlias, string secondAlias)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadToml($$$"""
            [engine.strata.catalog.qwen]
            default_profile = "{{{first}}}"
            serving = [
              { name = "{{{first}}}", max_context = 100, aliases = ["{{{firstAlias}}}"], docker = { env = ["TARGET=first"] } },
              { name = "{{{second}}}", max_context = 100, aliases = ["{{{secondAlias}}}"], docker = { env = ["TARGET=second"] } },
            ]
            """));
        Assert.Contains("duplicates a canonical tier or alias", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("default_profile")]
    [InlineData("profile")]
    public void ProfileAliasCannotBeConfiguredAsTheDefault(string key)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadToml($$$"""
            [engine.strata.catalog.qwen]
            {{{key}}} = "max-4"
            serving = [{ name = "tier", max_context = 100, aliases = ["max-4"], docker = { env = ["TARGET=tier"] } }]
            """));
        Assert.Contains("not one of the declared serving profiles", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"aliases\":null")]
    [InlineData("\"aliases\":[]")]
    [InlineData("\"aliases\":\"max-4\"")]
    [InlineData("\"aliases\":[null]")]
    [InlineData("\"aliases\":[17]")]
    [InlineData("\"aliases\":[\" \"]")]
    [InlineData("\"aliases\":[\"max-4 \"]")]
    [InlineData("\"aliases\":[\"max\\u00014\"]")]
    [InlineData("\"aliases\":[\"max-4\",\"max-4\"]")]
    [InlineData("\"aliases\":[\"max-4\"],\"aliases\":[]")]
    [InlineData("\"aliases\":[\"max-4\"],\"Aliases\":[]")]
    public void ProfileAliasJsonRefusesMalformedLists(string aliases)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadJson($$$$"""
            "qwen":{"serving":[{"name":"tier","max_context":100,{{{{aliases}}}},"docker":{"env":["TARGET=tier"]}}]}
            """));
        Assert.Contains("aliases", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"qwen\":{\"serving\":[" + AliasOnlyJson + "]}", "qwen.serving[0].docker must be an object")]
    [InlineData("\"qwen\":{\"serving\":[" + AliasOnlyJson + "],\"serving\":[]}", "qwen.serving is duplicated")]
    [InlineData("\"qwen\":{\"serving\":" + AliasOnlyJson + ",\"serving\":[]}", "qwen.serving must be an array")]
    [InlineData("\"qwen\":{\"serving\":[" + AliasOnlyJson + "]},\"qwen\":{}", "catalog.qwen is duplicated")]
    [InlineData("\"qwen\":{\"serving\":[" + AliasOnlyJson + "]},\"other\":{\"select\":{\"exec\":\"fixture\"},\"select\":null}", "other.select is duplicated")]
    public void ProfileAliasJsonChecksRawAliasOnlyAncestors(string catalog, string path)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadJson(catalog));
        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileAliasJsonChecksAuthorityBeforeDroppingAShadowedAlias()
    {
        var error = Assert.Throws<RouterConfigException>(() =>
            LoadJson("\"qwen\":{\"serving\":[" + AliasOnlyJson + "]}", authority: "\"Lifecycle_Owner\":\"external\","));
        Assert.StartsWith("engine.strata.lifecycle_owner ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("serving = [{ name = \"tier\", max_context = 100, aliases = [\"max-4\"] }]", "aliases")]
    [InlineData("serving = [{ name = \"tier\", max_context = 100, aliases = [\"max-4\"] }, { name = \"other\", max_context = 100, docker = { env = [\"TARGET=other\"] } }]", "aliases")]
    [InlineData("serving = [{ name = \"tier\", max_context = 100, aliases = [\"max-4\"], docker = {} }]", "aliases")]
    [InlineData("serving = [{ name = \"tier\", max_context = 100, aliases = [\"max-4\"], Aliases = [], docker = { env = [\"TARGET=tier\"] } }]", "aliases")]
    [InlineData("serving = [{ name = \"tier\", max_context = 100, aliases = [\"max-4\"] }]\nServing = []", "serving is duplicated")]
    [InlineData("serving = { aliases = [\"max-4\"] }\nServing = []", "serving must be an array")]
    [InlineData("serving = [{ name = \"tier\", max_context = 100, aliases = [\"max-4\"] }]\n[engine.strata.Catalog]", "catalog is duplicated")]
    [InlineData("serving = [{ name = \"tier\", max_context = 100, aliases = [\"max-4\"] }]\n[engine.strata.catalog.other]\nselect = { exec = \"fixture\" }\nSelect = { exec = \"replacement\" }", "other.select is duplicated")]
    public void ProfileAliasTomlRefusesMissingTargetsAndCaseShadows(string serving, string field)
    {
        var error = Assert.Throws<RouterConfigException>(() => LoadToml("[engine.strata.catalog.qwen]\n" + serving));
        Assert.Contains(field, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "aliases")]
    [InlineData(true, "requires a native Docker engine")]
    public void ProfileAliasDirectDtoRequiresItsOwnTargetAndNativeOwnership(bool hasTarget, string message)
    {
        var dto = new ConfigDto
        {
            Engines = new()
            {
                ["strata"] = new EngineDto
                {
                    Kind = "openai-compatible",
                    Endpoint = "http://127.0.0.1:18021",
                    LifecycleOwner = "external",
                    Docker = new DockerDto { Image = ImageId, RouterId = "test-router" },
                    Catalog = new()
                    {
                        ["qwen"] = new ModelDto
                        {
                            Serving = [AliasDto(hasTarget)],
                        },
                    },
                },
            },
        };
        var error = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.FromDto(dto, "fixture"));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    private static ServingDto AliasDto(bool hasTarget)
    {
        var tier = new ServingDto { Name = "tier", MaxContext = 100, Aliases = [AliasTier] };
        if (hasTarget) tier.Docker = new DockerTargetDto { Env = ["TARGET=tier"] };
        return tier;
    }

    [Fact]
    public void ProfileAliasDirectDtoRejectsExplicitNull()
    {
        var tier = new ServingDto();
        var error = Assert.Throws<RouterConfigException>(() => tier.Aliases = null);
        Assert.Contains("aliases cannot be null", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileAliasPlanParserRequiresACanonicalDefault()
    {
        var models = new Dictionary<string, ModelDto>
        {
            ["qwen"] = new() { DefaultProfile = AliasTier, Serving = [AliasDto(hasTarget: true)] },
        };
        var error = Assert.Throws<RouterConfigException>(() =>
            DockerTargetPlans.Parse("strata", models, new DockerDto { Image = ImageId }, common: null));
        Assert.StartsWith("engine.strata.catalog.qwen.default_profile ", error.Message, StringComparison.Ordinal);
    }
}
