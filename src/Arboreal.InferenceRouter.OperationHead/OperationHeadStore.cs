using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.OperationHead;

internal sealed class OperationHeadRoot(string path, SafeFileHandle handle, OperationHeadLinux.Entry identity, bool runtime)
{
    internal SafeFileHandle Handle { get; } = handle;
    internal OperationHeadLinux.Entry Identity { get; } = identity;

    internal void Verify()
    {
        using var current = OperationHeadLinux.OpenDirectory(path);
        if (OperationHeadLinux.DirectoryEntry(current, Identity.Uid, runtime) != Identity ||
            OperationHeadLinux.DirectoryEntry(Handle, Identity.Uid, runtime) != Identity)
            throw OperationHeadLinux.InvalidObject();
    }

    internal void VerifyEntry(string name, OperationHeadLinux.Entry expected)
    {
        using var pinned = OperationHeadLinux.Pin(Handle, name);
        if (OperationHeadLinux.Inspect(pinned) != expected) throw OperationHeadLinux.InvalidObject();
    }

    internal (OperationHeadLinux.Entry Entry, byte[] Bytes) Read(SafeFileHandle descriptors, string name, int limit)
    {
        using var pinned = OperationHeadLinux.Pin(Handle, name);
        var entry = OperationHeadLinux.CheckRegular(pinned, Identity);
        using var data = OperationHeadLinux.ReopenRegular(descriptors, pinned, Identity, entry);
        using var stream = new FileStream(data, FileAccess.Read, bufferSize: 1, isAsync: false);
        var bytes = new byte[limit + 1];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count == 0 || count > limit || OperationHeadLinux.CheckRegular(data, Identity) != entry)
            throw OperationHeadLinux.InvalidObject();
        VerifyEntry(name, entry);
        return (entry, bytes[..count]);
    }
}

internal enum OperationHeadCut { BeforeReplace, BeforeDirectorySync, BeforeReply }

internal sealed class OperationHeadStore
{
    internal const string StateName = "head.json";
    internal const string LockName = "head.lock";
    internal const string BrokerStateName = "serving-broker-head.json";

    private readonly Lock _guard = new();
    private readonly HostReservationScope _scope;
    private readonly OperationHeadRoot _root;
    private readonly SafeFileHandle _descriptors;
    private readonly SafeFileHandle _lock;
    private readonly OperationHeadLinux.Entry _lockIdentity;
    private readonly int _serviceVersion;
    private readonly Action<OperationHeadCut>? _cutForTests;
    private State _current;
    private BrokerState? _brokerCurrent;
    private bool _quarantined;

    private sealed record State(OperationHeadLinux.Entry Identity, byte[] Bytes, OperationHeadSnapshot Snapshot);
    private sealed record BrokerState(OperationHeadLinux.Entry Identity, byte[] Bytes, BrokerAnchorSnapshot Snapshot);

    internal OperationHeadStore(HostReservationScope scope, OperationHeadRoot root, SafeFileHandle descriptors,
        SafeFileHandle stableLock, int serviceVersion, bool brokerEnabled, Action<OperationHeadCut>? cutForTests)
    {
        _scope = scope;
        _root = root;
        _descriptors = descriptors;
        _lock = stableLock;
        _lockIdentity = OperationHeadLinux.CheckRegular(stableLock, root.Identity);
        _serviceVersion = serviceVersion;
        _cutForTests = cutForTests;
        VerifyRootAndLock();
        _current = ReadState();
        if (brokerEnabled) _brokerCurrent = ReadBrokerState();
    }

    internal BrokerAnchorReply DecideBroker(BrokerAnchorRequest request, OperationHeadBudget budget, Action verifyEndpoint)
    {
        lock (_guard)
        {
            budget.Check();
            var current = VerifyCurrentBroker(verifyEndpoint).Snapshot.Head;
            if (request.Operation == OperationHeadProtocol.Read)
                return new(request, OperationHeadProtocol.Current, current);
            if (request.Predecessor != current || request.Next is not { } next)
                return new(request, OperationHeadProtocol.Refused, null);

            var snapshot = BrokerAnchorSnapshot.FromHead(next);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, BrokerAnchorJsonContext.Default.BrokerAnchorSnapshot);
            CommitSnapshot(BrokerStateName, bytes, budget, () => VerifyCurrentBroker(verifyEndpoint), verifyEndpoint, entry =>
            {
                var installed = ReadBrokerState();
                VerifyReplacement(entry, bytes, installed.Identity, installed.Bytes);
                _brokerCurrent = installed;
            });
            _cutForTests?.Invoke(OperationHeadCut.BeforeReply);
            return new(request, OperationHeadProtocol.Applied, next);
        }
    }

    internal OperationHeadReply Decide(OperationHeadRequest request, OperationHeadBudget budget, Action verifyEndpoint)
    {
        lock (_guard)
        {
            budget.Check();
            VerifyCurrent(verifyEndpoint);
            var current = _current.Snapshot.Head;
            if (request.Operation == OperationHeadProtocol.Read)
                return new(request, current is null ? OperationHeadProtocol.Absent : OperationHeadProtocol.Current, current);

            if (request.Predecessor != current || request.Next is not { } next || current?.Revision == int.MaxValue ||
                next.Revision != (current?.Revision ?? -1) + 1)
                return new(request, OperationHeadProtocol.Refused, null);

            Install(OperationHeadSnapshot.Current(next), budget, verifyEndpoint);
            _cutForTests?.Invoke(OperationHeadCut.BeforeReply);
            return new(request, OperationHeadProtocol.Applied, next);
        }
    }

    private void VerifyRootAndLock()
    {
        _root.Verify();
        _root.VerifyEntry(LockName, _lockIdentity);
        if (OperationHeadLinux.CheckRegular(_lock, _root.Identity) != _lockIdentity)
            throw OperationHeadLinux.InvalidObject();
    }

    private State ReadState()
    {
        var (identity, bytes) = _root.Read(_descriptors, StateName, OperationHeadProtocol.MaxPayload);
        var snapshot = JsonSerializer.Deserialize(bytes, OperationHeadJsonContext.Default.OperationHeadSnapshot);
        if (snapshot is null || !snapshot.IsValidFor(_scope, _serviceVersion)) throw OperationHeadLinux.InvalidObject();
        return new(identity, bytes, snapshot);
    }

    private BrokerState ReadBrokerState()
    {
        var (identity, bytes) = _root.Read(_descriptors, BrokerStateName, OperationHeadProtocol.MaxPayload);
        var snapshot = JsonSerializer.Deserialize(bytes, BrokerAnchorJsonContext.Default.BrokerAnchorSnapshot);
        if (snapshot is null || !snapshot.IsValidFor(_scope)) throw OperationHeadLinux.InvalidObject();
        return new(identity, bytes, snapshot);
    }

    private void VerifyCurrent(Action verifyEndpoint)
    {
        if (_quarantined) throw OperationHeadLinux.InvalidObject();

        try
        {
            VerifyRootAndLock();
            verifyEndpoint();
            var observed = ReadState();
            if (observed.Identity != _current.Identity || !observed.Bytes.AsSpan().SequenceEqual(_current.Bytes))
                throw OperationHeadLinux.InvalidObject();
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            _quarantined = true;
            throw;
        }
    }

    private BrokerState VerifyCurrentBroker(Action verifyEndpoint)
    {
        if (_quarantined || _brokerCurrent is not { } current) throw OperationHeadLinux.InvalidObject();

        try
        {
            VerifyRootAndLock();
            verifyEndpoint();
            var observed = ReadBrokerState();
            VerifyReplacement(current.Identity, current.Bytes, observed.Identity, observed.Bytes);
            return current;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error))
        {
            _quarantined = true;
            throw;
        }
    }

    private void Install(OperationHeadSnapshot snapshot, OperationHeadBudget budget, Action verifyEndpoint)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, OperationHeadJsonContext.Default.OperationHeadSnapshot);
        CommitSnapshot(StateName, bytes, budget, () => VerifyCurrent(verifyEndpoint), verifyEndpoint, entry =>
        {
            var installed = ReadState();
            VerifyReplacement(entry, bytes, installed.Identity, installed.Bytes);
            _current = installed;
        });
    }

    // Only the two fixed service-owned snapshots use this commit path.
    private void CommitSnapshot(string stateName, byte[] bytes, OperationHeadBudget budget, Action verifyCurrent,
        Action verifyEndpoint, Action<OperationHeadLinux.Entry> install)
    {
        if (bytes.Length > OperationHeadProtocol.MaxPayload) throw OperationHeadLinux.InvalidObject();

        var temporary = $"head-{Guid.NewGuid():N}.tmp";
        OperationHeadLinux.Entry? created = null;
        var replaceEntered = false;
        try
        {
            budget.Check();
            using (var data = OperationHeadLinux.CreateTemporary(_root.Handle, temporary))
            {
                created = OperationHeadLinux.CheckRegular(data, _root.Identity);
                using var stream = new FileStream(data, FileAccess.Write, bufferSize: 4096, isAsync: false);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            _cutForTests?.Invoke(OperationHeadCut.BeforeReplace);
            verifyCurrent();
            budget.Check();
            _root.VerifyEntry(temporary, created.Value);
            replaceEntered = true;
            OperationHeadLinux.Replace(_root.Handle, temporary, stateName);
            _cutForTests?.Invoke(OperationHeadCut.BeforeDirectorySync);
            OperationHeadLinux.Sync(_root.Handle);

            VerifyRootAndLock();
            verifyEndpoint();
            install(created.Value);
        }
        catch (Exception error) when (replaceEntered && OperationHeadTransport.IsUnavailable(error))
        {
            _quarantined = true;
            throw;
        }
        finally
        {
            if (created is { } owned) OperationHeadLinux.RemoveOwned(_root.Handle, temporary, owned);
        }
    }

    private static void VerifyReplacement(OperationHeadLinux.Entry expected, byte[] expectedBytes,
        OperationHeadLinux.Entry observed, byte[] observedBytes)
    {
        if (expected != observed || !observedBytes.AsSpan().SequenceEqual(expectedBytes))
            throw OperationHeadLinux.InvalidObject();
    }
}
