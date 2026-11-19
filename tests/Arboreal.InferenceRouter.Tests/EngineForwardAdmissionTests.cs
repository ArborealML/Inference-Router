using System.Net;
using System.Threading.Channels;

namespace Arboreal.InferenceRouter.Tests;

public sealed class EngineForwardAdmissionTests
{
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReviewLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReviewHoldLimit = TimeSpan.FromSeconds(30);
    private const string CloseFaultMessage = "fixture fault from HTTP client cancellation";

    [Theory(Timeout = 20_000)]
    [InlineData(0, false)]
    [InlineData(60_000, false)]
    [InlineData(500, true)]
    public async Task AnAdmittedModelOnlySendKeepsItsClientThroughAnEngineLocalStop(
        int responseTimeoutMs, bool expectTimeout)
    {
        using var handler = new CapturingHandler { LoadedModels = ["plain"] };
        var definition = new EngineDefinition
        {
            Name = "fixture",
            Endpoint = new Uri("http://fixture.invalid/v1"),
            Remote = true,
            ResponseTimeoutMs = responseTimeoutMs,
            Health = new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000 },
        };
        var config = new RouterConfig
        {
            AutoSwitch = false,
            Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal)
            {
                [definition.Name] = definition,
            },
            HttpHandlerFactory = _ => handler,
        };
        var events = new EventRing();
        var healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        events.Subscribe(evt =>
        {
            if (evt.Kind == "engine.health" && evt.Engine == definition.Name) healthy.TrySetResult();
        });
        await using var router = new RouterCore(config, events: events);
        var engine = router.Engines[definition.Name];
        var admission = new Pause();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int releases = 0;
        engine.AfterForwardAdmissionForTests = admission.EnterAsync;
        engine.AfterForwardReleaseForTests = () =>
        {
            Interlocked.Increment(ref releases);
            released.TrySetResult();
        };
        router.Start();
        await healthy.Task.WaitAsync(HoldLimit);
        Assert.True(engine.IsReadyFor("plain", profile: null));
        Assert.Null(engine.Capability.SwitchProof);
        byte[] body = """{"model":"plain","prompt":"admitted model-only"}"""u8.ToArray();
        var ticket = await router.EnqueueAsync("fixture", new InferenceRequest
        {
            Model = "plain",
            Body = body,
            Options = new RouterRequestOptions(),
        });

        try
        {
            await admission.Entered.WaitAsync(HoldLimit);
            Assert.Equal(1, engine.HttpUsesForTests);
            Assert.Equal(0, handler.Writes);
            await engine.DisposeAsync().AsTask().WaitAsync(HoldLimit);
            Assert.Equal(1, engine.HttpUsesForTests);
            Assert.Equal(0, handler.Writes);
            AssertClosedToNewClaims(engine);

            admission.Release();
            await handler.BeforeReply.Entered.WaitAsync(HoldLimit);
            Assert.Equal(1, handler.Writes);
            Assert.Equal(body, Assert.IsType<byte[]>(handler.Body));

            if (expectTimeout)
            {
                var error = await Assert.ThrowsAsync<RouterRequestException>(
                    () => ticket.CompletionTask.WaitAsync(HoldLimit));
                Assert.Equal("engine_timeout", error.Code);
                Assert.True(handler.SendCancellation.IsCancellationRequested);
                Assert.Equal(1, engine.HttpUsesForTests);
                Assert.Equal(0, Volatile.Read(ref releases));
                Assert.False(handler.BeforeDisposal.Entered.IsCompleted);
                AssertClosedToNewClaims(engine);
            }

            handler.BeforeReply.Release();
            await handler.BeforeDisposal.Entered.WaitAsync(HoldLimit);
            Assert.Equal(1, engine.HttpUsesForTests);
            Assert.Equal(0, Volatile.Read(ref releases));
            Assert.Equal(0, handler.Disposals);
            AssertClosedToNewClaims(engine);

            handler.BeforeDisposal.Release();
            await released.Task.WaitAsync(HoldLimit);
            Assert.Equal(0, engine.HttpUsesForTests);
            Assert.Equal(1, Volatile.Read(ref releases));
            Assert.Equal(1, handler.Disposals);
            AssertClosedToNewClaims(engine);
            Assert.Equal(1, handler.Writes);

            if (expectTimeout)
            {
                Assert.Equal(TicketState.Failed, ticket.State);
                Assert.Equal("engine_timeout", ticket.ErrorCode);
            }
            else
            {
                Assert.Equal("{}"u8.ToArray(), await ticket.CompletionTask.WaitAsync(HoldLimit));
                Assert.Equal(TicketState.Completed, ticket.State);
            }
        }
        finally
        {
            admission.Release();
            handler.BeforeReply.Release();
            handler.BeforeDisposal.Release();
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreHeaderTimeoutRetainsBothLocalUsesThroughLateSuccessAndFault(bool faultFinishesFirst)
    {
        using var handler = new HeldForwardHandler { LoadedModels = ["plain"] };
        var events = new EventRing();
        var healthy = WaitForHealthyAsync(events);
        var clock = new ReadinessDeadlineTests.ManualClock();
        await using var router = CreateHeldRouter(handler, events, responseTimeoutMs: 100);
        router.ResponseTimeoutClockForTests = clock;
        var engine = router.Engines["fixture"];
        var releases = Channel.CreateUnbounded<bool>();
        engine.AfterForwardReleaseForTests = () => releases.Writer.TryWrite(true);
        router.Start();

        try
        {
            await healthy.WaitAsync(ReviewLimit);
            var succeedsLate = await EnqueueHeldTicketAsync(router, "success");
            await handler.First.Entered.Task.WaitAsync(ReviewLimit);
            var faultsLate = await EnqueueHeldTicketAsync(router, "fault");
            await handler.Second.Entered.Task.WaitAsync(ReviewLimit);

            clock.AdvanceTo(99);
            Assert.False(succeedsLate.CompletionTask.IsCompleted);
            Assert.False(faultsLate.CompletionTask.IsCompleted);

            clock.AdvanceTo(100);
            await AssertEngineTimeoutAsync(succeedsLate);
            await AssertEngineTimeoutAsync(faultsLate);
            await handler.First.Cancelled.Task.WaitAsync(ReviewLimit);
            await handler.Second.Cancelled.Task.WaitAsync(ReviewLimit);
            Assert.False(releases.Reader.TryRead(out _));

            await engine.DisposeAsync().AsTask().WaitAsync(ReviewLimit);
            await engine.DisposeAsync().AsTask().WaitAsync(ReviewLimit);
            Assert.Equal(2, engine.HttpUsesForTests);
            Assert.Equal(0, handler.First.ResponseDisposals);
            engine.Http.CancelPendingRequests();

            var first = faultFinishesFirst ? handler.Second : handler.First;
            var last = faultFinishesFirst ? handler.First : handler.Second;
            first.Release.TrySetResult();
            await releases.Reader.ReadAsync().AsTask().WaitAsync(ReviewLimit);
            Assert.Equal(1, engine.HttpUsesForTests);
            Assert.Equal(faultFinishesFirst, handler.Second.FaultRaised.Task.IsCompletedSuccessfully);
            Assert.Equal(faultFinishesFirst ? 0 : 1, handler.First.ResponseDisposals);
            // Both sends already saw their timeout cancellation. This checks the actual
            // client is still open without creating a new HTTP request.
            engine.Http.CancelPendingRequests();

            last.Release.TrySetResult();
            await releases.Reader.ReadAsync().AsTask().WaitAsync(ReviewLimit);
            Assert.Equal(0, engine.HttpUsesForTests);
            Assert.True(handler.Second.FaultRaised.Task.IsCompletedSuccessfully);
            Assert.Equal(1, handler.First.ResponseDisposals);
            Assert.Throws<ObjectDisposedException>(engine.Http.CancelPendingRequests);
            Assert.False(releases.Reader.TryRead(out _));
            Assert.Equal(2, events.Replay(0).Count(e => e.Kind == "request.failed"));
            await AssertEngineTimeoutAsync(succeedsLate);
            await AssertEngineTimeoutAsync(faultsLate);
        }
        finally
        {
            handler.First.Release.TrySetResult();
            handler.Second.Release.TrySetResult();
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task HealthAdmissionAndClientLifetimeRespectStop(
        bool modelsAlreadyStarted, bool stopLoopOnly)
    {
        using var handler = new HeldForwardHandler { LoadedModels = ["plain"] };
        var events = new EventRing();
        var healthy = WaitForHealthyAsync(events);
        await using var router = CreateHeldRouter(handler, events, responseTimeoutMs: 0);
        var engine = router.Engines["fixture"];
        var beforeProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forwardReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AfterForwardReleaseForTests = () => forwardReleased.TrySetResult();
        using var loop = new CancellationTokenSource();
        router.Start();
        var probe = Task.CompletedTask;

        try
        {
            await healthy.WaitAsync(ReviewLimit);
            var ticket = await EnqueueHeldTicketAsync(router, "held");
            await handler.First.Entered.Task.WaitAsync(ReviewLimit);
            Assert.Equal(2, handler.GetCalls);

            // Race stop with admission of either the models or state GET in a real health round.
            if (modelsAlreadyStarted)
            {
                handler.HoldModels = true;
            }
            else
            {
                engine.BeforeModelsFetchForTests = () =>
                {
                    beforeProbe.TrySetResult();
                    releaseProbe.Task.WaitAsync(ReviewHoldLimit, CancellationToken.None).GetAwaiter().GetResult();
                };
            }
            probe = Task.Run(() => engine.RunHealthLoopAsync(loop.Token), CancellationToken.None);
            var probeEntered = modelsAlreadyStarted ? handler.ModelsEntered.Task : beforeProbe.Task;
            await probeEntered.WaitAsync(ReviewLimit);
            Assert.Equal(modelsAlreadyStarted ? 2 : 1, engine.HttpUsesForTests);

            if (stopLoopOnly)
                await loop.CancelAsync();
            else
                await engine.DisposeAsync().AsTask().WaitAsync(ReviewLimit);

            if (modelsAlreadyStarted)
            {
                // The health GET is now the last owner. Closing the client here would cancel it.
                handler.First.Release.TrySetResult();
                await forwardReleased.Task.WaitAsync(ReviewLimit);
                Assert.Equal(1, engine.HttpUsesForTests);
                Assert.False(handler.ModelsCancellation.IsCancellationRequested);
            }

            releaseProbe.TrySetResult();
            handler.ReleaseModels.TrySetResult();
            await probe.WaitAsync(ReviewLimit);
            Assert.Equal(modelsAlreadyStarted ? 3 : 2, handler.GetCalls);

            if (!modelsAlreadyStarted)
            {
                Assert.Equal(1, engine.HttpUsesForTests);
                Assert.False(ticket.CompletionTask.IsCompleted);
                Assert.False(forwardReleased.Task.IsCompleted);
                if (stopLoopOnly) await engine.DisposeAsync().AsTask().WaitAsync(ReviewLimit);
                handler.First.Release.TrySetResult();
            }
            await ticket.CompletionTask.WaitAsync(ReviewLimit);
            await forwardReleased.Task.WaitAsync(ReviewLimit);
            Assert.Equal(TicketState.Completed, ticket.State);
            Assert.Equal(1, handler.First.ResponseDisposals);
            Assert.Equal(0, engine.HttpUsesForTests);
            Assert.Throws<ObjectDisposedException>(engine.Http.CancelPendingRequests);
        }
        finally
        {
            releaseProbe.TrySetResult();
            handler.ReleaseModels.TrySetResult();
            handler.First.Release.TrySetResult();
            await probe.WaitAsync(ReviewLimit);
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseFaultCannotSkipLifecycleDisposalOrReplaceTheTicketOutcome(bool deferred)
    {
        using var handler = new HeldForwardHandler(firstPostFaults: true) { LoadedModels = ["plain"] };
        var events = new EventRing();
        var healthy = WaitForHealthyAsync(events);
        var clock = new ReadinessDeadlineTests.ManualClock();
        await using var router = CreateHeldRouter(handler, events, responseTimeoutMs: 100, ownsLifecycle: true);
        router.ResponseTimeoutClockForTests = clock;
        var engine = router.Engines["fixture"];
        var lifecycleDisposals = 0;
        var forwardReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.AfterLifecycleDisposeForTests = () => Interlocked.Increment(ref lifecycleDisposals);
        engine.AfterForwardReleaseForTests = () => forwardReleased.TrySetResult();
        router.Start();

        await healthy.WaitAsync(ReviewLimit);
        Assert.Equal("docker", engine.LifecycleProvider);

        // Use the public client directly to install a throwing callback on HttpClient's own
        // pending-request token. The injected handler itself stays caller-owned.
        var closeProbe = engine.Http.GetAsync("close-fault", HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        try
        {
            await handler.CloseProbeEntered.Task.WaitAsync(ReviewLimit);
            Assert.Equal(0, engine.HttpUsesForTests);
            if (deferred)
            {
                var ticket = await EnqueueHeldTicketAsync(router, "deferred");
                await handler.First.Entered.Task.WaitAsync(ReviewLimit);
                clock.AdvanceTo(100);
                await AssertEngineTimeoutAsync(ticket);
                Assert.Equal(1, engine.HttpUsesForTests);

                await engine.DisposeAsync().AsTask().WaitAsync(ReviewLimit);
                Assert.Equal(1, Volatile.Read(ref lifecycleDisposals));
                Assert.False(handler.CloseFaultRaised.Task.IsCompleted);

                handler.First.Release.TrySetResult();
                await forwardReleased.Task.WaitAsync(ReviewLimit);
                await AssertEngineTimeoutAsync(ticket);
                Assert.Single(events.Replay(0), e => e.Kind == "request.failed");
            }
            else
            {
                var error = await Assert.ThrowsAsync<AggregateException>(
                    () => engine.DisposeAsync().AsTask().WaitAsync(ReviewLimit));
                var cause = Assert.IsType<InvalidOperationException>(Assert.Single(error.Flatten().InnerExceptions));
                Assert.Equal(CloseFaultMessage, cause.Message);
            }

            Assert.True(handler.CloseFaultRaised.Task.IsCompletedSuccessfully);
            Assert.Equal(0, engine.HttpUsesForTests);
            Assert.Equal(1, Volatile.Read(ref lifecycleDisposals));
            Assert.Throws<ObjectDisposedException>(engine.Http.CancelPendingRequests);
            await engine.DisposeAsync().AsTask().WaitAsync(ReviewLimit);
            Assert.Equal(1, Volatile.Read(ref lifecycleDisposals));
        }
        finally
        {
            handler.First.Release.TrySetResult();
            handler.ReleaseCloseProbe.TrySetResult();
            try
            {
                using var response = await closeProbe.WaitAsync(ReviewLimit);
            }
            catch (OperationCanceledException)
            {
                // The client can cancel this test-only probe while it is closing.
            }
        }
    }

    private static Task WaitForHealthyAsync(EventRing events)
    {
        var healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        events.Subscribe(e =>
        {
            if (e.Kind == "engine.health" && e.Value == 1) healthy.TrySetResult();
        });
        return healthy.Task;
    }

    private static RouterCore CreateHeldRouter(HttpMessageHandler handler, EventRing events, int responseTimeoutMs,
        bool ownsLifecycle = false) =>
        new(new RouterConfig
        {
            AutoSwitch = false,
            Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal)
            {
                ["fixture"] = new()
                {
                    Name = "fixture",
                    Endpoint = new Uri("http://fixture.invalid/v1"),
                    Remote = !ownsLifecycle,
                    Lifecycle = ownsLifecycle ? EngineLifecycleKind.Docker : EngineLifecycleKind.Delegated,
                    Docker = ownsLifecycle ? new DockerLifecycleConfig { Image = "fixture", RouterId = "test" } : null,
                    ResponseTimeoutMs = responseTimeoutMs,
                    Health = new HealthProbeDefinition { Path = "/props", IntervalMs = 3_600_000, TimeoutMs = 60_000 },
                },
            },
            HttpHandlerFactory = _ => handler,
        }, events: events);

    private static ValueTask<RouterTicket> EnqueueHeldTicketAsync(RouterCore router, string thread) =>
        router.EnqueueAsync(thread, new InferenceRequest
        {
            Model = "plain",
            Body = """{"model":"plain","prompt":"held forward"}"""u8.ToArray(),
            Options = new RouterRequestOptions(),
        });

    private static async Task AssertEngineTimeoutAsync(RouterTicket ticket)
    {
        var failure = await Assert.ThrowsAsync<RouterRequestException>(
            () => ticket.CompletionTask.WaitAsync(ReviewLimit));
        Assert.Equal("engine_timeout", failure.Code);
        Assert.Equal(TicketState.Failed, ticket.State);
    }

    private sealed class HeldForwardHandler(bool firstPostFaults = false) : FakeEngineHandler
    {
        private int _getCalls;
        private int _postCalls;

        internal HeldForward First { get; } = new(firstPostFaults);
        internal HeldForward Second { get; } = new(fault: true);
        internal int GetCalls => Volatile.Read(ref _getCalls);
        internal bool HoldModels { get; set; }
        internal CancellationToken ModelsCancellation { get; private set; }
        internal TaskCompletionSource ModelsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseModels { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CloseProbeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CloseFaultRaised { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCloseProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                var call = Interlocked.Increment(ref _postCalls);
                Assert.InRange(call, 1, 2);
                return (call == 1 ? First : Second).RespondAsync(cancellationToken);
            }
            if (request.Method == HttpMethod.Get)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/close-fault", StringComparison.Ordinal))
                    return WaitForFaultingCloseAsync(cancellationToken);
                Interlocked.Increment(ref _getCalls);
                if (HoldModels && path.EndsWith("/models", StringComparison.Ordinal))
                    return ReplyWithHeldModelsAsync(request, cancellationToken);
            }
            return base.SendAsync(request, cancellationToken);
        }

        private async Task<HttpResponseMessage> WaitForFaultingCloseAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                CloseFaultRaised.TrySetResult();
                throw new InvalidOperationException(CloseFaultMessage);
            });
            CloseProbeEntered.TrySetResult();
            await ReleaseCloseProbe.Task.WaitAsync(ReviewHoldLimit, CancellationToken.None);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private async Task<HttpResponseMessage> ReplyWithHeldModelsAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ModelsCancellation = cancellationToken;
            ModelsEntered.TrySetResult();
            await ReleaseModels.Task.WaitAsync(ReviewHoldLimit, CancellationToken.None);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class HeldForward(bool fault)
    {
        private int _responseDisposals;
        internal int ResponseDisposals => Volatile.Read(ref _responseDisposals);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FaultRaised { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task<HttpResponseMessage> RespondAsync(CancellationToken ct)
        {
            using var registration = ct.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ReviewHoldLimit, CancellationToken.None);
            if (fault)
            {
                FaultRaised.TrySetResult();
                throw new HttpRequestException("late fake upstream fault");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new TrackedResponse(() => Interlocked.Increment(ref _responseDisposals)),
            };
        }
    }

    private sealed class TrackedResponse(Action disposed) : StringContent("""{"choices":[]}""")
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) disposed();
        }
    }

    private static void AssertClosedToNewClaims(EngineRuntime engine)
    {
        var claimed = engine.TryBeginForward(CancellationToken.None);
        if (claimed) engine.EndForward();
        Assert.False(claimed);
    }

    private sealed class Pause
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;
        internal void Release() => _release.TrySetResult();

        internal Task EnterAsync()
        {
            _entered.TrySetResult();
            return _release.Task.WaitAsync(HoldLimit);
        }
    }

    private sealed class CapturingHandler : FakeEngineHandler
    {
        private int _writes;
        private int _disposals;
        internal int Writes => Volatile.Read(ref _writes);
        internal int Disposals => Volatile.Read(ref _disposals);
        internal byte[]? Body { get; private set; }
        internal CancellationToken SendCancellation { get; private set; }
        internal Pause BeforeReply { get; } = new();
        internal Pause BeforeDisposal { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
                return await base.SendAsync(request, cancellationToken);

            Interlocked.Increment(ref _writes);
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            SendCancellation = cancellationToken;
            // Allow a late response even after the router has cancelled the send.
            await BeforeReply.EnterAsync();
            return new HttpResponseMessage { Content = new HeldContent(this) };
        }

        private sealed class HeldContent(CapturingHandler handler) : StringContent("{}")
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing) handler.BeforeDisposal.EnterAsync().GetAwaiter().GetResult();
                base.Dispose(disposing);
                if (disposing) Interlocked.Increment(ref handler._disposals);
            }
        }
    }
}
