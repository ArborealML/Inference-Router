using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.Lifecycle;

// Called synchronously on the connector thread after it enters the held netns.
internal static class LinuxServingAcceptedSocket
{
    private const ushort NetlinkFamily = 16;
    private const ushort InetFamily = 2;
    private const ushort Inet6Family = 10;
    private const ushort SockDiagByFamily = 20;
    private const byte Established = 1;
    private const int NetlinkHeaderSize = 16;
    private const int InetDiagRequestSize = 56;
    private const int InetDiagMessageSize = 72;
    private const int TupleSize = 36;
    private const int RequestSequence = 1;
    private const int SocketCookieOption = 57;
    private const int PidFdGetFd = 438;

    internal static (ulong Inode, ulong Cookie)? Find(IPAddress local, int localPort, IPAddress remote, int remotePort,
        string handlerFdDirectory, SafeFileHandle handlerPidFd)
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return null;

        var address = local.GetAddressBytes();
        var peer = remote.GetAddressBytes();
        if (address.Length != peer.Length) return null;
        var family = address.Length == 4 ? InetFamily : Inet6Family;
        var request = new byte[NetlinkHeaderSize + InetDiagRequestSize];
        var tuple = request.AsSpan(24, TupleSize);
        BinaryPrimitives.WriteUInt16BigEndian(tuple, (ushort)localPort);
        BinaryPrimitives.WriteUInt16BigEndian(tuple[2..], (ushort)remotePort);
        address.CopyTo(tuple[4..]);
        peer.CopyTo(tuple[20..]);
        BinaryPrimitives.WriteInt32LittleEndian(request, request.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), SockDiagByFamily);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), 1); // NLM_F_REQUEST, one exact tuple.
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(8), RequestSequence);
        request[16] = (byte)family;
        request[17] = 6; // IPPROTO_TCP
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(20), 1 << Established);
        request.AsSpan(64).Fill(byte.MaxValue); // INET_DIAG_NOCOOKIE

        using var diag = OpenDiagnostics();
        if (diag.IsInvalid) return null;
        var kernel = new NetlinkAddress { Family = NetlinkFamily };
        if (Connect(diag, ref kernel, 12) != 0 || Send(diag, request, (nuint)request.Length, 0) != request.Length)
            return null;

        var response = new byte[4096];
        uint senderSize = 12;
        var size = Receive(diag, response, (nuint)response.Length, 0x20, out var sender, ref senderSize);
        if (size < NetlinkHeaderSize + InetDiagMessageSize || size > response.Length ||
            senderSize != 12 || sender.Family != NetlinkFamily || sender.Port != 0 || sender.Groups != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(response) != size ||
            BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(4)) != SockDiagByFamily ||
            BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(8)) != RequestSequence ||
            response[16] != family || response[17] != Established ||
            !tuple.SequenceEqual(response.AsSpan(20, TupleSize)))
            return null;

        var cookie = BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(60));
        var inode = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(84));
        if (cookie is 0 or ulong.MaxValue || inode == 0) return null;

        var link = FormattableString.Invariant($"socket:[{inode}]");
        var matching = false;
        foreach (var path in Directory.EnumerateFileSystemEntries(handlerFdDirectory))
        {
            if (new FileInfo(path).LinkTarget != link ||
                !int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out var fd))
                continue;
            if (matching || !VerifyDuplicate(handlerPidFd, fd, family, address, localPort, peer, remotePort, inode, cookie))
                return null;
            matching = true;
        }

        return matching ? (inode, cookie) : null;
    }

    private static bool VerifyDuplicate(SafeFileHandle handler, int fd, ushort family, byte[] local, int localPort,
        byte[] remote, int remotePort, uint inode, ulong cookie)
    {
        using var duplicate = new SafeFileHandle((nint)Duplicate(PidFdGetFd, handler, fd, 0), ownsHandle: true);
        if (duplicate.IsInvalid) return false;

        return ReadIdentity(duplicate) is { } actual && actual.Inode == inode && actual.Cookie == cookie &&
            MatchesEndpoint(duplicate, family, local, localPort, peer: false) &&
            MatchesEndpoint(duplicate, family, remote, remotePort, peer: true);
    }

    internal static (ulong Inode, ulong Cookie)? ReadIdentity(SafeFileHandle socket)
    {
        const uint statxInode = 0x100;
        uint size = sizeof(ulong);
        if (Stat(socket, default, 0x1000, statxInode, out var stat) != 0 ||
            (stat.Mask & statxInode) == 0 || stat.Inode == 0 ||
            GetSocketOption(socket, 1, SocketCookieOption, out var cookie, ref size) != 0 ||
            size != sizeof(ulong) || cookie is 0 or ulong.MaxValue) return null;

        return (stat.Inode, cookie);
    }

    internal static bool MatchesListener(ServingHostListenerDescriptor expected, SafeFileHandle handler)
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !IPAddress.TryParse(expected.Bound.Address, out var local)) return false;

        using var duplicate = new SafeFileHandle((nint)Duplicate(PidFdGetFd, handler, expected.Number, 0), ownsHandle: true);
        if (duplicate.IsInvalid || ReadIdentity(duplicate) is not { } actual ||
            actual.Inode != expected.Inode || actual.Cookie != expected.Cookie) return false;

        const int tcp = 6;
        const int socket = 1;
        return MatchesEndpoint(duplicate, local.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? InetFamily : Inet6Family, local.GetAddressBytes(), expected.Bound.Port, peer: false) &&
            HasOption(duplicate, socket, 30, expected: 1) && // SO_ACCEPTCONN
            HasOption(duplicate, socket, 15, expected: 0) && // SO_REUSEPORT
            HasOption(duplicate, tcp, 23, expected: 0) && // TCP_FASTOPEN
            HasOption(duplicate, tcp, 9, expected: 0); // TCP_DEFER_ACCEPT
    }

    private static bool HasOption(SafeFileHandle socket, int level, int option, int expected)
    {
        uint length = sizeof(int);
        return GetIntegerSocketOption(socket, level, option, out var current, ref length) == 0 &&
            length == sizeof(int) && current == expected;
    }

    private static bool MatchesEndpoint(SafeFileHandle socket, ushort family, byte[] address, int port, bool peer)
    {
        var endpoint = new byte[28];
        uint size = (uint)endpoint.Length;
        var result = peer ? GetPeerName(socket, endpoint, ref size) : GetSocketName(socket, endpoint, ref size);
        var ipv4 = family == InetFamily;
        return result == 0 && size == (ipv4 ? 16 : 28) && BinaryPrimitives.ReadUInt16LittleEndian(endpoint) == family &&
            BinaryPrimitives.ReadUInt16BigEndian(endpoint.AsSpan(2)) == port &&
            address.AsSpan().SequenceEqual(endpoint.AsSpan(ipv4 ? 4 : 8, address.Length));
    }

    private static SafeFileHandle OpenDiagnostics() =>
        new((nint)Socket(NetlinkFamily, 3 | 0x800 | 0x80000, 4), ownsHandle: true);

    internal static bool MatchesNamespace(SafeFileHandle descriptor, ServingHostNamespace expected)
    {
        const uint statxInode = 0x100;
        if (Stat(descriptor, default, 0x1000, statxInode, out var stat) != 0 ||
            (stat.Mask & statxInode) == 0 || stat.Inode != expected.Inode) return false;
        // Linux dev_t encoding used by the commissioning input.
        var major = (ulong)stat.DeviceMajor;
        var minor = (ulong)stat.DeviceMinor;
        var device = ((major & 0xfff) << 8) | ((major & ~0xfffUL) << 32) |
            (minor & 0xff) | ((minor & ~0xffUL) << 12);
        return device == expected.Device;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct NetlinkAddress
    {
        [FieldOffset(0)] internal ushort Family;
        [FieldOffset(4)] internal uint Port;
        [FieldOffset(8)] internal uint Groups;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }

    // The Linux x64 ABI uses fixed-size blittable fields. No unsafe build or runtime
    // reflection is needed. All FD arguments are held for each native call.
#pragma warning disable SYSLIB1054
    [DllImport("libc.so.6", EntryPoint = "socket", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Socket(int family, int type, int protocol);

    [DllImport("libc.so.6", EntryPoint = "connect", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Connect(SafeFileHandle fd, ref NetlinkAddress address, uint size);

    [DllImport("libc.so.6", EntryPoint = "send", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint Send(SafeFileHandle fd, byte[] request, nuint length, int flags);

    [DllImport("libc.so.6", EntryPoint = "recvfrom", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint Receive(SafeFileHandle fd, [Out] byte[] reply, nuint length, int flags,
        out NetlinkAddress sender, ref uint senderSize);

    [DllImport("libc.so.6", EntryPoint = "syscall", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint Duplicate(nint number, SafeFileHandle pidFd, int targetFd, uint flags);

    // AT_EMPTY_PATH takes a pointer to a single NUL byte.
    [DllImport("libc.so.6", EntryPoint = "statx", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Stat(SafeFileHandle fd, in byte path, int flags, uint mask, out Statx stat);

    [DllImport("libc.so.6", EntryPoint = "getsockopt", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int GetSocketOption(SafeFileHandle fd, int layer, int name, out ulong option, ref uint size);

    [DllImport("libc.so.6", EntryPoint = "getsockopt", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int GetIntegerSocketOption(SafeFileHandle fd, int layer, int name, out int option, ref uint size);

    [DllImport("libc.so.6", EntryPoint = "getsockname", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int GetSocketName(SafeFileHandle fd, [Out] byte[] endpoint, ref uint size);

    [DllImport("libc.so.6", EntryPoint = "getpeername", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int GetPeerName(SafeFileHandle fd, [Out] byte[] endpoint, ref uint size);
#pragma warning restore SYSLIB1054
}
