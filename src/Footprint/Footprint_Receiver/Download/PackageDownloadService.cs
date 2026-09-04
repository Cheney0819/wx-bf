using System.Buffers;
using System.Security.Cryptography;
using Footprint.Receiver.Internal;
using Footprint.Receiver.Network;

namespace Footprint.Receiver.Download;

public sealed class PackageDownloadService
{
    private const int BufferSize = 128 * 1024;

    public async Task<string> DownloadAsync(PendingRun pending, ReceiverPackageResponse response, string destinationPath, CancellationToken cancellationToken = default)
    {
        PackageIdentity.Validate(pending.RunId, pending.PackageLength, pending.PackageSha256);
        PackageIdentity.Validate(pending.RunId, response.PackageLength, response.PackageSha256);
        PackageIdentity.ValidateDeviceId(pending.SourceDeviceId);
        PackageIdentity.ValidateDeviceId(response.SourceDeviceId);
        if (pending.PackageLength != response.PackageLength || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pending.PackageSha256), Convert.FromHexString(response.PackageSha256)))
            throw new InvalidDataException("服务器 pending 与下载声明不一致。");
        if (!string.Equals(pending.SourceDeviceId, response.SourceDeviceId, StringComparison.Ordinal))
            throw new InvalidDataException("服务器 pending 与下载来源 DeviceId 不一致。");

        destinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("下载路径缺少父目录。");
        UnixDurability.SecureDirectory(directory);
        var partial = destinationPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long length = 0;
        byte[] hash;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                UnixDurability.SecureFile(partial);
                while (true)
                {
                    var read = await response.Content.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    length = checked(length + read);
                    if (length > pending.PackageLength) throw new InvalidDataException("下载包长度超过声明值。");
                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            hash = hasher.GetHashAndReset();
            if (length != pending.PackageLength || !CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(pending.PackageSha256))) throw new InvalidDataException("下载包的本地长度或 SHA-256 校验失败。");
            File.Move(partial, destinationPath, true);
            UnixDurability.SecureFile(destinationPath);
            UnixDurability.FlushDirectory(directory);
            return destinationPath;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, BufferSize));
            ArrayPool<byte>.Shared.Return(buffer);
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    internal static void RemoveStaleSiblingPartials(string destinationPath)
    {
        destinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("下载路径缺少父目录。");
        var fileName = Path.GetFileName(destinationPath);
        var prefix = fileName + ".";
        const string suffix = ".partial";
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, fileName + ".*.partial", SearchOption.TopDirectoryOnly))
        {
            var candidate = Path.GetFileName(path);
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal) || !candidate.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var identifier = candidate.AsSpan(prefix.Length, candidate.Length - prefix.Length - suffix.Length);
            if (identifier.Length != 32 || identifier.ContainsAnyExcept("0123456789abcdef")) continue;
            if (UnixDurability.IsRegularFileNoFollow(path)) File.Delete(path);
        }
    }
}
