using System.Net;
using System.Text;
using Arboreal.InferenceRouter.Cli;
using Arboreal.InferenceRouter.Monitoring;

namespace Arboreal.InferenceRouter.Tests;

public sealed class AirTopTests
{
    [Fact]
    public async Task QueueFilterAndTicketDrillDownPreserveIdentity()
    {
        using var handler = new Fixture();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://monitor.test") };
        using var monitor = new RouterMonitor(http);
        await monitor.RefreshAsync(CancellationToken.None);
        monitor.Observe(new RouterEvent { Seq = 1, Kind = "queued", TicketId = "same", Model = "cold" }, false);
        monitor.Observe(new RouterEvent { Seq = 2, Kind = "queued", TicketId = "other", Model = "hot" }, false);
        var top = new AirTop(monitor);
        var engine = Assert.Single(top.Read().Rows, r => r.Kind == "Engine");
        Assert.Contains("loaded hot", engine.Summary, StringComparison.Ordinal);
        Assert.Contains("target cold", engine.Summary, StringComparison.Ordinal);
        Assert.Contains("Slots unreported", engine.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', engine.Detail);
        var queue = Assert.Single(top.Read().Rows, r => r.Kind == "Queue");
        Assert.Contains("8 queued", queue.Summary, StringComparison.Ordinal);
        top.Open(queue);
        var activity = Assert.Single(top.Read().Rows, r => r.Kind == "Activity");
        top.Open(activity);
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Contains("same  unknown or expired", top.Read().Rows[0].Summary, StringComparison.Ordinal);
        Assert.Contains("ticket same", Assert.Single(top.Read().Rows, r => r.Kind == "History").Summary, StringComparison.Ordinal);
        top.Open(queue);
        Assert.Null(monitor.Snapshot().TicketId);
        Assert.Contains("reported model cold", Assert.Single(top.Read().Rows, r => r.Kind == "History").Summary, StringComparison.Ordinal);
        Assert.Single(top.Read().Rows, r => r.Kind == "Activity");
        top.Back();
        Assert.Equal(2, top.Read().Rows.Count(r => r.Kind == "Activity"));
    }

    [Fact]
    public void LongQueueNamesUseTheSameReportingKeyAsEvents()
    {
        using var http = new HttpClient();
        using var monitor = new RouterMonitor(http);
        string longName = new('m', 180);
        var ring = new EventRing();
        ring.Append(new RouterEvent { Kind = "queued", Model = longName, TicketId = "ticket" });
        var e = Assert.Single(ring.Replay(0));
        Assert.NotEqual(longName, e.Model);
        monitor.Observe(e, false);
        var top = new AirTop(monitor);
        top.Open(new("queue", "Queue", "model", "detail", Queue: longName));
        Assert.Equal("ticket", Assert.Single(top.Read().Rows, r => r.Kind == "Activity").Ticket);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/admin/status")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            const string payload = """
                {"Engines":[{"Engine":"gpu\u001b","Healthy":true,"Inflight":1,"LoadedModels":["hot"],"SwitchingToward":"cold"}],"QueueDepths":{"cold":8}}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
