using Wx411.Core;

namespace Wx411.Core.Tests;

public sealed class DatabaseSourceDiscoveryTests
{
    [Theory]
    [InlineData("db_storage/message/message_0.db", true)]
    [InlineData("message/message_42.db", true)]
    [InlineData("db_storage/session/session.db", true)]
    [InlineData("session/session.db", true)]
    [InlineData("db_storage/contact/contact.db", false)]
    [InlineData("db_storage/biz_message/message_0.db", false)]
    [InlineData("db_storage/message/message_backup.db", false)]
    public void SourceClassifiesRequiredAndAuxiliaryDatabases(
        string path,
        bool expectedRequired)
    {
        var source = new DatabaseSource(path, 4096);

        Assert.Equal(expectedRequired, source.IsRequired);
    }

    [Fact]
    public void DiscoverFiltersPlaintextAndInvalidFilesAndOrdersKnownDatabases()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wx411-source-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            WriteEncryptedDatabase(Path.Combine(root, "other.db"));
            WriteEncryptedDatabase(Path.Combine(root, "contact.db"));
            WriteEncryptedDatabase(Path.Combine(root, "message_1.db"));
            WriteEncryptedDatabase(Path.Combine(root, "session.db"));
            File.WriteAllBytes(Path.Combine(root, "invalid.db"), new byte[123]);

            var plaintext = new byte[8192];
            "SQLite format 3\0"u8.CopyTo(plaintext);
            File.WriteAllBytes(Path.Combine(root, "plaintext.db"), plaintext);

            var result = DatabaseSourceDiscovery.Discover([root]);

            Assert.Equal(
                ["message_1.db", "session.db", "contact.db", "other.db"],
                result.Select(item => Path.GetFileName(item.Path)));
            Assert.All(result, item => Assert.Equal(8192, item.Length));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiscoverHonorsCancellationBeforeWalkingRoots()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            DatabaseSourceDiscovery.Discover([Path.GetTempPath()], cancellation.Token));
    }

    [Fact]
    public void SourceClearsEveryDatabaseHeader()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "DatabaseSourceDiscovery.cs"));

        var allocation = RequiredIndex(source, "var header = new byte[16]");
        var read = RequiredIndex(source, "stream.Read(header", allocation);
        var cleanupScope = TokenIndex(source, "try", allocation);
        var finallyBlock = RequiredTokenIndex(source, "finally", cleanupScope);

        Assert.True(cleanupScope > allocation && cleanupScope < read);
        Assert.True(
            RequiredIndex(source, "CryptographicOperations.ZeroMemory(header)", finallyBlock) > finallyBlock);
    }

    private static void WriteEncryptedDatabase(string path)
    {
        var bytes = Enumerable.Range(0, 8192)
            .Select(index => (byte)(index * 13 + 5))
            .ToArray();
        File.WriteAllBytes(path, bytes);
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
