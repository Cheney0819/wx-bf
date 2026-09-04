using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Footprint.Core.Capture;

internal sealed record DecompressionSummaryBinding(string Path, int Tag, string KeySha256, int KeyLength);

internal sealed record DecompressionSummaryValidationResult(bool IsValid, string Code, string MessageZh,
    JsonElement Summary);

internal enum DecompressionDatabaseRequirement
{
    Required,
    Optional
}

internal static class DecompressionSummaryValidator
{
    internal const int MaximumBytes = 1024 * 1024;
    internal const int MaxDepth = 24;
    internal const string InvalidCode = "frida_decompression_invalid";
    internal const string InvalidMessageZh = "Frida 解压结果无效，已停止发布。";
    internal const string SummaryValidCode = "frida_decompression_summary_valid";
    internal const string SummaryValidWithOptionalFailuresCode =
        "frida_decompression_summary_valid_with_optional_failures";
    internal const string SummaryInvalidCode = "frida_decompression_summary_invalid";
    internal const string ResultCountMismatchCode = "frida_decompression_summary_result_count_mismatch";
    internal const string BindingMismatchCode = "frida_decompression_summary_binding_mismatch";
    internal const string DatabaseFailedCode = "frida_decompression_database_failed";
    internal static bool TryValidate(ReadOnlyMemory<byte> bytes,
        IReadOnlyCollection<DecompressionSummaryBinding> bindings, out JsonElement summary)
    {
        var result = Validate(bytes, bindings);
        summary = result.Summary;
        return result.IsValid;
    }

    internal static DecompressionSummaryValidationResult Validate(ReadOnlyMemory<byte> bytes,
        IReadOnlyCollection<DecompressionSummaryBinding> bindings)
    {
        if (bytes.Length is <= 0 or > MaximumBytes || bindings.Count == 0)
            return Invalid("Frida 解压摘要格式无效：内容为空、超过大小限制或缺少数据库绑定。");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxDepth });
            if (HasSecretMaterial(document.RootElement))
                return Invalid("Frida 解压摘要包含禁止的密钥字段。");
            if (!HasValidOptionalMetadata(document.RootElement))
                return Invalid("Frida 解压摘要元数据格式无效。");
            var validation = ValidateResults(document.RootElement, bindings);
            return validation ?? new DecompressionSummaryValidationResult(true, SummaryValidCode,
                "Frida 解压摘要验证通过。", document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Invalid("Frida 解压摘要 JSON 格式无效。");
        }
    }

    internal static bool DatabasePathsEqual(string left, string right) => string.Equals(
        NormalizeDatabasePath(left), NormalizeDatabasePath(right), StringComparison.OrdinalIgnoreCase);

    internal static string StageFailureCode(string code) =>
        string.Equals(code, DatabaseFailedCode, StringComparison.Ordinal) ? InvalidCode : code;

    private static DecompressionSummaryValidationResult? ValidateResults(JsonElement summary,
        IReadOnlyCollection<DecompressionSummaryBinding> bindings)
    {
        if (summary.ValueKind != JsonValueKind.Object ||
            !summary.TryGetProperty("database_results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
            return Invalid("Frida 解压摘要缺少有效的 database_results 数组。");

        var resultItems = results.EnumerateArray().ToArray();
        var resultPaths = new List<string>(resultItems.Length);
        foreach (var result in resultItems)
        {
            if (result.ValueKind != JsonValueKind.Object || !TryGetString(result, "path", out var path))
                return Invalid("Frida 解压摘要数据库结果缺少有效路径。");
            resultPaths.Add(path);
        }

        if (resultItems.Length != bindings.Count)
        {
            var missing = bindings.Where(binding => !resultPaths.Any(path => DatabasePathsEqual(binding.Path, path)))
                .Select(binding => binding.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var extra = resultPaths.Where(path => !bindings.Any(binding => DatabasePathsEqual(binding.Path, path)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var details = new List<string>();
            if (missing.Length > 0) details.Add($"缺少={string.Join(",", missing)}");
            if (extra.Length > 0) details.Add($"多余={string.Join(",", extra)}");
            return Failure(ResultCountMismatchCode,
                $"Frida 解压摘要数据库结果数量不一致：expected={bindings.Count}，actual={resultItems.Length}" +
                (details.Count == 0 ? "。" : $"；{string.Join("；", details)}。"));
        }

        var remaining = bindings.ToList();
        var optionalFailures = new List<string>();
        foreach (var result in resultItems)
        {
            var warningMessages = Array.Empty<string>();
            if (!TryGetString(result, "path", out var path) ||
                !TryGetString(result, "key_sha256", out var keySha256) || !IsSha256(keySha256) ||
                !result.TryGetProperty("tag", out var tagElement) || !tagElement.TryGetInt32(out var tag) ||
                !result.TryGetProperty("key_length", out var keyLengthElement) ||
                !keyLengthElement.TryGetInt32(out var keyLength) ||
                !result.TryGetProperty("records", out var recordsElement) ||
                !recordsElement.TryGetInt64(out var records) || records < 0 ||
                !result.TryGetProperty("failures", out var failures) ||
                !TryGetStringArray(failures, out var failureMessages) ||
                (result.TryGetProperty("warnings", out var warnings) &&
                 !TryGetStringArray(warnings, out warningMessages)))
                return Invalid($"Frida 解压摘要数据库结果字段无效：{path}。");

            var pathMatches = remaining.Where(binding => DatabasePathsEqual(binding.Path, path)).ToArray();
            if (pathMatches.Length == 0)
                return BindingMismatch(path, "path", string.Join(",", remaining.Select(item => item.Path)), path);
            var tagMatches = pathMatches.Where(binding => binding.Tag == tag).ToArray();
            if (tagMatches.Length == 0)
                return BindingMismatch(path, "tag", string.Join(",", pathMatches.Select(item => item.Tag)), tag);
            var keyMatches = tagMatches.Where(binding => string.Equals(binding.KeySha256, keySha256,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (keyMatches.Length == 0)
                return BindingMismatch(path, "key_sha256",
                    string.Join(",", tagMatches.Select(item => item.KeySha256)), keySha256);
            var exactMatches = keyMatches.Where(binding => binding.KeyLength == keyLength).ToArray();
            if (exactMatches.Length != 1)
                return BindingMismatch(path, "key_length",
                    string.Join(",", keyMatches.Select(item => item.KeyLength)), keyLength);

            var markerFieldError = FailureMarkerFieldError(result);
            if (markerFieldError is not null)
                return Invalid($"Frida 解压摘要数据库结果字段无效：{path}；{markerFieldError}。");
            var status = result.TryGetProperty("status", out var statusElement)
                ? statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() : null
                : null;
            if (result.TryGetProperty("status", out _) && status is null)
                return Invalid($"Frida 解压摘要数据库状态无效：{path}。");
            if (status is not null && status is not ("Succeeded" or "SkippedNotCompressed" or "Failed"))
                return Invalid($"Frida 解压摘要数据库状态无效：{path}；status={SanitizeExternalText(status)}。");
            var failureReason = ValidatedDatabaseFailureReason(result);
            if (failureReason is not null)
            {
                if (ClassifyDatabase(path) == DecompressionDatabaseRequirement.Optional)
                    optionalFailures.Add($"{path}；{failureReason}");
                else
                    return Failure(DatabaseFailedCode, $"Frida 解压数据库失败：{path}；{failureReason}。");
            }

            remaining.Remove(exactMatches[0]);
        }
        if (remaining.Count > 0)
            return Failure(ResultCountMismatchCode,
                $"Frida 解压摘要缺少数据库结果：expected={bindings.Count}，actual={resultItems.Length}；" +
                $"缺少={string.Join(",", remaining.Select(item => item.Path))}。");
        if (optionalFailures.Count > 0)
            return new DecompressionSummaryValidationResult(true, SummaryValidWithOptionalFailuresCode,
                $"Frida 解压摘要验证通过；可选数据库失败={optionalFailures.Count}；" +
                string.Join("；", optionalFailures) + "。", summary.Clone());
        return null;
    }

    internal static DecompressionDatabaseRequirement ClassifyDatabase(string path)
    {
        var segments = NormalizeDatabasePath(path).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var storageIndex = Array.FindLastIndex(segments, segment =>
            string.Equals(segment, "db_storage", StringComparison.OrdinalIgnoreCase));
        if (storageIndex < 0) return DecompressionDatabaseRequirement.Required;

        var hasCanonicalShape = storageIndex + 2 == segments.Length - 1;
        if (!hasCanonicalShape) return DecompressionDatabaseRequirement.Optional;
        var category = segments[storageIndex + 1];
        var fileName = segments[storageIndex + 2];
        var primaryMessage = string.Equals(category, "message", StringComparison.OrdinalIgnoreCase) &&
                             Regex.IsMatch(fileName, @"^(?:message|biz_message)_\d+\.db$",
                                 RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var primaryContact = string.Equals(category, "contact", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(fileName, "contact.db", StringComparison.OrdinalIgnoreCase);
        var primarySession = string.Equals(category, "session", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(fileName, "session.db", StringComparison.OrdinalIgnoreCase);
        return primaryMessage || primaryContact || primarySession
            ? DecompressionDatabaseRequirement.Required
            : DecompressionDatabaseRequirement.Optional;
    }

    private static bool HasValidOptionalMetadata(JsonElement summary)
    {
        foreach (var propertyName in new[]
                 {
                     "agent_diagnostic_count", "runtime_probe_count", "compression_record_count"
                 })
        {
            if (summary.TryGetProperty(propertyName, out var count) &&
                (!count.TryGetInt64(out var value) || value < 0))
                return false;
        }
        foreach (var propertyName in new[] { "schema_counts", "table_counts", "compressed_column_counts" })
        {
            if (!summary.TryGetProperty(propertyName, out var counts)) continue;
            if (counts.ValueKind != JsonValueKind.Object || counts.EnumerateObject().Any(item =>
                    !item.Value.TryGetInt64(out var count) || count < 0))
                return false;
        }
        foreach (var propertyName in new[]
                 {
                     "schema_path", "compression_records_path", "records_path", "table_stats_path",
                     "table_preview_path", "logical_rows_path"
                 })
        {
            if (summary.TryGetProperty(propertyName, out var path) &&
                path.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return false;
        }
        return true;
    }

    private static bool TryGetString(JsonElement value, string property, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        result = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(result);
    }

    private static string? FailureMarker(JsonElement result)
    {
        if (result.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            if (error.ValueKind != JsonValueKind.String) return "error 字段格式无效";
            if (!string.IsNullOrWhiteSpace(error.GetString())) return error.GetString();
        }
        return result.TryGetProperty("readable", out var readable) && readable.ValueKind == JsonValueKind.False
            ? "readable=false"
            : null;
    }

    internal static string? ValidatedDatabaseFailureReason(JsonElement result)
    {
        _ = TryGetStringArray(result.GetProperty("failures"), out var failures);
        var warnings = Array.Empty<string>();
        if (result.TryGetProperty("warnings", out var warningElement))
            _ = TryGetStringArray(warningElement, out warnings);
        var diagnostics = failures.Concat(warnings).ToArray();
        var fatalFailure = failures.FirstOrDefault(message => !IsMissingCompressionTable(message));
        var fatalWarning = warnings.FirstOrDefault(message =>
            !IsMissingCompressionTable(message) && !IsLiveLogicalRowCountDriftWarning(message));
        var fatal = fatalFailure ?? fatalWarning;
        var status = result.TryGetProperty("status", out var statusElement) &&
                     statusElement.ValueKind == JsonValueKind.String
            ? statusElement.GetString()
            : null;
        var marker = FailureMarker(result);
        var failedStatus = string.Equals(status, "Failed", StringComparison.Ordinal);
        var failedDiagnostic = failedStatus ? diagnostics.FirstOrDefault() : null;
        return failedStatus || fatal is not null || marker is not null
            ? SafeFailureReason(fatal ?? marker ?? failedDiagnostic ?? "status=Failed")
            : null;
    }

    private static string? FailureMarkerFieldError(JsonElement result)
    {
        if (result.TryGetProperty("readable", out var readable) &&
            readable.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return "readable 字段格式无效";
        if (result.TryGetProperty("error", out var error) &&
            error.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            return "error 字段格式无效";
        return null;
    }

    private static bool TryGetStringArray(JsonElement value, out string[] results)
    {
        results = [];
        if (value.ValueKind != JsonValueKind.Array) return false;
        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) return false;
            items.Add(item.GetString()!);
        }
        results = items.ToArray();
        return true;
    }

    private static bool IsMissingCompressionTable(string message) => Regex.IsMatch(message.Trim(),
        @"^wcdb_builtin_compression_record:\s*(?:prepare\s+rc\s*=\s*1:\s*)?" +
        @"no such table:\s*wcdb_builtin_compression_record\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsLiveLogicalRowCountDriftWarning(string message) => Regex.IsMatch(message.Trim(),
        @"^[^:\r\n]{1,128}:\s*logical row count changed during live export expected=\d+ actual=\d+$",
        RegexOptions.CultureInvariant);

    internal static string SafeFailureReason(string message)
    {
        var sanitized = SanitizeExternalText(message);
        if (sanitized.Length > 512) sanitized = sanitized[..512] + "…";
        var structured = Regex.IsMatch(sanitized, @"^[a-z0-9_. -]{1,80}:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var knownDatabaseError = sanitized.Contains("database disk image is malformed",
                                     StringComparison.OrdinalIgnoreCase) ||
                                 sanitized.Contains("file is not a database", StringComparison.OrdinalIgnoreCase) ||
                                 sanitized.Contains("logical row count mismatch", StringComparison.OrdinalIgnoreCase) ||
                                 sanitized.Contains("logical row count changed during live export",
                                     StringComparison.OrdinalIgnoreCase) ||
                                 sanitized.Contains("batch exceeded timeout", StringComparison.OrdinalIgnoreCase) ||
                                 sanitized.Contains("no such table", StringComparison.OrdinalIgnoreCase) ||
                                 Regex.IsMatch(sanitized, @"\brc\s*=\s*\d+\b",
                                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!structured && !knownDatabaseError &&
            !string.Equals(sanitized, "readable=false", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sanitized, "status=Failed", StringComparison.Ordinal))
            return "数据库执行失败";
        return string.IsNullOrWhiteSpace(sanitized) ? "数据库执行失败" : sanitized;
    }

    private static string SanitizeExternalText(string value)
    {
        var sanitized = Regex.Replace(value.Trim(), @"[\u0000-\u001f\u007f]+", " ",
            RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized,
            """\b(raw[_-]?key|key[_-]?(?:b64|hex|material)|password|passphrase)\b\s*[:=]\s*(?:"(?:\\.|[^"])*"|'(?:\\.|[^'])*'|[^\s,;]+)""",
            "$1=<redacted>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized, @"\bprivate[-_ ]?key[-_ ]?material\b", "<redacted>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized, @"\b[0-9a-f]{96,}\b", "<redacted>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized, @"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{80,}={0,2}(?![A-Za-z0-9+/])",
            "<redacted>", RegexOptions.CultureInvariant);
        return sanitized;
    }

    private static DecompressionSummaryValidationResult BindingMismatch(string path, string field,
        object expected, object actual) => Failure(BindingMismatchCode,
        $"Frida 解压摘要数据库绑定不一致：{path}；字段={field}；expected={expected}；actual={actual}。");

    private static DecompressionSummaryValidationResult Invalid(string messageZh) =>
        Failure(SummaryInvalidCode, messageZh);

    private static DecompressionSummaryValidationResult Failure(string code, string messageZh) =>
        new(false, code, messageZh, default);

    private static bool HasSecretMaterial(JsonElement summary)
    {
        var pending = new Stack<(JsonElement Value, int Depth)>();
        pending.Push((summary, 0));
        while (pending.TryPop(out var current))
        {
            if (current.Depth > MaxDepth) return true;
            if (current.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in current.Value.EnumerateObject())
                {
                    if (IsSecretFieldName(property.Name)) return true;
                    pending.Push((property.Value, current.Depth + 1));
                }
            }
            else if (current.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.Value.EnumerateArray())
                    pending.Push((item, current.Depth + 1));
            }
        }
        return false;
    }

    private static bool IsSecretFieldName(string name)
    {
        var normalized = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
                normalized.Append(char.ToLowerInvariant(character));
        }
        return normalized.ToString() is "rawkey" or "keyb64" or "keyhex" or "keymaterial";
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string NormalizeDatabasePath(string value) => value.Trim().Replace('/', '\\').TrimEnd('\\');
}
