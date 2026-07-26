using System.Buffers.Binary;

namespace Wx411.Core;

public enum WalOverlayFailureKind
{
    None,
    NoWal,
    Header,
    HeaderChecksum,
    FrameTruncated,
    FrameSalt,
    FrameChecksum,
    FramePageNumber,
    NoCommit,
    MissingPage,
}

public sealed record WalOverlayDiagnostics(
    WalOverlayFailureKind FailureKind,
    int ValidFrameCount,
    int LastCommitFrame,
    int LogicalPageCount,
    int OverlaidPageCount,
    int? FirstInvalidFrame,
    long AcceptedWalLength,
    string Detail);

public sealed record SqliteWalOverlayResult(
    byte[] Snapshot,
    bool Applied,
    WalOverlayDiagnostics Diagnostics);

public static class SqliteWalOverlay
{
    private const uint LittleEndianChecksumMagic = 0x377f0682;
    private const uint BigEndianChecksumMagic = 0x377f0683;
    private const uint SupportedVersion = 3_007_000;
    private const int HeaderSize = 32;

    public static SqliteWalOverlayResult Build(
        ReadOnlySpan<byte> database,
        ReadOnlySpan<byte> wal)
    {
        if (database.IsEmpty)
            throw new ArgumentException("Database snapshot must not be empty.", nameof(database));

        if (wal.IsEmpty)
            return Unchanged(database, WalOverlayFailureKind.NoWal, "WAL file is absent or empty.");
        if (wal.Length < HeaderSize)
            return Unchanged(database, WalOverlayFailureKind.Header, "WAL header is truncated.");

        var magic = BinaryPrimitives.ReadUInt32BigEndian(wal[..4]);
        if (magic is not (LittleEndianChecksumMagic or BigEndianChecksumMagic))
            return Unchanged(database, WalOverlayFailureKind.Header, "WAL magic is not supported.");
        if (BinaryPrimitives.ReadUInt32BigEndian(wal.Slice(4, 4)) != SupportedVersion)
            return Unchanged(database, WalOverlayFailureKind.Header, "WAL format version is not supported.");

        var pageSizeValue = BinaryPrimitives.ReadUInt32BigEndian(wal.Slice(8, 4));
        if (pageSizeValue > int.MaxValue || !IsValidPageSize((int)pageSizeValue))
            return Unchanged(database, WalOverlayFailureKind.Header, "WAL page size is invalid.");
        var pageSize = (int)pageSizeValue;
        if (database.Length % pageSize != 0)
            return Unchanged(database, WalOverlayFailureKind.Header, "Database length is not aligned to the WAL page size.");

        var computed = ExtendChecksum(wal[..24], magic, 0, 0);
        var stored0 = BinaryPrimitives.ReadUInt32BigEndian(wal.Slice(24, 4));
        var stored1 = BinaryPrimitives.ReadUInt32BigEndian(wal.Slice(28, 4));
        if (computed.S0 != stored0 || computed.S1 != stored1)
            return Unchanged(database, WalOverlayFailureKind.HeaderChecksum, "WAL header checksum does not match.");

        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(wal.Slice(16, 4));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(wal.Slice(20, 4));
        var frameSize = checked(24 + pageSize);
        var frames = new List<WalFrame>();
        var checksum0 = stored0;
        var checksum1 = stored1;
        var validFrameCount = 0;
        var lastCommitFrame = 0;
        var logicalPageCount = 0;
        var failureKind = WalOverlayFailureKind.None;
        int? firstInvalidFrame = null;
        var offset = HeaderSize;
        var frameIndex = 1;

        while (offset < wal.Length)
        {
            if (wal.Length - offset < frameSize)
            {
                failureKind = WalOverlayFailureKind.FrameTruncated;
                firstInvalidFrame = frameIndex;
                break;
            }

            var frameHeader = wal.Slice(offset, 24);
            var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(frameHeader[..4]);
            var databaseSize = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.Slice(4, 4));
            if (pageNumber == 0)
            {
                failureKind = WalOverlayFailureKind.FramePageNumber;
                firstInvalidFrame = frameIndex;
                break;
            }

            if (BinaryPrimitives.ReadUInt32BigEndian(frameHeader.Slice(8, 4)) != salt1 ||
                BinaryPrimitives.ReadUInt32BigEndian(frameHeader.Slice(12, 4)) != salt2)
            {
                failureKind = WalOverlayFailureKind.FrameSalt;
                firstInvalidFrame = frameIndex;
                break;
            }

            var nextChecksum = ExtendChecksum(frameHeader[..8], magic, checksum0, checksum1);
            nextChecksum = ExtendChecksum(
                wal.Slice(offset + 24, pageSize),
                magic,
                nextChecksum.S0,
                nextChecksum.S1);
            if (nextChecksum.S0 != BinaryPrimitives.ReadUInt32BigEndian(frameHeader.Slice(16, 4)) ||
                nextChecksum.S1 != BinaryPrimitives.ReadUInt32BigEndian(frameHeader.Slice(20, 4)))
            {
                failureKind = WalOverlayFailureKind.FrameChecksum;
                firstInvalidFrame = frameIndex;
                break;
            }

            if (databaseSize != 0 &&
                (databaseSize > int.MaxValue || (long)databaseSize * pageSize > int.MaxValue))
            {
                failureKind = WalOverlayFailureKind.FramePageNumber;
                firstInvalidFrame = frameIndex;
                break;
            }

            checksum0 = nextChecksum.S0;
            checksum1 = nextChecksum.S1;
            frames.Add(new WalFrame(pageNumber, databaseSize, offset + 24));
            validFrameCount++;
            if (databaseSize != 0)
            {
                lastCommitFrame = frameIndex;
                logicalPageCount = (int)databaseSize;
            }

            offset += frameSize;
            frameIndex++;
        }

        if (lastCommitFrame == 0)
        {
            var noCommitFailure = failureKind == WalOverlayFailureKind.None
                ? WalOverlayFailureKind.NoCommit
                : failureKind;
            return Unchanged(
                database,
                noCommitFailure,
                noCommitFailure == WalOverlayFailureKind.NoCommit
                    ? "WAL contains no committed frames."
                    : "WAL frame chain ended before a valid commit.",
                validFrameCount,
                lastCommitFrame,
                logicalPageCount,
                firstInvalidFrame);
        }

        var committedFrames = frames.Take(lastCommitFrame).ToArray();
        var latestByPage = new Dictionary<uint, WalFrame>();
        foreach (var frame in committedFrames)
        {
            if (frame.PageNumber <= logicalPageCount)
                latestByPage[frame.PageNumber] = frame;
        }

        var existingPageCount = database.Length / pageSize;
        for (var pageNumber = existingPageCount + 1; pageNumber <= logicalPageCount; pageNumber++)
        {
            if (!latestByPage.ContainsKey((uint)pageNumber))
            {
                return Unchanged(
                    database,
                    WalOverlayFailureKind.MissingPage,
                    $"WAL commit requires page {pageNumber}, which is absent from the main database and WAL.",
                    validFrameCount,
                    lastCommitFrame,
                    logicalPageCount,
                    firstInvalidFrame,
                    acceptedWalLength: HeaderSize + (long)lastCommitFrame * frameSize);
            }
        }

        var snapshot = GC.AllocateUninitializedArray<byte>(checked(logicalPageCount * pageSize));
        database[..Math.Min(database.Length, snapshot.Length)].CopyTo(snapshot);
        foreach (var (pageNumber, frame) in latestByPage)
        {
            wal.Slice(frame.DataOffset, pageSize)
                .CopyTo(snapshot.AsSpan(checked(((int)pageNumber - 1) * pageSize), pageSize));
        }

        return new SqliteWalOverlayResult(
            snapshot,
            Applied: true,
            new WalOverlayDiagnostics(
                failureKind,
                validFrameCount,
                lastCommitFrame,
                logicalPageCount,
                latestByPage.Count,
                firstInvalidFrame,
                HeaderSize + (long)lastCommitFrame * frameSize,
                failureKind == WalOverlayFailureKind.None
                    ? "Applied the last checksum-valid committed WAL view."
                    : "Applied the last valid WAL commit before the frame chain ended."));
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

    private static SqliteWalOverlayResult Unchanged(
        ReadOnlySpan<byte> database,
        WalOverlayFailureKind failureKind,
        string detail,
        int validFrameCount = 0,
        int lastCommitFrame = 0,
        int logicalPageCount = 0,
        int? firstInvalidFrame = null,
        long acceptedWalLength = 0) =>
        new(
            database.ToArray(),
            Applied: false,
            new WalOverlayDiagnostics(
                failureKind,
                validFrameCount,
                lastCommitFrame,
                logicalPageCount,
                OverlaidPageCount: 0,
                firstInvalidFrame,
                acceptedWalLength,
                detail));

    private sealed record WalFrame(uint PageNumber, uint DatabaseSize, int DataOffset);
}
