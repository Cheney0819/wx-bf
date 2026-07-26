using Microsoft.Data.Sqlite;

namespace DesktopPet.Recovery.Persistence;

internal static class RecoverySchema
{
    internal const int CurrentVersion = 1;

    internal static async Task EnsureCreatedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL,
                applied_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS recovery_epoch (
                id TEXT PRIMARY KEY,
                executable_version TEXT NOT NULL,
                data_root_identity TEXT NOT NULL,
                restart_count INTEGER NOT NULL DEFAULT 0 CHECK (restart_count BETWEEN 0 AND 2),
                active_restart_suppressed INTEGER NOT NULL DEFAULT 0 CHECK (active_restart_suppressed IN (0, 1)),
                mode TEXT NOT NULL,
                failure_code TEXT NULL,
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_recovery_epoch_single_active
            ON recovery_epoch(is_active)
            WHERE is_active = 1;

            CREATE TABLE IF NOT EXISTS database_generation (
                id TEXT PRIMARY KEY,
                epoch_id TEXT NOT NULL REFERENCES recovery_epoch(id),
                relative_path TEXT NOT NULL,
                content_fingerprint TEXT NOT NULL,
                status TEXT NOT NULL,
                output_path TEXT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS runtime_event (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var version = connection.CreateCommand();
        version.CommandText = """
            INSERT INTO schema_info(version, applied_at_utc)
            SELECT $version, $now
            WHERE NOT EXISTS (SELECT 1 FROM schema_info);
            """;
        version.Parameters.AddWithValue("$version", CurrentVersion);
        version.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await version.ExecuteNonQueryAsync(cancellationToken);
    }
}
