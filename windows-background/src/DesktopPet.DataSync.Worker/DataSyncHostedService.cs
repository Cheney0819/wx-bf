using Microsoft.Extensions.Hosting;

namespace DesktopPet.DataSync.Worker;

public sealed record DataSyncHostOptions(DataSyncRunMode RunMode);

public sealed class DataSyncHostedService : BackgroundService
{
    private readonly DataSyncWorker _worker;
    private readonly DataSyncRunMode _mode;
    private readonly IHostApplicationLifetime _lifetime;

    public DataSyncHostedService(
        DataSyncWorker worker,
        DataSyncHostOptions options,
        IHostApplicationLifetime lifetime)
    {
        _worker = worker;
        _mode = options.RunMode;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _worker.RunAsync(_mode, stoppingToken);
        if (_mode == DataSyncRunMode.Once) _lifetime.StopApplication();
    }
}
