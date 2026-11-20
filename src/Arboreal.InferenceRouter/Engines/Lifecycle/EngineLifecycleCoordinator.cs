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
    private readonly bool _ownsProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _journal = new();

    /// <param name="ownsProvider">True when the coordinator created the provider and must close its
    /// connection with itself; false when the caller injected it and stays responsible for it.</param>
    public EngineLifecycleCoordinator(IEngineLifecycleProvider provider, bool ownsProvider = false)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _ownsProvider = ownsProvider;
    }

    public string Kind => _provider.Kind;
    internal DockerLifecycleProvider? NativeProvider => _provider as DockerLifecycleProvider;

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

    internal readonly record struct SwitchReport(LifecycleResult Result, bool RefusedBeforeMutation);

    public async ValueTask<LifecycleResult> SwitchAsync(LifecycleTarget target, CancellationToken ct) =>
        (await SwitchWithReportAsync(target, ct).ConfigureAwait(false)).Result;

    internal ValueTask<SwitchReport> SwitchWithReportAsync(LifecycleTarget target, CancellationToken ct) =>
        SwitchWithReportAsync(target, timing: null, incumbent: null, ct);

    internal async ValueTask<SwitchReport> SwitchWithReportAsync(
        LifecycleTarget target, SwitchAttempt? timing, SwitchServingProof? incumbent, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var native = NativeProvider;
        var nativeEntered = false;
        try
        {
            if (native is not null)
            {
                await native.EnterOperationAsync(ct).ConfigureAwait(false);
                nativeEntered = true;
            }
            // 1. Ownership proof first: everything below is decided by what we are allowed to touch.
            timing?.TryStartStage(SwitchStage.Ownership);
            var peers = native is not null
                ? await native.OwnedPeersUnderGateAsync(ct).ConfigureAwait(false)
                : await _provider.OwnedPeersAsync(target, ct).ConfigureAwait(false);
            var ours = new List<EnginePeer>(peers.Count);
            foreach (var peer in peers)
            {
                if (peer.OwnedByUs) ours.Add(peer);
            }
            Record($"ownership:{target.EngineName} ours={ours.Count} seen={peers.Count}");

            // An ownership query that failed is not an ownership proof. Without this, "I could not ask"
            // would look like "there is nothing to stop", and the switch would start a second copy of a
            // model that is already running.
            foreach (var peer in peers)
            {
                if (peer.QueryFailed)
                {
                    timing?.TryCompleteStage(SwitchStage.Ownership, succeeded: false);
                    return new SwitchReport(LifecycleResult.Fail(
                        $"ownership_query_failed {peer.Id}",
                        "the router could not prove which containers it already owns, so it changed nothing: fix the daemon connection and switch again"),
                        RefusedBeforeMutation: true);
                }
            }
            timing?.TryCompleteStage(SwitchStage.Ownership, succeeded: true);

            // 2. Preflight before any mutation, so a refusal costs the engine nothing it had.
            timing?.TryStartStage(SwitchStage.Preflight);
            var preflight = await _provider.PreflightAsync(target, ct).ConfigureAwait(false);
            timing?.TryCompleteStage(SwitchStage.Preflight, preflight.Succeeded);
            if (!preflight.Succeeded)
            {
                Record($"preflight_refused:{preflight.Reason}");
                return new SwitchReport(preflight, RefusedBeforeMutation: true);
            }
            Record("preflight:ok");

            // 3. Stop only what the ownership proof claims. An unowned peer never reaches the provider,
            // so a provider that reports foreign containers cannot be talked into stopping them.
            if (ours.Count > 0)
            {
                timing?.TryStartStage(SwitchStage.Shutdown);
                var stopped = native is not null
                    ? await native.StopUnderGateAsync(ours, timing is null ? null : timing.ObserveNativeStop, ct).ConfigureAwait(false)
                    : await _provider.StopAsync(target, ours, ct).ConfigureAwait(false);
                timing?.TryCompleteShutdown(stopped.Succeeded, VerifiedShutdown(stopped, incumbent), stopped.NativeStops);
                if (!stopped.Succeeded)
                {
                    Record($"stop_failed:{stopped.Reason}");
                    return new SwitchReport(stopped, RefusedBeforeMutation: false);
                }
                Record($"stopped:{ours.Count}");
            }

            // 4. Launch the exact target. Readiness is proved by whoever owns the engine's health probe
            // (the runtime), not here: this step only claims the launch was accepted.
            var delegated = string.Equals(_provider.Kind, DelegatedExecProvider.ProviderKind, StringComparison.Ordinal);
            timing?.TryStartStage(delegated ? SwitchStage.DelegatedCommand : SwitchStage.Launch);
            var launched = native is not null
                ? await native.LaunchUnderGateAsync(target, ct).ConfigureAwait(false)
                : await _provider.LaunchAsync(target, ct).ConfigureAwait(false);
            if (delegated) timing?.TryCompleteStage(SwitchStage.DelegatedCommand, launched.Succeeded);
            else timing?.TryCompleteNativeLaunch(launched.Succeeded, launched.NativeLaunch);
            if (!launched.Succeeded)
            {
                Record($"launch_failed:{launched.Reason}");
                return new SwitchReport(launched, RefusedBeforeMutation: false);
            }
            Record($"launched:{target.Model}");
            return new SwitchReport(launched, RefusedBeforeMutation: false);
        }
        finally
        {
            if (nativeEntered) native!.ExitOperation();
            _gate.Release();
        }
    }

    private static SwitchSampleKey? VerifiedShutdown(LifecycleResult result, SwitchServingProof? incumbent)
    {
        if (!result.Succeeded || result.NativeStops.Length != 1 ||
            incumbent is not { NativeLaunch: { } launch })
            return null;
        var actual = result.NativeStops[0];
        return actual.Stop == NativeStopDisposition.Stopped &&
            (actual.Removal is NativeRemovalDisposition.NotRequested or NativeRemovalDisposition.Removed) &&
            launch.Matches(actual.Peer) && launch.Matches(incumbent.Target)
            ? incumbent.Target
            : null;
    }

    // IDISP007 assumes an injected dependency stays the caller's; here the coordinator may have created
    // the provider itself (EngineRuntime does), and _owns_provider is exactly that distinction.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness",
        "IDISP007:Don't dispose injected",
        Justification = "The provider is disposed only when the coordinator constructed it (_ownsProvider).")]
    public void Dispose()
    {
        // The gate is deliberately **not** disposed. A lifecycle transaction still running holds it, and the
        // release in its finally then answers ObjectDisposedException — which the switch can only report as a
        // failure it does not understand ("cannot access a disposed object"), a shutting-down router described
        // as a broken engine. Cancellation on the transaction's own token is what stops a waiting switch; the
        // semaphore is collected with the runtime. Same reasoning as the engine's switch lock.
        if (_ownsProvider) (_provider as IDisposable)?.Dispose();   // only what we created
    }

    private void Record(string line)
    {
        lock (_journal)
        {
            _journal.Enqueue(line);
            while (_journal.Count > 24) _journal.Dequeue();
        }
    }
}
