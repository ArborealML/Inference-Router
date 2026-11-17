using System.Text;
using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

/// <summary>
/// The replayable event ring under both live views and the agent-facing SSE feeds: a reconnecting
/// reader gets everything the ring still holds, the handover from replay to live tail neither
/// duplicates nor loses an event, and a subscriber that cannot keep up ends its feed instead of
/// silently dropping events.
/// </summary>
public class EventRingTests
{
    private static List<long> Range(int from, int count) =>
        Enumerable.Range(from, count).Select(static i => (long)i).ToList();

    private static RouterEvent Evt(string kind, long seq = 0) => new() { Kind = kind, Seq = seq };

    [Fact]
    public void Replay_CursorAheadOfTheRing_YieldsNothingInsteadOfThrowing()
    {
        var ring = new EventRing(capacity: 16);
        ring.Append(Evt("request.enqueued"));

        // A client reconnecting with a Seq from a previous router lifetime asks for the future.
        // The arithmetic used to go negative here and the list constructor threw — after the SSE
        // headers had already been flushed, so the client saw a truncated stream, not an error.
        Assert.Empty(ring.Replay(sinceSeq: 9_000));
        Assert.Empty(ring.Replay(sinceSeq: ring.CurrentSeq));
        Assert.Single(ring.Replay(sinceSeq: 0));
    }

    [Fact]
    public void Replay_ReturnsTheWholeRetainedWindow_NotASmallerDefaultCap()
    {
        var ring = new EventRing(capacity: 4096);          // the capacity the router runs with
        for (var i = 0; i < 2_000; i++) ring.Append(Evt("request.enqueued"));

        // 2000 events are inside the ring, so a reader that asks for seq 0 is entitled to all of
        // them. A default cap below the ring capacity lost nearly half of them with no signal.
        var all = ring.Replay(0);
        Assert.Equal(2_000, all.Count);
        Assert.Equal(1, all[0].Seq);
        Assert.Equal(2_000, all[^1].Seq);

        // Past the retention boundary the answer is the retained window, and OldestSeq says where
        // that boundary is.
        var small = new EventRing(capacity: 16);
        for (var i = 0; i < 40; i++) small.Append(Evt("engine.health"));
        Assert.Equal(25, small.OldestSeq);
        Assert.Equal([25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40],
                     small.Replay(0).Select(e => e.Seq));
    }

    [Fact]
    public void SubscribeReplay_HandoverNeitherDuplicatesNorLoses()
    {
        var ring = new EventRing(capacity: 16);
        ring.Append(Evt("request.enqueued"));              // seq 1, before the reader arrives

        var live = new List<RouterEvent>();
        var snapshot = ring.SubscribeReplay(0, live.Add);
        ring.Append(Evt("request.completed"));             // seq 2, after the snapshot is taken

        Assert.Equal([1], snapshot.Select(e => e.Seq));
        Assert.Equal([2], live.Select(e => e.Seq));

        // The event appended while a non-atomic reader was between "replay" and "subscribe" is the
        // one that used to reach neither path. Snapshot-then-subscribe under one lock is the fix, so
        // the live handler must not see anything the snapshot already carried.
        ring.Unsubscribe(live.Add);
    }

    [Fact]
    public void SubscribeReplay_CapsLiveSubscribers()
    {
        var ring = new EventRing(capacity: 16);
        var handlers = new List<Action<RouterEvent>>();
        for (var i = 0; i < EventRing.MaxSubscribers; i++)
        {
            Action<RouterEvent> h = _ => { };
            handlers.Add(h);
            ring.SubscribeReplay(0, h);
        }

        var ex = Assert.Throws<InvalidOperationException>(() => ring.SubscribeReplay(0, _ => { }));
        Assert.Contains("subscribers", ex.Message, StringComparison.Ordinal);

        foreach (var h in handlers) ring.Unsubscribe(h);
        ring.SubscribeReplay(0, _ => { });                 // room again once they leave
    }

    [Fact(Timeout = 20_000)]
    public async Task Feed_ClosesWhenASubscriberFallsBehind_InsteadOfDroppingQuietly()
    {
        var (router, events) = FeedHarness();
        await using var _ = router;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var feed = router.SubscribeEvents(new RouterEventFilter { SinceSeq = 0 }, cts.Token);
        var enumerator = feed.GetAsyncEnumerator();
        try
        {
            // Subscribe on the replay path so the subscription is certainly live before the flood:
            // one event exists first, MoveNextAsync returns it from replay, and the reader is then
            // paused while the router appends until the per-subscription buffer fills.
            events.Append(Evt("request.enqueued"));
            var first = await enumerator.MoveNextAsync();
            Assert.True(first);
            await Task.Run(() =>
            {
                for (var i = 0; i < 400; i++) events.Append(Evt("engine.health"));
            });

            var seen = new List<long> { enumerator.Current.Seq };
            while (await enumerator.MoveNextAsync()) seen.Add(enumerator.Current.Seq);

            // Dropping mid-stream is invisible to a filtered reader (no Seq gap), so a dropped terminal
            // event looks like a stream with nothing more coming. Closing the feed is the honest signal:
            // the reader reconnects from the last Seq it saw. The buffer is 256, so a reader that never
            // reads again gets that many live events and then the end of the feed.
            Assert.InRange(seen.Count, 256, 300);
            Assert.Equal(Range(1, seen.Count), seen);              // contiguous, no holes
            Assert.False(cts.IsCancellationRequested);             // the feed ended on its own
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task CallerCancellation_EndsTheFeed()
    {
        var (router, events) = FeedHarness();
        await using var _ = router;

        await Task.Run(() => { for (var i = 1; i <= 5; i++) events.Append(Evt("engine.health")); });

        using var cts = new CancellationTokenSource();
        var seen = 0;
        await foreach (var e in router.SubscribeEvents(new RouterEventFilter { SinceSeq = 0 }, cts.Token))
        {
            seen++;
            if (seen == 3) cts.Cancel();
        }

        // A hung-up reader must stop being read for: the buffered events after the cancel are not
        // delivered, and the subscription is gone rather than living for the life of the process.
        Assert.Equal(3, seen);
    }

    [Fact(Timeout = 20_000)]
    public async Task Feed_DeliversEventsAppendedWhileTheReaderIsCatchingUp()
    {
        var (router, events) = FeedHarness();
        await using var _ = router;

        await Task.Run(() => { for (var i = 1; i <= 200; i++) events.Append(Evt("engine.health")); });

        var seen = new List<long>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var e in router.SubscribeEvents(new RouterEventFilter { SinceSeq = 0 }, cts.Token))
        {
            seen.Add(e.Seq);
            if (seen.Count == 200) cts.Cancel();            // the feed itself never ends on its own
        }

        Assert.Equal(Range(1, 200), seen);          // each exactly once, none lost
    }

    private static (RouterCore Router, EventRing Events) FeedHarness()
    {
        var events = new EventRing();
        var cfg = new RouterConfig
        {
            DefaultModel = "test-model",
            HttpHandlerFactory = _ => new FakeEngineHandler(),
            Engines = new Dictionary<string, EngineDefinition>
            {
                ["fake"] = new EngineDefinition
                {
                    Name = "fake",
                    Kind = EngineKind.OpenAiCompatible,
                    Endpoint = new Uri("http://127.0.0.1:9/v1"),
                    Catalog = new Dictionary<string, EngineModelDefinition>
                    {
                        ["test-model"] = new EngineModelDefinition { Name = "test-model" },
                    },
                },
            },
        };
        return (new RouterCore(cfg, events: events), events);
    }
}
