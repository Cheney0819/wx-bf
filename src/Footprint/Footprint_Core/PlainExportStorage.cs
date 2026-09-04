using System.Text.Json;
using System.Text.Json.Serialization;

namespace Footprint.Core;

public sealed class PlainExportSummary
{
    public const int RequiredBusinessDatabaseCount = 20;
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = string.Empty;
    [JsonPropertyName("created_utc")] public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("status")] public string Status { get; set; } = "failed";
    [JsonPropertyName("expected")] public int Expected { get; init; }
    [JsonPropertyName("passed")] public int Passed { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("excluded")] public int Excluded { get; init; }
    [JsonPropertyName("databases")] public List<PlainExportDatabaseResult> Databases { get; init; } = [];

    public static string CalculateStatus(int passed, int failed, int expected) =>
        expected > 0 && passed == expected && failed == 0 ? "passed" : passed > 0 ? "partial" : "failed";
}

public sealed class PlainExportDatabaseResult
{
    [JsonPropertyName("slot")] public string? Slot { get; init; }
    [JsonPropertyName("source_index")] public int SourceIndex { get; init; }
    [JsonPropertyName("source_name")] public string SourceName { get; init; } = string.Empty;
    [JsonPropertyName("source_snapshot_files")] public List<SnapshotFile> SourceSnapshotFiles { get; init; } = [];
    [JsonPropertyName("output_path")] public string? OutputPath { get; init; }
    [JsonPropertyName("output_size")] public long OutputSize { get; init; }
    [JsonPropertyName("output_sha256")] public string? OutputSha256 { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "failed";
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("stage")] public string Stage { get; init; } = "source-check";
    [JsonPropertyName("compatibility")] public int Compatibility { get; init; }
    [JsonPropertyName("page_size")] public int PageSize { get; init; }
    [JsonPropertyName("source_user_version")] public int SourceUserVersion { get; init; }
    [JsonPropertyName("target_user_version")] public int TargetUserVersion { get; init; }
    [JsonPropertyName("source_schema_objects")] public int SourceSchemaObjects { get; init; }
    [JsonPropertyName("target_schema_objects")] public int TargetSchemaObjects { get; init; }
    [JsonPropertyName("tables_checked")] public int TablesChecked { get; init; }
    [JsonPropertyName("row_count_mismatches")] public List<string> RowCountMismatches { get; init; } = [];
    [JsonPropertyName("schema_mismatches")] public List<string> SchemaMismatches { get; init; } = [];
    [JsonPropertyName("integrity_check")] public string? IntegrityCheck { get; init; }
    [JsonPropertyName("duration_ms")] public long DurationMs { get; init; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; init; }
    [JsonPropertyName("error_summary")] public string? ErrorSummary { get; init; }
}

public static class PlainDbManifestWriter
{
    public static Task WriteAsync(PlainExportSummary summary, string path, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(path, (stream, token) =>
            JsonSerializer.SerializeAsync(stream, summary, TargetProfile.JsonOptions, token), cancellationToken);
}

public static class PlainDbPublisher
{
    public static Task PublishAsync(string temporaryPath, string finalPath, CancellationToken cancellationToken,
        Action<string, string>? replace = null, Action<string>? deleteFile = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        deleteFile ??= File.Delete;
        DeleteSidecars(temporaryPath, deleteFile);
        if (replace is not null)
        {
            replace(temporaryPath, finalPath);
            DeleteSidecars(finalPath, deleteFile);
            return Task.CompletedTask;
        }

        var backup = finalPath + ".rollback";
        var hadPrevious = File.Exists(finalPath);
        if (File.Exists(backup)) deleteFile(backup);
        try
        {
            if (hadPrevious) File.Replace(temporaryPath, finalPath, backup, true);
            else File.Move(temporaryPath, finalPath);
            DeleteSidecars(finalPath, deleteFile);
            if (File.Exists(backup)) deleteFile(backup);
            return Task.CompletedTask;
        }
        catch
        {
            if (File.Exists(finalPath)) File.Move(finalPath, temporaryPath, true);
            if (hadPrevious && File.Exists(backup)) File.Move(backup, finalPath, true);
            else if (File.Exists(backup)) deleteFile(backup);
            throw;
        }
    }

    public static void DeleteTemporary(string temporaryPath)
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        DeleteSidecars(temporaryPath, File.Delete);
    }

    private static void DeleteSidecars(string path, Action<string> deleteFile)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix)) deleteFile(path + suffix);
    }
}
