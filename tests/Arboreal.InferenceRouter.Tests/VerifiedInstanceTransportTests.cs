using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class VerifiedInstanceTransportTests
{
    private static readonly byte[] Payload = """{"model":"bound-model","prompt":"verified original"}"""u8.ToArray();

    [Fact(Timeout = 20_000)]
    public async Task ReboundPublicAddressReceivesNoVerifiedRequest()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var original = await LoopbackPair.OpenAsync(deadline.Token);
        using var publicAddress = new TcpListener(IPAddress.Loopback, 0);
        publicAddress.Start();
        var address = (IPEndPoint)publicAddress.LocalEndpoint;
        var invocation = Invocation(new Uri($"http://{address}/prefix/v1/completions?mode=raw%2Fbyte"));
        using var prepared = new HeldCheckout();
        var issuer = new FakeIssuer(invocation, async () =>
        {
            await prepared.HoldAsync(deadline.Token);
            return original.Client.GetStream();
        });
        var transfer = new VerifiedInstanceTransport(invocation, issuer);
        var backend = RespondAsync(original.Server.GetStream(), HttpStatusCode.OK, location: null, deadline.Token);
        var sending = transfer.SendAsync(invocation, Payload, "Bearer fixture", deadline.Token);

        await prepared.Reached.WaitAsync(deadline.Token);
        publicAddress.Stop();
        using var replacement = new TcpListener(address);
        replacement.Start();
        prepared.Release();

        await using (var exchange = await sending)
        {
            Assert.Equal(HttpStatusCode.OK, exchange.Response.StatusCode);
            var request = await backend;
            Assert.Contains("POST /prefix/v1/completions?mode=raw%2Fbyte HTTP/1.1\r\n", request.Headers, StringComparison.Ordinal);
            Assert.Contains($"Host: {address}\r\n", request.Headers, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Authorization: Bearer fixture\r\n", request.Headers, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Content-Type: application/json\r\n", request.Headers, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Payload, request.Body);
            Assert.False(replacement.Pending());
            Assert.Equal(1, issuer.Checkouts);
            Assert.Equal(1, issuer.PhysicalUses);
        }
        Assert.Equal(0, issuer.PhysicalUses);
        Assert.Equal(1, issuer.Releases);
        Assert.Equal(1, issuer.StreamCloses);

        // The replacement is live and receives a marker if sent a fresh public-address request.
        using var publicHandler = new SocketsHttpHandler { UseProxy = false };
        using var unsafeSender = new HttpClient(publicHandler, disposeHandler: false);
        byte[] marker = """{"model":"bound-model","prompt":"public replacement"}"""u8.ToArray();
        using var publicRequest = new ByteArrayContent(marker);
        var dialing = unsafeSender.PostAsync(invocation.LogicalUri, publicRequest, deadline.Token);
        using var contacted = await replacement.AcceptTcpClientAsync(deadline.Token);
        var reached = await RespondAsync(contacted.GetStream(), HttpStatusCode.OK, location: null, deadline.Token, marker.Length);
        using var publicResponse = await dialing;
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        Assert.Equal(marker, reached.Body);
    }

    [Fact(Timeout = 20_000)]
    public async Task HttpsMetadataUsesTheIssuerAuthenticatedApplicationStream()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pair = await LoopbackPair.OpenAsync(deadline.Token);
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var certificate = certificateRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddMinutes(5));
        var thumbprint = certificate.Thumbprint;
        using var client = new SslStream(pair.Client.GetStream(), leaveInnerStreamOpen: true,
            (_, remote, _, _) => remote is not null &&
                string.Equals(remote.GetCertHashString(), thumbprint, StringComparison.Ordinal));
        using var server = new SslStream(pair.Server.GetStream(), leaveInnerStreamOpen: true);
        await Task.WhenAll(
            server.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token),
            client.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, deadline.Token));

        var invocation = Invocation(new Uri("https://localhost:10443/inside?q=1"));
        var issuer = new FakeIssuer(invocation, () => ValueTask.FromResult<Stream>(client));
        var backend = RespondAsync(server, HttpStatusCode.OK, location: null, deadline.Token);
        await using var exchange = await new VerifiedInstanceTransport(invocation, issuer)
            .SendAsync(invocation, Payload, null, deadline.Token);

        var request = await backend;
        Assert.StartsWith("POST /inside?q=1 HTTP/1.1\r\n", request.Headers, StringComparison.Ordinal);
        Assert.Contains("Host: localhost:10443\r\n", request.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Payload, request.Body);
        Assert.Equal(1, issuer.Authorizations);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task RedirectReturnsStatusWithoutContactingReplacement(int status)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var original = await LoopbackPair.OpenAsync(deadline.Token);
        using var replacement = new TcpListener(IPAddress.Loopback, 0);
        replacement.Start();
        var address = (IPEndPoint)replacement.LocalEndpoint;
        var invocation = Invocation(new Uri($"http://{address}/original"));
        var issuer = new FakeIssuer(invocation, () => ValueTask.FromResult<Stream>(original.Client.GetStream()));
        var backend = RespondAsync(original.Server.GetStream(), (HttpStatusCode)status, $"http://{address}/redirected", deadline.Token);
        var transport = new VerifiedInstanceTransport(invocation, issuer);
        await using var exchange = await transport.SendAsync(invocation, Payload, null, deadline.Token);

        Assert.Equal((HttpStatusCode)status, exchange.Response.StatusCode);
        Assert.Equal(Payload, (await backend).Body);
        Assert.False(replacement.Pending());
        Assert.Equal(1, issuer.Checkouts);
        Assert.Equal(1, issuer.Authorizations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(invocation, Payload, null, deadline.Token));
        Assert.False(replacement.Pending());
    }

    [Theory]
    [InlineData("host")]
    [InlineData("enrollment")]
    [InlineData("engine")]
    [InlineData("target")]
    [InlineData("container")]
    [InlineData("process")]
    [InlineData("epoch")]
    [InlineData("route")]
    [InlineData("invocation")]
    public async Task MismatchRefusesCheckout(string field)
    {
        var expected = Invocation(new Uri("http://localhost/turn"));
        var issuer = new FakeIssuer(expected, static () => throw new InvalidOperationException("No checkout expected."));
        var route = expected.Route;
        var other = expected with
        {
            Id = field == "invocation" ? Guid.NewGuid() : expected.Id,
            Route = field switch
            {
                "host" => route with { HostGroup = "other" },
                "enrollment" => route with { EnrollmentGeneration = 9 },
                "engine" => route with { Engine = "other" },
                "target" => route with { EffectiveTarget = "other" },
                "container" => route with { ContainerId = "other" },
                "process" => route with { ServingIncarnation = "other" },
                "epoch" => route with { LaunchEpoch = 9 },
                "route" => route with { RouteIncarnation = 9 },
                _ => route,
            },
        };
        var transport = new VerifiedInstanceTransport(expected, issuer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(other, Payload, null, CancellationToken.None));
        Assert.Equal(0, issuer.Checkouts);
        Assert.Equal(0, issuer.Authorizations);
    }

    [Theory(Timeout = 20_000)]
    [InlineData("invocation")]
    [InlineData("container")]
    [InlineData("process")]
    public async Task WrongReturnedIdentityDisposesTheStreamWithoutWriting(string field)
    {
        var expected = Invocation(new Uri("http://localhost/turn"));
        var mismatched = field switch
        {
            "container" => expected with { Route = expected.Route with { ContainerId = "another-container" } },
            "process" => expected with { Route = expected.Route with { ServingIncarnation = "old-process" } },
            _ => expected with { Id = Guid.NewGuid() },
        };
        using var stream = new MemoryStream();
        var issuer = new FakeIssuer(mismatched, () => ValueTask.FromResult<Stream>(stream));
        var transport = new VerifiedInstanceTransport(expected, issuer);

        await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(expected, Payload, null, CancellationToken.None));
        Assert.Equal(1, issuer.Checkouts);
        Assert.Equal(0, issuer.Authorizations);
        Assert.Equal(1, issuer.StreamCloses);
        Assert.Empty(stream.ToArray());
        Assert.False(stream.CanWrite);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CloseFencesFirstWriteDuringCheckoutAndOnAnAlreadyHandedOffStream(bool duringCheckout)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pair = await LoopbackPair.OpenAsync(deadline.Token);
        var expected = Invocation(new Uri("http://localhost/turn"));
        using var prepared = new HeldCheckout();
        var issuer = new FakeIssuer(expected, async () =>
        {
            if (duringCheckout) await prepared.HoldAsync(deadline.Token);
            return pair.Client.GetStream();
        })
        {
            BeforeAuthorization = duringCheckout ? null : _ =>
                prepared.HoldAsync(deadline.Token).GetAwaiter().GetResult(),
        };
        var transport = new VerifiedInstanceTransport(expected, issuer);
        var pending = Task.Run(() => transport.SendAsync(expected, Payload, null, deadline.Token), deadline.Token);
        await prepared.Reached.WaitAsync(deadline.Token);
        issuer.Close();
        prepared.Release();

        await Assert.ThrowsAsync<HttpRequestException>(() => pending);
        Assert.Equal(0, await pair.Server.GetStream().ReadAsync(new byte[1], deadline.Token));
        Assert.Equal(1, issuer.Checkouts);
        Assert.Equal(0, issuer.Authorizations);
        Assert.Equal(0, issuer.Releases);
        Assert.Equal(1, issuer.StreamCloses);
    }

    [Fact(Timeout = 20_000)]
    public async Task IndependentTransportsCannotCheckOutTheSameInvocationTwice()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        using var original = await LoopbackPair.OpenAsync(ct);
        using var next = await LoopbackPair.OpenAsync(ct);
        using var checkout = new HeldCheckout();
        var invocation = Invocation(new Uri("http://localhost/turn"));
        var nextInvocation = invocation with { Id = Guid.NewGuid() };
        var connects = 0;
        var allowNext = 0;
        var issuer = new FakeIssuer(grantIdentity: null, async () =>
        {
            if (Interlocked.Increment(ref connects) != 1)
            {
                if (Volatile.Read(ref allowNext) == 0)
                    throw new InvalidOperationException("The duplicate invocation reached stream checkout.");
                return next.Client.GetStream();
            }
            await checkout.HoldAsync(ct);
            return original.Client.GetStream();
        });

        var first = new VerifiedInstanceTransport(invocation, issuer).SendAsync(invocation, Payload, null, ct);
        try
        {
            await checkout.Reached.WaitAsync(ct);
            await Assert.ThrowsAsync<HttpRequestException>(() => new VerifiedInstanceTransport(invocation, issuer)
                .SendAsync(invocation, Payload, null, ct));
            Assert.Equal(2, issuer.Checkouts);
            Assert.Equal(1, Volatile.Read(ref connects));
            Assert.Equal(0, issuer.Transfers);
            Assert.Equal(0, issuer.FirstWriteAttempts);
        }
        finally
        {
            checkout.Release();
        }

        var originalRead = RespondAsync(original.Server.GetStream(), HttpStatusCode.OK, location: null, ct);
        await using (var exchange = await first)
        {
            Assert.Equal(HttpStatusCode.OK, exchange.Response.StatusCode);
            Assert.Equal(Payload, (await originalRead).Body);
            Assert.Equal(1, issuer.Transfers);
            Assert.Equal(1, issuer.Authorizations);
            Assert.Equal(1, issuer.PhysicalUses);
        }

        await Assert.ThrowsAsync<HttpRequestException>(() => new VerifiedInstanceTransport(invocation, issuer)
            .SendAsync(invocation, Payload, null, ct));
        Assert.Equal(1, issuer.FirstWriteAttempts);
        Assert.Equal(1, issuer.StreamCloses);
        Assert.Equal(1, issuer.Releases);
        Assert.Equal(0, issuer.PhysicalUses);

        Volatile.Write(ref allowNext, 1);
        var nextRead = RespondAsync(next.Server.GetStream(), HttpStatusCode.OK, location: null, ct);
        await using (var exchange = await new VerifiedInstanceTransport(nextInvocation, issuer)
            .SendAsync(nextInvocation, Payload, null, ct))
        {
            Assert.Equal(HttpStatusCode.OK, exchange.Response.StatusCode);
            Assert.Equal(Payload, (await nextRead).Body);
        }
        Assert.Equal(4, issuer.Checkouts);
        Assert.Equal(2, Volatile.Read(ref connects));
        Assert.Equal(2, issuer.Transfers);
        Assert.Equal(2, issuer.FirstWriteAttempts);
        Assert.Equal(2, issuer.Authorizations);
        Assert.Equal(2, issuer.StreamCloses);
        Assert.Equal(2, issuer.Releases);
        Assert.Equal(0, issuer.PhysicalUses);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FirstWriteRequiresTheExactIssuedGrantEvenWhenInvocationMatches(bool substitute, bool alternateChannel)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        using var original = await LoopbackPair.OpenAsync(ct);
        using var alternate = await LoopbackPair.OpenAsync(ct);
        using var firstWrite = new HeldCheckout();
        var invocation = Invocation(new Uri("http://localhost/turn"));
        var originalStream = original.Client.GetStream();
        var substitutedStream = alternateChannel ? alternate.Client.GetStream() : originalStream;
        var substitutedCloses = 0;
        var wrongGrant = new VerifiedInstanceGrant(invocation, substitutedStream, () =>
        {
            Interlocked.Increment(ref substitutedCloses);
            // A same-stream substitute borrows the original grant's stream.
            return alternateChannel ? substitutedStream.DisposeAsync() : ValueTask.CompletedTask;
        });
        var presented = new TaskCompletionSource<VerifiedInstanceGrant>(TaskCreationOptions.RunContinuationsAsynchronously);
        var issuer = new FakeIssuer(invocation, () => ValueTask.FromResult<Stream>(originalStream))
        {
            SubstituteGrant = substitute ? wrongGrant : null,
            BeforeAuthorization = grant =>
            {
                presented.TrySetResult(grant);
                firstWrite.HoldAsync(ct).GetAwaiter().GetResult();
            },
        };

        var sending = Task.Run(() => new VerifiedInstanceTransport(invocation, issuer)
            .SendAsync(invocation, Payload, null, ct), ct);
        try
        {
            await firstWrite.Reached.WaitAsync(ct);
            var offered = await presented.Task.WaitAsync(ct);
            var issued = Assert.IsType<VerifiedInstanceGrant>(issuer.ReadIssuedGrant(invocation.Id));
            Assert.Same(originalStream, issued.ApplicationStream);
            Assert.Equal(invocation, offered.Invocation);
            Assert.Same(substitute ? substitutedStream : originalStream, offered.ApplicationStream);
            if (substitute)
            {
                Assert.NotSame(issued, offered);
                Assert.Same(wrongGrant, offered);
            }
            else
            {
                Assert.Same(issued, offered);
            }
            Assert.Equal(0, issuer.Authorizations);
            Assert.Equal(0, issuer.PhysicalUses);
        }
        finally
        {
            firstWrite.Release();
        }

        if (substitute)
        {
            var substitutedPeer = alternateChannel ? alternate.Server : original.Server;
            var firstByte = substitutedPeer.GetStream().ReadAsync(new byte[1], ct).AsTask();
            // A leaked byte fails immediately, even if the sender is still waiting for headers.
            if (await Task.WhenAny(firstByte, sending).WaitAsync(ct) == firstByte)
                Assert.Equal(0, await firstByte);
            await Assert.ThrowsAsync<HttpRequestException>(() => sending.WaitAsync(ct));
            Assert.Equal(1, Volatile.Read(ref substitutedCloses));
            Assert.Equal(0, issuer.StreamCloses);

            await issuer.RevokeWithheldGrantAsync(invocation.Id);
            Assert.Equal(0, await firstByte.WaitAsync(ct));
            if (alternateChannel)
                Assert.Equal(0, await original.Server.GetStream().ReadAsync(new byte[1], ct));
        }
        else
        {
            var received = RespondAsync(original.Server.GetStream(), HttpStatusCode.OK, location: null, ct);
            await using var exchange = await sending;
            Assert.Equal(HttpStatusCode.OK, exchange.Response.StatusCode);
            Assert.Equal(Payload, (await received).Body);
            Assert.Equal(1, issuer.PhysicalUses);
        }
        Assert.Equal(1, issuer.Checkouts);
        Assert.Equal(1, issuer.Transfers);
        Assert.Equal(1, issuer.FirstWriteAttempts);
        Assert.Equal(substitute ? 0 : 1, issuer.Authorizations);
        Assert.Equal(substitute ? 0 : 1, issuer.Releases);
        Assert.Equal(1, issuer.StreamCloses);
        Assert.Equal(0, issuer.PhysicalUses);
    }

    [Fact(Timeout = 20_000)]
    public async Task ATruncatedResponseCannotReplayTheInvocation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var original = await LoopbackPair.OpenAsync(deadline.Token);
        using var publicAddress = new TcpListener(IPAddress.Loopback, 0);
        publicAddress.Start();
        var invocation = Invocation(new Uri($"http://{publicAddress.LocalEndpoint}/completions"));
        var issuer = new FakeIssuer(invocation, () => ValueTask.FromResult<Stream>(original.Client.GetStream()));
        var transfer = new VerifiedInstanceTransport(invocation, issuer);
        var sending = transfer.SendAsync(invocation, Payload, null, deadline.Token);
        var server = original.Server.GetStream();
        Assert.Equal(Payload, (await ReadAsync(server, deadline.Token)).Body);
        await server.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 16\r\n\r\nshort"u8.ToArray(), deadline.Token);
        original.Server.Client.Shutdown(SocketShutdown.Send);

        await using var exchange = await sending;
        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => exchange.Response.Content.ReadAsByteArrayAsync(deadline.Token));
        Assert.Equal(HttpRequestError.ResponseEnded, error.HttpRequestError);
        Assert.Equal(1, issuer.Checkouts);
        Assert.Equal(1, issuer.Authorizations);
        Assert.False(publicAddress.Pending());
    }

    [Fact(Timeout = 20_000)]
    public async Task FailureAfterSubmissionClosesTheStreamAndRetiresThePermitOnce()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pair = await LoopbackPair.OpenAsync(deadline.Token);
        var invocation = Invocation(new Uri("http://localhost/turn"));
        var issuer = new FakeIssuer(invocation, () => ValueTask.FromResult<Stream>(pair.Client.GetStream()));
        var sending = new VerifiedInstanceTransport(invocation, issuer)
            .SendAsync(invocation, Payload, null, deadline.Token);

        var server = pair.Server.GetStream();
        Assert.Equal(Payload, (await ReadAsync(server, deadline.Token)).Body);
        pair.Server.Client.Shutdown(SocketShutdown.Send);

        await Assert.ThrowsAsync<HttpRequestException>(() => sending);
        Assert.Equal(0, await server.ReadAsync(new byte[1], deadline.Token));
        Assert.Equal(1, issuer.Checkouts);
        Assert.Equal(1, issuer.Authorizations);
        Assert.Equal(1, issuer.StreamCloses);
        Assert.Equal(1, issuer.Releases);
        Assert.Equal(0, issuer.PhysicalUses);
    }

    [Fact(Timeout = 20_000)]
    public async Task DisposeKeepsUseCountedUntilTheIssuerConfirmsTerminalCleanup()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var original = await LoopbackPair.OpenAsync(deadline.Token);
        using var terminal = new HeldCheckout();
        var invocation = Invocation(new Uri("http://localhost/turn"));
        var issuer = new FakeIssuer(invocation, () => ValueTask.FromResult<Stream>(original.Client.GetStream()))
        {
            BeforeRelease = () => terminal.HoldAsync(deadline.Token),
        };
        var backend = RespondAsync(original.Server.GetStream(), HttpStatusCode.OK, location: null, deadline.Token);
        await using var exchanging = await new VerifiedInstanceTransport(invocation, issuer)
            .SendAsync(invocation, Payload, null, deadline.Token);
        await backend;
        issuer.Close();
        var dispose = exchanging.DisposeAsync().AsTask();
        var concurrentDispose = exchanging.DisposeAsync().AsTask();
        Assert.Same(dispose, concurrentDispose);
        try
        {
            await terminal.Reached.WaitAsync(deadline.Token);
            Assert.Equal(1, issuer.PhysicalUses);
            Assert.Equal(0, issuer.Releases);
            Assert.False(dispose.IsCompleted);
            Assert.False(concurrentDispose.IsCompleted);
            Assert.Equal(1, issuer.StreamCloses);
            Assert.Equal(0, await original.Server.GetStream().ReadAsync(new byte[1], deadline.Token));
        }
        finally
        {
            terminal.Release();
            await Task.WhenAll(dispose, concurrentDispose);
        }
        Assert.Equal(0, issuer.PhysicalUses);
        Assert.Equal(1, issuer.Releases);
        Assert.Equal(1, issuer.StreamCloses);
    }

    private static VerifiedInstanceInvocation Invocation(Uri uri) => new(
        new VerifiedInstanceRoute("host", 1, "engine", "model/profile", "container", "process-1", 1, 1),
        Guid.NewGuid(), uri);

    private static async Task<(string Headers, byte[] Body)> RespondAsync(
        Stream stream, HttpStatusCode status, string? location, CancellationToken ct, int? bodyLength = null)
    {
        var received = await ReadAsync(stream, ct, bodyLength);
        var reply = $"HTTP/1.1 {(int)status} Fixture\r\nContent-Length: 0\r\nConnection: close\r\n";
        if (location is not null) reply += $"Location: {location}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\r\n"), ct);
        await stream.FlushAsync(ct);
        return received;
    }

    private static async Task<(string Headers, byte[] Body)> ReadAsync(Stream stream, CancellationToken ct, int? bodyLength = null)
    {
        using var headers = new MemoryStream();
        var octet = new byte[1];
        uint tail = 0;
        do
        {
            await stream.ReadExactlyAsync(octet, ct);
            headers.WriteByte(octet[0]);
            tail = (tail << 8) | octet[0];
            Assert.True(headers.Length < 4096);
        }
        while (tail != 0x0d0a0d0a);
        var text = Encoding.ASCII.GetString(headers.ToArray());
        var length = bodyLength ?? Payload.Length;
        Assert.Contains($"Content-Length: {length}\r\n", text, StringComparison.OrdinalIgnoreCase);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return (text, body);
    }

    private sealed class FakeIssuer(
        VerifiedInstanceInvocation? grantIdentity, Func<ValueTask<Stream>> connect) : IVerifiedInstanceStreamIssuer
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, VerifiedInstanceGrant?> _issued = [];
        private bool _open = true;
        internal int Checkouts { get; private set; }
        internal int Transfers { get; private set; }
        internal int FirstWriteAttempts { get; private set; }
        internal int Authorizations { get; private set; }
        internal int PhysicalUses { get; private set; }
        internal int Releases { get; private set; }
        private int _streamCloses;
        internal int StreamCloses => Volatile.Read(ref _streamCloses);
        internal Func<Task>? BeforeRelease { get; init; }
        internal Action<VerifiedInstanceGrant>? BeforeAuthorization { get; init; }
        internal VerifiedInstanceGrant? SubstituteGrant { get; init; }

        internal void Close() { lock (_gate) _open = false; }

        internal VerifiedInstanceGrant? ReadIssuedGrant(Guid invocationId)
        {
            lock (_gate) return _issued.GetValueOrDefault(invocationId);
        }

        internal ValueTask RevokeWithheldGrantAsync(Guid invocationId)
        {
            VerifiedInstanceGrant? withheld;
            lock (_gate)
            {
                withheld = _issued.GetValueOrDefault(invocationId);
                _issued[invocationId] = null;
            }
            return withheld?.CloseAsync() ?? ValueTask.CompletedTask;
        }

        public async ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                Checkouts++;
                if (!_open || !_issued.TryAdd(invocation.Id, null)) return null;
            }
            var stream = await connect();
            var grant = new VerifiedInstanceGrant(grantIdentity ?? invocation, stream, async () =>
            {
                await stream.DisposeAsync();
                Interlocked.Increment(ref _streamCloses);
            });
            lock (_gate)
            {
                _issued[invocation.Id] = grant;
                Transfers++;
            }
            return SubstituteGrant ?? grant;
        }

        public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
        {
            BeforeAuthorization?.Invoke(grant);
            lock (_gate)
            {
                FirstWriteAttempts++;
                if (!_open || !ReferenceEquals(grant, _issued.GetValueOrDefault(grant.Invocation.Id))) return null;
                _issued[grant.Invocation.Id] = null;
                Authorizations++;
                PhysicalUses++;
                return new Permit(this);
            }
        }

        private sealed class Permit(FakeIssuer owner) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                if (owner.BeforeRelease is { } before) await before();
                lock (owner._gate)
                {
                    owner.PhysicalUses--;
                    owner.Releases++;
                }
            }
        }
    }

    private sealed class HeldCheckout : IDisposable
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Reached => _reached.Task;
        internal Task HoldAsync(CancellationToken ct)
        {
            _reached.TrySetResult();
            return _release.Task.WaitAsync(ct);
        }
        internal void Release() => _release.TrySetResult();
        public void Dispose() => Release();
    }

    private sealed class LoopbackPair : IDisposable
    {
        private TcpClient? _server;
        internal TcpClient Client { get; } = new();
        internal TcpClient Server => _server ?? throw new InvalidOperationException("The listener has not accepted the pair.");

        internal static async Task<LoopbackPair> OpenAsync(CancellationToken ct)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            LoopbackPair? ownedPair = null;
            try
            {
                ownedPair = new LoopbackPair();
                await ownedPair.Client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, ct);
                await ownedPair.AcceptAsync(listener, ct);
                var connected = ownedPair;
                ownedPair = null;
                return connected;
            }
            finally
            {
                ownedPair?.Dispose();
            }
        }

        private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
        {
            if (_server is null) _server = await listener.AcceptTcpClientAsync(ct);
        }

        public void Dispose()
        {
            try { Client.Dispose(); }
            finally { _server?.Dispose(); }
        }
    }
}
