using Footprint.Core.Capture;
using Footprint.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace Footprint.Core.State;

public sealed class SqliteFootprintStateStore(string databasePath) : IFootprintStateStore
{
    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Footprint_StageCheckpoint(
                RunId TEXT PRIMARY KEY NOT NULL,
                Stage INTEGER NOT NULL,
                Step TEXT NOT NULL,
                Version INTEGER NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Footprint_Run(
                RunId TEXT PRIMARY KEY NOT NULL,
                DeviceId TEXT NOT NULL,
                AccountHash TEXT NOT NULL,
                WeixinDllSha256 TEXT NOT NULL,
                CaptureGeneration INTEGER NOT NULL,
                Stage INTEGER NOT NULL,
                Step TEXT NOT NULL,
                Status INTEGER NOT NULL,
                CheckpointVersion INTEGER NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                IsActive INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Footprint_Run_IsActive_UpdatedAtUtc
                ON Footprint_Run(IsActive, UpdatedAtUtc DESC);
            CREATE TABLE IF NOT EXISTS Footprint_DeviceState(
                DeviceId TEXT PRIMARY KEY NOT NULL,
                RestartPolicy INTEGER NOT NULL CHECK(RestartPolicy IN (0,1,2))
            );
            CREATE TABLE IF NOT EXISTS Footprint_RestartBudget(
                BudgetKey TEXT PRIMARY KEY NOT NULL,
                Reason TEXT NOT NULL,
                CommandId TEXT NULL,
                ConsumedAtUtc TEXT NOT NULL,
                CooldownUntilUtc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveCheckpointAsync(FootprintCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Footprint_StageCheckpoint(RunId, Stage, Step, Version, UpdatedAtUtc)
            VALUES($runId, $stage, $step, $version, $updated)
            ON CONFLICT(RunId) DO UPDATE SET
              Stage=excluded.Stage, Step=excluded.Step,
              Version=excluded.Version, UpdatedAtUtc=excluded.UpdatedAtUtc
            WHERE excluded.Version > Footprint_StageCheckpoint.Version;
            """;
        command.Parameters.AddWithValue("$runId", checkpoint.RunId);
        command.Parameters.AddWithValue("$stage", (int)checkpoint.Stage);
        command.Parameters.AddWithValue("$step", checkpoint.Step);
        command.Parameters.AddWithValue("$version", checkpoint.Version);
        command.Parameters.AddWithValue("$updated", checkpoint.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<FootprintCheckpoint?> LoadCheckpointAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Stage, Step, Version, UpdatedAtUtc
            FROM Footprint_StageCheckpoint WHERE RunId=$runId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new FootprintCheckpoint(
            runId,
            (FootprintStage)reader.GetInt32(0),
            reader.GetString(1),
            reader.GetInt64(2),
            DateTimeOffset.Parse(reader.GetString(3)));
    }

    public async Task SaveCaptureRunAsync(CaptureRunState state, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = "UPDATE Footprint_Run SET IsActive=0 WHERE IsActive=1 AND RunId<>$runId;";
            deactivate.Parameters.AddWithValue("$runId", state.RunId);
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Footprint_Run(
                RunId, DeviceId, AccountHash, WeixinDllSha256, CaptureGeneration,
                Stage, Step, Status, CheckpointVersion, UpdatedAtUtc, IsActive)
            VALUES(
                $runId, $deviceId, $accountHash, $weixinDllSha256, $captureGeneration,
                $stage, $step, $status, $checkpointVersion, $updatedAtUtc, 1)
            ON CONFLICT(RunId) DO UPDATE SET
                DeviceId=excluded.DeviceId,
                AccountHash=excluded.AccountHash,
                WeixinDllSha256=excluded.WeixinDllSha256,
                CaptureGeneration=excluded.CaptureGeneration,
                Stage=excluded.Stage,
                Step=excluded.Step,
                Status=excluded.Status,
                CheckpointVersion=excluded.CheckpointVersion,
                UpdatedAtUtc=excluded.UpdatedAtUtc,
                IsActive=1;
            """;
        command.Parameters.AddWithValue("$runId", state.RunId);
        command.Parameters.AddWithValue("$deviceId", state.Generation.DeviceId);
        command.Parameters.AddWithValue("$accountHash", state.Generation.AccountHash);
        command.Parameters.AddWithValue("$weixinDllSha256", state.Generation.WeixinDllSha256);
        command.Parameters.AddWithValue("$captureGeneration", state.Generation.CaptureGeneration);
        command.Parameters.AddWithValue("$stage", (int)state.Stage);
        command.Parameters.AddWithValue("$step", state.Step);
        command.Parameters.AddWithValue("$status", (int)state.Status);
        command.Parameters.AddWithValue("$checkpointVersion", state.CheckpointVersion);
        command.Parameters.AddWithValue("$updatedAtUtc", state.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CaptureRunState?> LoadActiveCaptureRunAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RunId, DeviceId, AccountHash, WeixinDllSha256, CaptureGeneration,
                   Stage, Step, Status, CheckpointVersion, UpdatedAtUtc
            FROM Footprint_Run
            WHERE IsActive=1
            ORDER BY UpdatedAtUtc DESC
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CaptureRunState(
            reader.GetString(0),
            new CaptureGenerationId(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4)),
            (FootprintStage)reader.GetInt32(5),
            reader.GetString(6),
            (CaptureStageStatus)reader.GetInt32(7),
            reader.GetInt64(8),
            DateTimeOffset.Parse(reader.GetString(9)));
    }

    public async Task<RestartPolicy> GetRestartPolicyAsync(string deviceId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RestartPolicy FROM Footprint_DeviceState WHERE DeviceId=$deviceId;";
        command.Parameters.AddWithValue("$deviceId", deviceId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result == DBNull.Value
            ? RestartPolicy.AutoOnce
            : ValidateRestartPolicy(Convert.ToInt32(result));
    }

    public async Task SetRestartPolicyAsync(string deviceId, RestartPolicy policy, CancellationToken cancellationToken)
    {
        EnsureDefinedRestartPolicy(policy);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Footprint_DeviceState(DeviceId, RestartPolicy)
            VALUES($deviceId, $restartPolicy)
            ON CONFLICT(DeviceId) DO UPDATE SET RestartPolicy=excluded.RestartPolicy;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$restartPolicy", (int)policy);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void EnsureDefinedRestartPolicy(RestartPolicy policy)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "重启策略无效。");
        }
    }

    private static RestartPolicy ValidateRestartPolicy(int value)
    {
        var policy = (RestartPolicy)value;
        if (!Enum.IsDefined(policy))
        {
            throw new InvalidOperationException("重启策略状态无效。");
        }

        return policy;
    }

    public async Task<bool> TryConsumeRestartBudgetAsync(RestartBudgetRecord record, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO Footprint_RestartBudget(
                BudgetKey, Reason, CommandId, ConsumedAtUtc, CooldownUntilUtc)
            VALUES($budgetKey, $reason, $commandId, $consumedAtUtc, $cooldownUntilUtc);
            """;
        command.Parameters.AddWithValue("$budgetKey", record.BudgetKey);
        command.Parameters.AddWithValue("$reason", record.Reason);
        command.Parameters.AddWithValue("$commandId", (object?)record.CommandId ?? DBNull.Value);
        command.Parameters.AddWithValue("$consumedAtUtc", record.ConsumedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$cooldownUntilUtc", record.CooldownUntilUtc.ToString("O"));
        var consumed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return consumed;
    }

    public async Task<RestartBudgetRecord?> LoadRestartBudgetAsync(string budgetKey, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BudgetKey, Reason, CommandId, ConsumedAtUtc, CooldownUntilUtc
            FROM Footprint_RestartBudget
            WHERE BudgetKey=$budgetKey;
            """;
        command.Parameters.AddWithValue("$budgetKey", budgetKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new RestartBudgetRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)));
    }
}
