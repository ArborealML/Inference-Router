using System.Diagnostics;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class HostSuccessorVacancyTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("air-vacancy-");
    private string CheckpointPath => Path.Combine(_directory.FullName, "controller.lock");

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
        Assert.Equal(new LedgerCounts(1, 1, 0, 0), Counts(launch.Id));
        Assert.False(HasTerminal(launch.Id));

        var queries = CountQueries();
        Assert.Equal("pending", await RunAsync("normal", "c"));
        Assert.Equal(pending, ReadCheckpoint());
        Assert.Equal(queries, CountQueries());
        Assert.Equal(new LedgerCounts(1, 1, 0, 0), Counts(launch.Id));
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
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await process.StandardOutput.ReadLineAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            return result;
        }
        finally { await TerminateAsync(process); }
    }

    private static async Task TerminateAsync(Process process)
    {
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private readonly record struct LedgerCounts(int Sends, int Acceptances, int Attempts, int Effects);
}
