using Microsoft.Data.Sqlite;

namespace Wx411.Export;

public static class SessionDatabaseReader
{
    public static async Task<SessionDatabaseSnapshot> ReadOptionalAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath)) return SessionDatabaseSnapshot.Empty;

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session.username, session.type, session.last_timestamp, session.summary,
                   fallback.session_title
            FROM SessionTable AS session
            LEFT JOIN SessionNoContactInfoTable AS fallback
              ON fallback.username = session.username
            ORDER BY session.username;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var sessions = new List<SourceSession>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sessions.Add(new SourceSession(
                reader.GetString(0),
                NullableInt64(reader, 1),
                NullableInt64(reader, 2),
                NullableString(reader, 3),
                NullableString(reader, 4)));
        return new SessionDatabaseSnapshot(sessions.AsReadOnly());
    }

    private static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
