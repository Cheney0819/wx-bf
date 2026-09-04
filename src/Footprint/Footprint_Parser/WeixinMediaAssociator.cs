namespace Footprint.Parser;

public sealed record CapturedMediaReference(string RelativePath, string Sha256, string SourceCategory,
    IReadOnlyDictionary<string, string> AssociationEvidence);

public static class WeixinMediaAssociator
{
    public static ParsedPackage Attach(ParsedPackage package, IReadOnlyList<CapturedMediaReference> captured)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(captured);
        var messages = package.Messages.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var media = package.Media.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var linkedMessages = package.Media.Select(value => value.MessageId).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in captured.OrderBy(value => value.RelativePath, StringComparer.Ordinal))
        {
            var snapshotPath = NormalizeRelativePath(reference.RelativePath);
            ValidateSha256(reference.Sha256);
            if (!reference.AssociationEvidence.TryGetValue("candidate_root", out var candidateRoot) ||
                !reference.AssociationEvidence.TryGetValue("source_relative_path", out var sourceRelative))
                continue;
            var originalPath = NormalizeRelativePath(candidateRoot.TrimEnd('/') + "/" + sourceRelative);
            var matches = messages.Values.Where(message => !linkedMessages.Contains(message.Id) &&
                IsCompatible(message.Kind, reference.SourceCategory) && References(message.Body, originalPath))
                .ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length != 1) throw new InvalidDataException("微信媒体路径匹配到多个消息。");
            var message = matches[0];
            var updated = message with { MediaRelativePath = snapshotPath, MediaSha256 = reference.Sha256 };
            messages[message.Id] = updated;
            linkedMessages.Add(message.Id);
            var id = DeterministicIdentity.Create("media", message.Id, snapshotPath);
            if (!media.TryAdd(id, new MediaRecord(id, message.Id, message.Kind, snapshotPath, reference.Sha256)))
                throw new InvalidDataException("微信媒体记录冲突。");
        }
        return package with
        {
            Messages = package.Messages.Select(value => messages[value.Id]).ToArray(),
            Media = media.Values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray()
        };
    }

    private static bool References(string body, string path)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        return body.Replace('\\', '/').Contains(path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompatible(string kind, string category) => category switch
    {
        "image" => kind == "image",
        "voice" => kind == "voice",
        "attachment" => kind is "video" or "file",
        _ => false
    };

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.StartsWith('/') || Path.IsPathRooted(value))
            throw new InvalidDataException("媒体相对路径无效。");
        var parts = value.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("媒体相对路径无效。");
        return value;
    }

    private static void ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("媒体摘要必须为 lowercase SHA-256。");
    }
}
