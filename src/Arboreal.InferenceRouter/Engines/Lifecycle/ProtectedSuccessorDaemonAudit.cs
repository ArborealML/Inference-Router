using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record SuccessorDaemonAuditFrame(SuccessorDaemonIngressIntent Intent,
    HttpStatusCode? Status, string? InstanceId);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SuccessorDaemonAuditFrame))]
internal sealed partial class SuccessorDaemonAuditJson : JsonSerializerContext;

// The precreated file belongs to the protected daemon ingress, outside the queued writer's
// restore and write domains. A torn or unreadable tail poisons the log. No reopen rewrites it.
internal sealed class ProtectedSuccessorDaemonAudit(HostReservationScope scope, string installedPath)
    : IProtectedSuccessorDaemonAudit
{
    private const int FrameLimit = 1024 * 1024;
    private const long LogLimit = 64L * FrameLimit;

    public bool TryReserve(SuccessorDaemonIngressIntent intent) => Amend(new(intent, Status: null, InstanceId: null));

    public bool TryRecord(SuccessorDaemonIngressEffect effect) => Amend(new(effect.Intent, effect.Status, effect.InstanceId));

    internal SuccessorDaemonIngressEffect? ReadAccepted(HostMutation operation, SuccessorDaemonIngressKind kind)
    {
        try
        {
            using var stream = new FileStream(installedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(stream)?.GetValueOrDefault((operation.Id, kind)) is { Status: { } status, InstanceId: { } id } accepted &&
                accepted.Intent.Command.Operation == operation ? new(accepted.Intent, status, id) : null;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error)) { return null; }
    }

    private bool Amend(SuccessorDaemonAuditFrame frame)
    {
        try
        {
            using var stream = new FileStream(installedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (Read(stream) is not { } history || !Extends(history, frame)) return false;
            var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SuccessorDaemonAuditJson.Default.SuccessorDaemonAuditFrame);
            if (payload.Length is 0 or > FrameLimit || stream.Length + sizeof(int) + payload.Length > LogLimit)
                return false;
            Span<byte> prefix = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
            stream.Write(prefix);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error)) { return false; }
    }

    private Dictionary<(Guid Operation, SuccessorDaemonIngressKind Kind), SuccessorDaemonAuditFrame>? Read(FileStream stream)
    {
        if (stream.Length > LogLimit) return null;
        Dictionary<(Guid, SuccessorDaemonIngressKind), SuccessorDaemonAuditFrame> history = [];
        Span<byte> prefix = stackalloc byte[sizeof(int)];
        while (stream.Position < stream.Length)
        {
            stream.ReadExactly(prefix);
            var size = BinaryPrimitives.ReadInt32BigEndian(prefix);
            if (size is <= 0 or > FrameLimit || size > stream.Length - stream.Position) return null;
            var payload = new byte[size];
            stream.ReadExactly(payload);
            var frame = JsonSerializer.Deserialize(payload.AsSpan(), SuccessorDaemonAuditJson.Default.SuccessorDaemonAuditFrame);
            if (frame is null || !Extends(history, frame)) return null;
        }
        return history;
    }

    private bool Extends(Dictionary<(Guid, SuccessorDaemonIngressKind), SuccessorDaemonAuditFrame> history,
        SuccessorDaemonAuditFrame frame)
    {
        if (frame.Intent is not { Command: { Operation: { } operation } command } intent ||
            command.Scope != scope || operation.Id == Guid.Empty || operation.Epoch <= 0 || intent.HeadRevision <= 0 ||
            !OperationHeadProtocol.IsCanonicalDigest(intent.HeadDigest) || !ExpectedKind(operation.Kind, intent.Kind))
            return false;
        var key = (operation.Id, intent.Kind);
        if (frame.Status is null)
        {
            if (frame.InstanceId is not null || (intent.Kind == SuccessorDaemonIngressKind.Start &&
                (history.GetValueOrDefault((operation.Id, SuccessorDaemonIngressKind.Create)) is not { Status: not null } create ||
                 create.Intent.Command != command || create.Intent.HeadRevision >= intent.HeadRevision ||
                 create.InstanceId != intent.InstanceId))) return false;
            return history.TryAdd(key, frame);
        }
        if (history.GetValueOrDefault(key) is not { Status: null } reserved ||
            !SameIntent(reserved.Intent, intent) || !AcceptedStatus(intent.Kind, frame.Status.Value) ||
            !SupervisedLaunchEvidence.IsFullContainerId(frame.InstanceId) ||
            (intent.Kind != SuccessorDaemonIngressKind.Create && frame.InstanceId != intent.InstanceId))
            return false;

        history[key] = frame;
        return true;
    }

    private static bool SameIntent(SuccessorDaemonIngressIntent left, SuccessorDaemonIngressIntent right) =>
        (left with { Body = default }) == (right with { Body = default }) && left.Body.Span.SequenceEqual(right.Body.Span);

    private static bool ExpectedKind(HostMutationKind operation, SuccessorDaemonIngressKind intent) => intent switch
    {
        SuccessorDaemonIngressKind.Stop => operation == HostMutationKind.Stop,
        SuccessorDaemonIngressKind.Create or SuccessorDaemonIngressKind.Start => operation == HostMutationKind.Launch,
        _ => false,
    };

    private static bool AcceptedStatus(SuccessorDaemonIngressKind kind, HttpStatusCode status) => kind switch
    {
        SuccessorDaemonIngressKind.Stop => status is HttpStatusCode.NoContent or HttpStatusCode.NotModified or HttpStatusCode.NotFound,
        SuccessorDaemonIngressKind.Create => status == HttpStatusCode.Created,
        SuccessorDaemonIngressKind.Start => status is HttpStatusCode.NoContent or HttpStatusCode.NotModified,
        _ => false,
    };
}
