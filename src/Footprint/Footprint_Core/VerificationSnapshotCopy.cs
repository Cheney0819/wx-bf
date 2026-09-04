namespace Footprint.Core;

public static class VerificationSnapshotCopy
{
    public static async Task<string> CreateAsync(SnapshotManifest snapshot, string databaseName, string destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (string.IsNullOrWhiteSpace(snapshot.Directory))
            throw new InvalidDataException("Snapshot directory is missing.");

        Directory.CreateDirectory(destination);
        foreach (var recorded in snapshot.Files)
        {
            var source = Path.Combine(snapshot.Directory, recorded.Name);
            if (!File.Exists(source)) throw new FileNotFoundException("Snapshot file is missing.", source);
            var sourceInfo = new FileInfo(source);
            if (sourceInfo.Length != recorded.Size || !string.Equals(
                    await Hashing.Sha256FileAsync(source, cancellationToken), recorded.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Snapshot fingerprint changed: {recorded.Name}");

            var target = Path.Combine(destination, recorded.Name);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            if (!string.Equals(await Hashing.Sha256FileAsync(target, cancellationToken), recorded.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Verification snapshot copy failed: {recorded.Name}");
        }

        return Path.Combine(destination, databaseName);
    }

    public static void Delete(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
