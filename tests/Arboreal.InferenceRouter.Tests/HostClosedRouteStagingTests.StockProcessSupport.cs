using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.Tests;

public sealed partial class HostClosedRouteStagingTests
{
    private async Task<string> PrepareReceiptAsync(string name, CancellationToken ct)
    {
        var path = StockPath(name);
        await File.WriteAllBytesAsync(path, [], ct);
        StockSetMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await SetStockOwnerAsync(path, BackendUid, ct);
        return path;
    }

    private async Task<string> PrepareRoleAsync(string role, uint uid, StockPeerKey? key, CancellationToken ct,
        bool operationHead = false)
    {
        var path = Directory.CreateDirectory(StockPath(role)).FullName;
        StockSetMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await SetStockOwnerAsync(path, uid, ct);
        if (key is not null)
        {
            await WritePrivateAsync(path, operationHead ? "authority.pem" : "certificate.pem",
                Encoding.UTF8.GetBytes(key.Certificate.ExportCertificatePem()), uid, ct);
            await WritePrivateAsync(path, operationHead ? "authority.key" : "key.pem",
                Encoding.UTF8.GetBytes(key.PrivateKey), uid, ct);
        }
        return path;
    }

    private static async Task WritePrivateAsync(string root, string name, byte[] data, uint uid, CancellationToken ct)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, data, ct);
        StockSetMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await SetStockOwnerAsync(path, uid, ct);
    }

    private static async Task SetStockOwnerAsync(string path, uint uid, CancellationToken ct)
    {
        await using var command = StockChild.Start("chown", FormattableString.Invariant($"{uid}:{SharedGroup}"), "--", path);
        Assert.Equal(0, await command.WaitForExitAsync(ct));
    }

    private static void StockSetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Commissioned process fixtures require Linux.");
        File.SetUnixFileMode(path, mode);
    }

    private static async Task WaitAsync(Func<bool> ready, string step, CancellationToken ct, params StockChild[] processes)
    {
        while (!ready())
        {
            foreach (var process in processes) await process.RequireRunningAsync(step);
            await Task.Delay(30, ct);
        }
    }

    private static async Task WaitForRouterAsync(HttpClient http, StockChild router, CancellationToken ct)
    {
        while (true)
        {
            await router.RequireRunningAsync("router readiness");
            try
            {
                using var reply = await http.GetAsync("healthz", ct);
                if (reply.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(30, ct);
        }
    }

    private static byte[] StockBody(CheckedEnrolledTarget target, string marker) => Encoding.UTF8.GetBytes(
        $$"""{"model":"{{target.Key.Model}}","messages":[{"role":"user","content":"{{marker}}"}]}""");

    private static async Task<HttpResponseMessage> PostStockAsync(HttpClient client, CheckedEnrolledTarget target,
        byte[] body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Inference-Profile", target.Key.Profile);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<string> ReadQueuedTicketAsync(HttpClient http, CheckedEnrolledTarget target, CancellationToken ct)
    {
        using var replay = await http.GetAsync("/admin/events?since=0", HttpCompletionOption.ResponseHeadersRead, ct);
        replay.EnsureSuccessStatusCode();
        using var events = new StreamReader(await replay.Content.ReadAsStreamAsync(ct));
        while (await events.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            using var json = JsonDocument.Parse(line[6..]);
            var row = json.RootElement;
            if (row.GetProperty("Kind").ValueEquals("request.enqueued") && row.GetProperty("Model").ValueEquals(target.Key.Model))
                return Assert.IsType<string>(row.GetProperty("TicketId").GetString());
        }
        throw new InvalidOperationException("The original successor did not reach the queue.");
    }

    private static async Task AssertOneCompletionAsync(HttpClient http, string ticket, CancellationToken ct)
    {
        using var replay = await http.GetAsync("/admin/events?ticket=" + ticket, HttpCompletionOption.ResponseHeadersRead, ct);
        replay.EnsureSuccessStatusCode();
        using var events = new StreamReader(await replay.Content.ReadAsStreamAsync(ct));
        var queued = 0;
        var selected = 0;
        while (await events.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            using var json = JsonDocument.Parse(line[6..]);
            if (json.RootElement.GetProperty("Kind").GetString() is "request.enqueued") queued++;
            if (json.RootElement.GetProperty("Kind").GetString() is "request.selected") selected++;
            if (json.RootElement.GetProperty("Kind").GetString() is "request.completed")
            {
                Assert.Equal(1, queued);
                Assert.Equal(1, selected);
                return;
            }
        }
        throw new InvalidOperationException("The original ticket did not deliver a terminal event.");
    }

    private static void ValidateReceipt(string receiptPath, byte[] body, StockBackend backend, ServingBrokerUse? expected = null)
    {
        var line = Assert.Single(File.ReadAllLines(receiptPath));
        using var parsed = JsonDocument.Parse(line);
        var receipt = parsed.RootElement;
        Assert.Equal(backend.Identity.Handler.ProcessId, receipt.GetProperty("pid").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)), receipt.GetProperty("sha256").GetString());
        var inode = receipt.GetProperty("inode").GetUInt64();
        var cookie = receipt.GetProperty("cookie").GetUInt64();
        var use = new Guid(Convert.FromHexString(receipt.GetProperty("use").GetString()!));
        Assert.NotEqual(0UL, inode);
        Assert.NotEqual(0UL, cookie);
        if (expected is { Accepted: { } accepted })
        {
            Assert.Equal(expected.Id, use);
            Assert.Equal(accepted.AcceptedInode, inode);
            Assert.Equal(accepted.AcceptedCookie, cookie);
        }
        Console.WriteLine(FormattableString.Invariant($"application pid={backend.Identity.Handler.ProcessId} inode={inode} cookie={cookie} use={use} sha256={receipt.GetProperty("sha256").GetString()}"));
    }

    private sealed class StockPeerKey : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        internal StockPeerKey(string name)
        {
            var request = new CertificateRequest("CN=air-stock-fixture-" + name, _key, HashAlgorithmName.SHA256);
            Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        }

        internal X509Certificate2 Certificate { get; }
        internal string Pin => Certificate.GetCertHashString(HashAlgorithmName.SHA256);
        internal string PrivateKey => _key.ExportPkcs8PrivateKeyPem();

        public void Dispose()
        {
            Certificate.Dispose();
            _key.Dispose();
        }
    }

    private sealed class StockSocketPeer : IAsyncDisposable
    {
        private readonly StockPeerKey _key = new("controlled-source");
        private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _worker;

        internal StockSocketPeer(string path, Func<Socket, X509Certificate2, CancellationToken, Task> serve)
        {
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            StockSetMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
            _listener.Listen(16);
            SuccessorPeer = new(path, _key.Pin);
            CatalogPeer = new(path, _key.Pin);
            _worker = WorkAsync(serve, _lifetime.Token);
        }

        internal SuccessorAuthorityPeer SuccessorPeer { get; }
        internal CatalogAuthorityPeer CatalogPeer { get; }

        private async Task WorkAsync(Func<Socket, X509Certificate2, CancellationToken, Task> serve, CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    using var accepted = await _listener.AcceptAsync(ct);
                    await serve(accepted, _key.Certificate, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _lifetime.CancelAsync();
                await _worker;
            }
            finally
            {
                _listener.Dispose();
                _key.Dispose();
                _lifetime.Dispose();
            }
        }
    }

    private sealed class StockChild : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _errors;
        private bool _stopped;

        private StockChild(Process process)
        {
            _process = process;
            _errors = _process.StandardError.ReadToEndAsync();
        }

        internal static string SocketScript => Path.Combine(
            Environment.GetEnvironmentVariable("AIR_STOCK_PROCESS_BINARIES") ?? AppContext.BaseDirectory, "sockets.py");

        internal static StockChild StartRole(uint uid, string executable, params string[] args) =>
            Start("setpriv", "--reuid=" + uid.ToString(CultureInfo.InvariantCulture),
                "--regid=" + SharedGroup.ToString(CultureInfo.InvariantCulture), "--clear-groups", "--", executable, args);

        internal static StockChild Activate(uint uid, string[] sockets, string executable, params string[] args) =>
            Start("python3", ["-I", SocketScript, "activate", uid.ToString(CultureInfo.InvariantCulture),
                SharedGroup.ToString(CultureInfo.InvariantCulture), .. sockets, "--", executable, .. args]);

        private static StockChild Start(string executable, string uid, string gid, string groups, string delimiter,
            string target, string[] args) => Start(executable, [uid, gid, groups, delimiter, target, .. args]);

        internal static StockChild Start(string executable, params string[] args)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            return new StockChild(Process.Start(start) ?? throw new InvalidOperationException("The fixture child did not start."));
        }

        internal async Task RequireRunningAsync(string phase)
        {
            if (!_process.HasExited) return;
            throw new InvalidOperationException(FormattableString.Invariant($"{phase}: child exited {_process.ExitCode}. {await _errors}"));
        }

        internal async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            await _process.WaitForExitAsync(ct);
            return _process.ExitCode;
        }

        internal async Task<string> ReadReadyAsync(CancellationToken ct) =>
            await _process.StandardOutput.ReadLineAsync(ct) ??
                throw new InvalidOperationException("The child stopped before its readiness witness. " + await _errors);

        internal async Task StopAsync()
        {
            if (_stopped) return;
            _stopped = true;
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (_process.HasExited) { }
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _process.WaitForExitAsync(cleanup.Token);
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); }
            finally { _process.Dispose(); }
        }
    }

    internal sealed record StockBackendIdentity(ServingHostLifetime Handler, ServingHostLifetime Worker,
        ServingHostNamespace Namespace, ServingHostListenerDescriptor Listener);

    private sealed class StockBackend(StockChild child, StockBackendIdentity identity, ServingBackendProfile profile) : IAsyncDisposable
    {
        internal StockBackendIdentity Identity { get; } = identity;
        internal ServingBackendProfile Profile { get; } = profile;

        internal static async Task<StockBackend> StartAsync(CheckedEnrolledTarget target, int slots, string terminal,
            string release, string receipts, CancellationToken ct)
        {
            var window = target.Key.Engine == DeepSeekEngineName ? 32768 : 262144;
            var process = StockChild.StartRole(BackendUid, "python3", "-I", StockChild.SocketScript, "backend",
                target.Endpoint.Port.ToString(CultureInfo.InvariantCulture), target.Key.Model, window.ToString(CultureInfo.InvariantCulture),
                slots.ToString(CultureInfo.InvariantCulture), terminal, release, receipts);
            try
            {
                var witness = Assert.IsType<StockBackendIdentity>(
                    JsonSerializer.Deserialize(await process.ReadReadyAsync(ct), StockBackendJson.Default.StockBackendIdentity));
                return new StockBackend(process, witness, new(target.Key.Model, target.Key.Profile, window, slots));
            }
            catch
            {
                await process.DisposeAsync();
                throw;
            }
        }

        internal void RequireKernelWitness(uint expectedUid)
        {
            var uid = File.ReadLines(FormattableString.Invariant($"/proc/{Identity.Handler.ProcessId}/status"))
                .Single(line => line.StartsWith("Uid:", StringComparison.Ordinal));
            Assert.Equal(expectedUid, uint.Parse(uid.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[2], CultureInfo.InvariantCulture));
            using var pin = CommissionedServingPins.TryPin(Identity.Handler);
            Assert.NotNull(pin);
            Assert.True(LinuxServingAcceptedSocket.MatchesListener(Identity.Listener, pin),
                "The fixture needs cross-UID pidfd_getfd, PID identity, and matching listener inode/cookie.");
            Console.WriteLine(FormattableString.Invariant($"handler uid={expectedUid} pid={Identity.Handler.ProcessId} start={Identity.Handler.StartTicks} worker={Identity.Worker.ProcessId} socket={Identity.Listener.Inode}/{Identity.Listener.Cookie}"));
        }

        internal async Task RequireConnectorAsync(ServingBrokerBinding binding, CancellationToken ct)
        {
            var serving = Serving(binding);
            using var pins = CommissionedServingPins.TryOpen(serving.Exclusion);
            Assert.NotNull(pins);
            var evidence = new LinuxServingHostSocketEvidence(pins.Handler, pins.Worker, serving.Exclusion,
                () => serving.Exclusion, static (_, _) => ValueTask.FromResult<ServingHostTerminalProof?>(null));
            var endpoint = new ServingBackendEndpoint(Identity.Listener.Bound, pins.NamespaceHandle, Identity.Namespace,
                Identity.Handler, Identity.Worker, Profile, evidence);
            var use = new ServingBrokerUse(Guid.NewGuid(), binding.Route, ServingBrokerUseKind.Models, Guid.NewGuid(), null,
                ServingBrokerUsePhase.Issued);
            await using var connection = await ServingBackendConnection.OpenAsync(endpoint,
                ServingBrokerIssuer.ServingIdentity(binding), use, deadline: null, ct);
            Assert.NotEqual(0UL, connection.Accepted.AcceptedInode);
            Assert.NotEqual(0UL, connection.Accepted.AcceptedCookie);
        }

        internal ServingBrokerBinding Binding(EnrolledSuccessorScope scope, HostEffectCheckpointCommitment checkpoint,
            CheckedEnrolledTarget target, long routeGeneration, int slots, string? journal)
        {
            var resident = Assert.IsType<HostCheckpointLaunchEffect>(checkpoint.State.AppliedLaunch);
            var incarnation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
                $"{Identity.Handler.ProcessId}/{Identity.Handler.StartTicks}/{Identity.Worker.ProcessId}/{Identity.Worker.StartTicks}/{Identity.Listener.Cookie}"))));
            return new(scope.Reservation, scope.Commission.Head, checkpoint,
                new(scope.Commission.Head.Partition, scope.Commission.Head.Generation, target.Key.Engine, target.PlanDigest,
                    resident.InstanceId, incarnation, resident.Operation.Epoch, routeGeneration), new string('A', 64), slots, journal);
        }

        internal HeldServingGeneration Serving(ServingBrokerBinding binding) =>
            new(new(ServingBrokerIssuer.ServingIdentity(binding), Identity.Namespace, Identity.Handler, Identity.Worker,
                Identity.Listener.Bound, Identity.Listener, binding.Applied.State.Revision, binding.Route.ServingIncarnation), Profile);

        internal Task StopAsync() => child.StopAsync();

        public ValueTask DisposeAsync() => child.DisposeAsync();
    }
}

[JsonSerializable(typeof(HostClosedRouteStagingTests.StockBackendIdentity))]
internal sealed partial class StockBackendJson : JsonSerializerContext;
