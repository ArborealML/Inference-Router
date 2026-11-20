namespace Arboreal.InferenceRouter;

/// <summary>Releases only wire bytes through a blank line. Retains one bounded, incomplete SSE
/// block without interpreting its fields or decoding its payload.</summary>
internal sealed class SseBlockBuffer(int maxBlockBytes = SseBlockBuffer.MaxBlockBytes)
{
    internal const int MaxBlockBytes = 1024 * 1024;

    private byte[] _pending = [];
    private int _pendingCount;
    private bool _lineHasBytes;
    private bool _afterCr;

    public bool HasIncompleteBlock => _pendingCount != 0;

    public (ReadOnlyMemory<byte> Complete, bool LimitExceeded) Append(ReadOnlySpan<byte> bytes)
    {
        var blockBytes = _pendingCount;
        var publishThrough = 0;

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
                if (!_lineHasBytes)
                {
                    publishThrough = index + 1;
                    blockBytes = 0;
                    continue;
                }
                _lineHasBytes = false;
            }
            else
            {
                _lineHasBytes = true;
            }

            if (++blockBytes > maxBlockBytes)
                return (TakePrefix(bytes[..publishThrough]), true);
        }

        var complete = TakePrefix(bytes[..publishThrough]);
        Retain(bytes[publishThrough..]);
        return (complete, false);
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
