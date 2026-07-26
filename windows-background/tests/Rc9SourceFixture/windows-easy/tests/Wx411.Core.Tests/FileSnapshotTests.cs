using Wx411.Core;

namespace Wx411.Core.Tests;

public sealed class FileSnapshotTests
{
    [Fact]
    public void MatchesFileChecksBytesBeyondFirstPage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wx411-snapshot-{Guid.NewGuid():N}.bin");
        var snapshot = Enumerable.Range(0, 3 * 4096)
            .Select(index => (byte)(index * 17 + 9))
            .ToArray();
        File.WriteAllBytes(path, snapshot);

        try
        {
            Assert.True(FileSnapshot.MatchesFile(path, snapshot));

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.Position = 2 * 4096 + 37;
                stream.WriteByte((byte)(snapshot[2 * 4096 + 37] ^ 0x80));
                stream.Flush(flushToDisk: true);
            }

            Assert.False(FileSnapshot.MatchesFile(path, snapshot));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MatchesFileRejectsLengthChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wx411-snapshot-{Guid.NewGuid():N}.bin");
        var snapshot = Enumerable.Range(0, 8192).Select(index => (byte)index).ToArray();
        File.WriteAllBytes(path, snapshot);

        try
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.WriteByte(0xA5);
            stream.Flush(flushToDisk: true);

            Assert.False(FileSnapshot.MatchesFile(path, snapshot));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
