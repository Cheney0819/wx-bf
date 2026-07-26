using System.Threading.Channels;

namespace DesktopPet.DataSync.Worker;

public sealed class HandoffReadyWatcher : IDataSyncHintSource
{
    private readonly string _readyRoot;
    private readonly DataSyncHintKind _hintKind;

    public HandoffReadyWatcher(
        string readyRoot,
        DataSyncHintKind hintKind = DataSyncHintKind.HandoffReady)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readyRoot);
        _readyRoot = Path.GetFullPath(readyRoot);
        _hintKind = hintKind;
    }

    public async Task RunAsync(
        ChannelWriter<DataSyncHint> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Directory.CreateDirectory(_readyRoot);
        using var watcher = new FileSystemWatcher(_readyRoot, "*.json")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler changed = (_, _) =>
            writer.TryWrite(new DataSyncHint(_hintKind));
        RenamedEventHandler renamed = (_, _) =>
            writer.TryWrite(new DataSyncHint(_hintKind));
        ErrorEventHandler error = (_, _) =>
            writer.TryWrite(new DataSyncHint(DataSyncHintKind.Reconciliation));
        watcher.Created += changed;
        watcher.Changed += changed;
        watcher.Renamed += renamed;
        watcher.Error += error;
        watcher.EnableRaisingEvents = true;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
