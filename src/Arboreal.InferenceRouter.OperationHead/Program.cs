using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arboreal.InferenceRouter.Lifecycle;

namespace Arboreal.InferenceRouter.OperationHead;

internal sealed record OperationHeadBootstrap(int Version, HostReservationScope Scope, uint ProducerUid);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(OperationHeadBootstrap))]
internal sealed partial class OperationHeadBootstrapJson : JsonSerializerContext;

internal static class Program
{
    internal const string SocketName = "operation-head.sock";
    internal const string BootstrapName = "bootstrap.json";
    internal const string CertificateName = "authority.pem";
    internal const string KeyName = "authority.key";
    private const int MaxPem = 32 * 1024;
    private const int MaxSocketPathBytes = 107;

    public static async Task<int> Main(string[] args)
    {
        if (!OperationHeadLinux.Supported || args.Length != 2)
        {
            Console.Error.WriteLine("Usage on Linux x64: air-operation-head <authority-directory> <socket-directory>");
            return 2;
        }

        using var stop = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });

        try
        {
            await RunAsync(args[0], args[1], stop.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (OperationHeadTransport.IsUnavailable(error) ||
            error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.Error.WriteLine("Operation-head authority could not serve.");
            return 1;
        }
    }

    internal static async Task RunAsync(string authorityPath, string runtimePath, CancellationToken ct,
        Action? listeningForTests = null, Action<OperationHeadCut>? cutForTests = null, TimeSpan? timeoutForTests = null,
        Action? decisionForTests = null)
    {
        if (!OperationHeadLinux.Supported || !IsDirectoryPath(authorityPath) || !IsDirectoryPath(runtimePath) ||
            Encoding.UTF8.GetByteCount(Path.Combine(runtimePath, SocketName)) > MaxSocketPathBytes)
            throw OperationHeadLinux.InvalidObject();

        var uid = OperationHeadLinux.EffectiveUid();
        using var authorityDirectory = OperationHeadLinux.OpenDirectory(authorityPath);
        var authority = new OperationHeadRoot(authorityPath, authorityDirectory,
            OperationHeadLinux.DirectoryEntry(authorityDirectory, uid, runtime: false), runtime: false);
        using var socketDirectory = OperationHeadLinux.OpenDirectory(runtimePath);
        var runtime = new OperationHeadRoot(runtimePath, socketDirectory,
            OperationHeadLinux.DirectoryEntry(socketDirectory, uid, runtime: true), runtime: true);
        if (authority.Identity == runtime.Identity) throw OperationHeadLinux.InvalidObject();

        using var descriptors = OperationHeadLinux.OpenDescriptors();
        using var lockPath = OperationHeadLinux.Pin(authorityDirectory, OperationHeadStore.LockName);
        using var stableLock = OperationHeadLinux.ReopenRegular(descriptors, lockPath, authority.Identity,
            OperationHeadLinux.CheckRegular(lockPath, authority.Identity), write: true);
        OperationHeadLinux.Lock(stableLock);
        var bootFile = authority.Read(descriptors, BootstrapName, OperationHeadProtocol.MaxPayload);
        var bootstrap = JsonSerializer.Deserialize(bootFile.Bytes, OperationHeadBootstrapJson.Default.OperationHeadBootstrap);
        if (bootstrap is null || bootstrap.Version != OperationHeadProtocol.Version ||
            !OperationHeadProtocol.IsScopeValid(bootstrap.Scope) ||
            (bootstrap.ProducerUid == uid && listeningForTests is null) || !EnvelopeFits(bootstrap.Scope))
            throw OperationHeadLinux.InvalidObject();

        var certFile = authority.Read(descriptors, CertificateName, MaxPem);
        var keyFile = authority.Read(descriptors, KeyName, MaxPem);
        using var certificate = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(certFile.Bytes),
            Encoding.UTF8.GetString(keyFile.Bytes));
        var store = new OperationHeadStore(bootstrap.Scope, authority, descriptors, stableLock, cutForTests);
        OperationHeadLinux.Entry? socketIdentity = null;
        try
        {
            runtime.Verify();
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            // Resolve the bind through the held runtime directory, even if a pathname is renamed.
            var directoryFd = socketDirectory.DangerousGetHandle().ToInt32().ToString(CultureInfo.InvariantCulture);
            listener.Bind(new AuthorityEndPoint($"/proc/self/fd/{directoryFd}/{SocketName}"));
            var bound = OperationHeadLinux.SecureSocket(socketDirectory, SocketName, runtime.Identity);
            socketIdentity = bound;
            VerifyEndpoint();
            listener.Listen(4);
            listeningForTests?.Invoke();
            while (!ct.IsCancellationRequested)
            {
                using var accepted = await listener.AcceptAsync(ct).ConfigureAwait(false);
                await OperationHeadTransport.ServeAsync(accepted, bootstrap.ProducerUid, certificate, bootstrap.Scope,
                    timeoutForTests ?? OperationHeadProtocol.Timeout,
                    (request, budget) =>
                    {
                        decisionForTests?.Invoke();
                        return store.Decide(request, budget, VerifyEndpoint);
                    }, ct).ConfigureAwait(false);
            }

            void VerifyEndpoint()
            {
                runtime.Verify();
                runtime.VerifyEntry(SocketName, bound);
                var currentBoot = authority.Read(descriptors, BootstrapName, OperationHeadProtocol.MaxPayload);
                if (currentBoot.Entry != bootFile.Entry || !currentBoot.Bytes.AsSpan().SequenceEqual(bootFile.Bytes))
                    throw OperationHeadLinux.InvalidObject();
                authority.VerifyEntry(CertificateName, certFile.Entry);
                authority.VerifyEntry(KeyName, keyFile.Entry);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller owns the service lifetime. Any entered commit has already settled.
        }
        finally
        {
            if (socketIdentity is { } owned)
                OperationHeadLinux.RemoveOwned(socketDirectory, SocketName, owned);
        }
    }

    private static bool IsDirectoryPath(string path) =>
        Path.IsPathFullyQualified(path) && string.Equals(path, Path.GetFullPath(path), StringComparison.Ordinal) &&
        !Path.EndsInDirectorySeparator(path);

    private static bool EnvelopeFits(HostReservationScope scope)
    {
        var digest = new string('F', OperationHeadProtocol.NonceBytes * 2);
        var head = new SupervisedLaunchJournalHead(scope, SupervisedLaunchJournal.LastRevision, digest);
        var request = new OperationHeadRequest(OperationHeadProtocol.Version, OperationHeadProtocol.CompareAdvance,
            scope, digest, head with { Revision = head.Revision - 1 }, head);
        var reply = new OperationHeadReply(request, OperationHeadProtocol.Applied, head);
        return JsonSerializer.SerializeToUtf8Bytes(request, OperationHeadJsonContext.Default.OperationHeadRequest).Length <=
            OperationHeadProtocol.MaxPayload &&
            JsonSerializer.SerializeToUtf8Bytes(reply, OperationHeadJsonContext.Default.OperationHeadReply).Length <=
            OperationHeadProtocol.MaxPayload;
    }

    // Bind does not register a path for Socket.Dispose to unlink. Only the service has
    // the filesystem identity required to remove the runtime name during shutdown.
    private sealed class AuthorityEndPoint(string path) : EndPoint
    {
        private readonly UnixDomainSocketEndPoint _inner = new(path);

        public override AddressFamily AddressFamily => AddressFamily.Unix;
        public override SocketAddress Serialize() => _inner.Serialize();
        public override EndPoint Create(SocketAddress socketAddress) => _inner.Create(socketAddress);
    }
}
