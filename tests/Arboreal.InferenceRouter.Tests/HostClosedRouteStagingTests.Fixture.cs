using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    private sealed class FakeHost(int readinessTimeoutMs = 1000, bool vacant = false) : IHostLifecycleProvider
    {
        private HostResident? _resident = vacant ? null : new("a", "instance-a");
        internal HostResident? Resident => Volatile.Read(ref _resident);
        private HostCheckpointLaunchEffect? _applied;
        public int Observations { get; private set; }
        public int Verified { get; private set; }
        public int Stops { get; private set; }
        public int Creates { get; private set; }
        public int Disposals { get; private set; }
        public bool Draining { get; private set; }
        public LifecycleResult Preflight { private get; set; } = LifecycleResult.Ok();
        public LifecycleResult Readiness { private get; set; } = LifecycleResult.Ok();
        public Func<Guid, CancellationToken, ValueTask<HostOccupancy>>? OnObserve { private get; set; }
        public Action? OnDrainDispose { private get; set; }
        public Func<LifecycleTarget, HostCheckpointPlan>? FrozenPlan { private get; init; }

        public HostCheckpointLaunchEffect? FindLaunch(HostMutation operation) => _applied?.Operation == operation ? _applied : null;

        public ValueTask<HostOccupancy> ObserveAsync(Guid queryId, CancellationToken ct)
        {
            Observations++;
            if (OnObserve is { } observe) return observe(queryId, ct);
            return ValueTask.FromResult(_resident is null ? new HostOccupancy(queryId, HostOccupancyKind.Vacant)
                : new HostOccupancy(queryId, HostOccupancyKind.Owned, _resident));
        }

        public ValueTask<LifecycleResult> PreflightAsync(LifecycleTarget target, CancellationToken ct) =>
            ValueTask.FromResult(Preflight);

        public string GetLaunchPlanDigest(LifecycleTarget target) => Plan(target).Digest();

        public ValueTask<IHostDrain> CloseAndDrainAsync(HostResident? incumbent, CancellationToken ct)
        {
            Assert.Equal(_resident, incumbent);
            Assert.False(Draining);
            Draining = true;
            Drain? untransferred = null;
            try
            {
                untransferred = new Drain(this);
                var result = ValueTask.FromResult<IHostDrain>(untransferred);
                untransferred = null;
                return result;
            }
            finally { untransferred?.Dispose(); }
        }

        public ValueTask<HostMutationOutcome> StopAsync(HostMutation operation, CancellationToken ct)
        {
            Assert.True(Draining);
            Assert.Equal(new HostResident(operation.Engine, operation.Target), _resident);
            Stops++;
            _resident = null;
            return ValueTask.FromResult(new HostMutationOutcome(operation, HostMutationStatus.Applied, operation.Target));
        }

        public ValueTask<HostMutationOutcome> LaunchAsync(HostMutation operation, LifecycleTarget target, CancellationToken ct)
        {
            Assert.True(Draining);
            Assert.Null(_resident);
            Creates++;
            var instance = $"instance-{operation.Engine}-{operation.Id:N}";
            SetAppliedLaunch(operation, target, instance);
            return ValueTask.FromResult(new HostMutationOutcome(operation, HostMutationStatus.Applied, instance));
        }

        public void SetAppliedLaunch(HostMutation operation, LifecycleTarget target, string instance)
        {
            var plan = Plan(target);
            Assert.True(plan.Matches(operation, operation.Epoch));
            _resident = new HostResident(operation.Engine, instance);
            _applied = new HostCheckpointLaunchEffect(operation, plan, instance);
        }

        public ValueTask<HostMutationOutcome> ReconcileAsync(HostMutation operation, CancellationToken ct) =>
            throw new InvalidOperationException("An already applied launch needs no dispatch reconciliation.");

        public ValueTask<LifecycleResult> VerifyReadyAsync(HostMutation launch, string containerId, CancellationToken ct)
        {
            Assert.True(Draining);
            var applied = Assert.IsType<HostCheckpointLaunchEffect>(_applied);
            Assert.Equal(applied.Operation, launch);
            Assert.Equal(applied.InstanceId, containerId);
            Assert.Equal(new HostResident(launch.Engine, containerId), _resident);
            Verified++;
            return ValueTask.FromResult(Readiness);
        }

        private HostCheckpointPlan Plan(LifecycleTarget target) =>
            FrozenPlan?.Invoke(target) ?? new(target.EngineName, target.Model, target.Profile?.Name ?? target.Definition.Profile,
                target.Definition.Name, readinessTimeoutMs, "fixture-private-route", new string('A', 64));

        private sealed class Drain(FakeHost host) : IHostDrain
        {
            public LifecycleResult Result => LifecycleResult.Ok();
            public void WithdrawIncumbent() => Assert.True(host.Draining);
            public bool CommitVerifiedRoute(HostServingProof proof, Func<bool> publish, Func<Action, bool> tryAccept) =>
                throw new InvalidOperationException("The staged binding must not call legacy immediate commit.");

            public void Dispose()
            {
                host.Disposals++;
                host.Draining = false;
                host.OnDrainDispose?.Invoke();
            }
        }
    }

    private async Task<FakeHost> SeedAppliedDeadlineLaunchAsync(LifecycleTarget target, CancellationToken ct)
    {
        var host = new FakeHost(readinessTimeoutMs: 100)
        {
            Readiness = LifecycleResult.Fail("fixture_seed_saved_launch", "Leave the applied launch available for recovery."),
        };
        HostReservation.ProvisionNew(ReservationPath);
        var seeded = await new HostLifecycleCoordinator(ReservationPath, host).SwitchAsync(target, ct);
        Assert.Equal("fixture_seed_saved_launch", seeded.Reason);
        Assert.Equal(1, host.Stops);
        Assert.Equal(1, host.Creates);
        var saved = ReadReservation();
        Assert.Equal(HostMutationPhase.LaunchApplied, saved.Phase);
        var operation = Assert.IsType<HostMutation>(saved.Pending);
        var launch = Assert.IsType<HostCheckpointLaunchEffect>(host.FindLaunch(operation));
        Assert.True(launch.IsValid());
        Assert.Equal(100, launch.Plan.ReadinessTimeoutMs);
        Assert.Equal(host.GetLaunchPlanDigest(target), operation.PlanDigest);
        Assert.Equal(saved.LaunchedContainerId, launch.InstanceId);
        host.Readiness = LifecycleResult.Ok();
        return host;
    }

    private sealed class FixtureIssuer(HostClosedRouteStaging authority) : IVerifiedInstanceStreamIssuer
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, Exchange> _issued = [];
        private int _permits;
        public Func<VerifiedInstanceGrant, Task>? HoldCheckout { private get; set; }
        public Func<VerifiedInstanceRoute, bool>? AllowsRoute { private get; init; }
        public int Grants { get { lock (_gate) return _issued.Count; } }
        public int Permits { get { lock (_gate) return _permits; } }

        public VerifiedInstanceGrant? Mint(VerifiedInstanceInvocation invocation)
        {
            lock (_gate)
            {
                if (!CanDeliver(invocation.Route) || invocation.Id == Guid.Empty || _issued.ContainsKey(invocation.Id))
                    return null;

                RecordedChannel? untransferred = null;
                try
                {
                    untransferred = new RecordedChannel();
                    var grant = new VerifiedInstanceGrant(invocation, untransferred, untransferred.DisposeAsync);
                    _issued.Add(invocation.Id, new Exchange(grant, untransferred));
                    untransferred = null;
                    return grant;
                }
                finally { untransferred?.Dispose(); }
            }
        }

        public async ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var grant = Mint(invocation);
            if (grant is not null && HoldCheckout is { } hold)
            {
                try { await hold(grant).WaitAsync(ct); }
                catch
                {
                    await grant.CloseAsync();
                    throw;
                }
            }
            return grant;
        }

        public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
        {
            lock (_gate)
            {
                if (!_issued.TryGetValue(grant.Invocation.Id, out var issued) || !ReferenceEquals(issued.Grant, grant) ||
                    issued.Authorized || !CanDeliver(grant.Invocation.Route)) return null;

                issued.Authorized = true;
                _permits++;
                return new FixturePermit();
            }
        }

        private bool CanDeliver(VerifiedInstanceRoute route) =>
            authority.CanMint(route) && (AllowsRoute is null || AllowsRoute(route));

        public byte[] Bytes(VerifiedInstanceInvocation invocation)
        {
            lock (_gate) return _issued.TryGetValue(invocation.Id, out var issued) ? issued.Channel.Bytes : [];
        }

        private sealed class Exchange(VerifiedInstanceGrant grant, RecordedChannel channel)
        {
            public VerifiedInstanceGrant Grant { get; } = grant;
            public RecordedChannel Channel { get; } = channel;
            public bool Authorized { get; set; }
        }

        private sealed class FixturePermit : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordedChannel(byte[]? response = null, Func<ValueTask>? beforeRead = null) : Stream
    {
        private readonly MemoryStream _request = new();
        private readonly MemoryStream _response = new(response ?? "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray());
        public byte[] Bytes => _request.ToArray();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _response.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _response.Read(buffer);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (beforeRead is not null) await beforeRead();
            return await _response.ReadAsync(buffer, ct);
        }
        public override void Write(byte[] buffer, int offset, int count) => _request.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _request.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _request.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _request.Dispose();
                _response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
