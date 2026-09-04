using System.Globalization;
using System.Text;

namespace Footprint.Core;

public static class PlainSql
{
    public static string StringLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    public static string Identifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public static string ReadOnlyUri(string path) => new UriBuilder
    {
        Scheme = Uri.UriSchemeFile,
        Path = Path.GetFullPath(path),
        Query = "mode=ro"
    }.Uri.AbsoluteUri;

    public static string NormalizeSchemaSql(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var input = value.Trim();
        if (input.EndsWith(';')) input = input[..^1].TrimEnd();
        var output = new StringBuilder(input.Length);
        var quote = '\0';
        var pendingSpace = false;
        for (var index = 0; index < input.Length; index++)
        {
            var character = input[index];
            if (quote != '\0')
            {
                output.Append(character);
                if (quote == ']' && character == ']') quote = '\0';
                else if (quote != ']' && character == quote)
                {
                    if (index + 1 < input.Length && input[index + 1] == quote)
                        output.Append(input[++index]);
                    else quote = '\0';
                }
                continue;
            }

            if (character is '\'' or '"' or '`' or '[')
            {
                if (pendingSpace && output.Length > 0) output.Append(' ');
                pendingSpace = false;
                quote = character == '[' ? ']' : character;
                output.Append(character);
            }
            else if (char.IsWhiteSpace(character))
            {
                pendingSpace = output.Length > 0;
            }
            else
            {
                if (pendingSpace && output.Length > 0) output.Append(' ');
                pendingSpace = false;
                output.Append(character);
            }
        }
        return output.ToString().Trim();
    }

    public static byte[] BuildMetadataScriptBytes(byte[]? key, int compatibility, int pageSize) =>
        BuildMetadataScriptBytes(key, compatibility, pageSize, quickCheck: false);

    private static byte[] BuildMetadataScriptBytes(byte[]? key, int compatibility, int pageSize, bool quickCheck)
    {
        const string prefix = ".bail on\n.headers off\n.mode list\n.separator |\n";
        var body = (key is null ? string.Empty :
                       $"\";\nPRAGMA cipher_compatibility={compatibility};\nPRAGMA cipher_page_size={pageSize};\n") +
                   "PRAGMA query_only=ON;\n" +
                   "SELECT '__FP_USER_VERSION__|' || user_version FROM pragma_user_version;\n" +
                   "SELECT '__FP_SCHEMA__|' || hex(type) || '|' || hex(name) || '|' || hex(tbl_name) || '|' || hex(coalesce(sql,'')) FROM sqlite_master ORDER BY type,name,tbl_name;\n" +
                   (quickCheck
                       ? "SELECT '__FP_INTEGRITY__|' || quick_check FROM pragma_quick_check;\n"
                       : "SELECT '__FP_INTEGRITY__|' || integrity_check FROM pragma_integrity_check;\n");
        return key is null
            ? Encoding.UTF8.GetBytes(prefix + body)
            : SqlCipherVerifier.BuildKeyedScript(key, prefix + "PRAGMA key=\"", body);
    }

    public static byte[] BuildMetadataScriptBytes(byte[]? key, int compatibility, int pageSize,
        IEnumerable<PlainSchemaObject> knownSchema)
        => BuildMetadataScriptBytes(key, compatibility, pageSize, RequiresDumpRestore(knownSchema));

    public static byte[] BuildCountAndProbeScriptBytes(byte[]? key, int compatibility, int pageSize,
        IEnumerable<PlainSchemaObject> schema)
    {
        var objects = schema.ToArray();
        const string prefix = ".bail on\n.headers off\n.mode list\n.separator |\n";
        var script = new StringBuilder();
        if (key is not null) script.Append("\";\nPRAGMA cipher_compatibility=").Append(compatibility)
            .Append(";\nPRAGMA cipher_page_size=").Append(pageSize).Append(";\n");
        script.Append("PRAGMA query_only=ON;\n");
        foreach (var item in objects.Where(item => item.Type == "table" && !IsVirtualTable(item))
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
            script.Append("SELECT '__FP_ROW_COUNT__|").Append(Hex(item.Name)).Append("|' || count(*) FROM ")
                .Append(Identifier(item.Name)).Append(";\n");
        if (objects.Any(item => item.Type == "table" && item.Name == "sqlite_sequence"))
            script.Append("SELECT '__FP_SEQUENCE__|' || hex(name) || '|' || coalesce(seq,0) FROM sqlite_sequence ORDER BY name;\n");
        foreach (var item in objects.Where(item => item.Type == "view" && !DependsOnCustomTokenizer(item, objects))
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
            script.Append("SELECT count(*) FROM (SELECT * FROM ").Append(Identifier(item.Name)).Append(" LIMIT 0);\n");
        return key is null
            ? Encoding.UTF8.GetBytes(prefix + script)
            : SqlCipherVerifier.BuildKeyedScript(key, prefix + "PRAGMA key=\"", script.ToString());
    }

    public static byte[] BuildSourceDumpScriptBytes(byte[] key, int compatibility, int pageSize) =>
        SqlCipherVerifier.BuildKeyedScript(key, ".bail on\n.headers off\nPRAGMA key=\"",
            $"\";\nPRAGMA cipher_compatibility={compatibility};\nPRAGMA cipher_page_size={pageSize};\n" +
            "PRAGMA query_only=ON;\n.dump\n");

    public static byte[] BuildDumpRestorePrefixBytes() => ".bail on\n"u8.ToArray();

    public static byte[] BuildDumpRestoreSuffixBytes(int userVersion) => Encoding.ASCII.GetBytes(
        "\n" + $"PRAGMA user_version={userVersion.ToString(CultureInfo.InvariantCulture)};\n" +
        "PRAGMA journal_mode=DELETE;\n.print __FP_PLAIN_JOURNAL_BEGIN__\nPRAGMA journal_mode;\n" +
        ".print __FP_PLAIN_JOURNAL_END__\n");

    public static bool IsVirtualTable(PlainSchemaObject item) =>
        item.Type == "table" && item.Sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresDumpRestore(IEnumerable<PlainSchemaObject> schema) =>
        schema.Any(item => IsVirtualTable(item) &&
                           item.Sql.Contains("MMFtsTokenizer", StringComparison.OrdinalIgnoreCase));

    private static bool DependsOnCustomTokenizer(PlainSchemaObject view, IReadOnlyList<PlainSchemaObject> schema)
    {
        var dependencies = schema.Where(item => item.Type == "table" && IsVirtualTable(item) &&
                                                item.Sql.Contains("MMFtsTokenizer", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name)
            .ToArray();
        return dependencies.Any(name => ContainsIdentifier(view.Sql, name));
    }

    private static bool ContainsIdentifier(string sql, string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(sql,
            $@"(?<![\p{{L}}\p{{N}}_]){System.Text.RegularExpressions.Regex.Escape(name)}(?![\p{{L}}\p{{N}}_])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static byte[] BuildExportScriptBytes(byte[] key, int compatibility, int pageSize, string temporaryPath,
        int userVersion)
        => SqlCipherVerifier.BuildKeyedScript(key, ".bail on\nPRAGMA key=\"",
            $"\";\nPRAGMA cipher_compatibility={compatibility};\nPRAGMA cipher_page_size={pageSize};\n" +
            $"ATTACH DATABASE {StringLiteral(temporaryPath)} AS plain KEY '';\n" +
            "SELECT sqlcipher_export('plain');\n" +
            $"PRAGMA plain.user_version={userVersion.ToString(CultureInfo.InvariantCulture)};\n" +
            "PRAGMA plain.journal_mode=DELETE;\nDETACH DATABASE plain;\n");

    public static byte[] BuildSourceExportScriptBytes(byte[] key, int compatibility, int pageSize, string temporaryPath,
        string countScriptPath)
        => SqlCipherVerifier.BuildKeyedScript(key,
            ".bail on\n.headers off\n.mode list\n.separator |\nPRAGMA key=\"",
            $"\";\nPRAGMA cipher_compatibility={compatibility};\nPRAGMA cipher_page_size={pageSize};\n" +
               $"ATTACH DATABASE {StringLiteral(temporaryPath)} AS plain KEY '';\n" +
               "BEGIN;\n" +
               "SELECT '__FP_USER_VERSION__|' || user_version FROM pragma_user_version;\n" +
               "SELECT '__FP_SCHEMA__|' || hex(type) || '|' || hex(name) || '|' || hex(tbl_name) || '|' || hex(coalesce(sql,'')) FROM sqlite_master ORDER BY type,name,tbl_name;\n" +
               "SELECT '__FP_INTEGRITY__|' || integrity_check FROM pragma_integrity_check;\n" +
               $".output {ShellArgument(countScriptPath)}\n" +
               "SELECT printf('PRAGMA plain.user_version=%d;', user_version) FROM pragma_user_version;\n" +
               "SELECT printf('SELECT ''__FP_ROW_COUNT__|%s|'' || count(*) FROM \"%w\";', hex(name), name) FROM sqlite_master WHERE type='table' ORDER BY name;\n" +
               "SELECT 'SELECT ''__FP_SEQUENCE__|'' || hex(name) || ''|'' || coalesce(seq,0) FROM sqlite_sequence ORDER BY name;' WHERE EXISTS (SELECT 1 FROM sqlite_master WHERE type='table' AND name='sqlite_sequence');\n" +
               "SELECT printf('SELECT count(*) FROM (SELECT * FROM \"%w\" LIMIT 0);', name) FROM sqlite_master WHERE type='view' ORDER BY name;\n" +
               ".output stdout\n" +
               $".read {ShellArgument(countScriptPath)}\n" +
               "SELECT sqlcipher_export('plain');\n" +
               "COMMIT;\n" +
               "PRAGMA plain.journal_mode=DELETE;\n" +
               ".print __FP_PLAIN_JOURNAL_BEGIN__\n" +
               "PRAGMA plain.journal_mode;\n" +
               ".print __FP_PLAIN_JOURNAL_END__\n" +
               "DETACH DATABASE plain;\n");


    // Legacy string-returning surface. Secure internal callers use the *Bytes variants above.
    public static string BuildMetadataScript(byte[]? key, int compatibility, int pageSize) =>
        DecodeAndZero(BuildMetadataScriptBytes(key, compatibility, pageSize));

    public static string BuildMetadataScript(byte[]? key, int compatibility, int pageSize,
        IEnumerable<PlainSchemaObject> knownSchema) =>
        DecodeAndZero(BuildMetadataScriptBytes(key, compatibility, pageSize, knownSchema));

    public static string BuildCountAndProbeScript(byte[]? key, int compatibility, int pageSize,
        IEnumerable<PlainSchemaObject> schema) =>
        DecodeAndZero(BuildCountAndProbeScriptBytes(key, compatibility, pageSize, schema));

    public static string BuildSourceDumpScript(byte[] key, int compatibility, int pageSize) =>
        DecodeAndZero(BuildSourceDumpScriptBytes(key, compatibility, pageSize));

    public static string BuildDumpRestorePrefix() => DecodeAndZero(BuildDumpRestorePrefixBytes());

    public static string BuildDumpRestoreSuffix(int userVersion) =>
        DecodeAndZero(BuildDumpRestoreSuffixBytes(userVersion));

    public static string BuildExportScript(byte[] key, int compatibility, int pageSize, string temporaryPath,
        int userVersion) =>
        DecodeAndZero(BuildExportScriptBytes(key, compatibility, pageSize, temporaryPath, userVersion));

    public static string BuildSourceExportScript(byte[] key, int compatibility, int pageSize, string temporaryPath,
        string countScriptPath) =>
        DecodeAndZero(BuildSourceExportScriptBytes(key, compatibility, pageSize, temporaryPath, countScriptPath));

    private static string DecodeAndZero(byte[] script)
    {
        try { return Encoding.UTF8.GetString(script); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(script); }
    }

    private static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    private static string ShellArgument(string value) => "\"" + value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
