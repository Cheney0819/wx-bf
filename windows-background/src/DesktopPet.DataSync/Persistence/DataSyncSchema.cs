using Microsoft.Data.Sqlite;

namespace DesktopPet.DataSync.Persistence;

internal static class DataSyncSchema
{
    internal const int CurrentVersion = 3;

    internal static async Task EnsureCreatedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL,
                applied_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS handoff_manifest (
                manifest_id TEXT PRIMARY KEY,
                epoch_id TEXT NOT NULL,
                source_set_id TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                imported_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS database_catalog (
                generation_id TEXT PRIMARY KEY,
                manifest_id TEXT NOT NULL REFERENCES handoff_manifest(manifest_id),
                relative_path TEXT NOT NULL,
                plaintext_path TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_database_catalog_relative_path
            ON database_catalog(relative_path, created_at_utc DESC);

            CREATE TABLE IF NOT EXISTS parse_job (
                id TEXT PRIMARY KEY,
                source_set_id TEXT NOT NULL UNIQUE,
                state TEXT NOT NULL CHECK (state IN ('pending', 'leased', 'completed', 'failed')),
                lease_owner TEXT NULL,
                lease_until_utc TEXT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                failure_code TEXT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS parse_job_input (
                job_id TEXT NOT NULL REFERENCES parse_job(id) ON DELETE CASCADE,
                generation_id TEXT NOT NULL REFERENCES database_catalog(generation_id),
                relative_path TEXT NOT NULL,
                plaintext_path TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
                PRIMARY KEY(job_id, generation_id),
                UNIQUE(job_id, relative_path),
                UNIQUE(job_id, ordinal)
            );

            CREATE TABLE IF NOT EXISTS exported_item (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                item_type TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                source_set_id TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE(item_type, idempotency_key)
            );

            CREATE TABLE IF NOT EXISTS outbox (
                id TEXT PRIMARY KEY,
                idempotency_key TEXT NOT NULL UNIQUE,
                endpoint TEXT NOT NULL,
                ciphertext BLOB NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('pending', 'leased', 'acknowledged', 'quarantined')),
                lease_owner TEXT NULL,
                lease_until_utc TEXT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                next_attempt_at_utc TEXT NOT NULL,
                last_status_code INTEGER NULL,
                last_error_summary TEXT NULL,
                acknowledged_at_utc TEXT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_outbox_due
            ON outbox(state, next_attempt_at_utc, created_at_utc);

            CREATE TABLE IF NOT EXISTS runtime_event (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS imported_telemetry (
                event_id TEXT PRIMARY KEY,
                component TEXT NOT NULL,
                event_name TEXT NOT NULL,
                severity TEXT NOT NULL,
                code TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                imported_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS operational_state (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS status_sequence (
                client_id TEXT PRIMARY KEY,
                last_sequence INTEGER NOT NULL CHECK (last_sequence >= 1)
            );

            INSERT INTO schema_info(version, applied_at_utc)
            SELECT $version, $now
            WHERE NOT EXISTS (SELECT 1 FROM schema_info);
            """;
        command.Parameters.AddWithValue("$version", CurrentVersion);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var version = connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "SELECT version FROM schema_info LIMIT 1;";
        var storedVersion = Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken));
        if (storedVersion is 1 or 2)
        {
            await using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = """
                UPDATE schema_info
                SET version = $version,
                    applied_at_utc = $now
                WHERE version = $stored_version;
                """;
            migrate.Parameters.AddWithValue("$version", CurrentVersion);
            migrate.Parameters.AddWithValue("$stored_version", storedVersion);
            migrate.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            if (await migrate.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidDataException("DataSync schema migration did not update one version row.");
        }
        else if (storedVersion != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported DataSync schema version {storedVersion}.");
        }

        transaction.Commit();
    }
}
