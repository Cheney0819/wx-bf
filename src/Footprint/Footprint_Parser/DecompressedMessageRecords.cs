using System.Globalization;
using System.Text.Json;

namespace Footprint.Parser;

public sealed record DecompressedMessage(string DatabasePath, string Table, string LocalId,
    string ServerId, long CreateTimeUnixSeconds, long LocalType, string Kind, string Body,
    string? DatabaseIdentityHash = null);

public sealed record DecompressedMessagePackage(IReadOnlyList<DecompressedMessage> Messages,
    IReadOnlyList<string> Diagnostics);

public static class DecompressedMessageRecords
{
    private const int MaximumLineCharacters = 16 * 1024 * 1024;

    public static async Task<DecompressedMessagePackage> ReadAsync(string tablePreviewPath,
        string recordsRoot, IReadOnlyDictionary<string, string>? databaseIdentityByPath = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRegularFile(tablePreviewPath);
        var recordsDirectory = new DirectoryInfo(Path.GetFullPath(recordsRoot));
        recordsDirectory.Refresh();
        if (!recordsDirectory.Exists || recordsDirectory.LinkTarget is not null ||
            (recordsDirectory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("解压 records 目录无效。");

        var previews = new Dictionary<string, PreviewRow>(StringComparer.Ordinal);
        await using (var stream = new FileStream(Path.GetFullPath(tablePreviewPath), FileMode.Open, FileAccess.Read,
                         FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var reader = new StreamReader(stream))
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length == 0) continue;
                if (line.Length > MaximumLineCharacters) throw new InvalidDataException("table-preview 记录过大。");
                using var document = Parse(line, "table-preview");
                if (!TryReadPreview(document.RootElement, out var preview)) continue;
                previews[Key(preview.DatabasePath, preview.Table, preview.LocalId)] = preview;
            }
        }

        var bodies = new Dictionary<string, (string DatabasePath, string Table, string LocalId, string Text)>(StringComparer.Ordinal);
        var diagnostics = new List<string>();
        foreach (var path in Directory.EnumerateFiles(recordsDirectory.FullName, "*.jsonl", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            ValidateRegularFile(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length == 0) continue;
                if (line.Length > MaximumLineCharacters) throw new InvalidDataException("解压 record 过大。");
                using var document = Parse(line, "records");
                var root = document.RootElement;
                if (!TryRequiredString(root, "database_path", out var databasePath) ||
                    !TryRequiredString(root, "table", out var table) ||
                    !TryRequiredString(root, "column", out var column) ||
                    !TryRequiredString(root, "row_identity", out var rowIdentity) ||
                    !column.Equals("message_content", StringComparison.OrdinalIgnoreCase) ||
                    !TryRequiredString(root, "text", out var text)) continue;
                var key = Key(databasePath, table, rowIdentity);
                if (!previews.ContainsKey(key))
                {
                    diagnostics.Add($"records 记录 {databasePath}/{table}/{rowIdentity} 没有对应的 table-preview，使用降级元数据。");
                }
                if (bodies.TryGetValue(key, out var existing) && !string.Equals(existing.Text, text, StringComparison.Ordinal))
                    diagnostics.Add($"records 记录 {databasePath}/{table}/{rowIdentity} 存在冲突正文，保留首条记录。");
                else bodies[key] = (databasePath, table, rowIdentity, text);
            }
        }

        var messages = new List<DecompressedMessage>();
        foreach (var preview in previews.Values.OrderBy(value => value.DatabasePath, StringComparer.Ordinal)
                     .ThenBy(value => value.Table, StringComparer.Ordinal).ThenBy(value => value.LocalId, StringComparer.Ordinal))
        {
            if (!bodies.TryGetValue(Key(preview.DatabasePath, preview.Table, preview.LocalId), out var bodyRecord))
            {
                diagnostics.Add($"table-preview 记录 {preview.DatabasePath}/{preview.Table}/{preview.LocalId} 没有解压正文。");
                continue;
            }
            string? databaseIdentity = null;
            if (databaseIdentityByPath is not null)
            {
                databaseIdentityByPath.TryGetValue(preview.DatabasePath, out databaseIdentity);
                if (databaseIdentity is null)
                {
                    var fileName = preview.DatabasePath.Replace('\\', '/').Split('/').Last();
                    databaseIdentityByPath.TryGetValue(fileName, out databaseIdentity);
                }
            }
            messages.Add(new DecompressedMessage(preview.DatabasePath, preview.Table, preview.LocalId,
                preview.ServerId, preview.CreateTimeUnixSeconds, preview.LocalType,
                MapMessageKind(preview.LocalType), bodyRecord.Text, databaseIdentity));
        }
        var previewKeys = previews.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var body in bodies.Values.Where(value => !previewKeys.Contains(Key(value.DatabasePath, value.Table, value.LocalId)))
                     .OrderBy(value => value.DatabasePath, StringComparer.Ordinal).ThenBy(value => value.Table, StringComparer.Ordinal)
                     .ThenBy(value => value.LocalId, StringComparer.Ordinal))
        {
            string? databaseIdentity = null;
            if (databaseIdentityByPath is not null)
            {
                databaseIdentityByPath.TryGetValue(body.DatabasePath, out databaseIdentity);
                if (databaseIdentity is null)
                    databaseIdentityByPath.TryGetValue(body.DatabasePath.Replace('\\', '/').Split('/').Last(), out databaseIdentity);
            }
            messages.Add(new DecompressedMessage(body.DatabasePath, body.Table, body.LocalId, "0", 0, 1,
                "text", body.Text, databaseIdentity));
        }
        return new DecompressedMessagePackage(messages, diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool TryReadPreview(JsonElement root, out PreviewRow preview)
    {
        preview = null!;
        if (!root.TryGetProperty("table", out var tableElement) || tableElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("database_path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("row", out var row) || row.ValueKind != JsonValueKind.Object)
            return false;
        var table = tableElement.GetString() ?? string.Empty;
        var databasePath = pathElement.GetString() ?? string.Empty;
        if (!table.StartsWith("Msg_", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(databasePath)) return false;
        var localId = ReadValue(row, "local_id");
        var serverId = ReadValue(row, "server_id");
        var localType = ReadInt64(row, "local_type");
        var createTime = ReadInt64(row, "create_time");
        if (string.IsNullOrWhiteSpace(localId) || localType is null || createTime is null) return false;
        preview = new PreviewRow(databasePath, table, localId, string.IsNullOrWhiteSpace(serverId) ? "0" : serverId,
            createTime.Value, localType.Value);
        return true;
    }

    private sealed record PreviewRow(string DatabasePath, string Table, string LocalId, string ServerId,
        long CreateTimeUnixSeconds, long LocalType);

    private static string Key(string databasePath, string table, string localId) =>
        databasePath + "\0" + table + "\0" + localId;

    private static string ReadValue(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object) return string.Empty;
        if (value.TryGetProperty("value", out var scalar) && scalar.ValueKind == JsonValueKind.String)
            return scalar.GetString() ?? string.Empty;
        if (value.TryGetProperty("text", out scalar) && scalar.ValueKind == JsonValueKind.String)
            return scalar.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static long? ReadInt64(JsonElement row, string property) =>
        long.TryParse(ReadValue(row, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string MapMessageKind(long value) => value switch
    {
        1 => "text", 3 => "image", 34 => "voice", 43 => "video", 49 => "file", _ => "unknown"
    };

    private static JsonDocument Parse(string line, string source)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException exception) { throw new InvalidDataException($"{source} JSON 无效。", exception); }
    }

    private static bool TryRequiredString(JsonElement root, string property, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumLineCharacters;
    }

    private static void ValidateRegularFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(Path.GetFullPath(path));
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("解压输入必须是普通文件。");
    }
}
