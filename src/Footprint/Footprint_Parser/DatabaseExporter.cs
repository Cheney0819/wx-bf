using Microsoft.Data.Sqlite;

namespace Footprint.Parser;

public sealed record DatabaseExportOptions
{
    public DatabaseExportOptions(int pageSize, string scratchRoot)
    {
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize), "分页大小必须在 1 到 1000 之间。");
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        PageSize = pageSize;
        ScratchRoot = Path.GetFullPath(scratchRoot);
    }

    public int PageSize { get; }
    public string ScratchRoot { get; }
}

public sealed record ExportPage(string Entity, long LastKey, int RowCount);

public sealed record DatabaseSourceFile(string SourcePath, string FileName, long Length, string Sha256);

public sealed record DatabaseSourceGroup
{
    public DatabaseSourceGroup(string stableNamespace, string databaseIdentity,
        IReadOnlyList<DatabaseSourceFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) throw new ArgumentException("数据库清单组不能为空。", nameof(files));
        StableNamespace = stableNamespace;
        DatabaseIdentity = databaseIdentity;
        Files = files;
    }
    public string StableNamespace { get; }
    public string DatabaseIdentity { get; }
    public IReadOnlyList<DatabaseSourceFile> Files { get; }
}

public sealed class DatabaseExporter(DatabaseExportOptions options, Action<ExportPage>? pageObserved = null)
{
    public async Task<ParsedPackage> ExportAsync(string sourceDatabasePath, string sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabasePath);
        var source = Path.GetFullPath(sourceDatabasePath);
        ValidateRegularSource(source);
        var files = new List<DatabaseSourceFile>();
        foreach (var candidate in new[] { source, source + "-wal", source + "-shm" }.Where(File.Exists))
        {
            var info = new FileInfo(candidate);
            files.Add(new DatabaseSourceFile(candidate, info.Name, info.Length,
                await HashFileAsync(candidate, cancellationToken).ConfigureAwait(false)));
        }
        var databaseIdentity = DeterministicIdentity.Create("database", source);
        return await ExportAsync(new DatabaseSourceGroup(sourceId, databaseIdentity, files), sourceId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ParsedPackage> ExportAsync(DatabaseSourceGroup sourceGroup, string sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceGroup);
        ValidateSourceId(sourceId);
        var primary = ValidateGroup(sourceGroup);
        Directory.CreateDirectory(options.ScratchRoot);
        RejectLink(new DirectoryInfo(options.ScratchRoot));
        var workspace = Path.Combine(options.ScratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            foreach (var file in sourceGroup.Files)
                await CopyVerifiedAsync(file, Path.Combine(workspace, file.FileName), cancellationToken).ConfigureAwait(false);
            var copy = Path.Combine(workspace, primary.FileName);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = copy,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var tables = await ReadKnownTablesAsync(connection, cancellationToken).ConfigureAwait(false);
            var stableNamespace = sourceGroup.StableNamespace;
            var contacts = tables.Contains("contacts")
                ? await ReadContactsAsync(connection, stableNamespace, cancellationToken).ConfigureAwait(false)
                : tables.Contains("contact")
                    ? await (await HasColumnAsync(connection, "contact", "nick_name", cancellationToken).ConfigureAwait(false)
                        ? ReadWeixinContactsAsync(connection, stableNamespace, cancellationToken)
                        : ReadClassicWeixinContactsAsync(connection, stableNamespace, cancellationToken)).ConfigureAwait(false) : [];
            var sessions = tables.Contains("sessions")
                ? await ReadSessionsAsync(connection, stableNamespace, cancellationToken).ConfigureAwait(false)
                : tables.Contains("SessionTable")
                    ? await ReadWeixinSessionsAsync(connection, stableNamespace, cancellationToken).ConfigureAwait(false)
                    : tables.Contains("Session") ? await ReadClassicWeixinSessionsAsync(connection, stableNamespace, cancellationToken).ConfigureAwait(false) : [];
            var messages = tables.Contains("messages")
                ? await ReadMessagesAsync(connection, stableNamespace, cancellationToken).ConfigureAwait(false)
                : tables.Contains("message")
                    ? await ReadWeixinMessagesAsync(connection, stableNamespace, sourceGroup.DatabaseIdentity,
                        cancellationToken).ConfigureAwait(false)
                    : tables.Contains("MSG") ? await ReadClassicWeixinMessagesAsync(connection, stableNamespace,
                        sourceGroup.DatabaseIdentity, cancellationToken).ConfigureAwait(false) : [];
            var favorites = tables.Contains("favorites") ? await ReadFavoritesAsync(connection, stableNamespace, cancellationToken).ConfigureAwait(false) : [];
            var media = messages.Where(message => message.MediaRelativePath is not null && message.MediaSha256 is not null)
                .Select(message => new MediaRecord(
                    DeterministicIdentity.Create("media", stableNamespace, message.SourceId, message.MediaRelativePath!),
                    message.Id, message.Kind, message.MediaRelativePath!, message.MediaSha256!))
                .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            return new ParsedPackage(sourceId, contacts, sessions, messages, favorites, media);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    private async Task<IReadOnlyList<ContactRecord>> ReadContactsAsync(SqliteConnection connection, string sourceId,
        CancellationToken cancellationToken) => await ReadPagesAsync(connection, "contacts",
        "source_id, display_name, alias", (reader, _) => new ContactRecord(
            DeterministicIdentity.Create("contact", sourceId, reader.GetString(1)), reader.GetString(1),
            reader.GetString(2), reader.GetString(3)), cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<SessionRecord>> ReadSessionsAsync(SqliteConnection connection, string sourceId,
        CancellationToken cancellationToken) => await ReadPagesAsync(connection, "sessions",
        "source_id, title, last_message_utc", (reader, _) => new SessionRecord(
            DeterministicIdentity.Create("session", sourceId, reader.GetString(1)), reader.GetString(1),
            reader.GetString(2), ParseUtc(reader.GetString(3))), cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<MessageRecord>> ReadMessagesAsync(SqliteConnection connection, string sourceId,
        CancellationToken cancellationToken) => await ReadPagesAsync(connection, "messages",
        "source_id, session_source_id, sender_source_id, sent_at_utc, kind, body, media_relative_path, media_sha256",
        (reader, _) =>
        {
            var sourceMessage = reader.GetString(1);
            var path = reader.IsDBNull(7) ? null : NormalizeRelativePath(reader.GetString(7));
            var hash = reader.IsDBNull(8) ? null : ValidateSha256(reader.GetString(8));
            if ((path is null) != (hash is null)) throw new InvalidDataException("媒体路径和摘要必须同时存在。");
            return new MessageRecord(DeterministicIdentity.Create("message", sourceId, sourceMessage), sourceMessage,
                DeterministicIdentity.Create("session", sourceId, reader.GetString(2)),
                DeterministicIdentity.Create("contact", sourceId, reader.GetString(3)), ParseUtc(reader.GetString(4)),
                reader.GetString(5), reader.GetString(6), path, hash);
        }, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<FavoriteRecord>> ReadFavoritesAsync(SqliteConnection connection, string sourceId,
        CancellationToken cancellationToken) => await ReadPagesAsync(connection, "favorites",
        "source_id, kind, title, url, created_at_utc", (reader, _) => new FavoriteRecord(
            DeterministicIdentity.Create("favorite", sourceId, reader.GetString(1)), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            ParseUtc(reader.GetString(5))), cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<ContactRecord>> ReadWeixinContactsAsync(SqliteConnection connection,
        string sourceId, CancellationToken cancellationToken) => await ReadPagesAsync(connection, "contact",
        "username,nick_name,remark,alias", (reader, _) =>
        {
            var username = reader.GetString(1);
            var nickname = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var remark = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var alias = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            return new ContactRecord(DeterministicIdentity.Create("contact", sourceId, username), username,
                string.IsNullOrWhiteSpace(remark) ? string.IsNullOrWhiteSpace(nickname) ? username : nickname : remark,
                alias);
        }, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<SessionRecord>> ReadWeixinSessionsAsync(SqliteConnection connection,
        string sourceId, CancellationToken cancellationToken) => await ReadPagesAsync(connection, "SessionTable",
        "username,summary,last_timestamp", (reader, _) =>
        {
            var username = reader.GetString(1);
            var title = reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2)) ? username : reader.GetString(2);
            return new SessionRecord(DeterministicIdentity.Create("session", sourceId, username), username, title,
                ParseUnixTimestamp(reader.GetInt64(3)));
        }, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<MessageRecord>> ReadWeixinMessagesAsync(SqliteConnection connection,
        string sourceId, string databaseIdentity, CancellationToken cancellationToken)
    {
        var mediaColumns = await ResolveMediaColumnsAsync(connection, "message", cancellationToken).ConfigureAwait(false);
        return await ReadPagesAsync(connection,
        "message", "server_id,local_id,talker,sender,create_time,local_type,message_content," + mediaColumns, (reader, rowId) =>
        {
            var serverId = reader.GetInt64(1);
            var localId = reader.GetInt64(2);
            var sourceMessage = (serverId != 0 ? serverId : localId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var talker = reader.GetString(3);
            var sender = reader.IsDBNull(4) || string.IsNullOrWhiteSpace(reader.GetString(4)) ? talker : reader.GetString(4);
            var kind = reader.GetInt64(6) switch { 1 => "text", 3 => "image", 34 => "voice", 43 => "video", 49 => "file", _ => "unknown" };
            var body = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            var messageId = serverId != 0
                ? DeterministicIdentity.Create("message", sourceId, sourceMessage)
                : DeterministicIdentity.Create("message", sourceId, sourceMessage, databaseIdentity, "message",
                    rowId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var path = reader.IsDBNull(8) ? null : NormalizeRelativePath(reader.GetString(8));
            var hash = reader.IsDBNull(9) ? null : ValidateSha256(reader.GetString(9));
            if ((path is null) != (hash is null)) throw new InvalidDataException("媒体路径和摘要必须同时存在。");
            return new MessageRecord(messageId, sourceMessage,
                DeterministicIdentity.Create("session", sourceId, talker),
                DeterministicIdentity.Create("contact", sourceId, sender), ParseUnixTimestamp(reader.GetInt64(5)),
                kind, body, path, hash);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ContactRecord>> ReadClassicWeixinContactsAsync(SqliteConnection connection,
        string sourceId, CancellationToken cancellationToken) => await ReadPagesAsync(connection, "Contact",
        "UserName,NickName,Remark,Alias", (reader, _) =>
        {
            var username = reader.GetString(1);
            var nickname = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var remark = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            return new ContactRecord(DeterministicIdentity.Create("contact", sourceId, username), username,
                string.IsNullOrWhiteSpace(remark) ? string.IsNullOrWhiteSpace(nickname) ? username : nickname : remark,
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4));
        }, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<SessionRecord>> ReadClassicWeixinSessionsAsync(SqliteConnection connection,
        string sourceId, CancellationToken cancellationToken) => await ReadPagesAsync(connection, "Session",
        "strUsrName,strNickName,nTime", (reader, _) =>
        {
            var username = reader.GetString(1);
            var nickname = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            return new SessionRecord(DeterministicIdentity.Create("session", sourceId, username), username,
                string.IsNullOrWhiteSpace(nickname) ? username : nickname, ParseUnixTimestamp(reader.GetInt64(3)));
        }, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<MessageRecord>> ReadClassicWeixinMessagesAsync(SqliteConnection connection,
        string sourceId, string databaseIdentity, CancellationToken cancellationToken)
    {
        var mediaColumns = await ResolveMediaColumnsAsync(connection, "MSG", cancellationToken).ConfigureAwait(false);
        return await ReadPagesAsync(connection,
        "MSG", "MsgSvrID,localId,StrTalker,CreateTime,Type,StrContent," + mediaColumns, (reader, rowId) =>
        {
            var serverId = reader.GetInt64(1);
            var localId = reader.GetInt64(2);
            var sourceMessage = (serverId != 0 ? serverId : localId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var talker = reader.GetString(3);
            var kind = reader.GetInt64(5) switch { 1 => "text", 3 => "image", 34 => "voice", 43 => "video", 49 => "file", _ => "unknown" };
            var messageId = serverId != 0
                ? DeterministicIdentity.Create("message", sourceId, sourceMessage)
                : DeterministicIdentity.Create("message", sourceId, sourceMessage, databaseIdentity, "MSG",
                    rowId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var path = reader.IsDBNull(7) ? null : NormalizeRelativePath(reader.GetString(7));
            var hash = reader.IsDBNull(8) ? null : ValidateSha256(reader.GetString(8));
            if ((path is null) != (hash is null)) throw new InvalidDataException("媒体路径和摘要必须同时存在。");
            return new MessageRecord(messageId, sourceMessage,
                DeterministicIdentity.Create("session", sourceId, talker), DeterministicIdentity.Create("contact", sourceId, talker),
                ParseUnixTimestamp(reader.GetInt64(4)), kind, reader.IsDBNull(6) ? string.Empty : reader.GetString(6), path, hash);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ResolveMediaColumnsAsync(
        SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            columns[reader.GetString(0)] = reader.GetString(0);
        var path = FindColumn(columns, "media_relative_path", "mediaRelativePath", "media_path", "MediaPath");
        var sha = FindColumn(columns, "media_sha256", "mediaSha256", "sha256");
        if ((path is null) != (sha is null)) throw new InvalidDataException("媒体路径和摘要列必须同时存在。");
        return path is null
            ? "NULL AS media_relative_path,NULL AS media_sha256"
            : $"\"{path}\",\"{sha}\"";
    }

    private static string? FindColumn(IReadOnlyDictionary<string, string> columns, params string[] candidates)
    {
        foreach (var candidate in candidates)
            if (columns.TryGetValue(candidate, out var actual)) return actual;
        return null;
    }

    private async Task<IReadOnlyList<T>> ReadPagesAsync<T>(SqliteConnection connection, string table, string columns,
        Func<SqliteDataReader, long, T> map, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        long lastKey = 0;
        var hasLastKey = false;
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = hasLastKey
                ? $"SELECT rowid, {columns} FROM {table} WHERE rowid > $last ORDER BY rowid LIMIT $limit;"
                : $"SELECT rowid, {columns} FROM {table} ORDER BY rowid LIMIT $limit;";
            if (hasLastKey) command.Parameters.AddWithValue("$last", lastKey);
            command.Parameters.AddWithValue("$limit", options.PageSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var count = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lastKey = reader.GetInt64(0);
                hasLastKey = true;
                result.Add(map(reader, lastKey));
                count++;
            }
            if (count == 0) break;
            pageObserved?.Invoke(new ExportPage(table, lastKey, count));
            if (count < options.PageSize) break;
        }
        return result;
    }

    private static async Task<HashSet<string>> ReadKnownTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        string[] known = ["contacts", "sessions", "messages", "favorites", "contact", "SessionTable", "message", "Session", "MSG"];
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) actual.Add(reader.GetString(0));
        actual.IntersectWith(known);
        return actual;
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, string table, string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$column COLLATE NOCASE;";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    private static DatabaseSourceFile ValidateGroup(DatabaseSourceGroup group)
    {
        if (group.StableNamespace.Length > 256) throw new InvalidDataException("数据库稳定命名空间无效。");
        ValidateSha256(group.DatabaseIdentity);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var primary = group.Files.Where(file => !file.FileName.EndsWith("-wal", StringComparison.Ordinal) &&
                                                 !file.FileName.EndsWith("-shm", StringComparison.Ordinal)).ToArray();
        if (primary.Length != 1) throw new InvalidDataException("数据库清单组必须包含一个主数据库。");
        var primaryDirectory = Path.GetDirectoryName(Path.GetFullPath(primary[0].SourcePath))!;
        foreach (var file in group.Files)
        {
            var full = Path.GetFullPath(file.SourcePath);
            if (Path.GetDirectoryName(full) != primaryDirectory || Path.GetFileName(full) != file.FileName ||
                !names.Add(file.FileName) || file.Length < 0) throw new InvalidDataException("数据库清单组文件无效。");
            ValidateSha256(file.Sha256);
        }
        var allowed = new HashSet<string>([primary[0].FileName, primary[0].FileName + "-wal", primary[0].FileName + "-shm"], StringComparer.Ordinal);
        if (names.Any(name => !allowed.Contains(name))) throw new InvalidDataException("数据库清单组包含无关文件。");
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(primary[0].SourcePath + suffix) && !names.Contains(primary[0].FileName + suffix))
                throw new InvalidDataException("数据库 sidecar 未由接收包清单声明。");
        return primary[0];
    }

    private static async Task CopyVerifiedAsync(DatabaseSourceFile source, string destination,
        CancellationToken cancellationToken)
    {
        ValidateRegularSource(source.SourcePath);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            await using var from = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (from.Length != source.Length) throw new InvalidDataException("数据库清单长度不匹配。");
            await using var to = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            long length = 0;
            while (true)
            {
                var read = await from.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                length = checked(length + read);
                hash.AppendData(buffer, 0, read);
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (length != source.Length || actual != source.Sha256) throw new InvalidDataException("数据库清单摘要不匹配。");
            await to.FlushAsync(cancellationToken).ConfigureAwait(false); to.Flush(true);
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, true); }
    }

    private static void ValidateRegularSource(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("本地内容数据库不存在。", path);
        var info = new FileInfo(path);
        info.Refresh();
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("本地内容数据库不能是链接。");
    }

    private static void RejectLink(FileSystemInfo info)
    {
        info.Refresh();
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("本地解析工作目录不能是链接。");
    }

    private static void ValidateSourceId(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (sourceId.Length > 128 || sourceId is "." or ".." || sourceId.Any(value => !(char.IsAsciiLetterOrDigit(value) || value is '-' or '_')))
            throw new InvalidDataException("来源标识无效。");
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed) || parsed.Offset != TimeSpan.Zero) throw new InvalidDataException("内容时间戳必须是 UTC。");
        return parsed;
    }

    private static DateTimeOffset ParseUnixTimestamp(long value)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(value).ToUniversalTime(); }
        catch (ArgumentOutOfRangeException exception) { throw new InvalidDataException("微信内容时间戳无效。", exception); }
    }

    private static string ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("媒体摘要必须为 lowercase SHA-256。");
        return value;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.StartsWith('/') || Path.IsPathRooted(value))
            throw new InvalidDataException("媒体相对路径无效。");
        var parts = value.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or "..")) throw new InvalidDataException("媒体相对路径无效。");
        return value;
    }
}
