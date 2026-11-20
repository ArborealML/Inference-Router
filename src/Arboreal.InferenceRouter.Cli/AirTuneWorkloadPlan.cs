using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Cli;

internal static class AirTuneWorkloadPlan
{
    private const int MaxTiers = 16;
    internal const int MaxManifestBytes = 524_288;
    private const string Usage =
        "usage: air tune plan --config FILE --engine ENGINE --model MODEL --seed HEX --max-concurrency N [--profile TIER]";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static int Run(ReadOnlySpan<string> args, Stream output, TextWriter error)
    {
        if (ParseArguments(args) is not { } options)
            return Error(error, Usage, 2);

        using var proc = AirTuneFile.OpenProcDescriptors();
        if (proc is null)
            return Error(error, "error: unsupported platform or file admission facility");

        var bytes = AirTuneFile.Read(proc, options.Path);
        if (bytes is null)
            return Error(error, "error: tuning config file could not be read");

        var config = ParseConfig(bytes, options.Path);
        if (config is null)
            return Error(error, "error: invalid tuning workload config");

        var manifest = Build(config, options.Engine, options.Model, options.Profile, options.Seed, options.Cap);
        if (manifest is null)
            return Error(error, "error: invalid tuning workload target or tier");

        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, AirTuneWorkloadJsonContext.Default.WorkloadManifest);
        return WriteManifest(json, output, error);
    }

    internal static int WriteManifest(ReadOnlySpan<byte> json, Stream output, TextWriter error)
    {
        // Reserve the terminal LF before any output.
        if (json.Length >= MaxManifestBytes)
            return Error(error, "error: tuning workload manifest exceeds its size limit");
        var payload = new byte[json.Length + 1];
        json.CopyTo(payload);
        payload[^1] = (byte)'\n';
        output.Write(payload);
        return 0;
    }

    internal static WorkloadManifest? Build(RouterConfig config, string engineKey, string spelling,
        string? profile, string seed, int cap)
    {
        if (!config.Engines.TryGetValue(engineKey, out var engine) ||
            config.Engines.Keys.Any(key => key != engineKey && key.Equals(engineKey, StringComparison.OrdinalIgnoreCase)) ||
            !AirTuneTextGenerator.IsIdentity(engineKey))
            return null;

        string? canonical = null;
        EngineModelDefinition? model = null;
        foreach (var (name, definition) in engine.Catalog)
        {
            if (!name.Equals(spelling, StringComparison.OrdinalIgnoreCase) &&
                !definition.Aliases.Contains(spelling, StringComparer.OrdinalIgnoreCase))
                continue;
            if (canonical is not null)
                return null;

            canonical = name;
            model = definition;
        }

        if (model is null || canonical is null || !AirTuneTextGenerator.IsIdentity(canonical) ||
            (model.Profile is { } label && !AirTuneTextGenerator.IsIdentity(label)))
            return null;

        var ladder = model.ServingProfiles;
        if (ladder.Count == 0 || (profile is null && ladder.Count > MaxTiers))
            return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in ladder)
            if (!names.Add(row.Name))
                return null;

        var selected = ladder.Where(row => profile is null || row.Name == profile)
            .OrderBy(row => row.MaxContext).ThenBy(row => row.Name, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0)
            return null;

        var tiers = new WorkloadTier[selected.Length];
        for (var index = 0; index < selected.Length; index++)
        {
            var row = selected[index];
            if (row.MaxContext < 672 || row.MaxSlots < 1 || !AirTuneTextGenerator.IsIdentity(row.Name))
                return null;
            tiers[index] = BuildTier(row, seed, cap);
        }

        return new WorkloadManifest(engineKey, canonical, model.Profile, seed, cap, tiers);
    }

    private static WorkloadTier BuildTier(ServingProfile tier, string seed, int cap)
    {
        var planned = Math.Min(cap, tier.MaxSlots);
        var requests = new WorkloadRequest[checked(1 + 3 * planned * (planned + 1) / 2)];
        requests[0] = new WorkloadRequest("warmup", 1, 0, 1, new AirTuneTextGenerator(seed, tier.Name, 1, 0, 1).Nonce);
        var index = 1;
        for (var concurrency = 1; concurrency <= planned; concurrency++)
        for (var wave = 1; wave <= 3; wave++)
        for (var worker = 1; worker <= concurrency; worker++)
        {
            var generator = new AirTuneTextGenerator(seed, tier.Name, concurrency, wave, worker);
            requests[index++] = new WorkloadRequest("grid", concurrency, wave, worker, generator.Nonce);
        }

        return new WorkloadTier(tier.Name, tier.MaxContext, tier.MaxSlots, planned,
            checked(tier.MaxContext - 671), checked(tier.MaxContext - 640), requests);
    }

    private static RouterConfig? ParseConfig(byte[] bytes, string path)
    {
        if (bytes.Length > AirTuneRecord.MaxBytes || bytes is [0xef, 0xbb, 0xbf, ..])
            return null;

        try
        {
            var text = StrictUtf8.GetString(bytes);
            var isJson = Path.GetExtension(path.AsSpan()).Equals(".json", StringComparison.OrdinalIgnoreCase);
            if (isJson && HasNullConfigMapEntry(text))
                return null;

            return RouterConfigLoader.Load(text, isJson: isJson);
        }
        catch (Exception exception) when (exception is RouterConfigException or JsonException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static bool HasNullConfigMapEntry(string json)
    {
        // Typed map values can deserialize as null even when the config mapper requires objects.
        // Keep the loader's JSON syntax allowances and validate these entries at the planner boundary.
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        if (HasNullEntry(root, "threads"))
            return true;
        if (!root.TryGetProperty("engine", out var engines) || engines.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var value in LastValues(engines).Values)
        {
            if (value.ValueKind == JsonValueKind.Null ||
                (value.ValueKind == JsonValueKind.Object && HasNullEntry(value, "catalog")))
                return true;
        }
        return false;

        static bool HasNullEntry(JsonElement owner, string name)
        {
            if (!owner.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var value in LastValues(map).Values)
                if (value.ValueKind == JsonValueKind.Null)
                    return true;
            return false;
        }

        static Dictionary<string, JsonElement> LastValues(JsonElement map)
        {
            // The loader's dictionaries keep the last value for each ordinal key.
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in map.EnumerateObject())
                values[entry.Name] = entry.Value;
            return values;
        }
    }

    private static PlanArguments? ParseArguments(ReadOnlySpan<string> args)
    {
        if (args.Length is not (10 or 12))
            return null;
        string? path = null, engine = null, model = null, seed = null, profile = null;
        var cap = 0;
        for (var i = 0; i < args.Length; i += 2)
        {
            var value = args[i + 1];
            if (value.Length == 0 || value[0] == '-' || value.AsSpan().Contains('\0'))
                return null;
            switch (args[i])
            {
                case "--config" when path is null && IsDirectPath(value):
                    path = value;
                    break;
                case "--engine" when engine is null:
                    engine = value;
                    break;
                case "--model" when model is null:
                    model = value;
                    break;
                case "--seed" when seed is null && AirTuneRecord.IsDigest(value):
                    seed = value;
                    break;
                case "--max-concurrency" when cap == 0 && value.Length == 1 && value[0] is >= '1' and <= '8':
                    cap = value[0] - '0';
                    break;
                case "--profile" when profile is null:
                    profile = value;
                    break;
                default:
                    return null;
            }
        }

        return path is null || engine is null || model is null || seed is null || cap == 0
            ? null : new PlanArguments(path, engine, model, seed, cap, profile);
    }

    private static bool IsDirectPath(string path)
    {
        if (path[0] == '\\' || path.StartsWith("//", StringComparison.Ordinal))
            return false;
        var colon = path.AsSpan().IndexOf(':');
        var slash = path.AsSpan().IndexOf('/');
        return (colon < 0 || slash >= 0 && slash < colon) &&
            path is not ("/dev/stdin" or "/dev/fd/0" or "/proc/self/fd/0" or "/proc/thread-self/fd/0");
    }

    private static int Error(TextWriter error, string message, int status = 1)
    {
        error.WriteLine(message);
        return status;
    }

    private sealed record PlanArguments(string Path, string Engine, string Model, string Seed, int Cap, string? Profile);
}

internal sealed record WorkloadManifest(string Engine, string Model, string? DefaultProfile, string Seed,
    int MaxConcurrency, [property: JsonPropertyOrder(2)] WorkloadTier[] Tiers)
{
    [JsonPropertyOrder(-5)]
    public string Kind { get; } = "air_tune_workload_plan";
    [JsonPropertyOrder(-4)]
    public int SchemaVersion { get; } = 1;
    [JsonPropertyOrder(-3)]
    public string Workload { get; } = "near-window-text-v1";
    [JsonPropertyOrder(-2)]
    public string Generator { get; } = AirTuneTextGenerator.Id;
    [JsonPropertyOrder(-1)]
    public string GeneratorSha256 { get; } = AirTuneTextGenerator.DefinitionSha256;
    [JsonPropertyOrder(1)]
    public WorkloadPolicy Policy { get; } = new();
}

internal sealed record WorkloadTier(string Profile, long ConfiguredContext, int ConfiguredSlots, int PlannedCap,
    long NominalPromptMin, long NominalPromptMax, WorkloadRequest[] Requests);

internal sealed record WorkloadRequest(string Kind, int Concurrency, int Wave, int Worker, string Nonce);

internal sealed class WorkloadPolicy
{
    public string FitPolicy { get; } = "near-window-fit-policy-v1";
    public int TargetOutputTokens { get; } = 512;
    public int WavesPerLevel { get; } = 3;
    public int RequestSeconds { get; } = 600;
    public int OutputIdleSeconds { get; } = 60;
    public int TierSeconds { get; } = 5_400;
    public int AbortDrainReserveSeconds { get; } = 30;
    public int MaxPrefixCountCalls { get; } = 16;
    public int MaxTailCountCalls { get; } = 8;
    public int MaxCountCalls { get; } = 24;
    public int MaxCountInputBytesPerPrompt { get; } = 8_388_608;
    public int MaxCountBodyBytesPerCall => MaxCountInputBytesPerPrompt / MaxCountCalls;
    public int MaxCandidateBytes { get; } = AirTuneTextGenerator.MaxCandidateBytes;
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(WorkloadManifest))]
internal sealed partial class AirTuneWorkloadJsonContext : JsonSerializerContext;
