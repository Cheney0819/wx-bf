using System.Text;
using Wx411.Core;
using Wx411.Core.Windows;

namespace DesktopPet.Recovery;

public sealed record WeChatDataRootResolution(
    string? DataRoot,
    int CandidateCount,
    int DatabaseCount,
    string Code,
    WeChatRuntimeIdentity? RuntimeIdentity = null)
{
    public bool Found => DataRoot is not null;
}

public interface IWeChatDataRootLocator
{
    string? CurrentDataRoot { get; }

    Task<WeChatDataRootResolution> LocateAsync(
        CancellationToken cancellationToken);

    Task<WeChatDataRootResolution> LocateAsync(
        WeChatRuntimeIdentity runtime,
        CancellationToken cancellationToken) =>
        LocateAsync(cancellationToken);
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
    private readonly Func<int, string, bool> _processOwnsDatabase;
    private readonly object _selectionGate = new();
    private string? _currentDataRoot;

    public WeChatDataRootLocator()
        : this(DefaultSearchRoots(), DefaultDriveRoots())
    {
    }

    public WeChatDataRootLocator(
        IEnumerable<string> searchRoots,
        IEnumerable<string> driveRoots)
        : this(searchRoots, driveRoots, ProcessOwnsDatabase)
    {
    }

    internal WeChatDataRootLocator(
        IEnumerable<string> searchRoots,
        IEnumerable<string> driveRoots,
        Func<int, string, bool> processOwnsDatabase)
    {
        ArgumentNullException.ThrowIfNull(searchRoots);
        ArgumentNullException.ThrowIfNull(driveRoots);
        ArgumentNullException.ThrowIfNull(processOwnsDatabase);
        _searchRoots = NormalizeRoots(searchRoots);
        _driveRoots = NormalizeRoots(driveRoots);
        _processOwnsDatabase = processOwnsDatabase;
    }

    public string? CurrentDataRoot
    {
        get
        {
            lock (_selectionGate) return _currentDataRoot;
        }
    }

    public Task<WeChatDataRootResolution> LocateAsync(
        CancellationToken cancellationToken) =>
        LocateCoreAsync(runtime: null, cancellationToken);

    public Task<WeChatDataRootResolution> LocateAsync(
        WeChatRuntimeIdentity runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(runtime));
        return LocateCoreAsync(runtime, cancellationToken);
    }

    private Task<WeChatDataRootResolution> LocateCoreAsync(
        WeChatRuntimeIdentity? runtime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ConfiguredRoots())
        {
            DiscoverBelow(root, MaximumSearchDepth, configuredAccounts, cancellationToken);
        }
        accounts.UnionWith(configuredAccounts);
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
                databases,
                configuredAccounts.Contains(account)));
        }

        var processCandidates = runtime is null
            ? []
            : candidates.Where(candidate => candidate.Databases.Any(database =>
                _processOwnsDatabase(runtime.ProcessId, database.Path))).ToArray();
        var configuredCandidates = candidates
            .Where(candidate => candidate.IsConfigured)
            .ToArray();
        var selected = processCandidates.Length == 1
            ? processCandidates[0]
            : processCandidates.Length == 0 && configuredCandidates.Length == 1
                ? configuredCandidates[0]
                : processCandidates.Length == 0 &&
                  configuredCandidates.Length == 0 &&
                  candidates.Count == 1
                    ? candidates[0]
                    : null;
        lock (_selectionGate)
        {
            _currentDataRoot = selected?.Path;
        }

        return Task.FromResult(selected is null
            ? new WeChatDataRootResolution(
                null,
                candidates.Count,
                0,
                candidates.Count == 0
                    ? "data_root_missing"
                    : "ambiguous_data_root",
                runtime)
            : new WeChatDataRootResolution(
                selected.Path,
                candidates.Count,
                selected.Databases.Count,
                processCandidates.Length == 1
                    ? "data_root_bound_to_process"
                    : "data_root_discovered",
                runtime));
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

    private static bool IsAccountRoot(string path)
    {
        var storage = Path.Combine(path, "db_storage");
        if (!File.Exists(Path.Combine(storage, "session", "session.db")))
            return false;

        return ContainsDatabase(Path.Combine(storage, "message")) ||
            ContainsDatabase(Path.Combine(storage, "biz_message"));
    }

    private static bool ContainsDatabase(string path)
    {
        try
        {
            return Directory.Exists(path) &&
                Directory.EnumerateFiles(path, "*.db", SearchOption.TopDirectoryOnly).Any();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

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

    private static IReadOnlyList<string> ConfiguredRoots()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appData)) return [];
        var configRoot = Path.Combine(appData, "Tencent", "xwechat", "config");
        if (!Directory.Exists(configRoot)) return [];

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateIniFiles(configRoot))
        {
            var value = ReadConfiguredRoot(path);
            if (string.IsNullOrWhiteSpace(value) ||
                value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                continue;
            }

            try
            {
                var normalized = NormalizePath(value);
                if (Directory.Exists(normalized)) roots.Add(normalized);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }
        return roots.OrderBy(root => root, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> EnumerateIniFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.ini", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadConfiguredRoot(string path)
    {
        byte[]? bytes = null;
        try
        {
            bytes = File.ReadAllBytes(path);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes).Trim();
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(
                    936,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(bytes).Trim();
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (bytes is not null) Array.Clear(bytes);
        }
    }

    private static IReadOnlyList<string> NormalizeRoots(
        IEnumerable<string> roots) =>
        roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool ProcessOwnsDatabase(int pid, string databasePath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return ProcessFileHandleFinder.FindProcessIdsHoldingFile(databasePath, [pid])
                .Contains(pid);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

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
        IReadOnlyList<DatabaseSource> Databases,
        bool IsConfigured);
}
