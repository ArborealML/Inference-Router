namespace Arboreal.InferenceRouter.Lifecycle;

public sealed partial class DockerLifecycleProvider
{
    internal async ValueTask<IReadOnlyList<EnginePeer>> ReadLocalInventoryAsync(CancellationToken ct)
    {
        await EnterOperationAsync(ct).ConfigureAwait(false);
        try { return await ReadOwnedPeersAsync(null, ct).ConfigureAwait(false); }
        finally { ExitOperation(); }
    }

    internal ValueTask<IReadOnlyList<EnginePeer>> ReadOwnedLocalPeersUnderGateAsync(CancellationToken ct) =>
        ReadOwnedPeersAsync(null, ct);

    internal static bool SameLocalInventory(IReadOnlyList<EnginePeer> expected, IReadOnlyList<EnginePeer> current) =>
        current.Count == expected.Count && current.All(peer => peer.OwnedByUs && !peer.QueryFailed && expected.Contains(peer));

    internal async ValueTask<LifecycleResult> StopLocalPeersAsync(IReadOnlyList<EnginePeer> peers,
        LocalLifecycleGroup.Lease owner, CancellationToken ct)
    {
        await EnterOperationAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await ReadOwnedPeersAsync(null, ct).ConfigureAwait(false);
            if (!SameLocalInventory(peers, current))
                return LifecycleResult.Fail("group_changed", "the owned peer inventory changed before stop");
            return await StopUnderGateAsync(current, null, ct, owner).ConfigureAwait(false);
        }
        finally { ExitOperation(); }
    }

    internal async ValueTask<IReadOnlyList<EnginePeer>> RecoverLocalInventoryAsync(
        IReadOnlyList<EnginePeer> inventory, Func<bool> ownerActive, CancellationToken ct)
    {
        await EnterOperationAsync(ct).ConfigureAwait(false);
        try
        {
            return _pendingLocalLaunch is null && _pendingPeerWrite is null ? inventory :
                await ReconcileLocalWriteAsync(ownerActive, ct).ConfigureAwait(false);
        }
        finally { ExitOperation(); }
    }

    // A request may retire only the exact object of the abandoned write. Check both
    // inspect and inventory after each clean-up write, including an acknowledged DELETE.
    private async ValueTask<IReadOnlyList<EnginePeer>> ReconcileLocalWriteAsync(Func<bool>? ownerActive,
        CancellationToken ct)
    {
        var launch = _pendingLocalLaunch;
        var peerWrite = _pendingPeerWrite;
        var locator = launch?.Locator ?? peerWrite!.Value.ContainerId;
        var path = $"{_cfg.ApiVersion}/containers/{Uri.EscapeDataString(locator)}";
        for (var attempt = 0; attempt <= 2; attempt++)
        {
            var inspect = await CallAsync(HttpMethod.Get, path + "/json", null, ct,
                already: [404], requiredStatus: 200).ConfigureAwait(false);
            if (!inspect.Ok) break;
            DockerObservationResponse? observed = null;
            if (inspect.Status == 200)
            {
                observed = TryRead(inspect.Body, DockerJsonContext.Default.DockerObservationResponse);
                if (observed is null || !LocalWriteMatches(observed, locator, launch)) break;
            }
            var peers = await ReadOwnedPeersAsync(observed is null && launch is not { Starting: false } ? locator : null, ct)
                .ConfigureAwait(false);
            if (peers.Any(peer => peer.QueryFailed || !peer.OwnedByUs) ||
                (observed is not null && !peers.Any(peer => peer.Id == observed.Id &&
                    peer.ContainerState == observed.State!.Status && (launch is null ||
                        peer.Model == launch.Model && peer.EffectiveProfile == launch.Profile && peer.ImageId == launch.Image))))
                return [FailedPeer("native_local_inventory_unconfirmed")];

            var state = observed?.State;
            var running = state is { Status: "running", Running: true };
            var inactive = state is { Status: "created" or "exited", Running: false };
            // A lost Create has no continuation that can issue Start. If Docker creates
            // it later, it appears as an ordinary stopped peer in the next census.
            if (observed is null || (launch is { Starting: false } && inactive) ||
                (launch is not null && (running || state!.Status == "exited") &&
                    state?.StartedAt is { } started && IsDockerTimestamp(started)) ||
                (peerWrite?.Kind == PeerWriteKind.Stop && inactive))
            {
                ct.ThrowIfCancellationRequested();
                _pendingPeerWrite = null;
                _pendingLocalLaunch = null;
                return peers;
            }
            if (attempt == 2 || ownerActive?.Invoke() != true) break;
            DockerCall cleanup;
            if (peerWrite is not null && running)
                cleanup = await CallAsync(HttpMethod.Post, $"{path}/stop?t={Math.Max(1, _cfg.StopTimeoutMs / 1000)}",
                    null, ct, already: [304, 404], requiredStatus: 204).ConfigureAwait(false);
            else if (inactive && (peerWrite?.Kind == PeerWriteKind.Delete || launch is { Starting: true }))
                cleanup = await CallAsync(HttpMethod.Delete, path, null, ct, already: [404], requiredStatus: 204)
                    .ConfigureAwait(false);
            else break;
            if (!cleanup.Ok) break;
        }

        return [FailedPeer("native_local_mutation_unresolved")];
    }

    private bool LocalWriteMatches(DockerObservationResponse observed, string locator, PendingLocalLaunch? launch)
    {
        if (!IsFullContainerId(observed.Id) || (launch is { Starting: false } && observed.Name != "/" + locator) ||
            !MatchesOwnedContainer(observed.Id, observed.Config?.Labels, launch is { Starting: false } ? observed.Id! : locator) ||
            observed.State is not { Paused: false, Restarting: false, Dead: false } state ||
            !(state is { Status: "running", Running: true } or { Status: "created" or "exited", Running: false }))
            return false;
        return launch is null || (observed.Config?.Labels is { } labels &&
            labels.GetValueOrDefault(LabelModel) == launch.Model && labels.GetValueOrDefault(LabelProfile) == launch.Profile &&
            observed.Image == launch.Image);
    }
}
