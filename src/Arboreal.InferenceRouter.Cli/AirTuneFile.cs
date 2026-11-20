using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.Cli;

/// <summary>Linux x64 file admission for the comparison command. Only an inspected inode may be reopened.</summary>
internal static partial class AirTuneFile
{
    private const string Libc = "libc.so.6";
    private const long OpenAt2Number = 437;
    private const int AtCurrentDirectory = -100;
    private const int AtEmptyPath = 0x1000;
    private const int CloseOnExec = 0x80000;
    private const int NonBlocking = 0x800;
    private const ulong DirectoryOnly = 0x10000;
    private const ulong PathOnly = 0x200000;
    private const ulong NoCrossMounts = 0x01;
    private const ulong NoMagicLinks = 0x02;
    private const ulong NoSymlinks = 0x04;
    private const ulong Beneath = 0x08;
    private const ulong TrustedProcChild = Beneath | NoSymlinks | NoCrossMounts;
    private const uint StatFields = 0x107; // STATX_INO, STATX_NLINK, STATX_MODE, STATX_TYPE
    private const int TypeMask = 0xf000;
    private const int RegularFile = 0x8000;
    private const long ProcFs = 0x9fa0;

    internal readonly record struct FileIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode, long FileSystem);

    internal static SafeFileHandle? OpenProcDescriptors()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return null;

        try
        {
            using var root = OpenPath(AtCurrentDirectory, "/proc", PathOnly | CloseOnExec | DirectoryOnly, NoSymlinks);
            if (root is null || !TryMetadata(root, out _, out var fileSystem) || fileSystem.Type != ProcFs)
                return null;

            var pid = ReadSelfPid(root);
            if (pid is null)
                return null;
            using var process = OpenChildPath(root, pid, PathOnly | CloseOnExec | DirectoryOnly);
            if (process is null)
                return null;
            using var status = OpenChildPath(process, "status", PathOnly | CloseOnExec);
            if (status is null || InspectRegular(status, ownProcStatus: true) is not { } identity)
                return null;

            return VerifyProcDirectory(process, status, identity);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    private static SafeFileHandle? VerifyProcDirectory(SafeFileHandle process, SafeFileHandle status, FileIdentity identity)
    {
        // Reopen the kernel-provided status inode with the production flags. No bytes are read.
        SafeFileHandle? directory = null;
        try
        {
            directory = OpenChildPath(process, "fd", PathOnly | CloseOnExec | DirectoryOnly);
            if (directory is null)
                return null;
            using (var data = Reopen(directory, status, identity))
            {
                if (data is null)
                    return null;
            }

            var verified = directory;
            directory = null;
            return verified;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    internal static SafeFileHandle? PinPath(string path) =>
        OpenPath(AtCurrentDirectory, path, PathOnly | CloseOnExec, NoSymlinks | NoMagicLinks);

    internal static FileIdentity? InspectRegular(SafeFileHandle metadata) => InspectRegular(metadata, ownProcStatus: false);

    private static FileIdentity? InspectRegular(SafeFileHandle handle, bool ownProcStatus)
    {
        if (!TryMetadata(handle, out var status, out var fileSystem) ||
            (status.Mask & StatFields) != StatFields || (status.Mode & TypeMask) != RegularFile ||
            status.Links != 1)
        {
            return null;
        }

        var knownFileSystem = fileSystem.Type switch
        {
            // ext2/3/4, XFS, Btrfs, tmpfs, overlayfs
            0xef53 or 0x58465342 or 0x9123683e or 0x01021994 or 0x794c7630 => !ownProcStatus,
            ProcFs => ownProcStatus,
            _ => false,
        };
        return knownFileSystem
            ? new FileIdentity(status.DeviceMajor, status.DeviceMinor, status.Inode, fileSystem.Type)
            : null;
    }

    private static bool TryMetadata(SafeFileHandle handle, out LinuxStatx status, out LinuxStatFs fileSystem)
    {
        var addedRef = false;
        fileSystem = default;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            var fd = handle.DangerousGetHandle().ToInt32();
            return GetStatus(fd, "", AtEmptyPath, StatFields, out status) == 0 &&
                GetFileSystem(fd, out fileSystem) == 0;
        }
        finally
        {
            if (addedRef)
                handle.DangerousRelease();
        }
    }

    internal static SafeFileHandle? Reopen(SafeFileHandle procDescriptors, SafeFileHandle metadata, FileIdentity expected)
    {
        var procRef = false;
        var metadataRef = false;
        int fd;
        try
        {
            procDescriptors.DangerousAddRef(ref procRef);
            metadata.DangerousAddRef(ref metadataRef);
            var name = metadata.DangerousGetHandle().ToInt32().ToString(CultureInfo.InvariantCulture);
            fd = OpenAt(procDescriptors.DangerousGetHandle().ToInt32(), name, NonBlocking | CloseOnExec, 0);
        }
        finally
        {
            if (metadataRef)
                metadata.DangerousRelease();
            if (procRef)
                procDescriptors.DangerousRelease();
        }

        if (fd < 0)
            return null;

        SafeFileHandle? data = null;
        try
        {
            data = new SafeFileHandle(fd, ownsHandle: true);
            if (InspectRegular(data, ownProcStatus: expected.FileSystem == ProcFs) != expected)
                return null;

            var verified = data;
            data = null;
            return verified;
        }
        finally
        {
            data?.Dispose();
        }
    }

    internal static byte[]? Read(SafeFileHandle procDescriptors, string path)
    {
        using var metadata = PinPath(path);
        if (metadata is null || InspectRegular(metadata) is not { } identity)
            return null;
        using var data = Reopen(procDescriptors, metadata, identity);
        if (data is null)
            return null;

        try
        {
            using var stream = new FileStream(data, FileAccess.Read, bufferSize: 1);
            var buffer = new byte[AirTuneRecord.MaxBytes + 1];
            var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return buffer[..count];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static SafeFileHandle? OpenPath(int directory, string path, ulong flags, ulong resolve)
    {
        var how = new OpenHow { Flags = flags, Mode = 0, Resolve = resolve };
        var fd = OpenAt2(OpenAt2Number, directory, path, in how, 24);
        return fd < 0 ? null : new SafeFileHandle((nint)fd, ownsHandle: true);
    }

    private static SafeFileHandle? OpenChildPath(SafeFileHandle directory, string path, ulong flags)
    {
        var addedRef = false;
        try
        {
            directory.DangerousAddRef(ref addedRef);
            return OpenPath(directory.DangerousGetHandle().ToInt32(), path, flags, TrustedProcChild);
        }
        finally
        {
            if (addedRef)
                directory.DangerousRelease();
        }
    }

    private static unsafe string? ReadSelfPid(SafeFileHandle proc)
    {
        Span<byte> bytes = stackalloc byte[16];
        var addedRef = false;
        nint count;
        try
        {
            proc.DangerousAddRef(ref addedRef);
            fixed (byte* buffer = bytes)
            {
                count = ReadLinkAt(proc.DangerousGetHandle().ToInt32(), "self", buffer, (nuint)bytes.Length);
            }
        }
        finally
        {
            if (addedRef)
                proc.DangerousRelease();
        }

        if (count <= 0 || count >= bytes.Length)
            return null;

        var digits = bytes[..(int)count];
        foreach (var digit in digits)
        {
            if (digit is < (byte)'0' or > (byte)'9')
                return null;
        }
        return Encoding.ASCII.GetString(digits);
    }

    // The syscall number and glibc LP64 layouts are specific to the linux-x64 command adapter.
    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct LinuxStatFs
    {
        [FieldOffset(0)] public long Type;
    }

    [LibraryImport(Libc, EntryPoint = "syscall", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial long OpenAt2(long number, long directory, string path, in OpenHow how, nuint size);

    [LibraryImport(Libc, EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GetStatus(int fd, string path, int flags, uint mask, out LinuxStatx status);

    [LibraryImport(Libc, EntryPoint = "fstatfs", SetLastError = true)]
    private static partial int GetFileSystem(int fd, out LinuxStatFs fileSystem);

    [LibraryImport(Libc, EntryPoint = "readlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial nint ReadLinkAt(int fd, string path, byte* buffer, nuint length);

    [LibraryImport(Libc, EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(int fd, string path, int flags, uint mode);
}
