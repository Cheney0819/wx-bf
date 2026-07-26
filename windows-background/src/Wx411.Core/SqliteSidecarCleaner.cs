namespace Wx411.Core;

public static class SqliteSidecarCleaner
{
    public static void DeleteForTemporaryDatabase(string temporaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        var fullPath = Path.GetFullPath(temporaryPath);
        var sidecars = new[]
        {
            fullPath + "-wal",
            fullPath + "-shm",
            fullPath + ".tmp-wal",
            fullPath + ".tmp-shm",
        };

        foreach (var path in sidecars.Distinct(StringComparer.OrdinalIgnoreCase))
            TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Sidecar cleanup is best effort after the temporary database is closed.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup denial does not change the database export result.
        }
    }
}
