using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Monitoring;

internal static class MonitorEventReader
{
    // AIR emits exactly one JSON data line per SSE event. Bound an unterminated line before decoding it.
    internal static async IAsyncEnumerable<RouterEvent> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var chunk = new byte[4096];
        var line = new byte[16_384];
        int length = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            for (int i = 0; i < read; i++)
            {
                if (chunk[i] != (byte)'\n')
                {
                    if (length == line.Length) throw new IOException("Event line exceeds monitor limit.");
                    line[length++] = chunk[i];
                    continue;
                }
                if (line.AsSpan(0, length).StartsWith("data:"u8))
                {
                    var item = JsonSerializer.Deserialize(line.AsSpan(5, length - 5), MonitorJson.Default.RouterEvent)
                        ?? throw new JsonException("Empty event.");
                    yield return item;
                }
                length = 0;
            }
        }
    }
}
