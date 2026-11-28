using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Cli;

// An enrolment supplies this lease only after it has closed generation admission to other
// clients. Every exchange and witness remains bound to that private route and generation.
internal interface ITuneAdmission
{
    ValueTask<ITuneResident?> AcquireAsync(EngineDefinition engine, string model, string profile,
        string expectedRecipeSha256, CancellationToken cancellationToken);
}

internal interface ITuneResident
{
    ValueTask<TuneWitness?> ObserveAsync(Guid challenge, Guid? request, CancellationToken cancellationToken);
    Task<int> CountAsync(byte[] body, CancellationToken cancellationToken);
    Task<HttpResponseMessage> StartAsync(Guid request, byte[] body, CancellationToken cancellationToken);

    // Only the authority can acknowledge retirement of the lease. Losing the caller or the
    // acknowledgement cannot authorize reopening a route with an unaccounted exchange.
    Task<bool> ReleaseAsync(CancellationToken cancellationToken);
}

internal sealed record TuneWitness(Guid Challenge, string InstanceId, long Generation, bool Exclusive,
    int Active, int Queued, int ContextReserve, bool ExactSettings, TuneTarget Target, TuneHardware Hardware,
    TuneNativeSample? Sample = null);

internal sealed record TuneNativeSample(Guid Request, bool Admitted, bool Settled, int Attempts, int Errors,
    int PromptTokens, int OutputAllowance, int? ReusedTokens, int? ServedOutputTokens, int? DecodeWorkTokens,
    long? PrefillMicroseconds, long? DecodeMicroseconds);

internal sealed record TuneRequestEvidence(WorkloadRequest Coordinate, int PromptTokens, int OutputAllowance,
    int Records, int TailBytes, string CorpusSha256, string BodySha256);

internal sealed record TuneSettlementEvidence(bool Proved, long? ObservedGeneration, bool? Exclusive,
    int? Active, int? Queued, bool? StableHardware);

internal sealed record TuneSampleEvidence(Guid Request, WorkloadRequest Coordinate, string Status, string? Reason,
    string SemanticOutcome, string SemanticEndReason, string UsageAvailability, long? TtftTicks, long? TerminalTicks,
    long? PromptTokens, long? CompletionTokens, TuneNativeSample? Native, TuneSettlementEvidence Settlement);

internal sealed record TuneRunEvidence(string RunId, string Engine, string Model, string Profile, string Seed,
    string Status, string? Reason, TuneWitness? Resident, bool? LeaseReleased, TuneRequestEvidence[] Workload,
    TuneSampleEvidence[] Samples, AirTuneRecord? Summary)
{
    public int SchemaVersion { get; } = 1;
    public string Kind { get; } = "air_tune_singleton_run";
    public string ColdLoad { get; } = "unavailable";
    public string LongDecode { get; } = "untested";

    internal static AirTuneRecord? ReadSummary(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > AirTuneRecord.MaxBytes || bytes.Span is [0xef, 0xbb, 0xbf, ..]) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Member(root, "kind") != "air_tune_singleton_run" ||
                !root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("summary", out var embedded) || embedded.ValueKind != JsonValueKind.Object)
                return null;

            var summary = AirTuneRecord.Parse(Encoding.UTF8.GetBytes(embedded.GetRawText()));
            if (summary is null || summary.RunId != Member(root, "run_id") || summary.Levels is not [var level] ||
                summary.Target.Model.Value != Member(root, "model") || summary.Target.Tier.Value != Member(root, "profile") ||
                summary.Workload.Seed.Value != Member(root, "seed"))
                return null;

            var status = Member(root, "status");
            if (status != "passed")
                return status is "failed" or "inconclusive" or "refused" && (level.Status == status || level.Status == "untested")
                    ? summary : null;

            if (level.Status != "passed" || !root.TryGetProperty("lease_released", out var released) ||
                released.ValueKind != JsonValueKind.True || !root.TryGetProperty("samples", out var samples) ||
                samples.ValueKind != JsonValueKind.Array || samples.GetArrayLength() != 4)
                return null;
            foreach (var sample in samples.EnumerateArray())
                if (sample.ValueKind != JsonValueKind.Object || Member(sample, "status") != "passed") return null;
            return summary;
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Member(JsonElement row, string name) => row.TryGetProperty(name, out var member) &&
            member.ValueKind == JsonValueKind.String ? member.GetString() : null;
    }
}

internal sealed record TuneHostRecord(int SchemaVersion, string RecipeSha256);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(TuneHostRecord))]
[JsonSerializable(typeof(TuneRunEvidence))]
[JsonSerializable(typeof(AirTuneRecord))]
[JsonSerializable(typeof(TuneTarget))]
[JsonSerializable(typeof(TuneHardware))]
internal sealed partial class AirTuneRunJsonContext : JsonSerializerContext;
