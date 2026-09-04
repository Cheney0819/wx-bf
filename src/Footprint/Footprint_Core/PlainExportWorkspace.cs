namespace Footprint.Core;

internal static class PlainExportWorkspace
{
    public static async Task VerifyAsync(PlainExportCandidate candidate, CancellationToken cancellationToken)
    {
        EnsureNoUnrecordedWal(candidate);
        foreach (var recorded in ContentFingerprintFiles(candidate))
        {
            var path = Path.Combine(candidate.Database.Snapshot.Directory!, recorded.Name);
            if (!File.Exists(path)) throw new FileNotFoundException("Snapshot file is missing.", path);
            var info = new FileInfo(path);
            if (info.Length != recorded.Size || !string.Equals(await Hashing.Sha256FileAsync(path, cancellationToken),
                    recorded.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Snapshot fingerprint changed: {recorded.Name}");
        }
    }

    public static async Task PrepareAsync(PlainExportCandidate candidate, CancellationToken cancellationToken)
    {
        DeleteDirectory(candidate.WorkingDirectory);
        Directory.CreateDirectory(candidate.WorkingDirectory);
        EnsureNoUnrecordedWal(candidate);
        foreach (var recorded in ContentFingerprintFiles(candidate))
        {
            var source = Path.Combine(candidate.Database.Snapshot.Directory!, recorded.Name);
            if (!File.Exists(source)) throw new FileNotFoundException("Snapshot file is missing.", source);
            var info = new FileInfo(source);
            if (info.Length != recorded.Size || !string.Equals(
                    await Hashing.Sha256FileAsync(source, cancellationToken), recorded.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Snapshot fingerprint changed: {recorded.Name}");
            var destination = Path.Combine(candidate.WorkingDirectory, recorded.Name);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            if (!string.Equals(await Hashing.Sha256FileAsync(destination, cancellationToken), recorded.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Working snapshot copy failed verification: {recorded.Name}");
        }
    }

    public static async Task<bool> CanReuseAsync(PlainExportCandidate candidate, PlainExportDatabaseResult? previous,
        CancellationToken cancellationToken)
    {
        if (previous?.OutputSha256 is null || !File.Exists(candidate.FinalPath) ||
            !string.Equals(previous.SourceName, candidate.SourceName, StringComparison.Ordinal) ||
            !string.Equals(previous.OutputPath, $"{candidate.Slot}/{candidate.SourceName}", StringComparison.Ordinal) ||
            previous.Compatibility != candidate.Database.Compatibility || previous.PageSize != candidate.Database.PageSize)
            return false;
        var recorded = previous.SourceSnapshotFiles.Where(item => IsContentFingerprintFile(candidate, item))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var current = ContentFingerprintFiles(candidate)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (recorded.Length != current.Length) return false;
        for (var index = 0; index < current.Length; index++)
            if (!string.Equals(recorded[index].Name, current[index].Name, StringComparison.OrdinalIgnoreCase) ||
                recorded[index].Size != current[index].Size ||
                !string.Equals(recorded[index].Sha256, current[index].Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
        return string.Equals(await Hashing.Sha256FileAsync(candidate.FinalPath, cancellationToken), previous.OutputSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureNoSidecars(string path)
    {
        if (new[] { "-wal", "-shm" }.Any(suffix => File.Exists(path + suffix)))
            throw new InvalidDataException("Plaintext target still has WAL/SHM sidecars.");
    }

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void EnsureNoUnrecordedWal(PlainExportCandidate candidate)
    {
        var walName = candidate.SourceName + "-wal";
        var walPath = Path.Combine(candidate.Database.Snapshot.Directory!, walName);
        if (File.Exists(walPath) && !candidate.Database.Snapshot.Files.Any(recorded =>
                string.Equals(recorded.Name, walName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Snapshot content file is not recorded: {walName}");
    }

    private static IEnumerable<SnapshotFile> ContentFingerprintFiles(PlainExportCandidate candidate) =>
        candidate.Database.Snapshot.Files.Where(recorded => IsContentFingerprintFile(candidate, recorded));

    private static bool IsContentFingerprintFile(PlainExportCandidate candidate, SnapshotFile recorded) =>
        string.Equals(recorded.Name, candidate.SourceName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(recorded.Name, candidate.SourceName + "-wal", StringComparison.OrdinalIgnoreCase);
}
