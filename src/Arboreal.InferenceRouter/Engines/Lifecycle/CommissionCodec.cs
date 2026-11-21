using System.Text.Json.Serialization;

namespace Arboreal.InferenceRouter.Lifecycle;

internal sealed record CommissionRequest(int Version, string Slot, string Challenge);
internal sealed record CommissionReply(int Version, string Slot, string Challenge, SupervisedLaunchCommission Commission);

[JsonSourceGenerationOptions(AllowDuplicateProperties = false, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CommissionRequest))]
[JsonSerializable(typeof(CommissionReply))]
internal sealed partial class CommissionJsonContext : JsonSerializerContext;

internal static class CommissionCodec
{
    internal const int Version = 1;
    internal const int ChallengeBytes = 32;
    internal const int RequestLimit = 1024;
    internal const int ReplyLimit = 512 * 1024;

    internal static bool IsRequestFor(CommissionRequest request, string slot) =>
        request.Version == Version && request.Slot == slot && !string.IsNullOrWhiteSpace(slot) &&
        request.Challenge is { Length: ChallengeBytes * 2 } challenge && challenge.All(char.IsAsciiHexDigit);

    internal static bool MatchesReply(CommissionReply reply, CommissionRequest request) =>
        reply.Version == request.Version && reply.Slot == request.Slot && reply.Challenge == request.Challenge;

    internal static ValueTask WriteRequestAsync(Stream stream, CommissionRequest request, CancellationToken ct) =>
        BoundedJsonFrame.WriteAsync(stream, request, CommissionJsonContext.Default.CommissionRequest, RequestLimit, ct);

    internal static ValueTask WriteReplyAsync(Stream stream, CommissionReply reply, CancellationToken ct) =>
        BoundedJsonFrame.WriteAsync(stream, reply, CommissionJsonContext.Default.CommissionReply, ReplyLimit, ct);

    internal static ValueTask<CommissionRequest> ReadRequestAsync(Stream stream, CancellationToken ct) =>
        BoundedJsonFrame.ReadAsync(stream, CommissionJsonContext.Default.CommissionRequest, RequestLimit, ct);

    internal static ValueTask<CommissionReply> ReadReplyAsync(Stream stream, CancellationToken ct) =>
        ReadReplyAsync(stream, null, ct);

    internal static ValueTask<CommissionReply> ReadReplyAsync(
        Stream stream, Action<int, CancellationToken>? payloadReadPendingForTests, CancellationToken ct) =>
        BoundedJsonFrame.ReadAsync(stream, CommissionJsonContext.Default.CommissionReply, ReplyLimit,
            payloadReadPendingForTests, ct);
}
