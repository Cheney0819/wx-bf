using Wx411.Core;

namespace DesktopPet.Recovery;

public sealed record WeChatDataRootResolution(
    string? DataRoot,
    int CandidateCount,
    int DatabaseCount,
    string Code)
{
    public bool Found => DataRoot is not null;
}

public interface IWeChatDataRootLocator
{
    string? CurrentDataRoot { get; }

    Task<WeChatDataRootResolution> LocateAsync(
        CancellationToken cancellationToken);
}

public sealed class WeChatDataRootLocator : IWeChatDataRootLocator
{
    private const int MaximumSearchDepth = 3;
    private const int MaximumVisitedDirectories = 4096;
    private static readonly HashSet<string> ContainerNames = new(
        ["xwechat_files", "Weixin Files", "WeChat Files"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<string> _searchRoots;
    private readonly IReadOnlyList<string> _driveRoots;
    private readonly object _selectionGate = new();
    private string? _currentDataRoot;

    public WeChatDataRootLocator()
        : this(DefaultSearchRoots(), DefaultDriveRoots())
    {
    }

    public WeChatDataRootLocator(
        IEnumerable<string> searchRoots,
        IEnumerable<string> driveRoots)
    {
        ArgumentNullException.ThrowIfNull(searchRoots);
        ArgumentNullException.ThrowIfNull(driveRoots);
        _searchRoots = NormalizeRoots(searchRoots);
        _driveRoots = NormalizeRoots(driveRoots);
    }

    public string? CurrentDataRoot
    {
        get
        {
            lock (_selectionGate) return _currentDataRoot;
        }
    }

    public Task<WeChatDataRootResolution> LocateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _searchRoots)
        {
            DiscoverBelow(root, MaximumSearchDepth, accounts, cancellationToken);
        }
        foreach (var root in _driveRoots)
        {
            DiscoverBelow(root, MaximumSearchDepth, accounts, cancellationToken);
        }

        var candidates = new List<Candidate>();
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var databases = DatabaseSourceDiscovery.Discover([account]);
            if (databases.Count == 0) continue;
            candidates.Add(new Candidate(
                account,
                databases.Count,
                LatestWriteUtc(databases)));
        }

        var selected = candidates
            .OrderByDescending(candidate => candidate.LatestWriteUtc)
            .ThenByDescending(candidate => candidate.DatabaseCount)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        lock (_selectionGate)
        {
            _currentDataRoot = selected?.Path;
        }

        return Task.FromResult(selected is null
            ? new WeChatDataRootResolution(
                null,
                candidates.Count,
                0,
                "data_root_missing")
            : new WeChatDataRootResolution(
                selected.Path,
                candidates.Count,
                selected.DatabaseCount,
                "data_root_discovered"));
    }

    private static void DiscoverBelow(
        string root,
        int maximumDepth,
        ISet<string> accounts,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return;
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited < MaximumVisitedDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            visited++;
            if (IsAccountRoot(current.Path))
                accounts.Add(NormalizePath(current.Path));

            if (ContainerNames.Contains(Path.GetFileName(
                    Path.TrimEndingDirectorySeparator(current.Path))))
            {
                foreach (var account in EnumerateDirectories(current.Path))
                {
                    if (IsAccountRoot(account))
                        accounts.Add(NormalizePath(account));
                }
                continue;
            }

            if (current.Depth >= maximumDepth) continue;
            foreach (var directory in EnumerateDirectories(current.Path))
                pending.Enqueue((directory, current.Depth + 1));
        }
    }

    private static bool IsAccountRoot(string path) =>
        Directory.Exists(Path.Combine(path, "db_storage", "session"));

    private static IReadOnlyList<string> EnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(path => !IsReparsePoint(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static DateTime LatestWriteUtc(
        IReadOnlyList<DatabaseSource> databases)
    {
        var latest = DateTime.MinValue;
        foreach (var database in databases)
        {
            try
            {
                var value = File.GetLastWriteTimeUtc(database.Path);
                if (value > latest) latest = value;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }
        return latest;
    }

    private static IReadOnlyList<string> NormalizeRoots(
        IEnumerable<string> roots) =>
        roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static IEnumerable<string> DefaultSearchRoots()
    {
        var roots = new List<string>();
        AddEnvironmentRoot(roots, "USERPROFILE");
        AddEnvironmentRoot(roots, "OneDrive");
        AddEnvironmentRoot(roots, "OneDriveConsumer");
        AddSpecialFolder(roots, Environment.SpecialFolder.UserProfile);
        AddSpecialFolder(roots, Environment.SpecialFolder.MyDocuments);
        return roots;
    }

    private static IEnumerable<string> DefaultDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.IsReady &&
                    drive.DriveType is not DriveType.CDRom and not DriveType.Ram)
                .Select(drive => drive.RootDirectory.FullName)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddEnvironmentRoot(
        ICollection<string> roots,
        string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) roots.Add(value);
    }

    private static void AddSpecialFolder(
        ICollection<string> roots,
        Environment.SpecialFolder folder)
    {
        var value = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(value)) roots.Add(value);
    }

    private sealed record Candidate(
        string Path,
        int DatabaseCount,
        DateTime LatestWriteUtc);
}

