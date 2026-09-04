using System.IO.Compression;

namespace Footprint.Core;

public sealed class Extractor(string rootDirectory)
{
    public async Task<string> ExtractSingleFileAsync(Stream source, string relativePath, string expectedSha256,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(rootDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (File.Exists(destination) && string.Equals(await Hashing.Sha256FileAsync(destination, cancellationToken),
                expectedSha256, StringComparison.OrdinalIgnoreCase))
            return destination;

        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        var actual = await Hashing.Sha256FileAsync(temporary, cancellationToken);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporary);
            throw new InvalidDataException($"Runtime payload hash mismatch for {relativePath}.");
        }

        File.Move(temporary, destination, true);
        return destination;
    }

    public static async Task EnsureZipExtractedAsync(string zipPath, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        await using var stream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            var root = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Runtime archive contains a path traversal entry.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination) && new FileInfo(destination).Length == entry.Length &&
                await EntryEqualsFileAsync(entry, destination, cancellationToken)) continue;
            await using var input = entry.Open();
            var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
            await using (var output = File.Create(temporary))
                await input.CopyToAsync(output, cancellationToken);
            File.Move(temporary, destination, true);
        }
    }

    private static async Task<bool> EntryEqualsFileAsync(ZipArchiveEntry entry, string path,
        CancellationToken cancellationToken)
    {
        await using var expected = entry.Open();
        await using var actual = File.OpenRead(path);
        var expectedBuffer = new byte[1024 * 64];
        var actualBuffer = new byte[1024 * 64];
        while (true)
        {
            var expectedRead = await expected.ReadAsync(expectedBuffer, cancellationToken);
            var actualRead = await actual.ReadAsync(actualBuffer, cancellationToken);
            if (expectedRead != actualRead) return false;
            if (expectedRead == 0) return true;
            if (!expectedBuffer.AsSpan(0, expectedRead).SequenceEqual(actualBuffer.AsSpan(0, actualRead))) return false;
        }
    }
}
