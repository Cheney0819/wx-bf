namespace Footprint.Core;

public static class AtomicFile
{
    internal static async Task WriteAsync(string finalPath,
        Func<Stream, CancellationToken, Task> writeAsync, CancellationToken cancellationToken,
        Action<string, string>? publish = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var temporary = finalPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous))
            {
                await writeAsync(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            (publish ?? Replace)(temporary, finalPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Replace(string temporaryPath, string finalPath)
    {
        if (!File.Exists(finalPath))
        {
            File.Move(temporaryPath, finalPath);
            return;
        }

        var backup = finalPath + ".rollback";
        if (File.Exists(backup)) File.Delete(backup);
        try
        {
            File.Replace(temporaryPath, finalPath, backup, true);
            File.Delete(backup);
        }
        catch
        {
            if (File.Exists(backup))
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(backup, finalPath);
            }
            throw;
        }
    }
}
