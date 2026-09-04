using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Footprint.Core.Runtime;

internal sealed record CaptureRuntimeLayout(HashSet<string> Files, HashSet<string> Directories);
internal readonly record struct CaptureRuntimeArchivePath(string FullPath, bool IsDirectory);

internal static class CaptureRuntimePayloadValidator
{
    internal static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal static async Task ExtractArchiveAsync(string archivePath, string destinationRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        using var archive = ZipFile.OpenRead(archivePath);
        var written = new HashSet<string>(PathComparer);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectArchiveLink(entry);
            var destination = SafeArchiveDestination(destinationRoot, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            if (!written.Add(destination))
                throw new CaptureRuntimeException("capture_runtime_archive_invalid", "采集运行时压缩包无效。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            var extractedLength = output.Length;
            await output.DisposeAsync();
            if (extractedLength != entry.Length)
                throw new CaptureRuntimeException("capture_runtime_length_mismatch", "采集运行时资源长度校验失败。");
            if (RequiresPeX64Verification(destination)) await VerifyPeX64Async(destination, cancellationToken);
        }
    }

    internal static async Task VerifyArchiveExtractionAsync(string archivePath, string destinationRoot,
        string? replacedRelativePath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectArchiveLink(entry);
            var destination = SafeArchiveDestination(destinationRoot, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (string.Equals(entry.FullName.Replace('\\', '/'), replacedRelativePath,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(destination) || new FileInfo(destination).Length != entry.Length)
                throw new CaptureRuntimeException("capture_runtime_length_mismatch", "采集运行时资源长度校验失败。");
            await using var archiveStream = entry.Open();
            await using var extractedStream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var archiveHash = await SHA256.HashDataAsync(archiveStream, cancellationToken);
            var extractedHash = await SHA256.HashDataAsync(extractedStream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(archiveHash, extractedHash))
                throw new CaptureRuntimeException("capture_runtime_hash_mismatch", "采集运行时资源哈希校验失败。");
            if (RequiresPeX64Verification(destination)) await VerifyPeX64Async(destination, cancellationToken);
        }
    }

    internal static List<CaptureRuntimeArchivePath> GetArchivePaths(string archivePath, string destinationRoot)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var paths = new List<CaptureRuntimeArchivePath>(archive.Entries.Count);
        foreach (var entry in archive.Entries)
        {
            RejectArchiveLink(entry);
            paths.Add(new CaptureRuntimeArchivePath(
                SafeArchiveDestination(destinationRoot, entry.FullName), string.IsNullOrEmpty(entry.Name)));
        }
        return paths;
    }

    internal static CaptureRuntimeLayout SnapshotLayout(string root)
    {
        if (!Directory.Exists(root))
            throw new CaptureRuntimeException("capture_runtime_incomplete", "采集运行时目录不完整。");
        RejectReparsePoint(root);
        var files = new HashSet<string>(PathComparer);
        var directories = new HashSet<string>(PathComparer);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparsePoint(entry);
                var relative = NormalizeRelative(root, entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                {
                    directories.Add(relative);
                    pending.Push(entry);
                }
                else
                {
                    files.Add(relative);
                }
            }
        }
        return new CaptureRuntimeLayout(files, directories);
    }

    internal static async Task VerifyPeX64Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var dos = new byte[64];
        await ReadExactlyAsync(stream, dos, cancellationToken);
        if (dos[0] != (byte)'M' || dos[1] != (byte)'Z') ThrowInvalidPe();
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3c, 4));
        if (peOffset < 64 || peOffset > stream.Length - 26) ThrowInvalidPe();
        stream.Position = peOffset;
        var header = new byte[26];
        await ReadExactlyAsync(stream, header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual("PE\0\0"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2)) != 0x8664 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(24, 2)) != 0x20b)
            ThrowInvalidPe();
        return;

        static void ThrowInvalidPe() => throw new CaptureRuntimeException(
            "capture_runtime_architecture_mismatch", "采集运行时 PE 架构不是 Windows x64。");
    }

    internal static string SafeDestination(string root, string relativePath)
    {
        if (!CaptureRuntimeManifest.IsSafeRelativePath(relativePath))
            throw new CaptureRuntimeException("capture_runtime_path_traversal", "采集运行时资源路径不安全。");
        return EnsureUnderRoot(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static bool RequiresPeX64Verification(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pyd", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string SafeArchiveDestination(string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0) return Path.GetFullPath(root);
        if (!CaptureRuntimeManifest.IsSafeRelativePath(normalized))
            throw new CaptureRuntimeException("capture_runtime_path_traversal", "采集运行时资源路径不安全。");
        return EnsureUnderRoot(root, normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string EnsureUnderRoot(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!destination.StartsWith(fullRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new CaptureRuntimeException("capture_runtime_path_traversal", "采集运行时资源路径不安全。");
        return destination;
    }

    private static void RejectArchiveLink(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
        if (unixType == 0xa000)
            throw new CaptureRuntimeException("capture_runtime_path_traversal", "采集运行时资源路径不安全。");
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new CaptureRuntimeException("capture_runtime_path_traversal", "采集运行时资源路径不安全。");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
                throw new CaptureRuntimeException("capture_runtime_architecture_mismatch",
                    "采集运行时 PE 架构不是 Windows x64。");
            total += read;
        }
    }
}
