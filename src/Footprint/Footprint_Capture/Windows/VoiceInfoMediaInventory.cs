using System.Text;
using System.Text.Json;
using Footprint.Core;
using Footprint.Core.Capture;

namespace Footprint.Capture.Windows;

public sealed record VoiceInfoMediaInventoryResult(
    IReadOnlyList<MediaSnapshotSource> Sources,
    int ExpectedRecordCount);

/// <summary>
/// Reads the media files emitted by the VoiceInfo export. The database BLOB is
/// exported by Frida as a stable Silk file; this class only inventories those
/// verified files and carries row-level provenance into the publish manifest.
/// </summary>
public sealed class VoiceInfoMediaInventory
{
    public VoiceInfoMediaInventoryResult Discover(string runtimeExportRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeExportRoot);
        var root = Path.GetFullPath(runtimeExportRoot);
        if (!Directory.Exists(root)) return new([], 0);
        if (IsReparsePoint(root)) throw new InvalidDataException("语音导出目录不能是链接。");

        var records = ReadRecords(root, cancellationToken);
        var expectedRecordCount = Math.Max(records.Count, ReadExpectedRecordCount(root, cancellationToken));
        var sources = new List<MediaSnapshotSource>();
        var voiceRoot = Path.Combine(root, "artifacts", "voice");
        if (Directory.Exists(voiceRoot) && !IsReparsePoint(voiceRoot))
        {
            foreach (var path in Directory.EnumerateFiles(voiceRoot, "*.silk", SearchOption.TopDirectoryOnly)
                         .OrderBy(item => item, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(path);
                if (IsReparsePoint(full)) continue;
                var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (!CaptureWorkspace.IsSafeRelativePath(relative)) continue;
                records.TryGetValue(relative, out var evidence);
                evidence ??= new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source_table"] = "VoiceInfo",
                    ["source_column"] = "voice_data",
                    ["artifact_path"] = relative
                };
                var identity = Hashing.Sha256Hex(Encoding.UTF8.GetBytes(relative));
                sources.Add(new MediaSnapshotSource(full, relative, CaptureSourceCategory.Voice, identity, evidence));
            }
        }

        return new VoiceInfoMediaInventoryResult(sources, expectedRecordCount);
    }

    private static int ReadExpectedRecordCount(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "table-stats.jsonl");
        if (!File.Exists(path) || IsReparsePoint(path)) return 0;
        long total = 0;
        foreach (var line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement;
                if (item.ValueKind != JsonValueKind.Object ||
                    !string.Equals(GetString(item, "table"), "VoiceInfo", StringComparison.OrdinalIgnoreCase) ||
                    !long.TryParse(GetString(item, "row_count"), out var count) || count <= 0) continue;
                total = Math.Min(int.MaxValue, total + count);
            }
            catch (JsonException)
            {
            }
        }
        return (int)total;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> ReadRecords(string root,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var path = Path.Combine(root, "voice-records.jsonl");
        if (!File.Exists(path) || IsReparsePoint(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement;
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("artifact_path", out var artifact) ||
                    artifact.ValueKind != JsonValueKind.String) continue;
                var relative = artifact.GetString()!.Replace('\\', '/');
                if (!CaptureWorkspace.IsSafeRelativePath(relative)) continue;
                var evidence = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["artifact_path"] = relative,
                    ["source_table"] = GetString(item, "table") ?? "VoiceInfo",
                    ["source_column"] = GetString(item, "column") ?? "voice_data"
                };
                CopyIfPresent(item, evidence, "database_path");
                CopyIfPresent(item, evidence, "row_identity");
                CopyIfPresent(item, evidence, "create_time");
                result[relative] = evidence;
            }
            catch (JsonException)
            {
                // A malformed optional record must not make an otherwise verified
                // artifact unavailable; the file remains discoverable without row evidence.
            }
        }
        return result;
    }

    private static string? GetString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    private static void CopyIfPresent(JsonElement item, IDictionary<string, string> evidence, string name)
    {
        var value = GetString(item, name);
        if (!string.IsNullOrWhiteSpace(value)) evidence[name] = value;
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
