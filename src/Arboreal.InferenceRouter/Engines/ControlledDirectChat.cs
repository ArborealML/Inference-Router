using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter;

// This composition exists for loopback qualifications. A production issuer has to integrate
// durable maximum liability, policy, credential and capacity ownership at this boundary.
internal sealed class ControlledDirectChat(
    Func<string, CancellationToken, ValueTask<ControlledChatCredential?>> credential,
    Func<DirectChatBinding, CancellationToken, ValueTask<ControlledChatPermit?>> issue)
{
    internal Func<ControlledChatPermit, CancellationToken, Task>? BeforeConnectForTests { get; set; }
    internal Action<DirectChatBinding>? BeforeFirstWriteForTests { get; set; }

    internal async Task<VerifiedInstanceExchange> SendAsync(DirectChatProvider provider, DirectChatModel model,
        RouterTicket ticket, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        try
        {
            if (!provider.Enabled || !DirectChatProvider.IsControlledLoopback(provider.ChatUri)) throw Unavailable();
            var secret = await credential(provider.CredentialReference, cancellationToken).ConfigureAwait(false);
            if (secret is not { Epoch: > 0, Token.Length: > 0 and <= 512 } ||
                secret.Token.Any(static ch => ch is <= ' ' or >= '\x7f')) throw Unavailable();
            var binding = new DirectChatBinding(Guid.NewGuid(), ticket.Id, provider, model, secret.Epoch,
                Convert.ToHexStringLower(SHA256.HashData(body.Span)));
            var permit = await issue(binding, cancellationToken).ConfigureAwait(false);
            if (permit is null || !permit.IsPrepared(binding)) throw Unavailable();
            if (BeforeConnectForTests is { } beforeConnect) await beforeConnect(permit, cancellationToken).ConfigureAwait(false);
            if (!permit.IsPrepared(binding)) throw Unavailable();
            var route = new VerifiedInstanceRoute(provider.Account, secret.Epoch, provider.Name, model.Equivalence,
                "loopback", model.ServedId, 0, 0);
            var invocation = new VerifiedInstanceInvocation(route, binding.Invocation, provider.ChatUri);
            var issuer = new LoopbackIssuer(invocation, binding, permit, ticket, BeforeFirstWriteForTests);
            return await new VerifiedInstanceTransport(invocation, issuer)
                .SendAsync(invocation, body, "Bearer " + secret.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (OwnedPrivatePolicy.IsRecoverableAuthorityFailure(error))
        {
            throw ticket.CommercialMayHaveSent ? new RouterRequestException("commercial_exchange_failed", "the controlled chat exchange failed")
                : Unavailable();
        }
    }

    internal static RouterRequestException Unavailable() =>
        new("commercial_transport_unavailable", "the commercial transport has no authorized send");

    private sealed class LoopbackIssuer(VerifiedInstanceInvocation expected, DirectChatBinding binding,
        ControlledChatPermit permit, RouterTicket ticket, Action<DirectChatBinding>? beforeWrite) : IVerifiedInstanceStreamIssuer
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "A successful grant owns the NetworkStream and its socket. The catch disposes a failed connection.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created",
            Justification = "A successful grant owns the NetworkStream and its socket. The catch disposes a failed connection.")]
        public async ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken ct)
        {
            if (expected != invocation || !permit.IsPrepared(binding)) return null;
            var address = IPAddress.Parse(invocation.LogicalUri.IdnHost.Trim('[', ']'));
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, invocation.LogicalUri.Port), ct).ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: true);
                var stop = ct.UnsafeRegister(static value => ((Socket)value!).Dispose(), socket);
                return new(invocation, stream, async () =>
                {
                    await stop.DisposeAsync().ConfigureAwait(false);
                    await stream.DisposeAsync().ConfigureAwait(false);
                });
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
        {
            if (grant.Invocation != expected) return null;
            beforeWrite?.Invoke(binding);
            if (!permit.TryConsume(binding)) return null;
            ticket.CommercialMayHaveSent = true;
            return Use.Instance;
        }
    }

    private sealed class Use : IAsyncDisposable
    {
        internal static readonly Use Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed record ControlledChatCredential(long Epoch, string Token);

internal sealed record DirectChatBinding(Guid Invocation, string Ticket, DirectChatProvider Provider, DirectChatModel Model,
    long CredentialEpoch, string BodySha256);

internal sealed class ControlledChatPermit(DirectChatBinding approved)
{
    private int _state;
    internal bool Consumed => Volatile.Read(ref _state) == 1;
    internal bool IsPrepared(DirectChatBinding binding) => approved == binding && Volatile.Read(ref _state) == 0;
    internal void Revoke() => Interlocked.CompareExchange(ref _state, 2, 0);
    internal bool TryConsume(DirectChatBinding binding) => approved == binding && Interlocked.CompareExchange(ref _state, 1, 0) == 0;
}
