using DesktopPet.Background.Contracts;
using DesktopPet.Background.Infrastructure;
using DesktopPet.Recovery.Persistence;

namespace DesktopPet.Recovery.Tests;

public sealed class RecoveryRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-recovery-repository-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RestartBudgetSurvivesRepositoryReopen()
    {
        var path = DatabasePath();
        string epochId;
        await using (var first = await OpenRepositoryAsync(path))
        {
            var epoch = await first.BeginOrLoadEpochAsync(
                new RecoveryEpochIdentity("4.1.0", "root-a"),
                explicitRetry: false,
                default);
            epochId = epoch.Id;
            Assert.True(await first.TryConsumeRestartAsync(epoch.Id, default));
            Assert.True(await first.TryConsumeRestartAsync(epoch.Id, default));
            Assert.False(await first.TryConsumeRestartAsync(epoch.Id, default));
        }

        await using var reopened = await OpenRepositoryAsync(path);
        Assert.False(await reopened.TryConsumeRestartAsync(epochId, default));
        Assert.Equal(2, (await reopened.GetEpochAsync(epochId, default))!.RestartCount);
    }

    [Fact]
    public async Task IdenticalIdentityReusesActiveEpoch()
    {
        await using var repository = await OpenRepositoryAsync(DatabasePath());
        var identity = new RecoveryEpochIdentity("4.1.0", "root-a");

        var first = await repository.BeginOrLoadEpochAsync(identity, false, default);
        var second = await repository.BeginOrLoadEpochAsync(identity, false, default);

        Assert.Equal(first.Id, second.Id);
    }

    [Theory]
    [InlineData("4.1.1", "root-a")]
    [InlineData("4.1.0", "root-b")]
    public async Task MeaningfulIdentityChangeCreatesEpoch(
        string executableVersion,
        string dataRootIdentity)
    {
        await using var repository = await OpenRepositoryAsync(DatabasePath());
        var first = await repository.BeginOrLoadEpochAsync(
            new RecoveryEpochIdentity("4.1.0", "root-a"), false, default);
        var second = await repository.BeginOrLoadEpochAsync(
            new RecoveryEpochIdentity(executableVersion, dataRootIdentity), false, default);

        Assert.NotEqual(first.Id, second.Id);
        Assert.False((await repository.GetEpochAsync(first.Id, default))!.IsActive);
        Assert.True(second.IsActive);
    }

    [Fact]
    public async Task ExplicitRetryCreatesEpochWithoutDeletingHistory()
    {
        await using var repository = await OpenRepositoryAsync(DatabasePath());
        var identity = new RecoveryEpochIdentity("4.1.0", "root-a");
        var first = await repository.BeginOrLoadEpochAsync(identity, false, default);
        var second = await repository.BeginOrLoadEpochAsync(identity, true, default);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotNull(await repository.GetEpochAsync(first.Id, default));
    }

    [Fact]
    public async Task ValidatedKeySuppressesAllFurtherRestarts()
    {
        await using var repository = await OpenRepositoryAsync(DatabasePath());
        var epoch = await repository.BeginOrLoadEpochAsync(
            new RecoveryEpochIdentity("4.1.0", "root-a"), false, default);

        await repository.MarkKeyAvailableAsync(epoch.Id, default);

        Assert.False(await repository.TryConsumeRestartAsync(epoch.Id, default));
        var updated = await repository.GetEpochAsync(epoch.Id, default);
        Assert.True(updated!.ActiveRestartSuppressed);
        Assert.Equal(RecoveryMode.KeyMaterialAvailable, updated.Mode);
    }

    [Fact]
    public async Task PendingCaptureSuppressesAllFurtherRestarts()
    {
        await using var repository = await OpenRepositoryAsync(DatabasePath());
        var epoch = await repository.BeginOrLoadEpochAsync(
            new RecoveryEpochIdentity("4.1.0", "root-a"), false, default);

        await repository.MarkPendingAvailableAsync(epoch.Id, default);

        Assert.False(await repository.TryConsumeRestartAsync(epoch.Id, default));
        var updated = await repository.GetEpochAsync(epoch.Id, default);
        Assert.True(updated!.ActiveRestartSuppressed);
        Assert.Equal(RecoveryMode.PassiveWaiting, updated.Mode);
    }

    [Fact]
    public async Task CircuitOpensAfterTwoConsumedRestarts()
    {
        await using var repository = await OpenRepositoryAsync(DatabasePath());
        var epoch = await repository.BeginOrLoadEpochAsync(
            new RecoveryEpochIdentity("4.1.0", "root-a"), false, default);
        Assert.True(await repository.TryConsumeRestartAsync(epoch.Id, default));
        Assert.True(await repository.TryConsumeRestartAsync(epoch.Id, default));

        await repository.OpenCircuitAsync(epoch.Id, "zero_key", default);

        var updated = await repository.GetEpochAsync(epoch.Id, default);
        Assert.Equal(RecoveryMode.CaptureCircuitOpen, updated!.Mode);
        Assert.True(updated.ActiveRestartSuppressed);
        Assert.Equal("zero_key", updated.FailureCode);
    }

    [Fact]
    public async Task RestartSnapshotIsDurableBeforeDatabaseBudgetMutation()
    {
        var path = DatabasePath();
        var observedDatabaseCounts = new List<int>();
        var snapshot = new CallbackSnapshotStore(async state =>
        {
            if (state.Mode != RecoveryMode.RestartingForCapture) return;
            await using var connection = await SqliteConnectionFactory.OpenAsync(
                path,
                readOnly: true,
                default);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT restart_count FROM recovery_epoch WHERE id = $id;";
            command.Parameters.AddWithValue("$id", state.EpochId);
            observedDatabaseCounts.Add(Convert.ToInt32(await command.ExecuteScalarAsync()));
        });
        await using var repository = new RecoveryRepository(path, TimeProvider.System, snapshot);
        await repository.InitializeAsync(default);
        var epoch = await repository.BeginOrLoadEpochAsync(
            new RecoveryEpochIdentity("4.1.0", "root-a"), false, default);

        Assert.True(await repository.TryConsumeRestartAsync(epoch.Id, default));

        Assert.Equal(new[] { 0 }, observedDatabaseCounts);
        Assert.Equal(1, snapshot.States.Last().RestartCount);
    }

    [Fact]
    public async Task RuntimeDiagnosticsRetainOnlyNewestTwoHundredEvents()
    {
        await using var repository = await OpenRepositoryAsync(DatabasePath());
        for (var index = 0; index < 205; index++)
        {
            await repository.RecordRuntimeEventAsync(
                "test_event",
                $"{{\"index\":{index}}}",
                default);
        }

        var events = await repository.GetRecentRuntimeEventsAsync(200, default);
        await using var connection = await SqliteConnectionFactory.OpenAsync(
            DatabasePath(),
            readOnly: true,
            default);
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM runtime_event;";

        Assert.Equal(200, events.Count);
        Assert.Equal(200L, (long)(await countCommand.ExecuteScalarAsync())!);
        Assert.Contains("204", events[0].PayloadJson);
        Assert.Contains("5", events[^1].PayloadJson);
    }

    [Fact]
    public async Task InvalidCriticalSnapshotCannotIncreaseRestartBudgetPastLimit()
    {
        await using var repository = await OpenRepositoryAsync(DatabasePath());
        var state = new CriticalRecoveryState(
            "epoch-invalid",
            new("4.1.0", "root-a"),
            3,
            true,
            RecoveryMode.CaptureCircuitOpen,
            "invalid",
            DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.ReconcileCriticalStateAsync(state, default));
    }

    private string DatabasePath() => Path.Combine(_root, "recovery.db");

    private static async Task<RecoveryRepository> OpenRepositoryAsync(string path)
    {
        var repository = new RecoveryRepository(path, TimeProvider.System);
        await repository.InitializeAsync(default);
        return repository;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CallbackSnapshotStore : ICriticalRecoverySnapshotStore
    {
        private readonly Func<CriticalRecoveryState, Task> _onSave;

        internal CallbackSnapshotStore(Func<CriticalRecoveryState, Task> onSave) =>
            _onSave = onSave;

        internal List<CriticalRecoveryState> States { get; } = [];

        public async Task SaveAsync(
            CriticalRecoveryState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            States.Add(state);
            await _onSave(state);
        }

        public Task<CriticalRecoveryState?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CriticalRecoveryState?>(null);
    }
}
