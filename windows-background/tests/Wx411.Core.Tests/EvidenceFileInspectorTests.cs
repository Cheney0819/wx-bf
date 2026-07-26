using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Wx411.Core.Tests;

public sealed class EvidenceFileInspectorTests
{
    [Fact]
    public async Task InspectAsyncReturnsLowercaseSha256AndLength()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "sample.bin");
            var payload = Encoding.UTF8.GetBytes("evidence-payload");
            await File.WriteAllBytesAsync(path, payload);
            var inspector = new EvidenceFileInspector();

            var result = await inspector.InspectAsync(path, verifySqlite: false, CancellationToken.None);

            Assert.True(result.Exists);
            Assert.Equal(payload.Length, result.Length);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                result.Sha256);
            Assert.Null(result.IntegrityCheck);
            Assert.Null(result.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InspectAsyncRecordsMissingFileWithoutThrowing()
    {
        var inspector = new EvidenceFileInspector();

        var result = await inspector.InspectAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.db"),
            verifySqlite: true,
            CancellationToken.None);

        Assert.False(result.Exists);
        Assert.Null(result.Sha256);
        Assert.Equal("file_not_found", result.Error);
    }

    [Fact]
    public async Task SqliteVerificationUsesPrivateCopyAndLeavesOriginalDirectoryClean()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "valid.sqlite");
            CreateSqliteDatabase(path);
            var before = Directory.GetFileSystemEntries(root).OrderBy(item => item).ToArray();
            var inspector = new EvidenceFileInspector();

            var result = await inspector.InspectAsync(path, verifySqlite: true, CancellationToken.None);

            Assert.Equal("ok", result.IntegrityCheck);
            Assert.Null(result.Error);
            Assert.Equal(before, Directory.GetFileSystemEntries(root).OrderBy(item => item));
            Assert.DoesNotContain(
                Directory.GetFileSystemEntries(root),
                item => item.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
                        item.EndsWith("-shm", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptSqliteReturnsFailedIntegrityEvidence()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "broken.sqlite");
            await File.WriteAllTextAsync(path, "not a sqlite database");
            var inspector = new EvidenceFileInspector();

            var result = await inspector.InspectAsync(path, verifySqlite: true, CancellationToken.None);

            Assert.True(result.Exists);
            Assert.Equal("failed", result.IntegrityCheck);
            Assert.Contains("integrity_check", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrivateCopyCleanupFailureIsRetriedAndReported()
    {
        var root = CreateTempDirectory();
        string? privateDirectory = null;
        var deleteAttempts = 0;
        try
        {
            var path = Path.Combine(root, "valid.sqlite");
            CreateSqliteDatabase(path);
            var cleaner = new EvidenceTemporaryDirectoryCleaner(
                Directory.Exists,
                (directory, recursive) =>
                {
                    Assert.True(recursive);
                    privateDirectory = directory;
                    deleteAttempts++;
                    throw new IOException("simulated cleanup lock");
                },
                _ => { });
            var inspector = new EvidenceFileInspector(cleaner);

            var result = await inspector.InspectAsync(path, verifySqlite: true, CancellationToken.None);

            Assert.Equal(3, deleteAttempts);
            Assert.Equal("failed", result.IntegrityCheck);
            Assert.Contains("cleanup", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(privateDirectory);
            Assert.True(Directory.Exists(privateDirectory));
        }
        finally
        {
            if (privateDirectory is not null && Directory.Exists(privateDirectory))
                Directory.Delete(privateDirectory, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteHashAndIntegrityUseOneSnapshotWhenSourceChangesDuringInspection()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source.sqlite");
            var replacement = Path.Combine(root, "replacement.sqlite");
            CreateSqliteDatabase(source, "original");
            CreateSqliteDatabase(replacement, "replacement");
            var originalSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))
                .ToLowerInvariant();
            var inspector = new EvidenceFileInspector(
                new EvidenceTemporaryDirectoryCleaner(),
                (_, _) =>
                {
                    File.Copy(replacement, source, overwrite: true);
                    File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
                });

            var result = await inspector.InspectAsync(source, verifySqlite: true, CancellationToken.None);

            Assert.Equal(originalSha256, result.Sha256);
            Assert.Equal("ok", result.IntegrityCheck);
            Assert.Equal("file_changed_during_inspection", result.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CaptureDirectorySortsEntriesAndMarksTemporaryArtifacts()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "z.sqlite-wal"), string.Empty);
            File.WriteAllText(Path.Combine(root, "a.sqlite"), string.Empty);
            File.WriteAllText(Path.Combine(root, "m.random.tmp"), string.Empty);
            Directory.CreateDirectory(Path.Combine(root, "subdir"));
            var inspector = new EvidenceFileInspector();

            var snapshot = inspector.CaptureDirectory(root);

            Assert.Null(snapshot.Error);
            Assert.Equal(
                new[] { "a.sqlite", "m.random.tmp", "subdir", "z.sqlite-wal" },
                snapshot.Entries.Select(entry => entry.Name));
            Assert.False(snapshot.Entries.Single(entry => entry.Name == "a.sqlite").IsTemporaryArtifact);
            Assert.True(snapshot.Entries.Single(entry => entry.Name == "m.random.tmp").IsTemporaryArtifact);
            Assert.True(snapshot.Entries.Single(entry => entry.Name == "z.sqlite-wal").IsTemporaryArtifact);
            Assert.True(snapshot.Entries.Single(entry => entry.Name == "subdir").IsDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateSqliteDatabase(string path, string value = "ok")
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; CREATE TABLE evidence(id INTEGER PRIMARY KEY, value TEXT); INSERT INTO evidence(value) VALUES ($value);";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "wx411-evidence-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
