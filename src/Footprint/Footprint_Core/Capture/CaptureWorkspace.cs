using System.Text.RegularExpressions;

namespace Footprint.Core.Capture;

public sealed class CaptureWorkspace
{
    private static readonly Regex RunIdPattern = new("^Footprint_Run_[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> AllowedDirectories = new(StringComparer.Ordinal)
    {
        "Footprint_Databases",
        "Footprint_Decompression",
        "Footprint_MediaSnapshot",
        "Footprint_Secrets",
        "Footprint_Package"
    };

    private CaptureWorkspace(string runId, string rootPath)
    {
        RunId = runId;
        RootPath = rootPath;
        DatabasesPath = Path.Combine(rootPath, "Footprint_Databases");
        DecompressionPath = Path.Combine(rootPath, "Footprint_Decompression");
        MediaSnapshotPath = Path.Combine(rootPath, "Footprint_MediaSnapshot");
        SecretsPath = Path.Combine(rootPath, "Footprint_Secrets");
        PackagePath = Path.Combine(rootPath, "Footprint_Package");
        ManifestPath = Path.Combine(rootPath, "Footprint_CaptureManifest.json");
    }

    public string RunId { get; }
    public string RootPath { get; }
    public string DatabasesPath { get; }
    public string DecompressionPath { get; }
    public string MediaSnapshotPath { get; }
    public string SecretsPath { get; }
    public string PackagePath { get; }
    public string ManifestPath { get; }

    public static IReadOnlyList<string> FixedTopLevelDirectories { get; } = AllowedDirectories
        .Order(StringComparer.Ordinal).ToArray();

    public static CaptureWorkspace Create(string productRoot, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productRoot);
        if (runId is null || !RunIdPattern.IsMatch(runId))
            throw new ArgumentException("Run ID must match Footprint_Run_ followed by 32 lowercase hexadecimal characters.",
                nameof(runId));

        var dataRoot = Path.GetFullPath(Path.Combine(productRoot, "Footprint_Data"));
        var runsRoot = Path.Combine(dataRoot, "Footprint_Runs");
        var runRoot = Path.Combine(runsRoot, runId);
        Directory.CreateDirectory(dataRoot);
        RejectReparsePoint(dataRoot);
        Directory.CreateDirectory(runsRoot);
        RejectReparsePoint(runsRoot);
        Directory.CreateDirectory(runRoot);
        RejectReparsePoint(runRoot);
        foreach (var existing in Directory.EnumerateFileSystemEntries(runRoot))
        {
            RejectReparsePoint(existing);
            var name = Path.GetFileName(existing);
            if (AllowedDirectories.Contains(name) ||
                string.Equals(name, "Footprint_CaptureManifest.json", StringComparison.Ordinal)) continue;
            throw new InvalidDataException("Capture Run contains an unrecognized top-level entry.");
        }
        if (Directory.Exists(Path.Combine(runRoot, "Footprint_CaptureManifest.json")))
            throw new InvalidDataException("Capture manifest path must be a file.");
        var workspace = new CaptureWorkspace(runId, runRoot);
        foreach (var directory in FixedTopLevelDirectories)
        {
            Directory.CreateDirectory(Path.Combine(runRoot, directory));
            RejectReparsePoint(Path.Combine(runRoot, directory));
        }
        return workspace;
    }

    public string ResolveRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (IsRootedOnAnyPlatform(relativePath))
            throw new ArgumentException("Capture paths must be relative.", nameof(relativePath));

        var portable = relativePath.Replace('\\', '/');
        var segments = portable.Split('/');
        if (segments.Length == 0 || segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new ArgumentException("Capture paths cannot contain traversal segments.", nameof(relativePath));
        if (!AllowedDirectories.Contains(segments[0]) &&
            !(segments.Length == 1 && string.Equals(segments[0], "Footprint_CaptureManifest.json", StringComparison.Ordinal)))
            throw new ArgumentException("Capture path is outside the fixed top-level layout.", nameof(relativePath));

        var fullPath = Path.GetFullPath(Path.Combine(RootPath, Path.Combine(segments)));
        var rootPrefix = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, PathComparison()))
            throw new ArgumentException("Capture path escapes the Run workspace.", nameof(relativePath));
        RejectReparsePoint(RootPath);
        var current = RootPath;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current)) RejectReparsePoint(current);
        }
        return fullPath;
    }

    public static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || IsRootedOnAnyPlatform(relativePath)) return false;
        var segments = relativePath.Replace('\\', '/').Split('/');
        return segments.Length > 0 && segments.All(segment =>
            !string.IsNullOrEmpty(segment) && segment is not "." and not "..");
    }

    private static bool IsRootedOnAnyPlatform(string path) =>
        Path.IsPathRooted(path) || path.StartsWith('\\') || path.StartsWith('/') ||
        (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Capture workspace paths cannot traverse reparse points.");
    }
}
