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
    private readonly object _deadlineGate = new();
    private ITimer? _timer;
    private RouterRequestException? _timeout;
    private long _lastSemanticAt;
    private bool _deadlineClosed;
    private readonly MemoryStream _leading = new();
    private readonly MemoryStream _terminalTail = new();
    private bool _headerIdentity;
    private bool _headerMismatch;
    private bool _verified;
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
        ArmTimer(provider.FirstOutputDeadline);
    }

    internal CancellationToken Token => _input.Token;
    internal bool TimedOut => _expired.IsCancellationRequested;
    internal CancellationToken DeadlineToken => _expired.Token;
    internal bool HasTerminal => _terminal;
    internal RouterRequestException Timeout()
    {
        Volatile.Read(ref _observer)?.End(StreamInputEnd.Fault);
        return Volatile.Read(ref _timeout)!;
    }
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

    internal void Observe(SemanticStreamObserver observer) => Volatile.Write(ref _observer, observer);

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

    internal SseBlockDecision Filter(ReadOnlySpan<byte> data, bool semanticOutput)
    {
        if (CheckDeadline() is { } expired) return Fail(expired);
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
        var qualifies = !_verified && observer.HasSemanticOutput && (_headerIdentity || observer.ModelIdentity == StreamModelIdentity.Matched);
        if (CheckDeadline(semanticProgress: qualifies || _verified && semanticOutput) is { } expiredAtRelease)
            return Fail(expiredAtRelease);
        return qualifies ? SseBlockDecision.Qualified : SseBlockDecision.Allow;
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
        if (blocks.RefusedBlock) throw _failure!;
        if (_terminal)
        {
            var tail = complete[terminalOffset..];
            if (_terminalTail.Length + tail.Length + blocks.PendingBytes > _provider.IdentityBufferBytes)
                throw DirectChatProtocol.BadResponse();
            _terminalTail.Write(tail.Span);
        }
    }

    internal async ValueTask PublishTerminalAsync(Func<ReadOnlyMemory<byte>, ValueTask> publish)
    {
        if (!_verified) throw DirectChatProtocol.Identity();
        if (!_terminal || _terminalTail.Length == 0) throw DirectChatProtocol.BadResponse();
        if (CheckDeadline(finished: true) is { } expired) throw expired;
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
        if (CheckDeadline() is { } expired) throw expired;
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
            if (CheckDeadline(finished: true) is { } expiredAtRelease) throw expiredAtRelease;
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

    // The producer and timer choose expiration under one gate. After qualification,
    // progress can only move the next deadline later. The armed wake recalculates that
    // deadline from the latest semantic timestamp before acting.
    private RouterRequestException? CheckDeadline(bool semanticProgress = false, bool finished = false, bool timerCallback = false)
    {
        RouterRequestException failure;
        lock (_deadlineGate)
        {
            if (_deadlineClosed || _timeout is not null) return _timeout;
            var now = _clock.GetTimestamp();
            var remaining = Remaining(now);
            if (remaining > TimeSpan.Zero)
            {
                if (finished) CloseDeadline();
                else if (semanticProgress)
                {
                    _lastSemanticAt = now;
                    if (!_verified)
                    {
                        _verified = true;
                        ArmTimer(Remaining(now));
                    }
                }
                else if (timerCallback) ArmTimer(remaining);
                return null;
            }
            failure = !_verified ? DirectChatProtocol.Deadline() :
                _clock.GetElapsedTime(_start, now) >= _provider.MaximumStreamDuration
                    ? new("commercial_stream_deadline", "the configured chat stream reached its maximum duration")
                    : new("commercial_stream_idle_timeout", "the configured chat stream stopped producing semantic output");
            Volatile.Write(ref _timeout, failure);
        }
        try { _expired.Cancel(); }
        catch (ObjectDisposedException) { /* The response finished while the callback was due. */ }
        return failure;
    }

    private TimeSpan Remaining(long now)
    {
        var elapsed = _clock.GetElapsedTime(_start, now);
        if (!_verified) return _provider.FirstOutputDeadline - elapsed;
        var absolute = _provider.MaximumStreamDuration - elapsed;
        var idle = _provider.SemanticIdleTimeout - _clock.GetElapsedTime(_lastSemanticAt, now);
        return absolute < idle ? absolute : idle;
    }

    private void ArmTimer(TimeSpan due)
    {
        _timer?.Dispose();
        _timer = _clock.CreateTimer(static state => ((DirectChatResponse)state!).CheckDeadline(timerCallback: true),
            this, due, System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void CloseDeadline()
    {
        _deadlineClosed = true;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        lock (_deadlineGate) CloseDeadline();
        _input.Dispose();
        _expired.Dispose();
        _leading.Dispose();
        _terminalTail.Dispose();
    }
}
