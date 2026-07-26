namespace Wx411.Export.Tests;

public sealed class MessageDatabaseReaderTests
{
    [Fact]
    public async Task ReadAsyncMapsConversationHashAndSenderRowIds()
    {
        using var fixture = TestDatabaseSet.CreateMessages();

        var snapshot = await MessageDatabaseReader.ReadAsync(fixture.MessagePath);

        var conversation = Assert.Single(snapshot.Conversations);
        Assert.Equal("room@chatroom", conversation.Username);
        Assert.Equal(2, conversation.MessageCount);

        Assert.Collection(
            snapshot.Messages.OrderBy(message => message.LocalId),
            first =>
            {
                Assert.Equal("room@chatroom", first.ConversationUsername);
                Assert.Equal("member_a", first.SenderUsername);
                Assert.Equal("hello", first.MessageContent);
                Assert.Equal(new byte[] { 1, 2 }, first.PackedInfoData);
                Assert.Equal("2026-07-23T13:11:39.0000000+00:00", first.CreateTimeUtc);
            },
            second =>
            {
                Assert.Equal("room@chatroom", second.SenderUsername);
                Assert.Equal(47, second.LocalType);
                Assert.Equal("source-b", second.Source);
            });
    }

    [Fact]
    public async Task ReadAsyncRejectsUnmappedMessageTable()
    {
        using var fixture = TestDatabaseSet.CreateMessages(addUnmappedTable: true);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => MessageDatabaseReader.ReadAsync(fixture.MessagePath));

        Assert.Contains("does not map", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
