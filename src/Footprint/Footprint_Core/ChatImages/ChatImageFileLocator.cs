namespace Footprint.Core;

public static class ChatImageAccountRoot
{
    public static bool TryResolve(string hardlinkDatabasePath, out string? accountRoot)
    {
        accountRoot = null;
        if (string.IsNullOrWhiteSpace(hardlinkDatabasePath)) return false;
        var full = Path.GetFullPath(hardlinkDatabasePath);
        var file = new DirectoryInfo(Path.GetDirectoryName(full)!);
        if (!string.Equals(Path.GetFileName(full), "hardlink.db", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(file.Name, "hardlink", StringComparison.OrdinalIgnoreCase) || file.Parent is null ||
            !string.Equals(file.Parent.Name, "db_storage", StringComparison.OrdinalIgnoreCase) || file.Parent.Parent is null)
            return false;
        accountRoot = file.Parent.Parent.FullName;
        return true;
    }
}

public sealed class ChatImageFileLocator
{
    public IReadOnlyList<ChatImageLocation> Locate(string hardlinkDatabasePath,
        IReadOnlyList<ChatImageIndexRecord> index, CancellationToken cancellationToken = default,
        string? mediaSnapshotDirectory = null)
    {
        if (!ChatImageAccountRoot.TryResolve(hardlinkDatabasePath, out var accountRoot) || accountRoot is null)
            return index.Select(item => new ChatImageLocation(item, [], "account_root_unresolved")).ToArray();

        var mediaRoots = (mediaSnapshotDirectory is null ? new[]
            {
                Path.Combine(accountRoot, "FileStorage", "Image"),
                Path.Combine(accountRoot, "FileStorage", "MsgAttach"),
                Path.Combine(accountRoot, "msg", "attach")
            } : new[]
            {
                Path.Combine(mediaSnapshotDirectory, "Image"),
                Path.Combine(mediaSnapshotDirectory, "MsgAttach"),
                Path.Combine(mediaSnapshotDirectory, "Msg", "Attach")
            })
            .Where(Directory.Exists)
            .ToArray();
        if (mediaRoots.Length == 0)
            return index.Select(item => new ChatImageLocation(item, [], "local_file_missing")).ToArray();

        var files = mediaRoots.SelectMany(root => Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        return index.Select(item =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!files.TryGetValue(item.FileName, out var candidates) || candidates.Length == 0)
                return new ChatImageLocation(item, [], "local_file_missing");
            var bounded = candidates.Where(path => mediaRoots.Any(root => IsUnderRoot(path, root))).ToArray();
            return bounded.Length switch
            {
                0 => new ChatImageLocation(item, [], "local_file_missing"),
                1 => new ChatImageLocation(item, bounded, null),
                _ => new ChatImageLocation(item, bounded, "ambiguous_candidates")
            };
        }).ToArray();
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
