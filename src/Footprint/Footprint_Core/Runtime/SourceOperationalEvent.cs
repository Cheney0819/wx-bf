namespace Footprint.Core.Runtime;

public sealed record SourceOperationalEvent(
    string EventId,
    string DeviceId,
    string RunId,
    string ComponentZh,
    string EventZh,
    string ResultZh,
    DateTimeOffset OccurredAtUtc)
{
    public void Validate()
    {
        ValidateIdentifier(EventId, nameof(EventId));
        ValidateIdentifier(DeviceId, nameof(DeviceId));
        ValidateIdentifier(RunId, nameof(RunId));
        ValidateText(ComponentZh, nameof(ComponentZh));
        ValidateText(EventZh, nameof(EventZh));
        ValidateText(ResultZh, nameof(ResultZh));
        if (OccurredAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("来源事件时间必须为 UTC。");
    }

    internal static string CleanText(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "无" : value.Trim();
        normalized = normalized.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Trim();
        return normalized.Length <= 2048 ? normalized : normalized[..2048];
    }

    internal static void ValidateIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new InvalidDataException($"来源事件{name}无效。");
    }

    private static void ValidateText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException($"来源事件{name}无效。");
    }
}

public sealed record StoredSourceOperationalEvent(SourceOperationalEvent Event, string Path);
