using System.Security.Cryptography;
using SkiaSharp;

namespace Footprint.Core;

public static class ChatImageValidator
{
    public static Task<ChatImageVerification> ValidateAsync(string path, string? expectedMd5,
        long? expectedSize, CancellationToken cancellationToken = default) =>
        ValidateWithDecoderAsync(path, expectedMd5, expectedSize, cancellationToken,
            OperatingSystem.IsWindows() ? DecodeOnWindows : null);

    private static async Task<ChatImageVerification> ValidateWithDecoderAsync(string path, string? expectedMd5,
        long? expectedSize, CancellationToken cancellationToken,
        Func<Stream, int, int, string?>? windowsDecoder)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[16];
            var read = await stream.ReadAsync(header, cancellationToken);
            var format = DetectFormat(header.AsSpan(0, read));
            if (format is null) return Failed("invalid_image_payload", "Unsupported image header.");

            stream.Position = 0;
            var dimensions = await ReadDimensionsAsync(stream, format, cancellationToken);
            if (dimensions is null || dimensions.Value.Width is <= 0 or > 100_000 ||
                dimensions.Value.Height is <= 0 or > 100_000 ||
                (long)dimensions.Value.Width * dimensions.Value.Height > 500_000_000)
                return Failed("invalid_image_payload", "Image structure or dimensions are invalid.");
            if (windowsDecoder is not null)
            {
                await using var decoderStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var decoderFailure = windowsDecoder(decoderStream, dimensions.Value.Width, dimensions.Value.Height);
                if (decoderFailure is not null)
                    return Failed("invalid_image_payload", decoderFailure);
            }

            stream.Position = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buffer = new byte[1024 * 1024];
            long size = 0;
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, count);
                md5.AppendData(buffer, 0, count);
                size += count;
            }

            var md5Hex = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            var shaHex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            var md5Status = string.IsNullOrWhiteSpace(expectedMd5) ? "not_applicable" :
                string.Equals(NormalizeMd5(expectedMd5), md5Hex, StringComparison.OrdinalIgnoreCase) ? "passed" : "failed";
            var sizeStatus = expectedSize is null ? "not_applicable" : expectedSize == size ? "passed" : "failed";
            var error = md5Status == "failed" ? "hardlink_md5_mismatch" :
                sizeStatus == "failed" ? "hardlink_size_mismatch" : null;
            return new ChatImageVerification("passed", "passed", md5Status, sizeStatus, format,
                dimensions.Value.Width, dimensions.Value.Height, size, md5Hex, shaHex, error,
                error is null ? null : "Indexed image metadata does not match recovered payload.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return Failed("invalid_image_payload", error.GetType().Name); }
    }

    private static ChatImageVerification Failed(string code, string summary) =>
        new("failed", "failed", "not_applicable", "not_applicable", "", 0, 0, 0, "", "", code, summary);

    private static string? DetectFormat(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[..3].SequenceEqual(new byte[] { 0xff, 0xd8, 0xff })) return "jpeg";
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "png";
        if (header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) return "gif";
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)) return "webp";
        return null;
    }

    private static async Task<(int Width, int Height)?> ReadDimensionsAsync(Stream stream, string format,
        CancellationToken cancellationToken) => format switch
        {
            "png" => await ReadPngAsync(stream, cancellationToken),
            "gif" => await ReadGifAsync(stream, cancellationToken),
            "jpeg" => await ReadJpegAsync(stream, cancellationToken),
            "webp" => await ReadWebpAsync(stream, cancellationToken),
            _ => null
        };

    private static async Task<(int, int)?> ReadPngAsync(Stream stream, CancellationToken token)
    {
        if (stream.Length < 45) return null;
        var bytes = new byte[24];
        if (await ReadExactlyAsync(stream, bytes, token) != bytes.Length ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return null;
        var width = ReadBigEndianInt32(bytes.AsSpan(16, 4));
        var height = ReadBigEndianInt32(bytes.AsSpan(20, 4));
        var tail = new byte[12];
        stream.Seek(-12, SeekOrigin.End);
        if (await ReadExactlyAsync(stream, tail, token) != tail.Length ||
            !tail.AsSpan(4, 4).SequenceEqual("IEND"u8)) return null;
        return (width, height);
    }

    private static async Task<(int, int)?> ReadGifAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[10];
        if (await ReadExactlyAsync(stream, bytes, token) != bytes.Length) return null;
        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() != 0x3b) return null;
        return (bytes[6] | bytes[7] << 8, bytes[8] | bytes[9] << 8);
    }

    private static async Task<(int, int)?> ReadJpegAsync(Stream stream, CancellationToken token)
    {
        var marker = new byte[2];
        if (await ReadExactlyAsync(stream, marker, token) != 2 || marker[0] != 0xff || marker[1] != 0xd8) return null;
        (int Width, int Height)? dimensions = null;
        while (stream.Position < stream.Length)
        {
            token.ThrowIfCancellationRequested();
            int value;
            do { value = stream.ReadByte(); } while (value >= 0 && value != 0xff);
            if (value < 0) return null;
            do { value = stream.ReadByte(); } while (value == 0xff);
            if (value == 0xd9) return dimensions;
            if (value < 0) return null;
            if (value == 0xda)
            {
                stream.Seek(-2, SeekOrigin.End);
                return await ReadExactlyAsync(stream, marker, token) == 2 && marker[0] == 0xff && marker[1] == 0xd9
                    ? dimensions : null;
            }
            if (value is >= 0xd0 and <= 0xd7 || value == 0x01) continue;
            var lengthBytes = new byte[2];
            if (await ReadExactlyAsync(stream, lengthBytes, token) != 2) return null;
            var length = lengthBytes[0] << 8 | lengthBytes[1];
            if (length < 2 || stream.Position + length - 2 > stream.Length) return null;
            if (value is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                var frame = new byte[5];
                if (length < 7 || await ReadExactlyAsync(stream, frame, token) != 5) return null;
                dimensions = (frame[3] << 8 | frame[4], frame[1] << 8 | frame[2]);
                stream.Seek(length - 7, SeekOrigin.Current);
            }
            else stream.Seek(length - 2, SeekOrigin.Current);
        }
        return null;
    }

    private static async Task<(int, int)?> ReadWebpAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[30];
        if (await ReadExactlyAsync(stream, bytes, token) < 30 ||
            !bytes.AsSpan(12, 4).SequenceEqual("VP8X"u8)) return null;
        if (BitConverter.ToUInt32(bytes, 4) + 8L != stream.Length) return null;
        return (1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16),
            1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16));
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) break;
            offset += read;
        }
        return offset;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> value) =>
        value[0] << 24 | value[1] << 16 | value[2] << 8 | value[3];

    private static string NormalizeMd5(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

    private static string? DecodeOnWindows(Stream stream, int width, int height)
    {
        try
        {
            using var codec = SKCodec.Create(stream);
            if (codec is null) return "SkiaSharp codec creation failed.";
            if (codec.Info.Width != width || codec.Info.Height != height)
                return $"SkiaSharp dimensions mismatch: expected {width}x{height}, decoded " +
                       $"{codec.Info.Width}x{codec.Info.Height}.";
            using var bitmap = new SKBitmap(codec.Info);
            var result = codec.GetPixels(codec.Info, bitmap.GetPixels());
            return result == SKCodecResult.Success ? null : WindowsDecoderFailureSummary(result);
        }
        catch (Exception error)
        {
            return $"SkiaSharp decoder exception: {error.GetType().Name}.";
        }
    }

    private static string WindowsDecoderFailureSummary(SKCodecResult result) =>
        $"SkiaSharp decoder result: {result}.";
}
