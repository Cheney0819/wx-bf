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

        string full;
        string profile;
        try
        {
            full = Normalize(input);
            profile = Normalize(profileDirectory);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

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
        string normalizedDirectory;
        string normalizedPath;
        try
        {
            normalizedDirectory = Normalize(directory);
            normalizedPath = Normalize(path);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        var separator = IsWindowsPath(normalizedDirectory) ? '\\' : Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(normalizedDirectory + separator, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("UNC/device paths are not valid installation targets.");
        }

        if (!IsWindowsPath(path))
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }

        // UNC, device and extended-length namespaces are alternate object
        // identities. Reject them before verification/deletion.
        var normalized = path.Replace('/', '\\');
        var drive = normalized[..2].ToUpperInvariant();
        var segments = new List<string>();
        foreach (var segment in normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            // Win32 aliases (trailing dot/space and 8.3 short names) must not
            // be compared textually with a canonical profile/root path.
            if (segment.EndsWith(' ') || segment.EndsWith('.') || segment.Contains('~') || segment.Contains(':'))
            {
                throw new InvalidOperationException("Non-canonical Windows path alias.");
            }
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
