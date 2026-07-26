using System.Security.Cryptography;

namespace Wx411.Core;

internal enum WalSnapshotReadStage
{
    MainRead,
    WalRead,
}

public static class WalSnapshotReader
{
    private const int BufferSize = 1024 * 1024;
    private const int MaximumAttempts = 3;

    public static SqliteWalOverlayResult ReadCommittedOverlay(
        string databasePath,
        CancellationToken cancellationToken = default) =>
        ReadCommittedOverlay(databasePath, observer: null, cancellationToken);

    internal static SqliteWalOverlayResult ReadCommittedOverlay(
        string databasePath,
        Action<int, WalSnapshotReadStage>? observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? database = null;
            byte[]? wal = null;
            SqliteWalOverlayResult? overlay = null;
            var accepted = false;
            try
            {
                database = ReadStableFile(databasePath, cancellationToken);
                observer?.Invoke(attempt, WalSnapshotReadStage.MainRead);

                var walPath = databasePath + "-wal";
                if (!File.Exists(walPath))
                {
                    if (!FileSnapshot.MatchesFile(databasePath, database, cancellationToken))
                        continue;
                    overlay = SqliteWalOverlay.Build(database, ReadOnlySpan<byte>.Empty);
                    accepted = true;
                    return overlay;
                }

                wal = ReadAllBytes(walPath, cancellationToken);
                observer?.Invoke(attempt, WalSnapshotReadStage.WalRead);
                overlay = SqliteWalOverlay.Build(database, wal);

                if (!HeaderMatches(walPath, wal, cancellationToken))
                    continue;
                if (overlay.Diagnostics.AcceptedWalLength > 0 &&
                    !PrefixMatches(
                        walPath,
                        wal,
                        overlay.Diagnostics.AcceptedWalLength,
                        cancellationToken))
                    continue;
                if (!FileSnapshot.MatchesFile(databasePath, database, cancellationToken))
                    continue;

                accepted = true;
                return overlay;
            }
            catch (FileNotFoundException) when (attempt < MaximumAttempts)
            {
                // A checkpoint can replace or remove the WAL between reads.
            }
            finally
            {
                if (!accepted && overlay is not null)
                    CryptographicOperations.ZeroMemory(overlay.Snapshot);
                if (database is not null)
                    CryptographicOperations.ZeroMemory(database);
                if (wal is not null)
                    CryptographicOperations.ZeroMemory(wal);
            }

            if (attempt < MaximumAttempts)
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(75));
        }

        throw new IOException(
            "数据库或 WAL 在连续三次捕获中发生代际变化，未取得 checksum 连续的一致快照。");
    }

    private static byte[] ReadStableFile(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var bytes = ReadAllBytes(path, cancellationToken);
            if (FileSnapshot.MatchesFile(path, bytes, cancellationToken))
                return bytes;
            CryptographicOperations.ZeroMemory(bytes);
            if (attempt < MaximumAttempts)
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(25));
        }

        throw new IOException("数据库主文件在连续三次读取中发生变化。");
    }

    private static byte[] ReadAllBytes(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan);
        if (stream.Length > int.MaxValue)
            throw new IOException("数据库或 WAL 超过当前单文件处理上限（2 GB）。");

        var bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        var offset = 0;
        try
        {
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(bytes, offset, Math.Min(BufferSize, bytes.Length - offset));
                if (read == 0) throw new EndOfStreamException("文件读取过程中被截断。");
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

    private static bool HeaderMatches(
        string walPath,
        ReadOnlySpan<byte> capturedWal,
        CancellationToken cancellationToken)
    {
        if (capturedWal.Length < 32) return File.Exists(walPath);
        var current = new byte[32];
        try
        {
            using var stream = new FileStream(
                walPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            cancellationToken.ThrowIfCancellationRequested();
            return stream.Read(current, 0, current.Length) == current.Length &&
                   capturedWal[..32].SequenceEqual(current);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    private static bool PrefixMatches(
        string walPath,
        ReadOnlySpan<byte> capturedWal,
        long acceptedLength,
        CancellationToken cancellationToken)
    {
        if (acceptedLength > capturedWal.Length || acceptedLength > int.MaxValue)
            return false;

        using var stream = new FileStream(
            walPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan);
        if (stream.Length < acceptedLength) return false;

        var buffer = new byte[BufferSize];
        try
        {
            var offset = 0;
            while (offset < acceptedLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(buffer.Length, acceptedLength - offset);
                var read = stream.Read(buffer, 0, requested);
                if (read != requested ||
                    !capturedWal.Slice(offset, read).SequenceEqual(buffer.AsSpan(0, read)))
                    return false;
                offset += read;
            }

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
