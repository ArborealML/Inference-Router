using System.Net;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class VerifiedInstanceTransportTests
{
    [Fact(Timeout = 20_000)]
    public async Task SameContainerRestartFencesAStaleGrantAndRequiresExplicitOpenAfterOldUseRetires()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        using var incumbent = await LoopbackPair.OpenAsync(ct);
        using var stale = await LoopbackPair.OpenAsync(ct);
        using var successor = await LoopbackPair.OpenAsync(ct);
        using var staleWrite = new HeldCheckout();
        using var terminal = new HeldCheckout();

        var initial = Invocation(new Uri("http://localhost/turn"));
        var delayed = initial with { Id = Guid.NewGuid() };
        var fresh = initial.Route with { ServingIncarnation = "process-2", RouteIncarnation = 2 };
        var issuer = new RestartingIssuer(initial.Route, Connect, BeforeWrite, () => terminal.HoldAsync(ct));

        var oldBackend = RespondAsync(incumbent.Server.GetStream(), HttpStatusCode.OK, location: null, ct);
        await using var oldExchange = await new VerifiedInstanceTransport(initial, issuer)
            .SendAsync(initial, Payload, null, ct);
        Assert.Equal(Payload, (await oldBackend).Body);
        Assert.Equal(1, issuer.PhysicalUses);

        var staleTransfer = new VerifiedInstanceTransport(delayed, issuer);
        var staleSend = Task.Run(() => staleTransfer.SendAsync(delayed, Payload, null, ct), ct);
        await staleWrite.Reached.WaitAsync(ct);
        issuer.ObserveRestart(fresh.ServingIncarnation);
        var oldCleanup = oldExchange.DisposeAsync().AsTask();
        try
        {
            await terminal.Reached.WaitAsync(ct);
            Assert.Equal(0, await incumbent.Server.GetStream().ReadAsync(new byte[1], ct));
            Assert.Equal(initial.Route.ContainerId, fresh.ContainerId);
            Assert.Equal(initial.Route.LaunchEpoch, fresh.LaunchEpoch);
            Assert.Equal(initial.Route.EffectiveTarget, fresh.EffectiveTarget);

            // The retained launch still names the same container. Its previous process alone
            // cannot satisfy readiness for the new route, even with the proposed generation.
            Assert.False(issuer.Stage(fresh with { ServingIncarnation = initial.Route.ServingIncarnation }));
            Assert.True(issuer.Stage(fresh));
            Assert.False(issuer.Open(fresh));
            await RefusedAsync(fresh);

            Assert.True(issuer.Complete(fresh));
            Assert.False(issuer.Open(fresh));
            await RefusedAsync(fresh);
            Assert.Equal(1, issuer.PhysicalUses);
            Assert.Equal(0, issuer.Releases);
            Assert.Equal(2, issuer.Grants);
            Assert.False(staleSend.IsCompleted);

            terminal.Release();
            await oldCleanup.WaitAsync(ct);
            Assert.Equal(0, issuer.PhysicalUses);
            Assert.Equal(1, issuer.Releases);
            await RefusedAsync(fresh);
            Assert.Equal(2, issuer.Grants);
            Assert.False(issuer.Open(fresh with { RouteIncarnation = initial.Route.RouteIncarnation }));
            Assert.True(issuer.Open(fresh));

            // Release a genuinely prepared old stream while the successor is already usable.
            // Global closure and a failed connection cannot account for this refusal.
            staleWrite.Release();
            await Assert.ThrowsAsync<HttpRequestException>(() => staleSend);
            Assert.Equal(0, await stale.Server.GetStream().ReadAsync(new byte[1], ct));
            await RefusedAsync(initial.Route);
            Assert.Equal(2, issuer.Grants);
            Assert.Equal(1, issuer.Authorizations);

            var next = initial with { Id = Guid.NewGuid(), Route = fresh };
            byte[] successorBody = """{"model":"bound-model","prompt":"fresh process"}"""u8.ToArray();
            var newBackend = RespondAsync(successor.Server.GetStream(), HttpStatusCode.OK, location: null, ct,
                successorBody.Length);
            await using var newExchange = await new VerifiedInstanceTransport(next, issuer)
                .SendAsync(next, successorBody, null, ct);
            Assert.Equal(HttpStatusCode.OK, newExchange.Response.StatusCode);
            Assert.Equal(successorBody, (await newBackend).Body);
            Assert.Equal(3, issuer.Grants);
            Assert.Equal(2, issuer.Authorizations);
        }
        finally
        {
            staleWrite.Release();
            terminal.Release();
            await oldCleanup;
        }

        Stream Connect(VerifiedInstanceInvocation invocation)
        {
            if (invocation.Id == initial.Id) return incumbent.Client.GetStream();
            if (invocation.Id == delayed.Id) return stale.Client.GetStream();
            return successor.Client.GetStream();
        }

        void BeforeWrite(VerifiedInstanceInvocation invocation)
        {
            if (invocation.Id == delayed.Id)
                staleWrite.HoldAsync(ct).GetAwaiter().GetResult();
        }

        async Task RefusedAsync(VerifiedInstanceRoute route)
        {
            var invocation = initial with { Id = Guid.NewGuid(), Route = route };
            await Assert.ThrowsAsync<HttpRequestException>(() => new VerifiedInstanceTransport(invocation, issuer)
                .SendAsync(invocation, Payload, null, ct));
        }
    }

    private sealed class RestartingIssuer(
        VerifiedInstanceRoute launched, Func<VerifiedInstanceInvocation, Stream> connect,
        Action<VerifiedInstanceInvocation> beforeWrite, Func<Task> waitForOldTerminal)
        : IVerifiedInstanceStreamIssuer
    {
        private enum Phase { Closed, Staged, Completed, Open }

        private readonly object _gate = new();
        private readonly Dictionary<Guid, VerifiedInstanceGrant?> _issued = [];
        private readonly VerifiedInstanceRoute _launched = launched;
        private VerifiedInstanceRoute _route = launched;
        private string _observedProcess = launched.ServingIncarnation;
        private Phase _phase = Phase.Open;
        internal int Grants { get; private set; }
        internal int Authorizations { get; private set; }
        internal int PhysicalUses { get; private set; }
        internal int Releases { get; private set; }

        internal void ObserveRestart(string process)
        {
            lock (_gate)
            {
                _observedProcess = process;
                _phase = Phase.Closed;
            }
        }

        internal bool Stage(VerifiedInstanceRoute proof)
        {
            lock (_gate)
            {
                var expected = _launched with
                {
                    ServingIncarnation = _observedProcess,
                    RouteIncarnation = _launched.RouteIncarnation + 1,
                };
                if (_phase != Phase.Closed || proof != expected) return false;
                _route = proof;
                _phase = Phase.Staged;
                return true;
            }
        }

        internal bool Complete(VerifiedInstanceRoute proof)
        {
            lock (_gate)
            {
                if (_phase != Phase.Staged || _route != proof) return false;
                _phase = Phase.Completed;
                return true;
            }
        }

        internal bool Open(VerifiedInstanceRoute proof)
        {
            lock (_gate)
            {
                if (_phase != Phase.Completed || _route != proof || PhysicalUses != 0) return false;
                _phase = Phase.Open;
                return true;
            }
        }

        public ValueTask<VerifiedInstanceGrant?> CheckoutAsync(VerifiedInstanceInvocation invocation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_phase != Phase.Open || _route != invocation.Route || !_issued.TryAdd(invocation.Id, null))
                    return ValueTask.FromResult<VerifiedInstanceGrant?>(null);
                Grants++;
            }

            var stream = connect(invocation);
            var grant = new VerifiedInstanceGrant(invocation, stream, stream.DisposeAsync);
            lock (_gate) _issued[invocation.Id] = grant;
            return ValueTask.FromResult<VerifiedInstanceGrant?>(grant);
        }

        public IAsyncDisposable? TryAuthorizeFirstWrite(VerifiedInstanceGrant grant)
        {
            var invocation = grant.Invocation;
            beforeWrite(invocation);
            lock (_gate)
            {
                if (!ReferenceEquals(grant, _issued.GetValueOrDefault(invocation.Id))) return null;
                _issued[invocation.Id] = null;
                if (_phase != Phase.Open || _route != invocation.Route) return null;
                PhysicalUses++;
                Authorizations++;
                return new Permit(this, invocation.Route == _launched);
            }
        }

        private async ValueTask RetireAsync(bool old)
        {
            if (old) await waitForOldTerminal();
            lock (_gate)
            {
                PhysicalUses--;
                Releases++;
            }
        }

        private sealed class Permit(RestartingIssuer owner, bool old) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => owner.RetireAsync(old);
        }
    }
}
