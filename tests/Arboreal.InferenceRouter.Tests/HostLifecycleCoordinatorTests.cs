using System.Diagnostics;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed class HostLifecycleCoordinatorTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("air-host-");

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnrolledRuntimePublishesOnlyThroughTheControllersAdmissionGuard(bool admit)
    {
        var path = CreateReservation("shared");
        var host = new FakeHost { AdmitVerifiedRoute = admit };
        var events = new EventRing();
        using var endpoint = new FakeDockerDaemonHandler { TransportFails = true };
        await using var engine = CreateEnrolledRuntime(path, host, events, endpoint);
        var publishedUnderGuard = false;
        engine.AfterSwitchProofForTests = () =>
        {
            Assert.True(host.AdmissionClosed);
            Assert.Throws<IOException>(() =>
            {
                using var unexpected = HostReservation.Open(path);
            });
            publishedUnderGuard = true;
        };

        var result = await engine.WaitForModelAsync("alias", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(admit, result.IsReady);
        Assert.Equal(admit, publishedUnderGuard);
        Assert.Equal(admit, engine.Healthy);
        Assert.Empty(endpoint.Requests);
        Assert.Equal(["observe", "preflight", "drain", "observe", "launch", "ready", "commit"], host.Calls);
        if (admit)
        {
            var proof = Assert.IsType<HostServingProof>(host.CommittedProof);
            Assert.Equal("enrolled.model", proof.Target.Model);
            Assert.Equal("enrolled-1", proof.ContainerId);
            Assert.Equal(1L, proof.Epoch);
            Assert.Equal(["enrolled.model"], engine.LoadedModels);
            Assert.Contains(events.Replay(0), entry => entry.Kind == "engine.switch_completed");
        }
        else
        {
            Assert.Null(host.CommittedProof);
            Assert.DoesNotContain(events.Replay(0), entry => entry.Kind == "engine.switch_completed");
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnrolledRuntimeRefusesForeignAndUnknownOccupancyWithoutFallback(bool foreign)
    {
        var host = new FakeHost { Report = foreign ? HostOccupancyKind.Foreign : HostOccupancyKind.Unknown };
        var events = new EventRing();
        using var endpoint = new FakeDockerDaemonHandler { TransportFails = true };
        await using var engine = CreateEnrolledRuntime(CreateReservation("shared"), host, events, endpoint);

        var result = await engine.WaitForModelAsync("alias", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("launch_failed", result.Detail);
        Assert.False(engine.Healthy);
        Assert.Empty(endpoint.Requests);
        Assert.Equal(["observe"], host.Calls);
        Assert.Contains(events.Replay(0), entry => entry.Kind == "engine.switch_failed" &&
            entry.Reason!.Contains("host_occupancy_unproven", StringComparison.Ordinal));
    }

    [Fact(Timeout = 30_000)]
    public async Task AProcessCrashReleasesTheFileLockButRetainsTheUnsettledEpoch()
    {
        var path = CreateReservation("shared");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var child = Process.Start(CreateReservationProbe(path)) ??
            throw new InvalidOperationException("Could not start the host reservation probe.");
        try
        {
            Assert.Equal("recorded", await child.StandardOutput.ReadLineAsync(timeout.Token));
            Assert.Throws<IOException>(() =>
            {
                using var unexpected = HostReservation.Open(path);
            });
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        HostMutation operation;
        using (var recovered = HostReservation.Open(path))
        {
            Assert.Equal(1L, recovered.State.Epoch);
            Assert.Equal("crashed-owner", recovered.State.Owner);
            operation = Assert.IsType<HostMutation>(recovered.State.Pending);
            Assert.Equal("original.model", operation.Target);
        }

        var host = new FakeHost();
        Assert.Equal("host_operation_unsettled", (await SwitchAsync(path, host, "successor")).Reason);
        Assert.Equal(["reconcile"], host.Calls);

        host.ReportReconciliation = new HostMutationOutcome(operation.Id, HostMutationStatus.Rejected);
        Assert.True((await SwitchAsync(path, host, "successor")).Succeeded);
        Assert.Equal(new HostResident("successor", "successor-2"), host.Resident);
    }

    [Fact(Timeout = 20_000)]
    public async Task HoldsTheHostThroughReadinessWithoutBlockingAnotherHost()
    {
        var path = CreateReservation("shared");
        var host = new FakeHost();
        var otherHost = new FakeHost();
        var enteredReadiness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReadiness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Readiness = async ct =>
        {
            enteredReadiness.SetResult();
            await releaseReadiness.Task.WaitAsync(ct);
        };

        var first = SwitchAsync(path, host, "a").AsTask();
        try
        {
            await enteredReadiness.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var competing = await SwitchAsync(path, host, "b");
            Assert.Equal("host_reservation_unavailable", competing.Reason);
            Assert.Equal(["observe", "preflight", "drain", "observe", "launch", "ready"], host.Calls);
            Assert.Equal("a", host.Resident?.Engine);

            Assert.True((await SwitchAsync(CreateReservation("independent"), otherHost, "c")).Succeeded);
            Assert.Equal("c", otherHost.Resident?.Engine);
        }
        finally
        {
            releaseReadiness.TrySetResult();
            await first;
        }

        Assert.True((await first).Succeeded);
        host.Readiness = _ =>
        {
            Assert.True(host.AdmissionClosed);
            return Task.CompletedTask;
        };
        Assert.True((await SwitchAsync(path, host, "b")).Succeeded);
        Assert.Equal("b", host.Resident?.Engine);
        Assert.Equal("a-1", Assert.Single(host.Stops).Target);
        Assert.False(host.AdmissionClosed);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADelayedMutationFencesTheSuccessorEvenAfterRestart(bool loseStop)
    {
        var path = CreateReservation("shared");
        var host = new FakeHost
        {
            Resident = loseStop ? new HostResident("old-engine", "old-instance") : null,
            LoseResponse = loseStop ? HostMutationKind.Stop : HostMutationKind.Launch,
        };

        var failed = await SwitchAsync(path, host, "a");
        Assert.Equal("host_operation_unsettled", failed.Reason);
        var delayed = Assert.IsType<HostMutation>(host.Delayed);
        Assert.Equal(loseStop ? "old-instance" : "a.model", delayed.Target);
        using (var reservation = HostReservation.Open(path))
        {
            Assert.Equal(delayed, reservation.State.Pending);
            Assert.Equal(1L, reservation.State.Epoch);
        }

        host.Calls.Clear();
        var blocked = await SwitchAsync(path, host, "b");
        Assert.Equal("host_operation_unsettled", blocked.Reason);
        Assert.Equal(["reconcile"], host.Calls);
        Assert.Equal(loseStop ? "old-instance" : null, host.Resident?.ContainerId);
        using (var reservation = HostReservation.Open(path))
        {
            Assert.Equal(1L, reservation.State.Epoch);
            Assert.Equal(delayed, reservation.State.Pending);
        }

        host.ReportReconciliation = new HostMutationOutcome(Guid.Empty, HostMutationStatus.Applied, "unrelated-instance");
        host.Calls.Clear();
        Assert.Equal("host_operation_unsettled", (await SwitchAsync(path, host, "b")).Reason);
        Assert.Equal(["reconcile"], host.Calls);
        host.ReportReconciliation = null;

        host.CompleteDelayed();
        Assert.True((await SwitchAsync(path, host, "b")).Succeeded);
        Assert.Equal(new HostResident("b", "b-2"), host.Resident);
        Assert.Equal(loseStop ? "old-instance" : "a-1", Assert.Single(host.Stops).Target);
        using var settled = HostReservation.Open(path);
        Assert.Equal(new HostReservationState(2, "b", null), settled.State);
    }

    [Fact(Timeout = 20_000)]
    public async Task RestartDuringReadinessReobservesTheLaunchedInstance()
    {
        var path = CreateReservation("shared");
        using var interrupted = new CancellationTokenSource();
        var host = new FakeHost
        {
            Readiness = ct =>
            {
                interrupted.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SwitchAsync(path, host, "a", interrupted.Token).AsTask());

        host.Readiness = null;
        host.Calls.Clear();
        Assert.True((await SwitchAsync(path, host, "b")).Succeeded);
        Assert.Equal(["observe", "preflight", "drain", "preflight", "observe", "stop", "observe", "launch", "ready"], host.Calls);
        Assert.Equal("a-1", Assert.Single(host.Stops).Target);
        Assert.Equal("b", host.Resident?.Engine);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnprovenInitialOccupancyBlocksEvenPreflight(bool foreign)
    {
        var host = new FakeHost { Report = foreign ? HostOccupancyKind.Foreign : HostOccupancyKind.Unknown };

        var refused = await SwitchAsync(CreateReservation("shared"), host, "a");

        Assert.Equal("host_occupancy_unproven", refused.Reason);
        Assert.Equal(["observe"], host.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task MissingImagePreflightPreservesTheIncumbent()
    {
        var incumbent = new HostResident("old-engine", "old-instance");
        var host = new FakeHost
        {
            Resident = incumbent,
            Preflight = LifecycleResult.Fail("image_not_present", "make the target image available"),
        };

        var refused = await SwitchAsync(CreateReservation("shared"), host, "a");

        Assert.Equal("image_not_present", refused.Reason);
        Assert.Equal(incumbent, host.Resident);
        Assert.Equal(["observe", "preflight"], host.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task RevalidatesTheTargetAfterDrainBeforeWithdrawingTheIncumbent()
    {
        var incumbent = new HostResident("old-engine", "old-instance");
        var host = new FakeHost { Resident = incumbent };
        host.OnDrain = _ =>
        {
            host.Preflight = LifecycleResult.Fail("image_not_present", "restore the target image");
            return Task.CompletedTask;
        };

        var refused = await SwitchAsync(CreateReservation("shared"), host, "a");

        Assert.Equal("image_not_present", refused.Reason);
        Assert.Equal(incumbent, host.Resident);
        Assert.False(host.AdmissionClosed);
        Assert.Equal(["observe", "preflight", "drain", "preflight"], host.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task AStaleOwnershipObservationNeverAuthorizesStop()
    {
        var replacement = new HostResident("old-engine", "replacement-instance");
        var host = new FakeHost { Resident = new HostResident("old-engine", "old-instance") };
        host.OnDrain = _ =>
        {
            host.Resident = replacement;
            return Task.CompletedTask;
        };

        var refused = await SwitchAsync(CreateReservation("shared"), host, "a");

        Assert.Equal("host_occupancy_unproven", refused.Reason);
        Assert.Equal(replacement, host.Resident);
        Assert.Equal(["observe", "preflight", "drain", "preflight", "observe"], host.Calls);
        Assert.False(host.AdmissionClosed);
    }

    [Fact(Timeout = 20_000)]
    public async Task StopSuccessAloneCannotAuthorizeLaunch()
    {
        var host = new FakeHost { Resident = new HostResident("old-engine", "old-instance") };
        host.OnStop = () => host.Report = HostOccupancyKind.Unknown;

        var refused = await SwitchAsync(CreateReservation("shared"), host, "a");

        Assert.Equal("host_occupancy_unproven", refused.Reason);
        Assert.Equal(["observe", "preflight", "drain", "preflight", "observe", "stop", "observe"], host.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task AReplayedVacancyObservationCannotAuthorizeLaunch()
    {
        var host = new FakeHost();
        host.OnPreflight = () =>
        {
            host.Report = HostOccupancyKind.Foreign;
            host.ReplayObservation = true;
        };

        var refused = await SwitchAsync(CreateReservation("shared"), host, "a");

        Assert.Equal("host_occupancy_unproven", refused.Reason);
        Assert.Equal(["observe", "preflight", "drain", "observe"], host.Calls);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrCorruptStorageRefusesAllProviderCalls(bool corrupt)
    {
        var path = Path.Combine(_directory.FullName, "reservation");
        if (corrupt) File.WriteAllBytes(path, new byte[64]);
        var host = new FakeHost();

        var refused = await SwitchAsync(path, host, "a");

        Assert.Equal("host_reservation_unavailable", refused.Reason);
        Assert.Empty(host.Calls);
    }

    [Fact(Timeout = 20_000)]
    public async Task TruncatedUsedReservationCannotGrantANewEpoch()
    {
        var path = CreateReservation("shared");
        using (var reservation = HostReservation.Open(path))
        {
            var epoch = reservation.Grant("previous");
            reservation.Begin(epoch, HostMutationKind.Launch, "previous", "original.model");
        }

        Assert.Throws<IOException>(() => HostReservation.ProvisionNew(path));
        File.WriteAllBytes(path, []);
        var host = new FakeHost();

        var refused = await SwitchAsync(path, host, "successor");

        Assert.Equal("host_reservation_unavailable", refused.Reason);
        Assert.Empty(host.Calls);
        Assert.Equal(0L, new FileInfo(path).Length);
    }

    [Fact(Timeout = 20_000)]
    public async Task RefusedStopIntentLeavesIncumbentServingAuthorityIntact()
    {
        var path = CreateReservation("shared");
        // Make persisting the stop intent fail at the record-size limit while the owner record fits.
        var incumbent = new HostResident("old-engine", new string('x', 20 * 1024));
        var host = new FakeHost { Resident = incumbent };

        var refused = await SwitchAsync(path, host, "a");

        Assert.Equal("host_reservation_unavailable", refused.Reason);
        Assert.Equal(incumbent, host.Resident);
        Assert.False(host.IncumbentWithdrawn);
        Assert.False(host.AdmissionClosed);
        Assert.Empty(host.Stops);
        Assert.Equal(["observe", "preflight", "drain", "preflight", "observe"], host.Calls);
        using var reservation = HostReservation.Open(path);
        Assert.Equal(new HostReservationState(1, "a", null), reservation.State);
    }

    [Fact(Timeout = 20_000)]
    public async Task InterruptedWithdrawalLeavesTheExactStopIntentForRecovery()
    {
        var path = CreateReservation("shared");
        var incumbent = new HostResident("old-engine", "old-instance");
        var host = new FakeHost
        {
            Resident = incumbent,
            OnWithdraw = () => throw new InvalidOperationException("interrupted withdrawal"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => SwitchAsync(path, host, "a").AsTask());

        Assert.Equal(incumbent, host.Resident);
        Assert.True(host.IncumbentWithdrawn);
        Assert.Empty(host.Stops);
        using (var reservation = HostReservation.Open(path))
        {
            var stop = Assert.IsType<HostMutation>(reservation.State.Pending);
            Assert.Equal(HostMutationKind.Stop, stop.Kind);
            Assert.Equal(incumbent.Engine, stop.Engine);
            Assert.Equal(incumbent.ContainerId, stop.Target);
        }

        host.Calls.Clear();
        Assert.Equal("host_operation_unsettled", (await SwitchAsync(path, host, "successor")).Reason);
        Assert.Equal(["reconcile"], host.Calls);
    }

    [Fact]
    public void APriorEpochCannotRecordNewMutation()
    {
        var path = CreateReservation("shared");
        long priorEpoch;
        using (var first = HostReservation.Open(path))
        {
            priorEpoch = first.Grant("a");
        }

        using var successor = HostReservation.Open(path);
        Assert.Equal(2L, successor.Grant("b"));
        Assert.Throws<InvalidOperationException>(
            () => successor.Begin(priorEpoch, HostMutationKind.Launch, "a", "a.model"));
        Assert.Throws<InvalidOperationException>(
            () => successor.Begin(2, HostMutationKind.Launch, "a", "a.model"));
        Assert.Null(successor.State.Pending);
    }

    private string CreateReservation(string name)
    {
        var path = Path.Combine(_directory.FullName, name);
        HostReservation.ProvisionNew(path);
        return path;
    }

    private static EngineRuntime CreateEnrolledRuntime(string path, FakeHost host, EventRing events, HttpMessageHandler endpoint)
    {
        var definition = new EngineDefinition
        {
            Name = "enrolled",
            Endpoint = new Uri("http://127.0.0.1/v1"),
            LifecycleOwner = EngineLifecycleOwner.Router,
            Lifecycle = EngineLifecycleKind.Docker,
            Docker = new DockerLifecycleConfig { Image = "fixture", RouterId = "test" },
            Catalog = new Dictionary<string, EngineModelDefinition>
            {
                ["enrolled.model"] = new() { Name = "enrolled.model", Profile = "default", Aliases = ["alias"] },
            },
        };
        var enrollment = new HostEngineEnrollment("test", definition.Name, new HostLifecycleCoordinator(path, host));
        return new EngineRuntime(definition, new RouterConfig(), events, endpoint,
            lifecycle: null, dispatchHint: null, hostEnrollment: enrollment);
    }

    private static ProcessStartInfo CreateReservationProbe(string path)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Arboreal.InferenceRouter.HostReservationProbe.dll"));
        start.ArgumentList.Add(path);
        return start;
    }

    private static ValueTask<LifecycleResult> SwitchAsync(
        string path, FakeHost host, string engine, CancellationToken ct = default)
    {
        var model = $"{engine}.model";
        var target = new LifecycleTarget(engine, model, null, new EngineModelDefinition { Name = model, Profile = "default" });
        var coordinator = new HostLifecycleCoordinator(path, host);
        return coordinator.SwitchAsync(target, ct);
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private sealed class FakeHost : IHostLifecycleProvider
    {
        private readonly Dictionary<Guid, HostMutationOutcome> _terminal = new();

        public List<string> Calls { get; } = [];
        public List<HostMutation> Stops { get; } = [];
        public HostResident? Resident { get; set; }
        public HostOccupancyKind? Report { get; set; }
        public HostMutationOutcome? ReportReconciliation { get; set; }
        public HostMutationKind? LoseResponse { get; set; }
        public HostMutation? Delayed { get; private set; }
        public bool AdmissionClosed { get; private set; }
        public bool ReplayObservation { get; set; }
        public Action? OnPreflight { get; set; }
        public Func<CancellationToken, Task>? OnDrain { get; set; }
        public Action? OnStop { get; set; }
        public Func<CancellationToken, Task>? Readiness { get; set; }
        public LifecycleResult Preflight { get; set; } = LifecycleResult.Ok();
        public bool AdmitVerifiedRoute { get; init; } = true;
        public HostServingProof? CommittedProof { get; private set; }
        public bool IncumbentWithdrawn { get; private set; }
        public Action? OnWithdraw { get; init; }
        private HostOccupancy LastObservation { get; set; }

        public ValueTask<HostOccupancy> ObserveAsync(Guid queryId, CancellationToken ct)
        {
            Calls.Add("observe");
            if (!ReplayObservation)
            {
                LastObservation = Report is { } report ? new HostOccupancy(queryId, report) : Resident is { } resident
                    ? new HostOccupancy(queryId, HostOccupancyKind.Owned, resident) : new HostOccupancy(queryId, HostOccupancyKind.Vacant);
            }
            return ValueTask.FromResult(LastObservation);
        }

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct)
        {
            Calls.Add("preflight");
            OnPreflight?.Invoke();
            return ValueTask.FromResult(Preflight);
        }

        public async ValueTask<IHostDrain> CloseAndDrainAsync(HostResident? incumbent, CancellationToken ct)
        {
            Calls.Add("drain");
            Assert.Equal(Resident, incumbent);
            Assert.False(AdmissionClosed);
            AdmissionClosed = true;
            IncumbentWithdrawn = false;
            try
            {
                if (OnDrain is { } drain) await drain(ct);
                return new DrainGuard(this);
            }
            catch
            {
                AdmissionClosed = false;
                throw;
            }
        }

        public ValueTask<HostMutationOutcome> StopAsync(HostMutation operation, CancellationToken ct)
        {
            Calls.Add("stop");
            Assert.True(AdmissionClosed);
            Assert.True(IncumbentWithdrawn);
            Stops.Add(operation);
            return ValueTask.FromResult(Mutate(operation));
        }

        public ValueTask<HostMutationOutcome> LaunchAsync(HostMutation operation, LifecycleTarget target, CancellationToken ct)
        {
            Calls.Add("launch");
            Assert.True(AdmissionClosed);
            Assert.Equal(target.EngineName, operation.Engine);
            Assert.Equal(target.Model, operation.Target);
            Assert.Equal("default", operation.Profile);
            return ValueTask.FromResult(Mutate(operation));
        }

        public ValueTask<HostMutationOutcome> ReconcileAsync(HostMutation operation, CancellationToken ct)
        {
            Calls.Add("reconcile");
            return ValueTask.FromResult(ReportReconciliation ?? _terminal.GetValueOrDefault(operation.Id));
        }

        public async ValueTask<LifecycleResult> VerifyReadyAsync(LifecycleTarget target, string containerId, CancellationToken ct)
        {
            Calls.Add("ready");
            Assert.True(AdmissionClosed);
            Assert.Equal(new HostResident(target.EngineName, containerId), Resident);
            if (Readiness is { } ready) await ready(ct);
            return LifecycleResult.Ok();
        }

        public void CompleteDelayed()
        {
            var pending = Assert.IsType<HostMutation>(Delayed);
            Apply(pending);
            Delayed = null;
        }

        private HostMutationOutcome Mutate(HostMutation operation)
        {
            Assert.Null(Delayed);
            if (operation.Kind != LoseResponse) return Apply(operation);

            LoseResponse = null;
            Delayed = operation;
            return default;
        }

        private HostMutationOutcome Apply(HostMutation operation)
        {
            HostMutationOutcome outcome;
            if (operation.Kind == HostMutationKind.Stop)
            {
                Assert.Equal(new HostResident(operation.Engine, operation.Target), Resident);
                Resident = null;
                OnStop?.Invoke();
                outcome = new HostMutationOutcome(operation.Id, HostMutationStatus.Applied);
            }
            else
            {
                Assert.Null(Resident);
                var containerId = $"{operation.Engine}-{operation.Epoch}";
                Resident = new HostResident(operation.Engine, containerId);
                outcome = new HostMutationOutcome(operation.Id, HostMutationStatus.Applied, containerId);
            }
            _terminal.Add(operation.Id, outcome);
            return outcome;
        }

        private sealed class DrainGuard(FakeHost host) : IHostDrain
        {
            public LifecycleResult Result { get; } = LifecycleResult.Ok();

            public void WithdrawIncumbent()
            {
                host.IncumbentWithdrawn = true;
                host.OnWithdraw?.Invoke();
            }

            public bool CommitVerifiedRoute(HostServingProof proof, Func<bool> publish)
            {
                host.Calls.Add("commit");
                Assert.True(host.AdmissionClosed);
                Assert.Equal(new HostResident(proof.Target.EngineName, proof.ContainerId), host.Resident);
                if (!host.AdmitVerifiedRoute || !publish()) return false;

                host.CommittedProof = proof;
                return true;
            }

            public void Dispose() => host.AdmissionClosed = false;
        }
    }
}
