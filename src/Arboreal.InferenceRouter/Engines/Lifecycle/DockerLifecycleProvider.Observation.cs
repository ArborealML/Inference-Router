using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

internal readonly record struct NativeContainerStamp(string StartedAt, long RestartCount);
internal enum NativeContainerStatus { Unknown, Running, Invalid }
internal readonly record struct NativeContainerObservation(NativeContainerStatus Status, NativeContainerStamp Stamp = default,
    int InitPid = 0);
internal readonly record struct NativeVllmObservation(NativeContainerStatus Status, VllmProcessEvidence? Evidence = null);

public sealed partial class DockerLifecycleProvider
{
    internal TimeProvider ObservationClock { get; init; } = TimeProvider.System;
    internal Action<Task>? ObservationCleanupForTests { get; init; }
    internal TimeSpan ObservationTimeout => TimeSpan.FromMilliseconds(_cfg.CallTimeoutMs);
    internal string VllmProcRoot { get; init; } = "/proc";

    // All callers retain their own gate (if any). The retired read can only dispose its response.
    internal ValueTask<ReadinessStep<NativeContainerObservation>> ObserveContainerAsync(
        NativeLaunch launch, ReadinessDeadline deadline, TimeSpan? cap = null) =>
        deadline.ObserveAsync(token => ReadContainerObservationAsync(launch, token), cap, ObservationCleanupForTests);

    internal bool HasIssuedStopOrDeleteUnderGate(string id) =>
        IsFullContainerId(id) && _issuedPeerIds.Contains(id);

    // Read-only throughout. The caller owns publication, deadlines and any operation gate.
    internal ValueTask<ReadinessStep<NativeVllmObservation>> ObserveVllmAsync(
        NativeLaunch launch, NativeContainerStamp baseline, ReadinessDeadline deadline) =>
        deadline.ObserveAsync(async ct =>
        {
            var before = await ReadContainerObservationAsync(launch, ct, includeProcess: true).ConfigureAwait(false);
            if (before.Status == NativeContainerStatus.Invalid || before.Status == NativeContainerStatus.Running && before.Stamp != baseline)
                return new NativeVllmObservation(NativeContainerStatus.Invalid);
            if (before is not { Status: NativeContainerStatus.Running, InitPid: > 0 }) return default;
            var evidence = VllmProcessReader.Read(VllmProcRoot, before.InitPid, launch.ContainerId, ct);
            var after = await ReadContainerObservationAsync(launch, ct, includeProcess: true).ConfigureAwait(false);
            if (after.Status == NativeContainerStatus.Invalid || after.Status == NativeContainerStatus.Running && after != before)
                return new(NativeContainerStatus.Invalid);
            return after == before ? new(NativeContainerStatus.Running, evidence) : default;
        }, captureCleanupForTests: ObservationCleanupForTests);

    private async Task<NativeContainerObservation> ReadContainerObservationAsync(NativeLaunch launch, CancellationToken ct,
        bool includeProcess = false)
    {
        if (!IsFullContainerId(launch.ContainerId) || Volatile.Read(ref _disposed) != 0) return default;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{_cfg.ApiVersion}/containers/{launch.ContainerId}/json");
            ct.ThrowIfCancellationRequested();
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (response.StatusCode == HttpStatusCode.NotFound) return new(NativeContainerStatus.Invalid);
            if (response.StatusCode != HttpStatusCode.OK) return default;
            var body = await ReadDiagnosticsBodyAsync(response.Content, ContainerInspectCeiling, ct).ConfigureAwait(false);
            var inspect = JsonSerializer.Deserialize(body.AsSpan(), DockerJsonContext.Default.DockerObservationResponse);
            var sample = ClassifyContainer(inspect, launch);
            if (includeProcess && sample.Status == NativeContainerStatus.Running &&
                inspect is { HostConfig: { Init: true, PidMode: "" or "private" }, State.Pid: > 0 })
                sample = sample with { InitPid = inspect.State.Pid.Value };
            return sample;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or IOException or
                                   SocketException or ObjectDisposedException or OperationCanceledException)
        {
            return default;
        }
    }

    private NativeContainerObservation ClassifyContainer(DockerObservationResponse? observed, NativeLaunch launch)
    {
        if (observed is not { } inspect || inspect.Id != launch.ContainerId ||
            !DockerImageIdentity.IsFullSha256(inspect.Image.AsSpan()) ||
            inspect.Config?.Labels is not { } labels ||
            !labels.TryGetValue(LabelRouter, out var router) || string.IsNullOrWhiteSpace(router) ||
            !labels.TryGetValue(LabelEngine, out var engine) || string.IsNullOrWhiteSpace(engine) ||
            !labels.TryGetValue(LabelModel, out var model) || string.IsNullOrWhiteSpace(model) ||
            !labels.TryGetValue(LabelProfile, out var profile) || profile is null ||
            inspect.State is not { Running: not null, Paused: not null, Restarting: not null, Dead: not null } state ||
            inspect.RestartCount is < 0)
            return default;

        var start = state.StartedAt;
        var hasStart = start is not null && start != "0001-01-01T00:00:00Z";
        if (hasStart && !IsDockerTimestamp(start!)) return default;
        var flags = (state.Status, state.Running, state.Paused, state.Restarting, state.Dead);
        var running = flags is ("running", true, false, false, false);
        var nonServing = flags is ("paused", true, true, false, false) or ("restarting", true, false, true, false) or
            ("exited", false, false, false, false) or ("dead", false, false, false, true) or
            ("removing", false, false, false, _) || (!hasStart && flags is ("created", false, false, false, false));
        if (nonServing) return new(NativeContainerStatus.Invalid);
        if (!running || !hasStart || inspect.RestartCount is not long count) return default;
        if (router != _scope.RouterId || engine != _scope.EngineName || model != launch.Model ||
            profile != launch.Profile || inspect.Image != launch.ImageId)
            return new(NativeContainerStatus.Invalid);
        // Preserve the daemon's fractional digits when comparing stamps.
        return new(NativeContainerStatus.Running, new(start!, count));
    }

    private static bool IsDockerTimestamp(string timestamp)
    {
        if (timestamp.Length is < 20 or > 30 || timestamp[^1] != 'Z' ||
            !DateTime.TryParseExact(timestamp.AsSpan(0, 19), "yyyy-MM-dd'T'HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) || value == DateTime.MinValue)
            return false;
        if (timestamp.Length == 20) return true;
        return timestamp.Length >= 22 && timestamp[19] == '.' &&
            timestamp.AsSpan(20, timestamp.Length - 21).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
