using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Footprint.Media;

public sealed record MediaTransformResult(string OutputPath, string OutputSha256, long Length, string Format);

internal static class MediaFiles
{
    private const int BufferSize = 128 * 1024;

    public static async Task<FileInfo> VerifySourceAsync(string sourcePath, string expectedSha256,
        CancellationToken cancellationToken)
    {
        ValidateSha256(expectedSha256);
        var path = Path.GetFullPath(sourcePath);
        if (!File.Exists(path)) throw new FileNotFoundException("媒体源文件不存在。", path);
        var info = new FileInfo(path);
        info.Refresh();
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("媒体源文件不能是链接。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal)) throw new InvalidDataException("媒体 SHA-256 校验失败。");
        info.Refresh();
        if (stream.Length != info.Length) throw new InvalidDataException("媒体源文件在校验期间发生变化。");
        return info;
    }

    public static async Task<MediaTransformResult> PublishAsync(string temporaryPath, string outputDirectory,
        string extension, string format, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var length = input.Length;
        Directory.CreateDirectory(outputDirectory);
        RejectDirectoryLink(outputDirectory);
        var final = Path.Combine(Path.GetFullPath(outputDirectory), hash + extension);
        if (File.Exists(final))
        {
            await VerifySourceAsync(final, hash, cancellationToken).ConfigureAwait(false);
            File.Delete(temporaryPath);
            return new MediaTransformResult(final, hash, length, format);
        }
        File.Move(temporaryPath, final);
        return new MediaTransformResult(final, hash, length, format);
    }

    public static string CreateTemporaryPath(string outputDirectory)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(outputDirectory)) ?? throw new InvalidDataException("媒体输出目录无效。");
        Directory.CreateDirectory(parent);
        RejectDirectoryLink(parent);
        return Path.Combine(parent, ".media.partial-" + Guid.NewGuid().ToString("N"));
    }

    public static void ValidateSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("SHA-256 必须为 64 位 lowercase 十六进制。", nameof(value));
    }

    public static void RejectDirectoryLink(string directory)
    {
        var current = new DirectoryInfo(directory);
        current.Refresh();
        if (current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("媒体输出目录不能是链接。");
    }
}

public sealed class ImageDecryptor
{
    private const int BufferSize = 128 * 1024;
    private static readonly (byte[] Header, string Extension, string Format)[] Formats =
    [
        (Encoding.ASCII.GetBytes("wxgf"), ".wxgf", "wxgf"),
        ([0xff, 0xd8, 0xff], ".jpg", "jpg"),
        ([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], ".png", "png"),
        (Encoding.ASCII.GetBytes("GIF87a"), ".gif", "gif"),
        (Encoding.ASCII.GetBytes("GIF89a"), ".gif", "gif")
    ];

    public async Task<MediaTransformResult> DecryptAsync(string sourcePath, string expectedSourceSha256,
        string outputDirectory, CancellationToken cancellationToken = default)
    {
        var source = await MediaFiles.VerifySourceAsync(sourcePath, expectedSourceSha256, cancellationToken).ConfigureAwait(false);
        byte[] prefix = new byte[Math.Min(16, checked((int)Math.Min(source.Length, 16)))];
        await using (var probe = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                         BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await probe.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var detected = Detect(prefix);
        var temporary = MediaFiles.CreateTemporaryPath(outputDirectory);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            {
                await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    BufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (detected.Key is not null)
                        for (var index = 0; index < read; index++) buffer[index] ^= detected.Key.Value;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            return await MediaFiles.PublishAsync(temporary, outputDirectory, detected.Extension, detected.Format,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static (byte? Key, string Extension, string Format) Detect(byte[] encrypted)
    {
        foreach (var format in Formats)
        {
            if (encrypted.Length < format.Header.Length) continue;
            if (encrypted.AsSpan(0, format.Header.Length).SequenceEqual(format.Header))
                return (null, format.Extension, format.Format);
            var key = (byte)(encrypted[0] ^ format.Header[0]);
            if (format.Header.Select((value, index) => (byte)(encrypted[index] ^ key) == value).All(matches => matches))
                return (key, format.Extension, format.Format);
        }
        throw new InvalidDataException("无法识别本地图片保护格式。");
    }
}

public sealed class VoiceTransformer
{
    public async Task<MediaTransformResult> ToWaveAsync(string sourcePath, string expectedSourceSha256,
        string outputDirectory, int sampleRate, short channels, CancellationToken cancellationToken = default)
    {
        var source = await MediaFiles.VerifySourceAsync(sourcePath, expectedSourceSha256, cancellationToken).ConfigureAwait(false);
        if (sampleRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (source.Length > int.MaxValue - 44) throw new InvalidDataException("语音文件过大。");
        var temporary = MediaFiles.CreateTemporaryPath(outputDirectory);
        try
        {
            {
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                var header = CreateWaveHeader(checked((int)source.Length), sampleRate, channels);
                await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            return await MediaFiles.PublishAsync(temporary, outputDirectory, ".wav", "wav", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static byte[] CreateWaveHeader(int dataLength, int sampleRate, short channels)
    {
        const short bits = 16;
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + dataLength);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), sampleRate);
        var blockAlign = checked((short)(channels * bits / 8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), checked(sampleRate * blockAlign));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), bits);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), dataLength);
        return header;
    }
}

public sealed class VerifiedMediaCopier
{
    public async Task<MediaTransformResult> CopyAsync(string sourcePath, string expectedSourceSha256,
        string outputDirectory, string extension, CancellationToken cancellationToken = default)
    {
        var source = await MediaFiles.VerifySourceAsync(sourcePath, expectedSourceSha256, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(extension) || extension.Contains('/') || extension.Contains('\\'))
            throw new ArgumentException("媒体扩展名无效。", nameof(extension));
        if (!extension.StartsWith('.')) extension = "." + extension;
        var temporary = MediaFiles.CreateTemporaryPath(outputDirectory);
        try
        {
            {
                await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            return await MediaFiles.PublishAsync(temporary, outputDirectory, extension, extension.TrimStart('.'), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }
}
