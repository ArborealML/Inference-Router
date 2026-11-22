using System.Net;
using System.Text;
using Arboreal.InferenceRouter.Monitoring;

namespace Arboreal.InferenceRouter.Tests;

public sealed class MonitorRecoveryTests
{
    [Fact]
    public async Task ReconnectUsesGlobalCursorAndRefreshesExactTicketAfterGap()
    {
        using var handler = new ReconnectFixture();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://monitor.test") };
        using var monitor = new RouterMonitor(http);
        monitor.Follow("original");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var running = monitor.RunAsync(cancel.Token);
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var s = monitor.Snapshot();
                if (s.Ticket?.State == "Failed" && s.History.Any(h => h.Event?.Kind == "engine.switch_failed")) break;
                await Task.Delay(20, cancel.Token);
            }
            Assert.Equal(["?since=0", "?since=2"], handler.Cursors.Take(2));
            var result = monitor.Snapshot();
            Assert.Contains(result.History, h => h.Boundary?.Contains("continuity uncertain", StringComparison.Ordinal) == true);
            Assert.Contains(result.History, h => h.Boundary?.Contains("gap", StringComparison.Ordinal) == true);
            Assert.Single(result.History, h => h.Event?.TicketId == "other");
            Assert.Equal(9, result.Gauges!.QueueDepths["cold"]);
            Assert.Equal("original", result.TicketId);
            Assert.Equal("switch_failed", result.Ticket!.ErrorCode);
        }
        finally
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    [Fact]
    public void ReplayOverlapDoesNotHideAnEventFromANewLifetime()
    {
        using var http = new HttpClient();
        using var monitor = new RouterMonitor(http);
        var before = new RouterEvent { Seq = 7, Kind = "queued", TicketId = "old" };
        monitor.Observe(before, false);
        monitor.Observe(before with { Seq = 8 }, false);
        monitor.Observe(before, true);
        monitor.Observe(before, false);
        monitor.Observe(before with { Seq = 8, TicketId = "new" }, false);
        Assert.Equal(2, monitor.Snapshot().History.Count(h => h.Event == before));
        Assert.Single(monitor.Snapshot().History, h => h.Boundary?.Contains("reset", StringComparison.Ordinal) == true);
        Assert.Equal("new", monitor.Snapshot().History[^1].Event!.TicketId);
    }

    [Fact]
    public async Task SnapshotConsumerCannotAlterLaterEngineOrQueueGauges()
    {
        using var handler = new ReconnectFixture();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://monitor.test") };
        using var monitor = new RouterMonitor(http);
        await monitor.RefreshAsync(CancellationToken.None);
        var gauges = monitor.Snapshot().Gauges!;
        var writableQueues = (IDictionary<string, int>)gauges.QueueDepths;
        Assert.Throws<NotSupportedException>(() => writableQueues["cold"] = -1);
        var writableRoster = (IList<string>)gauges.Engines[0].LoadedModels;
        Assert.Throws<NotSupportedException>(() => writableRoster[0] = "other");
        Assert.Equal("warm", monitor.Snapshot().Gauges!.Engines[0].LoadedModels[0]);
    }

    [Fact]
    public async Task UnterminatedOversizeFeedIsBounded()
    {
        using var stream = new MemoryStream(new byte[16_385]);
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var unused in MonitorEventReader.ReadAsync(stream, CancellationToken.None))
                Assert.Fail("An oversized line cannot supply an event.");
        });
    }

    private sealed class ReconnectFixture : HttpMessageHandler
    {
        internal System.Collections.Concurrent.ConcurrentQueue<string> Cursors { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var url = request.RequestUri!;
            bool recovered = Cursors.Count >= 2;
            if (url.AbsolutePath == "/admin/status")
                return Response($$$"""{"Engines":[{"Engine":"gpu","Healthy":true,"LoadedModels":["warm"],"Inflight":0,"SwitchingToward":null}],"QueueDepths":{"cold":{{{(recovered ? 9 : 1)}}} } }""", "application/json");
            if (url.AbsolutePath == "/admin/tickets/original")
                return Response($$"""{"Id":"original","ThreadId":"client","Model":"cold","QueuedAt":"2026-01-01T00:00:00Z","State":"{{(recovered ? "Failed" : "Queued")}}","ErrorCode":"switch_failed"}""", "application/json");
            Assert.Equal("/admin/events", url.AbsolutePath);
            Cursors.Enqueue(url.Query);
            string data = Cursors.Count switch
            {
                1 => "data: {\"Seq\":1,\"Kind\":\"request.queued\",\"TicketId\":\"other\"}\n\ndata: {\"Seq\":2,\"Kind\":\"request.queued\",\"TicketId\":\"original\"}\n\n",
                2 => "data: {\"Seq\":5,\"Kind\":\"engine.switch_failed\",\"Model\":\"cold\",\"Reason\":\"fixture\"}\n\n",
                _ => "",
            };
            return Response(data, "text/event-stream");
        }

        private static Task<HttpResponseMessage> Response(string value, string type) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, type) });
    }
}
