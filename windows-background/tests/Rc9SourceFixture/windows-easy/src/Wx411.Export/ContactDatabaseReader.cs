using Microsoft.Data.Sqlite;

namespace Wx411.Export;

public static class ContactDatabaseReader
{
    public static async Task<ContactDatabaseSnapshot> ReadOptionalAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath)) return ContactDatabaseSnapshot.Empty;

        await using var connection = OpenReadOnly(databasePath);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var businessIds = await ReadIdsAsync(connection, "biz_info", cancellationToken).ConfigureAwait(false);
        var roomIds = await ReadIdsAsync(connection, "chat_room", cancellationToken).ConfigureAwait(false);
        var identities = await ReadIdentitiesAsync(
            connection,
            businessIds,
            roomIds,
            cancellationToken).ConfigureAwait(false);
        var chatRooms = await ReadChatRoomsAsync(connection, cancellationToken).ConfigureAwait(false);
        var (members, unresolved) = await ReadMembersAsync(connection, cancellationToken).ConfigureAwait(false);

        return new ContactDatabaseSnapshot(
            identities.AsReadOnly(),
            chatRooms.AsReadOnly(),
            members.AsReadOnly(),
            unresolved);
    }

    private static SqliteConnection OpenReadOnly(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString());

    private static async Task<HashSet<long>> ReadIdsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM {table};";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(reader.GetInt64(0));
        return values;
    }

    private static async Task<List<SourceIdentity>> ReadIdentitiesAsync(
        SqliteConnection connection,
        IReadOnlySet<long> businessIds,
        IReadOnlySet<long> roomIds,
        CancellationToken cancellationToken)
    {
        var identities = new List<SourceIdentity>();
        await ReadIdentityTableAsync(
            connection,
            "contact",
            (id, username) => roomIds.Contains(id) || username.EndsWith("@chatroom", StringComparison.Ordinal)
                ? "chatroom"
                : businessIds.Contains(id) ? "business" : "contact_record",
            identities,
            cancellationToken).ConfigureAwait(false);
        await ReadIdentityTableAsync(
            connection,
            "stranger",
            (_, _) => "stranger",
            identities,
            cancellationToken).ConfigureAwait(false);
        return identities;
    }

    private static async Task ReadIdentityTableAsync(
        SqliteConnection connection,
        string table,
        Func<long, string, string> classify,
        ICollection<SourceIdentity> destination,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, username, alias, local_type, flag, delete_flag, verify_flag,
                   remark, nick_name, description
            FROM {table}
            ORDER BY id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = reader.GetInt64(0);
            var username = reader.GetString(1);
            var remark = NullableString(reader, 7);
            var nickname = NullableString(reader, 8);
            destination.Add(new SourceIdentity(
                id,
                username,
                FirstNonEmpty(remark, nickname, username),
                classify(id, username),
                NullableString(reader, 2),
                NullableInt64(reader, 3),
                NullableInt64(reader, 4),
                NullableInt64(reader, 5),
                NullableInt64(reader, 6),
                NullableString(reader, 9)));
        }
    }

    private static async Task<List<SourceChatRoom>> ReadChatRoomsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT room.username, room.owner, detail.announcement_
            FROM chat_room AS room
            LEFT JOIN chat_room_info_detail AS detail ON detail.room_id_ = room.id
            ORDER BY room.id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rooms = new List<SourceChatRoom>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rooms.Add(new SourceChatRoom(
                reader.GetString(0),
                NullableString(reader, 1),
                NullableString(reader, 2)));
        return rooms;
    }

    private static async Task<(List<SourceChatRoomMember> Members, int Unresolved)> ReadMembersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT room.username, member.username
            FROM chatroom_member AS edge
            LEFT JOIN contact AS room ON room.id = edge.room_id
            LEFT JOIN contact AS member ON member.id = edge.member_id
            ORDER BY edge.room_id, edge.member_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var members = new List<SourceChatRoomMember>();
        var unresolved = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                unresolved++;
                continue;
            }
            members.Add(new SourceChatRoomMember(reader.GetString(0), reader.GetString(1)));
        }
        return (members, unresolved);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
