using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record SuccessorDockerInspect(
    [property: JsonPropertyName("Id")] string? Id,
    [property: JsonPropertyName("Image")] string? Image,
    [property: JsonPropertyName("Config")] DockerCreateRequest? Configuration,
    [property: JsonPropertyName("HostConfig")] DockerHostConfig? Host);

internal sealed record SuccessorCensusCandidate(string? InstanceId, bool Running);

public sealed partial class DockerLifecycleProvider
{
    // Only a commissioned GET relay is given to the observer. The daemon census corroborates
    // independently enforced exclusion; Docker alone does not cover host processes or devices.
    internal sealed class SuccessorCensus : IDisposable
    {
        private const int CensusCeiling = 1024 * 1024;
        private const int InspectCeiling = 128 * 1024;
        private readonly SocketsHttpHandler _handler;
        private readonly HttpClient _client;
        private readonly string _api;

        internal SuccessorCensus(string readOnlySocket, string apiVersion)
        {
            if (!Path.IsPathFullyQualified(readOnlySocket) || readOnlySocket.Contains('\0', StringComparison.Ordinal) ||
                Path.GetFullPath(readOnlySocket) != readOnlySocket || !SupervisedLaunchApproval.IsPinnedApiVersion(apiVersion))
                throw new ArgumentException("The observer requires a commissioned read-only Unix Docker endpoint.", nameof(readOnlySocket));

            _api = apiVersion;
            _handler = CreateDaemonHandler(readOnlySocket);
            try { _client = CreateDaemonClient(new Uri("http://docker.engine/"), _handler); }
            catch
            {
                _handler.Dispose();
                throw;
            }
        }

        internal async ValueTask<SuccessorCensusCandidate?> ReadHeldCandidateAsync(string routerId, string? engine,
            CancellationToken ct)
        {
            var (status, containers) = await GetAsync($"{_api}/containers/json?all=true", CensusCeiling,
                DockerJsonContext.Default.ListDockerContainerSummary, ct).ConfigureAwait(false);
            if (status != HttpStatusCode.OK || containers is null) return null;

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SuccessorCensusCandidate? resident = null;
            foreach (var container in containers)
            {
                if (container is null || container.Id is not { } id || !SupervisedLaunchEvidence.IsFullContainerId(id) ||
                    !ids.Add(id)) return null;
                if (container.State is "exited" or "dead") continue;
                if (resident is not null || engine is null || container.State is not ("running" or "created") ||
                    !Owns(container.Labels, routerId, engine)) return null;

                resident = new(id, container.State == "running");
            }
            return resident ?? new(null, Running: false);
        }

        internal async ValueTask<bool> CorroborateAsync(string routerId, HostCheckpointLaunchEffect? resident,
            bool running, DockerImageIdentity? image, HostCheckpointStopEffect? stopped, CancellationToken ct)
        {
            var (listStatus, containers) = await GetAsync($"{_api}/containers/json?all=true",
                CensusCeiling, DockerJsonContext.Default.ListDockerContainerSummary, ct).ConfigureAwait(false);
            if (listStatus != HttpStatusCode.OK || containers is null) return false;

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = false;
            var expectedState = running ? "running" : "created";
            foreach (var container in containers)
            {
                if (container is null || container.Id is not { } id || !SupervisedLaunchEvidence.IsFullContainerId(id) ||
                    !ids.Add(id)) return false;
                if (container.State is "exited" or "dead") continue;
                if (resident is null || id != resident.InstanceId || container.State != expectedState ||
                    !Owns(container.Labels, routerId, resident.Plan.Engine)) return false;
                found = true;
            }
            if (found != (resident is not null)) return false;

            if (resident is not null)
            {
                var (status, inspected) = await InspectAsync(resident.InstanceId, ct).ConfigureAwait(false);
                if (status != HttpStatusCode.OK || inspected is null || inspected.Id != resident.InstanceId ||
                    image is null || inspected.Image != image.Id || !Owns(inspected.Config?.Labels, routerId, resident.Plan.Engine) ||
                    inspected.State is not { Paused: false, Restarting: false, Dead: false } state ||
                    state.Running != running || state.Status != expectedState) return false;
            }

            if (stopped is null) return true;
            var (stopStatus, former) = await InspectAsync(stopped.InstanceId, ct).ConfigureAwait(false);
            return stopStatus == HttpStatusCode.NotFound ||
                (stopStatus == HttpStatusCode.OK && former is not null && former.Id == stopped.InstanceId &&
                 Owns(former.Config?.Labels, routerId, stopped.IncumbentLaunch.Engine) &&
                 former.State is { Status: "exited", Running: false, Paused: false, Restarting: false, Dead: false });
        }

        internal async ValueTask<int?> ReadRunningInitPidAsync(string routerId, HostCheckpointLaunchEffect resident,
            DockerImageIdentity image, CancellationToken ct)
        {
            var (status, inspected) = await InspectAsync(resident.InstanceId, ct).ConfigureAwait(false);
            return status == HttpStatusCode.OK && inspected is
                { State: { Status: "running", Running: true, Paused: false, Restarting: false, Dead: false, Pid: > 0 } running } &&
                inspected.Id == resident.InstanceId && inspected.Image == image.Id &&
                Owns(inspected.Config?.Labels, routerId, resident.Plan.Engine) ? running.Pid : null;
        }

        internal async ValueTask<bool> HasImageAsync(DockerImageIdentity expected, CancellationToken ct)
        {
            var (status, image) = await GetAsync($"{_api}/images/{expected.Id}/json", InspectCeiling,
                DockerJsonContext.Default.DockerImageInspectResponse, ct).ConfigureAwait(false);
            if (status != HttpStatusCode.OK || image is null || image.Id != expected.Id ||
                image.RepoDigests?.Contains(expected.RepoDigest, StringComparer.Ordinal) != true ||
                !DockerImageIdentity.IsPlatformSegment(image.Os) || !DockerImageIdentity.IsPlatformSegment(image.Architecture) ||
                (!string.IsNullOrEmpty(image.Variant) && !DockerImageIdentity.IsPlatformSegment(image.Variant)))
                return false;

            var platform = string.IsNullOrEmpty(image.Variant) ? $"{image.Os}/{image.Architecture}" :
                $"{image.Os}/{image.Architecture}/{image.Variant}";
            return platform == expected.Platform;
        }

        internal async ValueTask<DockerCreateRequest?> ReadEffectiveCreateAsync(string id, DockerImageIdentity image,
            CancellationToken ct)
        {
            if (!SupervisedLaunchEvidence.IsFullContainerId(id)) return null;
            var (status, inspected) = await GetAsync($"{_api}/containers/{id}/json", InspectCeiling,
                DockerJsonContext.Default.SuccessorDockerInspect, ct).ConfigureAwait(false);
            if (status != HttpStatusCode.OK || inspected is not { Configuration: { } config, Host: { } host } ||
                inspected.Id != id || inspected.Image != image.Id) return null;

            // Inspect splits the actual effective inputs across Config and HostConfig.
            // Preserve the inspected values so the existing exact comparator can refuse drift.
            config.HostConfig = host;
            return config;
        }

        private ValueTask<(HttpStatusCode Status, DockerObservationResponse? Value)> InspectAsync(string id, CancellationToken ct)
        {
            if (!SupervisedLaunchEvidence.IsFullContainerId(id))
                throw new InvalidDataException("The observer requires a full Docker container ID.");
            return GetAsync($"{_api}/containers/{id}/json", InspectCeiling, DockerJsonContext.Default.DockerObservationResponse, ct);
        }

        private async ValueTask<(HttpStatusCode Status, T? Value)> GetAsync<T>(
            string path, int ceiling, JsonTypeInfo<T> type, CancellationToken ct) where T : class
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
            var body = await ReadDiagnosticsBodyAsync(response.Content, ceiling, ct).ConfigureAwait(false);
            return (response.StatusCode, JsonSerializer.Deserialize(body.AsSpan(), type));
        }

        private static bool Owns(Dictionary<string, string>? labels, string routerId, string engine) => labels is not null &&
            labels.TryGetValue(LabelRouter, out var router) && router == routerId &&
            labels.TryGetValue(LabelEngine, out var owner) && owner == engine;

        public void Dispose()
        {
            _client.Dispose();
            _handler.Dispose();
        }
    }
}
