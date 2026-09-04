namespace Footprint.Core;

public static class SqlReadPlan
{
    public static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public static string BuildCompressionQuery(string table, string column, bool hasRowId, int limit, long offset,
        IReadOnlyList<string>? primaryKeyColumns = null)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var qTable = QuoteIdentifier(table);
        var qColumn = QuoteIdentifier(column);
        var qType = QuoteIdentifier("WCDB_CT_" + column);
        var identity = hasRowId ? "rowid" : BuildIdentity(primaryKeyColumns ?? []);
        var order = hasRowId ? "rowid" : string.Join(", ", (primaryKeyColumns ?? []).Select(QuoteIdentifier));
        return $"SELECT {identity}, {qColumn}, {qType}, wcdb_decompress({qColumn}, {qType}) FROM {qTable} " +
               $"WHERE {qColumn} IS NOT NULL ORDER BY {order} LIMIT {limit} OFFSET {offset};";
    }

    private static string BuildIdentity(IReadOnlyList<string> columns)
    {
        if (columns.Count == 0) throw new ArgumentException("WITHOUT ROWID tables require primary key columns.", nameof(columns));
        return string.Join(" || char(31) || ", columns.Select(c => $"quote({QuoteIdentifier(c)})")) + " AS row_identity";
    }
}
