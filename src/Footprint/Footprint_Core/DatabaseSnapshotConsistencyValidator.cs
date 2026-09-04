using System.Buffers.Binary;

namespace Footprint.Core;

public sealed record DatabaseSnapshotConsistencyResult(
    bool IsConsistent,
    string ErrorCode,
    string MessageZh,
    long DatabaseSize,
    long WalSize,
    long ShmSize,
    int PageSize,
    bool IsSqliteHeader,
    bool WalHeaderValid,
    bool WalPageSizeMatches,
    bool WalFrameAligned,
    bool WalSaltConsistent);

public static class DatabaseSnapshotConsistencyValidator
{
    private static ReadOnlySpan<byte> SqliteHeader => "SQLite format 3\0"u8;
    private const int WalHeaderLength = 32;
    private const int WalFrameHeaderLength = 24;

    public static DatabaseSnapshotConsistencyResult Validate(string databasePath, int pageSize = 4096)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var databaseSize = File.Exists(databasePath) ? new FileInfo(databasePath).Length : 0;
        var walPath = databasePath + "-wal";
        var shmPath = databasePath + "-shm";
        var walSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        var shmSize = File.Exists(shmPath) ? new FileInfo(shmPath).Length : 0;
        if (!File.Exists(databasePath))
            return Result(false, "database_missing", "数据库主文件不存在。", databaseSize, walSize, shmSize, pageSize, false, false, false, false, false);
        if (!IsValidPageSize(pageSize))
            return Result(false, "page_size_invalid", "数据库页大小无效。", databaseSize, walSize, shmSize, pageSize, false, false, false, false, false);
        if (databaseSize < pageSize || databaseSize % pageSize != 0)
            return Result(false, "database_page_alignment_invalid", "数据库主文件长度未按页大小对齐。", databaseSize, walSize, shmSize, pageSize, false, false, false, false, false);

        var header = ReadPrefix(databasePath, 100);
        var isSqlite = header.AsSpan().StartsWith(SqliteHeader);
        var effectivePageSize = pageSize;
        if (isSqlite && header.Length >= 18)
        {
            var encoded = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16, 2));
            effectivePageSize = encoded == 1 ? 65536 : encoded;
            if (!IsValidPageSize(effectivePageSize) || effectivePageSize != pageSize)
                return Result(false, "sqlite_page_size_mismatch", "SQLite 页头页大小与绑定页大小不一致。", databaseSize, walSize, shmSize, effectivePageSize, true, false, false, false, false);
        }

        if (walSize == 0)
            return Result(true, "consistent", "数据库快照文件布局一致。", databaseSize, walSize, shmSize, effectivePageSize, isSqlite, true, true, true, true);
        if (walSize < WalHeaderLength)
            return Result(false, "wal_header_missing", "WAL 文件头不完整。", databaseSize, walSize, shmSize, effectivePageSize, isSqlite, false, false, false, false);

        var walHeader = ReadPrefix(walPath, WalHeaderLength);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(walHeader.AsSpan(0, 4));
        var walHeaderValid = magic is 0x377F0682 or 0x377F0683;
        var walPageSize = BinaryPrimitives.ReadInt32BigEndian(walHeader.AsSpan(8, 4));
        var walPageSizeMatches = walHeaderValid && walPageSize == effectivePageSize;
        var frameAligned = walHeaderValid && walPageSizeMatches && (walSize - WalHeaderLength) % (effectivePageSize + WalFrameHeaderLength) == 0;
        var walSaltConsistent = walHeaderValid && walPageSizeMatches && frameAligned && WalFramesMatchHeaderSalts(walPath, walSize, effectivePageSize, walHeader);
        if (!walHeaderValid)
            return Result(false, "wal_header_invalid", "WAL 文件头 magic 无效。", databaseSize, walSize, shmSize, effectivePageSize, isSqlite, false, false, false, false);
        if (!walPageSizeMatches)
            return Result(false, "wal_page_size_mismatch", "WAL 页大小与数据库页大小不一致。", databaseSize, walSize, shmSize, effectivePageSize, isSqlite, true, false, false, false);
        if (!frameAligned)
            return Result(false, "wal_frame_alignment_invalid", "WAL frame 长度未按页大小对齐。", databaseSize, walSize, shmSize, effectivePageSize, isSqlite, true, true, false, false);
        if (!walSaltConsistent)
            return Result(false, "wal_salt_mismatch", "WAL frame 盐值与 WAL 文件头不一致。", databaseSize, walSize, shmSize, effectivePageSize, isSqlite, true, true, true, false);
        return Result(true, "consistent", "数据库快照文件布局一致。", databaseSize, walSize, shmSize, effectivePageSize, isSqlite, true, true, true, true);
    }

    private static bool WalFramesMatchHeaderSalts(string path, long walSize, int pageSize, byte[] header)
    {
        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var frame = new byte[WalFrameHeaderLength];
        for (long offset = WalHeaderLength; offset + WalFrameHeaderLength <= walSize; offset += pageSize + WalFrameHeaderLength)
        {
            stream.Position = offset;
            if (stream.Read(frame, 0, frame.Length) != frame.Length) return false;
            if (BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8, 4)) != salt1 ||
                BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(12, 4)) != salt2) return false;
        }
        return true;
    }

    private static bool IsValidPageSize(int value) => value is >= 512 and <= 65536 && (value & (value - 1)) == 0;

    private static byte[] ReadPrefix(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(count, checked((int)Math.Min(stream.Length, count)))];
        _ = stream.Read(buffer, 0, buffer.Length);
        return buffer;
    }

    private static DatabaseSnapshotConsistencyResult Result(bool consistent, string code, string message,
        long databaseSize, long walSize, long shmSize, int pageSize, bool sqlite, bool walHeader, bool walPage, bool frame, bool salt) =>
        new(consistent, code, message, databaseSize, walSize, shmSize, pageSize, sqlite, walHeader, walPage, frame, salt);
}
