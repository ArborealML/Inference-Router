using System.Net;
using System.Runtime.ExceptionServices;

namespace Arboreal.InferenceRouter.Lifecycle;

/// <summary>Values frozen by the issuer when the exact serving route is committed.</summary>
internal readonly record struct VerifiedInstanceRoute(
    string HostGroup, long EnrollmentGeneration, string Engine, string EffectiveTarget,
    string ContainerId, string ServingIncarnation, long LaunchEpoch, long RouteIncarnation);

internal readonly record struct VerifiedInstanceInvocation(VerifiedInstanceRoute Route, Guid Id, Uri LogicalUri);

/// <summary>
/// The issuer owns authentication, route liveness and the physical admission order.
/// Checkout transfers one already-connected application stream for this invocation.
/// </summary>
internal interface IVerifiedInstanceStreamIssuer
{
    ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken ct);

    // Authorize only the exact, unspent grant that this issuer transferred for the invocation.
    // A non-null permit counts through issuer-confirmed terminal cleanup. This decision shares
    // ordering with route closure and does no I/O.
    IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant);
}

internal enum VerifiedCheckoutDisposition { Unknown, NoTransfer, ConfirmedRetired, Transferred }

internal readonly record struct VerifiedCheckout(VerifiedCheckoutDisposition Disposition, VerifiedInstanceGrant? Grant = null);

/// <summary>The fixture issuer certifies no usable transfer or retirement for its exact invocation.</summary>
internal interface IVerifiedDispatchIssuer : IVerifiedInstanceStreamIssuer
{
    ValueTask<VerifiedCheckout> CheckoutVerifiedAsync(VerifiedInstanceInvocation invocation, CancellationToken ct);
}

internal enum VerifiedRetirement { Unconfirmed, NoUse, Confirmed }

internal readonly record struct VerifiedTerminal(VerifiedRetirement Retirement,
    Exception? StreamCloseFailure = null, Exception? PermitFailure = null, Exception? ResponseCleanupFailure = null)
{
    internal bool Conclusive => Retirement != VerifiedRetirement.Unconfirmed;
}

internal sealed class VerifiedTransportOperation
{
    private readonly TaskCompletionSource<HttpResponseMessage> _primary = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _endOfUse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<VerifiedTerminal> _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<HttpResponseMessage> Primary => _primary.Task;
    internal Task<VerifiedTerminal> Terminal => _terminal.Task;
    internal Task EndOfUse => _endOfUse.Task;

    internal void PublishResponse(HttpResponseMessage response) => _primary.SetResult(response);

    internal void PublishFailure(Exception error)
    {
        if (error is OperationCanceledException canceled)
            _primary.TrySetCanceled(canceled.CancellationToken);
        else
            _primary.TrySetException(error);
    }

    internal void EndBorrow() => _endOfUse.TrySetResult();
    internal void Complete(VerifiedTerminal terminal) => _terminal.SetResult(terminal);

    internal async Task EndStandaloneAsync()
    {
        EndBorrow();
        var terminal = await Terminal.ConfigureAwait(false);
        if (CleanupFailure(terminal) is { } error) ExceptionDispatchInfo.Throw(error);
    }

    internal static Exception? CleanupFailure(VerifiedTerminal terminal)
    {
        var cleanup = terminal.PermitFailure ?? terminal.StreamCloseFailure;
        return terminal.ResponseCleanupFailure is { } response && cleanup is not null
            ? new AggregateException(response, cleanup) : terminal.ResponseCleanupFailure ?? cleanup;
    }
}

internal sealed class VerifiedInstanceGrant(
    VerifiedInstanceInvocation invocation, Stream applicationStream, Func<ValueTask> closeApplicationStream)
{
    internal VerifiedInstanceInvocation Invocation { get; } = invocation;
    internal Stream ApplicationStream { get; } = applicationStream;

    // The issuer supplies the cleanup for the channel it created. The exchange owns when
    // to invoke it; the stream exposed to the HTTP handler is a borrowed view.
    internal ValueTask CloseAsync() => closeApplicationStream();
}

/// <summary>Serializes one invocation over a handed-off channel. This has no public address connector.</summary>
internal sealed class VerifiedInstanceTransport(
    VerifiedInstanceInvocation expected, IVerifiedInstanceStreamIssuer issuer)
{
    private int _started;

    internal async Task<VerifiedInstanceExchange> SendAsync(
        VerifiedInstanceInvocation invocation, ReadOnlyMemory<byte> body, string? authorization, CancellationToken ct)
    {
        var operation = Start(invocation, body, authorization, certified: false, ct);
        try
        {
            return new VerifiedInstanceExchange(await operation.Primary.ConfigureAwait(false), operation);
        }
        catch (Exception error)
        {
            operation.EndBorrow();
            var terminal = await operation.Terminal.ConfigureAwait(false);
            if (VerifiedTransportOperation.CleanupFailure(terminal) is { } cleanup)
                throw new AggregateException(error, cleanup);
            throw;
        }
    }

    internal VerifiedTransportOperation SendVerified(VerifiedDispatchEnvelope envelope, CancellationToken ct) =>
        Start(envelope.Invocation, envelope.Body.AsMemory(), envelope.Credential.Authorization, certified: true, ct);

    private VerifiedTransportOperation Start(
        VerifiedInstanceInvocation invocation, ReadOnlyMemory<byte> body, string? authorization,
        bool certified, CancellationToken ct)
    {
        var operation = new VerifiedTransportOperation();
        _ = RunExchangeAsync(operation, invocation, body, authorization, certified, ct);
        return operation;
    }

    private async Task RunExchangeAsync(
        VerifiedTransportOperation operation, VerifiedInstanceInvocation invocation, ReadOnlyMemory<byte> body,
        string? authorization, bool certified, CancellationToken ct)
    {
        VerifiedInstanceGrant? grant = null;
        FirstWriteStream? stream = null;
        var retirement = VerifiedRetirement.NoUse;
        var matchingTransfer = false;
        Exception? responseCleanup = null;
        Exception? closeFailure = null;
        Exception? permitFailure = null;
        try
        {
            if (expected != invocation || invocation.Id == Guid.Empty)
                throw new InvalidOperationException("The invocation does not match the verified serving route.");
            var logical = invocation.LogicalUri;
            if (logical is null || !logical.IsAbsoluteUri || logical.Scheme is not ("http" or "https"))
                throw new ArgumentException("The verified invocation requires an HTTP or HTTPS origin.", nameof(invocation));
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("The verified invocation has already been used.");

            if (certified) ct.ThrowIfCancellationRequested();
            retirement = VerifiedRetirement.Unconfirmed;
            if (certified)
            {
                var checkedOut = await ((IVerifiedDispatchIssuer)issuer).CheckoutVerifiedAsync(invocation, ct)
                    .ConfigureAwait(false);
                grant = checkedOut.Grant;
                if (grant is null)
                {
                    retirement = checkedOut.Disposition switch
                    {
                        VerifiedCheckoutDisposition.NoTransfer => VerifiedRetirement.NoUse,
                        VerifiedCheckoutDisposition.ConfirmedRetired => VerifiedRetirement.Confirmed,
                        _ => VerifiedRetirement.Unconfirmed,
                    };
                }
                if (checkedOut.Disposition != VerifiedCheckoutDisposition.Transferred)
                    throw new HttpRequestException("The verified route did not issue a stream.");
            }
            else
            {
                grant = await issuer.CheckoutAsync(invocation, ct).ConfigureAwait(false);
            }
            if (grant is null)
                throw new HttpRequestException("The verified route did not issue a stream.");
            if (grant.Invocation != invocation)
                throw new HttpRequestException("The issuer returned a grant for a different invocation.");

            matchingTransfer = true;
            stream = new FirstWriteStream(grant, issuer);
            await UseChannelAsync(stream, logical, body, authorization, operation, ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (operation.Primary.IsCompletedSuccessfully) responseCleanup = error;
            else operation.PublishFailure(error);
        }
        finally
        {
            if (grant is not null)
            {
                try { await grant.CloseAsync().ConfigureAwait(false); }
                catch (Exception error) { closeFailure = error; }

                if (stream?.PhysicalUse is { } permit)
                {
                    try { await permit.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { permitFailure = error; }
                }
                if (matchingTransfer && closeFailure is null && permitFailure is null && responseCleanup is null)
                    retirement = stream?.PhysicalUse is null ? VerifiedRetirement.NoUse : VerifiedRetirement.Confirmed;
            }
            stream?.Dispose();
            operation.Complete(new VerifiedTerminal(retirement, closeFailure, permitFailure, responseCleanup));
        }
    }

    private static async Task UseChannelAsync(
        FirstWriteStream stream, Uri logical, ReadOnlyMemory<byte> body, string? authorization,
        VerifiedTransportOperation operation, CancellationToken ct)
    {
        // The stream is above any issuer TLS. HTTP on this synthetic URI selects only the
        // serializer; the single-use ConnectCallback supplies its only possible connection.
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri("http://instance.invalid" + logical.PathAndQuery))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ReadOnlyMemoryContent(body),
        };
        request.Headers.Host = logical.Authority;
        request.Headers.ConnectionClose = true;
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = stream.ConnectOnceAsync,
        };
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        operation.PublishResponse(response);
        await operation.EndOfUse.ConfigureAwait(false);
    }

    // Leaving the inner stream open here keeps cleanup in the exchange owner, even when the
    // HTTP handler drops its wrapper after a failed send or closes it on end of response.
    private sealed class FirstWriteStream(VerifiedInstanceGrant grant, IVerifiedInstanceStreamIssuer issuer) : Stream
    {
        private int _connectorUsed;
        private int _authorizationStarted;
        private Stream Inner => grant.ApplicationStream;
        internal IAsyncDisposable? PhysicalUse { get; private set; }

        internal ValueTask<Stream> ConnectOnceAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (context.DnsEndPoint.Port != 80 ||
                !string.Equals(context.DnsEndPoint.Host, "instance.invalid", StringComparison.OrdinalIgnoreCase) ||
                Interlocked.Exchange(ref _connectorUsed, 1) != 0)
                return ValueTask.FromException<Stream>(new HttpRequestException("The verified stream was already transferred."));
            return ValueTask.FromResult<Stream>(this);
        }

        private void Authorize(int byteCount)
        {
            if (byteCount == 0 || PhysicalUse is not null) return;
            if (Interlocked.Exchange(ref _authorizationStarted, 1) != 0)
                throw new HttpRequestException("A first write has already been decided for this invocation.");
            PhysicalUse = issuer.TryAuthorizeFirstWrite(grant)
                ?? throw new HttpRequestException("The issuer refused the first application write.");
        }

        public override bool CanRead => Inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => Inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => Inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => Inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            Authorize(count);
            Inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Authorize(buffer.Length);
            Inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Authorize(count);
            return Inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Authorize(buffer.Length);
            return Inner.WriteAsync(buffer, cancellationToken);
        }
    }
}


/// <summary>Holds borrowed response access until disposing signals the owning exchange worker.</summary>
internal sealed class VerifiedInstanceExchange(HttpResponseMessage response, VerifiedTransportOperation operation) : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _cleanup;

    internal HttpResponseMessage Response { get; } = response;

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_cleanup ??= operation.EndStandaloneAsync());
    }
}
