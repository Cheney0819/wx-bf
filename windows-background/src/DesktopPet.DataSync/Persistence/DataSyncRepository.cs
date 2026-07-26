using DesktopPet.Background.Infrastructure;
using DesktopPet.DataSync.Security;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopPet.DataSync.Persistence;

public sealed class DataSyncRepository : IDataSyncRepository
{
    private const string UploadCredentialFingerprintStateKey =
        "datasync_upload_credential_fingerprint_v1";

    private readonly string _databasePath;
    private readonly TimeProvider _timeProvider;
    private readonly IOutboxProtector _outboxProtector;

    public DataSyncRepository(
        string databasePath,
        TimeProvider timeProvider,
        IOutboxProtector outboxProtector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(outboxProtector);
        _databasePath = Path.GetFullPath(databasePath);
        _timeProvider = timeProvider;
        _outboxProtector = outboxProtector;
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await DataSyncSchema.EnsureCreatedAsync(connection, cancellationToken);
    }

    public async Task EnqueueParseJobAsync(
        ParseJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateIdentifier(job.Id, nameof(job));
        ValidateIdentifier(job.SourceSetId, nameof(job));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO parse_job(
                id, source_set_id, state, lease_owner, lease_until_utc,
                attempt_count, created_at_utc, updated_at_utc)
            VALUES(
                $id, $source_set_id, $state, $lease_owner, $lease_until,
                $attempt_count, $created, $updated)
            ON CONFLICT(source_set_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$source_set_id", job.SourceSetId);
        command.Parameters.AddWithValue("$state", ToDatabase(job.State));
        command.Parameters.AddWithValue("$lease_owner", (object?)job.LeaseOwner ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$lease_until",
            job.LeaseUntilUtc is null ? DBNull.Value : job.LeaseUntilUtc.Value.ToString("O"));
        command.Parameters.AddWithValue("$attempt_count", job.AttemptCount);
        command.Parameters.AddWithValue("$created", job.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", job.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ParseJob?> TryClaimParseJobAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateLease(workerId, leaseDuration);
        var now = _timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE parse_job
            SET state = 'leased',
                lease_owner = $worker,
                lease_until_utc = $lease_until,
                attempt_count = attempt_count + 1,
                updated_at_utc = $now
            WHERE id = (
                SELECT id
                FROM parse_job
                WHERE state = 'pending'
                   OR (state = 'leased' AND lease_until_utc < $now)
                ORDER BY created_at_utc, id
                LIMIT 1
            )
              AND (state = 'pending' OR (state = 'leased' AND lease_until_utc < $now))
            RETURNING *;
            """;
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$lease_until", (now + leaseDuration).ToString("O"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadParseJob(reader) : null;
    }

    public async Task<ParseJob?> GetParseJobAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(jobId, nameof(jobId));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM parse_job WHERE id = $id;";
        command.Parameters.AddWithValue("$id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadParseJob(reader) : null;
    }

    public async Task EnqueueOutboxAsync(
        OutboxDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateIdentifier(draft.Id, nameof(draft));
        ValidateIdentifier(draft.IdempotencyKey, nameof(draft));
        ValidateIdentifier(draft.Endpoint, nameof(draft));
        if (draft.Payload.Length == 0)
            throw new ArgumentException("Outbox payload must not be empty.", nameof(draft));

        var ciphertext = _outboxProtector.Protect(draft.Id, draft.Endpoint, draft.Payload);
        try
        {
            var now = _timeProvider.GetUtcNow();
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO outbox(
                    id, idempotency_key, endpoint, ciphertext, state,
                    attempt_count, next_attempt_at_utc, created_at_utc, updated_at_utc)
                VALUES(
                    $id, $idempotency_key, $endpoint, $ciphertext, 'pending',
                    0, $now, $now, $now)
                ON CONFLICT(idempotency_key) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", draft.Id);
            command.Parameters.AddWithValue("$idempotency_key", draft.IdempotencyKey);
            command.Parameters.AddWithValue("$endpoint", draft.Endpoint);
            command.Parameters.Add("$ciphertext", SqliteType.Blob).Value = ciphertext;
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public async Task EnqueueStatusOutboxAsync(
        OutboxDraft draft,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateIdentifier(draft.Id, nameof(draft));
        ValidateIdentifier(draft.IdempotencyKey, nameof(draft));
        ValidateIdentifier(draft.Endpoint, nameof(draft));
        ValidateIdentifier(sessionId, nameof(sessionId));
        if (!string.Equals(draft.Endpoint, "status", StringComparison.Ordinal))
            throw new ArgumentException("Status Outbox endpoint must be status.", nameof(draft));
        if (draft.Payload.Length == 0)
            throw new ArgumentException("Outbox payload must not be empty.", nameof(draft));

        var ciphertext = _outboxProtector.Protect(draft.Id, draft.Endpoint, draft.Payload);
        try
        {
            var now = _timeProvider.GetUtcNow().ToString("O");
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction(deferred: false);
            await using (var remove = connection.CreateCommand())
            {
                remove.Transaction = transaction;
                remove.CommandText = """
                    DELETE FROM outbox
                    WHERE endpoint = 'status'
                      AND state = 'pending'
                      AND substr(idempotency_key, 1, length($prefix)) = $prefix;
                    """;
                remove.Parameters.AddWithValue("$prefix", $"status:{sessionId}:");
                await remove.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO outbox(
                        id, idempotency_key, endpoint, ciphertext, state,
                        attempt_count, next_attempt_at_utc, created_at_utc, updated_at_utc)
                    VALUES($id, $idempotency_key, 'status', $ciphertext, 'pending',
                           0, $now, $now, $now)
                    ON CONFLICT(idempotency_key) DO NOTHING;
                    """;
                insert.Parameters.AddWithValue("$id", draft.Id);
                insert.Parameters.AddWithValue("$idempotency_key", draft.IdempotencyKey);
                insert.Parameters.Add("$ciphertext", SqliteType.Blob).Value = ciphertext;
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public async Task<long> AllocateHeartbeatSequenceAsync(
        string clientId,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(clientId, nameof(clientId));
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO status_sequence(client_id, last_sequence)
            VALUES($client_id, 1)
            ON CONFLICT(client_id) DO UPDATE SET
                last_sequence = status_sequence.last_sequence + 1
            RETURNING last_sequence;
            """;
        command.Parameters.AddWithValue("$client_id", clientId);
        var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        transaction.Commit();
        return sequence;
    }

    public async Task<IReadOnlyList<OutboxRecord>> GetPendingOutboxAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var now = _timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM outbox
            WHERE next_attempt_at_utc <= $now
              AND (state = 'pending' OR (state = 'leased' AND lease_until_utc < $now))
            ORDER BY created_at_utc, id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<OutboxRecord>();
        while (await reader.ReadAsync(cancellationToken)) records.Add(ReadOutbox(reader));
        return Array.AsReadOnly(records.ToArray());
    }

    public async Task<OutboxRecord?> TryClaimOutboxAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateLease(workerId, leaseDuration);
        var now = _timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outbox
            SET state = 'leased',
                lease_owner = $worker,
                lease_until_utc = $lease_until,
                attempt_count = attempt_count + 1,
                updated_at_utc = $now
            WHERE id = (
                SELECT id
                FROM outbox
                WHERE next_attempt_at_utc <= $now
                  AND (state = 'pending' OR (state = 'leased' AND lease_until_utc < $now))
                ORDER BY next_attempt_at_utc, created_at_utc, id
                LIMIT 1
            )
              AND next_attempt_at_utc <= $now
              AND (state = 'pending' OR (state = 'leased' AND lease_until_utc < $now))
            RETURNING *;
            """;
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$lease_until", (now + leaseDuration).ToString("O"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOutbox(reader) : null;
    }

    public async Task<HandoffImportResult> ImportHandoffAsync(
        ValidatedHandoffManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateIdentifier(manifest.ManifestId, nameof(manifest));
        ValidateIdentifier(manifest.EpochId, nameof(manifest));
        if (manifest.Databases.Count == 0)
            throw new ArgumentException("A handoff must contain at least one database.", nameof(manifest));

        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await ReadImportResultAsync(
            connection,
            transaction,
            manifest.ManifestId,
            cancellationToken);
        if (existing is not null)
        {
            transaction.Commit();
            return existing with { WasAlreadyImported = true };
        }

        var latest = await ReadLatestCatalogAsync(connection, transaction, cancellationToken);
        foreach (var database in manifest.Databases)
        {
            var candidate = new CatalogCandidate(
                database.GenerationId,
                database.RelativePath,
                database.PlaintextPath,
                database.Sha256,
                manifest.CreatedAtUtc);
            if (!latest.TryGetValue(database.RelativePath, out var current) ||
                IsNewer(candidate, current))
            {
                latest[database.RelativePath] = candidate;
            }
        }

        var selected = latest.Values
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var sourceSetId = ComputeSha256(string.Join(
            "|",
            selected.Select(item => $"{item.RelativePath}:{item.GenerationId}")));
        var jobId = ComputeSha256($"desktop-pet-datasync-parse-job-v1|{sourceSetId}");
        var now = _timeProvider.GetUtcNow();

        await using (var insertManifest = connection.CreateCommand())
        {
            insertManifest.Transaction = transaction;
            insertManifest.CommandText = """
                INSERT INTO handoff_manifest(
                    manifest_id, epoch_id, source_set_id, created_at_utc, imported_at_utc)
                VALUES($manifest_id, $epoch_id, $source_set_id, $created, $imported);
                """;
            insertManifest.Parameters.AddWithValue("$manifest_id", manifest.ManifestId);
            insertManifest.Parameters.AddWithValue("$epoch_id", manifest.EpochId);
            insertManifest.Parameters.AddWithValue("$source_set_id", sourceSetId);
            insertManifest.Parameters.AddWithValue("$created", manifest.CreatedAtUtc.ToString("O"));
            insertManifest.Parameters.AddWithValue("$imported", now.ToString("O"));
            await insertManifest.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var database in manifest.Databases)
        {
            await using var insertCatalog = connection.CreateCommand();
            insertCatalog.Transaction = transaction;
            insertCatalog.CommandText = """
                INSERT INTO database_catalog(
                    generation_id, manifest_id, relative_path,
                    plaintext_path, sha256, created_at_utc)
                VALUES(
                    $generation_id, $manifest_id, $relative_path,
                    $plaintext_path, $sha256, $created)
                ON CONFLICT(generation_id) DO NOTHING;
                """;
            insertCatalog.Parameters.AddWithValue("$generation_id", database.GenerationId);
            insertCatalog.Parameters.AddWithValue("$manifest_id", manifest.ManifestId);
            insertCatalog.Parameters.AddWithValue("$relative_path", database.RelativePath);
            insertCatalog.Parameters.AddWithValue("$plaintext_path", database.PlaintextPath);
            insertCatalog.Parameters.AddWithValue("$sha256", database.Sha256);
            insertCatalog.Parameters.AddWithValue("$created", manifest.CreatedAtUtc.ToString("O"));
            await insertCatalog.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertJob = connection.CreateCommand())
        {
            insertJob.Transaction = transaction;
            insertJob.CommandText = """
                INSERT INTO parse_job(
                    id, source_set_id, state, attempt_count, created_at_utc, updated_at_utc)
                VALUES($id, $source_set_id, 'pending', 0, $now, $now)
                ON CONFLICT(source_set_id) DO NOTHING;
                """;
            insertJob.Parameters.AddWithValue("$id", jobId);
            insertJob.Parameters.AddWithValue("$source_set_id", sourceSetId);
            insertJob.Parameters.AddWithValue("$now", now.ToString("O"));
            await insertJob.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var findJob = connection.CreateCommand())
        {
            findJob.Transaction = transaction;
            findJob.CommandText = "SELECT id FROM parse_job WHERE source_set_id = $source_set_id;";
            findJob.Parameters.AddWithValue("$source_set_id", sourceSetId);
            jobId = (string)(await findJob.ExecuteScalarAsync(cancellationToken) ??
                throw new InvalidDataException("Imported source set has no parse job."));
        }

        for (var ordinal = 0; ordinal < selected.Length; ordinal++)
        {
            var input = selected[ordinal];
            await using var insertInput = connection.CreateCommand();
            insertInput.Transaction = transaction;
            insertInput.CommandText = """
                INSERT INTO parse_job_input(
                    job_id, generation_id, relative_path, plaintext_path, sha256, ordinal)
                VALUES(
                    $job_id, $generation_id, $relative_path, $plaintext_path, $sha256, $ordinal)
                ON CONFLICT(job_id, generation_id) DO NOTHING;
                """;
            insertInput.Parameters.AddWithValue("$job_id", jobId);
            insertInput.Parameters.AddWithValue("$generation_id", input.GenerationId);
            insertInput.Parameters.AddWithValue("$relative_path", input.RelativePath);
            insertInput.Parameters.AddWithValue("$plaintext_path", input.PlaintextPath);
            insertInput.Parameters.AddWithValue("$sha256", input.Sha256);
            insertInput.Parameters.AddWithValue("$ordinal", ordinal);
            await insertInput.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
        return new HandoffImportResult(
            manifest.ManifestId,
            sourceSetId,
            jobId,
            WasAlreadyImported: false);
    }

    public async Task<IReadOnlyList<ImportedManifest>> ListManifestsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT manifest_id, epoch_id, source_set_id, created_at_utc, imported_at_utc
            FROM handoff_manifest
            ORDER BY imported_at_utc, manifest_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var manifests = new List<ImportedManifest>();
        while (await reader.ReadAsync(cancellationToken))
        {
            manifests.Add(new ImportedManifest(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                DateTimeOffset.Parse(reader.GetString(4))));
        }
        return Array.AsReadOnly(manifests.ToArray());
    }

    public async Task<IReadOnlyList<ParseJobInput>> ListParseJobInputsAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(jobId, nameof(jobId));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, generation_id, relative_path, plaintext_path, sha256, ordinal
            FROM parse_job_input
            WHERE job_id = $job_id
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var inputs = new List<ParseJobInput>();
        while (await reader.ReadAsync(cancellationToken))
        {
            inputs.Add(new ParseJobInput(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5)));
        }
        return Array.AsReadOnly(inputs.ToArray());
    }

    public Task<long> CountExportedItemsAsync(CancellationToken cancellationToken) =>
        CountAsync("exported_item", cancellationToken);

    public Task<long> CountOutboxAsync(CancellationToken cancellationToken) =>
        CountAsync("outbox", cancellationToken);

    public async Task<OutboxRecord?> GetOutboxAsync(
        string outboxId,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(outboxId, nameof(outboxId));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM outbox WHERE id = $id;";
        command.Parameters.AddWithValue("$id", outboxId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOutbox(reader) : null;
    }

    public Task AcknowledgeOutboxAsync(
        string outboxId,
        string workerId,
        int statusCode,
        CancellationToken cancellationToken) =>
        TransitionOutboxAsync(
            outboxId,
            workerId,
            "acknowledged",
            _timeProvider.GetUtcNow(),
            statusCode,
            summary: null,
            setAcknowledged: true,
            cancellationToken);

    public Task ScheduleOutboxRetryAsync(
        string outboxId,
        string workerId,
        DateTimeOffset nextAttemptAtUtc,
        int statusCode,
        string summary,
        CancellationToken cancellationToken) =>
        TransitionOutboxAsync(
            outboxId,
            workerId,
            "pending",
            nextAttemptAtUtc,
            statusCode,
            summary,
            setAcknowledged: false,
            cancellationToken);

    public Task QuarantineOutboxAsync(
        string outboxId,
        string workerId,
        int statusCode,
        string summary,
        CancellationToken cancellationToken) =>
        TransitionOutboxAsync(
            outboxId,
            workerId,
            "quarantined",
            _timeProvider.GetUtcNow(),
            statusCode,
            summary,
            setAcknowledged: false,
            cancellationToken);

    public async Task<int> RequeueQuarantinedOutboxAsync(
        IReadOnlyCollection<int> statusCodes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statusCodes);
        if (statusCodes.Count == 0 || statusCodes.Any(code => code is < 100 or > 599))
            throw new ArgumentException("At least one valid HTTP status code is required.", nameof(statusCodes));

        var parameters = statusCodes
            .Distinct()
            .Select((code, index) => (Name: $"$status_{index}", Code: code))
            .ToArray();
        var now = _timeProvider.GetUtcNow().ToString("O");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE outbox
            SET state = 'pending',
                lease_owner = NULL,
                lease_until_utc = NULL,
                next_attempt_at_utc = $now,
                updated_at_utc = $now
            WHERE state = 'quarantined'
              AND last_status_code IN ({string.Join(", ", parameters.Select(item => item.Name))});
            """;
        command.Parameters.AddWithValue("$now", now);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Code);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RequeueAuthenticationFailuresIfCredentialChangedAsync(
        string credentialFingerprint,
        CancellationToken cancellationToken)
    {
        if (credentialFingerprint.Length != 64 ||
            credentialFingerprint.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Credential fingerprint must be a SHA-256 hex string.",
                nameof(credentialFingerprint));
        }

        var normalizedFingerprint = credentialFingerprint.ToLowerInvariant();
        var valueJson = JsonSerializer.Serialize(normalizedFingerprint);
        var now = _timeProvider.GetUtcNow().ToString("O");
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT value_json FROM operational_state WHERE key = $key;";
            read.Parameters.AddWithValue("$key", UploadCredentialFingerprintStateKey);
            if (string.Equals(
                    Convert.ToString(await read.ExecuteScalarAsync(cancellationToken)),
                    valueJson,
                    StringComparison.Ordinal))
            {
                transaction.Commit();
                return 0;
            }
        }

        int requeued;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE outbox
                SET state = 'pending',
                    lease_owner = NULL,
                    lease_until_utc = NULL,
                    next_attempt_at_utc = $now,
                    updated_at_utc = $now
                WHERE state = 'quarantined'
                  AND last_status_code IN (401, 403);
                """;
            update.Parameters.AddWithValue("$now", now);
            requeued = await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var remember = connection.CreateCommand())
        {
            remember.Transaction = transaction;
            remember.CommandText = """
                INSERT INTO operational_state(key, value_json, updated_at_utc)
                VALUES($key, $value, $now)
                ON CONFLICT(key) DO UPDATE SET
                    value_json = excluded.value_json,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            remember.Parameters.AddWithValue("$key", UploadCredentialFingerprintStateKey);
            remember.Parameters.AddWithValue("$value", valueJson);
            remember.Parameters.AddWithValue("$now", now);
            await remember.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
        return requeued;
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
            throw new ArgumentException("Runtime event payload is too large.", nameof(payloadJson));
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
                    SELECT sequence FROM runtime_event ORDER BY sequence DESC LIMIT 200
                );
                """;
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    internal async Task<TelemetryImportResult> ImportTelemetryAsync(
        TelemetryCommitDraft draft,
        Func<CancellationToken, Task> beforeCommit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(beforeCommit);
        ValidateIdentifier(draft.Envelope.EventId, nameof(draft));
        ValidateIdentifier(draft.RequestId, nameof(draft));
        if (draft.Ciphertext.Length == 0)
            throw new ArgumentException("Telemetry ciphertext must not be empty.", nameof(draft));

        var now = _timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM imported_telemetry WHERE event_id = $event_id;";
            exists.Parameters.AddWithValue("$event_id", draft.Envelope.EventId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
            {
                transaction.Commit();
                return new TelemetryImportResult(
                    draft.Envelope.EventId,
                    draft.RequestId,
                    WasAlreadyImported: true,
                    WasRejected: false);
            }
        }

        await using (var identity = connection.CreateCommand())
        {
            identity.Transaction = transaction;
            identity.CommandText = """
                INSERT INTO imported_telemetry(
                    event_id, component, event_name, severity, code,
                    occurred_at_utc, imported_at_utc)
                VALUES(
                    $event_id, $component, $event_name, $severity, $code,
                    $occurred, $imported);
                """;
            identity.Parameters.AddWithValue("$event_id", draft.Envelope.EventId);
            identity.Parameters.AddWithValue("$component", draft.Envelope.Component);
            identity.Parameters.AddWithValue("$event_name", draft.Envelope.EventName);
            identity.Parameters.AddWithValue("$severity", draft.Envelope.Severity);
            identity.Parameters.AddWithValue("$code", draft.Envelope.Code);
            identity.Parameters.AddWithValue("$occurred", draft.Envelope.OccurredAtUtc.ToString("O"));
            identity.Parameters.AddWithValue("$imported", now.ToString("O"));
            await identity.ExecuteNonQueryAsync(cancellationToken);
        }

        var diagnostic = JsonSerializer.Serialize(new
        {
            eventId = draft.Envelope.EventId,
            component = draft.Envelope.Component,
            severity = draft.Envelope.Severity,
            code = draft.Envelope.Code,
        });
        await using (var runtimeEvent = connection.CreateCommand())
        {
            runtimeEvent.Transaction = transaction;
            runtimeEvent.CommandText = """
                INSERT INTO runtime_event(event_type, payload_json, created_at_utc)
                VALUES($event_type, $payload, $created);
                """;
            runtimeEvent.Parameters.AddWithValue("$event_type", draft.Envelope.EventName);
            runtimeEvent.Parameters.AddWithValue("$payload", diagnostic);
            runtimeEvent.Parameters.AddWithValue("$created", now.ToString("O"));
            await runtimeEvent.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var state in draft.OperationalState.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            await using var updateState = connection.CreateCommand();
            updateState.Transaction = transaction;
            updateState.CommandText = """
                INSERT INTO operational_state(key, value_json, updated_at_utc)
                VALUES($key, $value, $updated)
                ON CONFLICT(key) DO UPDATE SET
                    value_json = excluded.value_json,
                    updated_at_utc = excluded.updated_at_utc
                WHERE excluded.updated_at_utc >= operational_state.updated_at_utc;
                """;
            updateState.Parameters.AddWithValue("$key", state.Key);
            updateState.Parameters.AddWithValue("$value", state.Value);
            updateState.Parameters.AddWithValue(
                "$updated",
                draft.Envelope.OccurredAtUtc.ToString("O"));
            await updateState.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var outbox = connection.CreateCommand())
        {
            outbox.Transaction = transaction;
            outbox.CommandText = """
                INSERT INTO outbox(
                    id, idempotency_key, endpoint, ciphertext, state,
                    attempt_count, next_attempt_at_utc, created_at_utc, updated_at_utc)
                VALUES(
                    $id, $idempotency_key, 'events', $ciphertext, 'pending',
                    0, $now, $now, $now);
                """;
            outbox.Parameters.AddWithValue("$id", draft.RequestId);
            outbox.Parameters.AddWithValue("$idempotency_key", $"events:{draft.RequestId}");
            outbox.Parameters.Add("$ciphertext", SqliteType.Blob).Value = draft.Ciphertext;
            outbox.Parameters.AddWithValue("$now", now.ToString("O"));
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = """
                DELETE FROM runtime_event
                WHERE sequence NOT IN (
                    SELECT sequence FROM runtime_event ORDER BY sequence DESC LIMIT 200
                );
                """;
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }

        await beforeCommit(cancellationToken);
        transaction.Commit();
        return new TelemetryImportResult(
            draft.Envelope.EventId,
            draft.RequestId,
            WasAlreadyImported: false,
            WasRejected: false);
    }

    public async Task<OperationalStateEntry?> GetOperationalStateAsync(
        string key,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(key, nameof(key));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT key, value_json, updated_at_utc
            FROM operational_state
            WHERE key = $key;
            """;
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new OperationalStateEntry(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        SqliteConnectionFactory.OpenAsync(_databasePath, readOnly: false, cancellationToken);

    private static ParseJob ReadParseJob(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("source_set_id")),
        ParseJobStateFromDatabase(reader.GetString(reader.GetOrdinal("state"))),
        ReadNullableString(reader, "lease_owner"),
        ReadNullableDateTimeOffset(reader, "lease_until_utc"),
        reader.GetInt32(reader.GetOrdinal("attempt_count")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at_utc"))));

    private static OutboxRecord ReadOutbox(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("idempotency_key")),
        reader.GetString(reader.GetOrdinal("endpoint")),
        (byte[])reader[reader.GetOrdinal("ciphertext")],
        OutboxStateFromDatabase(reader.GetString(reader.GetOrdinal("state"))),
        ReadNullableString(reader, "lease_owner"),
        ReadNullableDateTimeOffset(reader, "lease_until_utc"),
        reader.GetInt32(reader.GetOrdinal("attempt_count")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("next_attempt_at_utc"))),
        reader.IsDBNull(reader.GetOrdinal("last_status_code"))
            ? null
            : reader.GetInt32(reader.GetOrdinal("last_status_code")),
        ReadNullableString(reader, "last_error_summary"),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at_utc"))));

    private static string ToDatabase(ParseJobState state) => state switch
    {
        ParseJobState.Pending => "pending",
        ParseJobState.Leased => "leased",
        ParseJobState.Completed => "completed",
        ParseJobState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static ParseJobState ParseJobStateFromDatabase(string state) => state switch
    {
        "pending" => ParseJobState.Pending,
        "leased" => ParseJobState.Leased,
        "completed" => ParseJobState.Completed,
        "failed" => ParseJobState.Failed,
        _ => throw new InvalidDataException("Stored parse job state is invalid."),
    };

    private static OutboxState OutboxStateFromDatabase(string state) => state switch
    {
        "pending" => OutboxState.Pending,
        "leased" => OutboxState.Leased,
        "acknowledged" => OutboxState.Acknowledged,
        "quarantined" => OutboxState.Quarantined,
        _ => throw new InvalidDataException("Stored Outbox state is invalid."),
    };

    private static string? ReadNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(
        SqliteDataReader reader,
        string column)
    {
        var value = ReadNullableString(reader, column);
        return value is null ? null : DateTimeOffset.Parse(value);
    }

    private static void ValidateLease(string workerId, TimeSpan duration)
    {
        ValidateIdentifier(workerId, nameof(workerId));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(duration));
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 256)
            throw new ArgumentException("Identifier exceeds 256 characters.", parameterName);
    }

    private async Task<long> CountAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private async Task TransitionOutboxAsync(
        string outboxId,
        string workerId,
        string state,
        DateTimeOffset nextAttemptAtUtc,
        int statusCode,
        string? summary,
        bool setAcknowledged,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(outboxId, nameof(outboxId));
        ValidateIdentifier(workerId, nameof(workerId));
        if (summary is not null && summary.Length > 256)
            throw new ArgumentException("Outbox error summary exceeds 256 characters.", nameof(summary));
        var now = _timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outbox
            SET state = $state,
                lease_owner = NULL,
                lease_until_utc = NULL,
                next_attempt_at_utc = $next_attempt,
                last_status_code = $status_code,
                last_error_summary = $summary,
                acknowledged_at_utc = CASE WHEN $acknowledged = 1 THEN $now ELSE NULL END,
                updated_at_utc = $now
            WHERE id = $id
              AND state = 'leased'
              AND lease_owner = $worker;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$next_attempt", nextAttemptAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$status_code", statusCode);
        command.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$acknowledged", setAcknowledged ? 1 : 0);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", outboxId);
        command.Parameters.AddWithValue("$worker", workerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Outbox lease changed before state transition.");
    }

    private static async Task<HandoffImportResult?> ReadImportResultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string manifestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT h.manifest_id, h.source_set_id, p.id
            FROM handoff_manifest h
            JOIN parse_job p ON p.source_set_id = h.source_set_id
            WHERE h.manifest_id = $manifest_id;
            """;
        command.Parameters.AddWithValue("$manifest_id", manifestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new HandoffImportResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            WasAlreadyImported: true);
    }

    private static async Task<Dictionary<string, CatalogCandidate>> ReadLatestCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT generation_id, relative_path, plaintext_path, sha256, created_at_utc
            FROM database_catalog
            ORDER BY relative_path, created_at_utc DESC, generation_id DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var latest = new Dictionary<string, CatalogCandidate>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var candidate = new CatalogCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)));
            if (!latest.TryGetValue(candidate.RelativePath, out var current) ||
                IsNewer(candidate, current))
            {
                latest[candidate.RelativePath] = candidate;
            }
        }
        return latest;
    }

    private static bool IsNewer(CatalogCandidate candidate, CatalogCandidate current) =>
        candidate.CreatedAtUtc > current.CreatedAtUtc ||
        candidate.CreatedAtUtc == current.CreatedAtUtc &&
        string.CompareOrdinal(candidate.GenerationId, current.GenerationId) > 0;

    private static string ComputeSha256(string material)
    {
        var bytes = Encoding.UTF8.GetBytes(material);
        var digest = SHA256.HashData(bytes);
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private sealed record CatalogCandidate(
        string GenerationId,
        string RelativePath,
        string PlaintextPath,
        string Sha256,
        DateTimeOffset CreatedAtUtc);
}
