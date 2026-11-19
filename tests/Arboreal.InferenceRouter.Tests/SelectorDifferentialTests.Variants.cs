using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arboreal.InferenceRouter.Lifecycle;
using Tomlyn;
using Tomlyn.Model;
using Xunit.Sdk;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class SelectorDifferentialTests
{
    private static readonly string[] OtherEngines = ["minimax", "glm", "maya_xs", "deepseek", HyperQwen];

    private static (int MixedParserChecks, int WrongShm) CheckHyperQwen(
        Row[] rows, HashSet<CaseKey> keys, NoHttp deny, Specimen specimen)
    {
        Assert.Equal(26, rows.Length);
        var deviceCoverage = new HashSet<(string Key, string Devices)>();
        var parkingRows = rows.Where(row => row.Parking).ToArray();
        Assert.Equal(12, parkingRows.Length);
        foreach (var row in parkingRows)
        {
            var parking = row.Data.GetProperty("parking");
            Assert.Equal(1, parking.GetProperty("cache_min").GetInt32());
            Assert.Equal(48, parking.GetProperty("cache_max").GetInt32());
            Assert.Equal(32, parking.GetProperty("cache_default").GetInt32());
            var variable = row.Tier == "parked";
            Assert.Equal(variable ? 1 : row.SelectorLanes, parking.GetProperty("lanes_min").GetInt32());
            Assert.Equal(variable ? 8 : row.SelectorLanes, parking.GetProperty("lanes_max").GetInt32());
            Assert.Equal(variable ? 2 : row.SelectorLanes, parking.GetProperty("lanes_default").GetInt32());
        }

        var raw = ParseToml(specimen.Text);
        for (var cache = 1; cache <= 48; cache++)
        for (var lanes = 1; lanes <= 8; lanes++)
        for (var device = 0; device < 2; device++)
        foreach (var utilization in Utilizations)
        {
            var shape = new Shape(cache, lanes, device == 1, utilization);
            WriteHyperQwenInputs(raw, shape);
            var engine = LoadJson(raw, HyperQwen);
            using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, deny);
            foreach (var row in rows)
            {
                // Fixed tiers do not accept variable lanes. Ordinary tiers have no cache argument.
                if (row.Tier != "parked" && (lanes != 1 || (!row.Parking && cache != 1))) continue;
                var devices = CompareHyperQwen(provider, engine, row, shape, keys);
                deviceCoverage.Add((row.Key, devices));
            }
        }

        foreach (var row in rows)
        {
            var choices = row.Data.GetProperty("devices").EnumerateArray().Select(DeviceList).ToArray();
            Assert.Equal(2, choices.Length);
            Assert.NotEqual(choices[0], choices[1]);
            foreach (var choice in choices)
                Assert.Contains((row.Key, choice), deviceCoverage);
        }
        Assert.Equal(52, deviceCoverage.Count);

        var paired = rows.Single(row => row.Model == "swift" && row.Tier == "max-2");
        var single = rows.Single(row => row.Model == "qwen-3.8-27b" && row.Tier == "single-64k");
        var parked = rows.Single(row => row.Model == "swift" && row.Tier == "parked");
        var mixedParserChecks = 0;
        foreach (var extra in new (Row Row, Shape Shape, bool CompareToml)[]
        {
            (paired, new Shape(32, 2, true, "0.873"), true),
            (single, new Shape(32, 2, true, "0.873"), true),
            (parked, new Shape(48, 8, true, "0.873"), true),
            (paired, new Shape(32, 2, true, "0.5000000000000001"), false),
            (parked, new Shape(1, 1, false, "0.9499999999999998"), false),
        })
        {
            WriteHyperQwenInputs(raw, extra.Shape);
            CompareSingleHyperQwen(LoadJson(raw, HyperQwen), deny, extra.Row, extra.Shape, keys);
            if (extra.CompareToml)
            {
                var toml = TomlSerializer.Serialize(raw, ConfigTomlDocumentContext.Default.TomlTable);
                var tomlEngine = RouterConfigLoader.Load(toml).Engines[HyperQwen];
                CompareSingleHyperQwen(tomlEngine, deny, extra.Row, extra.Shape, keys: null);
                mixedParserChecks++;
            }
        }

        // Compare a deliberately wrong reference to a real, unchanged production projection.
        var baseline = specimen.Engine;
        using var baselineProvider = new DockerLifecycleProvider(baseline.Docker!, baseline.Name, deny);
        var definition = baseline.Catalog[parked.Model];
        var actual = Create(baselineProvider, baseline, definition, parked.Model, FindTier(definition, parked.Tier));
        var wrong = JsonNode.Parse(parked.Create.GetRawText())!;
        wrong["HostConfig"]!["ShmSize"] = 36 * GiB + 1;
        using var wrongDocument = JsonDocument.Parse(wrong.ToJsonString());
        var wrongShm = 0;
        var rejection = Assert.Throws<FailException>(() => Match(wrongDocument.RootElement, actual, parked.Key));
        Assert.Contains($"{parked.Key}: mismatch at $.HostConfig.ShmSize. Expected ", rejection.Message, StringComparison.Ordinal);
        wrongShm++;
        Match(parked.Create, actual, parked.Key);
        return (mixedParserChecks, wrongShm);
    }

    private static void CompareSingleHyperQwen(EngineDefinition engine, NoHttp deny, Row row,
        Shape shape, HashSet<CaseKey>? keys)
    {
        using var provider = new DockerLifecycleProvider(engine.Docker!, engine.Name, deny);
        CompareHyperQwen(provider, engine, row, shape, keys);
    }

    private static string CompareHyperQwen(DockerLifecycleProvider provider, EngineDefinition engine, Row row,
        Shape shape, HashSet<CaseKey>? keys)
    {
        var definition = engine.Catalog[row.Model];
        var profile = FindTier(definition, row.Tier);
        var lanes = row.Lanes(shape);
        CheckCapacity(row, profile, row.Tier == "parked" ? lanes : row.Slots);
        var actual = Create(provider, engine, definition, row.Model, profile);
        var choices = row.Data.GetProperty("devices");
        // The recorded single-device config chooses [1]. The paired config chooses [0,1].
        var deviceIndex = choices[0].GetArrayLength() == 1
            ? (shape.ReverseDevices ? 0 : 1)
            : (shape.ReverseDevices ? 1 : 0);
        var wantedDevices = choices[deviceIndex];
        using var expected = ReferenceVariant(row, shape, lanes, wantedDevices);
        var devices = DeviceList(actual.GetProperty("HostConfig").GetProperty("DeviceRequests")[0].GetProperty("DeviceIDs"));
        var key = new CaseKey(row.Engine, row.Model, row.Tier, row.Parking ? shape.Cache : 0, lanes, devices, shape.Utilization);
        Match(expected.RootElement, actual, key.ToString());
        if (keys is not null) Assert.True(keys.Add(key), $"Duplicate positive vector: {key}");
        return devices;
    }

    private static string DeviceList(JsonElement ids) =>
        string.Join(',', ids.EnumerateArray().Select(id => id.GetString()));

    private static JsonDocument ReferenceVariant(Row row, Shape shape, int lanes, JsonElement devices)
    {
        // The branch reference is a full create document. Only these argument-dependent
        // fields change across the archived option ranges.
        var document = JsonNode.Parse(row.Create.GetRawText())!.AsObject();
        var host = document["HostConfig"]!;
        host["DeviceRequests"]![0]!["DeviceIDs"] = JsonNode.Parse(devices.GetRawText());
        var env = document["Env"]!.AsArray();
        ReplaceReferenceValue(env, "GPU_UTIL", shape.Utilization);
        if (row.Parking)
        {
            host["ShmSize"] = (shape.Cache + 4) * GiB;
            ReplaceReferenceValue(env, "KV_OFFLOAD_GB", shape.Cache.ToString(CultureInfo.InvariantCulture));
            ReplaceReferenceValue(env, "MAX_SEQS", lanes.ToString(CultureInfo.InvariantCulture));
        }
        return JsonDocument.Parse(document.ToJsonString());
    }

    private static void ReplaceReferenceValue(JsonArray env, string name, string value)
    {
        var prefix = name + "=";
        var index = Enumerable.Range(0, env.Count)
            .Single(i => env[i]!.GetValue<string>().StartsWith(prefix, StringComparison.Ordinal));
        env[index] = prefix + value;
    }

    private static void WriteHyperQwenInputs(TomlTable input, Shape shape)
    {
        var engine = RawEngine(input, HyperQwen);
        var docker = (TomlTable)engine["docker"]!;
        var resources = (TomlTable)docker["resources"]!;
        var sharedGpu = (TomlTable)resources["gpu"]!;
        sharedGpu["device_ids"] = shape.ReverseDevices ? new TomlArray { "1", "0" } : new TomlArray { "0", "1" };

        foreach (var (_, model) in (TomlTable)engine["catalog"]!)
        foreach (var tier in TableRows(((TomlTable)model!)["serving"]))
        {
            var target = (TomlTable)tier["docker"]!;
            var values = (TomlArray)target["env"]!;
            if (target.TryGetValue("gpu", out var gpu))
                ((TomlTable)gpu!)["device_ids"] = new TomlArray { shape.ReverseDevices ? "0" : "1" };

            // Shm is physically configured in bytes. The backend receives cache in GiB.
            var parked = (string?)tier["name"] == "parked";
            if (target.ContainsKey("shm_size_bytes")) target["shm_size_bytes"] = ((long)shape.Cache + 4) << 30;
            if (parked) tier["max_slots"] = (long)shape.Lanes;
            for (var index = 0; index < values.Count; index++)
            {
                var entry = (string)values[index]!;
                if (entry.StartsWith("GPU_UTIL=", StringComparison.Ordinal))
                    values[index] = "GPU_UTIL=" + shape.Utilization;
                else if (entry.StartsWith("KV_OFFLOAD_GB=", StringComparison.Ordinal))
                    values[index] = FormattableString.Invariant($"KV_OFFLOAD_GB={shape.Cache}");
                else if (parked && entry.StartsWith("MAX_SEQS=", StringComparison.Ordinal))
                    values[index] = FormattableString.Invariant($"MAX_SEQS={shape.Lanes}");
            }
        }
    }

    private static int CheckRefusals(Dictionary<string, Specimen> specimens)
    {
        var count = 0;
        var hyper = ParseToml(specimens[HyperQwen].Text);
        var hyperDocker = (TomlTable)RawEngine(hyper, HyperQwen)["docker"]!;
        var resources = (TomlTable)hyperDocker["resources"]!;
        var gpu = (TomlTable)resources["gpu"]!;
        var goodIds = gpu["device_ids"];
        gpu["device_ids"] = new TomlArray { "0", "0" };
        Refuses(() => LoadJson(hyper, HyperQwen), "unique canonical numeric GPU index");
        gpu["device_ids"] = goodIds;

        var rawModel = (TomlTable)((TomlTable)RawEngine(hyper, HyperQwen)["catalog"]!)["swift"]!;
        var parked = TableRows(rawModel["serving"]).Single(tier => (string?)tier["name"] == "parked");
        var target = (TomlTable)parked["docker"]!;
        var goodShm = target["shm_size_bytes"];
        target["shm_size_bytes"] = 0L;
        Refuses(() => LoadJson(hyper, HyperQwen), "shm_size_bytes must be a positive Int64 integer");
        target["shm_size_bytes"] = goodShm;
        var engine = LoadJson(hyper, HyperQwen);
        Refuses(() => engine.Docker!.TargetPlans!.Resolve(HyperQwen, "swift-27b", "absent"),
            "profile is not in the captured catalog");

        var strata = ParseToml(specimens["strata"].Text);
        var strataResources = (TomlTable)((TomlTable)RawEngine(strata, "strata")["docker"]!)["resources"]!;
        var keyMount = TableRows(strataResources["bind_mounts"]).First();
        keyMount["target"] = "/run/fixture-token";
        Refuses(() => LoadJson(strata, "strata"), "outside control directories");
        keyMount["target"] = "/opt/strata/fixture-token";

        // Combine all six already valid engine tables without downgrading their authority.
        foreach (var name in OtherEngines)
            ((TomlTable)strata["engine"]!).Add(name, RawEngine(ParseToml(specimens[name].Text), name));
        Refuses(() => LoadJson(strata, "strata"), "requires a dedicated single-engine configuration");
        return count;

        void Refuses(Action operation, string reason)
        {
            var exception = Assert.Throws<RouterConfigException>(operation);
            Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
            count++;
        }
    }

    private static TomlTable ParseToml(string value) =>
        TomlSerializer.Deserialize(value, ConfigTomlDocumentContext.Default.TomlTable)!;

    private static TomlTable RawEngine(TomlTable document, string engine) =>
        (TomlTable)((TomlTable)document["engine"]!)[engine]!;

    private static TomlTable FindRawTier(TomlTable document, Row row)
    {
        var model = (TomlTable)((TomlTable)RawEngine(document, row.Engine)["catalog"]!)[row.Model]!;
        return TableRows(model["serving"]).Single(tier => (string?)tier["name"] == row.Tier);
    }

    private static IEnumerable<TomlTable> TableRows(object? value) => value switch
    {
        TomlTableArray tables => tables,
        TomlArray array => array.Cast<TomlTable>(),
        _ => throw new InvalidOperationException("Expected a TOML array of tables."),
    };

    private static EngineDefinition LoadJson(TomlTable document, string engine)
    {
        // Transcode the untyped TOML document, preserving present and absent declarations.
        // The production JSON loader still must accept its own input and resolve the target.
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
            WriteJson(writer, document);
        return RouterConfigLoader.Load(Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length), isJson: true)
            .Engines[engine];
    }

    private static void WriteJson(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case TomlTable table:
                writer.WriteStartObject();
                foreach (var (name, child) in table)
                {
                    writer.WritePropertyName(name);
                    WriteJson(writer, child);
                }
                writer.WriteEndObject();
                break;
            case TomlTableArray tables:
                writer.WriteStartArray();
                foreach (var table in tables) WriteJson(writer, table);
                writer.WriteEndArray();
                break;
            case TomlArray array:
                writer.WriteStartArray();
                foreach (var item in array) WriteJson(writer, item);
                writer.WriteEndArray();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            default:
                throw new InvalidOperationException($"Unsupported selector fixture TOML value: {value?.GetType().Name}");
        }
    }
}
