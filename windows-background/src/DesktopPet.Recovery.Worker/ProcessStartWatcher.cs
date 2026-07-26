using System.Threading.Channels;
using Wx411.Core;

namespace DesktopPet.Recovery.Worker;

public sealed class ProcessStartWatcher : IRecoveryHintSource
{
    private readonly Func<IReadOnlyList<int>> _snapshotProcessIds;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _timeProvider;

    public ProcessStartWatcher()
        : this(
            static () => TargetProcessDiscovery.Discover()
                .Select(process => process.Pid)
                .ToArray(),
            TimeSpan.FromSeconds(1),
            TimeProvider.System)
    {
    }

    internal ProcessStartWatcher(
        Func<IReadOnlyList<int>> snapshotProcessIds,
        TimeSpan pollInterval,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshotProcessIds);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _snapshotProcessIds = snapshotProcessIds;
        _pollInterval = pollInterval;
        _timeProvider = timeProvider;
    }

    public async Task RunAsync(
        ChannelWriter<RecoveryHint> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var previous = Snapshot();
        await Task.Delay(_pollInterval, _timeProvider, cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Snapshot();
            foreach (var processId in current
                         .Where(processId => !previous.Contains(processId))
                         .OrderBy(processId => processId))
            {
                await writer.WriteAsync(
                    new RecoveryHint(RecoveryHintKind.ProcessStarted, processId),
                    cancellationToken);
            }
            previous = current;
            await Task.Delay(_pollInterval, _timeProvider, cancellationToken);
        }
    }

    private HashSet<int> Snapshot() =>
        _snapshotProcessIds()
            .Where(processId => processId > 0)
            .ToHashSet();
}
