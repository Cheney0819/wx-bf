using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Footprint.Archive;

internal sealed class ArchiveLease : IAsyncDisposable
{
    private readonly FileStream _stream;
    private ArchiveLease(FileStream stream) => _stream = stream;

    public static async Task<ArchiveLease> AcquireAsync(string archiveRoot, string sourceId,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(archiveRoot, "." + sourceId + ".archive.lock");
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.WriteThrough);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return new ArchiveLease(stream);
            }
            catch (IOException) when (attempt < 600)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync() => await _stream.DisposeAsync().ConfigureAwait(false);
}

internal static partial class ArchiveDurability
{
    private const int O_RDONLY = 0;
    private const int O_DIRECTORY_LINUX = 0x10000;

    public static void FlushDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        using var handle = open(path, OperatingSystem.IsLinux() ? O_RDONLY | O_DIRECTORY_LINUX : O_RDONLY);
        if (handle.IsInvalid || fsync(handle) != 0) throw new IOException("无法同步档案目录。");
    }

    public static void FlushTree(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(value => value.Length)) FlushDirectory(directory);
        FlushDirectory(root);
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial SafeFileHandle open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int fsync(SafeFileHandle handle);
}
