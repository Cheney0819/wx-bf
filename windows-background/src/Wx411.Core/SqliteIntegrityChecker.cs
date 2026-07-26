using Microsoft.Data.Sqlite;

namespace Wx411.Core;

public static class SqliteIntegrityChecker
{
    public static void VerifyFile(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            cancellationToken.ThrowIfCancellationRequested();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            command.CommandTimeout = 0;
            using var cancellationRegistration = cancellationToken.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            var results = new List<string>(4);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (results.Count < 16) results.Add(reader.GetString(0));
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (results.Count != 1 || !string.Equals(results[0], "ok", StringComparison.OrdinalIgnoreCase))
            {
                var detail = results.Count == 0 ? "no result" : string.Join(" | ", results);
                throw new IntegrityException("SQLite integrity_check failed: " + detail);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (SqliteException ex)
        {
            throw new IntegrityException("SQLite integrity_check failed: " + ex.Message);
        }
    }
}
