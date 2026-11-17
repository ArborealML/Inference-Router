using System.Net;
using System.Text;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>What happens to a streaming request that does not finish. These paths allocate a stream
/// channel the moment the request is admitted, so they are the ones where the caller can be left
/// waiting on a response that has already lost its only way to say anything.</summary>
public class StreamingFailureTests
{
    private static string Body(string content) =>
        $$"""{"model":"test-model","stream":true,"messages":[{"role":"user","content":"{{content}}"}]}""";

    [Fact(Timeout = 30_000)]
    public async Task EngineFailureOnAStreamingRequestEndsTheStreamItOpened()
    {
        var (app, fake, port) = await WireHarness.StartAsync(holdCompletions: false);
        using var _ = app;
        fake.CompletionStatus = 502;
        fake.CompletionErrorBody = """{"error":{"message":"engine exploded","type":"server_error"}}""";

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        var (ticket, res) = await WireHarness.StartStreamAsync(http, "stream-fail", Body("hello"));
        Assert.NotEmpty(ticket);

        // The response is already a committed 200 event stream by the time the engine answers, so the
        // only way the caller learns anything is the stream ending with an error frame. Until the
        // failure path completed the stream channel, this read simply waited.
        var lines = await ReadUntilQuietAsync(res, TimeSpan.FromSeconds(10));
        Assert.Contains(lines, l => l.Contains("\"error\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("[DONE]", StringComparison.Ordinal));
        res.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public async Task HangingUpMidStreamIsNotCountedAsACompletedRequest()
    {
        var (app, fake, port) = await WireHarness.StartAsync(holdCompletions: false);
        using var _ = app;
        fake.TrickleStreamMs = 25;

        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        var (ticket, res) = await WireHarness.StartStreamAsync(http, "stream-abort", Body("hello"));

        // Prove the stream is live, then hang up on it. The pump used to swallow the resulting
        // cancellation and return normally, which completed the ticket: a truncated request looked
        // identical to a served one, in the stats and in the event feed.
        using (var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await using var stream = await res.Content.ReadAsStreamAsync(readCts.Token);
            using var reader = new StreamReader(stream);
            var first = await reader.ReadLineAsync(readCts.Token);
            Assert.NotNull(first);
            Assert.StartsWith("data:", first, StringComparison.Ordinal);
        }
        res.Dispose();      // the caller is gone; the router should stop serving it

        // The disconnect has to be noticed by the server writing to a socket nobody is reading, so the
        // terminal event lands a little later than the request itself. Poll the replay rather than
        // trusting one quiet-window read: on a loaded machine a single window can end before the
        // cancellation is recorded, and a passing test that way proves nothing.
        string terminal = "";
        var deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            var frames = await WireHarness.ReadSseFramesAsync(http, $"/admin/tickets/{ticket}/events?since=0",
                idleMs: 700, maxMs: 4000);
            terminal = frames.FirstOrDefault(f =>
                f.Contains("request.cancelled", StringComparison.Ordinal) ||
                f.Contains("request.completed", StringComparison.Ordinal)) ?? "";
            if (terminal.Length > 0) break;
        }

        Assert.Contains("request.cancelled", terminal);
        Assert.DoesNotContain("request.completed", terminal);
    }

    private static async Task<List<string>> ReadUntilQuietAsync(HttpResponseMessage res, TimeSpan bound)
    {
        using var cts = new CancellationTokenSource(bound);
        await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var lines = new List<string>();
        while (true)
        {
            string? line;
            try { line = await reader.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            if (line is null) break;            // the stream closed: the caller can stop waiting
            lines.Add(line);
        }

        return lines;
    }
}
