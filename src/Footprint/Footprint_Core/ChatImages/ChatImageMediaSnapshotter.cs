namespace Footprint.Core;

public sealed record ChatImageMediaSnapshotResult(
    string? Directory,
    int CopiedFileCount,
    int MatchedIndexCount,
    int MissingIndexCount,
    IReadOnlyList<string> Errors);

public sealed class ChatImageMediaSnapshotter
{
    public async Task<ChatImageMediaSnapshotResult> CreateAsync(
        string hardlinkDatabasePath,
        string sessionDirectory,
        IReadOnlyCollection<ChatImageIndexRecord> index,
        CancellationToken cancellationToken = default)
    {
        if (!ChatImageAccountRoot.TryResolve(hardlinkDatabasePath, out var accountRoot) || accountRoot is null)
            return new(null, 0, 0, index.Count, ["account_root_unresolved"]);

        var roots = new[]
        {
            (Name: "Image", Path: Path.Combine(accountRoot, "FileStorage", "Image")),
            (Name: "MsgAttach", Path: Path.Combine(accountRoot, "FileStorage", "MsgAttach")),
            (Name: Path.Combine("Msg", "Attach"), Path: Path.Combine(accountRoot, "msg", "attach"))
        }.Where(item => Directory.Exists(item.Path)).ToArray();
        if (roots.Length == 0)
            return new(null, 0, 0, index.Count, ["media_root_missing"]);

        var names = index.Select(item => item.FileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinationRoot = Path.Combine(sessionDirectory, "chat-images", "media-snapshot");
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var copied = 0;
        foreach (var root in roots)
        {
            foreach (var source in Directory.EnumerateFiles(root.Path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(source);
                if (!names.Contains(name)) continue;
                var relative = Path.GetRelativePath(root.Path, source);
                var destination = Path.Combine(destinationRoot, root.Name, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try
                {
                    await CopyAsync(source, destination, cancellationToken);
                    matched.Add(name);
                    copied++;
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException) { errors.Add("media_copy_failed"); }
                catch (UnauthorizedAccessException) { errors.Add("media_copy_failed"); }
            }
        }

        var matchedIndexCount = index.Count(item => !string.IsNullOrWhiteSpace(item.FileName) &&
            matched.Contains(item.FileName));
        return new(destinationRoot, copied, matchedIndexCount, index.Count - matchedIndexCount, errors);
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var temporary = destination + ".partial";
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            AtomicFile.Replace(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
