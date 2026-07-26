using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Wx411.Export.Tests;

internal sealed class TestDatabaseSet : IDisposable
{
    private readonly List<SqliteConnection> heldConnections = [];

    private TestDatabaseSet(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public string MessagePath => Path.Combine(Directory, "message_0.readable.sqlite");

    public string BusinessMessagePath => Path.Combine(Directory, "biz_message_0.readable.sqlite");

    public string ContactPath => Path.Combine(Directory, "contact.readable.sqlite");

    public string SessionPath => Path.Combine(Directory, "session.readable.sqlite");

    public static TestDatabaseSet CreateMessages(bool addUnmappedTable = false)
    {
        var set = new TestDatabaseSet(Path.Combine(Path.GetTempPath(), $"wx411-export-test-{Guid.NewGuid():N}"));
        System.IO.Directory.CreateDirectory(set.Directory);

        using var connection = OpenWritable(set.MessagePath);
        Execute(connection, """
            CREATE TABLE Name2Id(user_name TEXT PRIMARY KEY, is_session INTEGER);
            INSERT INTO Name2Id(rowid, user_name, is_session) VALUES
              (1, 'room@chatroom', 1),
              (2, 'member_a', 0);
            """);

        var table = MessageTableName("room@chatroom");
        CreateMessageTable(connection, table);
        Execute(connection, $"""
            INSERT INTO "{table}"(
              local_id, server_id, local_type, sort_seq, real_sender_id,
              create_time, status, upload_status, download_status, server_seq,
              origin_source, source, message_content, packed_info_data)
            VALUES
              (1, 1001, 1, 10, 2, 1784812299, 2, 3, 4, 11, 5, 'source-a', 'hello', X'0102'),
              (2, 1002, 47, 20, 1, 1784812300, 6, 7, 8, 12, 9, 'source-b', 'sticker', X'0304');
            """);

        if (addUnmappedTable)
            CreateMessageTable(connection, "Msg_00000000000000000000000000000000");

        return set;
    }

    public TestDatabaseSet AddContacts(bool addUnresolvedMember = false)
    {
        using var connection = OpenWritable(ContactPath);
        Execute(connection, """
            CREATE TABLE contact(
              id INTEGER PRIMARY KEY, username TEXT, local_type INTEGER, alias TEXT,
              flag INTEGER, delete_flag INTEGER, verify_flag INTEGER, remark TEXT,
              nick_name TEXT, description TEXT);
            CREATE TABLE stranger(
              id INTEGER PRIMARY KEY, username TEXT, local_type INTEGER, alias TEXT,
              flag INTEGER, delete_flag INTEGER, verify_flag INTEGER, remark TEXT,
              nick_name TEXT, description TEXT);
            CREATE TABLE biz_info(id INTEGER PRIMARY KEY, username TEXT);
            CREATE TABLE chat_room(id INTEGER PRIMARY KEY, username TEXT, owner TEXT, ext_buffer BLOB);
            CREATE TABLE chat_room_info_detail(
              room_id_ INTEGER PRIMARY KEY, username_ TEXT, announcement_ TEXT);
            CREATE TABLE chatroom_member(
              room_id INTEGER, member_id INTEGER,
              CONSTRAINT room_member UNIQUE(room_id, member_id));

            INSERT INTO contact VALUES
              (1, 'member_a', 1, 'alice-alias', 11, 0, 0, 'Alice Remark', 'Alice Nick', 'normal'),
              (2, 'official', 2, NULL, 12, 0, 8, '', 'Official Name', 'business'),
              (3, 'room@chatroom', 3, NULL, 13, 0, 0, '', 'Room Name', 'room'),
              (4, 'member_b', 4, NULL, 14, 0, 0, '', '', 'member');
            INSERT INTO stranger VALUES
              (10, 'stranger_a', 10, NULL, 20, 0, 0, '', 'Stranger Name', 'stranger');
            INSERT INTO biz_info VALUES(2, 'official');
            INSERT INTO chat_room VALUES(3, 'room@chatroom', 'member_a', NULL);
            INSERT INTO chat_room_info_detail VALUES(3, 'room@chatroom', 'Welcome');
            INSERT INTO chatroom_member VALUES(3, 1), (3, 4);
            """);
        if (addUnresolvedMember)
            Execute(connection, "INSERT INTO chatroom_member VALUES(3, 999);");
        return this;
    }

    public TestDatabaseSet AddBusinessMessages(bool duplicateMainMessageSemantics = false)
    {
        using var connection = OpenWritable(BusinessMessagePath);
        var sender = duplicateMainMessageSemantics ? "member_a" : "member_b";
        Execute(connection, """
            CREATE TABLE Name2Id(user_name TEXT PRIMARY KEY, is_session INTEGER);
            """);
        Execute(connection, $"""
            INSERT INTO Name2Id(rowid, user_name, is_session) VALUES
              (1, 'room@chatroom', 1),
              (2, '{sender}', 0);
            """);

        var table = MessageTableName("room@chatroom");
        CreateMessageTable(connection, table);
        Execute(connection, $"""
            INSERT INTO "{table}"(
              local_id, server_id, local_type, sort_seq, real_sender_id,
              create_time, status, upload_status, download_status, server_seq,
              origin_source, source, message_content, packed_info_data)
            VALUES
              (1, 2001, 1, 30, 2, {(duplicateMainMessageSemantics ? 1784812299 : 1784812301)},
               2, 3, 4, 21, 5,
               '{(duplicateMainMessageSemantics ? "source-a" : "biz-source")}',
               '{(duplicateMainMessageSemantics ? "hello" : "business")}',
               X'{(duplicateMainMessageSemantics ? "0102" : "0506")}');
            """);
        return this;
    }

    public TestDatabaseSet AddMessageOnlySender()
    {
        using var connection = OpenWritable(MessagePath);
        Execute(connection, "INSERT INTO Name2Id(rowid, user_name, is_session) VALUES(3, 'message_only', 0);");
        var table = MessageTableName("room@chatroom");
        Execute(connection, $"""
            INSERT INTO "{table}"(
              local_id, server_id, local_type, sort_seq, real_sender_id,
              create_time, status, upload_status, download_status, server_seq,
              origin_source, source, message_content, packed_info_data)
            VALUES
              (3, 1003, 1, 30, 3, 1784812301, 2, 3, 4, 13, 5, 'source-c', 'missing identity', X'0506');
            """);
        return this;
    }

    public TestDatabaseSet AddDuplicateStrangerUsername()
    {
        using var connection = OpenWritable(ContactPath);
        Execute(connection, """
            INSERT INTO stranger VALUES
              (11, 'member_a', 10, NULL, 20, 0, 0, 'Wrong Stranger', 'Wrong Stranger', 'duplicate');
            """);
        return this;
    }

    public TestDatabaseSet EnableMessageWal()
    {
        var connection = OpenWritable(MessagePath);
        Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        heldConnections.Add(connection);
        return this;
    }

    public void AppendWalMessage(long localId, long serverId)
    {
        var connection = heldConnections.Single();
        var table = MessageTableName("room@chatroom");
        Execute(connection, $"""
            INSERT INTO "{table}"(
              local_id, server_id, local_type, sort_seq, real_sender_id,
              create_time, status, upload_status, download_status, server_seq,
              origin_source, source, message_content, packed_info_data)
            VALUES
              ({localId}, {serverId}, 1, {localId * 10}, 2, 1784812302, 2, 3, 4,
               {serverId}, 5, 'wal-source', 'wal message', X'0708');
            """);
    }

    public TestDatabaseSet AddSessions()
    {
        using var connection = OpenWritable(SessionPath);
        Execute(connection, """
            CREATE TABLE SessionTable(
              username TEXT PRIMARY KEY, type INTEGER, last_timestamp INTEGER,
              summary TEXT);
            CREATE TABLE SessionNoContactInfoTable(
              username TEXT PRIMARY KEY, session_title TEXT);
            INSERT INTO SessionTable VALUES('room@chatroom', 3, 1784812400, 'last message');
            INSERT INTO SessionNoContactInfoTable VALUES('room@chatroom', 'Fallback Room Title');
            """);
        return this;
    }

    public void Dispose()
    {
        foreach (var connection in heldConnections)
            connection.Dispose();
        if (System.IO.Directory.Exists(Directory))
            System.IO.Directory.Delete(Directory, recursive: true);
    }

    internal static SqliteConnection OpenWritable(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        return connection;
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void CreateMessageTable(SqliteConnection connection, string table) =>
        Execute(connection, $"""
            CREATE TABLE "{table}"(
              local_id INTEGER PRIMARY KEY,
              server_id INTEGER,
              local_type INTEGER,
              sort_seq INTEGER,
              real_sender_id INTEGER,
              create_time INTEGER,
              status INTEGER,
              upload_status INTEGER,
              download_status INTEGER,
              server_seq INTEGER,
              origin_source INTEGER,
              source TEXT,
              message_content TEXT,
              compress_content TEXT,
              packed_info_data BLOB,
              WCDB_CT_message_content INTEGER,
              WCDB_CT_source INTEGER);
            """);

    private static string MessageTableName(string username)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(username));
        return "Msg_" + Convert.ToHexString(digest).ToLowerInvariant();
    }
}
