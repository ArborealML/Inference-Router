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
        Assert.True(ring.TrySubscribeReplay(0, live.Add, out var snapshot));
        ring.Append(Evt("request.completed"));             // seq 2, after the snapshot is taken

        Assert.Equal([1], snapshot.Select(e => e.Seq));
        Assert.Equal([2], live.Select(e => e.Seq));

        // The event appended while a non-atomic reader was between "replay" and "subscribe" is the
        // one that used to reach neither path. Snapshot-then-subscribe under one lock is the fix, so
        // the live handler must not see anything the snapshot already carried.
        ring.Unsubscribe(live.Add);
    }

    [Fact]
    public void AFullFeedRefusesAnotherSubscriptionInsteadOfThrowing()
    {
        var ring = new EventRing(capacity: 16, maxLiveSubscribers: 2);
        Action<RouterEvent> a = _ => { };
        Action<RouterEvent> b = _ => { };
        Assert.True(ring.TrySubscribeReplay(0, a, out _));
        Assert.True(ring.TrySubscribeReplay(0, b, out _));
        Assert.Equal(2, ring.LiveSubscribers);

        // Refusal, not an exception: the HTTP layer has already answered 503 before any SSE headers
        // went out, and a throw after the headers would look to the client like a truncated success.
        Assert.False(ring.TrySubscribeReplay(0, _ => { }, out var refused));
        Assert.Empty(refused);

        ring.Unsubscribe(a);
        Assert.True(ring.TrySubscribeReplay(0, _ => { }, out _));   // room again once one leaves
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

[Fact]
    public void Replay_A_MaxLongCursor_DoesNotThrowAfterTheResponseIsCommitted()
    {
        var ring = new EventRing(capacity: 16);
        ring.Append(Evt("request.enqueued"));

        // The build is checked-arithmetic, so `since + 1` overflows on long.MaxValue. That happened
        // after the SSE headers had already gone out, which is the worst place in the request path to
        // throw: the client sees a 200 stream that dies, not an error.
        Assert.Empty(ring.Replay(long.MaxValue));
        Assert.Empty(ring.Replay(long.MaxValue - 1));
        Assert.Single(ring.Replay(long.MaxValue - long.MaxValue));
    }

    [Fact]
    public async Task SubscribeReplay_UnderConcurrentAppends_DeliversEveryEventExactlyOnce()
    {
        var ring = new EventRing(capacity: 4096);       // bigger than the load: no eviction in play
        var live = new System.Collections.Concurrent.ConcurrentBag<long>();
        Action<RouterEvent> handler = e => live.Add(e.Seq);

        var producers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++) ring.Append(Evt("engine.health"));
        })).ToArray();

        // Subscribing while producers are mid-flight is the handover window. Every event is either in
        // the snapshot or in the live tail, never in neither (lost) and never in both (duplicated).
        Assert.True(ring.TrySubscribeReplay(0, handler, out var snapshot));
        await Task.WhenAll(producers);
        ring.Unsubscribe(handler);

        var all = snapshot.Select(e => e.Seq).Concat(live).OrderBy(x => x).ToList();
        Assert.Equal(ring.CurrentSeq, all.Count);
        Assert.Equal(all.Distinct().Count(), all.Count);
    }

    [Fact(Timeout = 20_000)]
    public async Task Feed_ACursorFromAPreviousRouterLifetime_StillTakesTheLiveTail()
    {
        var (router, events) = FeedHarness();
        await using var _ = router;

        events.Append(Evt("request.enqueued"));         // pins the ring head at 1 before subscribing

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = router.SubscribeEvents(new RouterEventFilter { SinceSeq = 9_000 }, cts.Token)
                                   .GetAsyncEnumerator();
        try
        {
            // The iterator runs synchronously to its first await, so the snapshot, the clamp and the
            // subscription are all in place before MoveNextAsync returns — the flood below is live to it.
            var pending = enumerator.MoveNextAsync();
            await Task.Run(() =>
            {
                for (var i = 0; i < 5; i++) events.Append(Evt("engine.health"));
            });

            // A client that reconnects with a Seq from before a restart holds a cursor this router will
            // never reach. Filtering the live tail with it subscribes a feed that can only be empty; the
            // cursor is clamped to the head, so the live tail still arrives.
            var seen = new List<long>();
            if (await pending)
            {
                seen.Add(enumerator.Current.Seq);
                // Stop at the fifth live event: the feed stays open by design, so waiting for it to
                // end would wait for the caller's timeout instead of the thing under test.
                while (seen.Count < 5 && await enumerator.MoveNextAsync()) seen.Add(enumerator.Current.Seq);
            }

            Assert.Equal([2, 3, 4, 5, 6], seen);
            Assert.False(cts.IsCancellationRequested);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>§10, TASK-93 AC #1 and AC #5: the request path writes its own failure sentences, and more than one
    /// of them interpolates text that came from a request body. The ring, not the writer, owns the budget, so a
    /// caller who posts a four-hundred-and-five-character model id cannot choose how much text a feed record
    /// carries — whichever producer wrote it. And because a caller chooses the head of their own id, two ids that
    /// cut to the same head have to stay distinguishable <b>in the record</b>: the marker fingerprints the whole
    /// value. The id is still matched whole against the catalog; the cap is on reporting, never on lookup (§6.1).</summary>
    [Fact(Timeout = 60_000)]
    public async Task ARequestPathFailureStoresItsFieldsAtReportingLength()
    {
        var one = "qwen/" + new string('x', 400);                       // 405 characters, all caller-chosen
        var two = "qwen/" + new string('x', 400) + "-tail";             // same head, different whole value
        var events = new EventRing();
        var cfg = new RouterConfig
        {
            DefaultModel = one,
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
                        // Both ids are declared, so admission passes and the failure happens in the request path.
                        // The command starts and the engine never reports the model, which is the case that makes
                        // the request path write a reason sentence quoting the caller's own id.
                        [one] = new EngineModelDefinition
                        {
                            Name = one,
                            Select = new LifecycleCommand { Exec = "/bin/true", WaitHealth = true, WaitTimeoutMs = 1_500 },
                        },
                        [two] = new EngineModelDefinition
                        {
                            Name = two,
                            Select = new LifecycleCommand { Exec = "/bin/true", WaitHealth = true, WaitTimeoutMs = 1_500 },
                        },
                    },
                },
            },
        };
        var router = new RouterCore(cfg, events: events);
        await using var _ = router;
        router.Start();

        var first = await router.EnqueueAsync("t-one", LongIdRequest(one));
        var exOne = await Assert.ThrowsAsync<RouterRequestException>(
            () => first.CompletionTask.WaitAsync(TimeSpan.FromSeconds(20)));
        var second = await router.EnqueueAsync("t-two", LongIdRequest(two));
        await Assert.ThrowsAsync<RouterRequestException>(
            () => second.CompletionTask.WaitAsync(TimeSpan.FromSeconds(20)));

        var records = events.Replay(0).Where(e => e.Kind == "request.failed").ToList();
        Assert.Equal(2, records.Count);
        foreach (var record in records)
        {
            Assert.NotNull(record.Reason);
            Assert.NotNull(record.Model);
            Assert.True(record.Reason!.Length <= WireText.ReasonChars,
                $"a request-path reason of {record.Reason!.Length} characters exceeds the documented {WireText.ReasonChars}");
            Assert.True(record.Model!.Length <= WireText.IdentifierChars + WireText.MarkerReserve,
                $"the event payload carries {record.Model!.Length} characters of model id");
            Assert.DoesNotContain(one, record.Reason!);
            Assert.DoesNotContain(two, record.Reason!);
            Assert.DoesNotContain(one, record.Model!);
            Assert.DoesNotContain(two, record.Model!);
            Assert.Contains("… (+", record.Reason!);            // the reader sees that something was cut
            Assert.Contains("ch #", record.Reason!);            // …and how much, and which value it was
        }

        // The head is what the caller chose, so the two records agree there; the whole record does not, which is
        // what makes either request findable by an operator reading the feed.
        Assert.StartsWith(one[..96], records[0].Model!);
        Assert.Equal(records[0].Model![..96], records[1].Model![..96]);
        Assert.NotEqual(records[0].Model, records[1].Model);
        Assert.NotEqual(records[0].Reason, records[1].Reason);

        // The caller's own answer is bounded the same way: the sentence is the router's, even when the identifier
        // inside it is the caller's.
        Assert.DoesNotContain(one, exOne.Message!);
        Assert.Contains("… (+", exOne.Message!);
    }

    private static InferenceRequest LongIdRequest(string model) => new()
    {
        Model = model,
        Body = Encoding.UTF8.GetBytes(
            $$"""{"model":"{{model}}","messages":[{"role":"user","content":"ping"}]}"""),
        Options = new RouterRequestOptions(),
    };

    /// <summary>§10, TASK-93 AC #1/#3: the funnel is where the budget lives, so it holds for a producer that writes
    /// an event directly — engine name and thread id included — and not only for the engine's own emitter. A filter
    /// value of the same length still matches, because the cap is applied to the filter side too: reporting is
    /// bounded, lookup is not (§6.1).</summary>
    [Fact]
    public void TheFunnelCapsIdentifiersAndReasonForEveryProducer()
    {
        var events = new EventRing();
        var longEngine = new string('e', 400);
        var longThread = new string('t', 400);
        var longModel = new string('m', 400);
        var longReason = new string('r', 4_000);
        var seq = events.Append(new RouterEvent
        {
            Kind = "engine.health",
            Engine = longEngine,
            ThreadId = longThread,
            Model = longModel,
            Reason = longReason,
        });

        var stored = events.Replay(0).Single();
        Assert.Equal(seq, stored.Seq);
        Assert.True(stored.Engine!.Length <= WireText.IdentifierChars + WireText.MarkerReserve,
            $"the feed carries {stored.Engine!.Length} characters of engine name");
        Assert.True(stored.ThreadId!.Length <= WireText.IdentifierChars + WireText.MarkerReserve,
            $"the feed carries {stored.ThreadId!.Length} characters of thread id");
        Assert.True(stored.Model!.Length <= WireText.IdentifierChars + WireText.MarkerReserve,
            $"the feed carries {stored.Model!.Length} characters of model id");
        Assert.True(stored.Reason!.Length <= WireText.ReasonChars,
            $"a feed reason of {stored.Reason!.Length} characters exceeds the documented {WireText.ReasonChars}");
        Assert.Contains("… (+", stored.Reason!);
        Assert.DoesNotContain(longReason, stored.Reason!);
        Assert.DoesNotContain(longEngine, stored.Engine!);

        Assert.Single(events.Replay(0), e =>
            new RouterEventFilter { SinceSeq = 0, ThreadId = longThread, Engine = longEngine }.Matches(e));
        Assert.DoesNotContain(events.Replay(0), e =>
            new RouterEventFilter { SinceSeq = 0, ThreadId = new string('t', 401) }.Matches(e));
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
