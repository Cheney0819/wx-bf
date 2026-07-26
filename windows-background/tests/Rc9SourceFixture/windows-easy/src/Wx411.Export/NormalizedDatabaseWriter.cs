using Microsoft.Data.Sqlite;

namespace Wx411.Export;

internal static class NormalizedDatabaseWriter
{
    public static async Task WriteAsync(
        string path,
        MessageDatabaseSnapshot messages,
        ContactDatabaseSnapshot contacts,
        SessionDatabaseSnapshot sessions,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SchemaSql, cancellationToken).ConfigureAwait(false);

        await using var transaction = connection.BeginTransaction();
        await WriteIdentitiesAsync(connection, transaction, contacts.Identities, cancellationToken).ConfigureAwait(false);
        await WriteChatRoomsAsync(connection, transaction, contacts.ChatRooms, cancellationToken).ConfigureAwait(false);
        await WriteMembersAsync(connection, transaction, contacts.Members, cancellationToken).ConfigureAwait(false);
        await WriteConversationsAsync(
            connection,
            transaction,
            messages.Conversations,
            contacts.Identities,
            sessions.Sessions,
            cancellationToken).ConfigureAwait(false);
        await WriteMessagesAsync(connection, transaction, messages.Messages, cancellationToken).ConfigureAwait(false);
        await WriteMetaAsync(connection, transaction, messages, contacts, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<SourceIdentity> values,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO identities(
              username, source_id, display_name, record_kind, alias, local_type,
              flag, delete_flag, verify_flag, description)
            VALUES($username, $source_id, $display_name, $record_kind, $alias,
              $local_type, $flag, $delete_flag, $verify_flag, $description);
            """;
        foreach (var value in values)
        {
            await using var command = CreateCommand(connection, transaction, sql);
            Add(command, "$username", value.Username);
            Add(command, "$source_id", value.SourceId);
            Add(command, "$display_name", value.DisplayName);
            Add(command, "$record_kind", value.RecordKind);
            Add(command, "$alias", value.Alias);
            Add(command, "$local_type", value.LocalType);
            Add(command, "$flag", value.Flag);
            Add(command, "$delete_flag", value.DeleteFlag);
            Add(command, "$verify_flag", value.VerifyFlag);
            Add(command, "$description", value.Description);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteChatRoomsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<SourceChatRoom> values,
        CancellationToken cancellationToken)
    {
        foreach (var value in values)
        {
            await using var command = CreateCommand(connection, transaction,
                "INSERT INTO chatrooms(username, owner_username, announcement) VALUES($username, $owner, $announcement);");
            Add(command, "$username", value.Username);
            Add(command, "$owner", value.OwnerUsername);
            Add(command, "$announcement", value.Announcement);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteMembersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<SourceChatRoomMember> values,
        CancellationToken cancellationToken)
    {
        foreach (var value in values)
        {
            await using var command = CreateCommand(connection, transaction,
                "INSERT INTO chatroom_members(room_username, member_username) VALUES($room, $member);");
            Add(command, "$room", value.RoomUsername);
            Add(command, "$member", value.MemberUsername);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteConversationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<SourceConversation> values,
        IEnumerable<SourceIdentity> identities,
        IEnumerable<SourceSession> sessions,
        CancellationToken cancellationToken)
    {
        var identityMap = identities.ToDictionary(value => value.Username, StringComparer.Ordinal);
        var sessionMap = sessions.ToDictionary(value => value.Username, StringComparer.Ordinal);
        const string sql = """
            INSERT INTO conversations(
              source_database, username, source_table, display_name, conversation_kind,
              message_count, session_type, last_timestamp, summary)
            VALUES($source_database, $username, $source_table, $display_name, $kind,
              $message_count, $session_type, $last_timestamp, $summary);
            """;
        foreach (var value in values)
        {
            identityMap.TryGetValue(value.Username, out var identity);
            sessionMap.TryGetValue(value.Username, out var session);
            var displayName = identity?.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName)) displayName = session?.FallbackTitle;
            if (string.IsNullOrWhiteSpace(displayName)) displayName = value.Username;
            var kind = identity?.RecordKind ??
                       (value.Username.EndsWith("@chatroom", StringComparison.Ordinal)
                           ? "chatroom"
                           : "conversation");

            await using var command = CreateCommand(connection, transaction, sql);
            Add(command, "$source_database", value.SourceDatabase);
            Add(command, "$username", value.Username);
            Add(command, "$source_table", value.SourceTable);
            Add(command, "$display_name", displayName);
            Add(command, "$kind", kind);
            Add(command, "$message_count", value.MessageCount);
            Add(command, "$session_type", session?.Type);
            Add(command, "$last_timestamp", session?.LastTimestamp);
            Add(command, "$summary", session?.Summary);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteMessagesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<SourceMessage> values,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO messages(
              source_database, source_table, local_id, conversation_username, sender_username,
              server_id, local_type, sort_seq, create_time, create_time_utc,
              status, upload_status, download_status, server_seq, origin_source,
              source, message_content, packed_info_data)
            VALUES($source_database, $source_table, $local_id, $conversation, $sender,
              $server_id, $local_type, $sort_seq, $create_time, $create_time_utc,
              $status, $upload_status, $download_status, $server_seq, $origin_source,
              $source, $message_content, $packed_info_data);
            """;
        foreach (var value in values)
        {
            await using var command = CreateCommand(connection, transaction, sql);
            Add(command, "$source_database", value.SourceDatabase);
            Add(command, "$source_table", value.SourceTable);
            Add(command, "$local_id", value.LocalId);
            Add(command, "$conversation", value.ConversationUsername);
            Add(command, "$sender", value.SenderUsername);
            Add(command, "$server_id", value.ServerId);
            Add(command, "$local_type", value.LocalType);
            Add(command, "$sort_seq", value.SortSeq);
            Add(command, "$create_time", value.CreateTime);
            Add(command, "$create_time_utc", value.CreateTimeUtc);
            Add(command, "$status", value.Status);
            Add(command, "$upload_status", value.UploadStatus);
            Add(command, "$download_status", value.DownloadStatus);
            Add(command, "$server_seq", value.ServerSeq);
            Add(command, "$origin_source", value.OriginSource);
            Add(command, "$source", value.Source);
            Add(command, "$message_content", value.MessageContent);
            Add(command, "$packed_info_data", value.PackedInfoData);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteMetaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MessageDatabaseSnapshot messages,
        ContactDatabaseSnapshot contacts,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format_version"] = "2",
            ["message_count"] = messages.Messages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["conversation_count"] = messages.Conversations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["identity_count"] = contacts.Identities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["chatroom_count"] = contacts.ChatRooms.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["chatroom_member_count"] = contacts.Members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var value in values)
        {
            await using var command = CreateCommand(connection, transaction,
                "INSERT INTO export_meta(key, value) VALUES($key, $value);");
            Add(command, "$key", value.Key);
            Add(command, "$value", value.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SchemaSql = """
        PRAGMA journal_mode = DELETE;
        PRAGMA synchronous = FULL;
        CREATE TABLE export_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE identities(
          username TEXT PRIMARY KEY,
          source_id INTEGER NOT NULL,
          display_name TEXT NOT NULL,
          record_kind TEXT NOT NULL,
          alias TEXT,
          local_type INTEGER,
          flag INTEGER,
          delete_flag INTEGER,
          verify_flag INTEGER,
          description TEXT);
        CREATE TABLE conversations(
          source_database TEXT NOT NULL,
          username TEXT NOT NULL,
          source_table TEXT NOT NULL,
          display_name TEXT NOT NULL,
          conversation_kind TEXT NOT NULL,
          message_count INTEGER NOT NULL,
          session_type INTEGER,
          last_timestamp INTEGER,
          summary TEXT,
          PRIMARY KEY(source_database, source_table));
        CREATE TABLE messages(
          source_database TEXT NOT NULL,
          source_table TEXT NOT NULL,
          local_id INTEGER NOT NULL,
          conversation_username TEXT NOT NULL,
          sender_username TEXT,
          server_id INTEGER,
          local_type INTEGER,
          sort_seq INTEGER,
          create_time INTEGER,
          create_time_utc TEXT,
          status INTEGER,
          upload_status INTEGER,
          download_status INTEGER,
          server_seq INTEGER,
          origin_source INTEGER,
          source TEXT,
          message_content TEXT,
          packed_info_data BLOB,
          PRIMARY KEY(source_database, source_table, local_id));
        CREATE TABLE chatrooms(
          username TEXT PRIMARY KEY,
          owner_username TEXT,
          announcement TEXT);
        CREATE TABLE chatroom_members(
          room_username TEXT NOT NULL,
          member_username TEXT NOT NULL,
          PRIMARY KEY(room_username, member_username));
        CREATE INDEX idx_messages_time ON messages(create_time);
        CREATE INDEX idx_messages_conversation_time ON messages(conversation_username, create_time);
        CREATE INDEX idx_messages_sender ON messages(sender_username);
        CREATE INDEX idx_conversations_username ON conversations(username);
        CREATE INDEX idx_identities_kind ON identities(record_kind);
        """;
}
