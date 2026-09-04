namespace Footprint.Core;

public static class ChatImageResourceCorrelator
{
    public static IReadOnlyList<ChatImageResourceMatch> Correlate(
        IReadOnlyCollection<ChatImageResourceRecord> resources,
        IEnumerable<string> filePaths)
    {
        var files = filePaths
            .Where(path => string.Equals(Path.GetExtension(path), ".dat", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        return resources
            .Where(resource => !string.IsNullOrWhiteSpace(resource.Stem))
            .Select(resource => new ChatImageResourceMatch(resource,
                files.TryGetValue(resource.Stem!, out var candidates) ? candidates : []))
            .ToArray();
    }
}
