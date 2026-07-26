using System.Buffers.Binary;
using Wx411.Core;

namespace Wx411.Core.Tests;

public sealed class WalSnapshotReaderTests
{
    [Fact]
    public void ReturnsStableMainWhenWalIsAbsent()
    {
        WithDatabase(
            Pages(2, 0x10, 0x20),
            (path, _) =>
            {
                var result = WalSnapshotReader.ReadCommittedOverlay(path);
                Assert.False(result.Applied);
                Assert.Equal(WalOverlayFailureKind.NoWal, result.Diagnostics.FailureKind);
                Assert.Equal(File.ReadAllBytes(path), result.Snapshot);
            });
    }

    [Fact]
    public void AppliesStableCommittedWal()
    {
        WithDatabase(
            Pages(2, 0x10, 0x20),
            (path, walPath) =>
            {
                File.WriteAllBytes(walPath, BuildSingleFrameWal(Page(0xB2)));

                var result = WalSnapshotReader.ReadCommittedOverlay(path);

                Assert.True(result.Applied);
                Assert.Equal(Page(0xB2), result.Snapshot.AsSpan(4096, 4096).ToArray());
            });
    }

    [Fact]
    public void AcceptsAppendOnlyBytesAfterCommittedPrefix()
    {
        WithDatabase(
            Pages(2, 0x10, 0x20),
            (path, walPath) =>
            {
                File.WriteAllBytes(walPath, BuildSingleFrameWal(Page(0xB2)));
                var appended = false;

                var result = WalSnapshotReader.ReadCommittedOverlay(
                    path,
                    (attempt, stage) =>
                    {
                        if (!appended && stage == WalSnapshotReadStage.WalRead)
                        {
                            using var stream = new FileStream(walPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                            stream.Write(new byte[17]);
                            stream.Flush(flushToDisk: true);
                            appended = true;
                        }
                    });

                Assert.True(result.Applied);
                Assert.Equal(1, result.Diagnostics.LastCommitFrame);
            });
    }

    [Fact]
    public void RejectsWalHeaderResetDuringEveryAttempt()
    {
        WithDatabase(
            Pages(2, 0x10, 0x20),
            (path, walPath) =>
            {
                File.WriteAllBytes(walPath, BuildSingleFrameWal(Page(0xB2)));

                Assert.Throws<IOException>(() => WalSnapshotReader.ReadCommittedOverlay(
                    path,
                    (attempt, stage) =>
                    {
                        if (stage != WalSnapshotReadStage.WalRead) return;
                        using var stream = new FileStream(walPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                        stream.Position = 12;
                        stream.WriteByte((byte)attempt);
                        stream.Flush(flushToDisk: true);
                    }));
            });
    }

    [Fact]
    public void RejectsMainFileMutationDuringEveryAttempt()
    {
        WithDatabase(
            Pages(2, 0x10, 0x20),
            (path, walPath) =>
            {
                File.WriteAllBytes(walPath, BuildSingleFrameWal(Page(0xB2)));

                Assert.Throws<IOException>(() => WalSnapshotReader.ReadCommittedOverlay(
                    path,
                    (attempt, stage) =>
                    {
                        if (stage != WalSnapshotReadStage.WalRead) return;
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                        stream.Position = 100;
                        stream.WriteByte((byte)(0x30 + attempt));
                        stream.Flush(flushToDisk: true);
                    }));
            });
    }

    private static byte[] BuildSingleFrameWal(byte[] pageData)
    {
        const uint magic = 0x377f0682;
        const uint salt1 = 0x10203040;
        const uint salt2 = 0x50607080;
        var wal = new byte[32 + 24 + 4096];
        BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(0, 4), magic);
        BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(4, 4), 3_007_000);
        BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(8, 4), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(16, 4), salt1);
        BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(20, 4), salt2);
        var checksum = Extend(wal.AsSpan(0, 24), magic, 0, 0);
        BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(24, 4), checksum.s0);
        BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(28, 4), checksum.s1);

        var frame = wal.AsSpan(32, 24);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(0, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(4, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(8, 4), salt1);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(12, 4), salt2);
        pageData.CopyTo(wal, 56);
        checksum = Extend(frame[..8], magic, checksum.s0, checksum.s1);
        checksum = Extend(pageData, magic, checksum.s0, checksum.s1);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(16, 4), checksum.s0);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Slice(20, 4), checksum.s1);
        return wal;
    }

    private static (uint s0, uint s1) Extend(
        ReadOnlySpan<byte> bytes,
        uint magic,
        uint s0,
        uint s1)
    {
        for (var offset = 0; offset < bytes.Length; offset += 8)
        {
            var first = magic == 0x377f0683
                ? BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            var second = magic == 0x377f0683
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

    private static byte[] Page(byte value) => Enumerable.Repeat(value, 4096).ToArray();

    private static byte[] Pages(int count, params byte[] values)
    {
        Assert.Equal(count, values.Length);
        return values.SelectMany(Page).ToArray();
    }

    private static void WithDatabase(byte[] database, Action<string, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wx411-wal-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "message_0.db");
        try
        {
            File.WriteAllBytes(path, database);
            action(path, path + "-wal");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
