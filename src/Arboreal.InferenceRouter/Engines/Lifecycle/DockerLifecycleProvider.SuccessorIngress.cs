using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

internal enum SuccessorDaemonIngressKind { Stop, Create, Start }

internal sealed record SuccessorDaemonIngressIntent(SuccessorDaemonIngressKind Kind, SupervisedLaunchCommand Command,
    long HeadRevision, string HeadDigest, string RequestTarget, string? InstanceId, ReadOnlyMemory<byte> Body);

internal sealed record SuccessorDaemonIngressEffect(SuccessorDaemonIngressIntent Intent, HttpStatusCode Status,
    string InstanceId);

internal interface IProtectedSuccessorDaemonAudit
{
    // Permanently consumes this scope/operation/kind before forwarding to the daemon.
    bool TryReserve(SuccessorDaemonIngressIntent intent);
    bool TryRecord(SuccessorDaemonIngressEffect effect);
}

public sealed partial class DockerLifecycleProvider
{
    // This boundary runs at the independently protected ingress. It checks the actual inbound
    // bytes against the current anchored issue and sends those same bytes to the private daemon.
    internal sealed class SuccessorIngress : IDisposable
    {
        private const int ReplyCeiling = 16 * 1024;
        private readonly EnrolledSuccessorScope _scope;
        private readonly string _writerSocket;
        private readonly string _checkpointLock;
        private readonly Func<SuccessorIssueHead?> _readIssued;
        private readonly Func<SupervisedLaunchSnapshot?>? _readInitial;
        private readonly Func<CancellationToken, ValueTask<SuccessorHeldPartition?>> _readHeldPartition;
        private readonly IProtectedSuccessorDaemonAudit _audit;
        private readonly SocketsHttpHandler _handler;
        private readonly HttpClient _client;

        internal SuccessorIngress(EnrolledSuccessorScope scope, string writerSocket, string daemonSocket, string checkpointLock,
            Func<SuccessorIssueHead?> readIssued, IProtectedSuccessorDaemonAudit audit,
            Func<CancellationToken, ValueTask<SuccessorHeldPartition?>> readHeldPartition,
            Func<SupervisedLaunchSnapshot?>? readInitial = null)
        {
            if (!IsCanonicalUnixPath(writerSocket) || !IsCanonicalUnixPath(daemonSocket) || !IsCanonicalUnixPath(checkpointLock) ||
                writerSocket == daemonSocket || !OperationHeadProtocol.IsScopeValid(scope.Reservation))
                throw new ArgumentException("The successor ingress requires distinct commissioned Unix endpoints.", nameof(daemonSocket));
            _scope = scope;
            _writerSocket = writerSocket;
            _checkpointLock = checkpointLock;
            _readIssued = readIssued;
            _readInitial = readInitial;
            _readHeldPartition = readHeldPartition;
            _audit = audit;
            _handler = CreateDaemonHandler(daemonSocket);
            try { _client = CreateDaemonClient(new Uri("http://docker.engine/"), _handler); }
            catch
            {
                _handler.Dispose();
                throw;
            }
        }

        internal async ValueTask<(HttpStatusCode Status, byte[] Body)?> ForwardAuthenticatedAsync(
            Socket peer, uint writerUid, HttpMethod method, string requestTarget,
            ReadOnlyMemory<byte> receivedBody, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsLinux() || !HeadReadV1Endpoint.IsAuthorizedRequester(peer, writerUid) ||
                method != HttpMethod.Post) return null;
            if (_readInitial is not null)
                return await ForwardInitialAsync(requestTarget, receivedBody, ct).ConfigureAwait(false);
            if (_readIssued() is not { Current: { } issued } head ||
                Bind(head, requestTarget, receivedBody) is not { } intent) return null;

            var held = await _readHeldPartition(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return MatchesHeldPartition(issued.Binding, intent, held)
                ? await SendAuthorizedAsync(intent, () => StillIssued(head), ct).ConfigureAwait(false) : null;
        }

        private async ValueTask<(HttpStatusCode Status, byte[] Body)?> ForwardInitialAsync(string target,
            ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            if (_readInitial?.Invoke() is not
                    { Frame: { History: { Issued: { } dispatch, Applied: null }, Commission: { } commission } } issue ||
                commission.Scope != _scope.Reservation || commission.Topology.Head != _scope.Commission.Head ||
                !dispatch.MatchesDispatching(HostReservation.ReadEnrolled(_scope.Reservation)))
                return null;
            var command = commission.Command(dispatch);
            if (command.SocketPath != _writerSocket || !SupervisedLaunchApproval.IsPinnedApiVersion(command.ApiVersion) ||
                !_scope.Commission.Approvals.Any(approval => approval.Launch == commission.Approval(dispatch.Operation.Engine)))
                return null;

            SuccessorDaemonIngressIntent intent;
            if (issue.Head.Revision == 3 && issue.Frame.StartIssue is null &&
                target == $"/{command.ApiVersion}/containers/create" &&
                bytes.Span.SequenceEqual(Convert.FromBase64String(dispatch.Intent.CreateBytes)))
                intent = new(SuccessorDaemonIngressKind.Create, command, issue.Head.Revision, issue.Head.Digest,
                    target, null, bytes.ToArray());
            else if (issue.Head.Revision == 4 && issue.Frame.StartIssue is { } start && start.Command == command &&
                SupervisedLaunchEvidence.IsFullContainerId(start.InstanceId) && bytes.IsEmpty &&
                target == $"/{command.ApiVersion}/containers/{start.InstanceId}/start")
                intent = new(SuccessorDaemonIngressKind.Start, command, issue.Head.Revision, issue.Head.Digest,
                    target, start.InstanceId, bytes);
            else return null;

            var held = await _readHeldPartition(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (held is not { IngressClosed: true, ResidentRoute: null } || held.Scope != _scope.Reservation ||
                held.Catalog != command.Head || held.OwnerGeneration != dispatch.Policy.OwnerGeneration ||
                held.Revision != dispatch.Policy.PolicyGeneration ||
                !SupervisedLaunchCommission.SameDevices(_scope.Commission.Devices, held.CoveredDevices) ||
                held.ResidentRunning ||
                (intent.Kind == SuccessorDaemonIngressKind.Create ? held.Resident is not null :
                    held.Resident != new HostCheckpointLaunchEffect(command.Operation, dispatch.Intent.Plan, intent.InstanceId!)))
                return null;

            return await SendAuthorizedAsync(intent, () => _readInitial?.Invoke()?.Head == issue.Head &&
                dispatch.MatchesDispatching(HostReservation.ReadEnrolled(_scope.Reservation)), ct).ConfigureAwait(false);
        }

        private async ValueTask<(HttpStatusCode Status, byte[] Body)?> SendAuthorizedAsync(
            SuccessorDaemonIngressIntent intent, Func<bool> stillIssued, CancellationToken ct)
        {
            if (!stillIssued() || !_audit.TryReserve(intent) || !stillIssued()) return null;
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, intent.RequestTarget);
            if (intent.Kind == SuccessorDaemonIngressKind.Create)
            {
                request.Content = new ReadOnlyMemoryContent(intent.Body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using var reply = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var body = await ReadDiagnosticsBodyAsync(reply.Content, ReplyCeiling, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (AcceptedInstance(intent, reply.StatusCode, body) is not { } instance) return (reply.StatusCode, body);
            return stillIssued() && _audit.TryRecord(new(intent, reply.StatusCode, instance))
                ? (reply.StatusCode, body) : null;
        }

        private bool MatchesHeldPartition(SuccessorIssueBinding issued, SuccessorDaemonIngressIntent intent,
            SuccessorHeldPartition? held)
        {
            var retirement = issued.Retirement;
            if (held is not { IngressClosed: true, Revision: > 0 } || held.Scope != _scope.Reservation ||
                held.Catalog != _scope.Commission.Head || held.OwnerGeneration != retirement.OwnerGeneration ||
                held.IngressGeneration != retirement.IngressGeneration || held.CoverageGeneration != issued.CoverageGeneration ||
                !SupervisedLaunchCommission.SameDevices(_scope.Commission.Devices, held.CoveredDevices))
                return false;

            return intent.Kind switch
            {
                SuccessorDaemonIngressKind.Stop => held.Resident == retirement.Incumbent &&
                    held.ResidentRoute == retirement.SealedRoute && held.ResidentRunning,
                SuccessorDaemonIngressKind.Create => held is { Resident: null, ResidentRoute: null, ResidentRunning: false },
                SuccessorDaemonIngressKind.Start => held is { Resident: { } created, ResidentRoute: null, ResidentRunning: false } &&
                    created.Operation == intent.Command.Operation && created.InstanceId == intent.InstanceId &&
                    created.Plan == issued.Successor.Plan,
                _ => false,
            };
        }

        private bool StillIssued(SuccessorIssueHead head) => head.Current is { Binding.Checkpoint: { } installed } &&
            _readIssued() == head && HostEffectCheckpoint.ReadInstalledCommitment(_checkpointLock) == installed;

        private SuccessorDaemonIngressIntent? Bind(SuccessorIssueHead head, string target, ReadOnlyMemory<byte> body)
        {
            if (head.Scope != _scope.Reservation || head.Commission != _scope.Commission.Head ||
                head.Revision <= 0 || !OperationHeadProtocol.IsCanonicalDigest(head.Digest) ||
                head.Current is not { } issued || issued.Binding.Command is not { } command ||
                command.Scope != head.Scope || command.Head != head.Commission || command.SocketPath != _writerSocket ||
                !SupervisedLaunchApproval.IsPinnedApiVersion(command.ApiVersion)) return null;

            var operation = command.Operation;
            if (operation.Kind == HostMutationKind.Stop)
                return BindStop(head, issued, target, body);

            if (operation.Kind != HostMutationKind.Launch || operation.Id == Guid.Empty ||
                operation != issued.Binding.Checkpoint.State.Transition?.Launch || !issued.Binding.Successor.IsValid())
                return null;

            if (issued.Phase == SuccessorIssuePhase.MayHaveIssued && target == $"/{command.ApiVersion}/containers/create")
            {
                var exact = Convert.FromBase64String(issued.Binding.Successor.CreateBytes);
                return body.Span.SequenceEqual(exact)
                    ? new(SuccessorDaemonIngressKind.Create, command, head.Revision, head.Digest, target, null, body.ToArray())
                    : null;
            }

            if (issued is not { Phase: SuccessorIssuePhase.StartAnchored, Start: { } start } || start.Command != command ||
                !SupervisedLaunchEvidence.IsFullContainerId(start.InstanceId) ||
                start.CreateDigest != issued.Binding.Successor.CreateDigest || !body.IsEmpty ||
                target != $"/{command.ApiVersion}/containers/{start.InstanceId}/start") return null;
            return new(SuccessorDaemonIngressKind.Start, command, head.Revision, head.Digest, target, start.InstanceId, body);
        }

        private SuccessorDaemonIngressIntent? BindStop(SuccessorIssueHead head, SuccessorIssueRecord issued,
            string target, ReadOnlyMemory<byte> body)
        {
            var command = issued.Binding.Command;
            var incumbent = issued.Binding.Retirement.Incumbent;
            if (issued.Phase != SuccessorIssuePhase.MayHaveIssued || !body.IsEmpty ||
                command.Operation != issued.Binding.Retirement.Stop ||
                command.Operation != issued.Binding.Checkpoint.State.Transition?.Stop ||
                !SupervisedLaunchEvidence.IsFullContainerId(incumbent.InstanceId)) return null;

            var prior = _scope.Commission.Approvals.SingleOrDefault(approval => approval.Launch.Intent.Plan == incumbent.Plan);
            if (prior is null || JsonSerializer.Deserialize(Convert.FromBase64String(prior.Launch.Intent.CreateBytes),
                DockerJsonContext.Default.DockerCreateRequest) is not { StopTimeoutSeconds: int grace } || grace <= 0)
                return null;
            var expected = $"/{command.ApiVersion}/containers/{incumbent.InstanceId}/stop?t={grace.ToString(CultureInfo.InvariantCulture)}";
            return target == expected
                ? new(SuccessorDaemonIngressKind.Stop, command, head.Revision, head.Digest, target, incumbent.InstanceId, body)
                : null;
        }

        private static string? AcceptedInstance(SuccessorDaemonIngressIntent intent, HttpStatusCode status, byte[] body)
        {
            if (intent.Kind == SuccessorDaemonIngressKind.Create && status == HttpStatusCode.Created &&
                JsonSerializer.Deserialize(body.AsSpan(), DockerJsonContext.Default.DockerCreateResponse)?.Id is { } id &&
                SupervisedLaunchEvidence.IsFullContainerId(id)) return id;
            if (intent.Kind == SuccessorDaemonIngressKind.Stop &&
                status is HttpStatusCode.NoContent or HttpStatusCode.NotModified or HttpStatusCode.NotFound)
                return intent.InstanceId;
            return intent.Kind == SuccessorDaemonIngressKind.Start &&
                status is HttpStatusCode.NoContent or HttpStatusCode.NotModified ? intent.InstanceId : null;
        }

        private static bool IsCanonicalUnixPath(string path) => Path.IsPathFullyQualified(path) &&
            !path.Contains('\0', StringComparison.Ordinal) && Path.GetFullPath(path) == path;

        public void Dispose()
        {
            _client.Dispose();
            _handler.Dispose();
        }
    }
}
