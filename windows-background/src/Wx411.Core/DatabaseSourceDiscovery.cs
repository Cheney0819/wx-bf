using System.Security.Cryptography;

namespace Wx411.Core;

public sealed record DatabaseSource(string Path, long Length)
{
    public bool IsRequired => DatabaseSourceDiscovery.IsRequiredDatabasePath(Path);
}

public static class DatabaseSourceDiscovery
{
    public static IReadOnlyList<DatabaseSource> Discover() => Discover(DefaultRoots(), default);

    public static IReadOnlyList<DatabaseSource> Discover(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        cancellationToken.ThrowIfCancellationRequested();

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var path in Directory.EnumerateFiles(root, "*.db", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(path);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Discovery isolates inaccessible configured roots.
            }
            catch (IOException)
            {
                // A transient root failure does not block other configured roots.
            }
        }

        var databases = new List<DatabaseSource>();
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 16 ||
                    CipherProfileProbe.CandidateProfilesFor(info.Length).Count == 0)
                {
                    continue;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var header = new byte[16];
                try
                {
                    var read = stream.Read(header, 0, header.Length);
                    if (read == header.Length && header.AsSpan().SequenceEqual("SQLite format 3\0"u8))
                        continue;
                    databases.Add(new DatabaseSource(path, info.Length));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(header);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Discovery isolates an inaccessible database candidate.
            }
            catch (IOException)
            {
                // A changing or unavailable candidate is retried by later reconciliation.
            }
        }

        databases.Sort((left, right) =>
        {
            var priority = Priority(left.Path).CompareTo(Priority(right.Path));
            return priority != 0
                ? priority
                : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
        });
        return Array.AsReadOnly(databases.ToArray());
    }

    internal static bool IsRequiredDatabasePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var fileSeparator = normalized.LastIndexOf('/');
        if (fileSeparator <= 0 || fileSeparator == normalized.Length - 1)
            return false;

        var parentSeparator = normalized.LastIndexOf('/', fileSeparator - 1);
        var directory = normalized[(parentSeparator + 1)..fileSeparator];
        var fileName = normalized[(fileSeparator + 1)..];
        if (directory.Equals("session", StringComparison.OrdinalIgnoreCase))
        {
            return fileName.Equals("session.db", StringComparison.OrdinalIgnoreCase);
        }

        return directory.Equals("message", StringComparison.OrdinalIgnoreCase) &&
            IsNumberedMessageDatabase(fileName);
    }

    private static bool IsNumberedMessageDatabase(string fileName)
    {
        const string prefix = "message_";
        const string suffix = ".db";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var numberLength = fileName.Length - prefix.Length - suffix.Length;
        if (numberLength <= 0) return false;
        for (var index = prefix.Length; index < prefix.Length + numberLength; index++)
        {
            if (fileName[index] is < '0' or > '9') return false;
        }
        return true;
    }

    private static IEnumerable<string> DefaultRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            roots.Add(Path.Combine(profile, "Documents", "xwechat_files"));
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
            roots.Add(Path.Combine(documents, "xwechat_files"));
        return roots;
    }

    private static int Priority(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("message", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals("session.db", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Equals("contact.db", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Equals("key_info.db", StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }
}
