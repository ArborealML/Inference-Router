using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Cli;

// An enrolment supplies this lease only after it has closed generation admission to other
// clients. Every exchange and witness remains bound to that private route and generation.
internal interface ITuneAdmission
{
    ValueTask<ITuneResident?> AcquireAsync(EngineDefinition engine, string model, string profile,
        string expectedRecipeSha256, CancellationToken cancellationToken);
}

internal interface ITuneResident : IAsyncDisposable
{
    // Disposal ends the client's lease. The authority keeps the route fenced if an owned
    // exchange cannot be shown to have settled, even after the client's timers expire.
    ValueTask<TuneWitness?> ObserveAsync(Guid challenge, Guid? request, CancellationToken cancellationToken);
    Task<int> CountAsync(byte[] body, CancellationToken cancellationToken);
    Task<HttpResponseMessage> StartAsync(Guid request, byte[] body, CancellationToken cancellationToken);
}

internal sealed record TuneWitness(Guid Challenge, string InstanceId, long Generation, bool Exclusive,
    int Active, int Queued, int ContextReserve, bool ExactSettings, TuneTarget Target, TuneHardware Hardware,
    TuneNativeSample? Sample = null);

internal sealed record TuneNativeSample(Guid Request, bool Admitted, bool Settled, int Attempts, int Errors,
    int PromptTokens, int OutputAllowance, int? ReusedTokens, int? DecodeTokens,
    long? PrefillMicroseconds, long? DecodeMicroseconds);

internal sealed record TuneRequestEvidence(WorkloadRequest Coordinate, int PromptTokens, int OutputAllowance,
    int Records, int TailBytes, string CorpusSha256, string BodySha256);

internal sealed record TuneSampleEvidence(Guid Request, WorkloadRequest Coordinate, string Status, string? Reason,
    long? TtftTicks, long? TerminalTicks, long? PromptTokens, long? CompletionTokens, TuneNativeSample? Native);

internal sealed record TuneRunEvidence(string RunId, string Engine, string Model, string Profile, string Seed,
    string Status, string? Reason, TuneWitness? Resident, TuneRequestEvidence[] Workload,
    TuneSampleEvidence[] Samples, AirTuneRecord? Summary)
{
    public int SchemaVersion { get; } = 1;
    public string Kind { get; } = "air_tune_singleton_run";
    public string ColdLoad { get; } = "unavailable";
    public string LongDecode { get; } = "untested";
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
