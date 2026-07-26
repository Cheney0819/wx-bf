using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopPet.DataSync.Tests;

internal static class ParserResultTestData
{
    internal static object Document(
        string jobId = "job-1",
        string sourceSetId = "source-1") => new
    {
        schemaVersion = 1,
        jobId,
        sourceSetId,
        messages = new object[]
        {
            Message(1, "hello"),
            Message(2, "world"),
        },
        contacts = new object[]
        {
            new
            {
                wxid = "alice",
                alias = "alice_alias",
                remark = "Alice",
                nick_name = "Alice Nick",
                display_name = "Alice",
                avatar = "",
                source_updated_at = 0L,
                extra_json = (object?)null,
            },
        },
        favorites = new object[]
        {
            new
            {
                source_table = "Favorites",
                source_id = "7",
                title = "Saved title",
                summary = "Saved summary",
                item_type = "link",
                item_sub_type = "",
                source_updated_at = 99L,
                data_json = new Dictionary<string, object?> { ["id"] = 7 },
            },
        },
        notices = Array.Empty<object>(),
    };

    internal static object Message(long localId, string content) => new
    {
        wxid = "alice",
        local_id = localId,
        content,
        create_time = 100L + localId,
        is_sender = false,
        nickname = "Alice",
        sender = "Alice",
        avatar = "",
        msg_type = 1,
        msg_sub_type = 0,
        media_type = "",
        media_mime = "",
        media_name = "",
        media_data = "",
        media_sha256 = "",
    };

    internal static async Task<string> WriteAsync(string root, object? document = null)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"result-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(document ?? Document()));
        return path;
    }

    internal static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
