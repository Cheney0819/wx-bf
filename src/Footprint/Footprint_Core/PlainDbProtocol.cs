using System.Globalization;
using System.Text;

namespace Footprint.Core;

public sealed record PlainSchemaObject(string Type, string Name, string TableName, string Sql);

public sealed class PlainDatabaseBaseline
{
    public int UserVersion { get; init; }
    public IReadOnlyList<PlainSchemaObject> Schema { get; init; } = [];
    public IReadOnlyDictionary<string, long> RowCounts { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, long> SequenceValues { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);
    public string IntegrityCheck { get; init; } = string.Empty;
    public string PlainJournalMode { get; init; } = string.Empty;
}

public static class PlainExportOutputParser
{
    public static PlainDatabaseBaseline Parse(ReadOnlyMemory<byte> output, bool requireMetadata = false,
        bool requireRowCounts = false)
    {
        var outputText = Encoding.UTF8.GetString(output.Span);
        var version = 0;
        var hasVersion = false;
        var hasIntegrity = false;
        var malformedRowCount = false;
        var integrity = string.Empty;
        var journalMode = string.Empty;
        var journalMarkerOpen = false;
        var schema = new List<PlainSchemaObject>();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var sequences = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in outputText.Split(['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (line == "__FP_PLAIN_JOURNAL_BEGIN__")
            {
                journalMarkerOpen = true;
                continue;
            }
            if (line == "__FP_PLAIN_JOURNAL_END__")
            {
                journalMarkerOpen = false;
                continue;
            }
            if (journalMarkerOpen)
            {
                journalMode = line;
                continue;
            }
            if (parts[0] == "__FP_USER_VERSION__" && parts.Length == 2)
                hasVersion = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
            else if (parts[0] == "__FP_INTEGRITY__" && parts.Length == 2)
            {
                integrity = parts[1];
                hasIntegrity = true;
            }
            else if (parts[0] == "__FP_PLAIN_JOURNAL__" && parts.Length == 2)
                journalMode = parts[1];
            else if (parts[0] == "__FP_SCHEMA__" && parts.Length == 5)
                schema.Add(new PlainSchemaObject(Unhex(parts[1]), Unhex(parts[2]), Unhex(parts[3]), Unhex(parts[4])));
            else if (parts[0] == "__FP_ROW_COUNT__" && parts.Length == 3 &&
                     long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                counts[Unhex(parts[1])] = count;
            else if (parts[0] == "__FP_ROW_COUNT__") malformedRowCount = true;
            else if (parts[0] == "__FP_SEQUENCE__" && parts.Length == 3 &&
                     long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
                sequences[Unhex(parts[1])] = sequence;
            else if (parts[0] == "__FP_SEQUENCE__") malformedRowCount = true;
        }
        if (requireMetadata && (!hasVersion || !hasIntegrity))
            throw new InvalidDataException("Database metadata output is incomplete.");
        var tableCount = schema.Count(item => item.Type == "table");
        if (requireRowCounts && (malformedRowCount || tableCount > 0 && counts.Count != tableCount))
            throw new InvalidDataException("Database row-count output is incomplete.");
        return new PlainDatabaseBaseline
        {
            UserVersion = version,
            IntegrityCheck = integrity,
            PlainJournalMode = journalMode,
            Schema = schema,
            RowCounts = counts,
            SequenceValues = sequences
        };
    }

    // Legacy string overload. Byte-oriented callers retain the secure overload above.
    public static PlainDatabaseBaseline Parse(string output, bool requireMetadata = false, bool requireRowCounts = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        var bytes = Encoding.UTF8.GetBytes(output);
        try { return Parse(bytes, requireMetadata, requireRowCounts); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    public static PlainDatabaseBaseline Merge(PlainDatabaseBaseline metadata, PlainDatabaseBaseline counts) => new()
    {
        UserVersion = metadata.UserVersion,
        IntegrityCheck = metadata.IntegrityCheck,
        Schema = metadata.Schema,
        RowCounts = counts.RowCounts,
        SequenceValues = counts.SequenceValues
    };

    private static string Unhex(string value) => value.Length == 0 ? string.Empty :
        Encoding.UTF8.GetString(Convert.FromHexString(value));
}
