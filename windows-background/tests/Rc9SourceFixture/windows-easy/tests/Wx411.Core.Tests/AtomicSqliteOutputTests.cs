using Microsoft.Data.Sqlite;
using Wx411.Core;

namespace Wx411.Core.Tests;

public sealed class AtomicSqliteOutputTests
{
    [Fact]
    public void WritePublishesIntegrityCheckedCopyWithoutOverwritingExistingOutput()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wx411-atomic-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "message_0.db");
        var plaintext = CreateSqliteBytes(directory);

        try
        {
            var first = AtomicSqliteOutput.Write(sourcePath, directory, plaintext);
            var second = AtomicSqliteOutput.Write(sourcePath, directory, plaintext);

            Assert.Equal(Path.Combine(directory, "message_0.readable.sqlite"), first);
            Assert.NotEqual(first, second);
            Assert.Equal(plaintext, File.ReadAllBytes(first));
            Assert.Equal(plaintext, File.ReadAllBytes(second));
            SqliteIntegrityChecker.VerifyFile(first);
            SqliteIntegrityChecker.VerifyFile(second);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SourceChecksIntegrityBeforePublishAndAlwaysCleansTemporarySidecars()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "AtomicSqliteOutput.cs"));

        var integrityCheck = RequiredIndex(source, "SqliteIntegrityChecker.VerifyFile");
        var move = RequiredIndex(source, "File.Move(temporaryPath, outputPath)", integrityCheck);
        var cleanupScope = RequiredIndex(source, "finally", integrityCheck);
        var cleanup = RequiredIndex(
            source,
            "SqliteSidecarCleaner.DeleteForTemporaryDatabase(temporaryPath)",
            cleanupScope);

        Assert.True(integrityCheck < move);
        Assert.True(move < cleanupScope);
        Assert.True(cleanupScope < cleanup);
        Assert.DoesNotContain("EnumerateFiles", source, StringComparison.Ordinal);
    }

    private static byte[] CreateSqliteBytes(string directory)
    {
        var path = Path.Combine(directory, "fixture.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE payload(value TEXT NOT NULL); INSERT INTO payload VALUES ('ok');";
            command.ExecuteNonQuery();
        }

        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected source marker was not found: {value}");
        return index;
    }
}
