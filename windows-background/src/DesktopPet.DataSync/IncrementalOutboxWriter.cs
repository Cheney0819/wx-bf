using System.Security.Cryptography;
using System.Text.Json;
using DesktopPet.Background.Infrastructure;
using DesktopPet.DataSync.Identity;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;
using Microsoft.Data.Sqlite;

namespace DesktopPet.DataSync;

public interface IIncrementalCommitObserver
{
    Task BeforeParseCompletionAsync(CancellationToken cancellationToken);
}

public sealed class IncrementalOutboxWriter
{
    private const int MaximumPayloadBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly DataSyncRepository _repository;
    private readonly IOutboxProtector _protector;
    private readonly ClientIdentityDocument _identity;
    private readonly TimeProvider _timeProvider;
    private readonly IIncrementalCommitObserver _observer;

    public IncrementalOutboxWriter(
        DataSyncRepository repository,
        IOutboxProtector protector,
        ClientIdentityDocument identity,
        TimeProvider timeProvider,
        IIncrementalCommitObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _repository = repository;
        _protector = protector;
        _identity = identity;
        _timeProvider = timeProvider;
        _observer = observer ?? NoOpIncrementalCommitObserver.Instance;
    }

    public async Task CommitAsync(
        ParseJob job,
        ParserResultDocument result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);
        if (job.Id != result.JobId || job.SourceSetId != result.SourceSetId)
            throw new InvalidDataException("Parser result does not belong to the parse job.");

        await using var connection = await SqliteConnectionFactory.OpenAsync(
            _repository.DatabasePath,
            readOnly: false,
            cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var storedState = await ReadJobStateAsync(connection, transaction, job, cancellationToken);
        if (storedState == ParseJobState.Completed)
        {
            transaction.Commit();
            return;
        }
        if (storedState != ParseJobState.Leased)
            throw new InvalidOperationException("Parse job must be leased before result commit.");

        var candidates = CreateCandidates(result);
        var unseen = new List<ExportCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO exported_item(item_type, idempotency_key, source_set_id, created_at_utc)
                VALUES($type, $key, $source_set_id, $created)
                ON CONFLICT(item_type, idempotency_key) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$type", candidate.ItemType);
            insert.Parameters.AddWithValue("$key", candidate.Identity);
            insert.Parameters.AddWithValue("$source_set_id", result.SourceSetId);
            insert.Parameters.AddWithValue("$created", _timeProvider.GetUtcNow().ToString("O"));
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
                unseen.Add(candidate);
        }

        foreach (var batch in BuildBatches(unseen))
        {
            var plaintext = SerializeBatch(batch);
            var ciphertext = _protector.Protect(batch.RequestId, batch.Endpoint, plaintext);
            try
            {
                var now = _timeProvider.GetUtcNow();
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO outbox(
                        id, idempotency_key, endpoint, ciphertext, state,
                        attempt_count, next_attempt_at_utc, created_at_utc, updated_at_utc)
                    VALUES(
                        $id, $idempotency_key, $endpoint, $ciphertext, 'pending',
                        0, $now, $now, $now)
                    ON CONFLICT(idempotency_key) DO NOTHING;
                    """;
                insert.Parameters.AddWithValue("$id", batch.RequestId);
                insert.Parameters.AddWithValue(
                    "$idempotency_key",
                    $"{batch.Endpoint}:{batch.RequestId}");
                insert.Parameters.AddWithValue("$endpoint", batch.Endpoint);
                insert.Parameters.Add("$ciphertext", SqliteType.Blob).Value = ciphertext;
                insert.Parameters.AddWithValue("$now", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }

        if (result.NextCursor is null)
        {
            await _observer.BeforeParseCompletionAsync(cancellationToken);
            await using var complete = connection.CreateCommand();
            complete.Transaction = transaction;
            complete.CommandText = """
                    UPDATE parse_job
                    SET state = 'completed',
                        lease_owner = NULL,
                        lease_until_utc = NULL,
                        failure_code = NULL,
                        updated_at_utc = $now
                    WHERE id = $id
                      AND source_set_id = $source_set_id
                      AND state = 'leased'
                      AND lease_owner = $lease_owner;
                    """;
            complete.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
            complete.Parameters.AddWithValue("$id", job.Id);
            complete.Parameters.AddWithValue("$source_set_id", job.SourceSetId);
            complete.Parameters.AddWithValue("$lease_owner", job.LeaseOwner!);
            if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Parse job lease changed before result commit.");
        }
        transaction.Commit();
    }

    private static async Task<ParseJobState> ReadJobStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParseJob job,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT state, source_set_id
            FROM parse_job
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Parse job does not exist.");
        if (!string.Equals(reader.GetString(1), job.SourceSetId, StringComparison.Ordinal))
            throw new InvalidDataException("Stored parse job source set changed.");
        return reader.GetString(0) switch
        {
            "pending" => ParseJobState.Pending,
            "leased" => ParseJobState.Leased,
            "completed" => ParseJobState.Completed,
            "failed" => ParseJobState.Failed,
            _ => throw new InvalidDataException("Stored parse job state is invalid."),
        };
    }

    private static List<ExportCandidate> CreateCandidates(ParserResultDocument result)
    {
        var candidates = new List<ExportCandidate>(
            result.Messages.Count + result.Contacts.Count + result.Favorites.Count);
        candidates.AddRange(result.Messages.Select(item => new ExportCandidate(
            "message",
            ParserItemIdentity.Message(item),
            "messages",
            "messages",
            JsonSerializer.SerializeToElement(item, JsonOptions))));
        candidates.AddRange(result.Contacts.Select(item => new ExportCandidate(
            "contact",
            ParserItemIdentity.Contact(item),
            "contacts",
            "contacts",
            JsonSerializer.SerializeToElement(item, JsonOptions))));
        candidates.AddRange(result.Favorites.Select(item => new ExportCandidate(
            "favorite",
            ParserItemIdentity.Favorite(item),
            "favorites",
            "favorites",
            JsonSerializer.SerializeToElement(item, JsonOptions))));
        return candidates;
    }

    private IEnumerable<OutboxBatch> BuildBatches(IReadOnlyList<ExportCandidate> candidates)
    {
        foreach (var group in candidates
                     .GroupBy(item => (item.Endpoint, item.ArrayName))
                     .OrderBy(group => group.Key.Endpoint, StringComparer.Ordinal))
        {
            var countLimit = group.Key.Endpoint switch
            {
                "messages" => 500,
                "contacts" => 1000,
                "favorites" => 500,
                _ => throw new InvalidDataException("Unknown incremental endpoint."),
            };
            var current = new List<ExportCandidate>();
            foreach (var candidate in group)
            {
                var proposed = current.Append(candidate).ToArray();
                if (current.Count > 0 &&
                    (proposed.Length > countLimit || EstimateBatchBytes(group.Key.ArrayName, proposed) > MaximumPayloadBytes))
                {
                    yield return CreateBatch(group.Key.Endpoint, group.Key.ArrayName, current);
                    current.Clear();
                }
                current.Add(candidate);
                if (EstimateBatchBytes(group.Key.ArrayName, current) > MaximumPayloadBytes)
                    throw new InvalidDataException("One exported item exceeds the 8 MiB Outbox body limit.");
            }
            if (current.Count > 0)
                yield return CreateBatch(group.Key.Endpoint, group.Key.ArrayName, current);
        }
    }

    private static OutboxBatch CreateBatch(
        string endpoint,
        string arrayName,
        IReadOnlyList<ExportCandidate> items)
    {
        var requestId = ParserItemIdentity.Batch(
            endpoint,
            items.Select(item => item.Identity).ToArray());
        return new OutboxBatch(endpoint, arrayName, requestId, items.ToArray());
    }

    private int EstimateBatchBytes(
        string arrayName,
        IReadOnlyList<ExportCandidate> items)
    {
        var document = new Dictionary<string, object?>
        {
            ["request_id"] = new string('0', 64),
            ["session_id"] = _identity.SessionId,
            ["source"] = _identity.Source,
            [arrayName] = items.Select(item => item.Value).ToArray(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(document).Length;
    }

    private byte[] SerializeBatch(OutboxBatch batch)
    {
        var document = new Dictionary<string, object?>
        {
            ["request_id"] = batch.RequestId,
            ["session_id"] = _identity.SessionId,
            ["source"] = _identity.Source,
            [batch.ArrayName] = batch.Items.Select(item => item.Value).ToArray(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        if (bytes.Length > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("Outbox body exceeds 8 MiB.");
        }
        return bytes;
    }

    private sealed record ExportCandidate(
        string ItemType,
        string Identity,
        string Endpoint,
        string ArrayName,
        JsonElement Value);

    private sealed record OutboxBatch(
        string Endpoint,
        string ArrayName,
        string RequestId,
        IReadOnlyList<ExportCandidate> Items);

    private sealed class NoOpIncrementalCommitObserver : IIncrementalCommitObserver
    {
        internal static NoOpIncrementalCommitObserver Instance { get; } = new();

        public Task BeforeParseCompletionAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
