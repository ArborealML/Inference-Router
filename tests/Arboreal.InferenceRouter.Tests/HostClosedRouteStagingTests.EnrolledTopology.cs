using System.Collections.Immutable;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Tomlyn;
using Tomlyn.Model;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void EnrolledTopologyProjectsEveryConfiguredEngineAndRejectsIncompleteCoverage(int count)
    {
        var (text, originalClaim, originalAuthority) = EnrolledPairTopologyFixture();
        var document = ReadDeepSeekQwenDocument(text);
        var engines = Assert.IsType<TomlTable>(document["engine"]);
        engines.Clear();
        for (var i = 0; i < count; i++)
        {
            var source = ReadDeepSeekQwenDocument(text);
            var engine = DeepSeekQwenEngineTable(source, DeepSeekEngineName);
            engine["endpoint"] = $"http://127.0.0.1:{19000 + i}/v1";
            engines.Add($"engine{i}", engine);
        }
        var configured = WriteDeepSeekQwenDocument(document);
        var grants = ImmutableArray.CreateBuilder<EnrolledPairMemberGrant>(count);
        foreach (var (name, entry) in engines)
        {
            var single = ReadDeepSeekQwenDocument(configured);
            single["engine"] = new TomlTable { [name] = entry };
            var engine = RouterConfigLoader.Load(WriteDeepSeekQwenDocument(single)).Engines[name];
            var resolver = new VerifiedDispatchTargetResolver(engine);
            var (target, plan, _) = resolver.ResolveProjection(DeepSeekPairModel, "32k", resident: null,
                engine.Name + "-configuration", engine.Name + "-route");
            grants.Add(new(new(engine.Name, DeepSeekPairModel, "32k", plan.Configuration, plan.BackendRoute),
                engine.Docker!.Socket["unix://".Length..], engine.Endpoint, target.PlanDigest));
        }
        var authority = originalAuthority with { Members = grants.MoveToImmutable() };
        var claim = originalClaim with { Members = [.. authority.Members.Select(grant => grant.Member)] };
        var projected = RouterConfigLoader.PrepareEnrolledPair(configured, authority.Head.Slot, claim, _ => authority);
        Assert.Equal(count, projected.Members.Length);
        Assert.Equal(engines.Keys.Order(StringComparer.Ordinal),
            projected.Members.Select(member => member.Plan.Engine).Order(StringComparer.Ordinal));
        for (var i = 0; i < count; i++)
        {
            var missing = authority with { Members = authority.Members.RemoveAt(i) };
            var error = Assert.Throws<RouterConfigException>(() =>
                RouterConfigLoader.PrepareEnrolledPair(configured, authority.Head.Slot, claim, _ => missing));
            Assert.Equal("enrolled topology refused: member or route coverage mismatch", error.Message);
        }
    }

    [Fact]
    public void EnrolledPairTopologyAcceptsTheCurrentPairAndOwnsTheFrozenSnapshot()
    {
        var (text, claim, authority) = EnrolledPairTopologyFixture();
        string[]? parsedCommand = null;
        var reads = 0;
        var checkedPair = RouterConfigLoader.PrepareEnrolledPair(text, authority.Head.Slot, claim, slot =>
        {
            Assert.Equal(authority.Head.Slot, slot);
            reads++;
            // The typed launch settings must already own a copy when current authority is read.
            Assert.IsType<string[]>(parsedCommand)[^1] = "32769";
            return authority;
        }, beforeFreezeForTests: dto => parsedCommand = EnrolledTopologyDeepSeekCommand(dto));

        Assert.Equal(1, reads);
        Assert.Equal(authority.Head, checkedPair.Head);
        Assert.Equal<string>(authority.Devices, checkedPair.Devices);
        Assert.Equal(authority.Members.Length, checkedPair.Members.Length);
        for (var i = 0; i < authority.Members.Length; i++)
        {
            var member = checkedPair.Members[i];
            var grant = authority.Members[i];
            Assert.Equal(grant.Member.Engine, member.Plan.Engine);
            Assert.Equal(grant.Member.Model, member.Plan.Model);
            Assert.Equal(grant.Member.Tier, member.Plan.Tier);
            Assert.Equal(grant.Member.Configuration, member.Plan.Configuration);
            Assert.Equal(grant.Member.BackendRoute, member.Plan.BackendRoute);
            Assert.Equal(grant.Endpoint, member.Endpoint);
            Assert.Equal(grant.SocketPath, member.SocketPath);
            Assert.Equal(grant.PlanDigest, member.PlanDigest);
            Assert.Equal(grant.PlanDigest, member.Plan.Digest());
        }

        Assert.IsType<string[]>(parsedCommand)[^1] = "32770";
        var deepSeek = checkedPair.Members[0];
        using var create = JsonDocument.Parse(deepSeek.CreateJson);
        Assert.Equal("32768", create.RootElement.GetProperty("Cmd")[2].GetString());
        Assert.Equal(deepSeek.Image.Id, create.RootElement.GetProperty("Image").GetString());
        Assert.Equal(authority.Members[0].PlanDigest, deepSeek.Plan.Digest());
    }

    [Theory]
    [InlineData(true, "configuration identity mismatch")]
    [InlineData(false, "backend route identity mismatch")]
    public void EnrolledPairTopologyRejectsSecondMemberIdentityBeforeProjection(bool configuration, string reason)
    {
        var (text, claim, authority) = EnrolledPairTopologyFixture();
        var qwen = claim.Members[1];
        Assert.Equal(QwenEngineName, qwen.Engine);
        var other = configuration ? qwen with { Configuration = "alternate-config" }
            : qwen with { BackendRoute = "alternate-route" };
        var changed = claim with { Members = claim.Members.SetItem(1, other) };

        var failure = Assert.Throws<RouterConfigException>(() =>
            RouterConfigLoader.PrepareEnrolledPair(text, authority.Head.Slot, changed, _ => authority));
        Assert.Equal($"enrolled topology refused: {reason}", failure.Message);
    }

    [Theory]
    [InlineData("missing", "current commissioned head mismatch")]
    [InlineData("older head", "current commissioned head mismatch")]
    [InlineData("wrong head digest", "current commissioned head mismatch")]
    [InlineData("partial member grants", "member or route coverage mismatch")]
    [InlineData("duplicate member", "member or route coverage mismatch")]
    [InlineData("duplicate granted routes", "member or route coverage mismatch")]
    [InlineData("partial device grants", "device coverage mismatch")]
    [InlineData("duplicate submitted devices", "device coverage mismatch")]
    [InlineData("swapped plans", "effective launch plan mismatch")]
    public void EnrolledPairTopologyRefusesUncurrentOrIncompleteAuthority(string changed, string reason)
    {
        var (text, claim, authority) = EnrolledPairTopologyFixture();
        var first = authority.Members[0];
        var second = authority.Members[1];
        EnrolledPairAuthority? current = changed switch
        {
            "missing" => null,
            "older head" => authority with { Head = authority.Head with
            {
                Revision = authority.Head.Revision + 1,
                Digest = new string('B', 64),
            } },
            "wrong head digest" => authority with { Head = authority.Head with { Digest = new string('B', 64) } },
            "partial member grants" => authority with { Members = [first] },
            "duplicate granted routes" => authority with
            {
                Members = [first, second with { Member = second.Member with { BackendRoute = first.Member.BackendRoute } }],
            },
            "partial device grants" => authority with { Devices = [authority.Devices[0]] },
            "swapped plans" => authority with
            {
                Members = [first with { PlanDigest = second.PlanDigest }, second with { PlanDigest = first.PlanDigest }],
            },
            _ => authority,
        };
        var submitted = changed switch
        {
            "duplicate member" => claim with { Members = [first.Member, first.Member] },
            "duplicate submitted devices" => claim with { Devices = [claim.Devices[0], claim.Devices[0]] },
            _ => claim,
        };

        var failure = Assert.Throws<RouterConfigException>(() =>
            RouterConfigLoader.PrepareEnrolledPair(text, authority.Head.Slot, submitted, _ => current));
        Assert.Equal($"enrolled topology refused: {reason}", failure.Message);
    }

    [Fact]
    public void EnrolledPairTopologyRefusesMutationOfEffectiveSettingsBeforeFreeze()
    {
        var (text, claim, authority) = EnrolledPairTopologyFixture();
        var failure = Assert.Throws<RouterConfigException>(() =>
            RouterConfigLoader.PrepareEnrolledPair(text, authority.Head.Slot, claim, _ => authority,
                beforeFreezeForTests: dto => EnrolledTopologyDeepSeekCommand(dto)[^1] = "32769"));

        Assert.Equal("enrolled topology refused: effective launch plan mismatch", failure.Message);
    }

    [Theory]
    [InlineData("Load")]
    [InlineData("LoadFile")]
    [InlineData("FromDto")]
    public void EnrolledPairTopologyPreservesThePublicDedicatedOwnerRefusal(string entry)
    {
        var (text, _, _) = EnrolledPairTopologyFixture();
        var path = Path.Combine(_directory.FullName, "enrolled-pair.toml");
        if (entry == "LoadFile")
            File.WriteAllText(path, text);

        var failure = Assert.Throws<RouterConfigException>(() => entry switch
        {
            "Load" => RouterConfigLoader.Load(text),
            "LoadFile" => RouterConfigLoader.LoadFile(path),
            "FromDto" => RouterConfigLoader.FromDto(
                Assert.IsType<ConfigDto>(TomlSerializer.Deserialize(text, ConfigTomlContext.Default.ConfigDto)), path),
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        });
        Assert.Equal("engine.deepseek.lifecycle_owner = router requires a dedicated single-engine configuration until managed peer fencing is available",
            failure.Message);
    }

    [Fact]
    public void EnrolledPairTopologyPreservesTargetDeclarationValidation()
    {
        var (text, claim, authority) = EnrolledPairTopologyFixture();
        var document = ReadDeepSeekQwenDocument(text);
        var docker = Assert.IsType<TomlTable>(DeepSeekQwenEngineTable(document, DeepSeekEngineName)["docker"]);
        docker["healthcheck"] = new TomlTable { ["command"] = "/fixture/probe" };
        var altered = WriteDeepSeekQwenDocument(document);

        var publicFailure = Assert.Throws<RouterConfigException>(() => RouterConfigLoader.Load(altered));
        var internalFailure = Assert.Throws<RouterConfigException>(() =>
            RouterConfigLoader.PrepareEnrolledPair(altered, authority.Head.Slot, claim, _ => authority));
        Assert.Equal("engine.deepseek.docker.healthcheck is only supported inside a serving-tier docker block", publicFailure.Message);
        Assert.Equal(publicFailure.Message, internalFailure.Message);
    }

    private static (string Text, EnrolledPairClaim Claim, EnrolledPairAuthority Authority) EnrolledPairTopologyFixture(
        string? sharedSocket = null, Uri? deepSeekEndpoint = null, Uri? qwenEndpoint = null)
    {
        var deepSeek = CorrectDeepSeekEndpoint(ReadDeepSeekQwenConfig("deepseek.toml"));
        var qwen = ReadDeepSeekQwenConfig("strata.toml");
        if (deepSeekEndpoint is not null) deepSeek = ReplaceFixtureEndpoint(deepSeek, DeepSeekEngineName, deepSeekEndpoint);
        if (qwenEndpoint is not null) qwen = ReplaceFixtureEndpoint(qwen, QwenEngineName, qwenEndpoint);
        var combined = ReadDeepSeekQwenDocument(deepSeek);
        combined["auto_switch"] = false;
        var engines = Assert.IsType<TomlTable>(combined["engine"]);
        engines.Add(QwenEngineName, DeepSeekQwenEngineTable(ReadDeepSeekQwenDocument(qwen), QwenEngineName));

        using var stream = OpenPairResource("selector-reference.json");
        using var oracle = JsonDocument.Parse(stream);
        var first = EnrolledTopologyGrant(LoadDeepSeekQwenEngine(deepSeek, DeepSeekEngineName),
            DeepSeekPairModel, "32k", "/var/run/docker.sock", oracle.RootElement);
        var second = EnrolledTopologyGrant(LoadDeepSeekQwenEngine(qwen, QwenEngineName),
            QwenPairModel, "256k-4", "/fixture/no-daemon.sock", oracle.RootElement);
        if (sharedSocket is not null)
        {
            foreach (var engine in new[] { DeepSeekEngineName, QwenEngineName })
            {
                var docker = Assert.IsType<TomlTable>(DeepSeekQwenEngineTable(combined, engine)["docker"]);
                docker["socket"] = sharedSocket;
            }
            first = first with { SocketPath = sharedSocket };
            second = second with { SocketPath = sharedSocket };
        }
        var head = new EnrolledPairHead("fixture-slot", "fixture-partition", "fixture-daemon", "selector-fixture",
            Generation: 4, Revision: 8, Digest: new string('A', 64));
        ImmutableArray<string> devices = ["fixture-device-a", "fixture-device-b"];
        return (WriteDeepSeekQwenDocument(combined), new(head, [first.Member, second.Member], devices),
            new(head, [first, second], devices));
    }

    private static string ReplaceFixtureEndpoint(string text, string engine, Uri endpoint)
    {
        var document = ReadDeepSeekQwenDocument(text);
        DeepSeekQwenEngineTable(document, engine)["endpoint"] = endpoint.AbsoluteUri;
        return WriteDeepSeekQwenDocument(document);
    }

    private static EnrolledPairMemberGrant EnrolledTopologyGrant(EngineDefinition engine, string model, string tier,
        string socketPath, JsonElement reference)
    {
        var definition = engine.Catalog[model];
        var profile = Assert.Single(definition.ServingProfiles, entry => entry.Name == tier);
        var image = Assert.IsType<DockerImageIdentity>(engine.Docker!.ImageIdentity);
        var oracle = Assert.Single(reference.EnumerateArray(), row =>
            row.GetProperty("engine").GetString() == engine.Name &&
            row.GetProperty("model").GetString() == model &&
            row.GetProperty("tier").GetString() == tier).GetProperty("create");
        var launch = new LifecycleTarget(engine.Name, model, profile, definition);
        var plan = DeepSeekQwenCheckpoint(engine, launch, image, ReadPairCreate(oracle));
        return new(new(engine.Name, model, tier, plan.Configuration, plan.BackendRoute),
            socketPath, engine.Endpoint, plan.Digest());
    }

    private static string[] EnrolledTopologyDeepSeekCommand(ConfigDto dto)
    {
        var model = dto.Engines![DeepSeekEngineName].Catalog![DeepSeekPairModel];
        var tier = Assert.Single(model.Serving!, row => row.Name == "32k");
        return Assert.IsType<string[]>(tier.Docker!.Command);
    }
}
