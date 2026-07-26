using System.Threading.Channels;

namespace DesktopPet.Recovery.Worker;

public enum WorkerRunMode
{
    Once,
    Continuous,
}

public enum RecoveryHintKind
{
    ProcessStarted,
    DatabaseChanged,
    Reconciliation,
}

public enum RecoveryCycleTrigger
{
    Startup,
    ProcessStarted,
    DatabaseChanged,
    Reconciliation,
}

public sealed record RecoveryHint(RecoveryHintKind Kind, int? ProcessId = null);

public sealed record RecoveryWorkerOptions(
    TimeSpan DebounceInterval,
    TimeSpan ReconciliationInterval)
{
    public static RecoveryWorkerOptions Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMinutes(5));
}

public interface IRecoveryStartup
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IRecoveryCycle
{
    Task<RecoveryAction> RunAsync(
        RecoveryCycleTrigger trigger,
        CancellationToken cancellationToken);
}

public interface IRecoveryHintSource
{
    Task RunAsync(
        ChannelWriter<RecoveryHint> writer,
        CancellationToken cancellationToken);
}

public sealed class RecoveryWorker
{
    private readonly IRecoveryStartup _startup;
    private readonly IRecoveryCycle _cycle;
    private readonly IReadOnlyList<IRecoveryHintSource> _hintSources;
    private readonly RecoveryWorkerOptions _options;
    private readonly TimeProvider _timeProvider;

    public RecoveryWorker(
        IRecoveryStartup startup,
        IRecoveryCycle cycle,
        IReadOnlyList<IRecoveryHintSource> hintSources,
        RecoveryWorkerOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(hintSources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (options.DebounceInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.ReconciliationInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        _startup = startup;
        _cycle = cycle;
        _hintSources = hintSources;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task RunAsync(
        WorkerRunMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            await _startup.InitializeAsync(cancellationToken);
            if (mode == WorkerRunMode.Once)
            {
                await _cycle.RunAsync(RecoveryCycleTrigger.Startup, cancellationToken);
                return;
            }

            await RunContinuouslyAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal worker shutdown never schedules or refunds recovery work.
        }
    }

    private async Task RunContinuouslyAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<RecoveryHint>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        using var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var sourceTasks = _hintSources
            .Select(source => RunHintSourceAsync(
                source,
                channel.Writer,
                sourceCancellation.Token))
            .ToArray();
        try
        {
            await _cycle.RunAsync(RecoveryCycleTrigger.Startup, cancellationToken);
            while (true)
            {
                var hintReady = channel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var reconciliation = Task.Delay(
                    _options.ReconciliationInterval,
                    _timeProvider,
                    cancellationToken);
                var completed = await Task.WhenAny(hintReady, reconciliation);
                if (completed == reconciliation)
                {
                    await _cycle.RunAsync(
                        RecoveryCycleTrigger.Reconciliation,
                        cancellationToken);
                    continue;
                }

                if (!await hintReady) break;
                var trigger = DrainTrigger(channel.Reader);
                await Task.Delay(
                    _options.DebounceInterval,
                    _timeProvider,
                    cancellationToken);
                trigger = MoreImportant(trigger, DrainTrigger(channel.Reader));
                await _cycle.RunAsync(trigger, cancellationToken);
            }
        }
        finally
        {
            sourceCancellation.Cancel();
            channel.Writer.TryComplete();
            await AwaitHintSourcesAsync(sourceTasks, sourceCancellation.Token);
        }
    }

    private static RecoveryCycleTrigger DrainTrigger(ChannelReader<RecoveryHint> reader)
    {
        var trigger = RecoveryCycleTrigger.Reconciliation;
        while (reader.TryRead(out var hint))
        {
            var candidate = hint.Kind switch
            {
                RecoveryHintKind.ProcessStarted => RecoveryCycleTrigger.ProcessStarted,
                RecoveryHintKind.DatabaseChanged => RecoveryCycleTrigger.DatabaseChanged,
                _ => RecoveryCycleTrigger.Reconciliation,
            };
            trigger = MoreImportant(trigger, candidate);
        }
        return trigger;
    }

    private static RecoveryCycleTrigger MoreImportant(
        RecoveryCycleTrigger left,
        RecoveryCycleTrigger right) =>
        TriggerPriority(left) >= TriggerPriority(right) ? left : right;

    private static int TriggerPriority(RecoveryCycleTrigger trigger) => trigger switch
    {
        RecoveryCycleTrigger.ProcessStarted => 3,
        RecoveryCycleTrigger.Startup => 2,
        RecoveryCycleTrigger.DatabaseChanged => 1,
        _ => 0,
    };

    private static async Task RunHintSourceAsync(
        IRecoveryHintSource source,
        ChannelWriter<RecoveryHint> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await source.RunAsync(writer, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
            throw;
        }
    }

    private static async Task AwaitHintSourcesAsync(
        IReadOnlyList<Task> sourceTasks,
        CancellationToken cancellationToken)
    {
        if (sourceTasks.Count == 0) return;
        try
        {
            await Task.WhenAll(sourceTasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
