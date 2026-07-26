using System.Threading.Channels;

namespace DesktopPet.Recovery.Worker;

public sealed class KnownRootDatabaseWatcher : IRecoveryHintSource
{
    private readonly IReadOnlyList<string> _knownRoots;

    public KnownRootDatabaseWatcher(IReadOnlyList<string> knownRoots)
    {
        ArgumentNullException.ThrowIfNull(knownRoots);
        _knownRoots = Array.AsReadOnly(knownRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public async Task RunAsync(
        ChannelWriter<RecoveryHint> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var watchers = new List<FileSystemWatcher>();
        try
        {
            foreach (var root in _knownRoots.Where(Directory.Exists))
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName |
                        NotifyFilters.DirectoryName |
                        NotifyFilters.Size |
                        NotifyFilters.LastWrite,
                    Filter = "*",
                };
                FileSystemEventHandler changed = (_, eventArgs) =>
                    PublishIfDatabase(eventArgs.FullPath, writer);
                RenamedEventHandler renamed = (_, eventArgs) =>
                    PublishIfDatabase(eventArgs.FullPath, writer);
                ErrorEventHandler error = (_, _) =>
                    writer.TryWrite(new RecoveryHint(RecoveryHintKind.Reconciliation));
                watcher.Created += changed;
                watcher.Changed += changed;
                watcher.Deleted += changed;
                watcher.Renamed += renamed;
                watcher.Error += error;
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            foreach (var watcher in watchers) watcher.Dispose();
        }
    }

    private static void PublishIfDatabase(
        string path,
        ChannelWriter<RecoveryHint> writer)
    {
        if (IsDatabasePath(path))
            writer.TryWrite(new RecoveryHint(RecoveryHintKind.DatabaseChanged));
    }

    private static bool IsDatabasePath(string path) =>
        path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase);
}
