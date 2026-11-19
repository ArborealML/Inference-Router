using System.IO.Pipelines;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostCausalDrainTests
{
    private sealed class BackendAcknowledgements
    {
        private CausalBackendTerminal? _acknowledgment;

        public CausalBackendTerminal Complete(Guid closeId, CausalFrontierUse use)
        {
            var terminal = new CausalBackendTerminal(closeId, use);
            if (Interlocked.CompareExchange(ref _acknowledgment, terminal, null) is not null)
                throw new InvalidOperationException("The fake backend can acknowledge its invocation only once.");
            return terminal;
        }

        // Reference identity is the authored event in this in-process fake, separate from its payload.
        public bool Authored(CausalBackendTerminal terminal) => ReferenceEquals(Volatile.Read(ref _acknowledgment), terminal);
    }

    // The test issuer has exactly one immutable route and up to one submitted invocation.
    // The child controller receives evidence and never owns this permit or gate.
    private sealed class SurvivingIssuer(CausalRouteBinding binding, VerifiedInstanceInvocation invocation,
        Stream channel, Func<ValueTask> seal, BackendAcknowledgements backend) : IVerifiedInstanceStreamIssuer
    {
        private readonly object _gate = new();
        private readonly Dictionary<VerifiedInstanceGrant, CausalFrontierUse> _prepared = [];
        private readonly TaskCompletionSource _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<(Guid CloseId, CausalFrontierUse Use)> _closedFrontier =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Guid _closeId;
        private CausalFrontierUse? _submitted;
        private CausalBackendTerminal? _terminal;
        private bool _sealed;
        private int _releases;

        public Task<(Guid CloseId, CausalFrontierUse Use)> ClosedFrontier => _closedFrontier.Task;
        public int PhysicalUses { get { lock (_gate) return _submitted is not null && _releases == 0 ? 1 : 0; } }
        public int Releases { get { lock (_gate) return _releases; } }
        public int Prepared { get { lock (_gate) return _prepared.Count; } }

        public ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation requested, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_closeId != Guid.Empty || requested.Route != binding.Route || requested.Id == Guid.Empty ||
                    _submitted?.Invocation.Id == requested.Id || _prepared.Values.Any(g => g.Invocation.Id == requested.Id))
                    return ValueTask.FromResult<VerifiedInstanceGrant?>(null);

                var stamp = new CausalFrontierUse(requested, Guid.NewGuid(), binding.BackendId);
                var grant = requested == invocation
                    ? new VerifiedInstanceGrant(requested, channel, () => SealAsync(stamp))
                    : new VerifiedInstanceGrant(requested, Stream.Null, static () => ValueTask.CompletedTask);
                _prepared.Add(grant, stamp);
                return ValueTask.FromResult<VerifiedInstanceGrant?>(grant);
            }
        }

        public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
        {
            lock (_gate)
            {
                if (_closeId != Guid.Empty || _submitted is not null || grant.Invocation != invocation ||
                    !_prepared.Remove(grant, out var stamp)) return null;
                _submitted = stamp;
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
                    _prepared.Clear();
                    if (_submitted is { } submitted) _closedFrontier.SetResult((_closeId, submitted));
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

    private sealed class HeldApplicationChannel : Stream
    {
        private readonly Pipe _requests = new();
        private readonly Pipe _responses = new();
        private readonly SemaphoreSlim _writeFence = new(1);
        private int _receiverBytes;
        private bool _sealed;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _channelSealed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnteredWrite => _entered.Task;
        public Task ChannelSealed => _channelSealed.Task;
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

        public async Task<CausalBackendTerminal> RespondAsync(SurvivingIssuer issuer, BackendAcknowledgements backend,
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
            var (close, use) = await issuer.ClosedFrontier.WaitAsync(ct);
            await _responses.Writer.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray(), ct);
            await _responses.Writer.CompleteAsync();
            return backend.Complete(close, use);
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
