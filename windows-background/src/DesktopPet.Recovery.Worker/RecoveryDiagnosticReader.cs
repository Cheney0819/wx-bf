using System.Text.Json;
using DesktopPet.Background.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DesktopPet.Recovery.Worker;

public static class RecoveryDiagnosticReader
{
    public static async Task<string> ReadJsonAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
            return StatusJson("not_initialized", initialized: false);

        try
        {
            return await ReadExistingAsync(databasePath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return StatusJson("state_unreadable", initialized: false);
        }
    }

    private static async Task<string> ReadExistingAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = await SqliteConnectionFactory.OpenAsync(
            databasePath,
            readOnly: true,
            cancellationToken);
        await using var epochCommand = connection.CreateCommand();
        epochCommand.CommandText = """
            SELECT mode, restart_count, active_restart_suppressed, failure_code
            FROM recovery_epoch
            WHERE is_active = 1
            LIMIT 1;
            """;
        string mode = "no_active_epoch";
        var restartCount = 0;
        var restartSuppressed = true;
        string? failureCode = null;
        await using (var reader = await epochCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                mode = reader.GetString(0);
                restartCount = reader.GetInt32(1);
                restartSuppressed = reader.GetInt32(2) != 0;
                failureCode = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
        }

        await using var eventCommand = connection.CreateCommand();
        eventCommand.CommandText = """
            SELECT event_type, created_at_utc
            FROM runtime_event
            ORDER BY sequence DESC
            LIMIT 10;
            """;
        var recentEvents = new List<object>();
        await using (var reader = await eventCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                recentEvents.Add(new
                {
                    type = reader.GetString(0),
                    createdAtUtc = reader.GetString(1),
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            initialized = true,
            mode,
            restartCount,
            restartSuppressed,
            failureCode,
            recentEvents,
        });
    }

    private static string StatusJson(string mode, bool initialized) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            initialized,
            mode,
            restartCount = 0,
            restartSuppressed = true,
            failureCode = (string?)null,
            recentEvents = Array.Empty<object>(),
        });
}
