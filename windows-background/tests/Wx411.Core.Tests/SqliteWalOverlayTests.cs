using System.Buffers.Binary;
using Wx411.Core;

namespace Wx411.Core.Tests;

public sealed class SqliteWalOverlayTests
{
    [Theory]
    [InlineData(0x377f0682u)]
    [InlineData(0x377f0683u)]
    public void AcceptsValidHeaderAndReportsNoCommit(uint magic)
    {
        var database = new byte[4096];
        var wal = WalFixtureBuilder.BuildHeader(magic, pageSize: 4096, salt1: 0x10203040, salt2: 0x50607080);

        var result = SqliteWalOverlay.Build(database, wal);

        Assert.False(result.Applied);
        Assert.Equal(WalOverlayFailureKind.NoCommit, result.Diagnostics.FailureKind);
        Assert.Equal(database, result.Snapshot);
    }

    [Fact]
    public void RejectsHeaderChecksumCorruption()
    {
        var database = new byte[4096];
        var wal = WalFixtureBuilder.BuildHeader(0x377f0682u, pageSize: 4096, salt1: 1, salt2: 2);
        wal[24] ^= 0x80;

        var result = SqliteWalOverlay.Build(database, wal);

        Assert.False(result.Applied);
        Assert.Equal(WalOverlayFailureKind.HeaderChecksum, result.Diagnostics.FailureKind);
        Assert.Equal(database, result.Snapshot);
    }

    [Fact]
    public void AppliesLatestFrameForEachPageThroughLastCommit()
    {
        var database = Pages(3, 0x10, 0x20, 0x30);
        var firstPage2 = Page(0xA2);
        var committedPage2 = Page(0xB2);
        var wal = WalFixtureBuilder.BuildWal(
            0x377f0682u,
            4096,
            11,
            12,
            new WalFrameSpec(2, 0, firstPage2),
            new WalFrameSpec(2, 3, committedPage2));

        var result = SqliteWalOverlay.Build(database, wal);

        Assert.True(result.Applied);
        Assert.Equal(WalOverlayFailureKind.None, result.Diagnostics.FailureKind);
        Assert.Equal(2, result.Diagnostics.ValidFrameCount);
        Assert.Equal(2, result.Diagnostics.LastCommitFrame);
        Assert.Equal(3, result.Diagnostics.LogicalPageCount);
        Assert.Equal(1, result.Diagnostics.OverlaidPageCount);
        Assert.Equal(committedPage2, result.Snapshot.AsSpan(4096, 4096).ToArray());
    }

    [Fact]
    public void IgnoresValidButUncommittedFramesAfterLastCommit()
    {
        var database = Pages(2, 0x10, 0x20);
        var committedPage2 = Page(0xB2);
        var uncommittedPage1 = Page(0xC1);
        var wal = WalFixtureBuilder.BuildWal(
            0x377f0683u,
            4096,
            21,
            22,
            new WalFrameSpec(2, 2, committedPage2),
            new WalFrameSpec(1, 0, uncommittedPage1));

        var result = SqliteWalOverlay.Build(database, wal);

        Assert.True(result.Applied);
        Assert.Equal(1, result.Diagnostics.LastCommitFrame);
        Assert.Equal(Page(0x10), result.Snapshot.AsSpan(0, 4096).ToArray());
        Assert.Equal(committedPage2, result.Snapshot.AsSpan(4096, 4096).ToArray());
    }

    [Fact]
    public void StopsAtChecksumFailureAndDoesNotUseLaterFrames()
    {
        var database = Pages(2, 0x10, 0x20);
        var wal = WalFixtureBuilder.BuildWal(
            0x377f0682u,
            4096,
            31,
            32,
            new WalFrameSpec(1, 0, Page(0xA1)),
            new WalFrameSpec(2, 2, Page(0xB2)));
        wal[32 + 16] ^= 0x01;

        var result = SqliteWalOverlay.Build(database, wal);

        Assert.False(result.Applied);
        Assert.Equal(WalOverlayFailureKind.FrameChecksum, result.Diagnostics.FailureKind);
        Assert.Equal(1, result.Diagnostics.FirstInvalidFrame);
        Assert.Equal(0, result.Diagnostics.ValidFrameCount);
        Assert.Equal(0, result.Diagnostics.LastCommitFrame);
        Assert.Equal(database, result.Snapshot);
    }

    [Fact]
    public void UsesPriorCommitWhenTailFrameIsTruncated()
    {
        var database = Pages(2, 0x10, 0x20);
        var wal = WalFixtureBuilder.BuildWal(
            0x377f0682u,
            4096,
            41,
            42,
            new WalFrameSpec(2, 2, Page(0xB2)));
        Array.Resize(ref wal, wal.Length + 19);

        var result = SqliteWalOverlay.Build(database, wal);

        Assert.True(result.Applied);
        Assert.Equal(WalOverlayFailureKind.FrameTruncated, result.Diagnostics.FailureKind);
        Assert.Equal(2, result.Diagnostics.FirstInvalidFrame);
        Assert.Equal(1, result.Diagnostics.LastCommitFrame);
        Assert.Equal(Page(0xB2), result.Snapshot.AsSpan(4096, 4096).ToArray());
    }

    [Fact]
    public void RejectsSaltMismatchBeforeCommit()
    {
        var database = Pages(2, 0x10, 0x20);
        var wal = WalFixtureBuilder.BuildWal(
            0x377f0682u,
            4096,
            51,
            52,
            new WalFrameSpec(2, 2, Page(0xB2)));
        wal[32 + 8] ^= 0x01;

        var result = SqliteWalOverlay.Build(database, wal);

        Assert.False(result.Applied);
        Assert.Equal(WalOverlayFailureKind.FrameSalt, result.Diagnostics.FailureKind);
        Assert.Equal(1, result.Diagnostics.FirstInvalidFrame);
        Assert.Equal(database, result.Snapshot);
    }

    [Fact]
    public void CommitCanTruncateDatabase()
    {
        var database = Pages(3, 0x10, 0x20, 0x30);
        var wal = WalFixtureBuilder.BuildWal(
            0x377f0682u,
            4096,
            61,
            62,
            new WalFrameSpec(1, 2, Page(0xA1)));

        var result = SqliteWalOverlay.Build(database, wal);

        Assert.True(result.Applied);
        Assert.Equal(2 * 4096, result.Snapshot.Length);
        Assert.Equal(Page(0xA1), result.Snapshot.AsSpan(0, 4096).ToArray());
    }

    [Fact]
    public void RejectsExpansionWhenARequiredPageIsMissing()
    {
        var database = Pages(1, 0x10);
        var wal = WalFixtureBuilder.BuildWal(
            0x377f0682u,
            4096,
            71,
            72,
            new WalFrameSpec(3, 3, Page(0xC3)));

        var result = SqliteWalOverlay.Build(database, wal);

        Assert.False(result.Applied);
        Assert.Equal(WalOverlayFailureKind.MissingPage, result.Diagnostics.FailureKind);
        Assert.Equal(database, result.Snapshot);
    }

    [Fact]
    public void IllegalCommitSizeFrameIsNotCountedAsValid()
    {
        var database = Pages(1, 0x10);
        var wal = WalFixtureBuilder.BuildWal(
            0x377f0682u,
            4096,
            75,
            76,
            new WalFrameSpec(1, uint.MaxValue, Page(0xA1)));

        var result = SqliteWalOverlay.Build(database, wal);

        Assert.False(result.Applied);
        Assert.Equal(WalOverlayFailureKind.FramePageNumber, result.Diagnostics.FailureKind);
        Assert.Equal(1, result.Diagnostics.FirstInvalidFrame);
        Assert.Equal(0, result.Diagnostics.ValidFrameCount);
        Assert.Equal(0, result.Diagnostics.LastCommitFrame);
        Assert.Equal(0, result.Diagnostics.AcceptedWalLength);
    }

    [Fact]
    public void CommittedWalPageRepairsObservedPage231AuthenticationFailure()
    {
        var profile = SqlCipher4.Profile;
        var fixture = CipherFixtureFactory.Create(profile, pageCount: 240);
        var damagedMain = CipherFixtureFactory.CorruptPageTag(
            fixture.Encrypted,
            profile,
            pageNumber: 231);
        var page231 = fixture.Encrypted.AsSpan(230 * profile.PageSize, profile.PageSize).ToArray();
        var wal = WalFixtureBuilder.BuildWal(
            0x377f0682u,
            profile.PageSize,
            81,
            82,
            new WalFrameSpec(231, 240, page231));
        var directory = Directory.CreateTempSubdirectory("wx411-streaming-wal-").FullName;
        var databasePath = Path.Combine(directory, "message_0.db");
        var outputDirectory = Path.Combine(directory, "out");
        File.WriteAllBytes(databasePath, damagedMain);
        File.WriteAllBytes(databasePath + "-wal", wal);
        try
        {
            using var descriptor = DatabaseProbeDescriptor.Read(databasePath);
            var snapshot = StreamingWalSnapshot.Build(
                databasePath,
                outputDirectory,
                descriptor.Generation,
                CancellationToken.None);
            var encrypted = File.ReadAllBytes(snapshot.Path);
            try
            {
                var report = SqlCipher4.AuthenticateDatabase(encrypted, fixture.Key, profile);
                Assert.True(snapshot.WalApplied);
                Assert.True(report.IsValid);
                var plaintext = SqlCipher4.DecryptDatabase(encrypted, fixture.Key, profile);
                try
                {
                    Assert.Equal(fixture.Plaintext, plaintext);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(encrypted);
                StreamingWalSnapshot.TryDelete(snapshot.Path);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] Page(byte value) => Enumerable.Repeat(value, 4096).ToArray();

    private static byte[] Pages(int count, params byte[] values)
    {
        Assert.Equal(count, values.Length);
        return values.SelectMany(Page).ToArray();
    }

    private sealed record WalFrameSpec(uint PageNumber, uint DatabaseSize, byte[] PageData);

    private static class WalFixtureBuilder
    {
        internal static byte[] BuildHeader(uint magic, int pageSize, uint salt1, uint salt2)
        {
            var header = new byte[32];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), magic);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 3_007_000);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), (uint)pageSize);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), 7);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), salt1);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), salt2);

            var checksum = Checksum(header.AsSpan(0, 24), magic);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24, 4), checksum.s0);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28, 4), checksum.s1);
            return header;
        }

        internal static byte[] BuildWal(
            uint magic,
            int pageSize,
            uint salt1,
            uint salt2,
            params WalFrameSpec[] frames)
        {
            var header = BuildHeader(magic, pageSize, salt1, salt2);
            var result = new byte[header.Length + frames.Length * (24 + pageSize)];
            header.CopyTo(result, 0);
            var s0 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24, 4));
            var s1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28, 4));
            var offset = header.Length;
            foreach (var frame in frames)
            {
                Assert.Equal(pageSize, frame.PageData.Length);
                var frameHeader = result.AsSpan(offset, 24);
                BinaryPrimitives.WriteUInt32BigEndian(frameHeader.Slice(0, 4), frame.PageNumber);
                BinaryPrimitives.WriteUInt32BigEndian(frameHeader.Slice(4, 4), frame.DatabaseSize);
                BinaryPrimitives.WriteUInt32BigEndian(frameHeader.Slice(8, 4), salt1);
                BinaryPrimitives.WriteUInt32BigEndian(frameHeader.Slice(12, 4), salt2);
                frame.PageData.CopyTo(result, offset + 24);
                (s0, s1) = Checksum(frameHeader[..8], magic, s0, s1);
                (s0, s1) = Checksum(frame.PageData, magic, s0, s1);
                BinaryPrimitives.WriteUInt32BigEndian(frameHeader.Slice(16, 4), s0);
                BinaryPrimitives.WriteUInt32BigEndian(frameHeader.Slice(20, 4), s1);
                offset += 24 + pageSize;
            }

            return result;
        }

        private static (uint s0, uint s1) Checksum(
            ReadOnlySpan<byte> bytes,
            uint magic,
            uint s0 = 0,
            uint s1 = 0)
        {
            var bigEndianWords = magic == 0x377f0683u;
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
    }
}
