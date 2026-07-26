using DesktopPet.Recovery.Persistence;
using Microsoft.Data.Sqlite;

namespace DesktopPet.Recovery.Worker;

public sealed class RecoveryBootstrapper : IRecoveryStartup
{
    private readonly RecoveryRepository _repository;
    private readonly ICriticalRecoverySnapshotStore _snapshotStore;

    public RecoveryBootstrapper(
        RecoveryRepository repository,
        ICriticalRecoverySnapshotStore snapshotStore)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        _repository = repository;
        _snapshotStore = snapshotStore;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _snapshotStore.LoadAsync(cancellationToken);
        try
        {
            await _repository.InitializeAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            if (snapshot is null)
            {
                throw new InvalidDataException(
                    "Recovery state is corrupt and no critical snapshot is available.",
                    exception);
            }
            QuarantineStateDatabase(_repository.DatabasePath);
            await _repository.InitializeAsync(cancellationToken);
        }

        if (snapshot is not null)
            await _repository.ReconcileCriticalStateAsync(snapshot, cancellationToken);
    }

    private static void QuarantineStateDatabase(string databasePath)
    {
        var suffix = $".corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid():N}";
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path)) File.Move(path, path + suffix, overwrite: false);
        }
    }
}
