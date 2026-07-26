using Wx411.Core;

namespace Wx411.Core.Tests;

public sealed class StableDatabaseSnapshotTests
{
    [Fact]
    public void ReadReturnsExactStableFileContents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wx411-stable-snapshot-{Guid.NewGuid():N}.db");
        var expected = Enumerable.Range(0, 3 * 4096)
            .Select(index => (byte)(index * 31 + 7))
            .ToArray();
        File.WriteAllBytes(path, expected);

        byte[]? actual = null;
        try
        {
            actual = StableDatabaseSnapshot.Read(path);
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (actual is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(actual);
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadHonorsCancellationBeforeOpeningFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            StableDatabaseSnapshot.Read("missing.db", cancellation.Token));
    }

    [Fact]
    public void SourceZerosEveryRejectedReadBuffer()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "StableDatabaseSnapshot.cs"));

        var snapshotRead = RequiredIndex(source, "var bytes = ReadAllBytesCancellable");
        var postReadStamp = RequiredIndex(source, "var after = CaptureFileStamp", snapshotRead);
        var cleanupScope = TokenIndex(source, "try", snapshotRead);

        Assert.True(
            cleanupScope > snapshotRead && cleanupScope < postReadStamp,
            "Each snapshot must enter a try/finally cleanup scope before post-read checks.");
        var finallyBlock = RequiredTokenIndex(source, "finally", cleanupScope);
        Assert.True(
            RequiredIndex(source, "CryptographicOperations.ZeroMemory(bytes)", finallyBlock) > finallyBlock,
            "Every rejected snapshot buffer must be zeroed.");
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected source marker was not found: {value}");
        return index;
    }

    private static int RequiredTokenIndex(string source, string token, int startIndex = 0)
    {
        var index = TokenIndex(source, token, startIndex);
        Assert.True(index >= 0, $"Expected source token was not found: {token}");
        return index;
    }

    private static int TokenIndex(string source, string token, int startIndex = 0)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            source[startIndex..],
            $@"\b{System.Text.RegularExpressions.Regex.Escape(token)}\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? startIndex + match.Index : -1;
    }
}
