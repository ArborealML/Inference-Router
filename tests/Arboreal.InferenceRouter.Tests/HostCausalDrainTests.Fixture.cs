using System.IO.Pipelines;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostCausalDrainTests
{
    // The backend establishes the channel identity before checkout. The serving process label is
    // fixture metadata shared with the issuer, rather than a process attestation.
    private sealed class BackendAcknowledgements(VerifiedInstanceInvocation invocation, Guid backendId)
    {
        private readonly Guid _otherGrant = Guid.NewGuid();
        private CausalBackendTerminal? _acknowledgment;
        private CausalBackendTerminal? _mismatchedAcknowledgment;
        public CausalFrontierUse ChannelIdentity { get; } = new(invocation, Guid.NewGuid(), backendId);

        public CausalBackendTerminal Complete(Guid closeId)
        {
            var terminal = new CausalBackendTerminal(closeId, ChannelIdentity);
            if (Interlocked.CompareExchange(ref _acknowledgment, terminal, null) is not null)
                throw new InvalidOperationException("The fake backend can acknowledge its invocation only once.");
            return terminal;
        }

        public (CausalBackendTerminal Matched, CausalBackendTerminal WrongGrant) CompleteDelivery(Guid closeId)
        {
            var matched = Complete(closeId);
            // Inject a backend attribution fault after observing the request on its paired channel.
            var wrong = new CausalBackendTerminal(closeId, ChannelIdentity with { GrantId = _otherGrant });
            Volatile.Write(ref _mismatchedAcknowledgment, wrong);
            return (matched, wrong);
        }

        // Reference identity is the authored event in this in-process fake, separate from its payload.
        public bool Authored(CausalBackendTerminal terminal) => ReferenceEquals(Volatile.Read(ref _acknowledgment), terminal) ||
            ReferenceEquals(Volatile.Read(ref _mismatchedAcknowledgment), terminal);
    }

    // The test issuer has exactly one immutable route and up to one submitted invocation.
    // The child controller receives evidence and never owns this permit or gate.
    private sealed class SurvivingIssuer(CausalRouteBinding binding, VerifiedInstanceInvocation invocation,
        Stream channel, Func<ValueTask> seal, BackendAcknowledgements backend) : IVerifiedInstanceStreamIssuer
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Guid> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private VerifiedInstanceGrant? _prepared;
        private Guid _closeId;
        private CausalFrontierUse? _submitted;
        private CausalBackendTerminal? _terminal;
        private bool _sealed;
        private int _releases;

        public Func<VerifiedInstanceGrant, Task>? HoldCheckout { private get; init; }
        public Task<Guid> ClosedGeneration => _closed.Task;
        public int PhysicalUses { get { lock (_gate) return _submitted is not null && _releases == 0 ? 1 : 0; } }
        public int Releases { get { lock (_gate) return _releases; } }
        public bool Prepared { get { lock (_gate) return _prepared is not null; } }

        public async ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation requested, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            VerifiedInstanceGrant grant;
            lock (_gate)
            {
                var stamp = backend.ChannelIdentity;
                if (_closeId != Guid.Empty || requested.Route != binding.Route || requested != invocation ||
                    requested != stamp.Invocation || requested.Id == Guid.Empty ||
                    stamp.BackendId != binding.BackendId || _submitted is not null || _prepared is not null) return null;

                grant = new VerifiedInstanceGrant(requested, channel, () => SealAsync(stamp));
                _prepared = grant;
            }
            if (HoldCheckout is { } hold) await hold(grant).WaitAsync(ct);
            return grant;
        }

        public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
        {
            lock (_gate)
            {
                if (_closeId != Guid.Empty || _submitted is not null || _prepared is null || !ReferenceEquals(_prepared, grant))
                    return null;
                _prepared = null;
                _submitted = backend.ChannelIdentity;
                return new Permit(_retired.Task);
            }
        }

        public CausalDrainReply CloseAndObserve(CausalDrainRequest request)
        {
            lock (_gate)
            {
                var incumbent = binding.Incumbent;
                if (request.Binding != binding || request.Nonce == Guid.Empty || request.ControllerPid <= 0 ||
                    request.Stop.Kind != HostMutationKind.Stop || request.Stop.Epoch != request.Epoch ||
                    request.Stop.Id == Guid.Empty || request.Stop.Target != incumbent.InstanceId ||
                    request.Stop.Engine != incumbent.Operation.Engine || request.Epoch <= incumbent.Operation.Epoch ||
                    request.Successor == incumbent.Plan || !request.Successor.IsValid())
                    return new CausalDrainReply(request, Guid.Empty, null);

                if (_closeId == Guid.Empty)
                {
                    _closeId = Guid.NewGuid();
                    _prepared = null;
                    _closed.SetResult(_closeId);
                }

                // This fixture only certifies the exercised one-use frontier. There is no reopen API.
                var witness = _releases == 1 && _submitted is { } use && _terminal is { } terminal && _sealed
                    ? new CausalRetirementWitness(binding, _closeId, use, terminal, NoMoreWrites: true) : null;
                return new CausalDrainReply(request, _closeId, witness);
            }
        }

        public bool AcceptTerminal(CausalBackendTerminal terminal)
        {
            lock (_gate)
            {
                if (_closeId == Guid.Empty || terminal.CloseId != _closeId || terminal.Use != _submitted || _terminal is not null ||
                    !backend.Authored(terminal))
                    return false;
                _terminal = terminal;
                TryRetire();
                return true;
            }
        }

        private async ValueTask SealAsync(CausalFrontierUse stamp)
        {
            await seal();
            lock (_gate)
            {
                if (_submitted != stamp) return;
                _sealed = true;
                TryRetire();
            }
        }

        private void TryRetire()
        {
            if (!_sealed || _terminal is null || _releases != 0) return;
            _releases++;
            _retired.SetResult();
        }

        private sealed class Permit(Task retired) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => new(retired);
        }
    }

    private sealed class HeldApplicationChannel(BackendAcknowledgements backend) : Stream
    {
        private readonly Pipe _requests = new();
        private readonly Pipe _responses = new();
        private readonly SemaphoreSlim _writeFence = new(1);
        private int _receiverBytes;
        private bool _sealed;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _channelSealed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<byte[]> _requestDelivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnteredWrite => _entered.Task;
        public Task ChannelSealed => _channelSealed.Task;
        public Task<byte[]> RequestDelivered => _requestDelivered.Task;
        public int ReceiverBytes => Volatile.Read(ref _receiverBytes);
        public void ReleaseWrite() => _allowWrite.TrySetResult();

        public async ValueTask SealAsync()
        {
            await _writeFence.WaitAsync();
            try
            {
                _sealed = true;
                await _requests.Writer.CompleteAsync();
            }
            finally { _writeFence.Release(); }
            _channelSealed.TrySetResult();
        }

        public async Task<(CausalBackendTerminal Matched, CausalBackendTerminal WrongGrant)> RespondAsync(Task<Guid> closedGeneration,
            byte[] expected, CancellationToken ct)
        {
            using var request = _requests.Reader.AsStream(leaveOpen: true);
            var single = new byte[1];
            using var header = new MemoryStream();
            while (header.Length < 4096)
            {
                await request.ReadExactlyAsync(single, ct);
                Interlocked.Increment(ref _receiverBytes);
                header.WriteByte(single[0]);
                if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8))
                    break;
            }
            var body = new byte[expected.Length];
            await request.ReadExactlyAsync(body, ct);
            Interlocked.Add(ref _receiverBytes, body.Length);
            Assert.Equal(expected, body);
            _requestDelivered.SetResult(body);
            var close = await closedGeneration.WaitAsync(ct);
            await _responses.Writer.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray(), ct);
            await _responses.Writer.CompleteAsync();
            return backend.CompleteDelivery(close);
        }

        public async Task<int> ObserveReceiverEndAsync(CancellationToken ct)
        {
            using var stream = _requests.Reader.AsStream(leaveOpen: true);
            var bytes = await stream.ReadAsync(new byte[1], ct);
            Interlocked.Add(ref _receiverBytes, bytes);
            return bytes;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return;
            // The verified transport has already returned from issuer authorization at this inner write.
            _entered.TrySetResult();
            await _allowWrite.Task.WaitAsync(cancellationToken);
            await _writeFence.WaitAsync(cancellationToken);
            try
            {
                if (_sealed) throw new IOException("The application channel is sealed.");
                await _requests.Writer.WriteAsync(buffer, cancellationToken);
            }
            finally { _writeFence.Release(); }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            while (true)
            {
                var read = await _responses.Reader.ReadAsync(cancellationToken);
                var length = (int)Math.Min(buffer.Length, read.Buffer.Length);
                if (length != 0)
                {
                    var bytes = read.Buffer.Slice(0, length);
                    foreach (var segment in bytes)
                    {
                        segment.CopyTo(buffer);
                        buffer = buffer[segment.Length..];
                    }
                    _responses.Reader.AdvanceTo(bytes.End);
                    return length;
                }
                _responses.Reader.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted || read.IsCanceled) return 0;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _requests.Reader.Complete();
                _requests.Writer.Complete();
                _responses.Reader.Complete();
                _responses.Writer.Complete();
                _writeFence.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
