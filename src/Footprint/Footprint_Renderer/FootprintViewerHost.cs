using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Footprint.Archive;
using Microsoft.Data.Sqlite;

namespace Footprint.Renderer;

public sealed record ViewerRequest(string Method, string Path, string? Query);
public sealed record ViewerResponse(int StatusCode, string ContentType, string Body);
public sealed record ViewerSession(string Id, string Title, string LastMessageUtc);
public sealed record ViewerContact(string Id, string SourceId, string DisplayName, string Alias, string? SessionId);
public sealed record ViewerFavorite(string Id, string SourceId, string Kind, string Title, string? Url, string CreatedAtUtc);
public sealed record ViewerMessage(string Id, string SessionId, string SentAtUtc, string Kind, string Body, string? MediaId);
public sealed record ViewerMessageDetail(string Id, string SessionId, string SenderName, string SentAtUtc,
    string Kind, string Body, string? MediaId);

public sealed class FootprintViewerHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ViewerJson = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    private readonly string _databasePath;
    private readonly string _snapshotRoot;
    private readonly string _archiveRoot;
    private readonly ArchiveManifest _manifest;
    private FootprintViewerHost(string databasePath, string snapshotRoot, string archiveRoot, ArchiveManifest manifest)
    {
        _databasePath = databasePath; _snapshotRoot = snapshotRoot; _archiveRoot = archiveRoot; _manifest = manifest;
    }

    public static async Task<FootprintViewerHost> OpenAsync(string archiveDirectory, CancellationToken cancellationToken = default)
    {
        var root = new DirectoryInfo(Path.GetFullPath(archiveDirectory)); root.Refresh();
        if (!root.Exists || root.LinkTarget is not null || (root.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Viewer 只接受普通档案目录。");
        var manifestPath = Path.Combine(root.FullName, "archive-manifest.json");
        var database = Path.Combine(root.FullName, "archive.db");
        var rootFiles = Directory.EnumerateFiles(root.FullName, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var rootDirectories = Directory.EnumerateDirectories(root.FullName, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!rootFiles.SequenceEqual(["archive-manifest.json", "archive.db"], StringComparer.Ordinal) ||
            !rootDirectories.SequenceEqual(["media"], StringComparer.Ordinal))
            throw new InvalidDataException("Viewer 档案文件集无效。");
        if (!File.Exists(manifestPath) || !File.Exists(database)) throw new InvalidDataException("Viewer 档案不完整。");
        RejectLink(new FileInfo(manifestPath)); RejectLink(new FileInfo(database));
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false))
                       ?? throw new InvalidDataException("Viewer 档案清单无效。");
        if (manifest.Schema != "footprint.archive.v1" || manifest.Media is null) throw new InvalidDataException("Viewer 档案校验失败。");
        await ValidateMediaAsync(root.FullName, manifest, cancellationToken).ConfigureAwait(false);
        var snapshotRoot = Path.Combine(Path.GetTempPath(), "footprint-viewer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotRoot);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(snapshotRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var snapshot = Path.Combine(snapshotRoot, "archive.db");
        try
        {
            var hash = await CopyVerifiedDatabaseAsync(database, snapshot, cancellationToken).ConfigureAwait(false);
            if (hash != manifest.DatabaseSha256) throw new InvalidDataException("Viewer 档案校验失败。");
            await ArchiveVerifier.ValidateDatabaseAsync(snapshot, manifest, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(snapshot, UnixFileMode.UserRead);
            return new FootprintViewerHost(snapshot, snapshotRoot, root.FullName, manifest);
        }
        catch
        {
            if (Directory.Exists(snapshotRoot)) Directory.Delete(snapshotRoot, true);
            throw;
        }
    }

    public async Task<ViewerResponse> HandleAsync(ViewerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) && !string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
            return new ViewerResponse(405, "application/json; charset=utf-8", "{\"error\":\"只读 Viewer 拒绝修改请求。\"}");
        var isHead = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
        ViewerResponse response;
        if (request.Path == "/" || request.Path == "/index.html") response = new ViewerResponse(200, "text/html; charset=utf-8", FootprintChatPage.Html);
        else if (request.Path is "/sessions" or "/api/sessions") response = Json(200, await ReadSessionsAsync(cancellationToken).ConfigureAwait(false));
        else if (request.Path is "/contacts" or "/api/contacts") response = Json(200, await ReadContactsAsync(cancellationToken).ConfigureAwait(false));
        else if (request.Path is "/favorites" or "/api/favorites") response = Json(200, await ReadFavoritesAsync(cancellationToken).ConfigureAwait(false));
        else if (request.Path.StartsWith("/sessions/", StringComparison.Ordinal) && request.Path.EndsWith("/messages", StringComparison.Ordinal))
        {
            var sessionId = Uri.UnescapeDataString(request.Path["/sessions/".Length..^"/messages".Length]);
            response = Json(200, await ReadSessionMessagesAsync(sessionId, request.Query, cancellationToken).ConfigureAwait(false));
        }
        else if (request.Path == "/search" || request.Path == "/api/search") response = Json(200, await SearchAsync(request.Query ?? string.Empty, cancellationToken).ConfigureAwait(false));
        else response = new ViewerResponse(404, "application/json; charset=utf-8", "{\"error\":\"未找到。\"}");
        return isHead ? response with { Body = string.Empty } : response;
    }

    public async Task<IReadOnlyList<ViewerSession>> ReadSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT id,title,last_message_utc FROM sessions ORDER BY last_message_utc DESC,id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ViewerSession>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new ViewerSession(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public async Task<IReadOnlyList<ViewerContact>> ReadContactsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.source_id,c.display_name,c.alias,s.id
            FROM contacts c JOIN sessions s ON s.source_id=c.source_id
            ORDER BY c.display_name COLLATE NOCASE,c.source_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ViewerContact>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new ViewerContact(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    public async Task<IReadOnlyList<ViewerFavorite>> ReadFavoritesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,source_id,kind,title,url,created_at_utc FROM favorites ORDER BY created_at_utc DESC,id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ViewerFavorite>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new ViewerFavorite(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
        return result;
    }

    public async Task<IReadOnlyList<ViewerMessage>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<ViewerMessage>();
        if (query.Length > 256) throw new ArgumentOutOfRangeException(nameof(query));
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT m.id,m.session_id,m.sent_at_utc,m.kind,m.body,m.media_id FROM messages_fts f JOIN messages m ON m.id=f.message_id WHERE messages_fts MATCH $query ORDER BY m.sent_at_utc,m.id LIMIT 200;";
        command.Parameters.AddWithValue("$query", '"' + query.Replace("\"", "\"\"") + '"');
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ViewerMessage>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new ViewerMessage(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result;
    }

    public async Task<IReadOnlyList<ViewerMessageDetail>> ReadSessionMessagesAsync(string sessionId, string? query = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var limit = ParseLimit(query);
        var before = ParseQueryValue(query, "before");
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.id,m.session_id,c.display_name,m.sent_at_utc,m.kind,m.body,m.media_id
            FROM messages m JOIN contacts c ON c.id=m.sender_id
            WHERE m.session_id=$session AND ($before IS NULL OR m.sent_at_utc < $before)
            ORDER BY m.sent_at_utc,m.id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$before", string.IsNullOrWhiteSpace(before) ? DBNull.Value : before);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ViewerMessageDetail>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new ViewerMessageDetail(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }

    private async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA query_only=ON;"; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static ViewerResponse Json<T>(int status, T value) => new(status, "application/json; charset=utf-8", JsonSerializer.Serialize(value, ViewerJson));

    private static int ParseLimit(string? query)
    {
        var value = ParseQueryValue(query, "limit");
        return int.TryParse(value, out var limit) ? Math.Clamp(limit, 1, 200) : 50;
    }

    private static string? ParseQueryValue(string? query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var raw = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).FirstOrDefault(part => part.Length == 2 && part[0] == key);
        return raw is null ? null : Uri.UnescapeDataString(raw[1].Replace('+', ' '));
    }

    private static async Task<string> CopyVerifiedDatabaseAsync(string source, string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, true); }
    }

    private static async Task ValidateMediaAsync(string root, ArchiveManifest manifest,
        CancellationToken cancellationToken)
    {
        var mediaRoot = Path.Combine(root, "media");
        RejectLink(new DirectoryInfo(mediaRoot));
        var actual = Directory.Exists(mediaRoot) ? Directory.EnumerateFiles(mediaRoot).ToArray() : [];
        if (actual.Length != manifest.MediaCount || manifest.Media.Count != manifest.MediaCount)
            throw new InvalidDataException("Viewer 档案媒体文件集无效。");
        foreach (var item in manifest.Media)
        {
            var path = ArchiveVerifier.ResolveRegularFile(root, item.RelativePath);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (hash != item.Sha256) throw new InvalidDataException("Viewer 档案媒体校验失败。");
        }
    }

    private static void RejectLink(FileSystemInfo info)
    {
        info.Refresh();
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Viewer 档案不能包含链接。");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_snapshotRoot)) Directory.Delete(_snapshotRoot, true);
        return ValueTask.CompletedTask;
    }
}
