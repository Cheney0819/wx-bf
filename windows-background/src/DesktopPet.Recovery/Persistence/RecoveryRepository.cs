using DesktopPet.Background.Contracts;
using DesktopPet.Background.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DesktopPet.Recovery.Persistence;

public sealed class RecoveryRepository : IRecoveryRepository
{
    private readonly string _databasePath;
    private readonly TimeProvider _timeProvider;
    private readonly ICriticalRecoverySnapshotStore? _criticalSnapshot;

    public RecoveryRepository(
        string databasePath,
        TimeProvider timeProvider,
        ICriticalRecoverySnapshotStore? criticalSnapshot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _databasePath = Path.GetFullPath(databasePath);
        _timeProvider = timeProvider;
        _criticalSnapshot = criticalSnapshot;
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await RecoverySchema.EnsureCreatedAsync(connection, cancellationToken);
    }

    public async Task<RecoveryEpoch> BeginOrLoadEpochAsync(
        RecoveryEpochIdentity identity,
        bool explicitRetry,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var active = await ReadActiveEpochAsync(connection, transaction, cancellationToken);
        if (!explicitRetry && active is not null && active.Identity == identity)
        {
            transaction.Commit();
            await SaveCriticalAsync(active, cancellationToken);
            return active;
        }

        var now = _timeProvider.GetUtcNow();
        if (active is not null)
        {
            await using var deactivate = connection.CreateCommand();
            deactivate.Transaction = transaction;
            deactivate.CommandText = """
                UPDATE recovery_epoch
                SET is_active = 0, updated_at_utc = $now
                WHERE id = $id;
                """;
            deactivate.Parameters.AddWithValue("$id", active.Id);
            deactivate.Parameters.AddWithValue("$now", now.ToString("O"));
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }

        var created = new RecoveryEpoch(
            Guid.NewGuid().ToString("N"),
            identity,
            RestartCount: 0,
            ActiveRestartSuppressed: false,
            RecoveryMode.PassiveWaiting,
            FailureCode: null,
            IsActive: true,
            CreatedAtUtc: now,
            UpdatedAtUtc: now);
        await InsertEpochAsync(connection, transaction, created, cancellationToken);
        transaction.Commit();
        await SaveCriticalAsync(created, cancellationToken);
        return created;
    }

    public async Task<RecoveryEpoch?> GetEpochAsync(
        string epochId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epochId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM recovery_epoch WHERE id = $id;";
        command.Parameters.AddWithValue("$id", epochId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEpoch(reader) : null;
    }

    public async Task<bool> TryConsumeRestartAsync(
        string epochId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epochId);
        var epoch = await GetEpochAsync(epochId, cancellationToken);
        if (epoch is null || !epoch.IsActive || epoch.ActiveRestartSuppressed || epoch.RestartCount >= 2)
            return false;

        var prospective = epoch with
        {
            RestartCount = epoch.RestartCount + 1,
            Mode = RecoveryMode.RestartingForCapture,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
        };
        await SaveCriticalAsync(prospective, cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE recovery_epoch
            SET restart_count = restart_count + 1,
                mode = 'RestartingForCapture',
                updated_at_utc = $now
            WHERE id = $id
              AND is_active = 1
              AND restart_count < 2
              AND active_restart_suppressed = 0;
            """;
        command.Parameters.AddWithValue("$id", epochId);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public Task MarkKeyAvailableAsync(
        string epochId,
        CancellationToken cancellationToken) =>
        SuppressAsync(
            epochId,
            RecoveryMode.KeyMaterialAvailable,
            failureCode: null,
            requireExhaustedBudget: false,
            cancellationToken);

    public Task MarkPendingAvailableAsync(
        string epochId,
        CancellationToken cancellationToken) =>
        SuppressAsync(
            epochId,
            RecoveryMode.PassiveWaiting,
            failureCode: null,
            requireExhaustedBudget: false,
            cancellationToken);

    public Task OpenCircuitAsync(
        string epochId,
        string failureCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return SuppressAsync(
            epochId,
            RecoveryMode.CaptureCircuitOpen,
            failureCode,
            requireExhaustedBudget: true,
            cancellationToken);
    }

    public async Task RecordGenerationAsync(
        DatabaseGenerationState generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO database_generation(
                id, epoch_id, relative_path, content_fingerprint, status, output_path, updated_at_utc)
            VALUES($id, $epoch_id, $relative_path, $fingerprint, $status, $output_path, $updated)
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status,
                output_path = excluded.output_path,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", generation.Id);
        command.Parameters.AddWithValue("$epoch_id", generation.EpochId);
        command.Parameters.AddWithValue("$relative_path", generation.RelativePath);
        command.Parameters.AddWithValue("$fingerprint", generation.ContentFingerprint);
        command.Parameters.AddWithValue("$status", generation.Status);
        command.Parameters.AddWithValue("$output_path", (object?)generation.OutputPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", generation.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DatabaseGenerationState?> GetGenerationAsync(
        string generationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM database_generation WHERE id = $id;";
        command.Parameters.AddWithValue("$id", generationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new DatabaseGenerationState(
            reader.GetString(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("epoch_id")),
            reader.GetString(reader.GetOrdinal("relative_path")),
            reader.GetString(reader.GetOrdinal("content_fingerprint")),
            reader.GetString(reader.GetOrdinal("status")),
            reader.IsDBNull(reader.GetOrdinal("output_path"))
                ? null
                : reader.GetString(reader.GetOrdinal("output_path")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at_utc"))));
    }

    public async Task RecordRuntimeEventAsync(
        string eventType,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        if (eventType.Length > 64 || eventType.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw new ArgumentException("Runtime event type is invalid.", nameof(eventType));
        if (payloadJson.Length > 4096)
            throw new ArgumentException("Runtime event payload exceeds 4096 characters.", nameof(payloadJson));

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO runtime_event(event_type, payload_json, created_at_utc)
                VALUES($event_type, $payload, $created);
                """;
            insert.Parameters.AddWithValue("$event_type", eventType);
            insert.Parameters.AddWithValue("$payload", payloadJson);
            insert.Parameters.AddWithValue("$created", _timeProvider.GetUtcNow().ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = """
                DELETE FROM runtime_event
                WHERE sequence NOT IN (
                    SELECT sequence FROM runtime_event
                    ORDER BY sequence DESC
                    LIMIT 200
                );
                """;
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    public async Task<IReadOnlyList<RecoveryRuntimeEvent>> GetRecentRuntimeEventsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, event_type, payload_json, created_at_utc
            FROM runtime_event
            ORDER BY sequence DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<RecoveryRuntimeEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new RecoveryRuntimeEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3))));
        }
        return Array.AsReadOnly(events.ToArray());
    }

    public async Task ReconcileCriticalStateAsync(
        CriticalRecoveryState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.EpochId);
        ValidateIdentity(state.Identity);
        if (state.RestartCount is < 0 or > RecoveryPolicy.MaximumActiveRestarts)
            throw new InvalidDataException("Critical snapshot restart count is invalid.");
        var existing = await GetEpochAsync(state.EpochId, cancellationToken);
        var restartCount = Math.Max(existing?.RestartCount ?? 0, state.RestartCount);
        var mode = MoreConservative(existing?.Mode, state.Mode);
        var suppressed = (existing?.ActiveRestartSuppressed ?? false) ||
            state.ActiveRestartSuppressed ||
            mode is RecoveryMode.KeyMaterialAvailable or RecoveryMode.CaptureCircuitOpen;
        var failureCode = mode == RecoveryMode.CaptureCircuitOpen
            ? existing?.FailureCode ?? state.FailureCode ?? "critical_snapshot_recovered"
            : existing?.FailureCode ?? state.FailureCode;
        var createdAt = existing?.CreatedAtUtc ?? state.UpdatedAtUtc;
        var updatedAt = existing is not null && existing.UpdatedAtUtc > state.UpdatedAtUtc
            ? existing.UpdatedAtUtc
            : state.UpdatedAtUtc;

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE recovery_epoch SET is_active = 0 WHERE id <> $id;";
            deactivate.Parameters.AddWithValue("$id", state.EpochId);
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO recovery_epoch(
                    id, executable_version, data_root_identity, restart_count,
                    active_restart_suppressed, mode, failure_code, is_active,
                    created_at_utc, updated_at_utc)
                VALUES(
                    $id, $version, $root, $restart_count,
                    $suppressed, $mode, $failure, 1,
                    $created, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    executable_version = excluded.executable_version,
                    data_root_identity = excluded.data_root_identity,
                    restart_count = excluded.restart_count,
                    active_restart_suppressed = excluded.active_restart_suppressed,
                    mode = excluded.mode,
                    failure_code = excluded.failure_code,
                    is_active = 1,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            upsert.Parameters.AddWithValue("$id", state.EpochId);
            upsert.Parameters.AddWithValue("$version", state.Identity.ExecutableVersion);
            upsert.Parameters.AddWithValue("$root", state.Identity.DataRootIdentity);
            upsert.Parameters.AddWithValue("$restart_count", restartCount);
            upsert.Parameters.AddWithValue("$suppressed", suppressed ? 1 : 0);
            upsert.Parameters.AddWithValue("$mode", mode.ToString());
            upsert.Parameters.AddWithValue("$failure", (object?)failureCode ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$created", createdAt.ToString("O"));
            upsert.Parameters.AddWithValue("$updated", updatedAt.ToString("O"));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        SqliteConnectionFactory.OpenAsync(_databasePath, readOnly: false, cancellationToken);

    private static RecoveryMode MoreConservative(
        RecoveryMode? existing,
        RecoveryMode snapshot)
    {
        if (existing is null) return snapshot;
        return ModePriority(existing.Value) >= ModePriority(snapshot)
            ? existing.Value
            : snapshot;
    }

    private static int ModePriority(RecoveryMode mode) => mode switch
    {
        RecoveryMode.CaptureCircuitOpen => 6,
        RecoveryMode.KeyMaterialAvailable => 5,
        RecoveryMode.Degraded => 4,
        RecoveryMode.RestartingForCapture => 3,
        RecoveryMode.CapturingCurrentProcess => 2,
        RecoveryMode.PassiveWaiting => 1,
        _ => 0,
    };

    private async Task SuppressAsync(
        string epochId,
        RecoveryMode mode,
        string? failureCode,
        bool requireExhaustedBudget,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epochId);
        var epoch = await GetEpochAsync(epochId, cancellationToken) ??
            throw new InvalidOperationException("Recovery epoch does not exist.");
        if (!epoch.IsActive || (requireExhaustedBudget && epoch.RestartCount != 2))
            throw new InvalidOperationException("Recovery epoch cannot enter the requested state.");

        var prospective = epoch with
        {
            ActiveRestartSuppressed = true,
            Mode = mode,
            FailureCode = failureCode,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
        };
        await SaveCriticalAsync(prospective, cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE recovery_epoch
            SET active_restart_suppressed = 1,
                mode = $mode,
                failure_code = $failure,
                updated_at_utc = $now
            WHERE id = $id
              AND is_active = 1
              {(requireExhaustedBudget ? "AND restart_count = 2" : string.Empty)};
            """;
        command.Parameters.AddWithValue("$id", epochId);
        command.Parameters.AddWithValue("$mode", mode.ToString());
        command.Parameters.AddWithValue("$failure", (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Recovery epoch cannot enter the requested state.");
    }

    private static async Task<RecoveryEpoch?> ReadActiveEpochAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM recovery_epoch WHERE is_active = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEpoch(reader) : null;
    }

    private static async Task InsertEpochAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecoveryEpoch epoch,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recovery_epoch(
                id, executable_version, data_root_identity, restart_count,
                active_restart_suppressed, mode, failure_code, is_active,
                created_at_utc, updated_at_utc)
            VALUES(
                $id, $version, $root, $restarts,
                $suppressed, $mode, $failure, $active,
                $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", epoch.Id);
        command.Parameters.AddWithValue("$version", epoch.Identity.ExecutableVersion);
        command.Parameters.AddWithValue("$root", epoch.Identity.DataRootIdentity);
        command.Parameters.AddWithValue("$restarts", epoch.RestartCount);
        command.Parameters.AddWithValue("$suppressed", epoch.ActiveRestartSuppressed ? 1 : 0);
        command.Parameters.AddWithValue("$mode", epoch.Mode.ToString());
        command.Parameters.AddWithValue("$failure", DBNull.Value);
        command.Parameters.AddWithValue("$active", epoch.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$created", epoch.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", epoch.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static RecoveryEpoch ReadEpoch(SqliteDataReader reader) =>
        new(
            reader.GetString(reader.GetOrdinal("id")),
            new RecoveryEpochIdentity(
                reader.GetString(reader.GetOrdinal("executable_version")),
                reader.GetString(reader.GetOrdinal("data_root_identity"))),
            reader.GetInt32(reader.GetOrdinal("restart_count")),
            reader.GetInt32(reader.GetOrdinal("active_restart_suppressed")) == 1,
            Enum.Parse<RecoveryMode>(reader.GetString(reader.GetOrdinal("mode"))),
            reader.IsDBNull(reader.GetOrdinal("failure_code"))
                ? null
                : reader.GetString(reader.GetOrdinal("failure_code")),
            reader.GetInt32(reader.GetOrdinal("is_active")) == 1,
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at_utc"))));

    private static void ValidateIdentity(RecoveryEpochIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ExecutableVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.DataRootIdentity);
    }

    private Task SaveCriticalAsync(
        RecoveryEpoch epoch,
        CancellationToken cancellationToken) =>
        _criticalSnapshot?.SaveAsync(
            new CriticalRecoveryState(
                epoch.Id,
                epoch.Identity,
                epoch.RestartCount,
                epoch.ActiveRestartSuppressed,
                epoch.Mode,
                epoch.FailureCode,
                epoch.UpdatedAtUtc),
            cancellationToken) ?? Task.CompletedTask;
}
