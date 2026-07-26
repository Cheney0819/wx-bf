using System.Buffers;
using System.Security.Cryptography;

namespace Wx411.Core;

public static class FileSnapshot
{
    private const int BufferSize = 1024 * 1024;

    public static bool MatchesFile(
        string path,
        ReadOnlySpan<byte> snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: BufferSize,
            options: FileOptions.SequentialScan);
        if (stream.Length != snapshot.Length) return false;

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var offset = 0;
            while (offset < snapshot.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = Math.Min(buffer.Length, snapshot.Length - offset);
                var read = stream.Read(buffer, 0, requested);
                if (read == 0 || !snapshot.Slice(offset, read).SequenceEqual(buffer.AsSpan(0, read)))
                    return false;
                offset += read;
            }

            return stream.ReadByte() == -1;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
