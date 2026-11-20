using System.IO.Pipelines;

namespace Arboreal.InferenceRouter.Server;

public static partial class ServerApp
{
    // Reflects acknowledgments from the local writer only. The no-credit pump batches
    // complete blocks, and a bare CR can be followed by an LF in the next batch.
    private sealed class DefaultStreamOutput
    {
        private static ReadOnlySpan<byte> DoneTight => "data:[DONE]"u8;
        private static ReadOnlySpan<byte> DoneSpaced => "data: [DONE]"u8;
        private static ReadOnlySpan<byte> DataField => "data:"u8;

        private bool _healthy = true;
        private bool _atBoundary = true;
        private bool _hasDataBlock;
        private bool _sawDone;
        private bool _afterCr;
        private int _lineLength;
        private bool _dataLine = true;
        private bool _tight = true;
        private bool _spaced = true;
        private int _dataLines;
        private bool _doneLine;

        internal bool CanAppendTerminal => _healthy && _atBoundary && _hasDataBlock && !_sawDone;

        internal void Observe(FlushResult result) => _healthy &= !result.IsCanceled && !result.IsCompleted;

        internal void ObservePayload(FlushResult result, ReadOnlySpan<byte> bytes)
        {
            Observe(result);
            while (_healthy && !_sawDone && !bytes.IsEmpty)
            {
                if (_afterCr)
                {
                    _afterCr = false;
                    if (bytes[0] == (byte)'\n')
                    {
                        bytes = bytes[1..];
                        continue;
                    }
                }

                var end = bytes.IndexOfAny((byte)'\r', (byte)'\n');
                if (end < 0)
                {
                    ObserveLine(bytes);
                    return;
                }

                ObserveLine(bytes[..end]);
                EndLine();
                _afterCr = bytes[end] == (byte)'\r';
                bytes = bytes[(end + 1)..];
            }
        }

        private void ObserveLine(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return;
            _atBoundary = false;

            // Only the longest possible sentinel line can still match. Retain no payload.
            var limit = DoneSpaced.Length;
            foreach (var value in bytes)
            {
                if (_lineLength >= limit)
                {
                    _lineLength = limit + 1;
                    break;
                }
                if (_lineLength < DataField.Length)
                    _dataLine &= value == DataField[_lineLength];
                _tight &= _lineLength < DoneTight.Length && value == DoneTight[_lineLength];
                _spaced &= value == DoneSpaced[_lineLength];
                _lineLength++;
            }
        }

        private void EndLine()
        {
            if (_lineLength == 0)
            {
                _atBoundary = true;
                _hasDataBlock |= _dataLines != 0;
                _sawDone |= _dataLines == 1 && _doneLine;
                _dataLines = 0;
                _doneLine = false;
            }
            else if (_dataLine && _lineLength >= DataField.Length - 1)
            {
                _dataLines = Math.Min(2, _dataLines + 1);
                _doneLine = (_tight && _lineLength == DoneTight.Length) ||
                    (_spaced && _lineLength == DoneSpaced.Length);
            }

            _lineLength = 0;
            _dataLine = _tight = _spaced = true;
        }
    }
}
