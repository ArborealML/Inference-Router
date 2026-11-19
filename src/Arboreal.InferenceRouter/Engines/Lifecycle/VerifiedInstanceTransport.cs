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
        if (expected != invocation || invocation.Id == Guid.Empty)
            throw new InvalidOperationException("The invocation does not match the verified serving route.");
        var logical = invocation.LogicalUri;
        if (logical is null || !logical.IsAbsoluteUri || logical.Scheme is not ("http" or "https"))
            throw new ArgumentException("The verified invocation requires an HTTP or HTTPS origin.", nameof(invocation));
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The verified invocation has already been used.");

        var grant = await issuer.CheckoutAsync(invocation, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException("The verified route did not issue a stream.");
        var responseReady = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var endOfUse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = RunExchangeAsync(issuer, invocation, grant, body, authorization, responseReady, endOfUse.Task, ct);
        var response = await responseReady.Task.ConfigureAwait(false);
        return new VerifiedInstanceExchange(response, endOfUse, cleanup);
    }

    private static async Task RunExchangeAsync(
        IVerifiedInstanceStreamIssuer issuer, VerifiedInstanceInvocation invocation, VerifiedInstanceGrant grant,
        ReadOnlyMemory<byte> body, string? authorization, TaskCompletionSource<HttpResponseMessage> responseReady,
        Task endOfUse, CancellationToken ct)
    {
        using var stream = new FirstWriteStream(grant, issuer);
        Exception? error = null;
        try
        {
            if (grant.Invocation != invocation)
                throw new HttpRequestException("The issuer returned a grant for a different invocation.");

            await UseChannelAsync(stream, invocation.LogicalUri, body, authorization, responseReady, endOfUse, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        try
        {
            // A physical permit represents issuer-confirmed retirement. Closing the
            // application stream is a separate step and does not itself release the permit.
            try { await grant.CloseAsync().ConfigureAwait(false); }
            finally
            {
                if (stream.PhysicalUse is { } permit)
                    await permit.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            error = error is null ? ex : new AggregateException(error, ex);
        }

        if (error is OperationCanceledException canceled && responseReady.TrySetCanceled(canceled.CancellationToken))
            return;
        if (error is not null && !responseReady.TrySetException(error))
            ExceptionDispatchInfo.Throw(error);
    }

    private static async Task UseChannelAsync(
        FirstWriteStream stream, Uri logical, ReadOnlyMemory<byte> body, string? authorization,
        TaskCompletionSource<HttpResponseMessage> responseReady, Task endOfUse, CancellationToken ct)
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
        responseReady.SetResult(response);
        await endOfUse.ConfigureAwait(false);
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
internal sealed class VerifiedInstanceExchange(
    HttpResponseMessage response, TaskCompletionSource endOfUse, Task cleanup) : IAsyncDisposable
{
    internal HttpResponseMessage Response { get; } = response;

    public ValueTask DisposeAsync()
    {
        endOfUse.TrySetResult();
        return new ValueTask(cleanup);
    }
}
