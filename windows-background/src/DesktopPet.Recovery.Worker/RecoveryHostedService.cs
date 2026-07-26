using Microsoft.Extensions.Hosting;

namespace DesktopPet.Recovery.Worker;

public sealed record RecoveryHostOptions(WorkerRunMode RunMode);

public sealed class RecoveryHostedService : BackgroundService
{
    private readonly RecoveryWorker _worker;
    private readonly WorkerRunMode _mode;
    private readonly IHostApplicationLifetime _lifetime;

    public RecoveryHostedService(
        RecoveryWorker worker,
        RecoveryHostOptions options,
        IHostApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(lifetime);
        _worker = worker;
        _mode = options.RunMode;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _worker.RunAsync(_mode, stoppingToken);
        if (_mode == WorkerRunMode.Once) _lifetime.StopApplication();
    }
}
