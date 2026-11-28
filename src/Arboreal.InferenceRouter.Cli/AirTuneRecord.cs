using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Cli;

internal sealed class AirTuneRecord
{
    internal const int MaxBytes = 65_536;
    private const int MaxDepth = 10;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public required int SchemaVersion { get; init; }
    public required string RunId { get; init; }
    public required TuneFact<string> InstanceId { get; init; }
    public required TuneHardware Hardware { get; init; }
    public required TuneTarget Target { get; init; }
    public required TuneWorkload Workload { get; init; }
    public required int Cap { get; init; }
    public required TuneLevel[] Levels { get; init; }

    [JsonIgnore]
    internal JsonElement Evidence { get; private set; }

    internal static AirTuneRecord? Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxBytes || bytes.Span is [0xef, 0xbb, 0xbf, ..])
            return null;

        try
        {
            _ = StrictUtf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = MaxDepth,
                AllowDuplicateProperties = false,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !ValidTokens(root))
                return null;

            var record = root.Deserialize(AirTuneJsonContext.Default.AirTuneRecord);
            if (record is null || !record.IsValid())
                return null;

            record.Evidence = root.Clone();
            return record;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool IsValid()
    {
        if (SchemaVersion != 1 || !IsHex(RunId, 32) || !InstanceId.IsValid(value => IsHex(value, 32)) ||
            !Hardware.IsValid() || !Target.IsValid() || !Workload.IsValid() ||
            Cap is < 1 or > 8 || Levels.Length != Cap ||
            Target.ConfiguredSlots.Value is int configured && Cap > configured ||
            Target.ObservedSlots.Value is int observed && observed > 0 && Cap > observed)
        {
            return false;
        }

        var previousDidNotPass = false;
        for (var index = 0; index < Levels.Length; index++)
        {
            var level = Levels[index];
            if (level.Concurrency != index + 1 || !level.IsValid() ||
                (previousDidNotPass || Target.ObservedSlots.Value == 0) && level.Status != "untested" ||
                level.Reason == "prior_level_did_not_pass" && (!previousDidNotPass || level.Status != "untested"))
            {
                return false;
            }

            previousDidNotPass = level.Status != "passed";
        }

        return true;
    }

    // Generated metadata owns object member names and required members. This pass also
    // checks decoded strings, absent-vs-null semantics, and the raw numeric grammar.
    internal static bool ValidTokens(JsonElement element, bool rates = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Length > 128 ||
                        !ValidTokens(property.Value, property.NameEquals("wave_output_tokens_per_second")))
                    {
                        return false;
                    }
                }
                return true;
            case JsonValueKind.Array:
                if (element.GetArrayLength() > 16)
                    return false;
                foreach (var value in element.EnumerateArray())
                {
                    if (!ValidTokens(value, rates))
                        return false;
                }
                return true;
            case JsonValueKind.String:
                return IsToken(element.GetString()!);
            case JsonValueKind.Number:
                return ValidNumber(element.GetRawText(), rates);
            default:
                return false;
        }
    }

    private static bool ValidNumber(string raw, bool rate)
    {
        var integral = raw.AsSpan();
        if (rate)
        {
            var point = integral.IndexOf('.');
            if (point >= 0)
            {
                var fractional = integral[(point + 1)..];
                if (fractional.Length is < 1 or > 9 || !IsDigits(fractional))
                    return false;
                integral = integral[..point];
            }
        }

        return integral.Length >= 1 && integral.Length <= (rate ? 9 : 19) && IsDigits(integral);
    }

    private static bool IsDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }
        return true;
    }

    private static bool IsToken(string value)
    {
        if (value.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(value[0]))
            return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or ':' or '@' or '/' or '+' or '-'))
                return false;
        }
        return true;
    }

    private static bool IsHex(string value, int length)
    {
        if (value.Length != length)
            return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    internal static bool IsDigest(string value) => IsHex(value, 64);
    internal static bool IsRedactedId(string value) => IsHex(value, 32);
}

internal sealed class TuneFact<T>
{
    public required string Kind { get; init; }
    public T? Value { get; init; }

    internal bool IsValid(Func<T, bool> valid) => Kind switch
    {
        "known" => Value is { } value && valid(value),
        "unknown" => Value is null,
        _ => false,
    };

    internal bool IsValid() => IsValid(static _ => true);
}

internal sealed class TuneLimit<T>
{
    public required string Kind { get; init; }
    public T? Value { get; init; }

    internal bool IsValid(Func<T, bool> valid) => Kind switch
    {
        "finite" => Value is { } value && valid(value),
        "unknown" or "unlimited" => Value is null,
        _ => false,
    };
}

internal sealed class TuneHardware
{
    public required TuneFact<long?> PhysicalRamBytes { get; init; }
    public required TuneFact<long?> PhysicalSwapBytes { get; init; }
    public required TuneLimit<long?> RamLimitBytes { get; init; }
    public required TuneLimit<long?> RamPlusSwapLimitBytes { get; init; }
    public required TuneLimit<long?> SwapOnlyLimitBytes { get; init; }
    public required TuneFact<string> LimitSource { get; init; }
    public required TuneFact<string> CpuModel { get; init; }
    public required TuneFact<string> CpuAffinitySha256 { get; init; }
    public required TuneLimit<TuneCpuQuota> CpuQuota { get; init; }
    public required TuneFact<TuneGpu[]> Gpus { get; init; }
    public required TuneFact<string> Os { get; init; }
    public required TuneFact<string> Driver { get; init; }
    public required TuneFact<string> Power { get; init; }
    public required TuneFact<string> Thermals { get; init; }
    public required TuneFact<string> Background { get; init; }

    internal bool IsValid() =>
        PhysicalRamBytes.IsValid(static value => value > 0) && PhysicalSwapBytes.IsValid() &&
        RamLimitBytes.IsValid(static _ => true) && RamPlusSwapLimitBytes.IsValid(static _ => true) &&
        SwapOnlyLimitBytes.IsValid(static _ => true) && LimitSource.IsValid() && CpuModel.IsValid() &&
        CpuAffinitySha256.IsValid(AirTuneRecord.IsDigest) &&
        CpuQuota.IsValid(static quota => quota is { QuotaUs: > 0, PeriodUs: > 0 }) &&
        Gpus.IsValid(ValidGpus) && Os.IsValid() && Driver.IsValid() && Power.IsValid() &&
        Thermals.IsValid() && Background.IsValid();

    private static bool ValidGpus(TuneGpu[] gpus)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gpu in gpus)
        {
            if (!ids.Add(gpu.Id) || !gpu.VramBytes.IsValid(static value => value > 0))
                return false;
        }
        return true;
    }
}

internal sealed class TuneCpuQuota
{
    public required long QuotaUs { get; init; }
    public required long PeriodUs { get; init; }
}

internal sealed class TuneGpu
{
    public required string Id { get; init; }
    public required TuneFact<long?> VramBytes { get; init; }
}

internal sealed class TuneTarget
{
    public required TuneFact<string> Model { get; init; }
    public required TuneFact<string> Tier { get; init; }
    public required TuneFact<string> Quantization { get; init; }
    public required TuneFact<string> RuntimeBuild { get; init; }
    public required TuneFact<string> WeightsSha256 { get; init; }
    public required TuneFact<string> ExecutableSha256 { get; init; }
    public required TuneFact<string> TokenizerSha256 { get; init; }
    public required TuneFact<string> TemplateSha256 { get; init; }
    public required TuneFact<string> RecipeSha256 { get; init; }
    public required TuneFact<string> SharedSettingsSha256 { get; init; }
    public required TuneFact<string> CachePolicySha256 { get; init; }
    public required TuneFact<string> DeadlinePolicySha256 { get; init; }
    public required TuneFact<string> AdapterSha256 { get; init; }
    public required TuneFact<long?> NominalContext { get; init; }
    public required TuneFact<long?> ObservedContext { get; init; }
    public required TuneFact<long?> UsableContext { get; init; }
    public required TuneFact<int?> ConfiguredSlots { get; init; }
    public required TuneFact<int?> ObservedSlots { get; init; }

    internal bool IsValid() =>
        Model.IsValid() && Tier.IsValid() && Quantization.IsValid() && RuntimeBuild.IsValid() &&
        WeightsSha256.IsValid(AirTuneRecord.IsDigest) && ExecutableSha256.IsValid(AirTuneRecord.IsDigest) &&
        TokenizerSha256.IsValid(AirTuneRecord.IsDigest) && TemplateSha256.IsValid(AirTuneRecord.IsDigest) &&
        RecipeSha256.IsValid(AirTuneRecord.IsDigest) && SharedSettingsSha256.IsValid(AirTuneRecord.IsDigest) &&
        CachePolicySha256.IsValid(AirTuneRecord.IsDigest) && DeadlinePolicySha256.IsValid(AirTuneRecord.IsDigest) &&
        AdapterSha256.IsValid(AirTuneRecord.IsDigest) &&
        NominalContext.IsValid(static value => value > 0) &&
        ObservedContext.IsValid(static value => value > 0) &&
        UsableContext.IsValid(static value => value > 0) &&
        !(UsableContext.Value > ObservedContext.Value) &&
        ConfiguredSlots.IsValid(static value => value > 0) && ObservedSlots.IsValid();
}

internal sealed class TuneWorkload
{
    public required string Id { get; init; }
    public required TuneFact<string> Generator { get; init; }
    public required TuneFact<string> GeneratorSha256 { get; init; }
    public required TuneFact<string> Seed { get; init; }
    public required TuneFact<string> WarmupCorpusSha256 { get; init; }
    public required TuneFact<string> WarmupBodySha256 { get; init; }

    internal bool IsValid() =>
        Id == "near-window-text-v1" && Generator.IsValid() && GeneratorSha256.IsValid(AirTuneRecord.IsDigest) &&
        Seed.IsValid(AirTuneRecord.IsDigest) && WarmupCorpusSha256.IsValid(AirTuneRecord.IsDigest) &&
        WarmupBodySha256.IsValid(AirTuneRecord.IsDigest);
}

internal sealed class TuneLevel
{
    public required int Concurrency { get; init; }
    public required string Status { get; init; }
    public required TuneFact<string> CorpusSha256 { get; init; }
    public required TuneFact<string> BodySha256 { get; init; }
    public decimal[]? WaveOutputTokensPerSecond { get; init; }
    public string? Reason { get; init; }

    internal bool IsValid()
    {
        if (!CorpusSha256.IsValid(AirTuneRecord.IsDigest) || !BodySha256.IsValid(AirTuneRecord.IsDigest))
            return false;
        if (Status == "passed")
            return Reason is null && WaveOutputTokensPerSecond is [> 0, > 0, > 0];

        return Status is "failed" or "inconclusive" or "untested" &&
            Reason is "preflight_refusal" or "prior_level_did_not_pass" or "budget_exhausted" or
                "workload_failure" or "insufficient_evidence" or "other" &&
            WaveOutputTokensPerSecond is null &&
            (Status != "untested" || CorpusSha256.Kind == "unknown" && BodySha256.Kind == "unknown");
    }

    internal decimal? Median()
    {
        if (WaveOutputTokensPerSecond is not [var a, var b, var c])
            return null;

        return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
    }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AirTuneRecord))]
internal sealed partial class AirTuneJsonContext : JsonSerializerContext;
