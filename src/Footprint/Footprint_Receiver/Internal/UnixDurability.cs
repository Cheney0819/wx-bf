using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Footprint.Receiver.Internal;

internal static partial class UnixDurability
{
    private const int O_RDONLY = 0;
    private const int O_DIRECTORY_LINUX = 0x10000;
    private const uint S_IFMT = 0xF000;
    private const uint S_IFREG = 0x8000;

    public static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void SecureFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static bool IsRegularFileNoFollow(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            return info.Exists && info.LinkTarget is null && (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
        }
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return false;

        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (Lstat(path, buffer) != 0) return false;
            var mode = OperatingSystem.IsMacOS()
                ? unchecked((uint)(ushort)Marshal.ReadInt16(buffer, 4))
                : unchecked((uint)Marshal.ReadInt32(buffer, 24));
            return (mode & S_IFMT) == S_IFREG;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void FlushDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var flags = OperatingSystem.IsLinux() ? O_RDONLY | O_DIRECTORY_LINUX : O_RDONLY;
        using var handle = Open(path, flags);
        if (handle.IsInvalid || Fsync(handle) != 0) throw new IOException($"无法同步目录：{path}");
    }

    private static SafeFileHandle Open(string path, int flags) => open(path, flags);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial SafeFileHandle open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(SafeFileHandle handle);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Lstat(string path, IntPtr buffer);
}
