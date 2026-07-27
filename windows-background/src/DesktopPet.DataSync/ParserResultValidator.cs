using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.DataSync;

public sealed class ParserResultValidator
{
    private const long MaximumResultBytes = 32L * 1024 * 1024;
    private const int MaximumStringCharacters = 64 * 1024;
    private const int MaximumMediaBytes = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<ParserResultDocument> ValidateAsync(
        string resultPath,
        string expectedJobId,
        string expectedSourceSetId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedJobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceSetId);
        var fullPath = Path.GetFullPath(resultPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Parser result is missing.", fullPath);
        if (info.Length > MaximumResultBytes)
            throw new InvalidDataException("Parser result exceeds 32 MiB.");

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        try
        {
            ParserResultDocument result;
            try
            {
                using var json = JsonDocument.Parse(bytes);
                ValidateStringLengths(json.RootElement, propertyName: null);
                result = JsonSerializer.Deserialize<ParserResultDocument>(bytes, JsonOptions) ??
                    throw new InvalidDataException("Parser result is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Parser result JSON violates schema 1.", exception);
            }

            return ValidateDocument(result, expectedJobId, expectedSourceSetId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static ParserResultDocument ValidateDocument(
        ParserResultDocument result,
        string expectedJobId,
        string expectedSourceSetId)
    {
        if (result.SchemaVersion != 1)
            throw new InvalidDataException("Parser result schema is unsupported.");
        if (!string.Equals(result.JobId, expectedJobId, StringComparison.Ordinal) ||
            !string.Equals(result.SourceSetId, expectedSourceSetId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Parser result identity does not match its claimed job.");
        }
        if (result.Messages is null || result.Messages.Count > 5000 ||
            result.Contacts is null || result.Contacts.Count > 5000 ||
            result.Favorites is null || result.Favorites.Count > 1000 ||
            result.Notices is null || result.Notices.Count > 1000)
        {
            throw new InvalidDataException("Parser result count limit was exceeded.");
        }
        if (result.NextCursor is not null &&
            (string.IsNullOrWhiteSpace(result.NextCursor) ||
             result.NextCursor.Length > MaximumStringCharacters ||
             result.NextCursor.Any(character =>
                 !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))))
        {
            throw new InvalidDataException("Parser continuation cursor is invalid.");
        }

        var messageIdentities = new HashSet<string>(StringComparer.Ordinal);
        var uniqueMessages = new List<ParsedMessage>(result.Messages.Count);
        foreach (var message in result.Messages)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Wxid))
                throw new InvalidDataException("Parser message identity is empty.");
            ValidateMedia(message);
            if (messageIdentities.Add(ParserItemIdentity.Message(message)))
                uniqueMessages.Add(message);
        }

        var contacts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contact in result.Contacts)
        {
            if (contact is null || string.IsNullOrWhiteSpace(contact.Wxid) ||
                !contacts.Add(contact.Wxid))
            {
                throw new InvalidDataException("Parser result contains an invalid or duplicate contact.");
            }
        }

        var favorites = new HashSet<(string SourceTable, string SourceId)>();
        foreach (var favorite in result.Favorites)
        {
            if (favorite is null ||
                string.IsNullOrWhiteSpace(favorite.SourceTable) ||
                string.IsNullOrWhiteSpace(favorite.SourceId) ||
                favorite.DataJson is null ||
                !favorites.Add((favorite.SourceTable, favorite.SourceId)))
            {
                throw new InvalidDataException("Parser result contains an invalid or duplicate favorite.");
            }
        }

        foreach (var notice in result.Notices)
        {
            if (notice is null ||
                string.IsNullOrWhiteSpace(notice.Code) ||
                string.IsNullOrWhiteSpace(notice.Database) ||
                string.IsNullOrWhiteSpace(notice.Detail) ||
                IsUnsafeRelativePath(notice.Database))
            {
                throw new InvalidDataException("Parser notice is invalid.");
            }
        }

        return uniqueMessages.Count == result.Messages.Count
            ? result
            : result with { Messages = uniqueMessages };
    }

    private static void ValidateMedia(ParsedMessage message)
    {
        if (string.IsNullOrEmpty(message.MediaData))
        {
            if (!string.IsNullOrEmpty(message.MediaSha256))
                throw new InvalidDataException("Media hash exists without media data.");
            return;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(message.MediaData);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Embedded media is not valid base64.", exception);
        }
        try
        {
            if (decoded.Length > MaximumMediaBytes)
                throw new InvalidDataException("Embedded media exceeds 5 MiB.");
            if (!IsSha256(message.MediaSha256))
                throw new InvalidDataException("Embedded media SHA-256 is invalid.");
            var digest = SHA256.HashData(decoded);
            try
            {
                if (!string.Equals(
                        Convert.ToHexString(digest),
                        message.MediaSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Embedded media SHA-256 does not match its bytes.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static void ValidateStringLengths(JsonElement element, string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    ValidateStringLengths(property.Value, property.Name);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ValidateStringLengths(item, propertyName);
                break;
            case JsonValueKind.String:
                if (!string.Equals(propertyName, "media_data", StringComparison.Ordinal) &&
                    (element.GetString()?.Length ?? 0) > MaximumStringCharacters)
                {
                    throw new InvalidDataException("Parser result string exceeds 64 KiB.");
                }
                break;
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsUnsafeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith('/') ||
            normalized.StartsWith('\\') ||
            normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' ||
            normalized.Split('/').Any(part => part is "" or "." or "..");
    }
}

internal static class ParserItemIdentity
{
    internal static string Message(ParsedMessage message) => HashTuple(
        "desktop-pet-datasync-message-v2",
        message.Wxid,
        message.LocalId,
        message.CreateTime,
        message.IsSender,
        message.Sender,
        message.Content,
        message.MessageType,
        message.MessageSubType,
        message.MediaSha256);

    internal static string Contact(ParsedContact contact) => HashTuple(
        "desktop-pet-datasync-contact-v3",
        contact.Wxid,
        contact.Alias,
        contact.Remark,
        contact.NickName,
        contact.DisplayName,
        contact.Avatar,
        contact.SourceUpdatedAt,
        contact.ExtraJson is null ? null : CanonicalJson(contact.ExtraJson.Value));

    internal static string Favorite(ParsedFavorite favorite) => HashTuple(
        "desktop-pet-datasync-favorite-v3",
        favorite.SourceTable,
        favorite.SourceId,
        favorite.Title,
        favorite.Summary,
        favorite.ItemType,
        favorite.ItemSubType,
        favorite.SourceUpdatedAt,
        favorite.DataJson
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new object[] { item.Key, CanonicalJson(item.Value) })
            .ToArray());

    internal static string Batch(string endpoint, IReadOnlyList<string> itemIdentities) =>
        HashTuple(
            "desktop-pet-datasync-outbox-v2",
            endpoint,
            itemIdentities);

    private static string HashTuple(string version, params object?[] fields)
    {
        var tuple = new object?[fields.Length + 1];
        tuple[0] = version;
        fields.CopyTo(tuple, 1);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tuple);
        var digest = SHA256.HashData(bytes);
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static string CanonicalJson(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonicalJson(writer, value);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(
                             item => item.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
