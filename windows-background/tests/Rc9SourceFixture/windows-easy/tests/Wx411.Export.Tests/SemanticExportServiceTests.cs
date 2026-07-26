using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Wx411.Export.Tests;

public sealed class SemanticExportServiceTests
{
    [Fact]
    public async Task ExportAsyncWritesVerifiedNormalizedDatabaseAndSummaryWithoutChangingSources()
    {
        using var fixture = TestDatabaseSet.CreateMessages().AddContacts().AddSessions();
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");
        var before = SourceHashes(fixture);

        var result = await new SemanticExportService().ExportAsync(
            new SemanticExportRequest(fixture.Directory, output));

        Assert.Equal(2, result.MessageCount);
        Assert.Equal(1, result.ConversationCount);
        Assert.Equal(5, result.IdentityCount);
        Assert.Equal(1, result.ChatRoomCount);
        Assert.Equal(2, result.ChatRoomMemberCount);
        Assert.Equal(0, result.UnresolvedChatRoomMemberCount);
        Assert.Equal(before, SourceHashes(fixture));
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(result.SummaryPath));

        using var connection = new SqliteConnection($"Data Source={output};Mode=ReadOnly;Pooling=False");
        connection.Open();
        Assert.Equal("ok", Scalar<string>(connection, "PRAGMA integrity_check;"));
        Assert.Equal(2L, Scalar<long>(connection, "SELECT count(*) FROM messages;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT count(*) FROM conversations;"));
        Assert.Equal(5L, Scalar<long>(connection, "SELECT count(*) FROM identities;"));
        Assert.Equal("member_a", Scalar<string>(connection,
            "SELECT sender_username FROM messages WHERE local_id=1;"));
        Assert.Equal("Alice Remark", Scalar<string>(connection,
            "SELECT display_name FROM identities WHERE username='member_a';"));

        using var summary = JsonDocument.Parse(File.ReadAllText(result.SummaryPath));
        Assert.Equal(2, summary.RootElement.GetProperty("messageCount").GetInt32());
        Assert.Equal(3, summary.RootElement.GetProperty("sourceSha256").EnumerateObject().Count());
        Assert.Empty(Directory.EnumerateFiles(fixture.Directory, ".wx411-export.*.tmp*"));
    }

    [Fact]
    public async Task ExportAsyncRequiresExplicitOverwriteAndCleansTemporaryFiles()
    {
        using var fixture = TestDatabaseSet.CreateMessages();
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");
        var service = new SemanticExportService();
        await service.ExportAsync(new SemanticExportRequest(fixture.Directory, output));

        await Assert.ThrowsAsync<IOException>(() =>
            service.ExportAsync(new SemanticExportRequest(fixture.Directory, output)));

        var replacement = await service.ExportAsync(
            new SemanticExportRequest(fixture.Directory, output, Overwrite: true));
        Assert.Equal(2, replacement.MessageCount);
        Assert.Empty(Directory.EnumerateFiles(fixture.Directory, ".wx411-export.*.tmp*"));
    }

    [Fact]
    public async Task ExportAsyncRejectsSummaryPathEqualToDatabasePathBeforeWriting()
    {
        using var fixture = TestDatabaseSet.CreateMessages();
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new SemanticExportService().ExportAsync(
                new SemanticExportRequest(fixture.Directory, output, output)));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ExportAsyncIncludesBusinessMessagesWithSourceDatabaseAwareKeys()
    {
        using var fixture = TestDatabaseSet.CreateMessages().AddBusinessMessages();
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");

        var result = await new SemanticExportService().ExportAsync(
            new SemanticExportRequest(fixture.Directory, output));

        Assert.Equal(3, result.MessageCount);
        using var connection = new SqliteConnection($"Data Source={output};Mode=ReadOnly;Pooling=False");
        connection.Open();
        Assert.Equal(2L, Scalar<long>(connection,
            "SELECT count(*) FROM messages WHERE source_table LIKE 'Msg_%' AND local_id=1;"));
        Assert.Equal(2L, Scalar<long>(connection,
            "SELECT count(DISTINCT source_database) FROM messages WHERE local_id=1;"));
    }

    [Fact]
    public async Task ExportAsyncKeepsSemanticallyIdenticalMessagesWithDistinctServerIds()
    {
        using var fixture = TestDatabaseSet.CreateMessages()
            .AddBusinessMessages(duplicateMainMessageSemantics: true);
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");

        await new SemanticExportService().ExportAsync(
            new SemanticExportRequest(fixture.Directory, output));

        using var connection = new SqliteConnection($"Data Source={output};Mode=ReadOnly;Pooling=False");
        connection.Open();
        Assert.Equal(2L, Scalar<long>(connection, """
            SELECT count(*) FROM messages
            WHERE conversation_username='room@chatroom'
              AND sender_username='member_a'
              AND create_time=1784812299
              AND local_type=1
              AND message_content='hello';
            """));
        Assert.Equal(2L, Scalar<long>(connection, """
            SELECT count(DISTINCT server_id) FROM messages
            WHERE conversation_username='room@chatroom'
              AND sender_username='member_a'
              AND create_time=1784812299
              AND local_type=1
              AND message_content='hello';
            """));
    }

    [Fact]
    public async Task ExportAsyncRejectsOptionalSourceThatAppearsAfterGenerationCapture()
    {
        using var fixture = TestDatabaseSet.CreateMessages();
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");
        var progress = new InlineProgress<SemanticExportProgress>(value =>
        {
            if (value.Percent == 10)
                fixture.AddBusinessMessages();
        });

        var error = await Assert.ThrowsAsync<IOException>(() =>
            new SemanticExportService().ExportAsync(
                new SemanticExportRequest(fixture.Directory, output), progress));

        Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ExportAsyncAddsMessageNameIdentityMissingFromContacts()
    {
        using var fixture = TestDatabaseSet.CreateMessages().AddMessageOnlySender().AddContacts();
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");

        var result = await new SemanticExportService().ExportAsync(
            new SemanticExportRequest(fixture.Directory, output));

        Assert.Equal(6, result.IdentityCount);
        using var connection = new SqliteConnection($"Data Source={output};Mode=ReadOnly;Pooling=False");
        connection.Open();
        Assert.Equal("message_name", Scalar<string>(connection,
            "SELECT record_kind FROM identities WHERE username='message_only';"));
    }

    [Fact]
    public async Task ExportAsyncPrefersContactWhenStrangerHasSameUsername()
    {
        using var fixture = TestDatabaseSet.CreateMessages().AddContacts().AddDuplicateStrangerUsername();
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");

        await new SemanticExportService().ExportAsync(
            new SemanticExportRequest(fixture.Directory, output));

        using var connection = new SqliteConnection($"Data Source={output};Mode=ReadOnly;Pooling=False");
        connection.Open();
        Assert.Equal("Alice Remark", Scalar<string>(connection,
            "SELECT display_name FROM identities WHERE username='member_a';"));
        Assert.Equal(1L, Scalar<long>(connection,
            "SELECT count(*) FROM identities WHERE username='member_a';"));
    }

    [Fact]
    public async Task ExportAsyncHashesWalAndRejectsSourceGenerationChanges()
    {
        using var fixture = TestDatabaseSet.CreateMessages().EnableMessageWal();
        fixture.AppendWalMessage(3, 1003);
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");
        var progress = new InlineProgress<SemanticExportProgress>(value =>
        {
            if (value.Percent == 65)
                fixture.AppendWalMessage(4, 1004);
        });

        var error = await Assert.ThrowsAsync<IOException>(() =>
            new SemanticExportService().ExportAsync(
                new SemanticExportRequest(fixture.Directory, output), progress));

        Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ExportAsyncRecordsStableWalHashInSummary()
    {
        using var fixture = TestDatabaseSet.CreateMessages().EnableMessageWal();
        fixture.AppendWalMessage(3, 1003);
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");

        var result = await new SemanticExportService().ExportAsync(
            new SemanticExportRequest(fixture.Directory, output));

        Assert.Equal(3, result.MessageCount);
        Assert.Contains("message_0.readable.sqlite-wal", result.SourceSha256.Keys);
    }

    [Fact]
    public async Task ExportAsyncRollsBackDatabaseWhenSummaryPublicationFails()
    {
        using var fixture = TestDatabaseSet.CreateMessages();
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");
        var invalidSummaryPath = Path.Combine(fixture.Directory, "summary-directory");
        Directory.CreateDirectory(invalidSummaryPath);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            new SemanticExportService().ExportAsync(
                new SemanticExportRequest(fixture.Directory, output, invalidSummaryPath)));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ExportAsyncRestoresExistingDatabaseWhenReplacementSummaryPublicationFails()
    {
        using var fixture = TestDatabaseSet.CreateMessages();
        var output = Path.Combine(fixture.Directory, "wx411-export.sqlite");
        var original = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(output, original);
        var invalidSummaryPath = Path.Combine(fixture.Directory, "summary-directory");
        Directory.CreateDirectory(invalidSummaryPath);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            new SemanticExportService().ExportAsync(
                new SemanticExportRequest(fixture.Directory, output, invalidSummaryPath, Overwrite: true)));

        Assert.Equal(original, File.ReadAllBytes(output));
    }

    private static Dictionary<string, string> SourceHashes(TestDatabaseSet fixture) =>
        new[] { fixture.MessagePath, fixture.ContactPath, fixture.SessionPath }
            .Where(File.Exists)
            .ToDictionary(path => Path.GetFileName(path)!, path =>
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
