using System.Security.Cryptography;
using System.Text;
using Footprint.Parser;
using Microsoft.Data.Sqlite;

namespace Footprint.Archive;

public static class ArchiveVerifier
{
    private static readonly string[] RequiredTables =
    [
        "archive_metadata", "contacts", "favorites", "media", "messages", "messages_fts",
        "messages_fts_config", "messages_fts_content", "messages_fts_data", "messages_fts_docsize",
        "messages_fts_idx", "sessions"
    ];

    public static string ComputeContentSha256(ParsedPackage package, IReadOnlyList<ArchiveMediaInput> media,
        string deviceId)
    {
        var mediaByMessage = media.ToDictionary(value => value.MessageId, StringComparer.Ordinal);
        var rows = new List<CanonicalRow>();
        rows.AddRange(package.Contacts.Select(value => Row("contact", value.Id, value.SourceId, value.DisplayName, value.Alias)));
        rows.AddRange(package.Sessions.Select(value => Row("session", value.Id, value.SourceId, value.Title,
            value.LastMessageUtc.ToUniversalTime().ToString("O"))));
        rows.AddRange(package.Messages.Select(value => Row("message", value.Id, value.SourceId, value.SessionId,
            value.SenderId, value.SentAtUtc.ToUniversalTime().ToString("O"), value.Kind, value.Body,
            mediaByMessage.TryGetValue(value.Id, out var linked) ? linked.Id : null)));
        rows.AddRange(package.Favorites.Select(value => Row("favorite", value.Id, value.SourceId, value.Kind,
            value.Title, value.Url, value.CreatedAtUtc.ToUniversalTime().ToString("O"))));
        rows.AddRange(media.Select(value => Row("media", value.Id, value.MessageId, value.Kind, value.Sha256,
            NormalizeFormat(value.Format), "media/" + value.Id + "." + NormalizeFormat(value.Format))));
        return Hash(package.SourceId, deviceId, rows);
    }

    public static async Task ValidateDatabaseAsync(string databasePath, ArchiveManifest manifest,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var queryOnly = connection.CreateCommand())
        {
            queryOnly.CommandText = "PRAGMA query_only=ON;";
            await queryOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)),
                    "ok", StringComparison.Ordinal)) throw new InvalidDataException("档案数据库完整性检查失败。");
        }
        await using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("档案数据库外键无效。");
        }

        var tables = await ReadStringsAsync(connection,
            "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;",
            cancellationToken).ConfigureAwait(false);
        if (!tables.SequenceEqual(RequiredTables, StringComparer.Ordinal))
            throw new InvalidDataException("档案数据库结构无效。");

        var metadata = await ReadPairsAsync(connection,
            "SELECT key,value FROM archive_metadata ORDER BY key;", cancellationToken).ConfigureAwait(false);
        if (metadata.Count != 3 || !metadata.TryGetValue("schema", out var schema) || schema != "footprint.archive.v1" ||
            !metadata.TryGetValue("source_id", out var sourceId) || sourceId != manifest.SourceId ||
            !metadata.TryGetValue("device_id", out var deviceId) || deviceId != manifest.DeviceId)
            throw new InvalidDataException("档案数据库元数据无效。");

        var rows = new List<CanonicalRow>();
        await ReadRowsAsync(connection, "SELECT id,source_id,display_name,alias FROM contacts ORDER BY id;", "contact", 4,
            rows, cancellationToken).ConfigureAwait(false);
        await ReadRowsAsync(connection, "SELECT id,source_id,title,last_message_utc FROM sessions ORDER BY id;", "session", 4,
            rows, cancellationToken).ConfigureAwait(false);
        await ReadRowsAsync(connection, "SELECT id,source_id,session_id,sender_id,sent_at_utc,kind,body,media_id FROM messages ORDER BY id;", "message", 8,
            rows, cancellationToken).ConfigureAwait(false);
        await ReadRowsAsync(connection, "SELECT id,source_id,kind,title,url,created_at_utc FROM favorites ORDER BY id;", "favorite", 6,
            rows, cancellationToken).ConfigureAwait(false);
        await ReadRowsAsync(connection, "SELECT id,message_id,kind,sha256,format,relative_path FROM media ORDER BY id;", "media", 6,
            rows, cancellationToken).ConfigureAwait(false);

        var contacts = rows.Where(value => value.Kind == "contact").Select(value => Required(value, 0)).ToHashSet(StringComparer.Ordinal);
        var sessions = rows.Where(value => value.Kind == "session").Select(value => Required(value, 0)).ToHashSet(StringComparer.Ordinal);
        var messages = rows.Where(value => value.Kind == "message").ToDictionary(value => Required(value, 0), StringComparer.Ordinal);
        var media = rows.Where(value => value.Kind == "media").ToArray();
        if (messages.Values.Any(value => !sessions.Contains(Required(value, 2)) || !contacts.Contains(Required(value, 3))))
            throw new InvalidDataException("档案消息实体引用无效。");
        foreach (var item in media)
        {
            if (!messages.TryGetValue(Required(item, 1), out var message) || message.Fields[7] != item.Fields[0])
                throw new InvalidDataException("档案媒体实体引用无效。");
        }
        if (messages.Values.Any(value => value.Fields[7] is not null && media.All(item => item.Fields[0] != value.Fields[7])))
            throw new InvalidDataException("档案消息媒体引用无效。");

        var fts = await ReadPairsAsync(connection,
            "SELECT message_id,body FROM messages_fts ORDER BY message_id;", cancellationToken).ConfigureAwait(false);
        if (fts.Count != messages.Count || messages.Any(value => !fts.TryGetValue(value.Key, out var body) || body != value.Value.Fields[6]))
            throw new InvalidDataException("档案 FTS 内容无效。");

        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["contact"] = manifest.ContactCount, ["session"] = manifest.SessionCount,
            ["message"] = manifest.MessageCount, ["favorite"] = manifest.FavoriteCount,
            ["media"] = manifest.MediaCount
        };
        if (counts.Any(value => rows.Count(row => row.Kind == value.Key) != value.Value))
            throw new InvalidDataException("档案数据库计数不匹配。");
        if (manifest.Media is null || manifest.Media.Count != media.Length) throw new InvalidDataException("档案媒体清单无效。");
        var mediaById = media.ToDictionary(value => Required(value, 0), StringComparer.Ordinal);
        var manifestMedia = manifest.Media.OrderBy(value => value.Id, StringComparer.Ordinal).Select(value =>
        {
            if (!mediaById.TryGetValue(value.Id, out var row)) throw new InvalidDataException("档案媒体清单与数据库不匹配。");
            return Row("media", value.Id, row.Fields[1], row.Fields[2], value.Sha256, value.Format, value.RelativePath);
        }).ToArray();
        var orderedMedia = media.OrderBy(value => Required(value, 0), StringComparer.Ordinal).ToArray();
        if (manifestMedia.Length != orderedMedia.Length || manifestMedia.Where((value, index) =>
                value.Kind != orderedMedia[index].Kind || !value.Fields.SequenceEqual(orderedMedia[index].Fields, StringComparer.Ordinal)).Any())
            throw new InvalidDataException("档案媒体清单与数据库不匹配。");
        if (Hash(sourceId, deviceId, rows) != manifest.ContentSha256) throw new InvalidDataException("档案语义摘要不匹配。");
    }

    public static string ResolveRegularFile(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("档案路径逃逸。");
        var current = new FileInfo(path).Directory;
        while (current is not null)
        {
            current.Refresh();
            if (current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("档案路径包含链接。");
            if (string.Equals(current.FullName, fullRoot, comparison)) break;
            current = current.Parent;
        }
        var file = new FileInfo(path); file.Refresh();
        if (!file.Exists || file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("档案文件无效。");
        return path;
    }

    private static async Task ReadRowsAsync(SqliteConnection connection, string sql, string kind, int fieldCount,
        ICollection<CanonicalRow> rows, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fields = new string?[fieldCount];
            for (var index = 0; index < fieldCount; index++) fields[index] = reader.IsDBNull(index) ? null : reader.GetString(index);
            rows.Add(new CanonicalRow(kind, fields));
        }
    }

    private static async Task<string[]> ReadStringsAsync(SqliteConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static async Task<Dictionary<string, string>> ReadPairsAsync(SqliteConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (!values.TryAdd(reader.GetString(0), reader.GetString(1))) throw new InvalidDataException("档案数据库键重复。");
        return values;
    }

    private static CanonicalRow Row(string kind, params string?[] fields) => new(kind, fields);

    private static string Required(CanonicalRow row, int index) => row.Fields[index]
        ?? throw new InvalidDataException("档案数据库必填字段为空。");

    private static string Hash(string sourceId, string deviceId, IEnumerable<CanonicalRow> rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "footprint.archive.content.v1"); Append(hash, sourceId); Append(hash, deviceId);
        foreach (var row in rows.OrderBy(value => KindOrder(value.Kind)).ThenBy(value => value.Fields[0], StringComparer.Ordinal))
        {
            Append(hash, row.Kind);
            foreach (var field in row.Fields) Append(hash, field);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null) { hash.AppendData([0xff, 0xff, 0xff, 0xff]); return; }
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length); hash.AppendData(bytes);
    }

    private static int KindOrder(string kind) => kind switch
    {
        "contact" => 0, "session" => 1, "message" => 2, "favorite" => 3, "media" => 4,
        _ => throw new InvalidDataException("档案实体类型无效。")
    };

    private static string NormalizeFormat(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new InvalidDataException("档案媒体格式无效。");
        return value.ToLowerInvariant();
    }

    private sealed record CanonicalRow(string Kind, IReadOnlyList<string?> Fields);
}
