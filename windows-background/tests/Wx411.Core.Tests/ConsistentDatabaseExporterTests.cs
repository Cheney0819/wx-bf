using System.Security.Cryptography;

namespace Wx411.Core.Tests;

public sealed class ConsistentDatabaseExporterTests
{
    [Fact]
    public async Task ExportsPageByPageAndClearsTransferredKey()
    {
        var fixture = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 32);
        var directory = Directory.CreateTempSubdirectory("wx411-export-").FullName;
        var source = Path.Combine(directory, "message_0.db");
        var outputDirectory = Path.Combine(directory, "out");
        await File.WriteAllBytesAsync(source, fixture.Encrypted);
        var key = fixture.Key.ToArray();
        try
        {
            using var descriptor = DatabaseProbeDescriptor.Read(source);
            var exporter = new ConsistentDatabaseExporter((path, _) =>
            {
                File.WriteAllBytes(path + "-wal", []);
                File.WriteAllBytes(path + "-shm", new byte[32 * 1024]);
            });

            var result = await exporter.ExportAsync(new DatabaseExportRequest(
                source,
                descriptor.Generation,
                key,
                SqlCipher4.Profile,
                outputDirectory));

            Assert.Equal(DatabaseExportStatus.Completed, result.Status);
            Assert.NotNull(result.OutputPath);
            Assert.Equal(fixture.Plaintext, await File.ReadAllBytesAsync(result.OutputPath!));
            Assert.All(key, value => Assert.Equal(0, value));
            Assert.Empty(Directory.EnumerateFiles(outputDirectory, ".*.tmp"));
            Assert.Empty(Directory.EnumerateFiles(outputDirectory, ".*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AuthenticationFailureLeavesNoOutputOrTemporaryFile()
    {
        var fixture = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 8);
        var damaged = CipherFixtureFactory.CorruptPageTag(fixture.Encrypted, SqlCipher4.Profile, 7);
        var directory = Directory.CreateTempSubdirectory("wx411-export-").FullName;
        var source = Path.Combine(directory, "message_0.db");
        var outputDirectory = Path.Combine(directory, "out");
        await File.WriteAllBytesAsync(source, damaged);
        try
        {
            using var descriptor = DatabaseProbeDescriptor.Read(source);
            var exporter = new ConsistentDatabaseExporter((_, _) => { });

            var result = await exporter.ExportAsync(new DatabaseExportRequest(
                source,
                descriptor.Generation,
                fixture.Key.ToArray(),
                SqlCipher4.Profile,
                outputDirectory));

            Assert.Equal(DatabaseExportStatus.AuthenticationFailed, result.Status);
            Assert.Null(result.OutputPath);
            Assert.Empty(Directory.EnumerateFiles(outputDirectory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedGenerationIsRejectedBeforeOutputCreation()
    {
        var fixture = CipherFixtureFactory.Create(SqlCipher4.Profile, pageCount: 8);
        var directory = Directory.CreateTempSubdirectory("wx411-export-").FullName;
        var source = Path.Combine(directory, "message_0.db");
        var outputDirectory = Path.Combine(directory, "out");
        await File.WriteAllBytesAsync(source, fixture.Encrypted);
        try
        {
            using var descriptor = DatabaseProbeDescriptor.Read(source);
            await File.WriteAllBytesAsync(source, fixture.Encrypted.Concat(new byte[4096]).ToArray());
            var exporter = new ConsistentDatabaseExporter((_, _) => { });

            var result = await exporter.ExportAsync(new DatabaseExportRequest(
                source,
                descriptor.Generation,
                fixture.Key.ToArray(),
                SqlCipher4.Profile,
                outputDirectory));

            Assert.Equal(DatabaseExportStatus.GenerationChanged, result.Status);
            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExporterSourceHasNoWholeDatabaseRead()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "ConsistentDatabaseExporter.cs"));
        var snapshot = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "StreamingWalSnapshot.cs"));

        Assert.DoesNotContain("File.ReadAllBytes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllBytes", snapshot, StringComparison.Ordinal);
        Assert.Contains("FileOptions.SequentialScan", source, StringComparison.Ordinal);
        Assert.Contains("FileOptions.RandomAccess", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void WalMetadataReadAllowsConcurrentWriterAndFailureKeepsSnapshotIncomplete()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "StreamingWalSnapshot.cs"));
        var resolverStart = source.IndexOf(
            "private static int ResolvePageSize",
            StringComparison.Ordinal);
        var resolverEnd = source.IndexOf(
            "private static FileMarker? ReadFileMarker",
            resolverStart,
            StringComparison.Ordinal);
        Assert.True(resolverStart >= 0 && resolverEnd > resolverStart);
        var resolver = source[resolverStart..resolverEnd];

        Assert.DoesNotContain("File.OpenRead(walPath)", resolver, StringComparison.Ordinal);
        Assert.Contains("FileShare.ReadWrite | FileShare.Delete", resolver, StringComparison.Ordinal);

        var resolveBeforeReturn = source.IndexOf(
            "var pageSize = ResolvePageSize(temporaryPath, walPath);",
            StringComparison.Ordinal);
        var completed = source.IndexOf("completed = true;", StringComparison.Ordinal);
        Assert.True(resolveBeforeReturn >= 0 && resolveBeforeReturn < completed);
    }
}
