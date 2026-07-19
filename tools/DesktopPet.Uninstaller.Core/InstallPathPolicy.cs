using System.Text.RegularExpressions;

namespace DesktopPet.Uninstaller.Core;

public static class InstallPathPolicy
{
    private static readonly Regex WindowsDrivePath = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);

    public static bool TryCreate(string input, string profileDirectory, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(profileDirectory))
        {
            return false;
        }

        var full = Normalize(input);
        var profile = Normalize(profileDirectory);

        if (full.Equals(GetRoot(full), StringComparison.OrdinalIgnoreCase) ||
            full.Equals(profile, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalized = full;
        return true;
    }

    public static bool IsWithin(string directory, string path)
    {
        var normalizedDirectory = Normalize(directory);
        var normalizedPath = Normalize(path);
        var separator = IsWindowsPath(normalizedDirectory) ? '\\' : Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(normalizedDirectory + separator, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        if (!IsWindowsPath(path))
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }

        var normalized = path.Replace('/', '\\');
        var drive = normalized[..2].ToUpperInvariant();
        var segments = new List<string>();
        foreach (var segment in normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? drive + "\\" : drive + "\\" + string.Join('\\', segments);
    }

    private static string GetRoot(string path) =>
        IsWindowsPath(path) ? path[..2] + "\\" : Path.GetPathRoot(path)!;

    private static bool IsWindowsPath(string path) => WindowsDrivePath.IsMatch(path);
}
