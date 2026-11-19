using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostCausalDrainTests : IDisposable
{
    private static readonly HostEffectLedgerJsonContext Json = HostEffectLedgerJsonContext.Default;
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("air-causal-drain-");
    private string CheckpointPath => Path.Combine(_directory.FullName, "controller.lock");

    [Fact(Timeout = 60_000)]
    public async Task AuthorizedWriteSurvivesControllerDeathAndHoldsTheExactStopUntilPhysicalRetirement()
    {
        var binding = await PrepareAAsync();
        var invocation = Invocation(binding);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var acknowledgments = new BackendAcknowledgements(invocation, binding.BackendId);
        using var channel = new HeldApplicationChannel(acknowledgments);
        var issuer = new SurvivingIssuer(binding, invocation, channel, channel.SealAsync, acknowledgments);
        var body = "{\"model\":\"a\"}"u8.ToArray();
        var backend = channel.RespondAsync(issuer.ClosedGeneration, body, deadline.Token);
        var sending = new VerifiedInstanceTransport(invocation, issuer).SendAsync(invocation, body, null, deadline.Token);
        await channel.EnteredWrite.WaitAsync(deadline.Token);
        Assert.Equal(1, issuer.PhysicalUses);
        Assert.False(issuer.Prepared);
        Assert.Equal(0, channel.ReceiverBytes);

        // Only the transport and issuer are exercised. This is a synthetic logical timeout.
        var logical = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(logical.TrySetResult("timeout"));
        Assert.Equal("timeout", await logical.Task);
        Assert.False(sending.IsCompleted);

        HostEffectCheckpointState prepared;
        CausalDrainReply original;
        int originalPid;
        using (var controller = Start("hold-after-refusal", "b"))
        {
            originalPid = controller.Id;
            try
            {
                var request = await ReadRequestAsync(controller);
                original = issuer.CloseAndObserve(request);
                Assert.NotEqual(Guid.Empty, original.CloseId);
                Assert.Null(original.Witness);
                Assert.False(issuer.Prepared);
                await ReplyAsync(controller, original);
                Assert.Equal("pending", await ReadLineAsync(controller));
                prepared = ReadCheckpoint();
                AssertPrepared(prepared, request);
                AssertOnlyInitialLaunch(binding.Incumbent.Operation.Id);
                Assert.False(controller.HasExited);
                controller.Kill();
                await controller.WaitForExitAsync(deadline.Token);
                Assert.NotEqual(0, controller.ExitCode);
            }
            finally { await TerminateAsync(controller); }
        }

        var restarted = await TryDrainAsync(issuer, "c", "pending");
        Assert.NotEqual(originalPid, restarted.Request.ControllerPid);
        Assert.NotEqual(original.Request.Nonce, restarted.Request.Nonce);
        Assert.Equal(original.CloseId, restarted.CloseId);
        AssertPrepared(prepared, restarted.Request);
        Assert.Equal(prepared, ReadCheckpoint());
        AssertOnlyInitialLaunch(binding.Incumbent.Operation.Id);
        Assert.Equal(0, channel.ReceiverBytes);
        Assert.Equal(1, issuer.PhysicalUses);
        Assert.False(issuer.AcceptTerminal(new CausalBackendTerminal(original.CloseId, acknowledgments.ChannelIdentity)));

        channel.ReleaseWrite();
        var (terminal, wrongGrant) = await backend.WaitAsync(deadline.Token);
        var exchange = await sending.WaitAsync(deadline.Token);
        try
        {
            Assert.Equal("OK", await exchange.Response.Content.ReadAsStringAsync(deadline.Token));
            var cleanup = exchange.DisposeAsync().AsTask();
            await channel.ChannelSealed.WaitAsync(deadline.Token);
            Assert.False(cleanup.IsCompleted);
            Assert.True(acknowledgments.Authored(wrongGrant));
            Assert.Equal(terminal.CloseId, wrongGrant.CloseId);
            Assert.Equal(terminal.Use.Invocation, wrongGrant.Use.Invocation);
            Assert.NotEqual(terminal.Use.GrantId, wrongGrant.Use.GrantId);
            Assert.False(issuer.AcceptTerminal(wrongGrant));
            Assert.False(issuer.AcceptTerminal(terminal with { CloseId = Guid.NewGuid() }));
            Assert.Equal(1, issuer.PhysicalUses);
            Assert.Equal(0, issuer.Releases);
            var sealedButUnacknowledged = await TryDrainAsync(issuer, "c", "pending");
            Assert.Equal(original.CloseId, sealedButUnacknowledged.CloseId);
            AssertPrepared(prepared, sealedButUnacknowledged.Request);
            Assert.Equal(prepared, ReadCheckpoint());
            AssertOnlyInitialLaunch(binding.Incumbent.Operation.Id);

            Assert.True(issuer.AcceptTerminal(terminal));
            Assert.False(issuer.AcceptTerminal(terminal));
            await cleanup.WaitAsync(deadline.Token);
        }
        finally
        {
            // Test unwind can deliver the real backend acknowledgment if an assertion fails first.
            issuer.AcceptTerminal(terminal);
            await exchange.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        }
        Assert.Equal(0, issuer.PhysicalUses);
        Assert.Equal(1, issuer.Releases);
        var drained = await TryDrainAsync(issuer, "c", "pending", publishWitness: true);
        AssertPrepared(prepared, drained.Request);
        var witness = Assert.IsType<CausalRetirementWitness>(drained.Witness);
        Assert.Equal(original.CloseId, witness.CloseId);
        Assert.Equal(terminal, witness.Terminal);
        Assert.Equal(invocation, witness.FrontierUse.Invocation);
        var stopped = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.StopApplied, stopped.Phase);
        var stoppedTransition = Assert.IsType<HostCheckpointTransition>(stopped.Transition);
        Assert.Equal(prepared.Transition!.Stop, stoppedTransition.Stop);
        Assert.Equal(binding.Incumbent.Operation, stoppedTransition.StopEffect?.IncumbentLaunch);
        AssertOneOfEveryRecord(drained.Request.Stop.Id);
        var priorVacancyQueries = CountVacancyQueries();
        Assert.Equal(1, priorVacancyQueries); // The post-stop query still saw A in the independent source.

        // The independent partition observer only now reports absence of A.
        Assert.Equal("observed-facts-installed", await RunAsync("partition", "vacant-retired"));
        Assert.Equal("resident:b", await RunAsync("normal", "c"));
        Assert.Equal(priorVacancyQueries + 1, CountVacancyQueries());
        var completed = ReadCheckpoint();
        var b = Assert.IsType<HostCheckpointLaunchEffect>(completed.AppliedLaunch);
        Assert.Equal(drained.Request.Successor, b.Plan);
        Assert.Equal(drained.Request.Epoch, b.Operation.Epoch);
        Assert.Equal(stoppedTransition.StopEffect, completed.LastStoppedResident?.Effect);
        AssertOneOfEveryRecord(b.Operation.Id);
        foreach (var journal in EffectJournals) Assert.Equal(3, Count(journal));
        Assert.Null(await issuer.CheckoutAsync(invocation with
        {
            Id = Guid.NewGuid(), Route = invocation.Route with { Engine = "b", ContainerId = b.InstanceId },
        }, deadline.Token));
        Assert.False(logical.TrySetResult("ready"));
    }

    [Theory(Timeout = 25_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactPreparedGrantWritesOnlyWhenFirstWriteWinsAgainstClosure(bool closeFirst)
    {
        var binding = SyntheticBinding();
        var invocation = Invocation(binding);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var acknowledgments = new BackendAcknowledgements(invocation, binding.BackendId);
        using var channel = new HeldApplicationChannel(acknowledgments);
        var prepared = new TaskCompletionSource<VerifiedInstanceGrant>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheckout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var issuer = new SurvivingIssuer(binding, invocation, channel, channel.SealAsync, acknowledgments)
        {
            HoldCheckout = grant =>
            {
                prepared.SetResult(grant);
                return releaseCheckout.Task;
            },
        };
        var body = "{\"model\":\"a\"}"u8.ToArray();
        var sending = new VerifiedInstanceTransport(invocation, issuer).SendAsync(invocation, body, null, deadline.Token);
        var grant = await prepared.Task.WaitAsync(deadline.Token);
        Assert.Equal(invocation, grant.Invocation);
        Assert.Equal(invocation, acknowledgments.ChannelIdentity.Invocation);
        Assert.True(issuer.Prepared);
        Assert.Equal(0, issuer.PhysicalUses);
        var close = CloseRequest(binding);

        if (closeFirst)
        {
            var receiver = channel.ObserveReceiverEndAsync(deadline.Token);
            Assert.NotEqual(Guid.Empty, issuer.CloseAndObserve(close).CloseId);
            Assert.False(issuer.Prepared);
            releaseCheckout.SetResult();
            await Assert.ThrowsAsync<HttpRequestException>(() => sending.WaitAsync(deadline.Token));
            await channel.ChannelSealed.WaitAsync(deadline.Token);
            Assert.Equal(0, await receiver.WaitAsync(deadline.Token));
            Assert.False(channel.EnteredWrite.IsCompleted);
            Assert.Equal(0, channel.ReceiverBytes);
            Assert.Equal(0, issuer.PhysicalUses);
            Assert.Equal(0, issuer.Releases);
            return;
        }

        var backend = channel.RespondAsync(issuer.ClosedGeneration, body, deadline.Token);
        releaseCheckout.SetResult();
        await channel.EnteredWrite.WaitAsync(deadline.Token);
        Assert.Equal(1, issuer.PhysicalUses);
        channel.ReleaseWrite();
        Assert.Equal(body, await channel.RequestDelivered.WaitAsync(deadline.Token));
        Assert.Equal(1, issuer.PhysicalUses);
        Assert.NotEqual(Guid.Empty, issuer.CloseAndObserve(close).CloseId);
        var (terminal, _) = await backend.WaitAsync(deadline.Token);
        var exchange = await sending.WaitAsync(deadline.Token);
        try
        {
            Assert.Equal("OK", await exchange.Response.Content.ReadAsStringAsync(deadline.Token));
            Assert.True(issuer.AcceptTerminal(terminal));
        }
        finally
        {
            issuer.AcceptTerminal(terminal);
            await exchange.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        }
        Assert.Equal(0, issuer.PhysicalUses);
        Assert.Equal(1, issuer.Releases);
    }

    [Fact(Timeout = 45_000)]
    public async Task ExactAckBeforeCleanupDoesNotRetireThroughAnAttemptedOrFailedSeal()
    {
        var binding = SyntheticBinding();
        var invocation = Invocation(binding);
        var sealSucceeds = false;
        var acknowledgments = new BackendAcknowledgements(invocation, binding.BackendId);
        var issuer = new SurvivingIssuer(binding, invocation, Stream.Null, () => sealSucceeds
            ? ValueTask.CompletedTask : ValueTask.FromException(new IOException("Synthetic channel seal failure.")), acknowledgments);
        var grant = Assert.IsType<VerifiedInstanceGrant>(await issuer.CheckoutAsync(invocation, CancellationToken.None));
        var permit = Assert.IsAssignableFrom<IAsyncDisposable>(issuer.TryAuthorizeFirstWrite(grant));
        // Small issuer-only control, with an exact synthetic backend acknowledgment.
        var request = CloseRequest(binding);
        var closed = issuer.CloseAndObserve(request);
        Assert.NotEqual(Guid.Empty, closed.CloseId);
        Assert.True(issuer.AcceptTerminal(acknowledgments.Complete(closed.CloseId)));
        Assert.Equal(1, issuer.PhysicalUses);
        Assert.Null(issuer.CloseAndObserve(request).Witness);

        await Assert.ThrowsAsync<IOException>(() => grant.CloseAsync().AsTask());
        var retiring = permit.DisposeAsync().AsTask();
        Assert.False(retiring.IsCompleted);
        Assert.Equal(1, issuer.PhysicalUses);
        Assert.Null(issuer.CloseAndObserve(request).Witness);

        sealSucceeds = true;
        await grant.CloseAsync();
        await retiring.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, issuer.PhysicalUses);
        Assert.Equal(1, issuer.Releases);
        Assert.NotNull(issuer.CloseAndObserve(request).Witness);
    }

    private async Task<CausalRouteBinding> PrepareAAsync()
    {
        HostEffectCheckpoint.ProvisionNew(CheckpointPath);
        Assert.Equal("initialized", await RunAsync("initialize", "a"));
        var initial = Assert.IsType<string>(await RunAsync("prepare-initial", "a"));
        Assert.Equal("partition-seed-installed", await RunAsync("partition-seed", initial));
        Assert.Equal("resident:a", await RunAsync("normal", "a"));
        Assert.Equal("observed-facts-installed", await RunAsync("partition", "owned-retired"));
        var incumbent = Assert.IsType<HostCheckpointLaunchEffect>(ReadCheckpoint().AppliedLaunch);
        var facts = Assert.IsType<PartitionFacts>(JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(_directory.FullName, "partition-facts.json")), Json.PartitionFacts));
        var binding = NewBinding(facts.Scope, facts.OwnerGeneration, facts.IngressGeneration, incumbent);
        Publish(CausalDrainFiles.Binding, binding, Json.CausalRouteBinding);
        return binding;
    }

    private static CausalRouteBinding NewBinding(PartitionScope scope, long ownerGeneration, long ingressGeneration,
        HostCheckpointLaunchEffect incumbent)
    {
        var route = new VerifiedInstanceRoute(scope.Group, scope.EnrollmentGeneration, incumbent.Operation.Engine,
            incumbent.Plan.Digest(), incumbent.InstanceId, "serving-a", incumbent.Operation.Epoch, ingressGeneration);
        return new CausalRouteBinding(scope, ownerGeneration, incumbent, route, Guid.NewGuid(), Guid.NewGuid());
    }

    private static VerifiedInstanceInvocation Invocation(CausalRouteBinding binding) =>
        new(binding.Route, Guid.NewGuid(), new Uri("http://fixture.invalid/v1/chat/completions"));

    private static CausalRouteBinding SyntheticBinding()
    {
        var plan = new HostCheckpointPlan("a", "a.model", "named", "fixture-config", 1000, "fixture-route", new string('A', 64));
        var launch = new HostMutation(1, Guid.NewGuid(), HostMutationKind.Launch, plan.Engine, plan.Model, plan.Tier, plan.Digest());
        return NewBinding(new PartitionScope("fixture-group", "fixture-enrollment", 1, 1, 1), 1, 1,
            new HostCheckpointLaunchEffect(launch, plan, "fixture-a"));
    }

    private static CausalDrainRequest CloseRequest(CausalRouteBinding binding)
    {
        var successor = binding.Incumbent.Plan with { Engine = "b", Model = "b.model" };
        var epoch = binding.Incumbent.Operation.Epoch + 1;
        var stop = new HostMutation(epoch, Guid.NewGuid(), HostMutationKind.Stop, binding.Route.Engine, binding.Route.ContainerId, null, null);
        return new CausalDrainRequest(Guid.NewGuid(), Environment.ProcessId, binding, stop, epoch, successor);
    }

    private async Task<CausalDrainReply> TryDrainAsync(SurvivingIssuer issuer, string target, string expected, bool publishWitness = false)
    {
        using var controller = Start("normal", target);
        try
        {
            var request = await ReadRequestAsync(controller);
            var reply = issuer.CloseAndObserve(request);
            if (publishWitness)
                Publish(CausalDrainFiles.Frontier, Assert.IsType<CausalRetirementWitness>(reply.Witness), Json.CausalRetirementWitness);
            else
                Assert.Null(reply.Witness);
            await ReplyAsync(controller, reply);
            Assert.Equal(expected, await ReadExitAsync(controller));
            return reply;
        }
        finally { await TerminateAsync(controller); }
    }

    private void Publish<T>(string name, T value, JsonTypeInfo<T> type)
    {
        using var fence = new FileStream(Path.Combine(_directory.FullName, "worker.fence"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var path = Path.Combine(_directory.FullName, name);
        var temp = $"{path}.{Guid.NewGuid():N}.new";
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, value, type);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path);
    }

    private static void AssertPrepared(HostEffectCheckpointState prepared, CausalDrainRequest request)
    {
        Assert.Equal(HostCheckpointPhase.StopPrepared, prepared.Phase);
        Assert.Equal(request.Binding.Incumbent, prepared.AppliedLaunch);
        Assert.Equal(request.Epoch, prepared.Epoch);
        var transition = Assert.IsType<HostCheckpointTransition>(prepared.Transition);
        Assert.Equal(request.Stop, transition.Stop);
        Assert.Equal(request.Successor, transition.Plan);
        Assert.Null(transition.Launch);
    }

    private void AssertOnlyInitialLaunch(Guid initial)
    {
        foreach (var journal in EffectJournals)
        {
            Assert.Equal(1, Count(journal));
            Assert.Equal(1, Count(journal, initial));
        }
        Assert.False(File.Exists(Path.Combine(_directory.FullName, CausalDrainFiles.Frontier)));
    }

    private void AssertOneOfEveryRecord(Guid operation)
    {
        foreach (var journal in EffectJournals) Assert.Equal(1, Count(journal, operation));
    }

    private static readonly string[] EffectJournals = ["sends", "acceptances", "decisions", "attempts", "effects", "terminals"];

    private int Count(string journal, Guid? operation = null)
    {
        var directory = Path.Combine(_directory.FullName, journal);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, operation is { } id ? $"{id:N}*.json" : "*.json").Length : 0;
    }

    private int CountVacancyQueries() => Directory.GetFiles(Path.Combine(_directory.FullName, "partition-queries"), "*.json")
        .Count(static path => JsonSerializer.Deserialize(File.ReadAllBytes(path), Json.PartitionQuery)?.Vacant == true);

    private HostEffectCheckpointState ReadCheckpoint()
    {
        using var checkpoint = HostEffectCheckpoint.Open(CheckpointPath);
        return checkpoint.State;
    }

    private Process Start(string mode, string target)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Arboreal.InferenceRouter.HostReservationProbe.dll"));
        start.ArgumentList.Add("--causal-drain");
        start.ArgumentList.Add(CheckpointPath);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(target);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the causal-drain controller.");
    }

    private async Task<string?> RunAsync(string mode, string target)
    {
        using var controller = Start(mode, target);
        try { return await ReadExitAsync(controller); }
        finally { await TerminateAsync(controller); }
    }

    private static async Task<CausalDrainRequest> ReadRequestAsync(Process child)
    {
        var line = Assert.IsType<string>(await ReadLineAsync(child));
        Assert.InRange(line.Length, 1, 32 * 1024);
        var request = Assert.IsType<CausalDrainRequest>(JsonSerializer.Deserialize(line, Json.CausalDrainRequest));
        Assert.Equal(child.Id, request.ControllerPid);
        return request;
    }

    private static Task ReplyAsync(Process child, CausalDrainReply reply) =>
        child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(reply, Json.CausalDrainReply));

    private static async Task<string?> ReadLineAsync(Process child)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await child.StandardOutput.ReadLineAsync(timeout.Token);
    }

    private static async Task<string?> ReadExitAsync(Process child)
    {
        var result = await ReadLineAsync(child);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, child.ExitCode);
        return result;
    }

    private static async Task TerminateAsync(Process child)
    {
        if (!child.HasExited) child.Kill();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
