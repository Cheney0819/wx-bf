using System.Buffers.Binary;

namespace Wx411.Core;

internal sealed record StreamingWalSnapshotResult(
    string Path,
    bool WalApplied,
    int PageSize,
    long Length);

internal static class StreamingWalSnapshot
{
    private const uint LittleEndianChecksumMagic = 0x377f0682;
    private const uint BigEndianChecksumMagic = 0x377f0683;
    private const uint SupportedVersion = 3_007_000;
    private const int HeaderSize = 32;
    private const int CopyBufferSize = 1024 * 1024;

    internal static StreamingWalSnapshotResult Build(
        string databasePath,
        string outputDirectory,
        DatabaseFileGeneration expectedGeneration,
        CancellationToken cancellationToken)
    {
        if (DatabaseProbeDescriptor.GetGeneration(databasePath) != expectedGeneration)
            throw new DatabaseGenerationChangedException(databasePath);
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(outputDirectory, $".encrypted.{Guid.NewGuid():N}.tmp");
        var completed = false;
        try
        {
            CopyMainDatabase(databasePath, temporaryPath, cancellationToken);
            var walPath = databasePath + "-wal";
            var walBefore = ReadFileMarker(walPath);
            var applied = walBefore is not null && ApplyWal(walPath, temporaryPath, cancellationToken);
            var walAfter = ReadFileMarker(walPath);
            if (walBefore != walAfter ||
                DatabaseProbeDescriptor.GetGeneration(databasePath) != expectedGeneration)
            {
                throw new DatabaseGenerationChangedException(databasePath);
            }

            var length = new FileInfo(temporaryPath).Length;
            var pageSize = ResolvePageSize(temporaryPath, walPath);
            completed = true;
            return new StreamingWalSnapshotResult(
                temporaryPath,
                applied,
                pageSize,
                length);
        }
        finally
        {
            if (!completed) TryDelete(temporaryPath);
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CopyMainDatabase(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        try
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                CopyBufferSize,
                FileOptions.SequentialScan);
            using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                CopyBufferSize,
                FileOptions.RandomAccess);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                target.Write(buffer, 0, read);
            }
            target.Flush(flushToDisk: true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static bool ApplyWal(
        string walPath,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        using var wal = new FileStream(
            walPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var header = new byte[HeaderSize];
        if (!TryReadExactly(wal, header, cancellationToken)) return false;
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        if (magic is not (LittleEndianChecksumMagic or BigEndianChecksumMagic) ||
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)) != SupportedVersion)
        {
            return false;
        }
        var pageSizeValue = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (pageSizeValue > int.MaxValue || !IsValidPageSize((int)pageSizeValue)) return false;
        var pageSize = (int)pageSizeValue;
        var checksum = ExtendChecksum(header.AsSpan(0, 24), magic, 0, 0);
        var stored0 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24, 4));
        var stored1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28, 4));
        if (checksum.S0 != stored0 || checksum.S1 != stored1) return false;
        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        var frameHeader = new byte[24];
        var page = new byte[pageSize];
        var checksum0 = stored0;
        var checksum1 = stored1;
        var frameIndex = 0;
        var lastCommitFrame = 0;
        uint logicalPageCount = 0;
        try
        {
            while (TryReadExactly(wal, frameHeader, cancellationToken))
            {
                if (!TryReadExactly(wal, page, cancellationToken)) break;
                frameIndex++;
                var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(0, 4));
                var databaseSize = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(4, 4));
                if (pageNumber == 0 ||
                    BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8, 4)) != salt1 ||
                    BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12, 4)) != salt2)
                {
                    break;
                }
                var next = ExtendChecksum(frameHeader.AsSpan(0, 8), magic, checksum0, checksum1);
                next = ExtendChecksum(page, magic, next.S0, next.S1);
                if (next.S0 != BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(16, 4)) ||
                    next.S1 != BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(20, 4)))
                {
                    break;
                }
                checksum0 = next.S0;
                checksum1 = next.S1;
                if (databaseSize != 0)
                {
                    lastCommitFrame = frameIndex;
                    logicalPageCount = databaseSize;
                }
            }
            if (lastCommitFrame == 0 || logicalPageCount == 0) return false;

            wal.Position = HeaderSize;
            using var snapshot = new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.RandomAccess);
            snapshot.SetLength(checked((long)logicalPageCount * pageSize));
            for (var index = 0; index < lastCommitFrame; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadExactly(wal, frameHeader, cancellationToken) ||
                    !TryReadExactly(wal, page, cancellationToken))
                {
                    throw new EndOfStreamException("WAL changed while applying committed frames.");
                }
                var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(0, 4));
                if (pageNumber == 0 || pageNumber > logicalPageCount)
                    throw new IntegrityException("Committed WAL frame page number is outside the logical database.");
                snapshot.Position = checked((long)(pageNumber - 1) * pageSize);
                snapshot.Write(page, 0, page.Length);
            }
            snapshot.Flush(flushToDisk: true);
            return true;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(header);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(frameHeader);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(page);
        }
    }

    private static int ResolvePageSize(string snapshotPath, string walPath)
    {
        if (File.Exists(walPath))
        {
            Span<byte> bytes = stackalloc byte[12];
            using var stream = new FileStream(
                walPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Read(bytes) == bytes.Length)
            {
                var value = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
                if (value <= int.MaxValue && IsValidPageSize((int)value)) return (int)value;
            }
        }
        var length = new FileInfo(snapshotPath).Length;
        return CipherProfileProbe.CandidateProfilesFor(length).FirstOrDefault()?.PageSize ?? 4096;
    }

    private static FileMarker? ReadFileMarker(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return info.Exists ? new FileMarker(info.Length, info.LastWriteTimeUtc) : null;
    }

    private static bool TryReadExactly(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static bool IsValidPageSize(int pageSize) =>
        pageSize is >= 512 and <= 65536 && (pageSize & (pageSize - 1)) == 0;

    private static (uint S0, uint S1) ExtendChecksum(
        ReadOnlySpan<byte> bytes,
        uint magic,
        uint initial0,
        uint initial1)
    {
        if (bytes.Length == 0 || bytes.Length % 8 != 0)
            throw new ArgumentException("WAL checksum input must be a non-empty multiple of 8 bytes.", nameof(bytes));
        var bigEndianWords = magic == BigEndianChecksumMagic;
        var s0 = initial0;
        var s1 = initial1;
        for (var offset = 0; offset < bytes.Length; offset += 8)
        {
            var first = bigEndianWords
                ? BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            var second = bigEndianWords
                ? BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 4, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            unchecked
            {
                s0 += first + s1;
                s1 += second + s0;
            }
        }
        return (s0, s1);
    }

    private sealed record FileMarker(long Length, DateTime LastWriteTimeUtc);
}

internal sealed class DatabaseGenerationChangedException : IOException
{
    internal DatabaseGenerationChangedException(string path)
        : base($"Database or WAL generation changed during streaming export: {path}")
    {
    }
}
