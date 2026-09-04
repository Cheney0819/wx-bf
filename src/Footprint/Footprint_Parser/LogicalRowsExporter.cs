using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Footprint.Parser;

public sealed class LogicalRowsExporter
{
    private const int MaximumLineCharacters = 16 * 1024 * 1024;

    public async Task<ParsedPackage> ExportAsync(IReadOnlyList<string> logicalRowPaths,
        IReadOnlyCollection<string> allowedDatabaseIdentityHashes, string stableNamespace,
        string sourceId, CancellationToken cancellationToken = default)
        => await ExportAsync(logicalRowPaths, allowedDatabaseIdentityHashes, stableNamespace, sourceId,
            null, null, null, cancellationToken).ConfigureAwait(false);

    public async Task<ParsedPackage> ExportAsync(IReadOnlyList<string> logicalRowPaths,
        IReadOnlyCollection<string> allowedDatabaseIdentityHashes, string stableNamespace,
        string sourceId, string? tablePreviewPath, string? recordsRoot,
        IReadOnlyDictionary<string, string>? databaseIdentityByPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logicalRowPaths);
        ArgumentNullException.ThrowIfNull(allowedDatabaseIdentityHashes);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (logicalRowPaths.Count == 0 && string.IsNullOrWhiteSpace(tablePreviewPath))
            throw new ArgumentException("逻辑行或 table-preview 文件不能为空。", nameof(logicalRowPaths));
        var allowedIdentities = allowedDatabaseIdentityHashes.ToHashSet(StringComparer.Ordinal);
        if (allowedIdentities.Count == 0 || allowedIdentities.Any(value => !IsSha256(value)))
            throw new ArgumentException("逻辑行数据库身份允许列表无效。", nameof(allowedDatabaseIdentityHashes));

        var contacts = new List<ContactRecord>();
        var sessions = new List<SessionRecord>();
        var messages = new List<MessageRecord>();
        foreach (var path in logicalRowPaths.Order(StringComparer.Ordinal))
        {
            ValidateRegularFile(path);
            await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
                FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 128 * 1024,
                leaveOpen: false);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length == 0) continue;
                if (line.Length > MaximumLineCharacters) throw new InvalidDataException("逻辑行记录过大。");
                using var document = ParseLine(line);
                ParseRecord(document.RootElement, allowedIdentities, stableNamespace, contacts, sessions, messages);
            }
        }

        if (messages.Count == 0 && tablePreviewPath is not null && recordsRoot is not null)
        {
            var decompressed = await DecompressedMessageRecords.ReadAsync(tablePreviewPath, recordsRoot,
                databaseIdentityByPath, cancellationToken).ConfigureAwait(false);
            var decompressedSessions = new Dictionary<string, SessionRecord>(StringComparer.Ordinal);
            var decompressedContacts = new Dictionary<string, ContactRecord>(StringComparer.Ordinal);
            var sessionByMessageTable = sessions.ToDictionary(
                value => WeixinMessageTable(value.SourceId), value => value, StringComparer.Ordinal);
            var contactsBySource = contacts
                .GroupBy(value => value.SourceId, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => value.First(), StringComparer.Ordinal);
            foreach (var item in decompressed.Messages)
            {
                if (item.DatabaseIdentityHash is null || !allowedIdentities.Contains(item.DatabaseIdentityHash))
                    throw new InvalidDataException("解压消息数据库身份不在接收包允许列表中。");
                var sentAt = ParseUnixTimestamp(item.CreateTimeUnixSeconds);
                string sessionId;
                string fallbackContactSource;
                if (sessionByMessageTable.TryGetValue(item.Table, out var logicalSession))
                {
                    sessionId = logicalSession.Id;
                    fallbackContactSource = logicalSession.SourceId;
                }
                else
                {
                    var sessionSource = "decompressed-session:" + item.DatabasePath + ":" + item.Table;
                    sessionId = DeterministicIdentity.Create("session", stableNamespace, sessionSource);
                    if (!decompressedSessions.TryGetValue(sessionId, out var existingSession) ||
                        sentAt > existingSession.LastMessageUtc)
                        decompressedSessions[sessionId] = new SessionRecord(sessionId, sessionSource, item.Table, sentAt);
                    fallbackContactSource = sessionSource;
                }
                var senderSource = ExtractSenderSource(item.Body, contactsBySource) ?? fallbackContactSource;
                var senderId = EnsureFallbackContact(senderSource, stableNamespace, contactsBySource,
                    decompressedContacts, fallbackContactSource);
                var messageSource = item.ServerId is not "0" and not ""
                    ? item.ServerId
                    : item.DatabaseIdentityHash + ":" + item.Table + ":" + item.LocalId;
                messages.Add(new MessageRecord(
                    DeterministicIdentity.Create("message", stableNamespace, messageSource), messageSource,
                    sessionId, senderId, sentAt, item.Kind, item.Body, null, null));
            }
            sessions.AddRange(decompressedSessions.Values);
            contacts.AddRange(decompressedContacts.Values);
        }

        var uniqueContacts = Unique(contacts, value => value.Id, "contact");
        var contactsBySourceForSessions = uniqueContacts
            .GroupBy(value => value.SourceId, StringComparer.Ordinal)
            .ToDictionary(value => value.Key, value => value.First(), StringComparer.Ordinal);
        var uniqueSessions = Unique(sessions, value => value.Id, "session")
            .Select(value => contactsBySourceForSessions.TryGetValue(value.SourceId, out var contact) &&
                             !string.Equals(contact.DisplayName, contact.SourceId, StringComparison.Ordinal)
                ? value with { Title = contact.DisplayName }
                : value)
            .ToArray();
        return new ParsedPackage(sourceId, uniqueContacts, uniqueSessions,
            Unique(messages, value => value.Id, "message"), [], []);
    }

    private static void ParseRecord(JsonElement record, IReadOnlySet<string> allowedDatabaseIdentities,
        string stableNamespace, List<ContactRecord> contacts, List<SessionRecord> sessions,
        List<MessageRecord> messages)
    {
        if (record.ValueKind != JsonValueKind.Object ||
            !TryRequiredString(record, "database_path", out _) ||
            !TryRequiredString(record, "database_identity_hash", out var databaseIdentity) ||
            !IsSha256(databaseIdentity) || !allowedDatabaseIdentities.Contains(databaseIdentity) ||
            !record.TryGetProperty("tag", out var tag) || !tag.TryGetInt32(out _) ||
            !TryRequiredString(record, "table", out var table) ||
            !TryRequiredString(record, "row_identity", out var rowIdentity) ||
            !record.TryGetProperty("row", out var row) || row.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("逻辑行契约无效。");

        if (table.Equals("contact", StringComparison.Ordinal) && row.TryGetProperty("username", out _))
        {
            var username = RequiredString(row, "username");
            var nickname = OptionalString(row, "nick_name");
            var remark = OptionalString(row, "remark");
            contacts.Add(new ContactRecord(DeterministicIdentity.Create("contact", stableNamespace, username),
                username, FirstNonBlank(remark, nickname, username), OptionalString(row, "alias")));
        }
        else if (table.Equals("Contact", StringComparison.Ordinal))
        {
            var username = RequiredString(row, "UserName");
            var nickname = OptionalString(row, "NickName");
            var remark = OptionalString(row, "Remark");
            contacts.Add(new ContactRecord(DeterministicIdentity.Create("contact", stableNamespace, username),
                username, FirstNonBlank(remark, nickname, username), OptionalString(row, "Alias")));
        }
        else if (table.Equals("SessionTable", StringComparison.OrdinalIgnoreCase))
        {
            var username = RequiredString(row, "username");
            sessions.Add(new SessionRecord(DeterministicIdentity.Create("session", stableNamespace, username),
                username, FirstNonBlank(OptionalString(row, "summary"), username),
                ParseUnixTimestamp(RequiredInt64(row, "last_timestamp"))));
        }
        else if (table.Equals("Session", StringComparison.Ordinal))
        {
            var username = RequiredString(row, "strUsrName");
            sessions.Add(new SessionRecord(DeterministicIdentity.Create("session", stableNamespace, username),
                username, FirstNonBlank(OptionalString(row, "strNickName"), username),
                ParseUnixTimestamp(RequiredInt64(row, "nTime"))));
        }
        else if (table.Equals("message", StringComparison.OrdinalIgnoreCase))
        {
            var serverId = RequiredInt64(row, "server_id");
            var localId = RequiredInt64(row, "local_id");
            var sourceMessage = (serverId != 0 ? serverId : localId).ToString(CultureInfo.InvariantCulture);
            var talker = RequiredString(row, "talker");
            var sender = FirstNonBlank(OptionalString(row, "sender"), talker);
            var messageId = serverId != 0
                ? DeterministicIdentity.Create("message", stableNamespace, sourceMessage)
                : DeterministicIdentity.Create("message", stableNamespace, sourceMessage, databaseIdentity, table,
                    rowIdentity);
            messages.Add(new MessageRecord(messageId,
                sourceMessage, DeterministicIdentity.Create("session", stableNamespace, talker),
                DeterministicIdentity.Create("contact", stableNamespace, sender),
                ParseUnixTimestamp(RequiredInt64(row, "create_time")),
                MapMessageKind(RequiredInt64(row, "local_type")), OptionalString(row, "message_content"), null, null));
        }
        else if (table.Equals("MSG", StringComparison.Ordinal))
        {
            var serverId = RequiredInt64(row, "MsgSvrID");
            var localId = RequiredInt64(row, "localId");
            var sourceMessage = (serverId != 0 ? serverId : localId).ToString(CultureInfo.InvariantCulture);
            var talker = RequiredString(row, "StrTalker");
            var messageId = serverId != 0
                ? DeterministicIdentity.Create("message", stableNamespace, sourceMessage)
                : DeterministicIdentity.Create("message", stableNamespace, sourceMessage, databaseIdentity, table,
                    rowIdentity);
            messages.Add(new MessageRecord(messageId,
                sourceMessage, DeterministicIdentity.Create("session", stableNamespace, talker),
                DeterministicIdentity.Create("contact", stableNamespace, talker),
                ParseUnixTimestamp(RequiredInt64(row, "CreateTime")), MapMessageKind(RequiredInt64(row, "Type")),
                OptionalString(row, "StrContent"), null, null));
        }
    }

    private static JsonDocument ParseLine(string line)
    {
        try { return JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException exception) { throw new InvalidDataException("逻辑行 JSON 无效。", exception); }
    }

    private static IReadOnlyList<T> Unique<T>(IEnumerable<T> values, Func<T, string> identity, string entityKind)
    {
        var result = new List<T>();
        foreach (var group in values.GroupBy(identity, StringComparer.Ordinal).OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var distinct = group.Distinct().ToArray();
            if (distinct.Length != 1) throw new InvalidDataException($"逻辑行产生冲突的实体记录（{entityKind}，标识 {group.Key}）。");
            result.Add(distinct[0]);
        }
        return result;
    }

    private static string RequiredString(JsonElement value, string property)
    {
        if (!TryRequiredString(value, property, out var result))
            throw new InvalidDataException($"逻辑行缺少字段 {property}。");
        return result;
    }

    private static bool TryRequiredString(JsonElement value, string property, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        result = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(result) && result.Length <= MaximumLineCharacters;
    }

    private static string OptionalString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null) return string.Empty;
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException($"逻辑行字段 {property} 类型无效。");
        var result = element.GetString() ?? string.Empty;
        if (result.Length > MaximumLineCharacters) throw new InvalidDataException($"逻辑行字段 {property} 过大。");
        return result;
    }

    private static long RequiredInt64(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var element)) throw new InvalidDataException($"逻辑行缺少字段 {property}。");
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number)) return number;
        if (element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out number)) return number;
        throw new InvalidDataException($"逻辑行字段 {property} 类型无效。");
    }

    private static DateTimeOffset ParseUnixTimestamp(long value)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(value).ToUniversalTime(); }
        catch (ArgumentOutOfRangeException exception) { throw new InvalidDataException("微信内容时间戳无效。", exception); }
    }

    private static string MapMessageKind(long value) => value switch
    {
        1 => "text", 3 => "image", 34 => "voice", 43 => "video", 49 => "file", _ => "unknown"
    };

    private static string WeixinMessageTable(string sessionSourceId) => "Msg_" +
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sessionSourceId))).ToLowerInvariant();

    private static string? ExtractSenderSource(string body,
        IReadOnlyDictionary<string, ContactRecord> contactsBySource)
    {
        var newline = body.IndexOf('\n');
        if (newline <= 0) return null;
        var prefix = body[..newline].Trim();
        if (prefix.EndsWith(':')) prefix = prefix[..^1].Trim();
        return contactsBySource.ContainsKey(prefix) ? prefix : null;
    }

    private static string EnsureFallbackContact(string sourceId, string stableNamespace,
        IReadOnlyDictionary<string, ContactRecord> contactsBySource,
        IDictionary<string, ContactRecord> decompressedContacts, string fallbackDisplaySource)
    {
        if (contactsBySource.TryGetValue(sourceId, out var known)) return known.Id;
        var id = DeterministicIdentity.Create("contact", stableNamespace, sourceId);
        if (!decompressedContacts.ContainsKey(id))
            decompressedContacts[id] = new ContactRecord(id, sourceId,
                sourceId == fallbackDisplaySource ? fallbackDisplaySource : sourceId, string.Empty);
        return id;
    }

    private static string FirstNonBlank(params string[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value));

    private static bool IsSha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateRegularFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(Path.GetFullPath(path));
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("逻辑行输入必须是普通文件。");
    }
}
