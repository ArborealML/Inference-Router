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
        var engine = Assert.IsType<TopRow>(top.Read().Engines.At(0));
        Assert.Equal("hot", top.Read().Engines[0, 4]);
        Assert.Equal("cold", top.Read().Engines[0, 5]);
        Assert.Contains("Slots unreported", engine.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', engine.Detail);
        Assert.Contains("preferred small  required 256  dispatched big", engine.Detail, StringComparison.Ordinal);
        Assert.Contains("Thread s1  Title Review  tmux editor", engine.Detail, StringComparison.Ordinal);
        Assert.Contains("waiting  Queued (unassigned)", engine.Detail, StringComparison.Ordinal);
        var queue = Assert.IsType<TopRow>(top.Read().Queues.At(0));
        Assert.Equal("8", top.Read().Queues[0, 1]);
        top.Open(queue);
        var activity = Assert.IsType<TopRow>(top.Read().Events.At(0));
        top.Open(activity);
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Contains("State unknown or expired", top.Read().TicketDetail, StringComparison.Ordinal);
        Assert.Contains("ticket same", top.Read().Scope, StringComparison.Ordinal);
        top.Open(queue);
        Assert.Null(monitor.Snapshot().TicketId);
        Assert.Contains("reported model cold", top.Read().Scope, StringComparison.Ordinal);
        Assert.Equal(1, top.Read().Events.Rows);
        top.Back();
        Assert.Equal(2, top.Read().Events.Rows);
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
        top.Open(new("queue", ["model", "1"], "detail", Queue: longName));
        Assert.Equal("ticket", top.Read().Events.At(0)?.Ticket);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/admin/status")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            const string payload = """
                {"Engines":[{"Engine":"gpu\u001b","Healthy":true,"Inflight":1,"LoadedModels":["hot"],"SwitchingToward":"cold","Requests":[
                 {"Id":"running","Engine":"gpu","ThreadId":"s1","Model":"hot","State":"Executing","QueuedAt":"2026-01-01T00:00:00Z","PreferredProfile":"small","RequiredContext":256,"DispatchedProfile":"big","ThreadTitle":"Review","TmuxSession":"editor"},
                 {"Id":"waiting","ThreadId":"s2","Model":"cold","State":"Queued","QueuedAt":"2026-01-01T00:00:00Z"}]}],"QueueDepths":{"cold":8}}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
