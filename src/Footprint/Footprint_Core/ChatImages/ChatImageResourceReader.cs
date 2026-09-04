using System.Globalization;
using System.Text;

namespace Footprint.Core;

public sealed class ChatImageResourceReader
{
    private readonly IPlainProcessRunner _runner;

    public ChatImageResourceReader(IPlainProcessRunner? runner = null) =>
        _runner = runner ?? SystemPlainProcessRunner.Instance;

    public async Task<ChatImageResourceReadResult> ReadAsync(string databasePath, string sqliteExecutable,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqliteExecutable);
        if (!File.Exists(databasePath)) throw new FileNotFoundException("message_resource.db not found.", databasePath);

        var hasInfoTable = await HasMessageResourceInfoTableAsync(databasePath, sqliteExecutable, cancellationToken);
        var sql = hasInfoTable
            ? ".mode list\n.separator |\n" +
              "SELECT hex(CAST(d.resource_id AS TEXT)), hex(CAST(d.message_id AS TEXT)), " +
              "hex(CAST(d.type AS TEXT)), hex(CAST(d.size AS TEXT)), hex(CAST(d.create_time AS TEXT)), " +
              "hex(CAST(d.access_time AS TEXT)), hex(CAST(d.status AS TEXT)), hex(CAST(d.data_index AS TEXT)), " +
              "hex(CASE WHEN d.packed_info IS NOT NULL AND length(d.packed_info) > 0 " +
              "THEN d.packed_info ELSE i.packed_info END) " +
              "FROM MessageResourceDetail d LEFT JOIN MessageResourceInfo i ON i.message_id=d.message_id " +
              "ORDER BY d.resource_id;"
            : ".mode list\n.separator |\n" +
              "SELECT hex(CAST(d.resource_id AS TEXT)), hex(CAST(d.message_id AS TEXT)), " +
              "hex(CAST(d.type AS TEXT)), hex(CAST(d.size AS TEXT)), hex(CAST(d.create_time AS TEXT)), " +
              "hex(CAST(d.access_time AS TEXT)), hex(CAST(d.status AS TEXT)), hex(CAST(d.data_index AS TEXT)), " +
              "hex(d.packed_info) FROM MessageResourceDetail d ORDER BY d.resource_id;";
        var result = await _runner.RunAsync(sqliteExecutable, ["-batch", PlainSql.ReadOnlyUri(databasePath)],
            sql, TimeSpan.FromMinutes(2), cancellationToken);
        var output = result.StandardOutput;
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidDataException("message_resource.db resource read failed: " +
                                           result.StandardError.Trim());

        var items = new List<ChatImageResourceRecord>();
        var errors = new List<string>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = line.Split('|');
            var fields = encoded.Select(Decode).ToArray();
            if (encoded.Length != 9 || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var resourceId))
            {
                errors.Add("resource_row_invalid");
                continue;
            }

            byte[] packed;
            try { packed = string.IsNullOrEmpty(encoded[8]) ? [] : Convert.FromHexString(encoded[8]); }
            catch (FormatException)
            {
                errors.Add("resource_packed_info_invalid");
                continue;
            }

            ChatImagePackedInfo.TryReadStem(packed, out var stem);
            items.Add(new ChatImageResourceRecord
            {
                ResourceId = resourceId,
                MessageId = TryLong(fields[1]),
                Type = TryInt(fields[2]),
                Size = TryLong(fields[3]),
                CreateTime = TryLong(fields[4]),
                AccessTime = TryLong(fields[5]),
                Status = TryInt(fields[6]),
                DataIndex = NullIfEmpty(fields[7]),
                PackedInfoHex = string.IsNullOrEmpty(encoded[8]) ? null : encoded[8].ToLowerInvariant(),
                Stem = stem
            });
        }

        return new ChatImageResourceReadResult(databasePath, items, errors);
    }

    private async Task<bool> HasMessageResourceInfoTableAsync(string databasePath, string sqliteExecutable,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='MessageResourceInfo' LIMIT 1;";
        var result = await _runner.RunAsync(sqliteExecutable, ["-batch", PlainSql.ReadOnlyUri(databasePath)],
            sql, TimeSpan.FromMinutes(2), cancellationToken);
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidDataException("message_resource.db schema read failed: " +
                                           result.StandardError.Trim());
        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length > 0;
    }

    private static string Decode(string value)
    {
        if (value.Length == 0) return string.Empty;
        try { return Encoding.UTF8.GetString(Convert.FromHexString(value)); }
        catch (FormatException) { return value; }
    }

    private static long? TryLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static int? TryInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
