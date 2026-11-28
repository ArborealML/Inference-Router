using System.Text.Json;
using System.Text.Unicode;

namespace Arboreal.InferenceRouter;

public enum SemanticStreamOutcome : byte { Unknown, Complete, Incomplete, Failed }

public enum SemanticStreamEndReason : byte { Unknown, Complete, UpstreamClosedWithoutTerminator, StreamIncomplete, Failed }

public enum StreamModelIdentity : byte { Unknown, Matched, Mismatch, Conflicting }

public enum StreamUsageAvailability : byte { Unavailable, Available, Invalid }

/// <summary>Bounded upstream evidence for one streaming attempt. Timing starts when the SSE pump begins.
/// This is independent of the ticket's delivery state and carries no physical or financial settlement.</summary>
public sealed record SemanticStreamObservation
{
    public TimeSpan? FirstRawByte { get; init; }
    public TimeSpan? FirstSemanticOutput { get; init; }
    public string? ObservedModel { get; init; }
    public StreamModelIdentity ModelIdentity { get; init; }
    public StreamUsageAvailability UsageAvailability { get; init; }
    public long? PromptTokens { get; init; }
    public long? CompletionTokens { get; init; }
    public SemanticStreamOutcome Outcome { get; init; }
    public SemanticStreamEndReason EndReason { get; init; }

    internal static SemanticStreamObservation Unknown { get; } = new();
    internal static SemanticStreamObservation Failed { get; } = new()
    {
        Outcome = SemanticStreamOutcome.Failed,
        EndReason = SemanticStreamEndReason.Failed,
    };

    internal string OutcomeName => Outcome switch
    {
        SemanticStreamOutcome.Complete => "complete",
        SemanticStreamOutcome.Incomplete => "incomplete",
        SemanticStreamOutcome.Failed => "failed",
        _ => "unknown",
    };

    internal string EndReasonName => EndReason switch
    {
        SemanticStreamEndReason.Complete => "complete",
        SemanticStreamEndReason.UpstreamClosedWithoutTerminator => "upstream_closed_without_terminator",
        SemanticStreamEndReason.StreamIncomplete => "stream_incomplete",
        SemanticStreamEndReason.Failed => "failed",
        _ => "unknown",
    };
}

internal enum StreamInputEnd : byte { Unobserved, Eof, Torn, Fault, UpstreamCancelledRead }

internal readonly record struct ExactLengthStreamEvidence(
    bool Complete, TimeSpan? LastSemanticOutput, TimeSpan? Terminator);

/// <summary>Observes fully framed SSE data on the upstream producer. Parsing is synchronous and commits
/// only scalars. The ticket's terminal owner can take a consistent snapshot while the producer unwinds.</summary>
internal sealed class SemanticStreamObserver(TimeProvider clock, string expectedModel, ulong expectedChoices,
    bool requireExactLength = false)
{
    internal const int MaxModelUtf8Bytes = 512;
    private const int MaxChoices = sizeof(ulong) * 8;
    private static readonly JsonReaderOptions s_json = new() { MaxDepth = 32 };

    private readonly object _gate = new();
    private readonly long _started = clock.GetTimestamp();
    private readonly ExactLengthEvidence? _exact = requireExactLength ? new() : null;
    private TimeSpan? _firstRawByte;
    private TimeSpan? _firstSemanticOutput;
    private string? _model;
    private StreamModelIdentity _identity;
    private bool _invalidModel;
    private StreamUsageAvailability _usage;
    private long _promptTokens;
    private long _completionTokens;
    private ulong _seenChoices;
    private ulong _finishedChoices;
    private bool _unverifiedChoice;
    private bool _invalidPayload;
    private bool _sawDone;
    private bool _sawError;
    private StreamInputEnd _end;

    // These two flags are read only by the upstream producer. Snapshot accesses them under _gate.
    internal bool SawDone => _sawDone;
    internal bool SawError => _sawError;
    internal bool HasInvalidPayload => _invalidPayload || _invalidModel;
    internal bool HasSemanticOutput => _firstSemanticOutput is not null;
    internal StreamModelIdentity ModelIdentity => _identity;
    internal const string ReportedError = "upstream stream reported an error";

    internal TimeSpan ObserveRawByte()
    {
        lock (_gate) return _firstRawByte ??= clock.GetElapsedTime(_started);
    }

    internal void End(StreamInputEnd end)
    {
        lock (_gate) _end = end;
    }

    internal bool ObserveData(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            if (_exact is { } exact)
            {
                lock (_gate) exact.Invalid = true;
            }
            return false;
        }
        if (payload.SequenceEqual("[DONE]"u8))
        {
            lock (_gate)
            {
                if (_exact is { } exact)
                {
                    exact.Invalid |= _sawDone || !exact.Finished || !exact.HasTerminalUsage;
                    exact.Terminator ??= clock.GetElapsedTime(_started);
                }
                _sawDone = true;
            }
            return false;
        }

        var chunk = new Chunk();
        try
        {
            if (Utf8.IsValid(payload)) Parse(payload, ref chunk);
            else chunk.InvalidPayload = true;
        }
        catch (JsonException)
        {
            chunk.InvalidPayload = true;
        }
        catch (InvalidOperationException)
        {
            // Utf8JsonReader.GetString also rejects invalid UTF-16 escapes in a model identifier.
            chunk.InvalidPayload = true;
        }

        lock (_gate)
        {
            _sawError |= chunk.Error;
            _invalidPayload |= chunk.InvalidPayload;
            if (chunk.Error || chunk.InvalidPayload) return false;
            if (chunk.SemanticOutput && (_firstSemanticOutput is null || _exact is not null))
            {
                var at = clock.GetElapsedTime(_started);
                _firstSemanticOutput ??= at;
                if (_exact is { } exact) exact.LastSemanticOutput = at;
            }
            _exact?.Observe(chunk, _sawDone, expectedChoices, expectedModel);
            _seenChoices |= chunk.SeenChoices;
            _finishedChoices = (_finishedChoices & ~chunk.SeenChoices) | chunk.FinishedChoices;
            _unverifiedChoice |= chunk.UnverifiedChoice;
            RecordModel(chunk.Model, chunk.InvalidModel);
            RecordUsage(chunk);
            return chunk.SemanticOutput;
        }
    }

    internal ExactLengthStreamEvidence GetExactLengthEvidence()
    {
        lock (_gate)
        {
            if (_exact is not { } exact) return default;
            return new ExactLengthStreamEvidence(_end == StreamInputEnd.Eof && _sawDone &&
                !_sawError && !_invalidPayload && !exact.Invalid && exact.LastSemanticOutput is not null,
                exact.LastSemanticOutput, exact.Terminator);
        }
    }

    internal SemanticStreamObservation Snapshot()
    {
        lock (_gate)
        {
            var (outcome, reason) = Classify();
            return new SemanticStreamObservation
            {
                FirstRawByte = _firstRawByte,
                FirstSemanticOutput = _firstSemanticOutput,
                ObservedModel = _model,
                ModelIdentity = _identity,
                UsageAvailability = _usage,
                PromptTokens = _usage == StreamUsageAvailability.Available ? _promptTokens : null,
                CompletionTokens = _usage == StreamUsageAvailability.Available ? _completionTokens : null,
                Outcome = outcome,
                EndReason = reason,
            };
        }
    }

    private (SemanticStreamOutcome, SemanticStreamEndReason) Classify()
    {
        if (_sawError || _end == StreamInputEnd.Fault)
            return (SemanticStreamOutcome.Failed, SemanticStreamEndReason.Failed);
        if (_end == StreamInputEnd.Torn)
            return (SemanticStreamOutcome.Incomplete, SemanticStreamEndReason.StreamIncomplete);
        if (_end == StreamInputEnd.Unobserved || _invalidPayload)
            return (SemanticStreamOutcome.Unknown, SemanticStreamEndReason.Unknown);
        if (_sawDone || (_end == StreamInputEnd.Eof && !_unverifiedChoice && expectedChoices != 0 &&
            (_finishedChoices & expectedChoices) == expectedChoices && _finishedChoices == _seenChoices))
            return (SemanticStreamOutcome.Complete, SemanticStreamEndReason.Complete);
        return _end == StreamInputEnd.Eof
            ? (SemanticStreamOutcome.Incomplete, SemanticStreamEndReason.UpstreamClosedWithoutTerminator)
            : (SemanticStreamOutcome.Unknown, SemanticStreamEndReason.Unknown);
    }

    private void RecordModel(string? model, bool invalid)
    {
        _invalidModel |= invalid;
        if (_identity == StreamModelIdentity.Conflicting) return;
        if (model is not null)
        {
            if (_model is not null && !string.Equals(_model, model, StringComparison.Ordinal))
            {
                _identity = StreamModelIdentity.Conflicting;
                return;
            }
            _model = model;
        }
        _identity = _invalidModel || _model is null ? StreamModelIdentity.Unknown
            : string.Equals(_model, expectedModel, StringComparison.Ordinal) ? StreamModelIdentity.Matched
            : StreamModelIdentity.Mismatch;
    }

    private void RecordUsage(Chunk chunk)
    {
        if (chunk.Usage == StreamUsageAvailability.Unavailable || _usage == StreamUsageAvailability.Invalid) return;
        if (chunk.Usage == StreamUsageAvailability.Invalid ||
            (_usage == StreamUsageAvailability.Available &&
            (_promptTokens != chunk.PromptTokens || _completionTokens != chunk.CompletionTokens)))
        {
            _usage = StreamUsageAvailability.Invalid;
            return;
        }
        _usage = StreamUsageAvailability.Available;
        _promptTokens = chunk.PromptTokens;
        _completionTokens = chunk.CompletionTokens;
    }

    private void Parse(ReadOnlySpan<byte> payload, ref Chunk chunk)
    {
        var reader = new Utf8JsonReader(payload, s_json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            chunk.InvalidPayload = true;
            return;
        }

        var sawChoices = false;
        var sawModel = false;
        var sawUsage = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("error"u8))
            {
                reader.Read();
                var isError = reader.TokenType == JsonTokenType.StartObject;
                reader.Skip();
                // Only a fully consumed error object outlives a later parse failure in this event.
                chunk.Error |= isError;
            }
            else if (reader.ValueTextEquals("choices"u8))
            {
                reader.Read();
                chunk.InvalidPayload |= sawChoices || reader.TokenType != JsonTokenType.StartArray;
                if (reader.TokenType == JsonTokenType.StartArray) ReadChoices(ref reader, ref chunk);
                else reader.Skip();
                sawChoices = true;
            }
            else if (reader.ValueTextEquals("model"u8))
            {
                reader.Read();
                if (sawModel || reader.TokenType != JsonTokenType.String ||
                    reader.ValueSpan.Length is 0 or > MaxModelUtf8Bytes)
                    chunk.InvalidModel = true;
                else
                    chunk.Model = _model is not null && reader.ValueTextEquals(_model.AsSpan()) ? _model : reader.GetString();
                reader.Skip();
                sawModel = true;
            }
            else if (reader.ValueTextEquals("usage"u8))
            {
                reader.Read();
                if (sawUsage) chunk.Usage = StreamUsageAvailability.Invalid;
                else if (reader.TokenType != JsonTokenType.Null) ReadUsage(ref reader, ref chunk);
                reader.Skip();
                sawUsage = true;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
        chunk.InvalidPayload |= reader.TokenType != JsonTokenType.EndObject || reader.Read();
    }

    private static void ReadChoices(ref Utf8JsonReader reader, ref Chunk chunk)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                chunk.InvalidPayload = true;
                reader.Skip();
                continue;
            }
            ReadChoice(ref reader, ref chunk);
        }
    }

    private static void ReadChoice(ref Utf8JsonReader reader, ref Chunk chunk)
    {
        var index = -1;
        var sawIndex = false;
        var sawFinish = false;
        var finished = false;
        var length = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("index"u8))
            {
                reader.Read();
                if (!sawIndex && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)) index = value;
                else index = -1;
                sawIndex = true;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("finish_reason"u8))
            {
                reader.Read();
                finished = !sawFinish && IsNonemptyString(reader);
                length = finished && reader.ValueTextEquals("length"u8);
                sawFinish = true;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("text"u8))
            {
                reader.Read();
                chunk.SemanticOutput |= IsNonemptyString(reader);
                reader.Skip();
            }
            else if (reader.ValueTextEquals("delta"u8))
            {
                reader.Read();
                chunk.SemanticOutput |= ReadDelta(ref reader, ref chunk.NonTextDelta);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (index is < 0 or >= MaxChoices)
        {
            chunk.UnverifiedChoice = true;
            return;
        }
        var bit = 1UL << index;
        chunk.UnverifiedChoice |= (chunk.SeenChoices & bit) != 0;
        chunk.SeenChoices |= bit;
        if (finished) chunk.FinishedChoices |= bit;
        if (length) chunk.LengthFinishedChoices |= bit;
    }

    private static bool ReadDelta(ref Utf8JsonReader reader, ref bool nonTextDelta)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return false;
        }

        var output = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("content"u8) || reader.ValueTextEquals("reasoning_content"u8) ||
                reader.ValueTextEquals("reasoning"u8) || reader.ValueTextEquals("refusal"u8))
            {
                var refusal = reader.ValueTextEquals("refusal"u8);
                reader.Read();
                var hasText = IsNonemptyString(reader);
                output |= hasText;
                nonTextDelta |= refusal && hasText;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("function_call"u8))
            {
                reader.Read();
                nonTextDelta |= reader.TokenType != JsonTokenType.Null;
                output |= ReadToolOutput(ref reader, function: true);
            }
            else if (reader.ValueTextEquals("tool_calls"u8))
            {
                reader.Read();
                nonTextDelta |= reader.TokenType != JsonTokenType.Null;
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        output |= ReadToolOutput(ref reader, function: false);
                }
                else reader.Skip();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
        return output;
    }

    private static bool ReadToolOutput(ref Utf8JsonReader reader, bool function)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return false;
        }

        var output = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!function && reader.ValueTextEquals("function"u8))
            {
                reader.Read();
                output |= ReadToolOutput(ref reader, function: true);
            }
            else
            {
                var content = function && (reader.ValueTextEquals("name"u8) || reader.ValueTextEquals("arguments"u8));
                reader.Read();
                output |= content && IsNonemptyString(reader);
                reader.Skip();
            }
        }
        return output;
    }

    private static void ReadUsage(ref Utf8JsonReader reader, ref Chunk chunk)
    {
        chunk.Usage = StreamUsageAvailability.Invalid;
        if (reader.TokenType != JsonTokenType.StartObject) return;
        long prompt = -1, completion = -1, total = -1;
        var sawTotal = false;
        var invalid = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("prompt_tokens"u8))
                invalid |= !ReadCounter(ref reader, ref prompt);
            else if (reader.ValueTextEquals("completion_tokens"u8))
                invalid |= !ReadCounter(ref reader, ref completion);
            else if (reader.ValueTextEquals("total_tokens"u8))
            {
                invalid |= !ReadCounter(ref reader, ref total);
                sawTotal = true;
            }
            else reader.Read();
            reader.Skip();
        }

        if (invalid || prompt < 0 || completion < 0 ||
            (sawTotal && (total < prompt || total - prompt != completion))) return;
        chunk.Usage = StreamUsageAvailability.Available;
        chunk.PromptTokens = prompt;
        chunk.CompletionTokens = completion;
    }

    private static bool ReadCounter(ref Utf8JsonReader reader, ref long counter)
    {
        return reader.Read() && counter < 0 && reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt64(out counter) && counter >= 0;
    }

    private static bool IsNonemptyString(Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.String && !reader.ValueSpan.IsEmpty;

    internal static ulong ExpectedChoices(JsonElement count) =>
        count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n) && n is >= 1 and <= MaxChoices
            ? ulong.MaxValue >> (MaxChoices - n) : 0;

    private struct Chunk
    {
        internal bool Error;
        internal bool InvalidPayload;
        internal bool SemanticOutput;
        internal bool NonTextDelta;
        internal bool UnverifiedChoice;
        internal ulong SeenChoices;
        internal ulong FinishedChoices;
        internal ulong LengthFinishedChoices;
        internal string? Model;
        internal bool InvalidModel;
        internal StreamUsageAvailability Usage;
        internal long PromptTokens;
        internal long CompletionTokens;
    }

    private sealed class ExactLengthEvidence
    {
        internal bool Invalid;
        internal bool Finished;
        internal bool HasTerminalUsage;
        internal TimeSpan? LastSemanticOutput;
        internal TimeSpan? Terminator;

        internal void Observe(Chunk chunk, bool sawDone, ulong expectedChoices, string expectedModel)
        {
            Invalid |= sawDone || chunk.UnverifiedChoice || chunk.NonTextDelta || (chunk.SeenChoices & ~expectedChoices) != 0 ||
                Finished && chunk.SeenChoices != 0;
            if (chunk.FinishedChoices != 0)
            {
                Invalid |= chunk.LengthFinishedChoices != expectedChoices;
                Finished = true;
            }
            if (chunk.Usage != StreamUsageAvailability.Unavailable)
            {
                Invalid |= !Finished || HasTerminalUsage || chunk.Usage != StreamUsageAvailability.Available ||
                    !string.Equals(chunk.Model, expectedModel, StringComparison.Ordinal);
                HasTerminalUsage = true;
            }
        }
    }
}
