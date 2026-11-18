using Arboreal.InferenceRouter;

namespace Arboreal.InferenceRouter.Tests;

public class ModelQueueTests
{
    private static QueuedRequest Make(string threadId, ThreadPriority priority, long seq, string model = "m")
    {
        var ticket = new RouterTicket($"t{seq}", threadId, model, DateTimeOffset.UtcNow);
        var thread = new RouterThread(threadId, priority, 1.0, null);
        var node = new PrioritizedRequest(ticket, priority, seq);
        return new QueuedRequest
        {
            Ticket = ticket,
            Request = new InferenceRequest
            {
                Model = model,
                Stream = false,
                Body = "{}"u8.ToArray(),
                Options = new RouterRequestOptions(),
            },
            PriorityNode = node,
            Thread = thread,
            EnqueuedSeq = seq,
            PromptChars = 10,
        };
    }

    [Fact]
    public void Pops_HighestPriority_ThenFifo()
    {
        var queue = new ModelQueue("m", 16, (_, _) => { });
        queue.TryEnqueue(Make("t-a", ThreadPriority.Normal(), 1));
        queue.TryEnqueue(Make("t-b", ThreadPriority.High(), 2));
        queue.TryEnqueue(Make("t-c", ThreadPriority.Normal(), 3));
        queue.TryEnqueue(Make("t-d", ThreadPriority.Normal(10), 4));

        Assert.Equal("t-b", queue.TryPopHead(_ => true)!.Ticket.ThreadId); // high first
        Assert.Equal("t-d", queue.TryPopHead(_ => true)!.Ticket.ThreadId); // normal:200
        Assert.Equal("t-a", queue.TryPopHead(_ => true)!.Ticket.ThreadId); // seq 1 before 3
        Assert.Equal("t-c", queue.TryPopHead(_ => true)!.Ticket.ThreadId);
        Assert.Null(queue.TryPopHead(_ => true));
    }

    [Fact]
    public void Admission_Gate_Pops_Over_Blocked_Head()
    {
        var queue = new ModelQueue("m", 16, (_, _) => { });
        queue.TryEnqueue(Make("t-a", ThreadPriority.Normal(), 1));
        queue.TryEnqueue(Make("t-b", ThreadPriority.High(), 2));

        // t-a blocked (at capacity); t-b may skip it.
        Assert.Equal("t-b", queue.TryPopHead(t => t.ThreadId != "t-a")!.Ticket.ThreadId);
        Assert.Equal("t-a", queue.TryPopHead(_ => true)!.Ticket.ThreadId);
    }

    [Fact]
    public void AThreadBecomingAdmissibleInvalidatesTheSnapshotWithoutTakingEitherTicket()
    {
        var queue = new ModelQueue("m", 16, (_, _) => { });
        var head = Make("first", ThreadPriority.High(), 1);
        var second = Make("second", ThreadPriority.Normal(), 2);
        queue.TryEnqueue(head);
        queue.TryEnqueue(second);
        head.Thread.IncrementInflight();

        var snapshot = queue.SnapshotCandidate(7, HasCapacity);

        Assert.Equal(2, snapshot.Depth);
        Assert.Equal(7, snapshot.BaselineOrdinal);
        Assert.Same(second, snapshot.Request);
        head.Thread.DecrementInflight();
        Assert.False(queue.TryTakeExact(snapshot, HasCapacity));
        Assert.Equal(2, queue.Depth);
        Assert.Same(head, queue.SnapshotCandidate(7, HasCapacity).Request);

        static bool HasCapacity(RouterThread thread) => thread.Inflight < thread.MaxInflight;
    }

    [Fact]
    public void Overflow_Rejects_And_Calls_Callback()
    {
        string? rejectReason = null;
        var queue = new ModelQueue("m", 2, (item, reason) =>
        {
            rejectReason = reason;
        });
        Assert.True(queue.TryEnqueue(Make("t-a", ThreadPriority.Normal(), 1)));
        Assert.True(queue.TryEnqueue(Make("t-a", ThreadPriority.Normal(), 2)));
        Assert.False(queue.TryEnqueue(Make("t-a", ThreadPriority.Normal(), 3)));
        Assert.Equal("queue_overflow", rejectReason);
        Assert.Equal(2, queue.Depth);
    }

    [Fact]
    public void TryRemoveById_Works()
    {
        var queue = new ModelQueue("m", 16, (_, _) => { });
        queue.TryEnqueue(Make("t-a", ThreadPriority.Normal(), 1));
        Assert.True(queue.TryRemoveById("t1"));
        Assert.False(queue.TryRemoveById("t1")); // already removed
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public void Resort_Is_Stable_And_Total()
    {
        var queue = new ModelQueue("m", 16, (_, _) => { });
        queue.TryEnqueue(Make("t-a", ThreadPriority.Normal(), 1));
        queue.TryEnqueue(Make("t-b", ThreadPriority.Normal(5), 2));
        queue.TryEnqueue(Make("t-c", ThreadPriority.Normal(), 3));
        queue.Resort();
        // order unchanged: sub-priority 5 still beats equal-class seq ties
        Assert.Equal("t-b", queue.TryPopHead(_ => true)!.Ticket.ThreadId);
        Assert.Equal("t-a", queue.TryPopHead(_ => true)!.Ticket.ThreadId);
        Assert.Equal("t-c", queue.TryPopHead(_ => true)!.Ticket.ThreadId);
    }
}
