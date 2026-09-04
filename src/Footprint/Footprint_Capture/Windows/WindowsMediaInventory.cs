using System.Text;
using Footprint.Core;
using Footprint.Core.Capture;

namespace Footprint.Capture.Windows;

public sealed record WindowsMediaCandidateRoot(
    string Path,
    string RelativePath,
    CaptureSourceCategory Category,
    string SourceIdentityHash);

public sealed class WindowsMediaInventory
{
    private static readonly (CaptureSourceCategory Category, string RelativePath)[] CandidateRoots =
    [
        (CaptureSourceCategory.Image, "FileStorage/Image"),
        (CaptureSourceCategory.Image, "FileStorage/Images"),
        (CaptureSourceCategory.Voice, "FileStorage/Voice"),
        (CaptureSourceCategory.Voice, "FileStorage/Audio"),
        (CaptureSourceCategory.Voice, "msg/voice"),
        (CaptureSourceCategory.Favorite, "FileStorage/Favorite"),
        (CaptureSourceCategory.Favorite, "FileStorage/Favorites"),
        (CaptureSourceCategory.Favorite, "FileStorage/Fav"),
        (CaptureSourceCategory.Favorite, "Favorite"),
        (CaptureSourceCategory.Attachment, "FileStorage/MsgAttach"),
        (CaptureSourceCategory.Attachment, "FileStorage/File"),
        (CaptureSourceCategory.Attachment, "msg/attach")
    ];

    public IReadOnlyList<WindowsMediaCandidateRoot> DiscoverRoots(string accountRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountRoot);
        var fullAccountRoot = Path.GetFullPath(accountRoot);
        if (!Directory.Exists(fullAccountRoot))
            throw new DirectoryNotFoundException("The discovered account root does not exist.");
        if (IsReparsePoint(fullAccountRoot))
            throw new InvalidDataException("The discovered account root cannot be a reparse point.");

        return CandidateRoots
            .Select(candidate =>
            {
                var path = Path.GetFullPath(Path.Combine(fullAccountRoot,
                    candidate.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                return new WindowsMediaCandidateRoot(path, candidate.RelativePath, candidate.Category,
                    IdentityHash(path));
            })
            .Where(candidate => Directory.Exists(candidate.Path) && !IsReparsePoint(candidate.Path))
            .OrderBy(candidate => CategoryOrder(candidate.Category))
            .ThenBy(candidate => candidate.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<MediaSnapshotSource> DiscoverFiles(string accountRoot,
        CancellationToken cancellationToken)
    {
        var files = new List<MediaSnapshotSource>();
        foreach (var root in DiscoverRoots(accountRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateFiles(root.Path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    ReturnSpecialDirectories = false
                }).OrderBy(path => path, PathComparer()).ToArray();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sourcePath in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(sourcePath);
                if (!IsUnderRoot(fullPath, root.Path) || IsReparsePoint(fullPath)) continue;
                var relativePath = Path.GetRelativePath(root.Path, fullPath).Replace('\\', '/');
                if (!CaptureWorkspace.IsSafeRelativePath(relativePath)) continue;
                var category = ClassifyFile(root, relativePath, fullPath);
                files.Add(new MediaSnapshotSource(fullPath, relativePath, category, IdentityHash(fullPath),
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["candidate_root"] = root.RelativePath,
                        ["candidate_root_identity"] = root.SourceIdentityHash,
                        ["source_relative_path"] = relativePath
                    }));
            }
        }

        foreach (var imageDirectory in DiscoverImgDirectories(Path.GetFullPath(accountRoot), cancellationToken))
        {
            foreach (var sourcePath in Directory.EnumerateFiles(imageDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(sourcePath) || !IsImagePayloadExtension(sourcePath)) continue;
                var relativePath = Path.GetRelativePath(Path.GetFullPath(accountRoot), sourcePath).Replace('\\', '/');
                if (!CaptureWorkspace.IsSafeRelativePath(relativePath)) continue;
                files.Add(new MediaSnapshotSource(Path.GetFullPath(sourcePath), relativePath, CaptureSourceCategory.Image,
                    IdentityHash(sourcePath), new SortedDictionary<string, string>(StringComparer.Ordinal)
                    { ["candidate_root"] = "recursive-img", ["source_relative_path"] = relativePath }));
            }
        }

        return files
            .GroupBy(file => file.SourceIdentityHash, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(file => CategoryOrder(file.Category))
            .ThenBy(file => file.SourceIdentityHash, StringComparer.Ordinal)
            .ToArray();
    }

    private static CaptureSourceCategory ClassifyFile(WindowsMediaCandidateRoot root,
        string relativePath, string fullPath)
    {
        if (root.Category == CaptureSourceCategory.Attachment &&
            string.Equals(root.RelativePath, "msg/attach", StringComparison.OrdinalIgnoreCase) &&
            IsImagePayloadExtension(fullPath) &&
            relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => string.Equals(segment, "Img", StringComparison.OrdinalIgnoreCase)))
            return CaptureSourceCategory.Image;
        return root.Category;
    }

    private static IEnumerable<string> DiscoverImgDirectories(string accountRoot, CancellationToken cancellationToken)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(accountRoot, "Img", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MaxRecursionDepth = 12
            });
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsReparsePoint(directory)) yield return directory;
        }
    }

    private static bool IsImagePayloadExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".dat" or ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" => true,
        _ => false
    };

    private static string IdentityHash(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return Hashing.Sha256Hex(Encoding.UTF8.GetBytes(normalized));
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static int CategoryOrder(CaptureSourceCategory category) => category switch
    {
        CaptureSourceCategory.Image => 0,
        CaptureSourceCategory.Voice => 1,
        CaptureSourceCategory.Favorite => 2,
        CaptureSourceCategory.Attachment => 3,
        _ => 99
    };

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
