using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Cli;

internal sealed record FittedTunePrompt(byte[] Body, int Tokens, int Records, int TailBytes);

internal static class AirTunePromptFit
{
    // Count and generation receive the same bytes. Generated ASCII contains no JSON metacharacters
    // except LF, which Utf8JsonWriter encodes in two bytes.
    internal static byte[] ChatBody(string model, byte[] prompt, int outputTokens)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteNumber("temperature", 0);
            writer.WriteString("reasoning_effort", "none");
            writer.WriteNumber("max_tokens", outputTokens);
            writer.WriteBoolean("stream", true);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    // Called after the executor admits the resident. Count requests go directly to its pinned origin.
    internal static async Task<FittedTunePrompt?> FitMayaAsync(EngineDefinition engine, string? credential,
        WorkloadManifest manifest, WorkloadTier tier, WorkloadRequest coordinate, CancellationToken cancellationToken)
    {
        var countUri = new Uri(engine.Endpoint, "/api/tokens");
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        using var client = new HttpClient(handler) { MaxResponseContentBufferSize = 4096, Timeout = TimeSpan.FromSeconds(5) };
        var generator = new AirTuneTextGenerator(manifest.Seed, tier.Profile, coordinate.Concurrency,
            coordinate.Wave, coordinate.Worker);
        return await FitAsync(generator, manifest.Model, manifest.Policy.TargetOutputTokens, checked((int)tier.NominalPromptMin),
            checked((int)tier.NominalPromptMax), CountAsync, cancellationToken).ConfigureAwait(false);

        async Task<int> CountAsync(byte[] body, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, countUri);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("Engine refused the count request.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false),
                new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 4 });
            var row = doc.RootElement;
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("approximate", out var approximate) || approximate.ValueKind != JsonValueKind.False ||
                !row.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Number || !images.TryGetInt32(out var n) || n != 0 ||
                !row.TryGetProperty("max_context", out var ctx) || ctx.ValueKind != JsonValueKind.Number ||
                !ctx.TryGetInt64(out var window) || window != tier.ConfiguredContext ||
                !row.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Number ||
                !tokens.TryGetInt32(out var value) || value <= 0)
                throw new InvalidDataException("Engine did not confirm an exact count at the selected context.");
            return value;
        }
    }

    internal static async Task<FittedTunePrompt?> FitAsync(AirTuneTextGenerator generator, string model,
        int outputTokens, int minTokens, int maxTokens, Func<byte[], CancellationToken, Task<int>> count,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minTokens);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, minTokens);
        var policy = new WorkloadPolicy();
        var ceiling = policy.MaxCountInputBytesPerPrompt / policy.MaxCountCalls;
        var emptyBodyLength = ChatBody(model, [], outputTokens).Length;
        // Two LF in the fixed header, then one per full record. Reserve the next full record
        // so every possible trailing byte offset also fits the submitted-body budget.
        var low = 0;
        var high = Math.Min(ceiling / 67, AirTuneTextGenerator.MaxRecordCount - 1);
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            var next = mid + 1;
            if (emptyBodyLength + AirTuneTextGenerator.PrefixByteLength(next) + 2 + next <= ceiling)
                low = mid;
            else
                high = mid - 1;
        }
        if (low < 1) return null;
        high = low;
        low = 0;
        FittedTunePrompt? best = null;
        async Task<int> ProbeAsync(int records, int tail)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = ChatBody(model, generator.Candidate(records, tail), outputTokens);
            if (body.Length > ceiling) throw new InvalidDataException("Count body exceeds its budget.");
            var tokens = await count(body, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (tokens <= 0) throw new InvalidDataException("Invalid rendered token count.");
            if (tokens <= maxTokens && (best is null || tokens > best.Tokens ||
                (tokens == best.Tokens && body.Length > best.Body.Length)))
                best = new(body, tokens, records, tail);
            return tokens;
        }

        var countedPrefixes = new HashSet<int>();
        for (var call = 0; low <= high && call < policy.MaxPrefixCountCalls; call++)
        {
            var mid = low + (high - low) / 2;
            countedPrefixes.Add(mid);
            var tokens = await ProbeAsync(mid, 0).ConfigureAwait(false);
            if (tokens == maxTokens) return best;
            if (tokens < maxTokens) low = mid + 1;
            else high = mid - 1;
        }
        if (best is null) return null;
        var prefix = best.Records;
        var length = AirTuneTextGenerator.RecordByteLength(prefix + 1);
        var estimate = 4L * (maxTokens - best.Tokens);
        var tails = new HashSet<int> { 0 };
        if (countedPrefixes.Contains(prefix + 1)) tails.Add(length);
        foreach (var offset in new[] { 0, -1, 1, -2, 2, -4, 4, -8 })
        {
            var tail = (int)Math.Clamp(estimate + offset, 0, length);
            if (tails.Add(tail) && await ProbeAsync(prefix, tail).ConfigureAwait(false) == maxTokens)
                break;
        }
        return best.Tokens >= minTokens ? best : null;
    }
}
