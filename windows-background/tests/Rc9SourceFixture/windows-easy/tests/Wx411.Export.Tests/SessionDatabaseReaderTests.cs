namespace Wx411.Export.Tests;

public sealed class SessionDatabaseReaderTests
{
    [Fact]
    public async Task ReadOptionalAsyncReturnsSessionMetadata()
    {
        using var fixture = TestDatabaseSet.CreateMessages().AddSessions();

        var snapshot = await SessionDatabaseReader.ReadOptionalAsync(fixture.SessionPath);

        var session = Assert.Single(snapshot.Sessions);
        Assert.Equal("room@chatroom", session.Username);
        Assert.Equal(3, session.Type);
        Assert.Equal(1784812400, session.LastTimestamp);
        Assert.Equal("last message", session.Summary);
        Assert.Equal("Fallback Room Title", session.FallbackTitle);
    }

    [Fact]
    public async Task ReadOptionalAsyncReturnsEmptySnapshotWhenFileIsAbsent()
    {
        using var fixture = TestDatabaseSet.CreateMessages();

        var snapshot = await SessionDatabaseReader.ReadOptionalAsync(fixture.SessionPath);

        Assert.Empty(snapshot.Sessions);
    }
}
