using System.Net;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>
/// The event feed an agent uses to follow its own request: ticket-scoped replay on
/// /admin/tickets/{id}/events and the `ticket=` filter on /admin/events must not leak another
/// request's history, and the field names on the wire must stay the ones SPEC §10 and
/// samples/pi/inference-router-tools.json promise.
/// </summary>
public class EventSurfaceTests
{
    private const string Body = """{"model":"test-model","stream":true,"messages":[{"role":"user","content":"hi"}]}""";

    private static string? TicketOf(string frame)
    {
        using var doc = JsonDocument.Parse(frame);
        return doc.RootElement.TryGetProperty("TicketId", out var v) ? v.GetString() : null;
    }

    [Fact]
    public async Task TicketEventRoute_ReplaysOnlyThatTicketsEvents()
    {
        var (app, fake, port) = await WireHarness.StartAsync(holdCompletions: true);
        using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var (a, resA) = await WireHarness.StartStreamAsync(http, "thread-a", Body);
        var (b, resB) = await WireHarness.StartStreamAsync(http, "thread-b", Body);

        // Both requests are queued behind the fake's gate. Completing them before opening the event
        // stream makes the replay path (not the live tail) carry every event, which is what an agent
        // reconnecting after a drop actually hits.
        fake.ReleaseGate();
        await resA.Content.ReadAsStringAsync();
        await resB.Content.ReadAsStringAsync();

        var frames = await WireHarness.ReadSseFramesAsync(http, $"/admin/tickets/{a}/events?since=0");

        Assert.NotEmpty(frames);
        Assert.Contains(frames, f => f.Contains("\"request.completed\"", StringComparison.Ordinal));
        Assert.All(frames, f => Assert.Equal(a, TicketOf(f)));
        Assert.DoesNotContain(frames, f => f.Contains(b, StringComparison.Ordinal));

        // The same isolation from the global route.
        var viaQuery = await WireHarness.ReadSseFramesAsync(http, $"/admin/events?since=0&ticket={b}");
        Assert.NotEmpty(viaQuery);
        Assert.All(viaQuery, f => Assert.Equal(b, TicketOf(f)));

        // Casing pinned on purpose: SPEC §6 records the admin surface as PascalCase and says a
        // change must be deliberate. If this fails, someone changed the wire contract.
        var sample = frames[0];
        Assert.Contains("\"TicketId\":", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ticketId\":", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\":", sample, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownTicketYieldsAnEmptyStreamNotAnError()
    {
        var (app, _, port) = await WireHarness.StartAsync(holdCompletions: false);
        using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var frames = await WireHarness.ReadSseFramesAsync(http, "/admin/tickets/t-nope/events?since=0", idleMs: 600, maxMs: 3000);
        Assert.Empty(frames);
    }


    [Fact(Timeout = 30_000)]
    public async Task FeedAtTheSubscriberLimitRefusesBeforeAnySseHeaders()
    {
        // The cap includes the router's own internal subscription, so two means "one client feed".
        var (app, _, port) = await WireHarness.StartAsync(holdCompletions: false, maxEventSubscribers: 2);
        using var _ = app;
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var hold = new HttpRequestMessage(HttpMethod.Get, "/admin/events?since=0");
        var first = await http.SendAsync(hold, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("text/event-stream", first.Content.Headers.ContentType?.MediaType);

        HttpResponseMessage? refused = null;
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, "/admin/events?since=0");
            var res = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (res.StatusCode == HttpStatusCode.ServiceUnavailable) { refused = res; break; }
            res.Dispose();
            await Task.Delay(25, cts.Token);
        }

        // The refusal has to be a real error: a 200 that ends at once is indistinguishable from a
        // filter that matches nothing, so a full feed would look like a quiet router.
        Assert.NotNull(refused);
        var body = await refused!.Content.ReadAsStringAsync();
        Assert.Contains("too_many_subscribers", body, StringComparison.Ordinal);
        Assert.NotEqual("text/event-stream", refused.Content.Headers.ContentType?.MediaType);
        first.Dispose();
    }

    [Fact]
    public void Filter_WithTicketId_DropsEventsThatCarryNoTicket()
    {
        var filter = new RouterEventFilter { TicketId = "t1", Kinds = ["request.completed", "engine.switch_completed"] };
        Assert.True(filter.Matches(new RouterEvent { Kind = "request.completed", TicketId = "t1", Seq = 1 }));
        // engine.* events are emitted by the engine runtime and carry no ticket: a ticket-scoped
        // reader must not see them, otherwise "my events" silently includes unrelated traffic.
        Assert.False(filter.Matches(new RouterEvent { Kind = "engine.switch_completed", Engine = "fake", Seq = 2 }));
        Assert.False(filter.Matches(new RouterEvent { Kind = "request.completed", TicketId = "t2", Seq = 3 }));
        Assert.False(filter.Matches(new RouterEvent { Kind = "request.enqueued", TicketId = "t1", Seq = 0 }));

        var unfiltered = new RouterEventFilter { SinceSeq = 0 };
        Assert.True(unfiltered.Matches(new RouterEvent { Kind = "engine.health", Seq = 5 }));
    }
}
