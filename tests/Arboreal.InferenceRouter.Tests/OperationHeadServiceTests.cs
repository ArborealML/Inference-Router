using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Arboreal.InferenceRouter.Lifecycle;
using Arboreal.InferenceRouter.OperationHead;
using HeadProgram = Arboreal.InferenceRouter.OperationHead.Program;

namespace Arboreal.InferenceRouter.Tests;

public sealed class OperationHeadServiceTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(8);

    [Fact(Timeout = 30_000)]
    public async Task ExactConcurrentAdvanceWinsOnceAndReopens()
    {
        if (!OperatingSystem.IsLinux() || !OperationHeadLinux.Supported) return;

        await using var test = new Scratch();
        await test.StartAsync();
        Assert.Null(test.Client.Read(test.Scope));
        var genesis = test.Head(0, 'A');
        Assert.True(test.Client.TryAdvance(null, genesis));
        var genesisBytes = File.ReadAllBytes(test.StatePath);
        Assert.False(test.Client.TryAdvance(genesis with { Digest = Digest('B') }, test.Head(1, 'C')));
        Assert.False(test.Client.TryAdvance(genesis, test.Head(2, 'C')));
        Assert.False(test.Client.TryAdvance(null, genesis));
        Assert.Throws<IOException>(() => test.Client.TryAdvance(genesis, test.Head(1, 'C') with
        {
            Scope = test.Scope with { Enrollment = new("pair", 2) },
        }));
        Assert.Throws<IOException>(() => test.Client.TryAdvance(genesis, test.Head(1, 'a')));
        Assert.Equal(genesisBytes, File.ReadAllBytes(test.StatePath));

        var contenders = new[] { test.Head(1, 'C'), test.Head(1, 'D') };
        using var timeout = new CancellationTokenSource(WaitLimit);
        var results = await Task.WhenAll(contenders.Select(next =>
            Task.Run(() => test.Client.TryAdvance(genesis, next), timeout.Token)));
        Assert.Single(results, static applied => applied);
        var winner = contenders[Array.IndexOf(results, true)];
        Assert.Equal(winner, test.Client.Read(test.Scope));

        await Assert.ThrowsAsync<IOException>(() => HeadProgram.RunAsync(test.Authority, test.Runtime,
            CancellationToken.None));
        await test.StopAsync();
        await test.StartAsync();
        Assert.Equal(winner, test.Client.Read(test.Scope));
        Assert.False(test.Client.TryAdvance(genesis, contenders[0]));

        var current = winner;
        for (var revision = 2; revision <= 5; revision++)
        {
            var next = test.Head(revision, 'E');
            Assert.True(test.Client.TryAdvance(current, next));
            current = next;
        }
        var lastBytes = File.ReadAllBytes(test.StatePath);
        Assert.Equal(current, test.Client.Read(test.Scope));
        Assert.Throws<IOException>(() => test.Client.TryAdvance(current, test.Head(6, 'F')));
        Assert.Equal(lastBytes, File.ReadAllBytes(test.StatePath));
    }

    [Fact(Timeout = 25_000)]
    public async Task JournalUsesTheDurableHeadAndRefusesRestoredOrUnanchoredBytes()
    {
        if (!OperatingSystem.IsLinux() || !OperationHeadLinux.Supported) return;

        await using var test = new Scratch();
        await test.StartAsync();
        var journal = new SupervisedLaunchJournal(test.Scope, test.Client);
        journal.Provision();
        var (dispatch, commission) = FixtureDispatch(test.Scope);
        SupervisedLaunchJournalHead advanced;
        byte[] older;
        using (journal.EnterFence())
        {
            var genesis = Assert.IsType<SupervisedLaunchSnapshot>(journal.Read());
            Assert.Equal(0, genesis.Head.Revision);
            older = File.ReadAllBytes(journal.JournalPath);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(older.AsSpan(sizeof(int)))), genesis.Head.Digest);

            journal.Advance(genesis, genesis.Frame.History with { Dispatch = dispatch }, commission);
            var installed = Assert.IsType<SupervisedLaunchSnapshot>(journal.Read());
            Assert.Equal(1, installed.Head.Revision);
            Assert.Equal(dispatch, installed.Frame.History.Dispatch);
            Assert.Equal(genesis.Head.Digest, installed.Frame.Predecessor);
            advanced = installed.Head;
            Assert.Equal(advanced, test.Client.Read(test.Scope));

            var unanchored = File.ReadAllBytes(journal.JournalPath).Append((byte)42).ToArray();
            File.WriteAllBytes(journal.JournalPath, unanchored);
            Assert.Null(journal.Read());
            Assert.Equal(unanchored, File.ReadAllBytes(journal.JournalPath));

            File.WriteAllBytes(journal.JournalPath, older);
        }

        var protectedHead = File.ReadAllBytes(test.StatePath);
        await test.StopAsync();
        await test.StartAsync();
        var reopened = new SupervisedLaunchJournal(test.Scope, test.Client);
        using var reopenFence = reopened.EnterFence();
        Assert.Equal(advanced, test.Client.Read(test.Scope));
        Assert.Throws<EndOfStreamException>(() => reopened.Read());
        Assert.Equal(older, File.ReadAllBytes(reopened.JournalPath));
        Assert.Equal(protectedHead, File.ReadAllBytes(test.StatePath));
        Assert.Equal(advanced, test.Client.Read(test.Scope));
    }

    private static (HostInitialDispatch Dispatch, SupervisedLaunchCommission Commission) FixtureDispatch(HostReservationScope scope)
    {
        var enrollment = Assert.IsType<HostEnrollmentScope>(scope.Enrollment);
        var head = new EnrolledPairHead("fixture-slot", enrollment.Group, "fixture-daemon", "fixture-router",
            enrollment.Generation, 0, Digest('C'));
        var first = Member("first");
        var second = Member("second");
        var topology = new EnrolledPairAuthority(head, [first.Grant, second.Grant], ["left", "right"]);
        var commission = new SupervisedLaunchCommission(scope, topology, [first.Approval, second.Approval]);
        var intent = first.Approval.Intent;
        var plan = intent.Plan;
        using var reservation = HostReservation.OpenEnrolled(scope);
        var epoch = reservation.Grant(plan.Engine);
        var operation = reservation.Begin(epoch, HostMutationKind.Launch, plan.Engine, plan.Model, plan.Tier, plan.Digest());
        reservation.MarkDispatching(operation);
        var installed = Assert.IsType<HostEnrolledSnapshot>(reservation.Enrolled);
        var policy = new HostInitialPolicy(scope, 1, 1, CompletePair: true, ExclusiveWriter: true,
            IngressClosed: true, ForeignResident: false);
        var dispatch = new HostInitialDispatch(policy, operation, intent, installed.Record.Revision, installed.Digest);
        Assert.True(dispatch.MatchesDispatching(installed));
        return (dispatch, commission);

        static (EnrolledPairMemberGrant Grant, SupervisedLaunchApproval Approval) Member(string engine)
        {
            var plan = new HostCheckpointPlan(engine, engine + "-model", "32k", "fixture-config", 5_000, engine, Digest('D'));
            var member = new EnrolledPairMember(engine, plan.Model, "32k", plan.Configuration, plan.BackendRoute);
            var grant = new EnrolledPairMemberGrant(member, "/fixture/docker.sock", new Uri($"http://{engine}.invalid/v1"),
                plan.Digest());
            var image = new DockerImageIdentity("sha256:" + Digest('E'), "fixture@sha256:" + Digest('F'), "linux/amd64");
            return (grant, new(HostInitialLaunchIntent.Freeze(plan, "{}"u8), image, "v1.44"));
        }
    }

    [Theory(Timeout = 25_000)]
    [InlineData("before-replace")]
    [InlineData("directory-sync")]
    [InlineData("lost-reply")]
    public async Task CommitCutsNeverProduceAFalseOrSuccessfulReply(string fault)
    {
        if (!OperatingSystem.IsLinux() || !OperationHeadLinux.Supported) return;

        await using var test = new Scratch();
        var cut = fault switch
        {
            "before-replace" => OperationHeadCut.BeforeReplace,
            "directory-sync" => OperationHeadCut.BeforeDirectorySync,
            _ => OperationHeadCut.BeforeReply,
        };
        await test.StartAsync(visit: observed =>
        {
            if (observed == cut) throw new IOException("synthetic interrupted commit");
        });
        var old = File.ReadAllBytes(test.StatePath);
        var next = test.Head(0, 'C');

        Assert.Throws<IOException>(() => test.Client.TryAdvance(null, next));

        if (cut == OperationHeadCut.BeforeReplace)
        {
            Assert.Equal(old, File.ReadAllBytes(test.StatePath));
            Assert.Null(test.Client.Read(test.Scope));
        }
        else if (cut == OperationHeadCut.BeforeDirectorySync)
        {
            Assert.Throws<IOException>(() => test.Client.Read(test.Scope));
            Assert.Throws<IOException>(() => test.Client.TryAdvance(next, test.Head(1, 'D')));
        }
        else
        {
            Assert.Equal(next, test.Client.Read(test.Scope));
        }

        await test.StopAsync();
        await test.StartAsync();
        Assert.Equal(cut == OperationHeadCut.BeforeReplace ? null : next, test.Client.Read(test.Scope));
        if (cut != OperationHeadCut.BeforeReplace)
            Assert.False(test.Client.TryAdvance(null, next));
    }

    [Theory(Timeout = 20_000)]
    [InlineData("root")]
    [InlineData("state")]
    [InlineData("lock")]
    [InlineData("socket")]
    [InlineData("owner-mode")]
    [InlineData("missing-state")]
    [InlineData("corrupt-state")]
    [InlineData("symlink-state")]
    public async Task ReplacedOrUnsafeAuthorityIsUnavailable(string fault)
    {
        if (!OperatingSystem.IsLinux() || !OperationHeadLinux.Supported) return;

        await using var test = new Scratch();
        await test.StartAsync();
        var renamedSocket = test.SocketPath + ".old";
        var seed = File.ReadAllBytes(test.StatePath);

        switch (fault)
        {
            case "root":
                Directory.Move(test.Authority, test.Authority + ".old");
                Directory.CreateDirectory(test.Authority, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                break;
            case "state":
                File.Move(test.StatePath, test.StatePath + ".old");
                Scratch.WritePrivate(test.StatePath, seed);
                break;
            case "lock":
                File.Move(test.LockPath, test.LockPath + ".old");
                Scratch.WritePrivate(test.LockPath, []);
                break;
            case "socket":
                File.Move(test.SocketPath, renamedSocket);
                Scratch.WritePrivate(test.SocketPath, "replacement"u8.ToArray());
                break;
            case "owner-mode":
                File.SetUnixFileMode(test.LockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);
                break;
            case "missing-state":
                File.Delete(test.StatePath);
                break;
            case "corrupt-state":
                File.WriteAllBytes(test.StatePath, "{}"u8.ToArray());
                break;
            case "symlink-state":
                File.Move(test.StatePath, test.StatePath + ".old");
                File.CreateSymbolicLink(test.StatePath, test.StatePath + ".old");
                break;
        }

        var client = fault == "socket" ? test.At(renamedSocket) : test.Client;
        Assert.Throws<IOException>(() => client.Read(test.Scope));
        Assert.Throws<IOException>(() => client.TryAdvance(null, test.Head(0, 'C')));
        await test.StopAsync();
        if (fault == "socket")
            Assert.Equal("replacement", File.ReadAllText(test.SocketPath));
    }

    [Theory(Timeout = 20_000)]
    [InlineData("missing")]
    [InlineData("scope")]
    [InlineData("duplicate")]
    [InlineData("trailing")]
    public async Task StartupOnlyAcceptsACompleteExactProvisionedSeed(string fault)
    {
        if (!OperatingSystem.IsLinux() || !OperationHeadLinux.Supported) return;

        await using var test = new Scratch();
        var json = File.ReadAllText(test.StatePath);
        switch (fault)
        {
            case "missing": File.Delete(test.StatePath); break;
            case "scope": File.WriteAllText(test.StatePath, json.Replace("\"Generation\":1", "\"Generation\":2",
                StringComparison.Ordinal)); break;
            case "duplicate": File.WriteAllText(test.StatePath, json.Insert(1, "\"Version\":1,")); break;
            case "trailing": File.WriteAllText(test.StatePath, json + "{}"); break;
        }

        var listened = false;
        using var timeout = new CancellationTokenSource(WaitLimit);
        var error = await Record.ExceptionAsync(() => HeadProgram.RunAsync(test.Authority, test.Runtime,
            timeout.Token, () => listened = true));
        Assert.True(error is IOException or JsonException);
        Assert.False(listened);
        Assert.False(File.Exists(test.SocketPath));
        Assert.Throws<IOException>(() => test.Client.Read(test.Scope));
    }

    [Theory(Timeout = 25_000)]
    [InlineData("uid")]
    [InlineData("leaf")]
    [InlineData("partial-tls")]
    [InlineData("partial-length")]
    [InlineData("partial-body")]
    [InlineData("oversized")]
    [InlineData("duplicate")]
    public async Task InvalidOrPartialExchangeCannotReachTheStore(string fault)
    {
        if (!OperatingSystem.IsLinux() || !OperationHeadLinux.Supported) return;

        await using var test = new Scratch(wrongUid: fault == "uid");
        var decisions = 0;
        await test.StartAsync(exchangeBudget: TimeSpan.FromSeconds(2), decision: () => Interlocked.Increment(ref decisions));
        var original = File.ReadAllBytes(test.StatePath);

        if (fault == "uid" || fault == "leaf")
        {
            var client = fault == "leaf" ? test.At(test.SocketPath, Digest('F')) : test.Client;
            Assert.Throws<IOException>(() => client.TryAdvance(null, test.Head(0, 'A')));
            if (fault == "uid") await AssertRejectedBeforeTlsAsync(test);
        }
        else
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(test.SocketPath), timeout.Token);
            using var wire = new NetworkStream(socket, ownsSocket: false);
            if (fault == "partial-tls")
            {
                await wire.WriteAsync(new byte[] { 0x16, 0x03 }, timeout.Token);
                await AssertClosedAsync(wire, timeout.Token);
            }
            else
            {
                using var tls = new SslStream(wire, leaveInnerStreamOpen: true, (_, remote, _, _) =>
                    remote?.GetCertHashString(HashAlgorithmName.SHA256) == test.Pin);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "operation-head" },
                    timeout.Token);
                var input = fault switch
                {
                    "partial-length" => new byte[] { 0, 0 },
                    "partial-body" => new byte[] { 0, 0, 0, 12, (byte)'{' },
                    "oversized" => new byte[] { 0, 0, 0x10, 0x01 },
                    _ => DuplicateRequest(test),
                };
                await tls.WriteAsync(input, timeout.Token);
                await AssertClosedAsync(tls, timeout.Token);
            }
        }

        Assert.Equal(0, Volatile.Read(ref decisions));
        Assert.Equal(original, File.ReadAllBytes(test.StatePath));
    }

    [Theory(Timeout = 20_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EchoWithRightNonceAndWrongCasArgumentsIsUnavailable(bool mutatePredecessor)
    {
        if (!OperatingSystem.IsLinux() || !OperationHeadLinux.Supported) return;

        await using var test = new Scratch();
        await AssertForgedReplyUnavailableAsync(test, advance: true, request =>
        {
            var changed = mutatePredecessor
                ? request with { Predecessor = Assert.IsType<SupervisedLaunchJournalHead>(request.Predecessor) with { Digest = Digest('C') } }
                : request with { Next = Assert.IsType<SupervisedLaunchJournalHead>(request.Next) with { Digest = Digest('C') } };
            return new(changed, OperationHeadProtocol.Refused, null);
        });
    }

    [Theory(Timeout = 20_000)]
    [InlineData("applied-nonce")]
    [InlineData("applied-predecessor")]
    [InlineData("applied-head")]
    [InlineData("current-scope")]
    [InlineData("current-head")]
    public async Task SuccessfulReplyWithMismatchedEchoOrHeadIsUnavailable(string fault)
    {
        if (!OperatingSystem.IsLinux() || !OperationHeadLinux.Supported) return;

        await using var test = new Scratch();
        var foreign = test.Scope with { Enrollment = new("pair", 2) };
        var current = test.Head(0, 'A');
        await AssertForgedReplyUnavailableAsync(test, fault.StartsWith("applied-", StringComparison.Ordinal), request =>
        {
            return fault switch
            {
                "applied-nonce" => new(request with { Nonce = request.Nonce == Digest('C') ? Digest('D') : Digest('C') },
                    OperationHeadProtocol.Applied, request.Next),
                "applied-predecessor" => new(request with { Predecessor = current with { Digest = Digest('C') } },
                    OperationHeadProtocol.Applied, request.Next),
                "applied-head" => new(request, OperationHeadProtocol.Applied,
                    Assert.IsType<SupervisedLaunchJournalHead>(request.Next) with { Digest = Digest('C') }),
                "current-scope" => new(request with { Scope = foreign }, OperationHeadProtocol.Current, current),
                _ => new(request, OperationHeadProtocol.Current, current with { Scope = foreign }),
            };
        });
    }

    private static async Task AssertForgedReplyUnavailableAsync(Scratch test, bool advance,
        Func<OperationHeadRequest, OperationHeadReply> forge)
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(test.SocketPath));
        listener.Listen(1);
        using var timeout = new CancellationTokenSource(WaitLimit);
        var old = test.Head(0, 'A');
        var next = test.Head(1, 'B');

        var answer = ReplyAsync();
        var error = await Task.Run(() => Record.Exception(() =>
        {
            if (advance) _ = test.Client.TryAdvance(old, next);
            else _ = test.Client.Read(test.Scope);
        }), timeout.Token);
        await answer.WaitAsync(timeout.Token);
        Assert.IsType<IOException>(error);

        async Task ReplyAsync()
        {
            using var socket = await listener.AcceptAsync(timeout.Token);
            using var wire = new NetworkStream(socket, ownsSocket: false);
            using var tls = new SslStream(wire, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = test.Certificate },
                timeout.Token);
            var request = await BoundedJsonFrame.ReadAsync(tls, OperationHeadJsonContext.Default.OperationHeadRequest,
                OperationHeadProtocol.MaxPayload, timeout.Token);
            Assert.True(request.IsValidFor(test.Scope));
            Assert.Equal(advance ? OperationHeadProtocol.CompareAdvance : OperationHeadProtocol.Read, request.Operation);
            Assert.Equal(advance ? old : null, request.Predecessor);
            Assert.Equal(advance ? next : null, request.Next);
            await BoundedJsonFrame.WriteAsync(tls, forge(request), OperationHeadJsonContext.Default.OperationHeadReply,
                OperationHeadProtocol.MaxPayload, timeout.Token);
        }
    }

    private static async Task AssertClosedAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            Assert.Equal(0, await stream.ReadAsync(new byte[1], ct));
        }
        catch (IOException)
        {
            // The TLS peer can also close without a close-notify after an invalid request.
        }
    }

    private static async Task AssertRejectedBeforeTlsAsync(Scratch test)
    {
        using var timeout = new CancellationTokenSource(WaitLimit);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(test.SocketPath), timeout.Token);
        using var wire = new NetworkStream(socket, ownsSocket: false);
        using var tls = new SslStream(wire, leaveInnerStreamOpen: true, (_, remote, _, _) =>
            remote?.GetCertHashString(HashAlgorithmName.SHA256) == test.Pin);
        var error = await Record.ExceptionAsync(() => tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = "operation-head" }, timeout.Token));
        Assert.True(error is IOException or System.Security.Authentication.AuthenticationException);
    }

    private static byte[] DuplicateRequest(Scratch test)
    {
        var request = new OperationHeadRequest(OperationHeadProtocol.Version, OperationHeadProtocol.CompareAdvance,
            test.Scope, Digest('C'), null, test.Head(0, 'A'));
        var json = JsonSerializer.Serialize(request, OperationHeadJsonContext.Default.OperationHeadRequest);
        var bytes = Encoding.UTF8.GetBytes(json.Insert(1, "\"Version\":1,"));
        var frame = new byte[sizeof(int) + bytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length);
        bytes.CopyTo(frame, sizeof(int));
        return frame;
    }

    private static string Digest(char fill) => new(fill, 64);

    private sealed class Scratch : IAsyncDisposable
    {
        private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        private readonly DirectoryInfo _directory;
        private CancellationTokenSource? _stop;
        private Task? _service;

        internal Scratch(bool wrongUid = false)
        {
            if (!OperatingSystem.IsLinux() || !OperationHeadLinux.Supported)
                throw new PlatformNotSupportedException("The operation-head fixture requires Linux x64.");

            _directory = Directory.CreateTempSubdirectory("air-oh-");
            Authority = Directory.CreateDirectory(Path.Combine(_directory.FullName, "head"), DirectoryMode).FullName;
            Runtime = Directory.CreateDirectory(Path.Combine(_directory.FullName, "run"), DirectoryMode).FullName;
            Scope = HostReservation.ProvisionEnrolled(Path.Combine(_directory.FullName, "slot"), new HostEnrollmentScope("pair", 1));
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=operation-head-scratch", key, HashAlgorithmName.SHA256);
            Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            Pin = Certificate.GetCertHashString(HashAlgorithmName.SHA256);
            WritePrivate(Path.Combine(Authority, HeadProgram.CertificateName), Encoding.ASCII.GetBytes(Certificate.ExportCertificatePem()));
            WritePrivate(Path.Combine(Authority, HeadProgram.KeyName), Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem()));
            var producer = OperationHeadLinux.EffectiveUid();
            WritePrivate(Path.Combine(Authority, HeadProgram.BootstrapName), JsonSerializer.SerializeToUtf8Bytes(
                new OperationHeadBootstrap(OperationHeadProtocol.Version, Scope, wrongUid ? producer ^ 1u : producer),
                OperationHeadBootstrapJson.Default.OperationHeadBootstrap));
            var seed = new OperationHeadSnapshot(OperationHeadProtocol.Version, Scope, OperationHeadProtocol.Absent, null, null);
            WritePrivate(StatePath, JsonSerializer.SerializeToUtf8Bytes(seed, OperationHeadJsonContext.Default.OperationHeadSnapshot));
            WritePrivate(LockPath, []);
        }

        internal string Authority { get; }
        internal string Runtime { get; }
        internal string StatePath => Path.Combine(Authority, OperationHeadStore.StateName);
        internal string LockPath => Path.Combine(Authority, OperationHeadStore.LockName);
        internal string SocketPath => Path.Combine(Runtime, HeadProgram.SocketName);
        internal HostReservationScope Scope { get; }
        internal string Pin { get; }
        internal X509Certificate2 Certificate { get; }
        internal OperationHeadClient Client => At(SocketPath);
        internal OperationHeadClient At(string path, string? pin = null) => new(new UnixDomainSocketEndPoint(path), pin ?? Pin, Scope);
        internal SupervisedLaunchJournalHead Head(int revision, char fill) => new(Scope, revision, Digest(fill));

        internal async Task StartAsync(Action<OperationHeadCut>? visit = null, TimeSpan? exchangeBudget = null,
            Action? decision = null)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stop = new CancellationTokenSource();
            var service = HeadProgram.RunAsync(Authority, Runtime, _stop.Token, ready.SetResult, visit, exchangeBudget, decision);
            _service = service;
            var complete = await Task.WhenAny(ready.Task, service).WaitAsync(WaitLimit);
            await complete;
            Assert.True(ready.Task.IsCompletedSuccessfully);
        }

        internal async Task StopAsync()
        {
            using var stop = _stop;
            _stop = null;
            if (_service is not { } service) return;
            _service = null;
            await stop!.CancelAsync();
            await service.WaitAsync(WaitLimit);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            finally
            {
                Certificate.Dispose();
                _directory.Delete(recursive: true);
            }
        }

        internal static void WritePrivate(string path, byte[] bytes)
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("The operation-head fixture requires Linux permissions.");

            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            file.Write(bytes);
        }
    }
}
