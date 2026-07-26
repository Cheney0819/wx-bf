namespace Wx411.Core;

public static class AtomicSqliteOutput
{
    private const int BufferSize = 1024 * 1024;

    public static string Write(
        string inputPath,
        string outputDirectory,
        byte[] plaintext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(plaintext);

        Directory.CreateDirectory(outputDirectory);
        var inputName = Path.GetFileNameWithoutExtension(inputPath);
        if (string.IsNullOrWhiteSpace(inputName)) inputName = "wechat_database";
        var outputPath = Path.Combine(outputDirectory, inputName + ".readable.sqlite");
        if (string.Equals(
                Path.GetFullPath(inputPath),
                Path.GetFullPath(outputPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("输出路径不可覆盖源数据库。");
        }

        if (File.Exists(outputPath))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            outputPath = Path.Combine(outputDirectory, $"{inputName}.readable.{stamp}.sqlite");
            var suffix = 2;
            while (File.Exists(outputPath))
                outputPath = Path.Combine(outputDirectory, $"{inputName}.readable.{stamp}-{suffix++}.sqlite");
        }

        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: BufferSize,
                       options: FileOptions.SequentialScan))
            {
                for (var offset = 0; offset < plaintext.Length; offset += BufferSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(BufferSize, plaintext.Length - offset);
                    stream.Write(plaintext, offset, count);
                }
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            SqliteIntegrityChecker.VerifyFile(temporaryPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath);
            return outputPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
        finally
        {
            SqliteSidecarCleaner.DeleteForTemporaryDatabase(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of an output that was never committed.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the primary write failure when cleanup is denied.
        }
    }
}
