using System.Text.Json;
using DesktopPet.Background.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DesktopPet.DataSync.Worker;

public static class DataSyncDiagnosticReader
{
    public static async Task<string> ReadJsonAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath)) return Status("not_initialized", initialized: false);
        try
        {
            await using var connection = await SqliteConnectionFactory.OpenAsync(
                databasePath,
                readOnly: true,
                cancellationToken);
            return JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                initialized = true,
                mode = "ready",
                manifests = await CountAsync(connection, "handoff_manifest", null, cancellationToken),
                parsePending = await CountAsync(connection, "parse_job", "state = 'pending'", cancellationToken),
                parseLeased = await CountAsync(connection, "parse_job", "state = 'leased'", cancellationToken),
                parseCompleted = await CountAsync(connection, "parse_job", "state = 'completed'", cancellationToken),
                parseFailed = await CountAsync(connection, "parse_job", "state = 'failed'", cancellationToken),
                exportedItems = await CountAsync(connection, "exported_item", null, cancellationToken),
                outboxPending = await CountAsync(connection, "outbox", "state = 'pending'", cancellationToken),
                outboxLeased = await CountAsync(connection, "outbox", "state = 'leased'", cancellationToken),
                outboxAcknowledged = await CountAsync(connection, "outbox", "state = 'acknowledged'", cancellationToken),
                outboxQuarantined = await CountAsync(connection, "outbox", "state = 'quarantined'", cancellationToken),
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Status("state_unreadable", initialized: false);
        }
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string table,
        string? predicate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}" +
            (predicate is null ? ";" : $" WHERE {predicate};");
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static string Status(string mode, bool initialized) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            initialized,
            mode,
            manifests = 0,
            parsePending = 0,
            parseLeased = 0,
            parseCompleted = 0,
            parseFailed = 0,
            exportedItems = 0,
            outboxPending = 0,
            outboxLeased = 0,
            outboxAcknowledged = 0,
            outboxQuarantined = 0,
        });
}
