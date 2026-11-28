using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

// One router's admission boundary for engines sharing an operator-declared resource group.
// It does not claim exclusion over unrelated processes or other Docker clients.
internal sealed class LocalLifecycleGroup(string name, Action wake)
{
    private readonly Action _wake = wake;
    private readonly object _gate = new();
    private readonly Dictionary<string, EngineRuntime> _engines = new(StringComparer.Ordinal);
    private readonly HashSet<Lease> _uses = [];
    private QueuedRequest? _draining;
    private QueuedRequest? _waiting;
    private Task<EngineRuntime.ObservedSwitchResult>? _switch;
    private (string Engine, string Model)? _resident;
    private long _revision;
    internal string Name => name;
    internal long Revision => Volatile.Read(ref _revision);

    internal bool IsIdle
    {
        get { lock (_gate) return _uses.Count == 0 && _switch is not { IsCompleted: false }; }
    }

    internal void Attach(EngineRuntime engine) => _engines.Add(engine.Name, engine);

    internal bool IsResident(string engine, string model)
    {
        lock (_gate) return _resident is { } resident && resident.Engine == engine && resident.Model == model;
    }

    internal enum Availability { Warm, Cold, Draining, Owned, Switching }

    internal Availability Probe(QueuedRequest request, EngineRuntime engine, ServingProfile? profile, bool switchRequired)
    {
        lock (_gate) return ProbeUnderGate(request, engine, profile, switchRequired);
    }

    private Availability ProbeUnderGate(QueuedRequest request, EngineRuntime engine, ServingProfile? profile, bool switchRequired)
    {
        if (_switch is { IsCompleted: false }) return Availability.Switching;
        if ((_waiting is not null && !ReferenceEquals(_waiting, request)) ||
            (_draining is not null && !ReferenceEquals(_draining, request))) return Availability.Owned;

        // The measured window and lanes, rather than an inferred tier label, establish resident fit.
        var cold = switchRequired || _resident is not { } resident || resident.Engine != engine.Name ||
            resident.Model != request.Ticket.Model ||
            (profile is not null && (request.Request.Options?.Profile is not null
                ? !engine.MatchesNamedResident(request.Ticket.Model, profile, engine.Capability)
                : !engine.Capability.Serving.Matches(request.Ticket.Model, profile)));
        if (!cold) return Availability.Warm;
        return _uses.Count == 0 ? Availability.Cold : Availability.Draining;
    }

    // Drain ownership is an exact queued intent and is established only after ready routes were tried.
    internal bool TryStartDrain(QueuedRequest request, EngineRuntime engine, ServingProfile? profile, bool switchRequired,
                                long expectedRevision)
    {
        lock (_gate)
        {
            if (_revision != expectedRevision || ProbeUnderGate(request, engine, profile, switchRequired) != Availability.Draining)
                return false;
            _draining = request;
            Interlocked.Increment(ref _revision);
            return true;
        }
    }

    internal void RetireDrain(RouterTicket ticket)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_draining?.Ticket, ticket)) return;
            _draining = null;
            Interlocked.Increment(ref _revision);
        }
        _wake();
    }

    internal Lease? TryReserve(QueuedRequest request, EngineRuntime engine, ServingProfile? profile, bool switchRequired,
                                OwnerSwitchBudget budget, long expectedRevision)
    {
        lock (_gate)
        {
            if (_revision != expectedRevision) return null;
            var available = ProbeUnderGate(request, engine, profile, switchRequired);
            if (available is not (Availability.Warm or Availability.Cold)) return null;
            var cold = available == Availability.Cold;
            var lease = new Lease(this, request, engine, profile, cold, budget);
            _uses.Add(lease);
            if (ReferenceEquals(_draining, request)) _draining = null;
            if (cold) _waiting = request;
            Interlocked.Increment(ref _revision);
            return lease;
        }
    }

    private Task<EngineRuntime.ObservedSwitchResult> Prepare(Lease owner, CancellationToken stop)
    {
        lock (_gate)
        {
            if (!_uses.Contains(owner) || !owner.Cold || !ReferenceEquals(_waiting, owner.Request))
                throw new InvalidOperationException("The original request no longer owns this local switch.");
            stop.ThrowIfCancellationRequested();
            owner.ThrowIfCancelled();
            if (owner.Budget.IsExpired)
                return Task.FromResult(new EngineRuntime.ObservedSwitchResult(new(EngineRuntime.SwitchOutcome.TimedOut, null)));
            if (owner.Request.Ticket.IsTerminal)
                throw new OperationCanceledException("The switch owner has already ended.");
            return _switch ??= RunAsync(owner, stop);
        }
    }

    private async Task<EngineRuntime.ObservedSwitchResult> RunAsync(Lease owner, CancellationToken stop)
    {
        // Leave the short admission gate before provider I/O or readiness publication.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop, owner.Request.CallerToken, owner.Execution);
        lifetime.CancelAfter(owner.Budget.Remaining);
        owner.MutationToken = lifetime.Token;
        await Task.Yield();
        var succeeded = false;
        try
        {
            var engine = owner.Engine;
            var target = engine.LocalTarget(owner.Request.Ticket.Model, owner.Profile);
            var provider = engine.LocalLifecycle;
            var inventories = new List<(EngineRuntime Engine, IReadOnlyList<EnginePeer> Peers)>();
            foreach (var peer in _engines.Values)
            {
                var peers = peer.LocalLifecycle is DockerLifecycleProvider docker
                    ? await docker.ReadLocalInventoryAsync(lifetime.Token).ConfigureAwait(false)
                    : await peer.LocalLifecycle.OwnedPeersAsync(target, lifetime.Token).ConfigureAwait(false);
                if (peers.Any(static value => value.QueryFailed || !value.OwnedByUs))
                    throw new InvalidOperationException("The local group could not establish container ownership.");
                inventories.Add((peer, peers));
            }
            await PreflightAsync(provider, target, lifetime.Token).ConfigureAwait(false);
            if (!owner.CanRecover)
                throw new InvalidOperationException("The request ended before group inventory and preflight completed.");
            for (var i = 0; i < inventories.Count; i++)
            {
                var (member, peers) = inventories[i];
                if (member.LocalLifecycle is not DockerLifecycleProvider native) continue;
                var settled = await native.RecoverLocalInventoryAsync(peers, () => owner.CanRecover, lifetime.Token).ConfigureAwait(false);
                if (settled.Any(value => value.QueryFailed || !value.OwnedByUs))
                    throw new InvalidOperationException("The local group could not reconcile the previous Docker operation.");
                inventories[i] = (member, settled);
            }
            foreach (var (member, prior) in inventories)
            {
                if (member.LocalLifecycle is not DockerLifecycleProvider native) continue;
                var current = await native.ReadLocalInventoryAsync(lifetime.Token).ConfigureAwait(false);
                if (!DockerLifecycleProvider.SameLocalInventory(prior, current))
                    throw new InvalidOperationException("The local group changed during reconciliation.");
            }
            if (_resident is null)
            {
                var active = inventories.SelectMany(item => item.Peers
                    .Where(value => value.ContainerState is not ("exited" or "created"))
                    .Select(value => (item.Engine, Peer: value))).ToArray();
                if (active.Length > 1 || active.Any(item => item.Peer.ContainerState != "running" ||
                    string.IsNullOrEmpty(item.Peer.Model) || item.Peer.EffectiveProfile is null))
                    throw new InvalidOperationException("Cannot identify one running resident in the local group.");
                if (provider is DockerLifecycleProvider && active is [var incumbent] && ReferenceEquals(incumbent.Engine, engine) &&
                    incumbent.Peer.Model == target.Model && incumbent.Peer.EffectiveProfile == target.EffectiveProfile)
                    owner.Adopt = incumbent.Peer;
            }
            foreach (var (peer, peers) in inventories)
            {
                if (ReferenceEquals(peer, engine)) continue; // the existing target transaction handles its own peers
                if (owner.Adopt is not null) continue;
                lifetime.Token.ThrowIfCancellationRequested();
                if (!owner.CanRecover) throw new InvalidOperationException("The switch owner ended before peer stop.");
                var stopped = peer.LocalLifecycle is DockerLifecycleProvider native
                    ? await native.StopLocalPeersAsync(peers, owner, lifetime.Token).ConfigureAwait(false)
                    : await peer.LocalLifecycle.StopAsync(target, peers, lifetime.Token).ConfigureAwait(false);
                if (!stopped.Succeeded) throw new InvalidOperationException(stopped.ToString());
                peer.WithdrawLocalResident();
            }
            lifetime.Token.ThrowIfCancellationRequested();
            engine.WithdrawLocalResident();
            var observed = await engine.EnsureLoadedAsync(owner.Request.Ticket.Model, owner.Profile,
                Timeout.InfiniteTimeSpan, queue: null, lifetime.Token,
                explicitContextMinimum: owner.Request.ExplicitContextMinimum, localOwner: owner,
                exactProfile: owner.Request.Request.Options?.Profile is not null).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            succeeded = observed.Result.IsReady;
            return observed;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested && !stop.IsCancellationRequested)
        {
            owner.ThrowIfCancelled();
            return new(new(EngineRuntime.SwitchOutcome.TimedOut, null));
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException)
        {
            owner.ThrowIfCancelled();
            owner.Engine.Emit("engine.switch_failed", model: owner.Request.Ticket.Model, reason: error.Message);
            return new(EngineRuntime.SwitchResult.Failed(SwitchReason.LaunchFailed));
        }
        finally
        {
            lock (_gate)
            {
                _resident = succeeded ? (owner.Engine.Name, owner.Request.Ticket.Model) : null;
                _waiting = null;
                _switch = null;
                Interlocked.Increment(ref _revision);
            }
            _wake();
        }
    }

    private static async ValueTask PreflightAsync(IEngineLifecycleProvider provider, LifecycleTarget target, CancellationToken stop)
    {
        var result = await provider.PreflightAsync(target, stop).ConfigureAwait(false);
        if (!result.Succeeded) throw new InvalidOperationException(result.ToString());
    }

    internal sealed class Lease(LocalLifecycleGroup group, QueuedRequest request, EngineRuntime engine,
        ServingProfile? profile, bool cold, OwnerSwitchBudget budget)
    {
        internal QueuedRequest Request => request;
        internal EngineRuntime Engine => engine;
        internal ServingProfile? Profile => profile;
        internal EnginePeer? Adopt { get; set; }
        internal bool Cold => cold;
        internal OwnerSwitchBudget Budget => budget;
        internal CancellationToken Execution { get; private set; }
        internal CancellationToken MutationToken { get; set; }
        internal Task<EngineRuntime.ObservedSwitchResult> PrepareAsync(CancellationToken execution, CancellationToken stop)
        {
            Execution = execution;
            return group.Prepare(this, stop);
        }

        internal void ThrowIfCancelled()
        {
            Request.CallerToken.ThrowIfCancellationRequested();
            Execution.ThrowIfCancellationRequested();
        }

        internal bool CanRecover => !Budget.IsExpired && !Request.Ticket.IsTerminal &&
            !Request.CallerToken.IsCancellationRequested && !MutationToken.IsCancellationRequested;

        internal async ValueTask<bool> OtherEnginesVacantAsync(LifecycleTarget target, CancellationToken ct)
        {
            foreach (var peer in group._engines.Values)
            {
                if (ReferenceEquals(peer, Engine)) continue;
                var inventory = await peer.LocalLifecycle.OwnedPeersAsync(target, ct).ConfigureAwait(false);
                if (inventory.Any(value => value.QueryFailed || !value.OwnedByUs || value.ContainerState is not ("exited" or "created")))
                    return false;
            }
            return true;
        }

        // This is an admission charge, retired only by its conclusive transport cleanup.
        internal void Release()
        {
            lock (group._gate)
            {
                if (!group._uses.Remove(this)) return;
                if (ReferenceEquals(group._waiting, request) && group._switch is null) group._waiting = null;
                if (group._switch is { IsCompleted: true }) group._switch = null;
                Interlocked.Increment(ref group._revision);
            }
            group._wake();
        }
    }
}

internal readonly struct OwnerSwitchBudget(TimeProvider clock, long origin, int milliseconds)
{
    internal bool IsExpired => milliseconds > 0 && Remaining == TimeSpan.Zero;
    internal TimeSpan Remaining => milliseconds <= 0 ? Timeout.InfiniteTimeSpan :
        TimeSpan.FromTicks(Math.Max(0, TimeSpan.FromMilliseconds(milliseconds).Ticks - clock.GetElapsedTime(origin).Ticks));
}
