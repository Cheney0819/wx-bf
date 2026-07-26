namespace Wx411.Export;

public sealed record SourceConversation(
    string SourceDatabase,
    string Username,
    string SourceTable,
    int MessageCount);

public sealed record SourceMessage(
    string SourceDatabase,
    string SourceTable,
    long LocalId,
    string ConversationUsername,
    string? SenderUsername,
    long? ServerId,
    long? LocalType,
    long? SortSeq,
    long? CreateTime,
    string? CreateTimeUtc,
    long? Status,
    long? UploadStatus,
    long? DownloadStatus,
    long? ServerSeq,
    long? OriginSource,
    string? Source,
    string? MessageContent,
    byte[]? PackedInfoData);

public sealed record MessageDatabaseSnapshot(
    IReadOnlyList<SourceConversation> Conversations,
    IReadOnlyList<SourceMessage> Messages,
    IReadOnlyList<string> Names)
{
    public int NameCount => Names.Count;
}

public sealed record SourceIdentity(
    long SourceId,
    string Username,
    string DisplayName,
    string RecordKind,
    string? Alias,
    long? LocalType,
    long? Flag,
    long? DeleteFlag,
    long? VerifyFlag,
    string? Description);

public sealed record SourceChatRoom(
    string Username,
    string? OwnerUsername,
    string? Announcement);

public sealed record SourceChatRoomMember(
    string RoomUsername,
    string MemberUsername);

public sealed record ContactDatabaseSnapshot(
    IReadOnlyList<SourceIdentity> Identities,
    IReadOnlyList<SourceChatRoom> ChatRooms,
    IReadOnlyList<SourceChatRoomMember> Members,
    int UnresolvedMemberEdges)
{
    public static ContactDatabaseSnapshot Empty { get; } = new([], [], [], 0);
}

public sealed record SourceSession(
    string Username,
    long? Type,
    long? LastTimestamp,
    string? Summary,
    string? FallbackTitle);

public sealed record SessionDatabaseSnapshot(IReadOnlyList<SourceSession> Sessions)
{
    public static SessionDatabaseSnapshot Empty { get; } = new([]);
}

public sealed record SemanticExportRequest(
    string InputDirectory,
    string OutputPath,
    string? SummaryPath = null,
    bool Overwrite = false);

public sealed record SemanticExportProgress(
    int Percent,
    string Stage,
    string? Detail = null);

public sealed record SemanticExportResult(
    string OutputPath,
    string SummaryPath,
    string OutputSha256,
    int MessageCount,
    int ConversationCount,
    int IdentityCount,
    int ChatRoomCount,
    int ChatRoomMemberCount,
    int UnresolvedChatRoomMemberCount,
    IReadOnlyDictionary<string, string> SourceSha256,
    IReadOnlyList<string> Warnings);
