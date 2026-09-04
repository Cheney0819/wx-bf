using System.Globalization;
using System.Text;

namespace Footprint.Core;

public sealed class ChatImageIndexReader
{
    private readonly IPlainProcessRunner _runner;

    public ChatImageIndexReader(IPlainProcessRunner? runner = null) => _runner = runner ?? SystemPlainProcessRunner.Instance;

    public async Task<ChatImageIndexReadResult> ReadAsync(string databasePath, string sqliteExecutable,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqliteExecutable);
        if (!File.Exists(databasePath)) throw new FileNotFoundException("hardlink.db not found.", databasePath);

        const string sql =
            ".mode list\n.separator |\n" +
            "SELECT hex(CAST(i._rowid_ AS TEXT)), hex(CAST(i.md5_hash AS TEXT)), hex(CAST(i.md5 AS TEXT)), " +
            "hex(CAST(i.type AS TEXT)), hex(CAST(i.file_name AS TEXT)), hex(CAST(i.file_size AS TEXT)), " +
            "hex(CAST(i.modify_time AS TEXT)), hex(CAST(i.dir1 AS TEXT)), hex(CAST(i.dir2 AS TEXT)), " +
            "hex(i.extra_buffer), hex(CAST(d1.username AS TEXT)), hex(CAST(d2.username AS TEXT)) " +
            "FROM image_hardlink_info_v4 i " +
            "LEFT JOIN dir2id d1 ON d1.rowid=i.dir1 LEFT JOIN dir2id d2 ON d2.rowid=i.dir2 ORDER BY i._rowid_;";
        var result = await _runner.RunAsync(sqliteExecutable, ["-batch", PlainSql.ReadOnlyUri(databasePath)],
            sql, TimeSpan.FromMinutes(2), cancellationToken);
        var output = result.StandardOutput;
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidDataException("hardlink.db image index read failed: " +
                                           result.StandardError.Trim());

        var items = new List<ChatImageIndexRecord>();
        var errors = new List<string>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = line.Split('|');
            var fields = encoded.Select(Decode).ToArray();
            if (fields.Length != 12 || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowId) ||
                string.IsNullOrWhiteSpace(fields[4]))
            {
                errors.Add("index_row_invalid");
                continue;
            }

            items.Add(new ChatImageIndexRecord
            {
                RowId = rowId,
                Md5Hash = NullIfEmpty(fields[1]),
                Md5 = NullIfEmpty(fields[2]),
                Type = TryInt(fields[3]),
                FileName = fields[4],
                FileSize = TryLong(fields[5]),
                ModifyTime = NullIfEmpty(fields[6]),
                Dir1Id = TryInt(fields[7]),
                Dir2Id = TryInt(fields[8]),
                Dir1Name = NullIfEmpty(fields[10]),
                Dir2Name = NullIfEmpty(fields[11]),
                ExtraBufferHex = NullIfEmpty(encoded[9]?.ToLowerInvariant() ?? string.Empty),
                Variant = ClassifyVariant(fields[4])
            });
        }

        return new ChatImageIndexReadResult(databasePath, items, errors);
    }

    private static string Decode(string value)
    {
        if (value.Length == 0) return string.Empty;
        try { return Encoding.UTF8.GetString(Convert.FromHexString(value)); }
        catch (FormatException) { return value; }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
    private static ChatImageVariant ClassifyVariant(string fileName)
    {
        if (fileName.EndsWith("_t.dat", StringComparison.OrdinalIgnoreCase)) return ChatImageVariant.Thumbnail;
        return fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
            ? ChatImageVariant.Full
            : ChatImageVariant.Unknown;
    }
    private static long? TryLong(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static int? TryInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
}
