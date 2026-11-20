using System.Net;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class VerifiedInstanceTransportTests
{
    private static readonly byte[] VerifiedBody = """{"model":"model.canonical","prompt":"two  spaces"}"""u8.ToArray();
    private static readonly Uri VerifiedEndpoint = new("http://127.0.0.1:8123/prefix/v1?mode=exact");

    [Fact(Timeout = 20_000)]
    public async Task OptInIdleReleasesLogicalSlotWhileVerifiedIssuerRetirementIsHeld()
    {
        const string prefix = "data: valid\n\n";
        const string prompt = "verified relay prefix";
        var requestBody = "{\"model\":\"alias\",\"messages\":[{\"role\":\"user\",\"content\":\"verified relay prefix\"}],\"stream\":true}"u8.ToArray();
        await using var rig = new DispatchRig(streamIdleTimeoutMs: 300_000);
        using var peer = await LoopbackPair.OpenAsync(rig.Token);
        using var retiring = new HeldCheckout();
        rig.Issuer.Channels.Enqueue(peer.Client.GetStream());
        rig.Issuer.BeforeRetire = _ => retiring.HoldAsync(rig.Token);
        rig.Publish(generation: 1);
        var idle = new ReadinessDeadlineTests.ManualClock();
        rig.Router.StreamIdleClockForTests = idle;
        var transportSettled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Router.AfterOptInTransportTaskForTests = id => transportSettled.TrySetResult(id);
        Assert.True(await rig.Engine.ProbeHealthOnceAsync(rig.Token));
        var ticket = await rig.Router.EnqueueHttpAsync("verified-stream", new InferenceRequest
        {
            Model = "ALIAS",
            Stream = true,
            Body = requestBody,
            Options = new RouterRequestOptions { RequiredContext = 32_768 },
        }, rig.Token);
        var released = rig.WaitForRelease();
        rig.Start();
        try
        {
            var remote = peer.Server.GetStream();
            var sent = ModelRewriter.Rewrite(requestBody, "model.canonical");
            Assert.Equal(sent, (await ReadAsync(remote, rig.Token, sent.Length)).Body);
            await remote.WriteAsync("HTTP/1.1 200 Fixture\r\nContent-Length: 1000\r\nConnection: close\r\n\r\ndata: valid\n\n"u8.ToArray(),
                rig.Token);
            var channel = Assert.IsAssignableFrom<System.Threading.Channels.Channel<ReadOnlyMemory<byte>>>(
                await ticket.StreamReady.WaitAsync(rig.Token));
            var accepted = await channel.Reader.ReadAsync(rig.Token);
            Assert.Equal(prefix, System.Text.Encoding.UTF8.GetString(accepted.Span));
            while (idle.ActiveTimers == 0)
            {
                rig.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            idle.AdvanceTo(300_001);
            var error = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(rig.Token));
            Assert.Equal("engine_timeout", error.Code);
            await retiring.Reached.WaitAsync(rig.Token);
            while (rig.Router.GetStats().Engines.Single().Inflight != 0)
            {
                rig.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            Assert.Equal(1, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Admission.Releases);
            Assert.False(transportSettled.Task.IsCompleted);
            Assert.Equal(0, rig.Engine.MatchedPrefixChars([prompt]));
            Assert.False(await channel.Reader.WaitToReadAsync(rig.Token));
            retiring.Release();
            await released.WaitAsync(rig.Token);
            Assert.Equal(ticket.Id, await transportSettled.Task.WaitAsync(rig.Token));
            Assert.Equal(0, rig.Admission.PhysicalUses);
            Assert.Equal(1, rig.Admission.Releases);
            Assert.True(rig.Engine.MatchedPrefixChars([prompt]) > 0);
            Assert.Equal(1, rig.Terminals(ticket));
        }
        finally { retiring.Release(); }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(null, false, "resident")]
    [InlineData(null, true, "wide")]
    [InlineData("wide", true, "wide")]
    public async Task PreparedVerifiedTicketSendsOnlyTheFrozenRequest(string? selectedTier, bool switches, string residentTier)
    {
        await using var rig = new DispatchRig(selectedTier, switches);
        using var peer = await LoopbackPair.OpenAsync(rig.Token);
        rig.Issuer.Channels.Enqueue(peer.Client.GetStream());
        rig.Publish(generation: 1, tier: residentTier);
        var response = RespondAsync(peer.Server.GetStream(), HttpStatusCode.OK, location: null, rig.Token, VerifiedBody.Length);
        var released = rig.WaitForRelease();
        var ticket = await rig.EnqueueAsync();
        rig.Start();

        var request = await response.WaitAsync(rig.Token);
        Assert.Contains("POST /prefix/v1/completions?mode=exact HTTP/1.1\r\n", request.Headers, StringComparison.Ordinal);
        Assert.Contains("Host: 127.0.0.1:8123\r\n", request.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Type: application/json\r\n", request.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Authorization: Bearer fixture-key\r\n", request.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VerifiedBody, request.Body);
        await ticket.CompletionTask.WaitAsync(rig.Token);
        await released.WaitAsync(rig.Token);
        Assert.Equal("ALIAS", rig.ObservedOriginalModel);
        Assert.Equal(ticket.Id, Assert.Single(rig.Selected).TicketId);
        Assert.Equal(1, rig.Preparations);
        Assert.Equal(1, rig.Issuer.Checkouts);
        Assert.Equal(1, rig.Issuer.Authorizations);
        Assert.Equal(1, rig.Issuer.Releases);
        Assert.Equal(0, rig.Admission.PhysicalUses);
        Assert.Equal(0, rig.OrdinarySends);
        Assert.Empty(rig.Daemon.Requests);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(null, false, 0L, 2)]
    [InlineData(null, false, 32767L, 2)]
    [InlineData(null, true, 0L, 2)]
    [InlineData(null, true, 32768L, 0)]
    [InlineData("wide", true, 32767L, 2)]
    [InlineData("wide", true, 32768L, 0)]
    public async Task PreparedVerifiedCapacityCannotComeFromPublicGauges(
        string? tier, bool switches, long measuredWindow, int slots)
    {
        await using var rig = new DispatchRig(tier, switches);
        rig.Publish(generation: 1, tier: switches ? "wide" : "resident", measuredWindow, slots);
        var ticket = await rig.EnqueueAsync();

        Assert.False(rig.Router.TryDispatchOnce());
        Assert.Equal(TicketState.Queued, ticket.State);
        Assert.Empty(rig.Selected);
        Assert.Equal(0, rig.Preparations);
        Assert.Equal(0, rig.Issuer.Checkouts);
        Assert.Equal(0, rig.OrdinarySends);
    }

    [Fact(Timeout = 20_000)]
    public async Task PreparedVerifiedPublicShrinkVetoesAnOtherwiseFittingResident()
    {
        await using var rig = new DispatchRig();
        rig.Publish(generation: 1);
        rig.Discovery.NCtx = 16_384;
        Assert.True(await rig.Engine.ProbeHealthOnceAsync(rig.Token));
        var ticket = await rig.EnqueueAsync();

        Assert.False(rig.Router.TryDispatchOnce());
        Assert.Equal(TicketState.Queued, ticket.State);
        Assert.Empty(rig.Selected);
        Assert.Equal(0, rig.Issuer.Checkouts);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("body")]
    [InlineData("auth")]
    [InlineData("target")]
    [InlineData("raw-model")]
    [InlineData("unexpected-credential")]
    [InlineData("content-encoding")]
    [InlineData("endpoint")]
    public async Task PreparedVerifiedMismatchSettlesWithoutCheckoutOrQueueTimer(string change)
    {
        await using var rig = new DispatchRig(runtimeKey: change == "auth" ? "other-key" : "fixture-key",
            runtimeEndpoint: change == "endpoint" ? new Uri("http://127.0.0.1:8123/other/v1?mode=exact") : null);
        using var prepared = new HeldCheckout();
        var source = change == "raw-model"
            ? """{"model":"other-model","prompt":"two  spaces"}"""u8.ToArray()
            : """{"model":"alias","prompt":"two  spaces"}"""u8.ToArray();
        rig.Admission.BeforeVerifiedOfferForTests = offer =>
        {
            if (change == "unexpected-credential")
                offer.Headers.TryAddWithoutValidation("X-Api-Key", "unexpected-fixture-credential");
            if (change == "content-encoding")
                offer.Content!.Headers.ContentEncoding.Add("gzip");
        };
        rig.AfterPrepare = async (_, _) => await prepared.HoldAsync(rig.Token);
        rig.Publish(generation: 1, differentPlan: change == "target");
        var ticket = await rig.EnqueueAsync(source);
        rig.Start();
        try
        {
            if (change is not ("target" or "raw-model"))
            {
                await prepared.Reached.WaitAsync(rig.Token);
                if (change == "body") source[^4] = (byte)'X';
                prepared.Release();
            }

            var failure = await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(rig.Token));
            Assert.Equal("engine_unreachable", failure.Code);
            Assert.Equal(0, rig.Issuer.Checkouts);
            Assert.Equal(0, rig.OrdinarySends);
            Assert.Empty(rig.Daemon.Requests);
            Assert.Equal(1, rig.Terminals(ticket));
            Assert.Equal(change is "target" or "raw-model" ? 0 : 1, rig.Selected.Length);
        }
        finally
        {
            prepared.Release();
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task PreparedVerifiedCloseBeforeFirstAuthorizationNeedsFreshGeneration()
    {
        await using var rig = new DispatchRig();
        using var original = await LoopbackPair.OpenAsync(rig.Token);
        using var replacement = await LoopbackPair.OpenAsync(rig.Token);
        using var preWrite = new HeldCheckout();
        using var caller = new CancellationTokenSource();
        rig.Issuer.Channels.Enqueue(original.Client.GetStream());
        rig.Issuer.Channels.Enqueue(replacement.Client.GetStream());
        rig.Issuer.BeforeAuthorize = () => preWrite.HoldAsync(rig.Token).GetAwaiter().GetResult();
        var old = rig.Publish(generation: 1);
        var released = rig.WaitForRelease();
        var first = await rig.EnqueueAsync(callerToken: caller.Token);
        rig.Start();
        try
        {
            await preWrite.Reached.WaitAsync(rig.Token);
            Assert.True(rig.Issuer.Close(old.Route));
            Assert.True(rig.Admission.CloseVerified(old));
            Assert.False(rig.Issuer.Open(old.Route));
            Assert.False(rig.Admission.PublishVerified(old));
            Assert.False(caller.IsCancellationRequested);
            preWrite.Release();

            await Assert.ThrowsAsync<RouterRequestException>(() => first.CompletionTask.WaitAsync(rig.Token));
            await released.WaitAsync(rig.Token);
            Assert.Equal(0, rig.Issuer.Authorizations);
            Assert.Equal(0, await original.Server.GetStream().ReadAsync(new byte[1], rig.Token));
            Assert.Equal(0, replacement.Server.Available);
            Assert.Equal(0, rig.OrdinarySends);

            rig.Issuer.BeforeAuthorize = null;
            var secondRelease = rig.WaitForRelease(expected: 2);
            var successor = await rig.EnqueueAsync();
            var received = RespondAsync(replacement.Server.GetStream(), HttpStatusCode.OK, null, rig.Token, VerifiedBody.Length);
            rig.Publish(generation: 2, incarnation: "replacement");
            Assert.Equal(VerifiedBody, (await received.WaitAsync(rig.Token)).Body);
            await successor.CompletionTask.WaitAsync(rig.Token);
            await secondRelease.WaitAsync(rig.Token);
            Assert.Equal(1, rig.Issuer.Authorizations);
        }
        finally
        {
            preWrite.Release();
        }
    }

    [Theory(Timeout = 25_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedVerifiedPreHeaderFailureWaitsForRetirementAndPreservesPrimary(bool cleanupFails)
    {
        await using var rig = new DispatchRig();
        using var original = await LoopbackPair.OpenAsync(rig.Token);
        using var successorPeer = await LoopbackPair.OpenAsync(rig.Token);
        using var retiring = new HeldCheckout();
        rig.Issuer.Channels.Enqueue(original.Client.GetStream());
        rig.Issuer.Channels.Enqueue(successorPeer.Client.GetStream());
        rig.Issuer.BeforeRetire = async ordinal =>
        {
            if (ordinal != 1) return;
            await retiring.HoldAsync(rig.Token);
            if (cleanupFails) throw new InvalidOperationException("fixture-private-permit-failure");
        };
        rig.Issuer.AfterClose = ordinal =>
        {
            if (ordinal == 1 && cleanupFails) throw new InvalidOperationException("fixture-private-close-failure");
        };
        rig.Publish(generation: 1);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.PreparationCleanup = () => { disposed.TrySetResult(); return ValueTask.CompletedTask; };
        var cleanup = rig.WaitForCleanupFault();
        var first = await rig.EnqueueAsync();
        rig.Start();

        try
        {
            var received = await ReadAsync(original.Server.GetStream(), rig.Token, VerifiedBody.Length);
            Assert.Equal(VerifiedBody, received.Body);
            original.Server.Dispose();
            await retiring.Reached.WaitAsync(rig.Token);
            var failure = await Assert.ThrowsAsync<RouterRequestException>(() => first.CompletionTask.WaitAsync(rig.Token));
            Assert.Equal("engine_unreachable", failure.Code);
            Assert.Equal("The verified request could not be delivered.", failure.Message);
            Assert.Equal(1, rig.Terminals(first));
            await disposed.Task.WaitAsync(rig.Token);
            Assert.Equal(1, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Admission.Releases);
            Assert.Equal(1, rig.Issuer.PhysicalUses);
            var successor = await rig.EnqueueAsync();
            var secondRelease = cleanupFails ? Task.CompletedTask : rig.WaitForRelease(expected: 2);
            Assert.Equal(1, rig.Issuer.Checkouts);
            Assert.Equal(0, successorPeer.Server.Available);
            Assert.False(successor.CompletionTask.IsCompleted);

            retiring.Release();
            if (cleanupFails)
            {
                var fault = await cleanup.WaitAsync(rig.Token);
                Assert.Contains("stream close", fault.Reason, StringComparison.Ordinal);
                Assert.Contains("issuer permit retirement", fault.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("fixture-private", fault.Reason, StringComparison.Ordinal);
                Assert.Single(rig.Router.Events.Replay(0), row => row.Kind == "request.cleanup_failed");
                Assert.Equal(1, rig.Terminals(first));
                Assert.Equal(1, rig.Admission.PhysicalUses);
                Assert.Equal(0, rig.Admission.Releases);
                Assert.Equal(1, rig.Issuer.Checkouts);
                Assert.Equal(0, successorPeer.Server.Available);
            }
            else
            {
                var next = RespondAsync(successorPeer.Server.GetStream(), HttpStatusCode.OK, null, rig.Token, VerifiedBody.Length);
                Assert.Equal(VerifiedBody, (await next.WaitAsync(rig.Token)).Body);
                Assert.Equal(1, rig.Admission.Releases);
                await successor.CompletionTask.WaitAsync(rig.Token);
                await secondRelease.WaitAsync(rig.Token);
                Assert.Equal(2, rig.Issuer.Checkouts);
                Assert.DoesNotContain(rig.Router.Events.Replay(0), row => row.Kind == "request.cleanup_failed");
            }
        }
        finally
        {
            retiring.Release();
        }
    }

    [Theory(Timeout = 25_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedVerifiedResponseDisposalFaultUsesIssuerAndPreparationEvidence(bool preparationFails)
    {
        await using var rig = new DispatchRig();
        using var original = await LoopbackPair.OpenAsync(rig.Token);
        using var successorPeer = await LoopbackPair.OpenAsync(rig.Token);
        using var retiring = new HeldCheckout();
        rig.Issuer.Channels.Enqueue(original.Client.GetStream());
        rig.Issuer.Channels.Enqueue(successorPeer.Client.GetStream());
        rig.Issuer.BeforeRetire = ordinal => ordinal == 1 ? retiring.HoldAsync(rig.Token) : Task.CompletedTask;
        var grantCloses = 0;
        rig.Issuer.AfterClose = _ => Interlocked.Increment(ref grantCloses);
        var consumed = 0;
        var responseDisposals = 0;
        rig.Admission.AfterVerifiedConsumeForTests = response =>
        {
            if (Interlocked.Increment(ref consumed) != 1) return;
            response.Content = new ResponseDisposalProbe(response.Content, () =>
            {
                Interlocked.Increment(ref responseDisposals);
                throw new InvalidOperationException("fixture-private-response-failure");
            });
        };
        var preparationDisposals = 0;
        rig.PreparationCleanup = () =>
        {
            if (Interlocked.Increment(ref preparationDisposals) == 1 && preparationFails)
                throw new InvalidOperationException("fixture-private-preparation-failure");
            return ValueTask.CompletedTask;
        };
        rig.Publish(generation: 1);
        var cleanup = rig.WaitForCleanupFault();
        var first = await rig.EnqueueAsync();
        rig.Start();

        try
        {
            var peer = original.Server.GetStream();
            Assert.Equal(VerifiedBody, (await ReadAsync(peer, rig.Token, VerifiedBody.Length)).Body);
            await peer.WriteAsync("HTTP/1.1 200 Fixture\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray(), rig.Token);
            Assert.Equal("{}"u8.ToArray(), await first.CompletionTask.WaitAsync(rig.Token));
            await retiring.Reached.WaitAsync(rig.Token);
            Assert.Equal(1, Volatile.Read(ref responseDisposals));
            Assert.Equal(1, Volatile.Read(ref grantCloses));
            Assert.Equal(1, rig.Admission.PhysicalUses);
            Assert.Equal(0, rig.Admission.Releases);

            var successor = await rig.EnqueueAsync();
            Assert.Equal(1, rig.Issuer.Checkouts);
            Assert.Equal(0, successorPeer.Server.Available);
            Assert.False(successor.CompletionTask.IsCompleted);
            var allReleased = preparationFails ? Task.CompletedTask : rig.WaitForRelease(expected: 2);

            retiring.Release();
            var fault = await cleanup.WaitAsync(rig.Token);
            Assert.Equal(first.Id, fault.TicketId);
            Assert.Contains("response cleanup", fault.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-private", fault.Reason, StringComparison.Ordinal);
            Assert.Equal(1, rig.Issuer.Releases);
            Assert.Equal(1, Volatile.Read(ref preparationDisposals));
            if (preparationFails)
            {
                Assert.Contains("preparation cleanup", fault.Reason, StringComparison.Ordinal);
                Assert.Equal(1, rig.Admission.PhysicalUses);
                Assert.Equal(0, rig.Admission.Releases);
                Assert.Equal(1, rig.Issuer.Checkouts);
                Assert.Equal(0, successorPeer.Server.Available);
                Assert.False(successor.CompletionTask.IsCompleted);
            }
            else
            {
                Assert.Equal("Verified request cleanup failed: response cleanup.", fault.Reason);
                Assert.Equal(1, rig.Admission.Releases);
                var next = await RespondAsync(successorPeer.Server.GetStream(), HttpStatusCode.OK, null, rig.Token, VerifiedBody.Length);
                Assert.Equal(VerifiedBody, next.Body);
                await successor.CompletionTask.WaitAsync(rig.Token);
                await allReleased.WaitAsync(rig.Token);
                Assert.Equal(2, rig.Issuer.Checkouts);
                Assert.Equal(2, rig.Issuer.Releases);
                Assert.Equal(2, rig.Admission.Releases);
                Assert.Equal(0, rig.Admission.PhysicalUses);
                Assert.Equal(2, Volatile.Read(ref preparationDisposals));
                Assert.Equal(1, rig.Terminals(successor));
            }
            Assert.Equal(1, rig.Terminals(first));
            Assert.Single(rig.Router.Events.Replay(0), row => row.Kind == "request.cleanup_failed");
        }
        finally
        {
            retiring.Release();
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData("no-transfer", true)]
    [InlineData("retired", true)]
    [InlineData("unknown", false)]
    [InlineData("throw", false)]
    public async Task PreparedVerifiedCheckoutNeedsTypedEvidenceToRelease(string disposition, bool releases)
    {
        await using var rig = new DispatchRig();
        rig.Issuer.CheckoutFailure = disposition;
        rig.Publish(1);
        var released = rig.WaitForRelease();
        var cleanup = rig.WaitForCleanupFault();
        var ticket = await rig.EnqueueAsync();
        rig.Start();

        await Assert.ThrowsAsync<RouterRequestException>(() => ticket.CompletionTask.WaitAsync(rig.Token));
        if (releases) await released.WaitAsync(rig.Token);
        else await cleanup.WaitAsync(rig.Token);
        Assert.Equal(releases ? 0 : 1, rig.Admission.PhysicalUses);
        Assert.Equal(releases ? 1 : 0, rig.Admission.Releases);
        Assert.Equal(0, rig.Issuer.Authorizations);
        Assert.Equal(1, rig.Terminals(ticket));
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedVerifiedAvailabilityRearmsOnlyAFreshGeneration(bool staleRefusal)
    {
        await using var rig = new DispatchRig();
        using var peer = await LoopbackPair.OpenAsync(rig.Token);
        using var oldPreparation = new HeldCheckout();
        rig.Issuer.Channels.Enqueue(peer.Client.GetStream());
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Admission.AfterPreparationForTests = (claim, ready) =>
        {
            if (claim.Generation == 1 && !ready) retired.TrySetResult();
        };
        rig.BeforePrepare = async claim =>
        {
            if (claim.Generation != 1) return true;
            if (!staleRefusal) return false;
            await oldPreparation.HoldAsync(rig.Token);
            throw new VerifiedDispatchBindingException(VerifiedDispatchRefusal.Target);
        };
        var original = rig.Publish(generation: 1);
        var ticket = await rig.EnqueueAsync();
        rig.Start();
        try
        {
            if (staleRefusal)
                await oldPreparation.Reached.WaitAsync(rig.Token);
            else
                await retired.Task.WaitAsync(rig.Token);

            Assert.False(rig.Admission.PublishVerified(original));
            // A pass on the same availability generation cannot spend it a second time.
            if (!staleRefusal)
            {
                Assert.False(rig.Router.TryDispatchOnce());
                Assert.Equal(0, rig.Admission.PreparationCount);
            }
            Assert.Equal(1, rig.Preparations);
            Assert.Equal(TicketState.Queued, ticket.State);
            Assert.Equal(0, rig.Terminals(ticket));
            Assert.Equal(0, rig.Issuer.Checkouts);

            var response = RespondAsync(peer.Server.GetStream(), HttpStatusCode.OK, null, rig.Token, VerifiedBody.Length);
            rig.Publish(generation: 2);
            oldPreparation.Release();
            Assert.Equal(VerifiedBody, (await response.WaitAsync(rig.Token)).Body);
            await ticket.CompletionTask.WaitAsync(rig.Token);
            await retired.Task.WaitAsync(rig.Token);
            Assert.Equal(2, rig.Preparations);
            Assert.Equal(ticket.Id, Assert.Single(rig.Selected).TicketId);
            Assert.Equal(1, rig.Terminals(ticket));
            Assert.Equal(1, rig.Issuer.Checkouts);
        }
        finally
        {
            oldPreparation.Release();
        }
    }

    private sealed class ResponseDisposalProbe(HttpContent borrowed, Action onDispose) : ByteArrayContent([])
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            borrowed.Dispose();
            onDispose();
        }
    }

    private sealed class DispatchRig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(12));
        private readonly DockerLifecycleProvider _projection;
        private readonly VerifiedDispatchTargetResolver _resolver;
        private Task _dispatcher = Task.CompletedTask;
        private int _preparations;

        internal DispatchRig(string? tier = null, bool switches = false, string runtimeKey = "fixture-key",
            Uri? runtimeEndpoint = null, int streamIdleTimeoutMs = 0)
        {
            var parsed = VerifiedDispatchTargetTests.Load(endpoint: VerifiedEndpoint.AbsoluteUri);
            Daemon = new FakeDockerDaemonHandler();
            _projection = new DockerLifecycleProvider(parsed.Docker!, parsed.Name, Daemon);
            _resolver = new VerifiedDispatchTargetResolver(parsed, _projection);
            Discovery = new FakeEngineHandler { LoadedModels = ["model.canonical"], NCtx = 65_536, TotalSlots = 4 };
            // This runtime is inert and separate from the parsed, resource-bearing target configuration.
            var engine = new EngineDefinition
            {
                Name = parsed.Name, Endpoint = runtimeEndpoint ?? VerifiedEndpoint, Remote = true, Catalog = parsed.Catalog, ApiKey = runtimeKey,
                StreamIdleTimeoutMs = streamIdleTimeoutMs,
            };
            Admission = new ExactTicketAdmission(
                static (_, _) => throw new InvalidOperationException("A verified route requires its prepared sender."),
                static (_, _) => throw new InvalidOperationException("A verified route cannot use the ordinary sender."),
                PrepareAsync);
            Router = new RouterCore(new RouterConfig
            {
                Engines = new Dictionary<string, EngineDefinition>(StringComparer.Ordinal) { [engine.Name] = engine },
                HttpHandlerFactory = _ => Discovery,
            }, tier is null && !switches ? null : new BoundScheduler(engine.Name, tier, switches),
                events: null, clock: TimeProvider.System, admission: Admission);
            Credential = VerifiedDispatchCredential.Capture(new EngineDefinition
            {
                Name = engine.Name, Endpoint = VerifiedEndpoint, ApiKey = "fixture-key",
            }, "fixture-key", epoch: 1);
        }

        internal RouterCore Router { get; }
        internal ExactTicketAdmission Admission { get; }
        internal DispatchIssuer Issuer { get; } = new();
        internal EngineRuntime Engine => Router.Engines["fixture"];
        internal FakeEngineHandler Discovery { get; }
        internal FakeDockerDaemonHandler Daemon { get; }
        internal VerifiedDispatchCredential Credential { get; }
        internal CancellationToken Token => _deadline.Token;
        internal int Preparations => Volatile.Read(ref _preparations);
        internal int OrdinarySends => Discovery.CompletionCalls;
        internal string? ObservedOriginalModel { get; private set; }
        internal RouterEvent[] Selected => Router.Events.Replay(0).Where(row => row.Kind == "request.selected").ToArray();
        internal Func<ExactTicketAdmission.Claim, Task<bool>>? BeforePrepare { get; set; }
        internal Func<ExactTicketAdmission.Claim, VerifiedPreparedSender, Task>? AfterPrepare { get; set; }
        internal Func<ValueTask>? PreparationCleanup { get; set; }

        internal VerifiedDispatchResident Publish(long generation, string tier = "resident", long window = 32_768,
                                                   int slots = 1, string incarnation = "process", bool differentPlan = false)
        {
            var target = _resolver.Resolve("model.canonical", tier, null, differentPlan ? "other-config" : "captured", "route");
            var route = new VerifiedInstanceRoute("host", 1, target.Engine, target.PlanDigest,
                "container", incarnation, 1, generation);
            var resident = new VerifiedDispatchResident(target, route, 32_768, window, slots, VerifiedEndpoint, Credential, Issuer);
            Assert.True(Issuer.Open(route));
            Assert.True(Admission.PublishVerified(resident));
            return resident;
        }

        internal async ValueTask<RouterTicket> EnqueueAsync(byte[]? body = null, CancellationToken callerToken = default)
        {
            Assert.True(await Engine.ProbeHealthOnceAsync(Token));
            return await Router.EnqueueAsync(Guid.NewGuid().ToString(), new InferenceRequest
            {
                Model = "ALIAS",
                Body = body ?? """{ "model":"alias", "prompt": "two  spaces" }"""u8.ToArray(),
                Options = new RouterRequestOptions { RequiredContext = 32_768 },
            }, callerToken);
        }

        internal void Start() => _dispatcher = Task.Run(Router.DispatchLoop, Token);

        internal int Terminals(RouterTicket ticket) => Router.Events.Replay(0).Count(row =>
            row.TicketId == ticket.Id && row.Kind is "request.failed" or "request.completed" or "request.cancelled");

        internal Task WaitForRelease(int expected = 1)
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Admission.AfterReleaseForTests = () =>
            {
                if (Admission.Releases >= expected) settled.TrySetResult();
            };
            return settled.Task;
        }

        internal Task<RouterEvent> WaitForCleanupFault()
        {
            var fault = new TaskCompletionSource<RouterEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            Router.Events.Subscribe(row => { if (row.Kind == "request.cleanup_failed") fault.TrySetResult(row); });
            return fault.Task;
        }

        private async ValueTask<VerifiedPreparedSender?> PrepareAsync(ExactTicketAdmission.Claim claim, CancellationToken ct)
        {
            Interlocked.Increment(ref _preparations);
            ObservedOriginalModel = claim.Request.OriginalModel;
            if (BeforePrepare is { } before && !await before(claim)) return null;
            ct.ThrowIfCancellationRequested();
            var sender = VerifiedPreparedSender.Create(claim, _resolver, "captured", "route", PreparationCleanup);
            try
            {
                if (AfterPrepare is { } after) await after(claim, sender);
                return sender;
            }
            catch
            {
                await sender.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Router.DisposeAsync();
                await _dispatcher.WaitAsync(Token);
            }
            finally
            {
                _projection.Dispose();
                Daemon.Dispose();
                Discovery.Dispose();
                _deadline.Dispose();
            }
        }
    }

    private sealed class BoundScheduler(string engine, string? tier, bool switches) : IRequestScheduler
    {
        public SchedulerDecision SelectNext(QueuedRequest request, SchedulerView view, RouterConfig config) =>
            new() { Engine = engine, SwitchRequired = switches, TargetProfile = tier };
    }

    private sealed class DispatchIssuer : IVerifiedDispatchIssuer
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, VerifiedInstanceGrant> _issued = [];
        private VerifiedInstanceRoute? _open;
        private long _generation;
        internal Queue<Stream> Channels { get; } = new();
        internal int Checkouts { get; private set; }
        internal int Authorizations { get; private set; }
        internal int PhysicalUses { get; private set; }
        internal int Releases { get; private set; }
        internal string? CheckoutFailure { get; set; }
        internal Action? BeforeAuthorize { get; set; }
        internal Func<int, Task>? BeforeRetire { get; set; }
        internal Action<int>? AfterClose { get; set; }

        internal bool Open(VerifiedInstanceRoute route)
        {
            lock (_gate)
            {
                if (route.RouteIncarnation <= _generation) return false;
                _generation = route.RouteIncarnation;
                _open = route;
                return true;
            }
        }

        internal bool Close(VerifiedInstanceRoute route)
        {
            lock (_gate)
            {
                if (_open != route) return false;
                _open = null;
                return true;
            }
        }

        public ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken ct) =>
            throw new InvalidOperationException("The verified fixture requires typed checkout evidence.");

        public ValueTask<VerifiedCheckout> CheckoutVerifiedAsync(VerifiedInstanceInvocation invocation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var ordinal = ++Checkouts;
                if (_open != invocation.Route || CheckoutFailure == "no-transfer")
                    return ValueTask.FromResult(new VerifiedCheckout(VerifiedCheckoutDisposition.NoTransfer));
                if (CheckoutFailure == "retired")
                    return ValueTask.FromResult(new VerifiedCheckout(VerifiedCheckoutDisposition.ConfirmedRetired));
                if (CheckoutFailure == "unknown")
                    return ValueTask.FromResult(new VerifiedCheckout(VerifiedCheckoutDisposition.Unknown));
                if (CheckoutFailure == "throw") throw new InvalidOperationException("fixture-private-checkout-failure");
                var stream = Channels.Dequeue();
                var grant = new VerifiedInstanceGrant(invocation, stream, async () =>
                {
                    await stream.DisposeAsync();
                    lock (_gate) _issued.Remove(invocation.Id);
                    AfterClose?.Invoke(ordinal);
                });
                _issued.Add(invocation.Id, grant);
                return ValueTask.FromResult(new VerifiedCheckout(VerifiedCheckoutDisposition.Transferred, grant));
            }
        }

        public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
        {
            BeforeAuthorize?.Invoke();
            lock (_gate)
            {
                if (_open != grant.Invocation.Route || !ReferenceEquals(_issued.GetValueOrDefault(grant.Invocation.Id), grant))
                    return null;
                _issued.Remove(grant.Invocation.Id);
                PhysicalUses++;
                return new DispatchPermit(this, ++Authorizations);
            }
        }

        private sealed class DispatchPermit(DispatchIssuer issuer, int ordinal) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                if (issuer.BeforeRetire is { } before) await before(ordinal);
                lock (issuer._gate)
                {
                    issuer.PhysicalUses--;
                    issuer.Releases++;
                }
            }
        }
    }
}
