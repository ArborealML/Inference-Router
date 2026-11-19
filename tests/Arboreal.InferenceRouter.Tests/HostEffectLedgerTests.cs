using System.Diagnostics;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class HostEffectLedgerTests : IDisposable
{
    private const string WrongId = "wrong-instance";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("air-ledger-");
    private string CheckpointPath => Path.Combine(_directory.FullName, "controller.lock");
    private string DaemonPath => Path.Combine(_directory.FullName, "daemon-residency.json");

    [Theory(Timeout = 45_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingWrongOccupancyCannotSettleLaunchAndDurableContradictionCannotBeRewritten(bool durableWrongId)
    {
        await InitializeAsync();
        Assert.Equal("pending", await RunAsync(durableWrongId ? "wrong-terminal" : "wrong-reply", "a"));
        var pending = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.LaunchDispatching, pending.Phase);
        var launch = pending.Transition!.Launch!;
        var actualId = ActualInstance();
        Assert.NotNull(actualId);
        Assert.NotEqual(WrongId, actualId);
        Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts(launch.Id));
        var terminalPath = OperationPath("terminals", launch.Id);
        Assert.Equal(durableWrongId ? WrongId : actualId, TerminalInstance(terminalPath));
        var originalTerminal = File.ReadAllBytes(terminalPath);
        var originalResidency = File.ReadAllText(DaemonPath);
        var effectPath = OperationPath("effects", launch.Id);

        // The fake's installed occupancy can independently lie about the instance in the reply.
        ReplaceText(DaemonPath, originalResidency, actualId!, WrongId);
        Assert.Equal(WrongId, ActualInstance());
        Assert.Equal("pending", await RunAsync("normal", "b"));
        Assert.Equal(pending, ReadCheckpoint());
        Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts());
        File.WriteAllText(DaemonPath, originalResidency);
        if (durableWrongId)
        {
            var originalEffect = File.ReadAllText(effectPath);
            // Even matching terminal and effect files cannot override a contradictory resident.
            ReplaceText(effectPath, originalEffect, actualId!, WrongId);
            Assert.Equal(actualId, ActualInstance());
            Assert.Equal("pending", await RunAsync("echo-wrong", "b"));
            Assert.Equal(pending, ReadCheckpoint());
            File.WriteAllText(effectPath, originalEffect);
            Assert.Equal("pending", await RunAsync("corrected-reply", "b"));
            Assert.Equal(pending, ReadCheckpoint());
        }
        else
        {
            File.Move(effectPath, effectPath + ".withheld");
            Assert.Equal("pending", await RunAsync("normal", "b"));
            Assert.Equal(pending, ReadCheckpoint());
            var validEffect = File.ReadAllText(effectPath + ".withheld");
            var staleEffect = validEffect.Replace("\"Enrollment\":\"fixture-enrollment\"",
                "\"Enrollment\":\"stale-enrollment\"", StringComparison.Ordinal);
            Assert.NotEqual(validEffect, staleEffect);
            File.WriteAllText(effectPath, staleEffect);
            Assert.Equal("pending", await RunAsync("normal", "b"));
            Assert.Equal(pending, ReadCheckpoint());
            File.Move(effectPath + ".withheld", effectPath, overwrite: true);
            Assert.Equal("resident:a", await RunAsync("normal", "b"));
            var resident = ReadCheckpoint();
            var applied = Assert.IsType<HostCheckpointLaunchEffect>(resident.AppliedLaunch);
            Assert.Equal(launch, applied.Operation);
            Assert.Equal(actualId, applied.InstanceId);
        }
        Assert.Equal(originalTerminal, File.ReadAllBytes(terminalPath));
        Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts());
    }

    [Theory(Timeout = 45_000)]
    [InlineData("fixture-enrollment", "other-enrollment")]
    [InlineData("fixture-group", "other-group")]
    [InlineData("\"Foreign\":false", "\"Foreign\":true")]
    [InlineData("a-config", "changed-config")]
    public async Task SameInstanceIdCannotCorroborateAForeignOrMismatchedResident(string before, string after)
    {
        await InitializeAsync();
        Assert.Equal("pending", await RunAsync("wrong-reply", "a"));
        var pending = ReadCheckpoint();
        var originalResidency = File.ReadAllText(DaemonPath);
        var instanceId = ActualInstance();
        Assert.NotNull(instanceId);
        ReplaceText(DaemonPath, originalResidency, before, after);
        Assert.Equal(instanceId, ActualInstance());

        Assert.Equal("pending", await RunAsync("normal", "b"));
        Assert.Equal(pending, ReadCheckpoint());
        Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts());

        File.WriteAllText(DaemonPath, originalResidency);
        Assert.Equal("resident:a", await RunAsync("normal", "b"));
        Assert.Equal(instanceId, ReadCheckpoint().AppliedLaunch?.InstanceId);
        Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts());
    }

    [Theory(Timeout = 45_000)]
    [InlineData("accepted", false, 0, 0, false, false)]
    [InlineData("attempt", false, 1, 0, false, false)]
    [InlineData("effect", false, 1, 0, true, false)]
    [InlineData("applied", false, 1, 1, true, false)]
    [InlineData("terminal", false, 1, 1, true, true)]
    [InlineData("accepted", true, 0, 0, true, false)]
    [InlineData("attempt", true, 1, 0, true, false)]
    [InlineData("effect", true, 1, 0, false, false)]
    public async Task AProcessDeathLeavesUnprovedEffectsPending(string barrier, bool stop, int attempts, int effects,
        bool residentExists, bool settles)
    {
        await InitializeAsync();
        if (stop) Assert.Equal("resident:a", await RunAsync("normal", "a"));
        var existing = stop ? 1 : 0;
        var expected = new LedgerCounts(existing + 1, existing + 1, existing + attempts, existing + effects);
        using (var child = Start($"pause-{barrier}", stop ? "b" : "a"))
        {
            try
            {
                Assert.Equal(barrier, await ReadLineAsync(child));
                Assert.Equal(expected, Counts());
                Assert.Equal(residentExists, ActualInstance() is not null);
            }
            finally { await TerminateAsync(child); }
        }

        var pending = ReadCheckpoint();
        Assert.Equal(stop ? HostCheckpointPhase.StopDispatching : HostCheckpointPhase.LaunchDispatching, pending.Phase);
        var transition = pending.Transition!;
        var operation = (stop ? transition.Stop : transition.Launch)!;
        Assert.Equal(settles, File.Exists(OperationPath("terminals", operation.Id)));
        Assert.Equal(settles ? "resident:a" : "pending", await RunAsync("normal", "c"));
        Assert.Equal(expected, Counts());
        if (settles) Assert.Equal(operation, ReadCheckpoint().AppliedLaunch?.Operation);
        else Assert.Equal(pending, ReadCheckpoint());
    }

    [Theory(Timeout = 45_000)]
    [InlineData("append-sends", "sends", 0, 0, 0, 0, false)]
    [InlineData("append-acceptances", "acceptances", 1, 0, 0, 0, false)]
    [InlineData("append-decisions", "decisions", 1, 1, 0, 0, false)]
    [InlineData("append-attempts", "attempts", 1, 1, 0, 0, false)]
    [InlineData("append-residency", "", 1, 1, 1, 0, false)]
    [InlineData("append-effects", "effects", 1, 1, 1, 0, true)]
    [InlineData("append-terminals", "terminals", 1, 1, 1, 1, true)]
    [InlineData("publish-terminals", "terminals", 1, 1, 1, 1, true)]
    public async Task DeathInsideAppendOrBeforePublicationLeavesOnlyInstalledRecords(string barrier, string journal,
        int sends, int acceptances, int attempts, int effects, bool residentExists)
    {
        await InitializeAsync();
        var priorResidency = File.ReadAllBytes(DaemonPath);
        var expected = new LedgerCounts(sends, acceptances, attempts, effects);
        var stagedDirectory = Path.Combine(_directory.FullName, journal);
        string staged;
        using (var child = Start($"pause-{barrier}", "a"))
        {
            try
            {
                Assert.Equal(barrier, await ReadLineAsync(child));
                staged = Assert.Single(Directory.GetFiles(stagedDirectory, "*.json.*.new"));
                Assert.Equal(expected, Counts());
                Assert.Equal(residentExists, ActualInstance() is not null);
            }
            finally { await TerminateAsync(child); }
        }

        if (journal.Length == 0) Assert.Equal(priorResidency, File.ReadAllBytes(DaemonPath));
        else Assert.Empty(Directory.GetFiles(stagedDirectory, "*.json"));
        Assert.True(File.Exists(staged));
        if (barrier == "publish-terminals")
        {
            using var complete = JsonDocument.Parse(File.ReadAllBytes(staged));
            Assert.Equal((int)HostMutationStatus.Applied, complete.RootElement.GetProperty("Outcome").GetProperty("Status").GetInt32());
        }
        else
        {
            Assert.ThrowsAny<JsonException>(() =>
            {
                using var stagedDocument = JsonDocument.Parse(File.ReadAllBytes(staged));
            });
        }
        var pending = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.LaunchDispatching, pending.Phase);
        Assert.Equal("pending", await RunAsync("normal", "b"));
        Assert.Equal(pending, ReadCheckpoint());
        Assert.Equal(expected, Counts());
    }

    [Theory(Timeout = 45_000)]
    [InlineData("acceptances", "{")]
    [InlineData("decisions", "null")]
    [InlineData("effects", "{")]
    [InlineData("terminals", "{")]
    [InlineData("residency", "{\"Resident\":null}")]
    public async Task CorruptInstalledRecordRemainsPendingAndIsNeverTreatedAsAbsent(string journal, string corrupted)
    {
        await InitializeAsync();
        Assert.Equal("pending", await RunAsync("wrong-reply", "a"));
        var pending = ReadCheckpoint();
        var operation = pending.Transition!.Launch!;
        var path = journal == "residency" ? DaemonPath : OperationPath(journal, operation.Id);
        var valid = File.ReadAllBytes(path);
        File.WriteAllText(path, corrupted);

        Assert.Equal("pending", await RunAsync("normal", "b"));
        Assert.Equal(pending, ReadCheckpoint());
        Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts());
        Assert.Equal(corrupted, File.ReadAllText(path));
        File.WriteAllBytes(path, valid);
        Assert.Equal("resident:a", await RunAsync("normal", "b"));
        Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts());
    }

    [Fact(Timeout = 45_000)]
    public async Task LostAppliedStopReplyResumesFrozenBAcrossRestartAndDuplicateOnlyAddsASend()
    {
        await InitializeAsync();
        Assert.Equal("resident:a", await RunAsync("normal", "a"));
        var a = ReadCheckpoint().AppliedLaunch!;
        using (var child = Start("pause-terminal", "b"))
        {
            try
            {
                Assert.Equal("terminal", await ReadLineAsync(child));
                Assert.Equal(new LedgerCounts(2, 2, 2, 2), Counts());
                Assert.Null(ActualInstance());
            }
            finally { await TerminateAsync(child); }
        }

        var pending = ReadCheckpoint();
        var transition = Assert.IsType<HostCheckpointTransition>(pending.Transition);
        var stop = transition.Stop!;
        Assert.Equal(HostCheckpointPhase.StopDispatching, pending.Phase);
        Assert.Equal(a.InstanceId, stop.Target);
        Assert.Equal(a.Operation.Engine, stop.Engine);
        Assert.Equal(a.InstanceId, TerminalInstance(OperationPath("terminals", stop.Id)));
        Assert.Equal("resident:b", await RunAsync("normal", "c"));
        var completed = ReadCheckpoint();
        var b = completed.AppliedLaunch!;
        Assert.Equal(transition.Plan, b.Plan);
        Assert.Equal(stop.Epoch, b.Operation.Epoch);
        Assert.Equal(new HostCheckpointStoppedResident(a, new HostCheckpointStopEffect(stop, a.Operation, a.InstanceId)),
            completed.LastStoppedResident);
        foreach (var operation in new[] { a.Operation, stop, b.Operation })
            Assert.Equal(new LedgerCounts(1, 1, 1, 1), Counts(operation.Id));
        Assert.Equal(new LedgerCounts(3, 3, 3, 3), Counts());

        Assert.Equal("already-issued", await RunAsync("deliver", stop.Id.ToString("D")));
        Assert.Equal(new LedgerCounts(2, 1, 1, 1), Counts(stop.Id));
        Assert.Equal(new LedgerCounts(4, 3, 3, 3), Counts());
        Assert.Equal(completed, ReadCheckpoint());
    }

    [Fact(Timeout = 45_000)]
    public async Task RejectionWinsBeforeDelayedDaemonEntryAndBarsDeliveryDuringFenceAndAfterRestart()
    {
        await InitializeAsync();
        Assert.Equal("resident:a", await RunAsync("normal", "a"));
        var a = ReadCheckpoint().AppliedLaunch!;
        using var controller = Start("pause-accepted", "b");
        Guid stopId;
        try
        {
            Assert.Equal("accepted", await ReadLineAsync(controller));
            stopId = AcceptedStopId();
            Assert.Equal(new LedgerCounts(1, 1, 0, 0), Counts(stopId));
            using var rejection = Start("pause-rejection", stopId.ToString("D"));
            try
            {
                Assert.Equal("rejection", await ReadLineAsync(rejection));
                Assert.Equal("worker-busy", await RunAsync("deliver", stopId.ToString("D")));
                Assert.Equal(new LedgerCounts(2, 1, 0, 0), Counts(stopId));
                Assert.False(File.Exists(OperationPath("decisions", stopId)));
                await rejection.StandardInput.WriteLineAsync("continue");
                Assert.Equal("certified", await ReadExitAsync(rejection));
            }
            finally { await TerminateAsync(rejection); }
            Assert.True(NeverIssue(stopId));
            await controller.StandardInput.WriteLineAsync("continue");
            Assert.Equal("retained", await ReadExitAsync(controller));
        }
        finally { await TerminateAsync(controller); }

        var retained = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.RetainedAfterRejection, retained.Phase);
        Assert.Equal(a, retained.AppliedLaunch);
        Assert.Equal(stopId, retained.Transition!.Stop!.Id);
        Assert.Equal("retained", await RunAsync("normal", "a"));
        Assert.Equal("barred", await RunAsync("deliver", stopId.ToString("D")));
        Assert.Equal(new LedgerCounts(3, 1, 0, 0), Counts(stopId));
        Assert.Equal(new LedgerCounts(4, 2, 1, 1), Counts());
        Assert.Equal(a.InstanceId, ActualInstance());
        Assert.Equal(retained, ReadCheckpoint());
    }

    [Theory(Timeout = 45_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeathAfterNeverIssueRepairsOnlyMissingCanonicalTerminal(bool conflictingTerminal)
    {
        await InitializeAsync();
        Assert.Equal("resident:a", await RunAsync("normal", "a"));
        var a = ReadCheckpoint().AppliedLaunch!;
        using (var controller = Start("pause-accepted", "b"))
        {
            try { Assert.Equal("accepted", await ReadLineAsync(controller)); }
            finally { await TerminateAsync(controller); }
        }

        var pending = ReadCheckpoint();
        var stop = Assert.IsType<HostMutation>(pending.Transition!.Stop);
        using (var rejection = Start("pause-never-issue", stop.Id.ToString("D")))
        {
            try
            {
                Assert.Equal("never-issue", await ReadLineAsync(rejection));
                Assert.True(NeverIssue(stop.Id));
                Assert.False(File.Exists(OperationPath("terminals", stop.Id)));
            }
            finally { await TerminateAsync(rejection); }
        }

        var terminalPath = OperationPath("terminals", stop.Id);
        if (conflictingTerminal) File.Copy(OperationPath("terminals", a.Operation.Id), terminalPath);
        var conflictingBytes = conflictingTerminal ? File.ReadAllBytes(terminalPath) : null;
        Assert.Equal(conflictingTerminal ? "pending" : "retained", await RunAsync("normal", "c"));
        if (conflictingTerminal)
        {
            Assert.Equal(pending, ReadCheckpoint());
            Assert.Equal(conflictingBytes!, File.ReadAllBytes(terminalPath));
        }
        else
        {
            var retained = ReadCheckpoint();
            Assert.Equal(HostCheckpointPhase.RetainedAfterRejection, retained.Phase);
            Assert.Equal(a, retained.AppliedLaunch);
            Assert.Equal(stop, retained.Transition!.Stop);
            Assert.Null(TerminalInstance(terminalPath));
            Assert.Equal("certified", await RunAsync("reject", stop.Id.ToString("D")));
        }
        Assert.Equal(new LedgerCounts(2, 2, 1, 1), Counts());
        Assert.Equal("barred", await RunAsync("deliver", stop.Id.ToString("D")));
        Assert.Equal(new LedgerCounts(2, 1, 0, 0), Counts(stop.Id));
        Assert.Equal(new LedgerCounts(3, 2, 1, 1), Counts());
        Assert.Equal(a.InstanceId, ActualInstance());
    }

    [Theory(Timeout = 45_000)]
    [InlineData("authorized", 0)]
    [InlineData("applied", 1)]
    public async Task IssueWinnerCannotBeCertifiedNeverIssuedAndUnknownStopFencesBAndC(string barrier, int daemonCount)
    {
        await InitializeAsync();
        Assert.Equal("resident:a", await RunAsync("normal", "a"));
        using (var controller = Start($"pause-{barrier}", "b"))
        {
            try
            {
                Assert.Equal(barrier, await ReadLineAsync(controller));
                var id = AcceptedStopId();
                Assert.Equal(daemonCount == 0 ? "worker-busy" : "issue-won", await RunAsync("reject", id.ToString("D")));
                Assert.False(NeverIssue(id));
                Assert.False(File.Exists(OperationPath("terminals", id)));
                Assert.Equal(new LedgerCounts(1, 1, daemonCount, daemonCount), Counts(id));
            }
            finally { await TerminateAsync(controller); }
        }

        var pending = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.StopDispatching, pending.Phase);
        var stop = pending.Transition!.Stop!;
        Assert.Equal("pending", await RunAsync("normal", "b"));
        Assert.Equal("pending", await RunAsync("normal", "c"));
        Assert.Equal("pending", await RunAsync("weak-not-found", "c"));
        Assert.Equal("issue-won", await RunAsync("reject", stop.Id.ToString("D")));
        Assert.Equal(pending, ReadCheckpoint());
        Assert.Equal(new LedgerCounts(2, 2, 1 + daemonCount, 1 + daemonCount), Counts());
        Assert.Equal(new LedgerCounts(1, 1, daemonCount, daemonCount), Counts(stop.Id));
    }

    [Theory(Timeout = 45_000)]
    [InlineData("changed-owner", 0)]
    [InlineData("wrong-terminal", 1)]
    public async Task ChangedOwnerOrContradictoryStopEvidenceCannotAuthorizeSuccessor(string mode, int daemonCount)
    {
        await InitializeAsync();
        Assert.Equal("resident:a", await RunAsync("normal", "a"));
        Assert.Equal("pending", await RunAsync(mode, "b"));
        var pending = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.StopDispatching, pending.Phase);
        var stop = pending.Transition!.Stop!;
        Assert.Equal(new LedgerCounts(1, 1, daemonCount, daemonCount), Counts(stop.Id));
        Assert.Equal("pending", await RunAsync("normal", "c"));
        Assert.Equal(pending, ReadCheckpoint());
        Assert.Equal(new LedgerCounts(2, 2, 1 + daemonCount, 1 + daemonCount), Counts());
    }

    [Fact(Timeout = 45_000)]
    public async Task StaleVacancyRefusesFrozenBAtDaemonEntry()
    {
        await InitializeAsync();
        Assert.Equal("resident:a", await RunAsync("normal", "a"));
        Assert.Equal("pending", await RunAsync("changed-vacancy", "b"));
        var pending = ReadCheckpoint();
        Assert.Equal(HostCheckpointPhase.LaunchDispatching, pending.Phase);
        var launch = pending.Transition!.Launch!;
        Assert.Equal("b", launch.Engine);
        Assert.Equal(new LedgerCounts(1, 1, 0, 0), Counts(launch.Id));
        Assert.Equal("pending", await RunAsync("normal", "c"));
        Assert.Equal(pending, ReadCheckpoint());
        Assert.Equal(new LedgerCounts(3, 3, 2, 2), Counts());
    }

    private async Task InitializeAsync()
    {
        HostEffectCheckpoint.ProvisionNew(CheckpointPath);
        Assert.Equal("initialized", await RunAsync("initialize", "a"));
    }

    private HostEffectCheckpointState ReadCheckpoint()
    {
        using var checkpoint = HostEffectCheckpoint.Open(CheckpointPath);
        return checkpoint.State;
    }

    private LedgerCounts Counts(Guid? id = null) => new(
        Count("sends", id), Count("acceptances", id), Count("attempts", id), Count("effects", id));

    private int Count(string journal, Guid? id)
    {
        var directory = Path.Combine(_directory.FullName, journal);
        return Directory.Exists(directory) ? Directory.GetFiles(directory, id is { } operation ? $"{operation:N}*.json" : "*.json").Length : 0;
    }

    private string OperationPath(string journal, Guid id) => Path.Combine(_directory.FullName, journal, $"{id:N}.json");

    private static void ReplaceText(string path, string original, string before, string after)
    {
        var changed = original.Replace(before, after, StringComparison.Ordinal);
        Assert.NotEqual(original, changed);
        File.WriteAllText(path, changed);
    }

    private string? ActualInstance()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(DaemonPath));
        var resident = document.RootElement.GetProperty("Resident");
        return resident.ValueKind == JsonValueKind.Null ? null : resident.GetProperty("Effect").GetProperty("InstanceId").GetString();
    }

    private static string? TerminalInstance(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("Outcome").GetProperty("ContainerId").GetString();
    }

    private bool NeverIssue(Guid id)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(OperationPath("decisions", id)));
        return document.RootElement.GetProperty("NeverIssue").GetBoolean();
    }

    private Guid AcceptedStopId()
    {
        var ids = new List<Guid>();
        foreach (var path in Directory.GetFiles(Path.Combine(_directory.FullName, "acceptances"), "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var operation = document.RootElement.GetProperty("Operation");
            if (operation.GetProperty("Kind").GetInt32() == (int)HostMutationKind.Stop) ids.Add(operation.GetProperty("Id").GetGuid());
        }
        return Assert.Single(ids);
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
        start.ArgumentList.Add("--effect-ledger");
        start.ArgumentList.Add(CheckpointPath);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(target);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the offline effect probe.");
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
        var line = await ReadLineAsync(process);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, process.ExitCode);
        return line;
    }

    private static async Task TerminateAsync(Process process)
    {
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private readonly record struct LedgerCounts(int Sends, int Acceptances, int Attempts, int Effects);
}
