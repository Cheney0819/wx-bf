using Wx411.Core;

namespace Wx411.Core.Tests;

public sealed class SqliteSidecarCleanerTests
{
    [Fact]
    public void DeletesOnlyExactTemporaryDatabaseSidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wx411-sidecars-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var temporaryPath = Path.Combine(root, ".message.readable.sqlite.random");
            var deleted = new[]
            {
                temporaryPath + "-wal",
                temporaryPath + "-shm",
                temporaryPath + ".tmp-wal",
                temporaryPath + ".tmp-shm",
            };
            var preserved = new[]
            {
                temporaryPath,
                temporaryPath + "-wal.backup",
                temporaryPath + ".other-wal",
                Path.Combine(root, ".message.readable.sqlite.other-wal"),
                Path.Combine(root, "message.readable.sqlite"),
            };

            foreach (var path in deleted.Concat(preserved))
                File.WriteAllBytes(path, [1, 2, 3]);

            SqliteSidecarCleaner.DeleteForTemporaryDatabase(temporaryPath);

            Assert.All(deleted, path => Assert.False(File.Exists(path), path));
            Assert.All(preserved, path => Assert.True(File.Exists(path), path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingSidecarsAreIgnored()
    {
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"wx411-sidecars-missing-{Guid.NewGuid():N}");

        SqliteSidecarCleaner.DeleteForTemporaryDatabase(temporaryPath);
    }
}
