using System.Security.Cryptography;
using System.Text;

namespace Footprint.Parser;

public sealed record ContactRecord(string Id, string SourceId, string DisplayName, string Alias);
public sealed record SessionRecord(string Id, string SourceId, string Title, DateTimeOffset LastMessageUtc);
public sealed record MessageRecord(string Id, string SourceId, string SessionId, string SenderId,
    DateTimeOffset SentAtUtc, string Kind, string Body, string? MediaRelativePath, string? MediaSha256);
public sealed record FavoriteRecord(string Id, string SourceId, string Kind, string Title, string? Url,
    DateTimeOffset CreatedAtUtc);
public sealed record MediaRecord(string Id, string MessageId, string Kind, string RelativePath, string Sha256);

public sealed record ParsedPackage(string SourceId, IReadOnlyList<ContactRecord> Contacts,
    IReadOnlyList<SessionRecord> Sessions, IReadOnlyList<MessageRecord> Messages,
    IReadOnlyList<FavoriteRecord> Favorites, IReadOnlyList<MediaRecord> Media);

public static class DeterministicIdentity
{
    public static string Create(string entity, params string[] sourceParts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(sourceParts);
        if (sourceParts.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("确定性标识的源字段不能为空。", nameof(sourceParts));
        var canonical = entity + "\n" + string.Join("\n", sourceParts.Select(value => value.Normalize(NormalizationForm.FormC)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
