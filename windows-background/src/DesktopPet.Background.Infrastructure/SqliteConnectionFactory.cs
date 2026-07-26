using Microsoft.Data.Sqlite;

namespace DesktopPet.Background.Infrastructure;

public static class SqliteConnectionFactory
{
    public static async Task<SqliteConnection> OpenAsync(
        string databasePath,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("Database path must include a directory.", nameof(databasePath));
        if (!readOnly) Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = readOnly
                ? "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"
                : "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; " +
                  "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
