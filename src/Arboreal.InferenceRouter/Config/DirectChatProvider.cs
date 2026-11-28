using System.Collections.Frozen;
using System.Net;
using System.Text.Json.Serialization.Metadata;

namespace Arboreal.InferenceRouter;

public sealed record DirectChatProvider(string Name, Uri ChatUri, string Account, string CredentialReference,
    bool Enabled, bool AllowsStreaming, bool AllowsTools, bool AllowsReasoning, string? BackupParameter,
    long InputPriceMicroUsdPerMillion, long OutputPriceMicroUsdPerMillion, long MaximumAttemptMicroUsd,
    TimeSpan FirstOutputDeadline, int IdentityBufferBytes, int ResponseBodyBytes,
    IReadOnlyDictionary<string, DirectChatModel> Models)
{
    public TimeSpan SemanticIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan MaximumStreamDuration { get; init; } = TimeSpan.FromMinutes(15);
    internal DirectChatResponseMapping Response { get; init; } = new(null, null, null, null, FrozenDictionary<string, string>.Empty);

    internal static IReadOnlyDictionary<string, DirectChatProvider> Parse(Dictionary<string, DirectChatProviderDto>? input)
    {
        if (input is null) return FrozenDictionary<string, DirectChatProvider>.Empty;
        if (input.Count > 32 || input.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.Count)
            throw new RouterConfigException("commercial_provider requires at most 32 distinct provider names");
        var providers = input.ToFrozenDictionary(static pair => pair.Key, static pair => Parse(pair.Key, pair.Value), StringComparer.Ordinal);
        var equivalents = new Dictionary<string, (string Model, string Equivalence)>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers.Values)
        foreach (var (model, declaration) in provider.Models)
        {
            if (!equivalents.TryAdd(model, (model, declaration.Equivalence)) && equivalents[model] != (model, declaration.Equivalence))
                throw new RouterConfigException("commercial model declarations do not agree on explicit equivalence");
        }
        return providers;
    }

    private static DirectChatProvider Parse(string name, DirectChatProviderDto dto)
    {
        if (!OwnedPrivatePolicy.IsIdentifier(name) || dto is null || dto.Protocol != "openai-chat-completions" ||
            !OwnedPrivatePolicy.IsIdentifier(dto.Account) || !OwnedPrivatePolicy.IsIdentifier(dto.CredentialRef) ||
            !Uri.TryCreate(dto.Origin, UriKind.Absolute, out var origin) || origin.AbsolutePath != "/" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            (origin.Scheme != "https" && !IsControlledLoopback(origin)) || !IsPath(dto.ChatPath) ||
            dto.InputPrice is null or < 0 || dto.OutputPrice is null or < 0 || dto.AttemptCap is null or <= 0 ||
            dto.FirstOutputMs is < 1 or > 600_000 || dto.SemanticIdleMs is < 1 or > 600_000 ||
            dto.MaximumStreamMs is < 1 or > 3_600_000 || dto.MaximumStreamMs < (dto.FirstOutputMs ?? 30_000) ||
            dto.IdentityBufferBytes is < 1024 or > 1024 * 1024 ||
            dto.ResponseBodyBytes is < 1024 or > 16 * 1024 * 1024 || dto.Models is not { Count: > 0 and <= 128 } ||
            dto.BackupParameter is { } backup && (!IsField(backup) || ReservedRequestField(backup)))
            throw Invalid();
        var models = new Dictionary<string, DirectChatModel>(StringComparer.Ordinal);
        foreach (var (key, model) in dto.Models)
        {
            if (!IsModel(key) || model is null || !IsModel(model.RequestId) || !IsModel(model.ServedId) ||
                !IsModel(model.Equivalence) || model.ReasoningEfforts is { } efforts &&
                (dto.AllowsReasoning != true || efforts.Length > 8 || efforts.Any(effort => !IsField(effort)) ||
                    efforts.Distinct(StringComparer.Ordinal).Count() != efforts.Length)) throw Invalid();
            models.Add(key, new(model.RequestId!, model.ServedId!, model.Equivalence!,
                Array.AsReadOnly(model.ReasoningEfforts?.ToArray() ?? [])));
        }
        var provider = new DirectChatProvider(name, new Uri(origin, dto.ChatPath!), dto.Account!, dto.CredentialRef!, dto.Enabled == true,
            dto.AllowsStreaming == true, dto.AllowsTools == true, dto.AllowsReasoning == true, dto.BackupParameter,
            dto.InputPrice.Value, dto.OutputPrice.Value, dto.AttemptCap.Value, TimeSpan.FromMilliseconds(dto.FirstOutputMs ?? 30_000),
            dto.IdentityBufferBytes ?? 64 * 1024, dto.ResponseBodyBytes ?? 1024 * 1024, models.ToFrozenDictionary(StringComparer.Ordinal))
        {
            SemanticIdleTimeout = TimeSpan.FromMilliseconds(dto.SemanticIdleMs ?? 60_000),
            MaximumStreamDuration = TimeSpan.FromMilliseconds(dto.MaximumStreamMs ?? 900_000),
        };
        return dto.Response is null ? provider : provider with { Response = DirectChatResponseMapping.Parse(dto.Response) };
    }

    internal static bool IsControlledLoopback(Uri uri) => uri.Scheme == "http" &&
        IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);

    private static bool IsPath(string? path) => path is { Length: > 1 and <= 256 } && path[0] == '/' &&
        path[1] != '/' && path.Split('/').Skip(1).All(segment => segment.Length != 0 && segment is not ("." or "..") &&
            segment.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'));

    internal static bool IsField(string? name) => name is { Length: > 0 and <= 64 } &&
        name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    internal static bool IsModel(string? name) => name is { Length: > 0 and <= 128 } &&
        name.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/' or ':');

    internal static bool ReservedRequestField(string name) => name is "model" or "messages" or "stream" or "stream_options" or
        "reasoning_effort" or "tools" or "tool_choice" or "parallel_tool_calls" or "max_completion_tokens" or "max_tokens" or
        "temperature" or "top_p" or "stop" or "seed" or "n";

    internal static RouterConfigException Invalid() => new("invalid commercial provider definition");

    internal static void CheckDeclaration(ConfigDeclaration? providers)
    {
        if (providers is null) return;
        var context = ConfigJsonContext.Default;
        if (providers.Fields is null) throw Invalid();
        foreach (var definition in providers.Fields.Values)
        {
            Check(definition, context.DirectChatProviderDto);
            if (definition.Fields!.TryGetValue("model", out var models) && models.Fields is { } rows)
                foreach (var model in rows.Values) Check(model, context.DirectChatModelDto);
            if (!definition.Fields.TryGetValue("response", out var response)) continue;
            Check(response, context.DirectChatResponseDto);
            foreach (var key in new[] { "retry_after", "usage_prompt", "usage_completion" })
                if (response.Fields!.TryGetValue(key, out var scalar)) Check(scalar, context.DirectChatScalarDto);
        }

        static void Check(ConfigDeclaration declaration, JsonTypeInfo type)
        {
            if (declaration.Fields is not { } fields || fields.Keys.Any(key => type.Properties.All(prop => prop.Name != key)))
                throw Invalid();
        }
    }
}

public sealed record DirectChatModel(string RequestId, string ServedId, string Equivalence, IReadOnlyList<string> ReasoningEfforts);

internal enum DirectChatScalarSource { Header, Json }
internal enum DirectChatScalarUnit { Tokens, Seconds, Milliseconds, UnixSeconds, UnixMilliseconds }
internal sealed record DirectChatScalar(DirectChatScalarSource Source, string Name, DirectChatScalarUnit Unit)
{
    internal static DirectChatScalar Parse(DirectChatScalarDto input, bool tokens)
    {
        if (input.Name is not { Length: > 0 and <= 128 } name) throw DirectChatProvider.Invalid();
        var source = input.Source switch
        {
            "header" when DirectChatProvider.IsField(name) => DirectChatScalarSource.Header,
            "json" when name.Split('.') is { Length: <= 5 } path && path.All(DirectChatProvider.IsField) => DirectChatScalarSource.Json,
            _ => throw DirectChatProvider.Invalid(),
        };
        var unit = input.Unit switch
        {
            "tokens" when tokens => DirectChatScalarUnit.Tokens,
            "seconds" when !tokens => DirectChatScalarUnit.Seconds,
            "milliseconds" when !tokens => DirectChatScalarUnit.Milliseconds,
            "unix_seconds" when !tokens => DirectChatScalarUnit.UnixSeconds,
            "unix_milliseconds" when !tokens => DirectChatScalarUnit.UnixMilliseconds,
            _ => throw DirectChatProvider.Invalid(),
        };
        return new(source, name, unit);
    }
}

internal sealed record DirectChatResponseMapping(string? ServedModelHeader, DirectChatScalar? RetryAfter, DirectChatScalar? Prompt,
    DirectChatScalar? Completion, IReadOnlyDictionary<string, string> Codes)
{
    internal static DirectChatResponseMapping Parse(DirectChatResponseDto input)
    {
        var codes = input.Codes;
        if (input.ServedModelHeader is { } header && !DirectChatProvider.IsField(header) || (input.Prompt is null) != (input.Completion is null) ||
            codes is not null && (codes.Count > 16 || codes.Any(static code => !DirectChatProvider.IsField(code.Key) ||
                code.Value is not ("rate_limited" or "unavailable" or "bad_request")))) throw DirectChatProvider.Invalid();
        return new(input.ServedModelHeader, input.RetryAfter is { } retry ? DirectChatScalar.Parse(retry, tokens: false) : null,
            input.Prompt is { } prompt ? DirectChatScalar.Parse(prompt, tokens: true) : null,
            input.Completion is { } completion ? DirectChatScalar.Parse(completion, tokens: true) : null,
            codes?.ToFrozenDictionary(StringComparer.Ordinal) ?? FrozenDictionary<string, string>.Empty);
    }
}
