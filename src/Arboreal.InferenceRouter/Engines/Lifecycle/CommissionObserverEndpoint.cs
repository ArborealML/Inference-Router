using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Arboreal.InferenceRouter.Lifecycle;

/// <summary>Serves one immutable commission on an accepted local connection owned by the listener.</summary>
internal sealed class CommissionObserverEndpoint(HostReservationScope scope, string slot, uint requesterUid,
    X509Certificate2 certificate, Func<SupervisedLaunchCommission?> readCommission)
{
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromSeconds(5);

    internal Func<CancellationToken, Task>? AfterReplyForTests { private get; init; }

    internal async Task ServeAsync(Socket accepted, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(ConnectionLifetime);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var token = lifetime.Token;
        try
        {
            if (!HeadReadV1Endpoint.IsAuthorizedRequester(accepted, requesterUid)) return;

            using var network = new NetworkStream(accepted, ownsSocket: false);
            using var tls = new SslStream(network, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate },
                token).ConfigureAwait(false);
            var request = await CommissionCodec.ReadRequestAsync(tls, token).ConfigureAwait(false);
            if (!CommissionCodec.IsRequestFor(request, slot)) return;

            token.ThrowIfCancellationRequested();
            var commission = readCommission();
            if (commission is null || !commission.AuthorizesScope(scope, slot)) return;

            var reply = new CommissionReply(request.Version, slot, request.Challenge, commission);
            await CommissionCodec.WriteReplyAsync(tls, reply, token).ConfigureAwait(false);
            if (AfterReplyForTests is { } afterReply) await afterReply(token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or SocketException or AuthenticationException or JsonException
            or OperationCanceledException or UnauthorizedAccessException)
        {
            // Closing a refused connection conveys no substitute for the commissioned evidence.
        }
    }
}
