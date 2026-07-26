using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Wx411.Core.Tests;

public sealed class EvidenceBundleServiceTests
{
    [Fact]
    public async Task ExportCreatesSelfCheckingRedactedBundleWithoutLegacyDiagnosticsOrDatabasePayloads()
    {
        var root = CreateTempDirectory();
        try
        {
            var output = Path.Combine(root, "output");
            Directory.CreateDirectory(output);
            var secret = new string('A', 64);
            var session = Session(errorMessage: $"raw key: {secret}");

            var result = await new EvidenceBundleService().ExportAsync(
                session,
                $"candidate key: {secret}\nnormal log",
                output,
                CancellationToken.None);

            Assert.True(File.Exists(result.BundlePath));
            Assert.Equal(EvidenceGateStatus.Incomplete, result.Assessment.Overall);
            using var archive = ZipFile.OpenRead(result.BundlePath);
            var names = archive.Entries
                .Select(entry => entry.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                new[]
                {
                    "SHA256SUMS.txt",
                    "SUMMARY.txt",
                    "evidence.json",
                    "window-log.txt",
                },
                names);
            Assert.DoesNotContain(names, name =>
                name.StartsWith("diagnostics/", StringComparison.Ordinal) ||
                name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".capture", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(secret, ReadText(archive, "window-log.txt"), StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", ReadText(archive, "window-log.txt"), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, ReadText(archive, "evidence.json"), StringComparison.Ordinal);
            var summary = ReadText(archive, "SUMMARY.txt");
            Assert.Contains("门禁 A：N/A", summary, StringComparison.Ordinal);
            Assert.Contains("总结果只由门禁 B", summary, StringComparison.Ordinal);
            AssertChecksums(archive);
            Assert.Empty(Directory.EnumerateFiles(output, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DestinationFailureLeavesNoTemporaryZip()
    {
        var root = CreateTempDirectory();
        try
        {
            var destinationFile = Path.Combine(root, "not-a-directory");
            await File.WriteAllTextAsync(destinationFile, "occupied");

            await Assert.ThrowsAnyAsync<IOException>(() => new EvidenceBundleService().ExportAsync(
                Session(),
                "log",
                destinationFile,
                CancellationToken.None));

            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationBeforeExportCreatesNoDestination()
    {
        var root = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "cancelled-output");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new EvidenceBundleService().ExportAsync(
                    Session(),
                    "log",
                    destination,
                    cancellation.Token));

            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static EvidenceSessionSnapshot Session(string? errorMessage = null)
    {
        var source = new EvidenceFileRecord("source.db", true, 10, "aa", null, null);
        var output = new EvidenceFileRecord("message_0.readable.sqlite", true, 10, "bb", "ok", null);
        var operation = new EvidenceOperationRecord(
            Guid.NewGuid(),
            EvidenceOperationKind.PreciseCapture,
            EvidenceOperationOutcome.Success,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow,
            new EvidenceProcessSelection(null, "automatic", true),
            source,
            source,
            "output",
            DirectorySnapshot(),
            DirectorySnapshot(),
            Array.AsReadOnly(new[] { output }),
            errorMessage is null ? null : "InvalidOperationException",
            errorMessage,
            null);
        return new EvidenceSessionSnapshot(
            "1.5-dev",
            DateTimeOffset.UtcNow,
            "Windows",
            true,
            true,
            Array.AsReadOnly(new[] { operation }));
    }

    private static EvidenceDirectorySnapshot DirectorySnapshot() => new(
        "output",
        DateTimeOffset.UtcNow,
        Array.Empty<EvidenceDirectoryEntry>(),
        null);

    private static string ReadText(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException(entryName);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void AssertChecksums(ZipArchive archive)
    {
        var checksums = ReadText(archive, "SHA256SUMS.txt")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
        var expectedNames = archive.Entries
            .Select(entry => entry.FullName)
            .Where(name => name != "SHA256SUMS.txt")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedNames, checksums.Keys.OrderBy(name => name, StringComparer.Ordinal));
        foreach (var name in expectedNames)
        {
            var entry = archive.GetEntry(name)!;
            using var stream = entry.Open();
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            Assert.Equal(checksums[name], actual);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "wx411-evidence-bundle-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
