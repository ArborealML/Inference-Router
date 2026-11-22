using System.Net;
using System.Text;
using Arboreal.InferenceRouter.Monitoring;

namespace Arboreal.InferenceRouter.Tests;

public sealed class RouterMonitorTests
{
    [Fact]
    public async Task RefreshReplacesGaugesAndKeepsAnUnknownExactTicket()
    {
        List<string> paths = [];
        int depth = 7;
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            string path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return path == "/admin/status"
                ? Json($$"""{"Engines":[],"QueueDepths":{"cold":{{depth}}},"Threads":[]}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://monitor.test") };
        using var monitor = new RouterMonitor(http);
        monitor.Follow("one/two");
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(7, monitor.Snapshot().Gauges!.QueueDepths["cold"]);
        depth = 0;
        await monitor.RefreshAsync(CancellationToken.None);
        var snapshot = monitor.Snapshot();
        Assert.Equal(0, snapshot.Gauges!.QueueDepths["cold"]);
        Assert.Equal("one/two", snapshot.TicketId);
        Assert.Equal("unknown or expired", snapshot.TicketError);
        Assert.Equal(["/admin/status", "/admin/tickets/one%2Ftwo", "/admin/status", "/admin/tickets/one%2Ftwo"], paths);
    }

    [Fact]
    public async Task ReadsJsonSequenceAndKeepsBoundedHistory()
    {
        using var http = new HttpClient();
        using var monitor = new RouterMonitor(http);
        using var stream = new MemoryStream("id: 999\ndata: {\"Seq\":1,\"Kind\":\"request.queued\"}\r\n\r\n"u8.ToArray());
        await foreach (var item in MonitorEventReader.ReadAsync(stream, CancellationToken.None)) monitor.Observe(item, false);
        Assert.Equal(1, Assert.Single(monitor.Snapshot().History).Event!.Seq);
        for (int i = 2; i <= RouterMonitor.HistoryCapacity + 4; i++)
            monitor.Observe(new RouterEvent { Seq = i, Kind = "request.dispatched" }, false);
        Assert.Equal(RouterMonitor.HistoryCapacity, monitor.Snapshot().History.Length);
        Assert.Equal(4, monitor.Snapshot().Evicted);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
