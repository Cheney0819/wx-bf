using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Footprint.Core;

public sealed class SessionManifest
{
    [JsonPropertyName("session_id")] public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("created_utc")] public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("dll_sha256")] public string DllSha256 { get; init; } = string.Empty;
    [JsonPropertyName("databases")] public List<DatabaseManifest> Databases { get; init; } = [];
    [JsonPropertyName("ambiguities")] public List<CaptureAmbiguity> Ambiguities { get; init; } = [];
    [JsonPropertyName("chat_images")] public ChatImageManifest ChatImages { get; set; } = new();

    public static SessionManifest Sample(string keySha256, string rawKey) => new()
    {
        DllSha256 = "4914a621a810ecbc0a132b6ff8f612658cfce323d3989b3e5fe32d4ff343ba46",
        Databases = [new DatabaseManifest { Path = "C:\\sample.db", KeySha256 = keySha256, KeyLength = rawKey.Length / 2 }]
    };
}

public sealed class DatabaseManifest
{
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("tag")] public int Tag { get; init; }
    [JsonPropertyName("db_pointer")] public string DbPointer { get; init; } = string.Empty;
    [JsonPropertyName("path_from_db")] public string? PathFromDb { get; init; }
    [JsonPropertyName("key_sha256")] public string KeySha256 { get; init; } = string.Empty;
    [JsonPropertyName("key_length")] public int KeyLength { get; init; }
    [JsonPropertyName("protected_key_path")] public string? ProtectedKeyPath { get; set; }
    [JsonPropertyName("page_size")] public int PageSize { get; set; }
    [JsonPropertyName("compatibility")] public int Compatibility { get; set; }
    [JsonPropertyName("snapshot")] public SnapshotManifest Snapshot { get; set; } = new();
    [JsonPropertyName("verification")] public VerificationManifest Verification { get; set; } = new();
    [JsonPropertyName("decompression")] public DecompressionManifest Decompression { get; set; } = new();
    [JsonPropertyName("plaintext_export")] public PlaintextExportManifest PlaintextExport { get; set; } = new();
}

public sealed class SnapshotManifest
{
    [JsonPropertyName("directory")] public string? Directory { get; set; }
    [JsonPropertyName("files")] public List<SnapshotFile> Files { get; set; } = [];
    [JsonPropertyName("stable")] public bool Stable { get; set; }
    [JsonPropertyName("stability_attempts")] public int StabilityAttempts { get; set; }
}

public sealed record SnapshotFile(string Name, long Size, DateTimeOffset LastWriteUtc, string Sha256);
public sealed class VerificationManifest
{
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("trials")] public List<VerificationTrial> Trials { get; set; } = [];
}
public sealed class DecompressionManifest
{
    [JsonPropertyName("completed")] public bool Completed { get; set; }
    [JsonPropertyName("records")] public long Records { get; set; }
    [JsonPropertyName("schema_objects")] public long SchemaObjects { get; set; }
    [JsonPropertyName("tables")] public long Tables { get; set; }
    [JsonPropertyName("compressed_columns")] public long CompressedColumns { get; set; }
    [JsonPropertyName("schema_path")] public string? SchemaPath { get; set; }
    [JsonPropertyName("compression_records_path")] public string? CompressionRecordsPath { get; set; }
    [JsonPropertyName("records_path")] public string? RecordsPath { get; set; }
    [JsonPropertyName("table_stats_path")] public string? TableStatsPath { get; set; }
    [JsonPropertyName("table_preview_path")] public string? TablePreviewPath { get; set; }
    [JsonPropertyName("failures")] public List<string> Failures { get; set; } = [];
}

public sealed class PlaintextExportManifest
{
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("stage")] public string? Stage { get; set; }
    [JsonPropertyName("output_path")] public string? OutputPath { get; set; }
    [JsonPropertyName("manifest_path")] public string? ManifestPath { get; set; }
    [JsonPropertyName("output_sha256")] public string? OutputSha256 { get; set; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
    [JsonPropertyName("error_summary")] public string? ErrorSummary { get; set; }
}

public static class ManifestWriter
{
    public static Task WriteAsync(SessionManifest manifest, string path, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAsync(path, (stream, token) =>
            JsonSerializer.SerializeAsync(stream, manifest, TargetProfile.JsonOptions, token), cancellationToken);
}

public static class ReportRenderer
{
    public static string Render(SessionManifest manifest)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        static string Link(string? path, string label) => path is null ? "—" :
            $"<a href=\"{E(path.Replace('\\', '/'))}\">{E(label)}</a>";
        var rows = new StringBuilder();
        foreach (var db in manifest.Databases)
        {
            var failures = db.Decompression.Failures.Count == 0 ? "—" : string.Join("；", db.Decompression.Failures);
            var plaintextPassed = string.Equals(db.PlaintextExport.Status, "passed", StringComparison.Ordinal);
            var plaintextStatus = db.PlaintextExport.Status switch
            {
                "passed" => "明文数据库已生成",
                "excluded" => "已排除",
                "failed" => "失败",
                _ => "未执行"
            };
            rows.Append("<tr><td>").Append(E(db.Path)).Append("</td><td>").Append(db.Tag)
                .Append("</td><td><code>").Append(E(db.KeySha256)).Append("</code></td><td>")
                .Append(db.Verification.Accepted ? "通过" : "未通过").Append("</td><td>")
                .Append(E(plaintextStatus)).Append("</td><td>")
                .Append(db.Decompression.SchemaObjects).Append("</td><td>").Append(db.Decompression.Tables)
                .Append("</td><td>").Append(db.Decompression.CompressedColumns).Append("</td><td>")
                .Append(db.Decompression.Records).Append("</td><td>").Append(E(db.Verification.Reason))
                .Append("</td><td>").Append(E(failures)).Append("</td><td>")
                .Append(Link(db.Decompression.SchemaPath, "schema.jsonl")).Append(" · ")
                .Append(Link(db.Decompression.TableStatsPath, "table-stats.jsonl")).Append(" · ")
                .Append(Link(db.Decompression.TablePreviewPath, "table-preview.jsonl")).Append(" · ")
                .Append(Link(db.Decompression.RecordsPath, "records/")).Append("</td><td>")
                .Append(plaintextPassed ? Link(db.PlaintextExport.OutputPath, db.PlaintextExport.OutputPath ?? "明文数据库") : "—")
                .Append(plaintextPassed && db.PlaintextExport.ManifestPath is not null
                    ? " · " + Link(db.PlaintextExport.ManifestPath, "manifest.json") : string.Empty)
                .Append("</td></tr>");
        }

        var ambiguities = new StringBuilder();
        if (manifest.Ambiguities.Count == 0) ambiguities.Append("<p>无。</p>");
        else foreach (var ambiguity in manifest.Ambiguities)
                ambiguities.Append("<li>").Append(E(ambiguity.Reason)).Append("（事件数：")
                    .Append(ambiguity.Events.Count).Append("）</li>");

        var images = manifest.ChatImages;
        var imageFailures = images.Items.Where(item => item.Status != "passed")
            .GroupBy(item => item.ErrorCode ?? "unknown", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{E(group.Key)}：{group.Count()}");
        var imageSummary = images.Status == "not_run"
            ? "<p>Windows 源端不执行 FFmpeg，聊天图片待 Mac 接收端恢复。</p>"
            : $"<p>状态：{E(images.Status)} · 索引 {images.Expected} · 已定位 {images.Located} · 已恢复 {images.Passed} · 失败 {images.Failed} · 本地缺失 {images.Missing}</p>" +
              $"<p>媒体快照：已复制 {images.MediaSnapshot.CopiedFileCount} · 已匹配索引 {images.MediaSnapshot.MatchedIndexCount} · 缺失索引 {images.MediaSnapshot.MissingIndexCount}</p>" +
              "<p><a href=\"chat-images/manifest.json\">manifest.json</a> · <a href=\"chat-images/\">图片输出目录</a></p>" +
              $"<p>失败汇总：{(imageFailures.Any() ? string.Join("；", imageFailures) : "无")}</p>";

        return "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>Footprint</title>" +
               "<style>body{font-family:Segoe UI,Microsoft YaHei,sans-serif;margin:32px;background:#f5f6f8;color:#202124}" +
               "main{max-width:1200px;margin:auto;background:white;padding:28px;border-radius:12px}table{width:100%;border-collapse:collapse}" +
               "th,td{padding:10px;border-bottom:1px solid #ddd;text-align:left}code{word-break:break-all}</style></head><body><main>" +
               $"<h1>Weixin 数据库采集报告</h1><p>会话：{E(manifest.SessionId)}</p><p>DLL SHA-256：<code>{E(manifest.DllSha256)}</code></p>" +
               "<table><thead><tr><th>数据库</th><th>tag</th><th>密钥指纹</th><th>验证</th><th>明文导出</th><th>Schema 对象</th><th>表</th><th>压缩列</th><th>解压记录</th><th>验证说明</th><th>失败原因</th><th>解压结果</th><th>明文结果</th></tr></thead><tbody>" +
               rows + "</tbody></table><h2>聊天图片</h2>" + imageSummary +
               "<h2>歧义诊断</h2><ul>" + ambiguities + "</ul></main></body></html>";
    }
}
