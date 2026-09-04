using System.Text.Json;

namespace Footprint.Core;

public sealed class ChatImagePublisher
{
    private readonly Dictionary<string, string> _publishedBySha = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, string>? _publish;

    public ChatImagePublisher() { }

    internal ChatImagePublisher(Action<string, string> publish) =>
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));

    public async Task<string> PublishAsync(string sessionDirectory, string sourcePath, ChatImageIndexRecord index,
        ChatImageVerification verification, CancellationToken cancellationToken = default)
    {
        var bucket = index.Variant switch
        {
            ChatImageVariant.Thumbnail => "thumbnails",
            ChatImageVariant.Full => "full",
            _ => "unknown"
        };
        var safeName = SafeName(Path.GetFileNameWithoutExtension(index.FileName));
        var extension = "." + verification.Format;
        var relative = _publishedBySha.TryGetValue(verification.OutputSha256, out var existing)
            ? existing
            : $"chat-images/{bucket}/{verification.OutputSha256[..8]}-{safeName}{extension}";
        if (!_publishedBySha.ContainsKey(verification.OutputSha256))
        {
            var destination = Path.Combine(sessionDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await AtomicFile.WriteAsync(destination, async (output, token) =>
            {
                await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, 1024 * 1024, token);
            }, cancellationToken, _publish);
            _publishedBySha[verification.OutputSha256] = relative;
        }
        return relative;
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(safe) ? "image" : safe;
    }
}
