using Wx411.Core;

namespace Wx411.Core.Tests;

public sealed class SqliteIntegrityCheckerTests
{
    private static readonly byte[] RawKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

    [Fact]
    public void AcceptsProcessedRealFixture()
    {
        var plaintext = ProcessFixture();
        WithTemporaryFile(plaintext, path => SqliteIntegrityChecker.VerifyFile(path));
    }

    [Fact]
    public void RejectsDamagedBtreePage()
    {
        var plaintext = ProcessFixture();
        plaintext[4096] = 0x7F;

        WithTemporaryFile(plaintext, path =>
            Assert.Throws<IntegrityException>(() => SqliteIntegrityChecker.VerifyFile(path)));
    }

    [Fact]
    public void CancellationStopsBeforeOpeningFile()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SqliteIntegrityChecker.VerifyFile("MISSING.sqlite", source.Token));
    }

    private static byte[] ProcessFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sqlcipher4_raw_key.db");
        return SqlCipher4.DecryptDatabase(File.ReadAllBytes(path), RawKey);
    }

    private static void WithTemporaryFile(byte[] bytes, Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wx411-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fixture.sqlite");
        try
        {
            File.WriteAllBytes(path, bytes);
            action(path);
        }
        finally
        {
            Array.Clear(bytes);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
