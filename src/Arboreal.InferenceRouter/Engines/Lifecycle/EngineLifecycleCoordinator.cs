namespace Arboreal.InferenceRouter.Lifecycle;

/// <summary>Runs one lifecycle transaction in the order that makes it safe: prove ownership, validate,
/// then mutate. It is the only place that ordering lives, which is what lets a provider be swapped
/// (a delegated selector program, the Docker Engine API, a fake in a test) without changing when the
/// router is willing to stop or start something.</summary>
/// <remarks>
/// Ordering that is deliberately <em>not</em> here yet: closing admission to the engine and draining
/// the cohort before mutating. Today a switch is started by an admitted request that counts as inflight
/// and then waits on the switch's own readiness, so draining to zero would wait on the request that
/// asked for the switch. That needs the scheduler to reserve the switch's ticket before the transaction
/// starts, and it is tracked as its own task rather than half-implemented here.
/// </remarks>
public sealed class EngineLifecycleCoordinator : IDisposable
{
    private readonly IEngineLifecycleProvider _provider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _journal = new();

    public EngineLifecycleCoordinator(IEngineLifecycleProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string Kind => _provider.Kind;

    /// <summary>Most recent journal lines, oldest first. Advisory: it explains to an operator why a
    /// switch was refused, and it is not part of the wire contract.</summary>
    public IReadOnlyList<string> Journal
    {
        get
        {
            lock (_journal)
            {
                return [.. _journal];
            }
        }
    }

    public ReadinessPolicy Readiness(LifecycleTarget target) => _provider.Readiness(target);

    public async ValueTask<LifecycleResult> SwitchAsync(LifecycleTarget target, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. Ownership proof first: everything below is decided by what we are allowed to touch.
            var peers = await _provider.OwnedPeersAsync(target, ct).ConfigureAwait(false);
            var ours = new List<EnginePeer>(peers.Count);
            foreach (var peer in peers)
            {
                if (peer.OwnedByUs) ours.Add(peer);
            }
            Record($"ownership:{target.EngineName} ours={ours.Count} seen={peers.Count}");

            // 2. Preflight before any mutation, so a refusal costs the engine nothing it had.
            var preflight = await _provider.PreflightAsync(target, ct).ConfigureAwait(false);
            if (!preflight.Succeeded)
            {
                Record($"preflight_refused:{preflight.Reason}");
                return preflight;
            }
            Record("preflight:ok");

            // 3. Stop only what the ownership proof claims. An unowned peer never reaches the provider,
            // so a provider that reports foreign containers cannot be talked into stopping them.
            if (ours.Count > 0)
            {
                var stopped = await _provider.StopAsync(target, ours, ct).ConfigureAwait(false);
                if (!stopped.Succeeded)
                {
                    Record($"stop_failed:{stopped.Reason}");
                    return stopped;
                }
                Record($"stopped:{ours.Count}");
            }

            // 4. Launch the exact target. Readiness is proved by whoever owns the engine's health probe
            // (the runtime), not here: this step only claims the launch was accepted.
            var launched = await _provider.LaunchAsync(target, ct).ConfigureAwait(false);
            if (!launched.Succeeded)
            {
                Record($"launch_failed:{launched.Reason}");
                return launched;
            }
            Record($"launched:{target.Model}");
            return launched;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private void Record(string line)
    {
        lock (_journal)
        {
            _journal.Enqueue(line);
            while (_journal.Count > 24) _journal.Dequeue();
        }
    }
}
