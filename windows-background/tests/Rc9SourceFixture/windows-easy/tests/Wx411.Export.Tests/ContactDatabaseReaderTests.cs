namespace Wx411.Export.Tests;

public sealed class ContactDatabaseReaderTests
{
    [Fact]
    public async Task ReadOptionalAsyncClassifiesOnlyEvidenceBackedKinds()
    {
        using var fixture = TestDatabaseSet.CreateMessages().AddContacts();

        var snapshot = await ContactDatabaseReader.ReadOptionalAsync(fixture.ContactPath);

        Assert.Equal(5, snapshot.Identities.Count);
        Assert.Equal("contact_record", Find("member_a").RecordKind);
        Assert.Equal("Alice Remark", Find("member_a").DisplayName);
        Assert.Equal("business", Find("official").RecordKind);
        Assert.Equal("Official Name", Find("official").DisplayName);
        Assert.Equal("chatroom", Find("room@chatroom").RecordKind);
        Assert.Equal("stranger", Find("stranger_a").RecordKind);
        Assert.Equal("member_b", Find("member_b").DisplayName);

        var room = Assert.Single(snapshot.ChatRooms);
        Assert.Equal("member_a", room.OwnerUsername);
        Assert.Equal("Welcome", room.Announcement);
        Assert.Equal(2, snapshot.Members.Count);
        Assert.Equal(0, snapshot.UnresolvedMemberEdges);

        SourceIdentity Find(string username) =>
            Assert.Single(snapshot.Identities, identity => identity.Username == username);
    }

    [Fact]
    public async Task ReadOptionalAsyncCountsAndSkipsUnresolvedMembershipEdges()
    {
        using var fixture = TestDatabaseSet.CreateMessages().AddContacts(addUnresolvedMember: true);

        var snapshot = await ContactDatabaseReader.ReadOptionalAsync(fixture.ContactPath);

        Assert.Equal(2, snapshot.Members.Count);
        Assert.Equal(1, snapshot.UnresolvedMemberEdges);
    }

    [Fact]
    public async Task ReadOptionalAsyncReturnsEmptySnapshotWhenFileIsAbsent()
    {
        using var fixture = TestDatabaseSet.CreateMessages();

        var snapshot = await ContactDatabaseReader.ReadOptionalAsync(fixture.ContactPath);

        Assert.Empty(snapshot.Identities);
        Assert.Empty(snapshot.ChatRooms);
        Assert.Empty(snapshot.Members);
    }
}
