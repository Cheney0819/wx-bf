using System.Threading.Channels;
using DesktopPet.Recovery;

namespace DesktopPet.Recovery.Worker;

public sealed class SelectedRootDatabaseWatcher : IRecoveryHintSource
{
    private readonly IWeChatDataRootLocator _locator;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _timeProvider;

    public SelectedRootDatabaseWatcher(IWeChatDataRootLocator locator)
        : this(locator, TimeSpan.FromSeconds(5), TimeProvider.System)
    {
    }

    internal SelectedRootDatabaseWatcher(
        IWeChatDataRootLocator locator,
        TimeSpan pollInterval,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _locator = locator;
        _pollInterval = pollInterval;
        _timeProvider = timeProvider;
    }

    public async Task RunAsync(
        ChannelWriter<RecoveryHint> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var previous = SnapshotCurrent();
        await Task.Delay(_pollInterval, _timeProvider, cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = SnapshotCurrent();
            if (current != previous)
            {
                await writer.WriteAsync(
                    new RecoveryHint(RecoveryHintKind.DatabaseChanged),
                    cancellationToken);
            }
            previous = current;
            await Task.Delay(_pollInterval, _timeProvider, cancellationToken);
        }
    }

    private DatabaseSnapshot SnapshotCurrent()
    {
        var root = _locator.CurrentDataRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new DatabaseSnapshot(root, "");

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            var entries = Directory.EnumerateFiles(root, "*", options)
                .Where(IsDatabasePath)
                .Select(FileSignature)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            return new DatabaseSnapshot(
                Path.GetFullPath(root),
                string.Join('\n', entries));
        }
        catch (UnauthorizedAccessException)
        {
            return new DatabaseSnapshot(Path.GetFullPath(root), "unavailable");
        }
        catch (IOException)
        {
            return new DatabaseSnapshot(Path.GetFullPath(root), "unavailable");
        }
    }

    private static string FileSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (UnauthorizedAccessException)
        {
            return path + "|unavailable";
        }
        catch (IOException)
        {
            return path + "|unavailable";
        }
    }

    private static bool IsDatabasePath(string path) =>
        path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase);

    private sealed record DatabaseSnapshot(
        string? Root,
        string Fingerprint);
}

