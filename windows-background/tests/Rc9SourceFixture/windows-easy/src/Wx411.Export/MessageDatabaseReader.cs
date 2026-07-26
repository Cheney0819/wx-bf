using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Wx411.Export;

public static partial class MessageDatabaseReader
{
    public static async Task<MessageDatabaseSnapshot> ReadAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("Message database was not found.", databasePath);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var sourceDatabase = Path.GetFileName(databasePath);
        var namesById = await ReadNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        var tables = await ReadMessageTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        var tableMappings = BuildTableMappings(namesById.Values, tables);
        var conversations = new List<SourceConversation>(tables.Count);
        var messages = new List<SourceMessage>();

        foreach (var table in tables.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var username = tableMappings[table];
            var tableMessages = await ReadMessagesAsync(
                connection,
                sourceDatabase,
                table,
                username,
                namesById,
                cancellationToken).ConfigureAwait(false);
            conversations.Add(new SourceConversation(sourceDatabase, username, table, tableMessages.Count));
            messages.AddRange(tableMessages);
        }

        return new MessageDatabaseSnapshot(
            conversations.AsReadOnly(),
            messages.AsReadOnly(),
            namesById.Values.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static async Task<Dictionary<long, string>> ReadNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid, user_name FROM Name2Id ORDER BY rowid;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var names = new Dictionary<long, string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            names.Add(reader.GetInt64(0), reader.GetString(1));
        return names;
    }

    private static async Task<List<string>> ReadMessageTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table' AND name LIKE 'Msg_%'
            ORDER BY name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tables = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var table = reader.GetString(0);
            if (!MessageTablePattern().IsMatch(table))
                throw new InvalidDataException($"Message table has an invalid name: {table}");
            tables.Add(table);
        }

        return tables;
    }

    private static Dictionary<string, string> BuildTableMappings(
        IEnumerable<string> usernames,
        IReadOnlyCollection<string> tables)
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var username in usernames)
        {
            var table = BuildMessageTableName(username);
            if (!tables.Contains(table, StringComparer.Ordinal))
                continue;
            if (!mappings.TryAdd(table, username))
                throw new InvalidDataException($"Message table {table} maps to more than one Name2Id row.");
        }

        foreach (var table in tables)
            if (!mappings.ContainsKey(table))
                throw new InvalidDataException($"Message table {table} does not map to a Name2Id row.");

        return mappings;
    }

    private static async Task<List<SourceMessage>> ReadMessagesAsync(
        SqliteConnection connection,
        string sourceDatabase,
        string table,
        string conversationUsername,
        IReadOnlyDictionary<long, string> namesById,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT local_id, server_id, local_type, sort_seq, real_sender_id,
                   create_time, status, upload_status, download_status, server_seq,
                   origin_source, source, message_content, packed_info_data
            FROM "{table}"
            ORDER BY local_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<SourceMessage>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var senderId = NullableInt64(reader, 4);
            var createTime = NullableInt64(reader, 5);
            messages.Add(new SourceMessage(
                sourceDatabase,
                table,
                reader.GetInt64(0),
                conversationUsername,
                senderId is long id && namesById.TryGetValue(id, out var sender) ? sender : null,
                NullableInt64(reader, 1),
                NullableInt64(reader, 2),
                NullableInt64(reader, 3),
                createTime,
                FormatUnixTime(createTime),
                NullableInt64(reader, 6),
                NullableInt64(reader, 7),
                NullableInt64(reader, 8),
                NullableInt64(reader, 9),
                NullableInt64(reader, 10),
                NullableString(reader, 11),
                NullableString(reader, 12),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<byte[]>(13)));
        }

        return messages;
    }

    private static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string? FormatUnixTime(long? value)
    {
        if (value is null) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value.Value).ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string BuildMessageTableName(string username)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(username));
        return "Msg_" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    [GeneratedRegex("^Msg_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex MessageTablePattern();
}
