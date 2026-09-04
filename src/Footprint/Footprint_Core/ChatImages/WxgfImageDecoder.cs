using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace Footprint.Core;

public readonly record struct WxgfImageInfo(int Width, int Height, int HevcOffset);

public interface IWxgfProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class WxgfProcessRunner : IWxgfProcessRunner
{
    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(fileName, arguments, null, timeout, cancellationToken);
}

public sealed class ChatImagePayloadNormalizationException : IOException
{
    public ChatImagePayloadNormalizationException(string errorCode, string message)
        : this(errorCode, message, null, null, null, null) { }

    public ChatImagePayloadNormalizationException(string errorCode, string message, int? ffmpegExitCode = null,
        bool? ffmpegTimedOut = null, long? ffmpegOutputSize = null, string? ffmpegErrorSummary = null)
        : base(ProcessSummary(message, ffmpegExitCode, ffmpegTimedOut, ffmpegOutputSize, ffmpegErrorSummary))
    {
        ErrorCode = errorCode;
        FfmpegExitCode = ffmpegExitCode;
        FfmpegTimedOut = ffmpegTimedOut;
        FfmpegOutputSize = ffmpegOutputSize;
        FfmpegErrorSummary = Bound(ffmpegErrorSummary);
    }

    public string ErrorCode { get; }
    public int? FfmpegExitCode { get; }
    public bool? FfmpegTimedOut { get; }
    public long? FfmpegOutputSize { get; }
    public string? FfmpegErrorSummary { get; }

    private static string ProcessSummary(string message, int? exitCode, bool? timedOut, long? outputSize,
        string? errorSummary)
    {
        if (exitCode is null && timedOut is null && outputSize is null && errorSummary is null) return message;
        return $"{message} exit_code={exitCode?.ToString() ?? "unknown"}; " +
               $"timed_out={timedOut?.ToString().ToLowerInvariant() ?? "unknown"}; " +
               $"output_size={outputSize?.ToString() ?? "unknown"}; " +
               $"stderr={Bound(errorSummary)}";
    }

    private static string Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var normalized = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }
}

public sealed class WxgfImageDecoder
{
    private static ReadOnlySpan<byte> Magic => "wxgf"u8;
    private readonly string _ffmpegExecutable;
    private readonly IWxgfProcessRunner _processRunner;

    public WxgfImageDecoder(string ffmpegExecutable, IWxgfProcessRunner? processRunner = null)
    {
        if (string.IsNullOrWhiteSpace(ffmpegExecutable))
            throw new ArgumentException("FFmpeg executable path is required.", nameof(ffmpegExecutable));
        _ffmpegExecutable = ffmpegExecutable;
        _processRunner = processRunner ?? new WxgfProcessRunner();
    }

    public static bool IsWxgf(ReadOnlySpan<byte> payload) =>
        payload.Length >= Magic.Length && payload[..Magic.Length].SequenceEqual(Magic);

    public static bool TryParse(ReadOnlySpan<byte> payload, out WxgfImageInfo info)
    {
        info = default;
        if (payload.Length < 15 || !IsWxgf(payload)) return false;
        var width = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(7, 2));
        var height = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(9, 2));
        if (width == 0 || height == 0) return false;
        var hevcOffset = FindAnnexBOffset(payload);
        if (hevcOffset < 0) return false;
        info = new WxgfImageInfo(width, height, hevcOffset);
        return true;
    }

    public async Task<string> DecodeFirstFrameAsync(string sourcePath, string workDirectory,
        CancellationToken cancellationToken)
    {
        var payload = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        if (!TryParse(payload, out var info))
            throw new ChatImagePayloadNormalizationException("invalid_wxgf_payload", "WXGF header or HEVC payload is invalid.");

        Directory.CreateDirectory(workDirectory);
        var token = Guid.NewGuid().ToString("N");
        var hevcPath = Path.Combine(workDirectory, token + ".hevc");
        var outputPath = Path.Combine(workDirectory, token + ".png");
        try
        {
            await File.WriteAllBytesAsync(hevcPath, payload[info.HevcOffset..], cancellationToken);
            ProcessResult result;
            try
            {
                result = await _processRunner.RunAsync(_ffmpegExecutable,
                [
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-f", "hevc", "-i", hevcPath,
                    "-frames:v", "1", "-update", "1", outputPath
                ], TimeSpan.FromMinutes(2), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new ChatImagePayloadNormalizationException("wxgf_decode_failed",
                    $"FFmpeg process failed: {error.GetType().Name}.", ffmpegTimedOut: false,
                    ffmpegOutputSize: 0);
            }
            var outputSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
            var errorSummary = SanitizeProcessError(result.StandardError, workDirectory, hevcPath, outputPath,
                _ffmpegExecutable);
            if (result.TimedOut)
                throw new ChatImagePayloadNormalizationException("wxgf_decode_timeout", "FFmpeg timed out decoding WXGF.",
                    result.ExitCode, result.TimedOut, outputSize, errorSummary);
            if (result.ExitCode != 0 || outputSize == 0)
                throw new ChatImagePayloadNormalizationException("wxgf_decode_failed", "FFmpeg did not produce a PNG frame.",
                    result.ExitCode, result.TimedOut, outputSize, errorSummary);
            return outputPath;
        }
        catch
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
        finally
        {
            if (File.Exists(hevcPath)) File.Delete(hevcPath);
        }
    }

    private static int FindAnnexBOffset(ReadOnlySpan<byte> payload)
    {
        for (var index = 11; index <= payload.Length - 3; index++)
        {
            if (index <= payload.Length - 4 && payload[index] == 0 && payload[index + 1] == 0 &&
                payload[index + 2] == 0 && payload[index + 3] == 1) return index;
            if (payload[index] == 0 && payload[index + 1] == 0 && payload[index + 2] == 1) return index;
        }
        return -1;
    }

    private static string? SanitizeProcessError(string? value, string workDirectory, string hevcPath,
        string outputPath, string ffmpegExecutable)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sanitized = value;
        foreach (var replacement in new[]
                 {
                     (Path: hevcPath, Token: "<hevc>"),
                     (Path: outputPath, Token: "<output>"),
                     (Path: ffmpegExecutable, Token: "<ffmpeg>"),
                     (Path: workDirectory, Token: "<work>")
                 })
        {
            sanitized = sanitized.Replace(replacement.Path, replacement.Token,
                StringComparison.OrdinalIgnoreCase);
            sanitized = sanitized.Replace(replacement.Path.Replace('\\', '/'), replacement.Token,
                StringComparison.OrdinalIgnoreCase);
        }
        sanitized = Regex.Replace(sanitized, @"(?i)(?<![\w])(?:[a-z]:[\\/]|\\\\)[^\s""'<>|]*", "<path>");
        sanitized = Regex.Replace(sanitized, @"(?<![\w<])/(?:[^\s""'<>|]+)", "<path>");
        return sanitized;
    }
}

public interface IChatImagePayloadNormalizer
{
    Task<string> NormalizeAsync(string decryptedPath, string workDirectory, CancellationToken cancellationToken);
}

public sealed class ChatImagePayloadNormalizer(WxgfImageDecoder? wxgfDecoder = null) : IChatImagePayloadNormalizer
{
    public async Task<string> NormalizeAsync(string decryptedPath, string workDirectory,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await using (var stream = new FileStream(decryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         header.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var read = await stream.ReadAsync(header, cancellationToken);
            if (!WxgfImageDecoder.IsWxgf(header.AsSpan(0, read))) return decryptedPath;
        }

        if (wxgfDecoder is null)
            throw new ChatImagePayloadNormalizationException("wxgf_decoder_unavailable", "WXGF decoder is unavailable.");
        return await wxgfDecoder.DecodeFirstFrameAsync(decryptedPath, workDirectory, cancellationToken);
    }
}
