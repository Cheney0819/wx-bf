using System.Text.Json;
using System.Text.Json.Serialization;

namespace Footprint.Core;

public sealed record ChatImageRuntimePathProbeResult(
    int EventCount,
    int MediaEventCount,
    int MatchedIndexCount,
    int SuccessfulOpenCount,
    IReadOnlyDictionary<string, int> RootCounts);

public static class ChatImageRuntimePathProbeReader
{
    public static ChatImageRuntimePathProbeResult Read(string captureDirectory,
        IReadOnlyCollection<ChatImageIndexRecord> index)
    {
        var path = Path.Combine(captureDirectory, "runtime-file-opens.jsonl");
        if (!File.Exists(path)) return new(0, 0, 0, 0, new Dictionary<string, int>());

        var names = index.Select(item => item.FileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eventCount = 0;
        var mediaCount = 0;
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var successful = 0;
        var roots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            RuntimeFileOpen? item;
            try { item = JsonSerializer.Deserialize<RuntimeFileOpen>(line, TargetProfile.JsonOptions); }
            catch (JsonException) { continue; }
            if (item is null) continue;
            eventCount++;
            if (!item.IsMedia && !LooksLikeMedia(item)) continue;
            mediaCount++;
            var root = string.IsNullOrWhiteSpace(item.PathKind) ? "unknown" : item.PathKind!;
            roots[root] = roots.TryGetValue(root, out var count) ? count + 1 : 1;
            if (item.OpenSucceeded) successful++;
            if (!string.IsNullOrWhiteSpace(item.FileName) && names.Contains(item.FileName)) matched.Add(item.FileName);
        }
        return new(eventCount, mediaCount, matched.Count, successful, roots);
    }

    private sealed class RuntimeFileOpen
    {
        [JsonPropertyName("path_kind")]
        public string? PathKind { get; init; }
        [JsonPropertyName("file_name")]
        public string? FileName { get; init; }
        [JsonPropertyName("open_succeeded")]
        public bool OpenSucceeded { get; init; }
        [JsonPropertyName("is_media")]
        public bool IsMedia { get; init; }
    }

    private static bool LooksLikeMedia(RuntimeFileOpen item) =>
        (item.PathKind?.StartsWith("FileStorage/", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (item.PathKind?.StartsWith("Msg/Attach", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (item.FileName?.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ?? false);
}
