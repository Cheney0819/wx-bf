using System.Text.Json;
using Footprint.Core;
using Footprint.Core.Capture;

namespace Footprint.Capture.Windows;

public sealed record ImageProtocolMediaInventoryResult(
    IReadOnlyList<MediaSnapshotSource> Sources,
    int ExpectedArtifactCount,
    bool ProtocolVerified,
    int DecryptedArtifactCount);

/// <summary>
/// Inventories plaintext image payloads emitted by the verified Frida image protocol.
/// Protocol verification alone is not publication; only files with a matching artifact
/// record and an existing payload enter Footprint_ImageSnapshot.
/// </summary>
public sealed class ImageProtocolMediaInventory
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".wxgf", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bin" };

    public ImageProtocolMediaInventoryResult Discover(string captureOutputRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureOutputRoot);
        var root = Path.GetFullPath(captureOutputRoot);
        if (!Directory.Exists(root) || IsReparsePoint(root)) return new([], 0, false, 0);
        var records = ReadRecords(root, cancellationToken);
        var protocolVerified = ReadProtocolVerified(root);
        var artifactRoot = Path.Combine(root, "image-protocol-artifacts");
        if (!Directory.Exists(artifactRoot) || IsReparsePoint(artifactRoot))
            return new([], records.Count, protocolVerified, 0);

        var sources = new List<MediaSnapshotSource>();
        foreach (var path in Directory.EnumerateFiles(artifactRoot, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(item => item, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(path) || !Extensions.Contains(Path.GetExtension(path))) continue;
            var full = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (!CaptureWorkspace.IsSafeRelativePath(relative) || !records.TryGetValue(relative, out var evidence))
                continue;
            var identity = Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(relative));
            sources.Add(new MediaSnapshotSource(full, relative, CaptureSourceCategory.Image, identity, evidence));
        }
        return new(sources, Math.Max(records.Count, sources.Count), protocolVerified, sources.Count);
    }

    private static bool ReadProtocolVerified(string root)
    {
        var path = Path.Combine(root, "image-protocol-diagnostics.json");
        if (!File.Exists(path) || IsReparsePoint(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("verified", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> ReadRecords(string root,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var path = Path.Combine(root, "image-protocol-artifacts.jsonl");
        if (!File.Exists(path) || IsReparsePoint(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement;
                if (!item.TryGetProperty("artifact_path", out var artifact) ||
                    artifact.ValueKind != JsonValueKind.String) continue;
                var relative = artifact.GetString()!.Replace('\\', '/');
                if (!CaptureWorkspace.IsSafeRelativePath(relative) ||
                    !relative.StartsWith("image-protocol-artifacts/", StringComparison.Ordinal)) continue;
                var evidence = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["artifact_path"] = relative,
                    ["source"] = "verified_image_protocol",
                    ["format"] = GetString(item, "format") ?? Path.GetExtension(relative).TrimStart('.')
                };
                CopyIfPresent(item, evidence, "sha256");
                CopyIfPresent(item, evidence, "length");
                CopyIfPresent(item, evidence, "container_sha256");
                CopyIfPresent(item, evidence, "stem");
                CopyIfPresent(item, evidence, "candidate_root");
                CopyIfPresent(item, evidence, "source_relative_path");
                result[relative] = evidence;
            }
            catch (JsonException) { }
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
