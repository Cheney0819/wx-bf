using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.DataSync;

public sealed record ParserJobManifest(
    int SchemaVersion,
    string JobId,
    string SourceSetId,
    string InputRoot,
    string OutputRoot,
    IReadOnlyList<ParserDatabaseInput> Databases,
    int MaximumMessages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cursor = null);

public sealed record ParserDatabaseInput(
    string GenerationId,
    string RelativePath,
    string Path,
    string Sha256);

public sealed record BuiltParserJob(
    string JobRoot,
    string InputRoot,
    string OutputRoot,
    string JobManifestPath,
    ParserJobManifest Manifest);

public sealed record ParserInstallManifest(
    int SchemaVersion,
    string ExecutablePath,
    string Sha256);

public sealed record ParserSupervisorOptions(
    TimeSpan SoftTimeout,
    TimeSpan HardTimeout,
    int DiagnosticByteLimit,
    string RequiredExecutableName,
    TimeSpan PostKillGrace)
{
    public static ParserSupervisorOptions Default { get; } = new(
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(180),
        64 * 1024,
        "wx_parser.exe",
        TimeSpan.FromSeconds(2));
}

public interface IParserPostKillWaiter
{
    Task<bool> WaitAsync(Task completion, TimeSpan grace);
}

public sealed class ParserSupervisorException : InvalidOperationException
{
    public ParserSupervisorException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record ParserProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    bool SoftCancellationRequested,
    bool HardKilled);

public sealed record ParserResultDocument(
    int SchemaVersion,
    string JobId,
    string SourceSetId,
    IReadOnlyList<ParsedMessage> Messages,
    IReadOnlyList<ParsedContact> Contacts,
    IReadOnlyList<ParsedFavorite> Favorites,
    IReadOnlyList<ParserNotice> Notices,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor = null);

public sealed record ParsedMessage(
    [property: JsonPropertyName("wxid")] string Wxid,
    [property: JsonPropertyName("local_id")] long LocalId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("create_time")] long CreateTime,
    [property: JsonPropertyName("is_sender")] bool IsSender,
    [property: JsonPropertyName("nickname")] string Nickname,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("avatar")] string Avatar,
    [property: JsonPropertyName("msg_type")] int MessageType,
    [property: JsonPropertyName("msg_sub_type")] int MessageSubType,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("media_mime")] string MediaMime,
    [property: JsonPropertyName("media_name")] string MediaName,
    [property: JsonPropertyName("media_data")] string MediaData,
    [property: JsonPropertyName("media_sha256")] string MediaSha256);

public sealed record ParsedContact(
    [property: JsonPropertyName("wxid")] string Wxid,
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("remark")] string Remark,
    [property: JsonPropertyName("nick_name")] string NickName,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar")] string Avatar,
    [property: JsonPropertyName("source_updated_at")] long SourceUpdatedAt,
    [property: JsonPropertyName("extra_json")] JsonElement? ExtraJson);

public sealed record ParsedFavorite(
    [property: JsonPropertyName("source_table")] string SourceTable,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("item_type")] string ItemType,
    [property: JsonPropertyName("item_sub_type")] string ItemSubType,
    [property: JsonPropertyName("source_updated_at")] long SourceUpdatedAt,
    [property: JsonPropertyName("data_json")] IReadOnlyDictionary<string, JsonElement> DataJson);

public sealed record ParserNotice(
    string Code,
    string Database,
    string Detail);
