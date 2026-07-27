using System.Threading.Channels;
using DesktopPet.DataSync.Upload;

namespace DesktopPet.DataSync.Worker;

public enum DataSyncRunMode
{
    Once,
    Continuous,
}

public enum DataSyncHintKind
{
    HandoffReady,
    Reconciliation,
}

public sealed record DataSyncHint(DataSyncHintKind Kind);

public sealed record DataSyncWorkerOptions(
    TimeSpan DebounceInterval,
    TimeSpan ReconciliationInterval,
    TimeSpan UploadPollInterval,
    int UploadConcurrency,
    TimeSpan HeartbeatInterval = default)
{
    public static DataSyncWorkerOptions Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(15),
        2,
        TimeSpan.FromSeconds(60));
}

public interface IDataSyncRuntime
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task ReconcileHandoffsAsync(CancellationToken cancellationToken);

    Task ReconcileTelemetryAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task EnqueueHeartbeatAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task<bool> ProcessOneParserJobAsync(CancellationToken cancellationToken);

    Task<UploadDisposition> UploadOneAsync(
        string workerId,
        CancellationToken cancellationToken);
}

public interface IDataSyncHintSource
{
    Task RunAsync(
        ChannelWriter<DataSyncHint> writer,
        CancellationToken cancellationToken);
}

public sealed class DataSyncWorker
{
    private readonly IDataSyncRuntime _runtime;
    private readonly IReadOnlyList<IDataSyncHintSource> _hintSources;
    private readonly DataSyncWorkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _workerId = $"datasync-{Guid.NewGuid():N}";

    public DataSyncWorker(
        IDataSyncRuntime runtime,
        IReadOnlyList<IDataSyncHintSource> hintSources,
        DataSyncWorkerOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(hintSources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (options.DebounceInterval < TimeSpan.Zero ||
            options.ReconciliationInterval <= TimeSpan.Zero ||
            options.UploadPollInterval <= TimeSpan.Zero ||
            (options.HeartbeatInterval != default && options.HeartbeatInterval <= TimeSpan.Zero) ||
            options.UploadConcurrency != 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        _runtime = runtime;
        _hintSources = hintSources;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task RunAsync(
        DataSyncRunMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            await _runtime.InitializeAsync(cancellationToken);
            if (mode == DataSyncRunMode.Once)
            {
                await EnqueueHeartbeatBestEffortAsync(cancellationToken);
                await ReconcileAndParseAsync(cancellationToken);
                await _runtime.ReconcileTelemetryAsync(cancellationToken);
                await PollUploadsAsync(cancellationToken);
                return;
            }
            await RunContinuouslyAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown leaves committed leases intact for expiry-based recovery.
        }
    }

    private async Task RunContinuouslyAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<DataSyncHint>(new BoundedChannelOptions(64)
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
        await EnqueueHeartbeatBestEffortAsync(cancellationToken);
        var heartbeatTask = RunHeartbeatLoopAsync(sourceCancellation.Token);
        var maintenanceTask = RunMaintenanceLoopAsync(sourceCancellation.Token);
        try
        {
            await ReconcileAndParseAsync(cancellationToken);
            var reconciliation = Delay(_options.ReconciliationInterval, cancellationToken);
            while (true)
            {
                var hintReady = channel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var completed = await Task.WhenAny(hintReady, reconciliation);
                if (completed == reconciliation)
                {
                    await ReconcileAndParseAsync(cancellationToken);
                    reconciliation = Delay(_options.ReconciliationInterval, cancellationToken);
                    continue;
                }
                if (!await hintReady) break;
                DrainHints(channel.Reader);
                await Delay(_options.DebounceInterval, cancellationToken);
                DrainHints(channel.Reader);
                await ReconcileAndParseAsync(cancellationToken);
            }
        }
        finally
        {
            sourceCancellation.Cancel();
            channel.Writer.TryComplete();
            await AwaitHintSourcesAsync(sourceTasks, sourceCancellation.Token);
            await AwaitHeartbeatAsync(heartbeatTask, sourceCancellation.Token);
            await AwaitHeartbeatAsync(maintenanceTask, sourceCancellation.Token);
        }
    }

    private TimeSpan EffectiveHeartbeatInterval =>
        _options.HeartbeatInterval == default
            ? TimeSpan.FromSeconds(60)
            : _options.HeartbeatInterval;

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Delay(EffectiveHeartbeatInterval, cancellationToken);
            await EnqueueHeartbeatBestEffortAsync(cancellationToken);
        }
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await ReconcileTelemetryBestEffortAsync(cancellationToken);
                await PollUploadsAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Retry telemetry and uploads on the next fixed cadence.
            }
            await Delay(_options.UploadPollInterval, cancellationToken);
        }
    }

    private async Task ReconcileTelemetryBestEffortAsync(CancellationToken cancellationToken)
    {
        try { await _runtime.ReconcileTelemetryAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { }
    }

    private async Task EnqueueHeartbeatBestEffortAsync(CancellationToken cancellationToken)
    {
        try { await _runtime.EnqueueHeartbeatAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { }
    }

    private static async Task AwaitHeartbeatAsync(Task heartbeat, CancellationToken cancellationToken)
    {
        try { await heartbeat; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ReconcileAndParseAsync(CancellationToken cancellationToken)
    {
        await _runtime.ReconcileHandoffsAsync(cancellationToken);
        while (await _runtime.ProcessOneParserJobAsync(cancellationToken))
            cancellationToken.ThrowIfCancellationRequested();
    }

    private Task PollUploadsAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(Enumerable.Range(0, _options.UploadConcurrency).Select(index =>
            _runtime.UploadOneAsync(
                $"{_workerId}-upload-{index}",
                cancellationToken)));

    private Task Delay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, _timeProvider, cancellationToken);

    private static void DrainHints(ChannelReader<DataSyncHint> reader)
    {
        while (reader.TryRead(out _)) { }
    }

    private static async Task RunHintSourceAsync(
        IDataSyncHintSource source,
        ChannelWriter<DataSyncHint> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await source.RunAsync(writer, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal source shutdown.
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
            throw;
        }
    }

    private static async Task AwaitHintSourcesAsync(
        IReadOnlyList<Task> sources,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0) return;
        try
        {
            await Task.WhenAll(sources);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal source shutdown.
        }
    }
}
