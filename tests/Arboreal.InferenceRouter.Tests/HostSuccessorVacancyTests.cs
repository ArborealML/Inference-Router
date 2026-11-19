using System.Diagnostics;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class HostSuccessorVacancyTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("air-vacancy-");
    private string CheckpointPath => Path.Combine(_directory.FullName, "controller.lock");
    private string DaemonPath => Path.Combine(_directory.FullName, "daemon-residency.json");

    [Theory(Timeout = 45_000)]
    [InlineData("vacant-unavailable")]
    [InlineData("vacant-partial")]
    [InlineData("vacant-old-enrollment")]
    [InlineData("vacant-old-controller")]
    [InlineData("vacant-old-worker")]
    [InlineData("vacant-unknown-owner")]
    [InlineData("vacant-foreign")]
    public async Task TerminalStopAndVacantDaemonCannotReplaceIndependentPartitionProof(string facts)
    {
        await InitializeAAsync();
        var stopped = await ApplyStopWithoutVacancyAsync();
        await InstallFactsAsync(facts);
        Assert.Equal("pending", await RunAsync("normal", "c"));
        Assert.Equal(stopped, ReadCheckpoint());
        Assert.Null(stopped.Transition!.Launch);
        Assert.Equal(new LedgerCounts(2, 2, 2, 2), Counts());

        await InstallFactsAsync("vacant-retired");
        Assert.Equal("resident:b", await RunAsync("normal", "c"));
        AssertSuccessor(stopped);
    }

    [Theory(Timeout = 45_000)]
    [InlineData("coverage-aba", "CoverageGeneration")]
    [InlineData("owner-aba", "OwnerGeneration")]
    public async Task ChangedAndRestoredPartitionCannotPassTheIssueFence(string rotation, string generationProperty)
    {
        await InitializeAAsync();
        var stopped = await ApplyStopWithoutVacancyAsync();
        await InstallFactsAsync("vacant-retired");
        var oldFacts = ReadJson("partition-facts.json");
        var vacantDaemon = File.ReadAllBytes(DaemonPath);

        Assert.Equal("pending", await RunAsync(rotation, "c"));
        var pending = ReadCheckpoint();
        var transition = Assert.IsType<HostCheckpointTransition>(pending.Transition);
        var launch = Assert.IsType<HostMutation>(transition.Launch);
        Assert.Equal(stopped.Transition!.Plan, transition.Plan);
        Assert.Equal(stopped.Epoch, launch.Epoch);
        Assert.Equal(HostCheckpointPhase.LaunchDispatching, pending.Phase);
        var restored = ReadJson("partition-facts.json");
        Assert.Equal(oldFacts.GetProperty("Coverage").GetInt32(), restored.GetProperty("Coverage").GetInt32());
        Assert.Equal(oldFacts.GetProperty("Foreign").GetBoolean(), restored.GetProperty("Foreign").GetBoolean());
        Assert.Equal(oldFacts.GetProperty("Resident").ValueKind, restored.GetProperty("Resident").ValueKind);
        Assert.Equal(oldFacts.GetProperty(generationProperty).GetInt64() + 2, restored.GetProperty(generationProperty).GetInt64());
        AssertNoDaemonEntry(launch.Id, vacantDaemon);

        var queries = CountQueries();
        Assert.Equal("pending", await RunAsync("normal", "c"));
        Assert.Equal(pending, ReadCheckpoint());
        Assert.Equal(queries, CountQueries());
        AssertNoDaemonEntry(launch.Id, vacantDaemon);
    }

    [Fact(Timeout = 45_000)]
    public async Task StopPreparedReopensWithExactOwnerAndFreshCausalRetirement()
    {
        await InitializeAAsync();
        await KillAtAsync("stop-prepared", "b");
        var prepared = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.StopPrepared, prepared.Phase);
        var preparedTransition = Assert.IsType<HostCheckpointTransition>(prepared.Transition);
        var stop = preparedTransition.Stop!;
        var oldQuery = LastQuery("owner").GetProperty("Nonce").GetGuid();
        var queries = CountQueries();

        Assert.Equal("pending", await RunAsync("replay-owner", "c"));
        Assert.Equal(prepared, ReadCheckpoint());
        Assert.Equal(queries + 1, CountQueries());
        Assert.Equal(oldQuery, LastQuery("owner").GetProperty("Nonce").GetGuid());
        Assert.Equal(new LedgerCounts(0, 0, 0, 0), Counts(stop.Id));

        foreach (var facts in new[] { "owned-unretired", "owned-stale-retirement" })
        {
            await InstallFactsAsync(facts);
            Assert.Equal("pending", await RunAsync("normal", "c"));
            Assert.Equal(prepared, ReadCheckpoint());
            Assert.Equal(new LedgerCounts(0, 0, 0, 0), Counts(stop.Id));
        }

        await InstallFactsAsync("owned-retired");
        Assert.Equal("pending", await RunAsync("normal", "c"));
        var stopped = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.StopApplied, stopped.Phase);
        var stoppedTransition = Assert.IsType<HostCheckpointTransition>(stopped.Transition);
        Assert.Equal(stop, stoppedTransition.Stop);
        Assert.Equal(preparedTransition.Plan, stoppedTransition.Plan);
        Assert.Equal(prepared.Epoch, stopped.Epoch);
        Assert.NotEqual(oldQuery, LastQuery("owner").GetProperty("Nonce").GetGuid());
        Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts(stop.Id));
        Assert.Equal(new LedgerCounts(2, 2, 2, 2), Counts());
    }

    [Fact(Timeout = 45_000)]
    public async Task SameInstanceWithWrongLaunchIdCannotAuthorizeStopOrSuccessor()
    {
        await InitializeAAsync();
        var a = Assert.IsType<HostCheckpointLaunchEffect>(ReadCheckpoint().AppliedLaunch);
        await InstallFactsAsync("owned-wrong-launch");
        var owner = ReadJson("partition-facts.json").GetProperty("Resident");
        AssertWrongLaunchId(owner, a);

        Assert.Equal("pending", await RunAsync("normal", "b"));
        var prepared = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.StopPrepared, prepared.Phase);
        var stop = prepared.Transition!.Stop!;
        Assert.Equal(new LedgerCounts(0, 0, 0, 0), Counts(stop.Id));
        Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts());

        await InstallFactsAsync("owned-retired");
        Assert.Equal("pending", await RunAsync("normal", "c"));
        var stopped = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.StopApplied, stopped.Phase);
        Assert.Equal(stop, stopped.Transition!.Stop);
        await InstallFactsAsync("vacant-wrong-launch");
        var retired = ReadJson("partition-facts.json").GetProperty("Retirement").GetProperty("Resident");
        AssertWrongLaunchId(retired, a);
        Assert.Equal("pending", await RunAsync("normal", "c"));
        Assert.Equal(stopped, ReadCheckpoint());
        Assert.Equal(new LedgerCounts(2, 2, 2, 2), Counts());

        await InstallFactsAsync("vacant-retired");
        Assert.Equal("resident:b", await RunAsync("normal", "c"));
        AssertSuccessor(stopped);
    }

    [Fact(Timeout = 45_000)]
    public async Task ChangedPriorLaunchInInstalledStopEffectCannotAuthorizeDaemonEntry()
    {
        await InitializeAAsync();
        var stopped = await ApplyStopWithoutVacancyAsync();
        var stoppedTransition = Assert.IsType<HostCheckpointTransition>(stopped.Transition);
        var stop = stoppedTransition.Stop!;
        var effectPath = Path.Combine("effects", $"{stop.Id:N}.json");
        var predecessor = ReadJson(effectPath).GetProperty("Stop").GetProperty("IncumbentLaunch");
        var a = Assert.IsType<HostCheckpointLaunchEffect>(stopped.AppliedLaunch);
        Assert.Equal(a.Operation.Id, predecessor.GetProperty("Id").GetGuid());
        await InstallFactsAsync("vacant-retired");
        Assert.Equal("stopped-launch-poisoned", await RunAsync("poison-stopped-launch", stop.Id.ToString("D")));
        var changed = ReadJson(effectPath).GetProperty("Stop").GetProperty("IncumbentLaunch");
        Assert.NotEqual(a.Operation.Id, changed.GetProperty("Id").GetGuid());
        Assert.Equal(a.Operation.Engine, changed.GetProperty("Engine").GetString());
        Assert.Equal(a.Operation.Epoch, changed.GetProperty("Epoch").GetInt64());
        Assert.Equal(a.InstanceId, ReadJson(effectPath).GetProperty("Stop").GetProperty("InstanceId").GetString());
        var vacantDaemon = File.ReadAllBytes(DaemonPath);

        Assert.Equal("pending", await RunAsync("normal", "c"));
        var pending = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.LaunchDispatching, pending.Phase);
        var transition = Assert.IsType<HostCheckpointTransition>(pending.Transition);
        Assert.Equal(stoppedTransition.StopEffect, transition.StopEffect);
        Assert.Equal(stoppedTransition.Plan, transition.Plan);
        Assert.Equal(stopped.Epoch, pending.Epoch);
        var b = Assert.IsType<HostMutation>(transition.Launch);
        AssertNoDaemonEntry(b.Id, vacantDaemon);
    }

    [Fact(Timeout = 45_000)]
    public async Task IndependentFactsWriterIsExcludedUntilDaemonEntryReleasesTheFence()
    {
        await InitializeAAsync();
        var stopped = await ApplyStopWithoutVacancyAsync();
        await InstallFactsAsync("vacant-retired");
        var factsPath = Path.Combine(_directory.FullName, "partition-facts.json");
        var factsBefore = File.ReadAllBytes(factsPath);
        var coverage = ReadJson("partition-facts.json").GetProperty("CoverageGeneration").GetInt64();
        var vacantDaemon = File.ReadAllBytes(DaemonPath);
        using var controller = Start("pause-attempt", "c");
        try
        {
            Assert.Equal("attempt", await ReadLineAsync(controller));
            var acceptancePath = Assert.Single(Directory.GetFiles(Path.Combine(_directory.FullName, "acceptances"), "*.json"),
                file => ReadJson(file).GetProperty("Operation").GetProperty("Engine").GetString() == "b");
            var operationId = Guid.ParseExact(Path.GetFileNameWithoutExtension(acceptancePath), "N");
            Assert.Equal(new LedgerCounts(1, 1, 1, 0), Counts(operationId));
            Assert.Equal(1, Count("decisions", operationId));

            using var writer = Start("pause-writer-blocked", "vacant-partial");
            try
            {
                Assert.Equal("writer-blocked", await ReadLineAsync(writer));
                Assert.Equal(factsBefore, File.ReadAllBytes(factsPath));
                Assert.Equal(vacantDaemon, File.ReadAllBytes(DaemonPath));
                Assert.Equal(new LedgerCounts(1, 1, 1, 0), Counts(operationId));
                await controller.StandardInput.WriteLineAsync("continue");
                Assert.Equal("resident:b", await ReadExitAsync(controller));
                AssertSuccessor(stopped);

                await writer.StandardInput.WriteLineAsync("continue");
                Assert.Equal("observed-facts-installed", await ReadExitAsync(writer));
            }
            finally { await TerminateAsync(writer); }
        }
        finally { await TerminateAsync(controller); }

        Assert.Equal(coverage + 1, ReadJson("partition-facts.json").GetProperty("CoverageGeneration").GetInt64());
        Assert.Equal(1, ReadJson("partition-facts.json").GetProperty("Coverage").GetInt32());
        AssertSuccessor(stopped);
    }

    [Fact(Timeout = 45_000)]
    public async Task LaunchPreparedReopensOnlyWithFreshProofAndKeepsTheOriginalBOperation()
    {
        await InitializeAAsync();
        var stopped = await ApplyStopWithoutVacancyAsync();
        await InstallFactsAsync("vacant-retired");
        await KillAtAsync("launch-prepared", "c");
        var prepared = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.LaunchPrepared, prepared.Phase);
        var launch = prepared.Transition!.Launch!;
        var oldQuery = LastQuery("vacancy").GetProperty("Nonce").GetGuid();

        await InstallFactsAsync("vacant-old-worker");
        Assert.Equal("pending", await RunAsync("normal", "c"));
        Assert.Equal(prepared, ReadCheckpoint());
        Assert.Equal(new LedgerCounts(0, 0, 0, 0), Counts(launch.Id));

        await InstallFactsAsync("vacant-retired");
        Assert.Equal("resident:b", await RunAsync("normal", "c"));
        Assert.Equal(launch, ReadCheckpoint().AppliedLaunch?.Operation);
        Assert.NotEqual(oldQuery, LastQuery("vacancy").GetProperty("Nonce").GetGuid());
        AssertSuccessor(stopped);
    }

    [Fact(Timeout = 45_000)]
    public async Task RecoveredStopRequiresANewVacancyQueryAndRejectsThePriorControllerReply()
    {
        await InitializeAAsync();
        await KillAtAsync("terminal", "b");
        var lostStop = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.StopDispatching, lostStop.Phase);
        var stop = lostStop.Transition!.Stop!;
        Assert.True(HasTerminal(stop.Id));
        Assert.Equal(1, CountQueries()); // Only the owner query preceded the stop.

        await InstallFactsAsync("vacant-retired");
        await KillAtAsync("vacancy-observed", "c");
        var stopped = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.StopApplied, stopped.Phase);
        Assert.Equal(stop, stopped.Transition!.Stop);
        var observed = LastQuery("vacancy");
        Assert.Equal(stop.Id, observed.GetProperty("Stop").GetProperty("Id").GetGuid());
        var incumbent = Assert.IsType<HostCheckpointLaunchEffect>(stopped.AppliedLaunch);
        Assert.Equal(incumbent.Operation.Id, observed.GetProperty("StopEffect").GetProperty("IncumbentLaunch").GetProperty("Id").GetGuid());
        Assert.Equal(incumbent.Operation.PlanDigest,
            observed.GetProperty("Previous").GetProperty("Operation").GetProperty("PlanDigest").GetString());
        Assert.Equal(stopped.Epoch, observed.GetProperty("Epoch").GetInt64());
        Assert.Equal("b", observed.GetProperty("Successor").GetProperty("Engine").GetString());
        var oldQuery = observed.GetProperty("Nonce").GetGuid();

        Assert.Equal("pending", await RunAsync("replay-vacancy", "c"));
        Assert.Equal(stopped, ReadCheckpoint());
        Assert.Equal(3, CountQueries());
        Assert.Equal(oldQuery, LastQuery("vacancy").GetProperty("Nonce").GetGuid());
        Assert.Equal(new LedgerCounts(2, 2, 2, 2), Counts());

        Assert.Equal("resident:b", await RunAsync("normal", "c"));
        Assert.NotEqual(oldQuery, LastQuery("vacancy").GetProperty("Nonce").GetGuid());
        AssertSuccessor(stopped);
    }

    [Theory(Timeout = 45_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApparentlyVacantPartitionCannotSettleUncertainDispatch(bool launch)
    {
        await InitializeAAsync();
        if (launch)
        {
            await ApplyStopWithoutVacancyAsync();
            await InstallFactsAsync("vacant-retired");
        }
        await KillAtAsync("applied", launch ? "c" : "b");
        var pending = ReadCheckpoint();
        var transition = pending.Transition!;
        var operation = (launch ? transition.Launch : transition.Stop)!;
        Assert.Equal(launch ? HostCheckpointPhase.LaunchDispatching : HostCheckpointPhase.StopDispatching, pending.Phase);
        Assert.False(HasTerminal(operation.Id));
        Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts(operation.Id));

        await InstallFactsAsync("vacant-retired");
        var queries = CountQueries();
        var counts = Counts();
        Assert.Equal("pending", await RunAsync("normal", "c"));
        Assert.Equal(pending, ReadCheckpoint());
        Assert.Equal(counts, Counts());
        Assert.Equal(queries, CountQueries());
    }

    [Fact(Timeout = 45_000)]
    public async Task RecoveredLaunchUsesItsExactEffectInsteadOfQueryingVacancyAgain()
    {
        await InitializeAAsync();
        var stopped = await ApplyStopWithoutVacancyAsync();
        await InstallFactsAsync("vacant-retired");
        await KillAtAsync("terminal", "c");
        var pending = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.LaunchDispatching, pending.Phase);
        var launch = pending.Transition!.Launch!;
        Assert.True(HasTerminal(launch.Id));

        await InstallFactsAsync("vacant-unavailable");
        var queries = CountQueries();
        Assert.Equal("resident:b", await RunAsync("normal", "c"));
        Assert.Equal(queries, CountQueries());
        Assert.Equal(launch, ReadCheckpoint().AppliedLaunch?.Operation);
        AssertSuccessor(stopped);
    }

    private async Task InitializeAAsync()
    {
        HostEffectCheckpoint.ProvisionNew(CheckpointPath);
        Assert.Equal("initialized", await RunAsync("initialize", "a"));
        var operationId = Assert.IsType<string>(await RunAsync("prepare-initial", "a"));
        Assert.Equal("partition-seed-installed", await RunAsync("partition-seed", operationId));
        Assert.Equal("resident:a", await RunAsync("normal", "a"));
        await InstallFactsAsync("owned-retired");
    }

    private async Task<HostEffectCheckpointState> ApplyStopWithoutVacancyAsync()
    {
        // The daemon really stops A. The independent source still observes an owner.
        Assert.Equal("pending", await RunAsync("normal", "b"));
        var stopped = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.StopApplied, stopped.Phase);
        Assert.Equal(JsonValueKind.Null, ReadJson("daemon-residency.json").GetProperty("Resident").ValueKind);
        Assert.Equal(JsonValueKind.Object, ReadJson("partition-facts.json").GetProperty("Resident").ValueKind);
        Assert.Equal(new LedgerCounts(2, 2, 2, 2), Counts());
        return stopped;
    }

    private void AssertSuccessor(HostEffectCheckpointState stopped)
    {
        var resident = ReadCheckpoint();
        var applied = Assert.IsType<HostCheckpointLaunchEffect>(resident.AppliedLaunch);
        var transition = Assert.IsType<HostCheckpointTransition>(stopped.Transition);
        Assert.Equal(transition.Plan, applied.Plan);
        Assert.Equal(stopped.Epoch, applied.Operation.Epoch);
        Assert.Equal(transition.Stop, resident.LastStoppedResident?.Effect.Operation);
        Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts(applied.Operation.Id));
        Assert.Equal(new LedgerCounts(3, 3, 3, 3), Counts());
    }

    private async Task InstallFactsAsync(string facts) =>
        Assert.Equal("observed-facts-installed", await RunAsync("partition", facts));

    private static void AssertWrongLaunchId(JsonElement resident, HostCheckpointLaunchEffect expected)
    {
        var operation = resident.GetProperty("Operation");
        Assert.NotEqual(expected.Operation.Id, operation.GetProperty("Id").GetGuid());
        Assert.Equal(expected.Operation.Engine, operation.GetProperty("Engine").GetString());
        Assert.Equal(expected.Operation.Epoch, operation.GetProperty("Epoch").GetInt64());
        Assert.Equal(expected.InstanceId, resident.GetProperty("InstanceId").GetString());
        Assert.Equal(expected.Operation.PlanDigest, operation.GetProperty("PlanDigest").GetString());
    }

    private void AssertNoDaemonEntry(Guid launchId, byte[] expectedDaemon)
    {
        Assert.Equal(new LedgerCounts(1, 1, 0, 0), Counts(launchId));
        Assert.Equal(0, Count("decisions", launchId));
        Assert.Equal(0, Count("effects", launchId));
        Assert.False(HasTerminal(launchId));
        Assert.Equal(expectedDaemon, File.ReadAllBytes(DaemonPath));
    }

    private HostEffectCheckpointState ReadCheckpoint()
    {
        using var checkpoint = HostEffectCheckpoint.Open(CheckpointPath);
        return checkpoint.State;
    }

    private LedgerCounts Counts(Guid? id = null) => new(Count("sends", id), Count("acceptances", id), Count("attempts", id), Count("effects", id));
    private int CountQueries() => Count("partition-queries");
    private bool HasTerminal(Guid id) => Count("terminals", id) != 0;

    private int Count(string journal, Guid? id = null)
    {
        var directory = Path.Combine(_directory.FullName, journal);
        return Directory.Exists(directory) ? Directory.GetFiles(directory, id is { } operation ? $"{operation:N}*.json" : "*.json").Length : 0;
    }

    private JsonElement LastQuery(string kind) => ReadJson($"last-{kind}-proof.json").GetProperty("Query");

    private JsonElement ReadJson(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_directory.FullName, name)));
        return document.RootElement.Clone();
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
        start.ArgumentList.Add("--successor-vacancy");
        start.ArgumentList.Add(CheckpointPath);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(target);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the offline vacancy probe.");
    }

    private async Task KillAtAsync(string barrier, string target)
    {
        using var process = Start($"pause-{barrier}", target);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal(barrier, await process.StandardOutput.ReadLineAsync(timeout.Token));
            Assert.False(process.HasExited);
        }
        finally { await TerminateAsync(process); }
    }

    private async Task<string?> RunAsync(string mode, string target)
    {
        using var process = Start(mode, target);
        try { return await ReadExitAsync(process); }
        finally { await TerminateAsync(process); }
    }

    private static async Task<string?> ReadLineAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await process.StandardOutput.ReadLineAsync(timeout.Token);
    }

    private static async Task<string?> ReadExitAsync(Process process)
    {
        var result = await ReadLineAsync(process);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, process.ExitCode);
        return result;
    }

    private static async Task TerminateAsync(Process process)
    {
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private readonly record struct LedgerCounts(int Sends, int Acceptances, int Attempts, int Effects);
}
