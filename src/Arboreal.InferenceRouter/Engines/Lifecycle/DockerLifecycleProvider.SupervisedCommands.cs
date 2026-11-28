using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record SupervisedDockerStopIssue(HostReservationScope Scope, EnrolledPairHead Head, string SocketPath,
    string ApiVersion, HostMutation Operation, HostCheckpointLaunchEffect Incumbent, int GraceSeconds);

// The protected writer owns this exclusion through daemon entry. A Take must return only the exact current,
// durably authorized first entry for the requested phase. It checks the installed record, current authenticated
// authority and exclusive possible-issue record. Returning a cached or previously used authorization is invalid.
internal interface ISupervisedDockerCommandAuthority
{
    SupervisedDockerStopIssue? TakeStopUnderIssueFence(HostMutation operation);
    (SupervisedLaunchCommand Command, HostInitialLaunchIntent Intent)? TakeCreateUnderIssueFence(SupervisedLaunchCommand command);
    (HostInitialLaunchIntent Intent, HostInitialCreate Observed, SupervisedStartIssue Anchored)?
        TakeStartUnderIssueFence(SupervisedLaunchCommand command);
}

public sealed partial class DockerLifecycleProvider
{
    // This port reports the daemon reply only. The writer must retain the effect fence until independent
    // observation, including vacancy before Create and effective running state before Applied.
    internal sealed class SupervisedCommands : ISupervisedLaunchPort, IDisposable
    {
        private const int CreateReplyCeiling = 16 * 1024;
        private readonly HostReservationScope _scope;
        private readonly EnrolledPairHead _head;
        private readonly string _socketPath;
        private readonly string _apiVersion;
        private readonly int _callTimeoutMs;
        private readonly ISupervisedDockerCommandAuthority _authority;
        private readonly HttpClient _http;
        private readonly SocketsHttpHandler? _ownedHandler;
        private int _disposed;

        internal SupervisedCommands(HostReservationScope scope, EnrolledPairHead head, string socketPath,
            string apiVersion, int callTimeoutMs, ISupervisedDockerCommandAuthority authority,
            HttpMessageHandler? handler = null)
        {
            if (scope.Identity == Guid.Empty || scope.Enrollment is not { IsValid: true } enrollment ||
                head.Partition != enrollment.Group || head.Generation != enrollment.Generation || head.Revision < 0 ||
                string.IsNullOrWhiteSpace(head.Daemon) || string.IsNullOrWhiteSpace(head.RouterId) ||
                string.IsNullOrWhiteSpace(head.Slot) || !HostMutation.IsPlanDigest(head.Digest) ||
                !Path.IsPathFullyQualified(socketPath) || socketPath.Contains('\0', StringComparison.Ordinal) ||
                Path.GetFullPath(socketPath) != socketPath || !SupervisedLaunchApproval.IsPinnedApiVersion(apiVersion))
                throw new ArgumentException("A commissioned scope and canonical Unix Docker binding are required.", nameof(scope));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(callTimeoutMs);
            _scope = scope;
            _head = head;
            _socketPath = socketPath;
            _apiVersion = apiVersion;
            _callTimeoutMs = callTimeoutMs;
            _authority = authority;
            _ownedHandler = handler is null ? CreateDaemonHandler(socketPath) : null;
            try { _http = CreateDaemonClient(new Uri("http://docker.engine/"), handler ?? _ownedHandler!); }
            catch
            {
                _ownedHandler?.Dispose();
                throw;
            }
        }

        // Null means this port sent no request. Unconfirmed means a stop may have reached the daemon.
        internal async ValueTask<NativeStopDisposition?> StopAsync(HostMutation operation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (operation is not { Kind: HostMutationKind.Stop, Profile: null, PlanDigest: null } ||
                !IsOperation(operation) || !IsFullContainerId(operation.Target) ||
                _authority.TakeStopUnderIssueFence(operation) is not { GraceSeconds: > 0 } issue ||
                !MatchesBinding(issue.Scope, issue.Head, issue.SocketPath, issue.ApiVersion) ||
                issue.Operation != operation || !issue.Incumbent.IsValid() ||
                issue.Incumbent.Operation.Engine != operation.Engine || issue.Incumbent.Operation.Epoch >= operation.Epoch ||
                issue.Incumbent.InstanceId != operation.Target)
                return null;

            var grace = issue.GraceSeconds.ToString(CultureInfo.InvariantCulture);
            var path = $"{ContainerPath(_apiVersion, operation.Target)}/stop?t={grace}";
            var reply = await SendAsync(path, createBytes: null, ct).ConfigureAwait(false);
            return reply.Status switch
            {
                HttpStatusCode.NoContent => NativeStopDisposition.Stopped,
                HttpStatusCode.NotModified => NativeStopDisposition.AlreadyStopped,
                HttpStatusCode.NotFound => NativeStopDisposition.Absent,
                _ => NativeStopDisposition.Unconfirmed,
            };
        }

        public async ValueTask<string?> CreateAsync(SupervisedLaunchCommand command, ReadOnlyMemory<byte> bytes,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!MatchesLaunch(command) || _authority.TakeCreateUnderIssueFence(command) is not { } authorization ||
                authorization.Command != command || ReadApprovedBytes(command, authorization.Intent) is not { } frozen ||
                !bytes.Span.SequenceEqual(frozen))
                return null;

            var reply = await SendAsync(CreateContainerPath(_apiVersion), frozen, ct).ConfigureAwait(false);
            return reply.Id;
        }

        public async ValueTask<bool> StartAsync(SupervisedLaunchCommand command, string instance, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!MatchesLaunch(command) || !IsFullContainerId(instance) ||
                _authority.TakeStartUnderIssueFence(command) is not { } authorization ||
                authorization.Observed != new HostInitialCreate(command.Operation, instance, authorization.Intent.CreateDigest, CompletePair: true) ||
                authorization.Anchored != new SupervisedStartIssue(command, instance, authorization.Intent.CreateDigest) ||
                ReadApprovedBytes(command, authorization.Intent) is null)
                return false;

            var reply = await SendAsync($"{ContainerPath(_apiVersion, instance)}/start", createBytes: null, ct)
                .ConfigureAwait(false);
            return reply.Status is HttpStatusCode.NoContent or HttpStatusCode.NotModified;
        }

        private bool MatchesBinding(HostReservationScope scope, EnrolledPairHead head, string socketPath, string apiVersion) =>
            scope == _scope && head == _head && socketPath == _socketPath && apiVersion == _apiVersion;

        private bool MatchesLaunch(SupervisedLaunchCommand command) =>
            MatchesBinding(command.Scope, command.Head, command.SocketPath, command.ApiVersion) &&
            command.Operation.Kind == HostMutationKind.Launch && IsOperation(command.Operation);

        private static bool IsOperation(HostMutation operation) =>
            operation.Id != Guid.Empty && operation.Epoch > 0 && !string.IsNullOrWhiteSpace(operation.Engine);

        private static byte[]? ReadApprovedBytes(SupervisedLaunchCommand command, HostInitialLaunchIntent intent)
        {
            if (!intent.Plan.Matches(command.Operation, command.Operation.Epoch)) return null;
            byte[] frozen;
            try { frozen = Convert.FromBase64String(intent.CreateBytes); }
            catch (FormatException) { return null; }
            return frozen.Length > 0 && Convert.ToHexString(SHA256.HashData(frozen)) == intent.CreateDigest ? frozen : null;
        }

        private async ValueTask<(HttpStatusCode? Status, string? Id)> SendAsync(string path, byte[]? createBytes,
            CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_callTimeoutMs);
            var token = timeout.Token;
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            if (createBytes is not null)
            {
                request.Content = new ByteArrayContent(createBytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            try
            {
                token.ThrowIfCancellationRequested();
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (createBytes is null || response.StatusCode != HttpStatusCode.Created)
                    return (response.StatusCode, null);

                var body = await ReadDiagnosticsBodyAsync(response.Content, CreateReplyCeiling, token).ConfigureAwait(false);
                var id = JsonSerializer.Deserialize(body.AsSpan(), DockerJsonContext.Default.DockerCreateResponse)?.Id;
                token.ThrowIfCancellationRequested();
                return (response.StatusCode, IsFullContainerId(id) ? id : null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return default; }
            catch (Exception e) when (e is HttpRequestException or IOException or SocketException or InvalidDataException or JsonException)
            {
                ct.ThrowIfCancellationRequested();
                return default;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _http.Dispose(); }
            finally { _ownedHandler?.Dispose(); }
        }
    }
}
