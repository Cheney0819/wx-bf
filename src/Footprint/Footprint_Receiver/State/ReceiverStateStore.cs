using Footprint.Receiver.Internal;
using Footprint.Receiver.Network;
using Microsoft.Data.Sqlite;

namespace Footprint.Receiver.State;

public interface IReceiverStateStore
{
    Task<bool> HasReceivedAsync(PendingRun run, CancellationToken cancellationToken);
    Task RecordReceivedIntentAsync(PendingRun run, CancellationToken cancellationToken);
    Task RecordCompletedIntentAsync(PendingRun run, CancellationToken cancellationToken) => RecordReceivedIntentAsync(run, cancellationToken);
    Task RecordStatusIntentAsync(string runId, string status, CancellationToken cancellationToken);
    Task EnsureReceivedStatusAsync(string runId, CancellationToken cancellationToken);
    Task EnsureCompletedStatusAsync(string runId, CancellationToken cancellationToken) => EnsureReceivedStatusAsync(runId, cancellationToken);
    Task<IReadOnlyList<ReceiverStatusUpdate>> GetPendingStatusReportsAsync(CancellationToken cancellationToken);
    Task AcknowledgeStatusAsync(ReceiverStatusUpdate update, CancellationToken cancellationToken);
}

public sealed class ReceiverStateStore : IReceiverStateStore
{
    private readonly string _path;
    private readonly string _connectionString;

    public ReceiverStateStore(string path)
    {
        _path = Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private }.ToString();
    }

    public async Task<bool> HasReceivedAsync(PendingRun run, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT package_length, package_sha256 FROM received_runs WHERE run_id=$run";
        command.Parameters.AddWithValue("$run", run.RunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return false;
        if (reader.GetInt64(0) != run.PackageLength || !string.Equals(reader.GetString(1), run.PackageSha256, StringComparison.Ordinal)) throw new InvalidDataException("相同 RunId 的已接收包元数据冲突。");
        return true;
    }

    public async Task RecordReceivedIntentAsync(PendingRun run, CancellationToken cancellationToken)
        => await RecordTerminalIntentAsync(run, "已接收", cancellationToken).ConfigureAwait(false);

    public async Task RecordCompletedIntentAsync(PendingRun run, CancellationToken cancellationToken)
        => await RecordTerminalIntentAsync(run, "已完成", cancellationToken).ConfigureAwait(false);

    private async Task RecordTerminalIntentAsync(PendingRun run, string status, CancellationToken cancellationToken)
    {
        PackageIdentity.Validate(run.RunId, run.PackageLength, run.PackageSha256);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO received_runs(run_id,package_length,package_sha256,received_at_utc) VALUES($run,$length,$sha,$utc) ON CONFLICT(run_id) DO UPDATE SET package_length=excluded.package_length,package_sha256=excluded.package_sha256 WHERE received_runs.package_length=excluded.package_length AND received_runs.package_sha256=excluded.package_sha256; SELECT changes();";
            command.Parameters.AddWithValue("$run", run.RunId); command.Parameters.AddWithValue("$length", run.PackageLength); command.Parameters.AddWithValue("$sha", run.PackageSha256); command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1) throw new InvalidDataException("相同 RunId 的包元数据冲突。");
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO status_outbox(run_id,status,message_zh) VALUES($run,$status,'') ON CONFLICT(run_id) DO UPDATE SET status=excluded.status,message_zh=excluded.message_zh";
            command.Parameters.AddWithValue("$run", run.RunId);
            command.Parameters.AddWithValue("$status", status);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordStatusIntentAsync(string runId, string status, CancellationToken cancellationToken)
    {
        PackageIdentity.ValidateRunId(runId);
        if (status is not ("已接收" or "失败")) throw new ArgumentException("接收状态无效。", nameof(status));
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO status_outbox(run_id,status,message_zh) VALUES($run,$status,'') ON CONFLICT(run_id) DO UPDATE SET status=excluded.status,message_zh='' WHERE status_outbox.status<>'已接收' OR excluded.status='已接收'";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$status", status);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureReceivedStatusAsync(string runId, CancellationToken cancellationToken)
        => await EnsureTerminalStatusAsync(runId, "已接收", cancellationToken).ConfigureAwait(false);

    public async Task EnsureCompletedStatusAsync(string runId, CancellationToken cancellationToken)
        => await EnsureTerminalStatusAsync(runId, "已完成", cancellationToken).ConfigureAwait(false);

    private async Task EnsureTerminalStatusAsync(string runId, string status, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO status_outbox(run_id,status,message_zh) SELECT run_id,$status,'' FROM received_runs WHERE run_id=$run ON CONFLICT(run_id) DO UPDATE SET status=$status,message_zh=''";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$status", status);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReceiverStatusUpdate>> GetPendingStatusReportsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,status,message_zh FROM status_outbox ORDER BY rowid";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<ReceiverStatusUpdate>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new ReceiverStatusUpdate(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public async Task AcknowledgeStatusAsync(ReceiverStatusUpdate update, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM status_outbox WHERE run_id=$run AND status=$status";
        command.Parameters.AddWithValue("$run", update.RunId); command.Parameters.AddWithValue("$status", update.Status);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("状态路径缺少父目录。");
        UnixDurability.SecureDirectory(directory);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        UnixDurability.SecureFile(_path);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS received_runs(run_id TEXT PRIMARY KEY,package_length INTEGER NOT NULL,package_sha256 TEXT NOT NULL,received_at_utc TEXT NOT NULL); CREATE TABLE IF NOT EXISTS status_outbox(run_id TEXT PRIMARY KEY,status TEXT NOT NULL,message_zh TEXT NOT NULL); UPDATE status_outbox SET status='已接收',message_zh='' WHERE status='Received';";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        UnixDurability.FlushDirectory(directory);
        return connection;
    }
}
