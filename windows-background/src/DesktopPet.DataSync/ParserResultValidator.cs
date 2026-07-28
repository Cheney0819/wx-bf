using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.DataSync;

public sealed class ParserResultValidator
{
    private const long MaximumResultBytes = 32L * 1024 * 1024;
    private const int MaximumStringCharacters = 64 * 1024;
    private const int MaximumMediaBytes = 5 * 1024 * 1024;
    private static readonly IReadOnlySet<string> ResultMembers = new HashSet<string>(
        ["schemaVersion", "jobId", "sourceSetId", "messages", "contacts", "favorites", "notices"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> MessageMembers = new HashSet<string>(
        [
            "wxid", "local_id", "content", "create_time", "is_sender", "nickname", "sender",
            "avatar", "msg_type", "msg_sub_type", "media_type", "media_mime", "media_name",
            "media_data", "media_sha256",
        ],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ContactMembers = new HashSet<string>(
        [
            "wxid", "alias", "remark", "nick_name", "display_name", "avatar",
            "source_updated_at", "extra_json",
        ],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> FavoriteMembers = new HashSet<string>(
        [
            "source_table", "source_id", "title", "summary", "item_type", "item_sub_type",
            "source_updated_at", "data_json",
        ],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> NoticeMembers = new HashSet<string>(
        ["code", "database", "detail"],
        StringComparer.Ordinal);
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
            throw Failure("parser_result_too_large", "Parser result exceeds 32 MiB.");

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        try
        {
            ParserResultDocument result;
            try
            {
                using var json = JsonDocument.Parse(bytes);
                ValidateNoDuplicateMembers(json.RootElement);
                ValidateJsonContract(json.RootElement);
                ValidateStringLengths(json.RootElement, propertyName: null);
                result = JsonSerializer.Deserialize<ParserResultDocument>(bytes, JsonOptions) ??
                    throw Failure("parser_result_json_invalid", "Parser result is empty.");
            }
            catch (JsonException exception)
            {
                throw Failure(
                    "parser_result_json_invalid",
                    "Parser result JSON violates schema 1.",
                    exception);
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
            throw Failure("parser_result_schema_invalid", "Parser result schema is unsupported.");
        if (!string.Equals(result.JobId, expectedJobId, StringComparison.Ordinal) ||
            !string.Equals(result.SourceSetId, expectedSourceSetId, StringComparison.Ordinal))
        {
            throw Failure(
                "parser_result_identity_mismatch",
                "Parser result identity does not match its claimed job.");
        }
        if (result.Messages is null || result.Messages.Count > 5000 ||
            result.Contacts is null || result.Contacts.Count > 5000 ||
            result.Favorites is null || result.Favorites.Count > 1000 ||
            result.Notices is null || result.Notices.Count > 1000)
        {
            throw Failure("parser_result_count_invalid", "Parser result count limit was exceeded.");
        }
        if (result.NextCursor is not null &&
            (string.IsNullOrWhiteSpace(result.NextCursor) ||
             result.NextCursor.Length > MaximumStringCharacters ||
             result.NextCursor.Any(character =>
                 !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))))
        {
            throw Failure("parser_result_cursor_invalid", "Parser continuation cursor is invalid.");
        }

        var messageIdentities = new HashSet<string>(StringComparer.Ordinal);
        var uniqueMessages = new List<ParsedMessage>(result.Messages.Count);
        foreach (var message in result.Messages)
        {
            if (message is null ||
                string.IsNullOrWhiteSpace(message.Wxid) ||
                message.Content is null ||
                message.Nickname is null ||
                message.Sender is null ||
                message.Avatar is null ||
                message.MediaType is null ||
                message.MediaMime is null ||
                message.MediaName is null)
            {
                throw Failure("parser_result_message_invalid", "Parser message identity is empty.");
            }
            ValidateMedia(message);
            if (messageIdentities.Add(ParserItemIdentity.Message(message)))
                uniqueMessages.Add(message);
        }

        var contacts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contact in result.Contacts)
        {
            if (contact is null || string.IsNullOrWhiteSpace(contact.Wxid) ||
                contact.Alias is null || contact.Remark is null ||
                contact.NickName is null || contact.DisplayName is null ||
                contact.Avatar is null ||
                !contacts.Add(contact.Wxid))
            {
                throw Failure(
                    "parser_result_contact_invalid",
                    "Parser result contains an invalid or duplicate contact.");
            }
        }

        var favorites = new HashSet<(string SourceTable, string SourceId)>();
        foreach (var favorite in result.Favorites)
        {
            if (favorite is null ||
                string.IsNullOrWhiteSpace(favorite.SourceTable) ||
                string.IsNullOrWhiteSpace(favorite.SourceId) ||
                favorite.Title is null || favorite.Summary is null ||
                favorite.ItemType is null || favorite.ItemSubType is null ||
                favorite.DataJson is null ||
                !favorites.Add((favorite.SourceTable, favorite.SourceId)))
            {
                throw Failure(
                    "parser_result_favorite_invalid",
                    "Parser result contains an invalid or duplicate favorite.");
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
                throw Failure("parser_result_notice_invalid", "Parser notice is invalid.");
            }
        }

        return uniqueMessages.Count == result.Messages.Count
            ? result
            : result with { Messages = uniqueMessages };
    }

    private static void ValidateMedia(ParsedMessage message)
    {
        if (message.MediaData is null || message.MediaSha256 is null)
            throw Failure("parser_result_media_invalid", "Embedded media fields are null.");
        if (string.IsNullOrEmpty(message.MediaData))
        {
            if (!string.IsNullOrEmpty(message.MediaSha256))
                throw Failure("parser_result_media_invalid", "Media hash exists without media data.");
            return;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(message.MediaData);
        }
        catch (FormatException exception)
        {
            throw Failure(
                "parser_result_media_invalid",
                "Embedded media is not valid base64.",
                exception);
        }
        try
        {
            if (decoded.Length > MaximumMediaBytes)
                throw Failure("parser_result_media_invalid", "Embedded media exceeds 5 MiB.");
            if (!IsSha256(message.MediaSha256))
                throw Failure("parser_result_media_invalid", "Embedded media SHA-256 is invalid.");
            var digest = SHA256.HashData(decoded);
            try
            {
                if (!string.Equals(
                        Convert.ToHexString(digest),
                        message.MediaSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw Failure(
                        "parser_result_media_invalid",
                        "Embedded media SHA-256 does not match its bytes.");
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
                    throw Failure(
                        "parser_result_string_too_large",
                        "Parser result string exceeds 64 KiB.");
                }
                break;
        }
    }

    private static void ValidateNoDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw Failure("parser_result_json_invalid", "Parser result contains a duplicate member.");
                ValidateNoDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateNoDuplicateMembers(item);
        }
    }

    private static void ValidateJsonContract(JsonElement root)
    {
        ValidateObjectMembers(
            root,
            ResultMembers,
            "parser_result_json_invalid",
            optionalMember: "nextCursor");
        ValidateArrayMembers(
            root.GetProperty("messages"),
            MessageMembers,
            "parser_result_message_invalid");
        ValidateArrayMembers(
            root.GetProperty("contacts"),
            ContactMembers,
            "parser_result_contact_invalid");
        ValidateArrayMembers(
            root.GetProperty("favorites"),
            FavoriteMembers,
            "parser_result_favorite_invalid");
        ValidateArrayMembers(
            root.GetProperty("notices"),
            NoticeMembers,
            "parser_result_notice_invalid");
    }

    private static void ValidateArrayMembers(
        JsonElement array,
        IReadOnlySet<string> requiredMembers,
        string failureCode)
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw Failure(failureCode, "Parser result collection is invalid.");
        foreach (var item in array.EnumerateArray())
            ValidateObjectMembers(item, requiredMembers, failureCode);
    }

    private static void ValidateObjectMembers(
        JsonElement value,
        IReadOnlySet<string> requiredMembers,
        string failureCode,
        string? optionalMember = null)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Failure(failureCode, "Parser result object is invalid.");
        var actual = value.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!requiredMembers.IsSubsetOf(actual) ||
            actual.Any(member => !requiredMembers.Contains(member) && member != optionalMember))
        {
            throw Failure(failureCode, "Parser result object members are invalid.");
        }
    }

    private static InvalidDataException Failure(
        string code,
        string message,
        Exception? innerException = null)
    {
        var exception = innerException is null
            ? new InvalidDataException(message)
            : new InvalidDataException(message, innerException);
        exception.Data["failureCode"] = code;
        return exception;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

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
