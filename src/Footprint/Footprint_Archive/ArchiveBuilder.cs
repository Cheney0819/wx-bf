using System.Security.Cryptography;
using System.Text.Json;
using Footprint.Parser;
using Microsoft.Data.Sqlite;

namespace Footprint.Archive;

public enum ArchiveBuildStage { DatabaseCommitted, BeforePublish, Published }
public sealed record ArchiveMediaInput(string Id, string MessageId, string Kind, string SourcePath, string Sha256, string Format);
public sealed record ArchiveArtifact(string SourceId, string DeviceId, string ArchiveDirectory, string DatabasePath,
    string DatabaseSha256, string ContentSha256);
public sealed record ArchiveManifestMedia(string Id, string Sha256, string Format, string RelativePath);
public sealed record ArchiveManifest(string Schema, string SourceId, string DeviceId, string DatabaseSha256, string ContentSha256,
    int ContactCount, int SessionCount, int MessageCount, int FavoriteCount, int MediaCount,
    IReadOnlyList<ArchiveManifestMedia> Media);

public sealed class ArchiveBuilder(Action<ArchiveBuildStage>? stageObserved = null)
{
    public async Task<ArchiveArtifact> BuildAsync(ParsedPackage package, IReadOnlyList<ArchiveMediaInput> media,
        string archiveRoot, string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(media);
        ValidateSourceId(package.SourceId);
        ValidateDeviceId(deviceId);
        archiveRoot = Path.GetFullPath(archiveRoot);
        Directory.CreateDirectory(archiveRoot);
        RejectLink(new DirectoryInfo(archiveRoot), "档案根目录不能是链接。");
        await using var lease = await ArchiveLease.AcquireAsync(archiveRoot, package.SourceId, cancellationToken).ConfigureAwait(false);
        RemoveStalePartials(archiveRoot, package.SourceId);
        var final = Path.Combine(archiveRoot, package.SourceId);
        var expectedContentSha = ArchiveVerifier.ComputeContentSha256(package, media, deviceId);
        if (Directory.Exists(final)) return await ValidateExistingAsync(final, package, media, deviceId,
            expectedContentSha, cancellationToken).ConfigureAwait(false);
        if (File.Exists(final)) throw new InvalidDataException("档案发布目标不是目录。");
        var temporary = Path.Combine(archiveRoot, package.SourceId + ".partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var database = Path.Combine(temporary, "archive.db");
            await BuildDatabaseAsync(database, package, media, deviceId, cancellationToken).ConfigureAwait(false);
            stageObserved?.Invoke(ArchiveBuildStage.DatabaseCommitted);
            var mediaRoot = Path.Combine(temporary, "media");
            Directory.CreateDirectory(mediaRoot);
            foreach (var item in media.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateSha256(item.Sha256);
                var source = new FileInfo(Path.GetFullPath(item.SourcePath));
                source.Refresh();
                if (!source.Exists || source.LinkTarget is not null || (source.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("档案媒体输入不是普通文件。");
                var extension = NormalizeFormat(item.Format);
                var target = Path.Combine(mediaRoot, item.Id + "." + extension);
                await CopyDurableVerifiedAsync(source.FullName, target, item.Sha256, cancellationToken).ConfigureAwait(false);
            }

            var databaseSha = await HashFileAsync(database, cancellationToken).ConfigureAwait(false);
            var manifestMedia = media.OrderBy(value => value.Id, StringComparer.Ordinal).Select(value =>
                new ArchiveManifestMedia(value.Id, value.Sha256, NormalizeFormat(value.Format),
                    "media/" + value.Id + "." + NormalizeFormat(value.Format))).ToArray();
            var manifest = new ArchiveManifest("footprint.archive.v1", package.SourceId, deviceId, databaseSha, expectedContentSha,
                package.Contacts.Count, package.Sessions.Count, package.Messages.Count, package.Favorites.Count, media.Count,
                manifestMedia);
            var manifestPath = Path.Combine(temporary, "archive-manifest.json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllBytesAsync(manifestPath, bytes, cancellationToken).ConfigureAwait(false);
            FlushFile(manifestPath);
            await ArchiveVerifier.ValidateDatabaseAsync(database, manifest, cancellationToken).ConfigureAwait(false);
            ArchiveDurability.FlushTree(temporary);
            stageObserved?.Invoke(ArchiveBuildStage.BeforePublish);
            Directory.Move(temporary, final);
            ArchiveDurability.FlushDirectory(archiveRoot);
            stageObserved?.Invoke(ArchiveBuildStage.Published);
            return new ArchiveArtifact(package.SourceId, deviceId, final, Path.Combine(final, "archive.db"), databaseSha, expectedContentSha);
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            throw;
        }
    }

    private static async Task BuildDatabaseAsync(string path, ParsedPackage package, IReadOnlyList<ArchiveMediaInput> media,
        string deviceId, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA page_size=4096; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA auto_vacuum=NONE; PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, """
            CREATE TABLE archive_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE contacts(id TEXT PRIMARY KEY, source_id TEXT NOT NULL, display_name TEXT NOT NULL, alias TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE sessions(id TEXT PRIMARY KEY, source_id TEXT NOT NULL, title TEXT NOT NULL, last_message_utc TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE messages(id TEXT PRIMARY KEY, source_id TEXT NOT NULL, session_id TEXT NOT NULL REFERENCES sessions(id) DEFERRABLE INITIALLY DEFERRED, sender_id TEXT NOT NULL REFERENCES contacts(id) DEFERRABLE INITIALLY DEFERRED, sent_at_utc TEXT NOT NULL, kind TEXT NOT NULL, body TEXT NOT NULL, media_id TEXT NULL REFERENCES media(id) DEFERRABLE INITIALLY DEFERRED) WITHOUT ROWID;
            CREATE TABLE favorites(id TEXT PRIMARY KEY, source_id TEXT NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL, url TEXT NULL, created_at_utc TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE media(id TEXT PRIMARY KEY, message_id TEXT NOT NULL REFERENCES messages(id) DEFERRABLE INITIALLY DEFERRED, kind TEXT NOT NULL, sha256 TEXT NOT NULL, format TEXT NOT NULL, relative_path TEXT NOT NULL) WITHOUT ROWID;
            CREATE VIRTUAL TABLE messages_fts USING fts5(message_id UNINDEXED, body, tokenize='trigram');
            """, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await InsertAsync(connection, transaction, "INSERT INTO archive_metadata VALUES ('schema','footprint.archive.v1'),('source_id',$source),('device_id',$device);", [("$source", package.SourceId), ("$device", deviceId)], cancellationToken).ConfigureAwait(false);
        foreach (var item in package.Contacts.OrderBy(value => value.Id, StringComparer.Ordinal))
            await InsertAsync(connection, transaction, "INSERT INTO contacts VALUES ($id,$source,$name,$alias);", [("$id", item.Id), ("$source", item.SourceId), ("$name", item.DisplayName), ("$alias", item.Alias)], cancellationToken).ConfigureAwait(false);
        foreach (var item in package.Sessions.OrderBy(value => value.Id, StringComparer.Ordinal))
            await InsertAsync(connection, transaction, "INSERT INTO sessions VALUES ($id,$source,$title,$utc);", [("$id", item.Id), ("$source", item.SourceId), ("$title", item.Title), ("$utc", item.LastMessageUtc.ToUniversalTime().ToString("O"))], cancellationToken).ConfigureAwait(false);
        var mediaByMessage = media.ToDictionary(value => value.MessageId, StringComparer.Ordinal);
        foreach (var item in package.Messages.OrderBy(value => value.Id, StringComparer.Ordinal))
        {
            mediaByMessage.TryGetValue(item.Id, out var linked);
            await InsertAsync(connection, transaction, "INSERT INTO messages VALUES ($id,$source,$session,$sender,$utc,$kind,$body,$media);", [("$id", item.Id), ("$source", item.SourceId), ("$session", item.SessionId), ("$sender", item.SenderId), ("$utc", item.SentAtUtc.ToUniversalTime().ToString("O")), ("$kind", item.Kind), ("$body", item.Body), ("$media", linked?.Id)], cancellationToken).ConfigureAwait(false);
            await InsertAsync(connection, transaction, "INSERT INTO messages_fts VALUES ($id,$body);", [("$id", item.Id), ("$body", item.Body)], cancellationToken).ConfigureAwait(false);
        }
        foreach (var item in package.Favorites.OrderBy(value => value.Id, StringComparer.Ordinal))
            await InsertAsync(connection, transaction, "INSERT INTO favorites VALUES ($id,$source,$kind,$title,$url,$utc);", [("$id", item.Id), ("$source", item.SourceId), ("$kind", item.Kind), ("$title", item.Title), ("$url", item.Url), ("$utc", item.CreatedAtUtc.ToUniversalTime().ToString("O"))], cancellationToken).ConfigureAwait(false);
        foreach (var item in media.OrderBy(value => value.Id, StringComparer.Ordinal))
            await InsertAsync(connection, transaction, "INSERT INTO media VALUES ($id,$message,$kind,$sha,$format,$path);", [("$id", item.Id), ("$message", item.MessageId), ("$kind", item.Kind), ("$sha", item.Sha256), ("$format", NormalizeFormat(item.Format)), ("$path", "media/" + item.Id + "." + NormalizeFormat(item.Format))], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA optimize;", cancellationToken).ConfigureAwait(false);
        await connection.CloseAsync().ConfigureAwait(false);
        FlushFile(path);
    }

    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        IEnumerable<(string Name, object? Value)> parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ArchiveArtifact> ValidateExistingAsync(string directory, ParsedPackage package,
        IReadOnlyList<ArchiveMediaInput> expectedMedia, string deviceId, string expectedContentSha,
        CancellationToken cancellationToken)
    {
        RejectLink(new DirectoryInfo(directory), "现有档案不能是链接。");
        var rootFiles = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var rootDirectories = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        if (!rootFiles.SequenceEqual(["archive-manifest.json", "archive.db"], StringComparer.Ordinal) ||
            !rootDirectories.SequenceEqual(["media"], StringComparer.Ordinal))
            throw new InvalidDataException("现有档案文件集无效。");
        var manifestPath = Path.Combine(directory, "archive-manifest.json");
        var database = Path.Combine(directory, "archive.db");
        if (!File.Exists(manifestPath) || !File.Exists(database)) throw new InvalidDataException("现有档案不完整。");
        RejectLink(new FileInfo(manifestPath), "现有档案清单不能是链接。");
        RejectLink(new FileInfo(database), "现有档案数据库不能是链接。");
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false))
                       ?? throw new InvalidDataException("现有档案清单无效。");
        if (manifest.Schema != "footprint.archive.v1" || manifest.SourceId != package.SourceId ||
            manifest.DeviceId != deviceId || manifest.ContentSha256 != expectedContentSha ||
            manifest.ContactCount != package.Contacts.Count ||
            manifest.SessionCount != package.Sessions.Count || manifest.MessageCount != package.Messages.Count ||
            manifest.FavoriteCount != package.Favorites.Count || manifest.MediaCount != expectedMedia.Count ||
            manifest.Media is null) throw new InvalidDataException("现有档案身份或内容不匹配。");
        var validationSnapshot = Path.Combine(Path.GetDirectoryName(directory)!, "." + package.SourceId + ".verify-" + Guid.NewGuid().ToString("N") + ".db");
        string hash;
        try
        {
            await CopyDurableVerifiedAsync(database, validationSnapshot, manifest.DatabaseSha256, cancellationToken).ConfigureAwait(false);
            hash = await HashFileAsync(validationSnapshot, cancellationToken).ConfigureAwait(false);
            await ArchiveVerifier.ValidateDatabaseAsync(validationSnapshot, manifest, cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(validationSnapshot)) File.Delete(validationSnapshot); }
        var expectedFiles = manifest.Media.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        if (expectedFiles.Length != expectedMedia.Count || !expectedFiles.Select(value => (value.Id, value.Sha256, value.Format))
                .SequenceEqual(expectedMedia.OrderBy(value => value.Id, StringComparer.Ordinal)
                    .Select(value => (value.Id, value.Sha256, NormalizeFormat(value.Format)))))
            throw new InvalidDataException("现有档案媒体清单冲突。");
        var mediaRoot = Path.Combine(directory, "media");
        RejectLink(new DirectoryInfo(mediaRoot), "现有档案媒体目录不能是链接。");
        var actualPaths = Directory.Exists(mediaRoot)
            ? Directory.EnumerateFiles(mediaRoot, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray()
            : [];
        if (actualPaths.Length != expectedFiles.Length) throw new InvalidDataException("现有档案媒体文件集不完整。");
        foreach (var item in expectedFiles)
        {
            var path = ArchiveVerifier.ResolveRegularFile(directory, item.RelativePath);
            if (await HashFileAsync(path, cancellationToken).ConfigureAwait(false) != item.Sha256)
                throw new InvalidDataException("现有档案媒体摘要不匹配。");
        }
        return new ArchiveArtifact(package.SourceId, deviceId, directory, database, hash, manifest.ContentSha256);
    }

    private static async Task CopyDurableVerifiedAsync(string source, string target, string expectedSha,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
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
            if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != expectedSha)
                throw new InvalidDataException("档案媒体输入摘要不匹配。");
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, true); }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void FlushFile(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); stream.Flush(true); }
    private static string NormalizeFormat(string value) { if (string.IsNullOrWhiteSpace(value) || value.Any(character => !char.IsAsciiLetterOrDigit(character))) throw new InvalidDataException("档案媒体格式无效。"); return value.ToLowerInvariant(); }
    private static void ValidateSha256(string value) { if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw new InvalidDataException("档案媒体摘要无效。"); }
    private static void ValidateSourceId(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value is "." or ".." || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) throw new InvalidDataException("档案来源标识无效。"); }
    private static void ValidateDeviceId(string value) { if (value is not { Length: >= 1 and <= 128 } || !char.IsAsciiLetterOrDigit(value[0]) || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) throw new InvalidDataException("档案设备标识无效。"); }
    private static void RejectLink(FileSystemInfo info, string message) { info.Refresh(); if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(message); }
    private static void RemoveStalePartials(string root, string sourceId) { foreach (var path in Directory.EnumerateDirectories(root, sourceId + ".partial-*", SearchOption.TopDirectoryOnly)) Directory.Delete(path, true); }
}
