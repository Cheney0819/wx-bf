using System.Buffers;
using System.IO.Compression;
using System.Text;
using Footprint.Receiver.Internal;
using Footprint.Receiver.Network;

namespace Footprint.Receiver.Publication;

public sealed record PackageExpansionLimits(int MaxEntries, long MaxEntryBytes, long MaxTotalBytes, double MaxCompressionRatio)
{
    public static PackageExpansionLimits Default { get; } = new(100_000, 16L * 1024 * 1024 * 1024, 256L * 1024 * 1024 * 1024, 1000);
}

public sealed class PackageExpander(PackageExpansionLimits? limits = null)
{
    private readonly PackageExpansionLimits _limits = limits ?? PackageExpansionLimits.Default;
    private const int BufferSize = 128 * 1024;

    public async Task<string> ExpandAndPublishAsync(string packagePath, string runId, string packagesRoot, CancellationToken cancellationToken = default)
    {
        PackageIdentity.ValidateRunId(runId);
        packagePath = Path.GetFullPath(packagePath);
        packagesRoot = Path.GetFullPath(packagesRoot);
        UnixDurability.SecureDirectory(packagesRoot);
        RejectDirectoryLink(new DirectoryInfo(packagesRoot));
        var final = Path.Combine(packagesRoot, runId);
        if (Directory.Exists(final))
        {
            await ValidateExistingPublicationAsync(packagePath, final, cancellationToken).ConfigureAwait(false);
            RemoveStalePartials(packagesRoot, runId);
            return final;
        }
        if (File.Exists(final)) throw new IOException("发布目标不是目录。");

        var temporary = Path.Combine(packagesRoot, runId + ".partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        UnixDurability.SecureDirectory(temporary);
        try
        {
            await ExpandValidatedAsync(packagePath, temporary, cancellationToken).ConfigureAwait(false);
            FlushTreeDirectories(temporary);
            Directory.Move(temporary, final);
            UnixDurability.FlushDirectory(packagesRoot);
            RemoveStalePartials(packagesRoot, runId);
            return final;
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            throw;
        }
    }

    private async Task ValidateExistingPublicationAsync(string packagePath, string publishedRoot, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > _limits.MaxEntries) throw new InvalidDataException("ZIP 条目数量超限。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        long declaredTotal = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateEntry(entry, names);
            if (entry.Length > _limits.MaxEntryBytes) throw new InvalidDataException("ZIP 单条目大小超限。");
            declaredTotal = checked(declaredTotal + entry.Length);
            if (declaredTotal > _limits.MaxTotalBytes) throw new InvalidDataException("ZIP 总展开大小超限。");
            if (entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > _limits.MaxCompressionRatio)) throw new InvalidDataException("ZIP 压缩比超限。");
        }
        ValidatePublishedTreeShape(publishedRoot, names);
        var prefix = publishedRoot.EndsWith(Path.DirectorySeparatorChar) ? publishedRoot : publishedRoot + Path.DirectorySeparatorChar;
        var left = ArrayPool<byte>.Shared.Rent(BufferSize);
        var right = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(publishedRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(target)) throw new InvalidDataException("现有发布目录与已验证包不一致。");
                var info = new FileInfo(target);
                if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != entry.Length) throw new InvalidDataException("现有发布文件无效。");
                await using var expected = entry.Open();
                await using var actual = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var crc = uint.MaxValue;
                while (true)
                {
                    var expectedRead = await expected.ReadAsync(left.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    var actualRead = await actual.ReadAsync(right.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    if (expectedRead != actualRead || !left.AsSpan(0, expectedRead).SequenceEqual(right.AsSpan(0, actualRead))) throw new InvalidDataException("现有发布文件内容与已验证包不一致。");
                    if (expectedRead == 0) break;
                    Crc32.Update(ref crc, left.AsSpan(0, expectedRead));
                }
                if (~crc != entry.Crc32) throw new InvalidDataException("ZIP 条目 CRC-32 校验失败。");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(left, clearArray: true);
            ArrayPool<byte>.Shared.Return(right, clearArray: true);
        }
    }

    private static void ValidatePublishedTreeShape(string publishedRoot, HashSet<string> expectedFiles)
    {
        RejectDirectoryLink(new DirectoryInfo(publishedRoot));
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in expectedFiles)
        {
            var parts = file.Split('/');
            var current = "";
            for (var index = 0; index < parts.Length - 1; index++)
            {
                current = current.Length == 0 ? parts[index] : current + "/" + parts[index];
                expectedDirectories.Add(current);
            }
        }

        var actualFiles = new HashSet<string>(StringComparer.Ordinal);
        var actualDirectories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(DirectoryInfo Directory, string Relative)>();
        pending.Push((new DirectoryInfo(publishedRoot), ""));
        while (pending.TryPop(out var current))
        {
            foreach (var item in current.Directory.EnumerateFileSystemInfos())
            {
                if (item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("现有发布树包含链接。");
                var relative = current.Relative.Length == 0 ? item.Name : current.Relative + "/" + item.Name;
                if (item is DirectoryInfo directory)
                {
                    if (!expectedDirectories.Contains(relative) || !actualDirectories.Add(relative)) throw new InvalidDataException("现有发布目录与已验证包不一致。");
                    pending.Push((directory, relative));
                }
                else if (item is FileInfo)
                {
                    if (!expectedFiles.Contains(relative) || !actualFiles.Add(relative)) throw new InvalidDataException("现有发布目录与已验证包不一致。");
                }
                else throw new InvalidDataException("现有发布树包含非普通文件。");
            }
        }
        if (!actualFiles.SetEquals(expectedFiles) || !actualDirectories.SetEquals(expectedDirectories)) throw new InvalidDataException("现有发布目录与已验证包不一致。");
    }

    private static void RejectDirectoryLink(DirectoryInfo directory)
    {
        directory.Refresh();
        if (directory.LinkTarget is not null || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("发布目录不能是链接。");
    }

    private async Task ExpandValidatedAsync(string packagePath, string root, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count > _limits.MaxEntries) throw new InvalidDataException("ZIP 条目数量超限。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        long declaredTotal = 0;
        foreach (var entry in archive.Entries)
        {
            var name = ValidateEntry(entry, names);
            if (entry.Length > _limits.MaxEntryBytes) throw new InvalidDataException("ZIP 单条目大小超限。");
            declaredTotal = checked(declaredTotal + entry.Length);
            if (declaredTotal > _limits.MaxTotalBytes) throw new InvalidDataException("ZIP 总展开大小超限。");
            if (entry.Length > 0 && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > _limits.MaxCompressionRatio)) throw new InvalidDataException("ZIP 压缩比超限。");
            _ = name;
        }

        long actualTotal = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName.Normalize(NormalizationForm.FormC);
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("ZIP 条目逃逸发布目录。");
            var parent = Path.GetDirectoryName(target)!;
            UnixDurability.SecureDirectory(parent);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long entryActual = 0;
            var crc = uint.MaxValue;
            try
            {
                await using var source = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                UnixDurability.SecureFile(target);
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    entryActual = checked(entryActual + read);
                    actualTotal = checked(actualTotal + read);
                    if (entryActual > entry.Length || entryActual > _limits.MaxEntryBytes || actualTotal > _limits.MaxTotalBytes) throw new InvalidDataException("ZIP 实际展开大小超限。");
                    Crc32.Update(ref crc, buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (entryActual != entry.Length) throw new InvalidDataException("ZIP 条目长度与目录声明不一致。");
                if (~crc != entry.Crc32) throw new InvalidDataException("ZIP 条目 CRC-32 校验失败。");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
    }

    private static string ValidateEntry(ZipArchiveEntry entry, HashSet<string> names)
    {
        var name = entry.FullName;
        if (string.IsNullOrEmpty(name) || name.EndsWith('/') || name.Contains('\\') || Path.IsPathRooted(name) || name.StartsWith('/') || name.Contains('\0') ||
            (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':')) throw new InvalidDataException("ZIP 路径无效。");
        if (!string.Equals(name, name.Normalize(NormalizationForm.FormC), StringComparison.Ordinal)) throw new InvalidDataException("ZIP 路径不是 NFC 规范形式。");
        var parts = name.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or "..")) throw new InvalidDataException("ZIP 路径包含无效段。");
        if (!names.Add(name)) throw new InvalidDataException("ZIP 包含重复条目。");
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType != 0 && unixType != 0x8000) throw new InvalidDataException("ZIP 仅允许普通文件。");
        if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0) throw new InvalidDataException("ZIP 仅允许普通文件。");
        return name;
    }

    private static void FlushTreeDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(value => value.Length)) UnixDurability.FlushDirectory(directory);
        UnixDurability.FlushDirectory(root);
    }

    private static void RemoveStalePartials(string root, string runId)
    {
        foreach (var path in Directory.EnumerateDirectories(root, runId + ".partial-*", SearchOption.TopDirectoryOnly)) Directory.Delete(path, true);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();
        public static void Update(ref uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes) crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
        }
        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
                table[index] = value;
            }
            return table;
        }
    }
}
