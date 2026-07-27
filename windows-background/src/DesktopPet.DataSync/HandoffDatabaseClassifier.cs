namespace DesktopPet.DataSync;

internal static class HandoffDatabaseClassifier
{
    internal static bool ContainsMessageDatabase(
        IEnumerable<string> relativePaths) =>
        relativePaths.Any(IsMessageDatabase);

    internal static bool IsMessageDatabase(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var normalized = relativePath.Replace('\\', '/');
        const string storagePrefix = "db_storage/";
        if (normalized.StartsWith(storagePrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[storagePrefix.Length..];
        const string messagePrefix = "message/";
        if (!normalized.StartsWith(messagePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = normalized[messagePrefix.Length..];
        if (fileName.Contains('/')) return false;
        return IsNumberedDatabase(fileName, "message_") ||
            IsNumberedDatabase(fileName, "biz_message_");
    }

    private static bool IsNumberedDatabase(string fileName, string prefix)
    {
        const string suffix = ".db";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var number = fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return !number.IsEmpty && number.IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
