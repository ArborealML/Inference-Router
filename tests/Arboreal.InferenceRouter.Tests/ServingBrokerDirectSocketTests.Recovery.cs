using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class ServingBrokerDirectSocketTests
{
    [Fact]
    public void BrokerReadsOnlyTheAnchoredSlotAfterRefusalAndLostReadback()
    {
        using var fixture = new BrokerDiskFixture();
        using (var tail = new FileStream(fixture.Records, FileMode.Append, FileAccess.Write))
            tail.Write("unanchored legacy suffix"u8);
        var original = Assert.IsType<ServingBrokerSnapshot>(fixture.Store.Read());
        var proposed = original.Record with { Revision = 1, Predecessor = original.Digest, ApplicationWriter = Guid.NewGuid() };
        fixture.Head.RefuseNext = true;
        Assert.False(fixture.Store.TryAdvance(original, proposed));
        var reopened = fixture.Open();
        Assert.Equal(original.Digest, reopened.Read()?.Digest);
        Assert.Equal(Guid.Empty, reopened.Read()?.Record.ApplicationWriter);

        fixture.Head.LoseNextReply = true;
        Assert.True(reopened.TryAdvance(original, proposed));
        var committed = Assert.IsType<ServingBrokerSnapshot>(fixture.Open().Read());
        Assert.Equal(proposed.ApplicationWriter, committed.Record.ApplicationWriter);

        var next = committed.Record with { Revision = 2, Predecessor = committed.Digest };
        fixture.Head.LoseNextReply = true;
        fixture.Head.LoseReadbackOnCommit = true;
        Assert.False(reopened.TryAdvance(committed, next));
        Assert.Null(fixture.Open().Read());
        fixture.Head.LoseReadback = false;
        Assert.Equal(next.Revision, fixture.Open().Read()?.Record.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartReprobesOnlyTheExactSettledInitialResident(bool openBeforeFailure)
    {
        using var fixture = new BrokerDiskFixture();
        var ledger = fixture.Ledger();
        var writer = Assert.IsType<Guid>(ledger.BeginInitialProof(fixture.Binding, observedOpen: false));
        var probe = Guid.NewGuid();
        CompleteProbe(ledger, fixture.Binding, probe, ServingBrokerUseKind.Models);
        CompleteProbe(ledger, fixture.Binding, probe, ServingBrokerUseKind.State);
        if (openBeforeFailure) Assert.True(ledger.TryDelegateOpen(fixture.Binding, probe));
        var installed = Assert.IsType<ServingBrokerSnapshot>(fixture.Store.Read());
        Assert.Equal(openBeforeFailure, installed.Record.Open);
        Assert.NotEqual(0, installed.Record.Revision);

        var reopened = fixture.Ledger();
        Assert.Null(reopened.BeginInitialProof(fixture.Binding, observedOpen: !openBeforeFailure));
        Assert.Null(reopened.BeginInitialProof(fixture.Binding with { PolicyDigest = new string('D', 64) }, openBeforeFailure));
        Assert.Null(new ServingBrokerLedger(fixture.Binding.Scope, Guid.NewGuid(), fixture.Open())
            .BeginInitialProof(fixture.Binding, openBeforeFailure));
        Assert.Equal(installed.Digest, fixture.Store.Read()?.Digest);
        Assert.NotEqual(writer, Assert.IsType<Guid>(reopened.BeginInitialProof(fixture.Binding, openBeforeFailure)));
        Assert.True(reopened.OwnsClosedInitial(fixture.Binding, fixture.Epoch));
        Assert.False(reopened.TryDelegateOpen(fixture.Binding, probe));

        var replacement = Guid.NewGuid();
        CompleteProbe(reopened, fixture.Binding, replacement, ServingBrokerUseKind.Models);
        CompleteProbe(reopened, fixture.Binding, replacement, ServingBrokerUseKind.State);
        Assert.True(reopened.TryDelegateOpen(fixture.Binding, replacement));
        Assert.True(reopened.CanAttachInitial(fixture.Binding));
    }

    [Fact]
    public void LongLivedBrokerKeepsBoundedDiskAndRejectsReplayAcrossRolloverAndRestart()
    {
        using var fixture = new BrokerDiskFixture();
        var ledger = fixture.Ledger();
        var writer = Assert.IsType<Guid>(ledger.BeginInitialProof(fixture.Binding, observedOpen: false));
        OpenProbed(ledger, fixture.Binding);
        var original = Invoke(fixture.Binding.Route, 1, writer);
        var held = StartUse(ledger, fixture.Binding, original);
        var latest = original;
        for (var i = 2; i <= 640; i++)
        {
            latest = Invoke(fixture.Binding.Route, i, writer);
            var used = StartUse(ledger, fixture.Binding, latest);
            Assert.True(ledger.Retire(used, Terminal(used.Accepted!)));
        }
        var high = Invoke(fixture.Binding.Route, 700_000, writer);
        var final = StartUse(ledger, fixture.Binding, high);
        Assert.True(ledger.Retire(final, Terminal(final.Accepted!)));
        Assert.False(ledger.Retire(final, Terminal(final.Accepted!)));

        var snapshot = Assert.IsType<ServingBrokerSnapshot>(fixture.Open().Read());
        Assert.Single(snapshot.Record.Uses, use => use.Kind == ServingBrokerUseKind.Application && use == held);
        Assert.InRange(new FileInfo(fixture.Records + ".0").Length, 1, 16_384);
        Assert.InRange(new FileInfo(fixture.Records + ".1").Length, 1, 16_384);
        Assert.Null(ledger.Reserve(fixture.Binding, ServingBrokerUseKind.Application, latest.Id, latest));
        Assert.Null(ledger.Reserve(fixture.Binding, ServingBrokerUseKind.Application, high.Id, high));
        Assert.Null(ledger.BeginInitialProof(fixture.Binding, observedOpen: true));
        Assert.True(ledger.Retire(held, Terminal(held.Accepted!)));

        // This fresh old-writer request was never reserved and lies within the numeric window.
        var latePrepared = Invoke(fixture.Binding.Route, high.Sequence + 1, writer);
        var reopened = fixture.Ledger();
        var replacementWriter = Assert.IsType<Guid>(reopened.BeginInitialProof(fixture.Binding, observedOpen: true));
        OpenProbed(reopened, fixture.Binding);
        Assert.Null(reopened.Reserve(fixture.Binding, ServingBrokerUseKind.Application, latePrepared.Id, latePrepared));
        var first = Invoke(fixture.Binding.Route, VerifiedInstanceInvocation.IssueSequence(), replacementWriter);
        Assert.True(first.Sequence > high.Sequence);
        var next = StartUse(reopened, fixture.Binding, first);
        Assert.True(reopened.Retire(next, Terminal(next.Accepted!)));
        Assert.Null(reopened.Reserve(fixture.Binding, ServingBrokerUseKind.Application, first.Id, first));

        var earlier = Invoke(fixture.Binding.Route, VerifiedInstanceInvocation.IssueSequence(), replacementWriter);
        var later = Invoke(fixture.Binding.Route, VerifiedInstanceInvocation.IssueSequence(), replacementWriter);
        foreach (var arrived in new[] { later, earlier })
        {
            var accepted = StartUse(reopened, fixture.Binding, arrived);
            Assert.True(reopened.Retire(accepted, Terminal(accepted.Accepted!)));
            Assert.Null(reopened.Reserve(fixture.Binding, ServingBrokerUseKind.Application, arrived.Id, arrived));
        }

        static VerifiedInstanceInvocation Invoke(VerifiedInstanceRoute route, long sequence, Guid applicationWriter) =>
            new(route, Guid.NewGuid(), new Uri("http://127.0.0.1/v1/chat/completions"), sequence, applicationWriter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeReconcilesAnExactReservedUseAfterIssueFailure(bool issueCommittedWithoutReceipt)
    {
        using var fixture = new BrokerDiskFixture();
        var ledger = fixture.Ledger();
        Assert.NotNull(ledger.BeginInitialProof(fixture.Binding, observedOpen: false));
        var refusing = new RefusedIssueStore(fixture.Store, issueCommittedWithoutReceipt);
        ledger = new ServingBrokerLedger(fixture.Binding.Scope, fixture.Epoch, refusing);
        var binding = fixture.Binding;
        var plan = binding.Applied.State.AppliedLaunch!.Plan;
        using var invalidHandle = new SafeFileHandle(-1, ownsHandle: false);
        var backend = new ServingBackendEndpoint(new("127.0.0.1", 1), invalidHandle, new(0, 0), new(0, 0), new(0, 0),
            new(plan.Model, plan.Tier!, 8192, binding.TotalSlots), new UnusedSocketEvidence());
        var probe = new ServingBrokerProbe(new Uri("http://127.0.0.1:1/models"), 8192);
        using var deadline = ReadinessDeadline.Start(TimeProvider.System, TimeSpan.FromSeconds(10), CancellationToken.None);
        var readiness = new ServingBrokerReadiness(ledger, new Lock(), TimeSpan.FromSeconds(1));
        var credential = VerifiedDispatchCredential.Capture(new EngineDefinition { Name = binding.Route.Engine,
            Endpoint = probe.Uri }, resolvedKey: null, epoch: 1);
        var result = await readiness.ReadAsync(binding, backend,
            new(binding.PolicyDigest, credential, probe, probe), deadline,
            static (_, _) => throw new InvalidOperationException("The connector must never be used."));
        Assert.Null(result.Observation);
        var uses = Assert.IsType<ServingBrokerSnapshot>(fixture.Open().Read()).Record.Uses;
        if (issueCommittedWithoutReceipt)
        {
            Assert.Equal(ServingBrokerUsePhase.Issued, Assert.Single(uses).Phase);
            Assert.Null(fixture.Ledger().BeginInitialProof(binding, observedOpen: false));
        }
        else
        {
            Assert.Empty(uses);
            Assert.NotNull(fixture.Ledger().BeginInitialProof(binding, observedOpen: false));
        }
    }

    private static void OpenProbed(ServingBrokerLedger ledger, ServingBrokerBinding binding)
    {
        var exchange = Guid.NewGuid();
        CompleteProbe(ledger, binding, exchange, ServingBrokerUseKind.Models);
        CompleteProbe(ledger, binding, exchange, ServingBrokerUseKind.State);
        Assert.True(ledger.TryDelegateOpen(binding, exchange));
    }

    private static void CompleteProbe(ServingBrokerLedger ledger, ServingBrokerBinding binding, Guid exchange, ServingBrokerUseKind kind)
    {
        var use = StartUse(ledger, binding, null, exchange, kind);
        Assert.True(ledger.CompleteProbe(use, Terminal(use.Accepted!)));
    }

    private static ServingBrokerUse StartUse(ServingBrokerLedger ledger, ServingBrokerBinding binding,
        VerifiedInstanceInvocation? invocation, Guid? exchange = null, ServingBrokerUseKind kind = ServingBrokerUseKind.Application)
    {
        var reserved = Assert.IsType<ServingBrokerUse>(ledger.Reserve(binding, kind, invocation?.Id ?? exchange!.Value, invocation));
        var issued = Assert.IsType<ServingBrokerUse>(ledger.Issue(reserved));
        var request = new ServingHostSocketRequest(ServingBrokerIssuer.ServingIdentity(binding), issued.Id, kind,
            invocation, new(1, 2), new(10, 12), new(11, 13), new("127.0.0.1", 1234), new("127.0.0.1", 5678));
        var ordinal = invocation is { } application ? checked((ulong)application.Sequence + 10UL) : (ulong)kind;
        return Assert.IsType<ServingBrokerUse>(ledger.BindAccepted(issued, new(request, ordinal, ordinal, new string('A', 64))));
    }

    private static ServingHostTerminalProof Terminal(ServingHostAcceptedSocket accepted) =>
        new(accepted, 1, new string('A', 64), ServingHostTerminalKind.WorkTerminal);

    private sealed class BrokerDiskFixture : IDisposable
    {
        private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("air-broker-disk-");

        internal BrokerDiskFixture()
        {
            var original = FixtureBinding(Path.Combine(_scratch.FullName, "reservation"));
            // Keep two application slots so one use can remain unresolved throughout the test.
            Binding = original with { TotalSlots = 2 };
            var directory = Directory.CreateDirectory(Binding.Scope.AuthorityDirectory);
            Records = Path.Combine(directory.FullName, ServingBrokerFileStore.RecordsName);
            var initial = new ServingBrokerRecord(ServingBrokerRecord.CurrentFormat, Binding.Scope, 0, null, Epoch,
                Binding, Open: false, Close: null, []);
            var payload = JsonSerializer.SerializeToUtf8Bytes(initial, ServingBrokerRecordJsonContext.Default.ServingBrokerRecord);
            var framed = new byte[sizeof(int) + payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length);
            payload.CopyTo(framed, sizeof(int));
            File.WriteAllBytes(Records, framed);
            File.WriteAllBytes(Path.Combine(directory.FullName, ServingBrokerFileStore.FenceName), []);
            Head = new(new(Binding.Scope, 0, Convert.ToHexString(SHA256.HashData(payload))));
            Store = Open();
        }

        internal Guid Epoch { get; } = Guid.NewGuid();
        internal ServingBrokerBinding Binding { get; }
        internal string Records { get; }
        internal Anchor Head { get; }
        internal ServingBrokerFileStore Store { get; }
        internal ServingBrokerFileStore Open() => ServingBrokerFileStore.OpenWriter(Binding.Scope, Head);
        internal ServingBrokerLedger Ledger() => new(Binding.Scope, Epoch, Open());
        public void Dispose() => _scratch.Delete(recursive: true);
    }

    private sealed class RefusedIssueStore(IServingBrokerStore inner, bool commit) : IServingBrokerStore
    {
        public ServingBrokerSnapshot? Read() => inner.Read();

        public bool TryAdvance(ServingBrokerSnapshot predecessor, ServingBrokerRecord next)
        {
            if (!next.Uses.Any(use => use.Phase == ServingBrokerUsePhase.Issued))
                return inner.TryAdvance(predecessor, next);
            if (commit) Assert.True(inner.TryAdvance(predecessor, next));
            return false;
        }
    }

    private sealed class UnusedSocketEvidence : IServingHostSocketEvidence
    {
        public bool IsGenerationCurrent => throw new InvalidOperationException();
        public bool CanConnect(CancellationToken ct) => throw new InvalidOperationException();
        public ServingHostAcceptedSocket? JoinAccepted(ServingHostSocketRequest request, CancellationToken ct) =>
            throw new InvalidOperationException();
        public bool IsLive(ServingHostAcceptedSocket accepted) => throw new InvalidOperationException();
        public ValueTask<ServingHostTerminalProof?> ConfirmTerminalAsync(ServingHostAcceptedSocket accepted, CancellationToken ct) =>
            throw new InvalidOperationException();
    }
}
