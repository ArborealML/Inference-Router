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

    [Fact]
    public async Task WaitAlternativesAreAdvisoryBoundedAndSanitizedInTicketAndHistory()
    {
        const string dispatch = """
            {"ObservationId":17,"Reason":"lifecycle_group_draining","Scope":"gpu\u001b","AcceptedDrainScope":"gpu",
            "Alternatives":[{"Engine":"two\u001b","State":"observed_ready","Reason":"route_admission_wait","Scope":"two"},
            {"Engine":"three","State":"not_evaluated"}],"AlternativesTruncated":true}
            """;
        var ticket = $$"""
            {"Id":"waiter","ThreadId":"caller","Model":"shared","QueuedAt":"2026-01-01T00:00:00+00:00",
            "State":"Queued","QueueDispatch":{{dispatch}}}
            """;
        using var handler = new Fixture(ticket);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://monitor.test") };
        using var monitor = new RouterMonitor(http);
        monitor.Follow("waiter");
        await monitor.RefreshAsync(CancellationToken.None);
        var item = Assert.IsType<TicketSnapshot>(monitor.Snapshot().Ticket);
        Assert.Equal(17, item.QueueDispatch?.ObservationId);
        var e = System.Text.Json.JsonSerializer.Deserialize(
            $$"""{"Seq":1,"Kind":"request.waiting","TicketId":"waiter","Model":"shared","QueueDispatch":{{dispatch}}}""",
            MonitorJson.Default.RouterEvent);
        monitor.Observe(Assert.IsType<RouterEvent>(e), false);

        var rows = new AirTop(monitor).Read().Rows;
        foreach (var row in rows.Where(static row => row.Kind is "Ticket" or "Activity"))
        {
            Assert.Contains("Queue advisory", row.Detail, StringComparison.Ordinal);
            Assert.Contains("Accepted drain gpu", row.Detail, StringComparison.Ordinal);
            Assert.Contains("observed_ready (probe only)", row.Detail, StringComparison.Ordinal);
            Assert.Contains("not_evaluated", row.Detail, StringComparison.Ordinal);
            Assert.Contains("Other alternatives omitted", row.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain('\u001b', row.Detail);
        }
        Assert.Equal(2, rows.Count(static row => row.Kind is "Ticket" or "Activity"));
    }

    private sealed class Fixture(string? ticket = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/admin/tickets/waiter" && ticket is not null) return Reply(ticket);
            if (path != "/admin/status")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            const string payload = """
                {"Engines":[{"Engine":"gpu\u001b","Healthy":true,"Inflight":1,"LoadedModels":["hot"],"SwitchingToward":"cold"}],"QueueDepths":{"cold":8}}
                """;
            return Reply(payload);
        }

        private static Task<HttpResponseMessage> Reply(string payload) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });
    }
}
