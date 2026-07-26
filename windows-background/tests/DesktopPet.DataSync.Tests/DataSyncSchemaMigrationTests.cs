using DesktopPet.Background.Infrastructure;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;
using Microsoft.Data.Sqlite;

namespace DesktopPet.DataSync.Tests;

public sealed class DataSyncSchemaMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-datasync-migration-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SchemaOneUpgradePreservesExistingOutboxAndCreatesTelemetryTables()
    {
        var path = Path.Combine(_root, "sync.db");
        Directory.CreateDirectory(_root);
        await CreateSchemaOneAsync(path);
        var repository = new DataSyncRepository(path, TimeProvider.System, new PassThroughProtector());

        await repository.InitializeAsync(default);

        await using var connection = await SqliteConnectionFactory.OpenAsync(path, false, default);
        Assert.Equal(3L, await ScalarAsync(connection, "SELECT version FROM schema_info LIMIT 1;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM outbox WHERE id = 'legacy-outbox';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM runtime_event WHERE event_type = 'legacy_event';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM handoff_manifest WHERE manifest_id = 'legacy-manifest';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM database_catalog WHERE generation_id = 'legacy-generation';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM parse_job WHERE id = 'legacy-job';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM parse_job_input WHERE job_id = 'legacy-job';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM exported_item WHERE idempotency_key = 'legacy-item';"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM imported_telemetry;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM operational_state;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM status_sequence;"));
    }

    private static async Task CreateSchemaOneAsync(string path)
    {
        await using var connection = await SqliteConnectionFactory.OpenAsync(path, false, default);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE schema_info (version INTEGER NOT NULL, applied_at_utc TEXT NOT NULL);
            INSERT INTO schema_info VALUES (1, '2026-07-01T00:00:00.0000000+00:00');

            CREATE TABLE outbox (
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
            INSERT INTO outbox(
                id, idempotency_key, endpoint, ciphertext, state, attempt_count,
                next_attempt_at_utc, created_at_utc, updated_at_utc)
            VALUES(
                'legacy-outbox', 'legacy-key', 'events', X'010203', 'pending', 0,
                '2026-07-01T00:00:00.0000000+00:00',
                '2026-07-01T00:00:00.0000000+00:00',
                '2026-07-01T00:00:00.0000000+00:00');

            CREATE TABLE handoff_manifest (
                manifest_id TEXT PRIMARY KEY,
                epoch_id TEXT NOT NULL,
                source_set_id TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                imported_at_utc TEXT NOT NULL
            );
            INSERT INTO handoff_manifest VALUES(
                'legacy-manifest', 'legacy-epoch', 'legacy-source',
                '2026-07-01T00:00:00.0000000+00:00',
                '2026-07-01T00:00:00.0000000+00:00');

            CREATE TABLE database_catalog (
                generation_id TEXT PRIMARY KEY,
                manifest_id TEXT NOT NULL REFERENCES handoff_manifest(manifest_id),
                relative_path TEXT NOT NULL,
                plaintext_path TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );
            INSERT INTO database_catalog VALUES(
                'legacy-generation', 'legacy-manifest', 'message/message_0.db',
                '/legacy/message.sqlite', 'legacy-sha',
                '2026-07-01T00:00:00.0000000+00:00');

            CREATE TABLE parse_job (
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
            INSERT INTO parse_job(
                id, source_set_id, state, attempt_count, created_at_utc, updated_at_utc)
            VALUES(
                'legacy-job', 'legacy-source', 'completed', 1,
                '2026-07-01T00:00:00.0000000+00:00',
                '2026-07-01T00:00:00.0000000+00:00');

            CREATE TABLE parse_job_input (
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
            INSERT INTO parse_job_input VALUES(
                'legacy-job', 'legacy-generation', 'message/message_0.db',
                '/legacy/message.sqlite', 'legacy-sha', 0);

            CREATE TABLE exported_item (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                item_type TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                source_set_id TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE(item_type, idempotency_key)
            );
            INSERT INTO exported_item(item_type, idempotency_key, source_set_id, created_at_utc)
            VALUES(
                'message', 'legacy-item', 'legacy-source',
                '2026-07-01T00:00:00.0000000+00:00');

            CREATE TABLE runtime_event (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );
            INSERT INTO runtime_event(event_type, payload_json, created_at_utc)
            VALUES('legacy_event', '{"code":"legacy"}', '2026-07-01T00:00:00.0000000+00:00');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class PassThroughProtector : IOutboxProtector
    {
        public byte[] Protect(string outboxId, string endpoint, ReadOnlySpan<byte> plaintext) =>
            plaintext.ToArray();

        public byte[] Unprotect(string outboxId, string endpoint, ReadOnlySpan<byte> ciphertext) =>
            ciphertext.ToArray();
    }
}
