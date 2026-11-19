using System.Net;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class SwitchCostDispatchTests
{
    [Theory(Timeout = 20_000)]
    [InlineData(20, 80, "a")]
    [InlineData(80, 20, "b")]
    public async Task ChangingOnlyMeasuredActivationChangesTheExactTicketAndNativeLaunch(
        int aLoadMs, int bLoadMs, string winner)
    {
        await using var rig = new SwitchRig(aLoadMs, bLoadMs);
        await rig.TrainAsync();
        rig.Clock.Set(TimeSpan.FromMinutes(1));
        var a = await rig.EnqueueAsync("a");
        var b = await rig.EnqueueAsync("b");

        Assert.Equal(TimeSpan.FromMilliseconds(aLoadMs + 7), rig.Estimate("a"));
        Assert.Equal(TimeSpan.FromMilliseconds(bLoadMs + 7), rig.Estimate("b"));
        await rig.AssertDispatchAsync(winner == "a" ? a : b);
        Assert.Equal(TicketState.Queued, winner == "a" ? b.State : a.State);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(-1, "a")]
    [InlineData(0, "b")]
    public async Task AnEligibleAgedModelDispatchesAtTheInclusiveBoundary(int ticksFromBoundary, string winner)
    {
        await using var rig = new SwitchRig(aLoadMs: 20, bLoadMs: 80);
        await rig.TrainAsync();
        var enqueueTime = TimeSpan.FromMinutes(1);
        rig.Clock.Set(enqueueTime);
        var b = await rig.EnqueueAsync("b");
        rig.Clock.Set(enqueueTime + TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(ticksFromBoundary));
        var a = await rig.EnqueueAsync("a");

        await rig.AssertDispatchAsync(winner == "a" ? a : b);
    }

    [Fact(Timeout = 20_000)]
    public async Task AContenderWithTooFewSamplesRestoresEnqueueOrderForTheWholeComparison()
    {
        await using var rig = new SwitchRig(aLoadMs: 20, bLoadMs: 80);
        await rig.TrainAsync(bSamples: 2);
        rig.Clock.Set(TimeSpan.FromMinutes(1));
        var b = await rig.EnqueueAsync("b");
        await rig.EnqueueAsync("a");

        Assert.NotNull(rig.Estimate("a"));
        Assert.Null(rig.Estimate("b"));
        await rig.AssertDispatchAsync(b);
    }

    [Fact(Timeout = 20_000)]
    public async Task ACheapModelThatCannotFitTheRequestDoesNotEnterTheComparison()
    {
        await using var rig = new SwitchRig(aLoadMs: 20, bLoadMs: 80);
        await rig.TrainAsync();
        rig.Clock.Set(TimeSpan.FromMinutes(1));
        var a = await rig.EnqueueAsync("a", requiredContext: 8_192);
        var b = await rig.EnqueueAsync("b");

        await rig.AssertDispatchAsync(b);
        Assert.Equal(TicketState.Queued, a.State);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true, "a")]
    [InlineData(false, "b")]
    public async Task ALoadedRouteEndsTheSwitchComparisonOnlyWithAProvenWindow(
        bool proveIncumbentWindow, string winner)
    {
        await using var rig = new SwitchRig(aLoadMs: 80, bLoadMs: 20);
        await rig.TrainAsync(proveIncumbentWindow: proveIncumbentWindow);
        rig.Clock.Set(TimeSpan.FromMinutes(1));

        var capability = rig.Engine.Capability;
        Assert.Equal("c", Assert.Single(capability.ModelRoster));
        Assert.Equal(proveIncumbentWindow ? 1_024L : 0L, capability.Serving.WindowFor("c"));
        Assert.Equal(TimeSpan.FromMilliseconds(87), rig.Estimate("a"));
        Assert.Equal(TimeSpan.FromMilliseconds(27), rig.Estimate("b"));

        var a = await rig.EnqueueAsync("a");
        var c = await rig.EnqueueAsync("c", requiredContext: 1_024);
        var b = await rig.EnqueueAsync("b");

        await rig.AssertDispatchAsync(winner == "a" ? a : b);
        Assert.Equal(TicketState.Queued, c.State);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(null, 0L, true)]
    [InlineData(128L, 0L, false)]
    [InlineData(128L, 127L, false)]
    [InlineData(128L, 128L, true)]
    public async Task SwitchWithoutATierProvesTheExplicitFloorBeforeSending(
        long? explicitMinimum, long launchWindow, bool forwards)
    {
        await using var rig = new SwitchRig(aLoadMs: 80, bLoadMs: 20, servingLadder: false);
        await rig.LoadAsync("c");
        var ticket = await rig.EnqueueAsync("a", requiredContext: explicitMinimum);

        await rig.AssertModelSwitchFloorAsync(ticket, launchWindow, forwards);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(129L, 128L, true)]
    [InlineData(128L, 129L, false)]
    public async Task RankedSwitchUsesTheWinnersExplicitFloorInsteadOfTheScanHeads(
        long headMinimum, long winnerMinimum, bool forwards)
    {
        await using var rig = new SwitchRig(aLoadMs: 80, bLoadMs: 20, servingLadder: false);
        await rig.TrainAsync();
        rig.Clock.Set(TimeSpan.FromMinutes(1));
        var head = await rig.EnqueueAsync("a", requiredContext: headMinimum);
        var winner = await rig.EnqueueAsync("b", requiredContext: winnerMinimum);
        Assert.Equal(TimeSpan.FromMilliseconds(87), rig.Estimate("a"));
        Assert.Equal(TimeSpan.FromMilliseconds(27), rig.Estimate("b"));

        await rig.AssertModelSwitchFloorAsync(winner, launchWindow: 128, forwards);
        Assert.Equal(TicketState.Queued, head.State);
    }

    [Fact(Timeout = 20_000)]
    public async Task AReadOnlyRefusalRestoresTheServingTupleAndTheMeasuredIncumbent()
    {
        await using var rig = new SwitchRig(aLoadMs: 80, bLoadMs: 20);
        await rig.LoadProfileAsync("c");
        var before = rig.Engine.Capability;
        var incumbent = Assert.IsType<SwitchSampleKey>(rig.Engine.ReadVerifiedSwitchIncumbent(before));
        Assert.Equal(1_024L, before.Serving.WindowFor("c"));
        rig.RefusePreflight = true;

        var result = await rig.Engine.WaitForModelAsync("a", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.IsReady);
        var restored = rig.Engine.Capability;
        Assert.True(restored.Healthy);
        Assert.Null(restored.SwitchingToward);
        Assert.Equal(before.Serving, restored.Serving);
        Assert.Same(before.SwitchProof, restored.SwitchProof);
        Assert.Equal(incumbent, Assert.IsType<SwitchSampleKey>(rig.Engine.ReadVerifiedSwitchIncumbent(restored)));
        var refused = rig.Engine.SwitchHistory[^1].Terminal;
        Assert.Equal(SwitchAttemptOutcome.Failed, refused.Outcome);
        Assert.Null(refused.GetStage(SwitchStage.Shutdown));
        Assert.Null(refused.GetStage(SwitchStage.Launch));
    }

    [Fact(Timeout = 20_000)]
    public async Task ANewIncumbentInvalidatesTheCostPickBeforeTheExactTicketIsTaken()
    {
        await using var rig = new SwitchRig(aLoadMs: 80, bLoadMs: 20);
        await rig.TrainAsync();
        rig.Clock.Set(TimeSpan.FromMinutes(1));
        var a = await rig.EnqueueAsync("a");
        var b = await rig.EnqueueAsync("b");

        await rig.ReplaceIncumbentBeforeCommitAsync("a");

        Assert.Equal(TicketState.Queued, a.State);
        Assert.Equal(TicketState.Queued, b.State);
    }

    [Fact(Timeout = 20_000)]
    public async Task TheSingleEngineCostWinnerSurvivesAChangeInPressureAtEqualInflight()
    {
        await using var rig = new SwitchRig(aLoadMs: 80, bLoadMs: 20);
        await rig.TrainAsync();
        rig.Clock.Set(TimeSpan.FromMinutes(1));
        var a = await rig.EnqueueAsync("a");
        var b = await rig.EnqueueAsync("b");
        var engine = rig.Engine;
        var capability = engine.Capability;
        const long promptChars = 8_192;

        // Model the accounting interval after EndRequest releases the count and before
        // it subtracts the prompt. Balance the deferred charge while exact take is paused.
        engine.BeginRequest(promptChars);
        engine.EndRequest(0);
        await rig.AssertDispatchAsync(b, () =>
        {
            Assert.Equal(0, engine.Inflight);
            Assert.Equal(promptChars, engine.InflightTokenProxy);
            engine.BeginRequest(0);
            engine.EndRequest(promptChars);
            Assert.Equal(0, engine.Inflight);
            Assert.Equal(0L, engine.InflightTokenProxy);
            Assert.Same(capability, engine.Capability);
            return Task.CompletedTask;
        });
        Assert.Equal(TicketState.Queued, a.State);
    }

    [Fact(Timeout = 20_000)]
    public async Task CancellingTheRankedWinnerDoesNotLaunchItOrCommitASubstitute()
    {
        await using var rig = new SwitchRig(aLoadMs: 80, bLoadMs: 20);
        await rig.TrainAsync();
        rig.Clock.Set(TimeSpan.FromMinutes(1));
        var a = await rig.EnqueueAsync("a");
        var b = await rig.EnqueueAsync("b");
        var attemptCount = rig.Engine.SwitchHistory.Count;

        rig.CancelAtCostCommit(b);

        Assert.Equal(TicketState.Cancelled, b.State);
        Assert.Equal(TicketState.Queued, a.State);
        Assert.Equal(attemptCount, rig.Engine.SwitchHistory.Count);
        await rig.AssertDispatchAsync(a);
    }

    [Fact(Timeout = 20_000)]
    public async Task ASameModelSlotPublicationInvalidatesThePickWithoutMintingATimingAttempt()
    {
        await using var rig = new SwitchRig(aLoadMs: 80, bLoadMs: 20);
        await rig.TrainAsync();
        await rig.LoadProfileAsync("a");
        rig.Clock.Set(TimeSpan.FromMinutes(1));
        var b = await rig.EnqueueAsync("b");
        var c = await rig.EnqueueAsync("c");
        var before = rig.Engine.Capability;
        var proof = Assert.IsType<SwitchServingProof>(before.SwitchProof);
        var attempts = rig.Engine.SwitchHistory.Count;

        await rig.ChangeSlotsBeforeCommitAsync(3);

        var after = rig.Engine.Capability;
        Assert.NotEqual(before.Version, after.Version);
        Assert.Equal(3, after.Serving.TotalSlots);
        Assert.Same(proof, after.SwitchProof);
        Assert.Equal(attempts, rig.Engine.SwitchHistory.Count);
        Assert.Equal(TicketState.Queued, b.State);
        Assert.Equal(TicketState.Queued, c.State);
    }

    [Theory(Timeout = 20_000)]
    [InlineData((int)HttpStatusCode.NotModified, "AlreadyStopped")]
    [InlineData((int)HttpStatusCode.NotFound, "Absent")]
    [InlineData((int)HttpStatusCode.InternalServerError, "Failed")]
    public async Task IdempotentOrFailedStopRetainsEvidenceWithoutTrainingShutdown(int status, string expected)
    {
        await using var rig = new SwitchRig(aLoadMs: 20, bLoadMs: 80);
        await rig.LoadAsync("c");
        rig.StopStatus = (HttpStatusCode)status;

        var answer = await rig.Engine.WaitForModelAsync("a", TimeSpan.FromSeconds(5), CancellationToken.None);

        var record = rig.Engine.SwitchHistory[^1].Terminal;
        Assert.Null(record.ShutdownSource);
        Assert.Equal(expected, Assert.Single(record.NativeStops).Stop.ToString());
        if (expected == "Failed")
        {
            Assert.False(answer.IsReady);
            Assert.Equal(SwitchAttemptOutcome.Failed, record.Outcome);
            Assert.Null(record.GetStage(SwitchStage.Launch));
            Assert.Equal(SwitchStageOutcome.Failed,
                Assert.IsType<SwitchStageObservation>(record.GetStage(SwitchStage.Shutdown)).Outcome);
        }
        else
        {
            Assert.True(answer.IsReady);
            Assert.Equal(SwitchAttemptOutcome.Ready, record.Outcome);
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task RepeatedIdempotentStopsLeaveTheTransitionEstimateUnknown()
    {
        await using var rig = new SwitchRig(aLoadMs: 80, bLoadMs: 20);
        rig.StopStatus = HttpStatusCode.NotModified;
        await rig.TrainAsync();

        var launches = rig.Engine.SwitchHistory.Where(item => item.Terminal.Target.CanonicalModel == "a").ToArray();
        Assert.Equal(3, launches.Length);
        Assert.All(launches, item =>
        {
            Assert.NotNull(item.Terminal.NativeLaunch);
            Assert.Equal(SwitchAttemptOutcome.Ready, item.Terminal.Outcome);
            Assert.Equal(NativeStopDisposition.AlreadyStopped, Assert.Single(item.Terminal.NativeStops).Stop);
            Assert.Null(item.Terminal.ShutdownSource);
        });
        Assert.Null(rig.Estimate("a"));
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrMismatchedPeerProfileCannotSupplyAShutdownSample(bool missing)
    {
        await using var rig = new SwitchRig(aLoadMs: 20, bLoadMs: 80);
        await rig.LoadAsync("c");
        rig.MutatePeerProfile = missing ? static _ => null : static _ => "other";

        await rig.LoadAsync("a");

        var record = rig.Engine.SwitchHistory[^1].Terminal;
        Assert.Equal(NativeStopDisposition.Stopped, Assert.Single(record.NativeStops).Stop);
        Assert.Null(record.ShutdownSource);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultiplePeersOrFailedRemovalCannotSupplyAShutdownSample(bool multiplePeers)
    {
        await using var rig = new SwitchRig(aLoadMs: 20, bLoadMs: 80);
        await rig.LoadAsync("c");
        rig.MultiplePeers = multiplePeers;
        if (!multiplePeers) rig.RemovalStatus = HttpStatusCode.InternalServerError;

        var answer = await rig.Engine.WaitForModelAsync("a", TimeSpan.FromSeconds(5), CancellationToken.None);

        var record = rig.Engine.SwitchHistory[^1].Terminal;
        Assert.Null(record.ShutdownSource);
        Assert.Equal(multiplePeers, answer.IsReady);
        if (multiplePeers)
            Assert.Equal(2, record.NativeStops.Length);
        else
            Assert.Equal(NativeRemovalDisposition.Failed, Assert.Single(record.NativeStops).Removal);
    }

    [Fact(Timeout = 20_000)]
    public async Task FirstSendAttemptIsRecordedBeforeTheHttpHandlerThrows()
    {
        await using var rig = new SwitchRig(aLoadMs: 80, bLoadMs: 20);
        await rig.LoadAsync("c");
        var ticket = await rig.EnqueueAsync("a");

        await rig.AssertSendAttemptPrecedesHandlerFailureAsync(ticket);
    }

    [Fact(Timeout = 20_000)]
    public async Task OnlyTheMatchingCurrentProofAttachesTheFirstSendAttemptOnce()
    {
        await using var rig = new SwitchRig(aLoadMs: 20, bLoadMs: 80);
        await rig.LoadAsync("c");
        var displaced = Assert.IsType<SwitchServingProof>(rig.Engine.Capability.SwitchProof);
        await rig.LoadAsync("a");

        rig.Engine.ObserveFirstForward(displaced, "c", CancellationToken.None);
        var current = Assert.IsType<SwitchServingProof>(rig.Engine.Capability.SwitchProof);
        rig.Engine.ObserveFirstForward(current, "c", CancellationToken.None);
        rig.Engine.ObserveFirstForward(current, "a", new CancellationToken(canceled: true));
        Assert.All(rig.Engine.SwitchHistory, item => Assert.Null(item.FirstForwardedAt));

        var first = rig.Clock.GetTimestamp();
        rig.Engine.ObserveFirstForward(current, "a", CancellationToken.None);
        rig.Clock.Advance(TimeSpan.FromTicks(1));
        rig.Engine.ObserveFirstForward(current, "a", CancellationToken.None);

        var history = rig.Engine.SwitchHistory;
        Assert.Null(Assert.Single(history, item => item.Terminal.Id == displaced.Attempt).FirstForwardedAt);
        Assert.Equal(first, Assert.Single(history, item => item.Terminal.Id == current.Attempt).FirstForwardedAt);
    }

    private sealed class SwitchRig : IAsyncDisposable
    {
        private const string ImageId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
        private const string Digest = "fixture/inference@sha256:2222222222222222222222222222222222222222222222222222222222222222";
        private readonly CompletionEntryHandler _http = new() { LoadedModels = [], NCtx = 1_024 };
        private readonly FakeDockerDaemonHandler _daemon = new();
        private readonly DockerLifecycleProvider _provider;
        private readonly RouterCore _router;
        private readonly Dictionary<string, int> _activationMs;
        private readonly List<(string Model, string Profile)> _launches = [];
        private (string Id, string Model, string Profile)? _container;
        private int _created;

        internal TestClock Clock { get; } = new();
        internal EngineRuntime Engine => _router.Engines["fixture"];
        internal HttpStatusCode StopStatus { get; set; } = HttpStatusCode.NoContent;
        internal HttpStatusCode RemovalStatus { get; set; } = HttpStatusCode.NoContent;
        internal bool MultiplePeers { get; set; }
        internal bool RefusePreflight { get; set; }
        internal Func<string, string?>? MutatePeerProfile { get; set; }

        internal SwitchRig(int aLoadMs, int bLoadMs, bool servingLadder = true)
        {
            _activationMs = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["a"] = aLoadMs, ["b"] = bLoadMs, ["c"] = 10,
            };
            _daemon.Responder = Respond;
            var docker = new DockerLifecycleConfig
            {
                Image = "fixture/inference:local",
                ImageIdentity = new DockerImageIdentity(ImageId, Digest, "linux/amd64"),
                RouterId = "fixture",
                WaitHealth = false,
            };
            var engine = new EngineDefinition
            {
                Name = "fixture",
                Endpoint = new Uri("http://127.0.0.1:18031/v1"),
                Lifecycle = EngineLifecycleKind.Docker,
                Docker = docker,
                ResponseTimeoutMs = 0,
                Catalog = new Dictionary<string, EngineModelDefinition>(StringComparer.Ordinal)
                {
                    ["a"] = MeasuredModel("a", servingLadder),
                    ["b"] = MeasuredModel("b", servingLadder),
                    ["c"] = MeasuredModel("c", servingLadder),
                },
            };
            var config = new RouterConfig
            {
                Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal) { ["fixture"] = engine },
                HttpHandlerFactory = _ => _http,
            };
            _provider = new DockerLifecycleProvider(docker, engine.Name, _daemon);
            _router = new RouterCore(config, scheduler: null, events: null, clock: Clock,
                                     lifecycleFactory: _ => _provider);
        }

        private static EngineModelDefinition MeasuredModel(string name, bool servingLadder) => new()
        {
            Name = name,
            Profile = "p",
            ServingProfiles = servingLadder ? [new ServingProfile { Name = "p", MaxContext = 1_024 }] : [],
        };

        internal async Task LoadAsync(string model)
        {
            var result = await Engine.WaitForModelAsync(model, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.True(result.IsReady, result.Detail);
        }

        internal async Task LoadProfileAsync(string model)
        {
            var profile = Assert.IsType<ServingProfile>(Engine.GetProfile(model, "p"));
            var result = await Engine.WaitForProfileAsync(model, profile, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.True(result.IsReady, result.Detail);
        }

        internal async Task TrainAsync(int bSamples = 3, bool proveIncumbentWindow = false)
        {
            await LoadAsync("c");
            for (var i = 0; i < 3; i++)
            {
                await LoadAsync("a");
                if (i < bSamples)
                {
                    await LoadAsync("c");
                    await LoadAsync("b");
                }
                if (i == 2 && proveIncumbentWindow)
                    await LoadProfileAsync("c");
                else
                    await LoadAsync("c");
            }
        }

        internal ValueTask<RouterTicket> EnqueueAsync(string model, long? requiredContext = null) =>
            _router.EnqueueAsync(model, new InferenceRequest
            {
                Model = model,
                Body = """{"messages":[{"role":"user","content":"dispatch"}]}"""u8.ToArray(),
                Options = new RouterRequestOptions { RequiredContext = requiredContext },
            });

        internal TimeSpan? Estimate(string target)
        {
            var incumbent = Engine.ReadVerifiedSwitchIncumbent(Engine.Capability);
            return Engine.EstimateVerifiedTransition(Assert.IsType<SwitchSampleKey>(incumbent),
                                                    target, profile: null, now: Clock.GetTimestamp());
        }

        internal async Task AssertModelSwitchFloorAsync(RouterTicket ticket, long launchWindow, bool forwards)
        {
            var count = _launches.Count;
            var changed = false;
            _router.BeforeCostCommitForTests = () =>
            {
                _http.NCtx = launchWindow;
                changed = true;
            };
            try
            {
                Assert.True(_router.TryDispatchOnce());
            }
            finally
            {
                _router.BeforeCostCommitForTests = null;
            }

            Assert.True(changed);
            Assert.Equal(ticket.Id, Assert.Single(_router.Events.Replay(0), evt => evt.Kind == "request.selected").TicketId);
            if (forwards)
            {
                await ticket.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(TicketState.Completed, ticket.State);
            }
            else
            {
                await Assert.ThrowsAsync<RouterRequestException>(
                    () => ticket.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal("switch_failed", ticket.ErrorCode);
                Assert.Contains("window_too_small", ticket.Error);
            }

            Assert.Equal((ticket.Model, "p"), Assert.Single(_launches.Skip(count)));
            Assert.Equal(forwards ? 1 : 0, _http.CompletionCalls);
        }

        internal async Task AssertDispatchAsync(RouterTicket expected, Func<Task>? beforeCommit = null)
        {
            var count = _launches.Count;
            if (beforeCommit is null)
                Assert.True(_router.TryDispatchOnce());
            else
                await DispatchWithCommitPausedAsync(beforeCommit);
            var selection = Assert.Single(_router.Events.Replay(0), evt => evt.Kind == "request.selected");
            Assert.Equal(expected.Id, selection.TicketId);
            await expected.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal((expected.Model, "p"), Assert.Single(_launches.Skip(count)));
            Assert.Equal(TicketState.Completed, expected.State);
            var attempt = Engine.SwitchHistory[^1];
            Assert.Equal(expected.Model, attempt.Terminal.Target.CanonicalModel);
            Assert.NotNull(attempt.Terminal.GetStage(SwitchStage.Queue));
            Assert.NotNull(attempt.FirstForwardedAt);
        }

        internal async Task AssertSendAttemptPrecedesHandlerFailureAsync(RouterTicket ticket)
        {
            long? atHandlerEntry = null;
            _http.BeforeCompletion = () =>
            {
                atHandlerEntry = Engine.SwitchHistory[^1].FirstForwardedAt;
                throw new HttpRequestException("fake failure before forwarding");
            };

            Assert.True(_router.TryDispatchOnce());
            await Assert.ThrowsAsync<RouterRequestException>(
                () => ticket.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal("engine_unreachable", ticket.ErrorCode);
            Assert.Equal(0, _http.CompletionCalls);
            Assert.NotNull(atHandlerEntry);
            Assert.Equal(atHandlerEntry, Engine.SwitchHistory[^1].FirstForwardedAt);
        }

        internal Task ReplaceIncumbentBeforeCommitAsync(string newIncumbent) =>
            InterruptCostCommitAsync(async () =>
            {
                var previous = Engine.Capability;
                await LoadAsync(newIncumbent);
                Assert.NotEqual(previous.Version, Engine.Capability.Version);
            });

        internal Task ChangeSlotsBeforeCommitAsync(int slots) => InterruptCostCommitAsync(async () =>
        {
            using var stop = new CancellationTokenSource();
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe(RouterEvent evt)
            {
                if (evt.Kind == "engine.health" && evt.Engine == Engine.Name) observed.TrySetResult();
            }

            _router.Events.Subscribe(Observe);
            try
            {
                _http.TotalSlots = slots;
                Engine.StartHealthLoop(stop.Token);
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                _router.Events.Unsubscribe(Observe);
                stop.Cancel();
            }
        });

        internal void CancelAtCostCommit(RouterTicket ticket)
        {
            var count = _launches.Count;
            _router.BeforeCostCommitForTests = () => Assert.True(_router.TryCancel(ticket.Id));
            try
            {
                Assert.True(_router.TryDispatchOnce());
            }
            finally
            {
                _router.BeforeCostCommitForTests = null;
            }
            Assert.DoesNotContain(_router.Events.Replay(0), evt => evt.Kind == "request.selected");
            Assert.Equal(count, _launches.Count);
            Assert.Equal(0, _http.CompletionCalls);
        }

        private async Task InterruptCostCommitAsync(Func<Task> beforeCommit)
        {
            await DispatchWithCommitPausedAsync(beforeCommit);
            Assert.DoesNotContain(_router.Events.Replay(0), evt => evt.Kind == "request.selected");
        }

        private async Task DispatchWithCommitPausedAsync(Func<Task> beforeCommit)
        {
            var compared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _router.BeforeCostCommitForTests = () => Pause(compared, release.Task);
            var dispatch = Task.Run(_router.TryDispatchOnce);
            try
            {
                await compared.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await beforeCommit();
            }
            finally
            {
                _router.BeforeCostCommitForTests = null;
                release.TrySetResult();
            }

            Assert.True(await dispatch.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        private static void Pause(TaskCompletionSource entered, Task release)
        {
            entered.TrySetResult();
            release.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }

        private (HttpStatusCode Status, string Body) Respond(string path)
        {
            if (path.EndsWith("/version", StringComparison.Ordinal))
                return (HttpStatusCode.OK, """{"ApiVersion":"1.44"}""");
            if (path.Contains("/images/", StringComparison.Ordinal))
            {
                if (RefusePreflight) return (HttpStatusCode.NotFound, "");
                return (HttpStatusCode.OK,
                    $$"""{"Id":"{{ImageId}}","RepoDigests":["{{Digest}}"],"Os":"linux","Architecture":"amd64"}""");
            }
            if (path.Contains("/containers/json", StringComparison.Ordinal))
            {
                if (_container is not { } peer) return (HttpStatusCode.OK, "[]");
                var profile = MutatePeerProfile is { } mutate ? mutate(peer.Profile) : peer.Profile;
                var profileLabel = profile is null ? "" : $",\"arboreal.profile\":\"{profile}\"";
                var labels = $$"""{"arboreal.router":"fixture","arboreal.engine":"fixture","arboreal.model":"{{peer.Model}}"{{profileLabel}}"""
                    + "}";
                var entry = $$"""{"Id":"{{peer.Id}}","ImageID":"{{ImageId}}","Labels":{{labels}}""" + "}";
                var extra = MultiplePeers
                    ? """,{"Id":"another-container","Labels":{"arboreal.router":"fixture","arboreal.engine":"fixture"}}"""
                    : "";
                return (HttpStatusCode.OK, "[" + entry + extra + "]");
            }
            if (path.Contains("/stop?", StringComparison.Ordinal))
            {
                Clock.Advance(TimeSpan.FromMilliseconds(5));
                return (StopStatus, "");
            }
            if (path.Contains("?force=true", StringComparison.Ordinal))
            {
                Clock.Advance(TimeSpan.FromMilliseconds(2));
                if (RemovalStatus == HttpStatusCode.NoContent) _container = null;
                return (RemovalStatus, "");
            }
            if (path.EndsWith("/containers/create", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(_daemon.Bodies[^1]);
                var labels = body.RootElement.GetProperty("Labels");
                var id = $"container-{++_created}";
                _container = (id, labels.GetProperty("arboreal.model").GetString()!,
                                  labels.GetProperty("arboreal.profile").GetString()!);
                return (HttpStatusCode.Created, $$"""{"Id":"{{id}}"}""");
            }
            if (path.EndsWith("/start", StringComparison.Ordinal) && _container is { } current)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(_activationMs[current.Model]));
                _http.LoadedModels = [current.Model];
                _launches.Add((current.Model, current.Profile));
                return (HttpStatusCode.NoContent, "");
            }
            return (HttpStatusCode.InternalServerError, "");
        }

        public async ValueTask DisposeAsync()
        {
            await _router.DisposeAsync();
            _provider.Dispose();
            _daemon.Dispose();
            _http.Dispose();
        }
    }

    private sealed class CompletionEntryHandler : FakeEngineHandler
    {
        internal Action? BeforeCompletion { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) BeforeCompletion?.Invoke();
            return base.SendAsync(request, ct);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
        internal void Set(TimeSpan elapsed) => Interlocked.Exchange(ref _timestamp, elapsed.Ticks);
    }
}
