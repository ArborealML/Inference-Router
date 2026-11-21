using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Arboreal.InferenceRouter.OperationHead;

// All mutable names are fixed children of an authority-owned directory. Path-only opens
// pin an inode before opening it for I/O, including when the named path refers to a FIFO.
internal static partial class OperationHeadLinux
{
    private const string Libc = "libc.so.6";
    private const long OpenAt2Number = 437;
    private const int AtCurrentDirectory = -100;
    private const int AtEmptyPath = 0x1000;
    private const ulong CloseOnExec = 0x80000;
    private const ulong NonBlocking = 0x800;
    private const ulong DirectoryOnly = 0x10000;
    private const ulong PathOnly = 0x200000;
    private const ulong WriteOnly = 0x1;
    private const ulong ReadWrite = 0x2;
    private const ulong CreateExclusive = 0xc0;
    private const ulong NoMagicLinks = 0x02;
    private const ulong NoSymlinks = 0x04;
    private const ulong FixedChild = 0x01 | NoMagicLinks | NoSymlinks | 0x08;
    private const uint StatFields = 0x111f; // type, mode, nlink, uid, gid, inode, mount ID
    private const int TypeMask = 0xf000;
    private const int Regular = 0x8000;
    private const int Directory = 0x4000;
    private const int SocketFile = 0xc000;
    private const int PrivateFile = 0x180;      // 0600
    private const int PrivateDirectory = 0x1c0; // 0700
    private const int GroupTraverse = 0x8;      // 0010
    private const int GroupSocket = 0x1b0;      // 0660
    private const int OtherWriters = 0x12;    // 0022
    private const int Sticky = 0x200;         // 01000
    private const int LockExclusiveNonBlocking = 2 | 4;
    private const long ProcFileSystem = 0x9fa0;

    internal static bool Supported => OperatingSystem.IsLinux() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64;

    internal readonly record struct Entry(uint Major, uint Minor, ulong Mount, ulong Inode,
        ushort Mode, uint Uid, uint Gid, uint Links)
    {
        internal bool SameMount(Entry other) => Major == other.Major && Minor == other.Minor && Mount == other.Mount;
    }

    internal static SafeFileHandle OpenDirectory(string path)
    {
        var owner = EffectiveUid();
        for (var end = path.LastIndexOf('/'); end > 0; end = path.LastIndexOf('/', end - 1))
        {
            using var parent = OpenPath(AtCurrentDirectory, path[..end], DirectoryOnly | PathOnly,
                NoSymlinks | NoMagicLinks);
            var entry = Inspect(parent);
            // A root-owned sticky scratch directory allows creating names but still protects
            // the authority-owned child. Other untrusted directory writers can replace it.
            if ((entry.Uid != 0 && entry.Uid != owner) ||
                ((entry.Mode & OtherWriters) != 0 && (entry.Uid != 0 || (entry.Mode & Sticky) == 0)))
                throw InvalidObject();
        }
        return OpenPath(AtCurrentDirectory, path, DirectoryOnly, NoSymlinks | NoMagicLinks);
    }

    internal static Entry DirectoryEntry(SafeFileHandle handle, uint owner, bool runtime)
    {
        var entry = Inspect(handle);
        if (entry.Uid != owner || (entry.Mode != (Directory | PrivateDirectory) &&
            (!runtime || entry.Mode != (Directory | PrivateDirectory | GroupTraverse))))
            throw InvalidObject();
        return entry;
    }

    internal static SafeFileHandle OpenDescriptors()
    {
        // This is the only magic-link namespace used. Reopening through the kernel's
        // own procfs descriptor directory cannot follow a caller-selected file name.
        var descriptors = OpenPath(AtCurrentDirectory, "/proc/self/fd", DirectoryOnly, 0);
        try
        {
            if (GetFileSystem(descriptors, out var info) != 0 || info.Type != ProcFileSystem)
                throw InvalidObject();
            return descriptors;
        }
        catch
        {
            descriptors.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle Pin(SafeFileHandle root, string name) =>
        OpenPath(root.DangerousGetHandle().ToInt32(), name, PathOnly, FixedChild);

    internal static Entry CheckRegular(SafeFileHandle handle, Entry root)
    {
        var entry = Inspect(handle);
        if (!entry.SameMount(root) || entry.Mode != (Regular | PrivateFile) ||
            entry.Uid != root.Uid || entry.Links != 1)
            throw InvalidObject();
        return entry;
    }

    internal static SafeFileHandle ReopenRegular(SafeFileHandle descriptors, SafeFileHandle pinned,
        Entry root, Entry expected, bool write = false)
    {
        var addedReference = false;
        try
        {
            pinned.DangerousAddRef(ref addedReference);
            var name = pinned.DangerousGetHandle().ToInt32().ToString(CultureInfo.InvariantCulture);
            var data = Own(OpenAt(descriptors, name, (int)(CloseOnExec | NonBlocking | (write ? ReadWrite : 0)), 0));
            try
            {
                if (CheckRegular(data, root) != expected) throw InvalidObject();
                return data;
            }
            catch
            {
                data.Dispose();
                throw;
            }
        }
        finally
        {
            if (addedReference) pinned.DangerousRelease();
        }
    }

    internal static void Lock(SafeFileHandle handle)
    {
        if (Flock(handle, LockExclusiveNonBlocking) != 0) throw InvalidObject();
    }

    internal static SafeFileHandle CreateTemporary(SafeFileHandle root, string name) =>
        OpenPath(root.DangerousGetHandle().ToInt32(), name, CreateExclusive | WriteOnly, FixedChild, PrivateFile);

    internal static void Replace(SafeFileHandle root, string temporary, string name)
    {
        if (RenameAt(root, temporary, root, name) != 0) throw InvalidObject();
    }

    internal static void Sync(SafeFileHandle directory)
    {
        if (FSync(directory) != 0) throw InvalidObject();
    }

    internal static Entry SecureSocket(SafeFileHandle root, string name, Entry rootEntry)
    {
        using var pinned = Pin(root, name);
        var entry = Inspect(pinned);
        if (!entry.SameMount(rootEntry) || (entry.Mode & TypeMask) != SocketFile ||
            entry.Uid != rootEntry.Uid || entry.Links != 1)
            throw InvalidObject();

        // The runtime directory is protected against replacement by the producer.
        if (ChmodAt(root, name, GroupSocket, 0) != 0) throw InvalidObject();
        using var secured = Pin(root, name);
        var identity = Inspect(secured);
        if (identity != (entry with { Mode = SocketFile | GroupSocket })) throw InvalidObject();
        return identity;
    }

    internal static void RemoveOwned(SafeFileHandle root, string name, Entry owned)
    {
        try
        {
            using var pinned = Pin(root, name);
            if (Inspect(pinned) == owned) _ = UnlinkAt(root, name, 0);
        }
        catch (IOException)
        {
            // A missing or replaced name cannot be cleaned up by this owner.
        }
    }

    internal static Entry Inspect(SafeFileHandle handle)
    {
        if (GetStatus(handle, "", AtEmptyPath, StatFields, out var status) != 0 ||
            (status.Mask & StatFields) != StatFields) throw InvalidObject();
        return new(status.Major, status.Minor, status.Mount, status.Inode, status.Mode, status.Uid,
            status.Gid, status.Links);
    }

    internal static IOException InvalidObject() => new("Operation-head storage or socket identity is unavailable.");

    private static SafeFileHandle OpenPath(int directory, string path, ulong flags, ulong resolve, ulong mode = 0)
    {
        var options = new OpenHow { Flags = CloseOnExec | flags, Mode = mode, Resolve = resolve };
        return Own(checked((int)OpenAt2(OpenAt2Number, directory, path, in options, 24)));
    }

    private static SafeFileHandle Own(int fd) =>
        fd >= 0 ? new SafeFileHandle(fd, ownsHandle: true) : throw InvalidObject();

    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(24)] public uint Gid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint Major;
        [FieldOffset(140)] public uint Minor;
        [FieldOffset(144)] public ulong Mount;
    }

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct StatFs
    {
        [FieldOffset(0)] public long Type;
    }

    [LibraryImport(Libc, EntryPoint = "geteuid")]
    internal static partial uint EffectiveUid();

    [LibraryImport(Libc, EntryPoint = "syscall", StringMarshalling = StringMarshalling.Utf8)]
    private static partial long OpenAt2(long number, long directory, string path, in OpenHow options, nuint size);

    [LibraryImport(Libc, EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GetStatus(SafeFileHandle fd, string path, int flags, uint mask, out Statx status);

    [LibraryImport(Libc, EntryPoint = "fstatfs")]
    private static partial int GetFileSystem(SafeFileHandle fd, out StatFs info);

    [LibraryImport(Libc, EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(SafeFileHandle fd, string path, int flags, uint mode);

    [LibraryImport(Libc, EntryPoint = "flock")]
    private static partial int Flock(SafeFileHandle fd, int operation);

    [LibraryImport(Libc, EntryPoint = "fsync")]
    private static partial int FSync(SafeFileHandle fd);

    [LibraryImport(Libc, EntryPoint = "renameat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt(SafeFileHandle from, string fromName, SafeFileHandle to, string toName);

    [LibraryImport(Libc, EntryPoint = "fchmodat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ChmodAt(SafeFileHandle fd, string name, uint mode, int flags);

    [LibraryImport(Libc, EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(SafeFileHandle directory, string name, int flags);
}
