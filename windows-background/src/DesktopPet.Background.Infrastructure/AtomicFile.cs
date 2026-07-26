namespace DesktopPet.Background.Infrastructure;

public static class AtomicFile
{
    public static void Replace(string destination, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var fullDestination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullDestination) ??
            throw new ArgumentException("Destination must include a directory.", nameof(destination));
        Directory.CreateDirectory(directory);

        var temporary = TemporaryPath(directory, fullDestination);
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullDestination, overwrite: true);
        }
        finally
        {
            TryDeleteTemporary(temporary);
        }
    }

    public static async Task ReplaceAsync(
        string destination,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        cancellationToken.ThrowIfCancellationRequested();

        var fullDestination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullDestination) ??
            throw new ArgumentException("Destination must include a directory.", nameof(destination));
        Directory.CreateDirectory(directory);

        var temporary = TemporaryPath(directory, fullDestination);
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullDestination, overwrite: true);
        }
        finally
        {
            TryDeleteTemporary(temporary);
        }
    }

    private static string TemporaryPath(string directory, string destination) =>
        Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup after the destination was committed or the write failed.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; callers still receive the original operation failure.
        }
    }
}
