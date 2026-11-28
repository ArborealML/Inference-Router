namespace Arboreal.InferenceRouter;

internal enum SseBlockDecision { Allow, Qualified, HoldTerminal, Refuse }
internal delegate SseBlockDecision SseBlockFilter(ReadOnlySpan<byte> payload);

/// <summary>Releases only wire bytes through a blank line. Optionally exposes the data payload
/// of each committed block, using the same line and block boundaries as the byte relay.</summary>
internal sealed class SseBlockBuffer(int maxBlockBytes = SseBlockBuffer.MaxBlockBytes,
    SemanticStreamObserver? observer = null, SseBlockFilter? filter = null)
{
    internal const int MaxBlockBytes = 1024 * 1024;

    private byte[] _pending = [];
    private int _pendingCount;
    private int _lineLength;
    private bool _afterCr;
    private int _dataOffset;
    private int _dataLength = -1;
    private byte[] _joined = [];
    private int _joinedLength;

    public bool HasIncompleteBlock => _pendingCount != 0;
    internal bool RefusedBlock { get; private set; }
    internal int? TerminalOffset { get; private set; }
    internal int? QualifiedThrough { get; private set; }
    internal int PendingBytes => _pendingCount;

    public (ReadOnlyMemory<byte> Complete, bool LimitExceeded) Append(ReadOnlySpan<byte> bytes)
    {
        var blockBytes = _pendingCount;
        var publishThrough = 0;
        TerminalOffset = null;
        QualifiedThrough = null;

        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            var continuesCr = _afterCr && value == (byte)'\n';
            _afterCr = value == (byte)'\r';

            if (continuesCr)
            {
                // The CR of a blank line already committed that block, even in an earlier read.
                // Its optional LF belongs to that same delimiter. Other CRLFs count both bytes.
                if (blockBytes == 0)
                {
                    publishThrough = index + 1;
                    continue;
                }
            }
            else if (value is (byte)'\r' or (byte)'\n')
            {
                var end = _pendingCount + index;
                var blockStart = end - blockBytes;
                if (_lineLength == 0)
                {
                    if (observer is not null)
                    {
                        var decision = ObserveBlock(observer, bytes, blockStart);
                        if (decision == SseBlockDecision.Refuse)
                        {
                            RefusedBlock = true;
                            return (TakePrefix(bytes[..publishThrough]), false);
                        }
                        if (decision == SseBlockDecision.HoldTerminal) TerminalOffset ??= blockStart;
                        if (decision == SseBlockDecision.Qualified) QualifiedThrough ??= _pendingCount + index + 1;
                    }
                    publishThrough = index + 1;
                    blockBytes = 0;
                    continue;
                }
                if (observer is not null) ObserveLine(bytes, blockStart, end);
                _lineLength = 0;
            }
            else
            {
                _lineLength++;
            }

            if (++blockBytes > maxBlockBytes)
                return (TakePrefix(bytes[..publishThrough]), true);
        }

        var complete = TakePrefix(bytes[..publishThrough]);
        Retain(bytes[publishThrough..]);
        return (complete, false);
    }

    private void ObserveLine(ReadOnlySpan<byte> bytes, int blockStart, int end)
    {
        var start = end - _lineLength;
        if (_lineLength < 4 || ByteAt(bytes, start) != (byte)'d' || ByteAt(bytes, start + 1) != (byte)'a' ||
            ByteAt(bytes, start + 2) != (byte)'t' || ByteAt(bytes, start + 3) != (byte)'a') return;
        if (_lineLength == 4)
        {
            start = end;
        }
        else
        {
            if (ByteAt(bytes, start + 4) != (byte)':') return;
            start += 5;
            if (start < end && ByteAt(bytes, start) == (byte)' ') start++;
        }

        if (_dataLength < 0)
        {
            _dataOffset = start - blockStart;
            _dataLength = end - start;
            return;
        }

        if (_joinedLength == 0) Join(bytes, blockStart + _dataOffset, _dataLength);
        EnsureJoined(_joinedLength + 1);
        _joined[_joinedLength++] = (byte)'\n';
        Join(bytes, start, end - start);
    }

    private SseBlockDecision ObserveBlock(SemanticStreamObserver target, ReadOnlySpan<byte> bytes, int blockStart)
    {
        if (_dataLength < 0) return filter?.Invoke(default) ?? SseBlockDecision.Allow;
        var start = blockStart + _dataOffset;
        if (_joinedLength == 0 && start < _pendingCount && start + _dataLength > _pendingCount)
            Join(bytes, start, _dataLength);
        var payload = _joinedLength > 0 ? _joined.AsSpan(0, _joinedLength)
            : start >= _pendingCount ? bytes.Slice(start - _pendingCount, _dataLength)
            : _pending.AsSpan(start, _dataLength);
        target.ObserveData(payload);
        var decision = filter?.Invoke(payload) ?? SseBlockDecision.Allow;
        _dataLength = -1;
        _joinedLength = 0;
        return decision;
    }

    private byte ByteAt(ReadOnlySpan<byte> bytes, int index) =>
        index < _pendingCount ? _pending[index] : bytes[index - _pendingCount];

    private void Join(ReadOnlySpan<byte> bytes, int start, int length)
    {
        EnsureJoined(_joinedLength + length);
        var pending = Math.Clamp(_pendingCount - start, 0, length);
        if (pending > 0) _pending.AsSpan(start, pending).CopyTo(_joined.AsSpan(_joinedLength));
        if (pending < length)
            bytes.Slice(start + pending - _pendingCount, length - pending).CopyTo(_joined.AsSpan(_joinedLength + pending));
        _joinedLength += length;
    }

    private void EnsureJoined(int required)
    {
        if (required > _joined.Length)
            Array.Resize(ref _joined, Math.Max(required, Math.Min(maxBlockBytes, Math.Max(256, _joined.Length * 2))));
    }

    private ReadOnlyMemory<byte> TakePrefix(ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty) return default;

        // Neither the read scratch nor the pending storage belongs to the channel.
        var owned = new byte[_pendingCount + prefix.Length];
        _pending.AsSpan(0, _pendingCount).CopyTo(owned);
        prefix.CopyTo(owned.AsSpan(_pendingCount));
        _pendingCount = 0;
        return owned;
    }

    private void Retain(ReadOnlySpan<byte> tail)
    {
        if (tail.IsEmpty) return;

        var required = _pendingCount + tail.Length;
        if (required > _pending.Length)
        {
            var grown = Math.Min(maxBlockBytes, Math.Max(256, _pending.Length * 2));
            Array.Resize(ref _pending, Math.Max(required, grown));
        }
        tail.CopyTo(_pending.AsSpan(_pendingCount));
        _pendingCount = required;
    }
}
