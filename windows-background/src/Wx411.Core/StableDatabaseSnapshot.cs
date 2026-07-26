using System.Security.Cryptography;

namespace Wx411.Core;

public static class StableDatabaseSnapshot
{
    private const int BufferSize = 1024 * 1024;
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(75);

    public static byte[] Read(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = CaptureFileStamp(path);
            var bytes = ReadAllBytesCancellable(path, cancellationToken);
            var accepted = false;
            try
            {
                var after = CaptureFileStamp(path);
                if (before == after && FileSnapshot.MatchesFile(path, bytes, cancellationToken))
                {
                    accepted = true;
                    return bytes;
                }
            }
            finally
            {
                if (!accepted) CryptographicOperations.ZeroMemory(bytes);
            }

            if (attempt < MaximumAttempts)
                cancellationToken.WaitHandle.WaitOne(RetryDelay);
        }

        throw new IOException("目标程序正在更新数据文件，连续三次读取都发生变化。暂停几秒后重试。");
    }

    private static byte[] ReadAllBytesCancellable(
        string path,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: BufferSize,
            options: FileOptions.SequentialScan);
        if (stream.Length > int.MaxValue)
            throw new IOException("数据库超过当前单文件处理上限（2 GB）。");

        var bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        var offset = 0;
        try
        {
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(bytes, offset, Math.Min(BufferSize, bytes.Length - offset));
                if (read == 0) throw new EndOfStreamException("数据库读取过程中被截断。");
                offset += read;
            }
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static FileStamp CaptureFileStamp(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists) throw new FileNotFoundException("数据库文件已消失。", path);
        return new FileStamp(info.Length, info.LastWriteTimeUtc);
    }

    private readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc);
}
