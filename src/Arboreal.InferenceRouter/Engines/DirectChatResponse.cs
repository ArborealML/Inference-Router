using System.Text.Json;

namespace Arboreal.InferenceRouter;

internal sealed class DirectChatResponse : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly long _start;
    private readonly DirectChatProvider _provider;
    private readonly DirectChatModel _model;
    private readonly CancellationTokenSource _expired = new();
    private readonly CancellationTokenSource _input;
    private ITimer? _timer;
    private readonly MemoryStream _leading = new();
    private readonly MemoryStream _terminalTail = new();
    private bool _headerIdentity;
    private bool _headerMismatch;
    private volatile bool _verified;
    private bool _terminal;
    private bool _bufferedIdentity;
    private double? _firstRaw;
    private double? _firstSemantic;
    private RouterRequestException? _failure;
    private SemanticStreamObserver? _observer;

    internal DirectChatResponse(DirectChatProvider provider, DirectChatModel model, TimeProvider clock, CancellationToken cancellationToken)
    {
        _clock = clock;
        _provider = provider;
        _model = model;
        _start = clock.GetTimestamp();
        _input = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _expired.Token);
        ArmTimer();
    }

    internal CancellationToken Token => _input.Token;
    internal bool TimedOut => _expired.IsCancellationRequested;
    internal CancellationToken DeadlineToken => _expired.Token;
    internal bool HasTerminal => _terminal;
    internal RouterRequestException Timeout() => _verified ? DirectChatProtocol.BadResponse() : DirectChatProtocol.Deadline();
    internal DirectChatMetrics Metrics { get; private set; } = null!;

    internal void Bind(HttpResponseMessage response)
    {
        Metrics = new(_provider.Response, response.Headers, _clock);
        if (_provider.Response.ServedModelHeader is not { } name) return;
        var supplied = response.Headers.TryGetValues(name, out var values) ? DirectChatMetrics.Single(values) : null;
        _headerIdentity = supplied == _model.ServedId;
        _headerMismatch = supplied is not null && !_headerIdentity;
        if (!_headerIdentity && response.IsSuccessStatusCode) throw DirectChatProtocol.Identity();
    }

    internal void Observe(SemanticStreamObserver observer) => _observer = observer;

    internal void ObserveRaw() => _firstRaw ??= _clock.GetElapsedTime(_start).TotalMilliseconds;

    internal DirectChatEvidence Evidence()
    {
        var identity = _observer?.Snapshot().ModelIdentity switch
        {
            StreamModelIdentity.Conflicting => "conflicting",
            StreamModelIdentity.Mismatch => "mismatch",
            StreamModelIdentity.Matched => "matched",
            _ when _bufferedIdentity || _headerIdentity => "matched",
            _ when _headerMismatch => "mismatch",
            _ => "unknown",
        };
        return Metrics.Evidence() with { ModelIdentity = identity, VerifiedModel = identity == "matched" ? _model.ServedId : null,
            FirstRawMilliseconds = _firstRaw, FirstSemanticMilliseconds = _firstSemantic };
    }

    internal SseBlockDecision Filter(ReadOnlySpan<byte> data)
    {
        if (!_verified && _clock.GetElapsedTime(_start) >= _provider.FirstOutputDeadline)
            return Fail(DirectChatProtocol.Deadline());
        var observer = _observer!;
        if (observer.HasSemanticOutput) _firstSemantic ??= _clock.GetElapsedTime(_start).TotalMilliseconds;
        if (observer.ModelIdentity is StreamModelIdentity.Mismatch or StreamModelIdentity.Conflicting)
            return Fail(DirectChatProtocol.Identity());
        if (observer.SawError)
        {
            try
            {
                using var error = DirectChatProtocol.Parse(data.ToArray());
                return Fail(Metrics.Refusal(error.RootElement, status: 200));
            }
            catch (JsonException) { return Fail(DirectChatProtocol.BadResponse()); }
        }
        if (observer.HasInvalidPayload) return Fail(DirectChatProtocol.Identity());
        if (_terminal)
            return data.IsEmpty ? SseBlockDecision.HoldTerminal : Fail(DirectChatProtocol.BadResponse());
        if (observer.SawDone)
        {
            if (!_verified) return Fail(DirectChatProtocol.Identity());
            _terminal = true;
            ArmTimer();
            return SseBlockDecision.HoldTerminal;
        }
        if (!data.IsEmpty)
        {
            try
            {
                using var metadata = DirectChatProtocol.Parse(data.ToArray());
                if (!ValidateOutput(metadata.RootElement, "delta")) return Fail(DirectChatProtocol.BadResponse());
                Metrics.Observe(metadata.RootElement);
            }
            catch (JsonException) { return Fail(DirectChatProtocol.BadResponse()); }
        }
        if (!_verified && observer.HasSemanticOutput && (_headerIdentity || observer.ModelIdentity == StreamModelIdentity.Matched))
        {
            _verified = true;
            ClearTimer();
            return SseBlockDecision.Qualified;
        }
        return SseBlockDecision.Allow;
    }

    internal async ValueTask RelayAsync(SseBlockBuffer blocks, ReadOnlyMemory<byte> complete, bool heldBeforeRead,
        Func<ReadOnlyMemory<byte>, ValueTask> publish)
    {
        if (_leading.Length + (blocks.QualifiedThrough ?? (!_verified ? complete.Length + blocks.PendingBytes : 0)) > _provider.IdentityBufferBytes)
            throw DirectChatProtocol.Identity();
        if (!_verified)
        {
            if (blocks.RefusedBlock) throw _failure!;
            _leading.Write(complete.Span);
            return;
        }
        if (_leading.Length != 0)
        {
            await publish(_leading.ToArray()).ConfigureAwait(false);
            _leading.SetLength(0);
        }
        var terminalOffset = heldBeforeRead ? 0 : blocks.TerminalOffset ?? complete.Length;
        if (terminalOffset != 0) await publish(complete[..terminalOffset]).ConfigureAwait(false);
        if (terminalOffset < complete.Length)
        {
            var tail = complete[terminalOffset..];
            if (_terminalTail.Length + tail.Length > _provider.IdentityBufferBytes) throw DirectChatProtocol.BadResponse();
            _terminalTail.Write(tail.Span);
        }
        if (blocks.RefusedBlock) throw _failure!;
    }

    internal async ValueTask PublishTerminalAsync(Func<ReadOnlyMemory<byte>, ValueTask> publish)
    {
        if (!_verified) throw DirectChatProtocol.Identity();
        if (!_terminal || _terminalTail.Length == 0 || TimedOut) throw DirectChatProtocol.BadResponse();
        ClearTimer();
        await publish(_terminalTail.ToArray()).ConfigureAwait(false);
    }

    internal async Task<byte[]> ReadBufferedAsync(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentLength > _provider.ResponseBodyBytes) throw DirectChatProtocol.BadResponse();
        using var buffer = new MemoryStream();
        using var stream = await response.Content.ReadAsStreamAsync(Token).ConfigureAwait(false);
        var bytes = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(bytes, Token).ConfigureAwait(false);
            if (count == 0) break;
            ObserveRaw();
            if (buffer.Length + count > _provider.ResponseBodyBytes) throw DirectChatProtocol.BadResponse();
            buffer.Write(bytes, 0, count);
        }
        if (_clock.GetElapsedTime(_start) >= _provider.FirstOutputDeadline) throw DirectChatProtocol.Deadline();
        var result = buffer.ToArray();
        try
        {
            using var document = DirectChatProtocol.Parse(result);
            var json = document.RootElement;
            if (!response.IsSuccessStatusCode || json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error) &&
                error.ValueKind != JsonValueKind.Null) throw Metrics.Refusal(json, (int)response.StatusCode);
            if (json.ValueKind != JsonValueKind.Object) throw DirectChatProtocol.BadResponse();
            if (json.TryGetProperty("model", out var model) ? model.ValueKind != JsonValueKind.String || !model.ValueEquals(_model.ServedId)
                : !_headerIdentity) throw DirectChatProtocol.Identity();
            _bufferedIdentity = true;
            if (!ValidateOutput(json, "message")) throw DirectChatProtocol.BadResponse();
            if (!json.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 || !choices.EnumerateArray().Any(IsSemanticChoice)) throw DirectChatProtocol.BadResponse();
            _firstSemantic = _clock.GetElapsedTime(_start).TotalMilliseconds;
            if (_firstSemantic >= _provider.FirstOutputDeadline.TotalMilliseconds) throw DirectChatProtocol.Deadline();
            _verified = true;
            ClearTimer();
            Metrics.Observe(json);
            return result;
        }
        catch (JsonException) when (!response.IsSuccessStatusCode) { throw Metrics.Refusal(default, (int)response.StatusCode); }
        catch (JsonException) { throw DirectChatProtocol.BadResponse(); }
    }

    private bool ValidateOutput(JsonElement root, string outputName)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("choices", out var choices)) return true;
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() > 64) return false;
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object) return false;
            if (!choice.TryGetProperty(outputName, out var output)) continue;
            if (output.ValueKind != JsonValueKind.Object) return false;
            foreach (var field in output.EnumerateObject())
            {
                if (field.Value.ValueKind == JsonValueKind.Null) continue;
                if (field.Name is "content" or "refusal" && field.Value.ValueKind != JsonValueKind.String ||
                    field.Name is "reasoning" or "reasoning_content" && (!_provider.AllowsReasoning || field.Value.ValueKind != JsonValueKind.String) ||
                    field.Name == "tool_calls" && (!_provider.AllowsTools || !ValidToolOutput(field.Value)) || field.Name == "function_call")
                    return false;
            }
        }
        return true;

        static bool ValidToolOutput(JsonElement tools) => tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() <= 64 &&
            tools.EnumerateArray().All(static tool => tool.ValueKind == JsonValueKind.Object &&
                (!tool.TryGetProperty("function", out var function) || function.ValueKind == JsonValueKind.Object &&
                    function.EnumerateObject().All(static field => field.Name is not ("name" or "arguments") ||
                        field.Value.ValueKind == JsonValueKind.String)));
    }

    private static bool IsSemanticChoice(JsonElement choice) => choice.ValueKind == JsonValueKind.Object &&
        choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
        message.EnumerateObject().Any(static field => field.Name switch
        {
            "content" or "reasoning" or "reasoning_content" or "refusal" => field.Value.ValueKind == JsonValueKind.String &&
                field.Value.GetString() is { Length: > 0 },
            "tool_calls" => field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() > 0,
            _ => false,
        });

    private SseBlockDecision Fail(RouterRequestException error)
    {
        _failure = error;
        return SseBlockDecision.Refuse;
    }

    private void ArmTimer()
    {
        _timer?.Dispose();
        _timer = _clock.CreateTimer(static state =>
        {
            try { ((CancellationTokenSource)state!).Cancel(); }
            catch (ObjectDisposedException) { /* The response completed while the callback was due. */ }
        }, _expired, _provider.FirstOutputDeadline, System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void ClearTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        ClearTimer();
        _input.Dispose();
        _expired.Dispose();
        _leading.Dispose();
        _terminalTail.Dispose();
    }
}
