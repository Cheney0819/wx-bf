using DesktopPet.Recovery;

namespace DesktopPet.Recovery.Tests;

public sealed class WeChatDataRootLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-data-root-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SelectsMostRecentlyWrittenAccountWithoutFixedDatabaseCount()
    {
        var container = Path.Combine(_root, "Documents", "xwechat_files");
        var accountWithEighteen = CreateAccount(container, "account-a", 18);
        var accountWithSix = CreateAccount(container, "account-b", 6);
        SetDatabaseTimes(accountWithEighteen, DateTime.UtcNow.AddMinutes(-10));
        SetDatabaseTimes(accountWithSix, DateTime.UtcNow);

        var locator = new WeChatDataRootLocator([_root], []);

        var result = await locator.LocateAsync(default);

        Assert.True(result.Found);
        Assert.Equal(Path.GetFullPath(accountWithSix), result.DataRoot);
        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(6, result.DatabaseCount);
        Assert.Equal("data_root_discovered", result.Code);
    }

    [Theory]
    [InlineData("xwechat_files")]
    [InlineData("Weixin Files")]
    [InlineData("WeChat Files")]
    public async Task SupportsEveryKnownContainerName(string containerName)
    {
        var account = CreateAccount(
            Path.Combine(_root, containerName),
            "account",
            3);
        var locator = new WeChatDataRootLocator([_root], []);

        var result = await locator.LocateAsync(default);

        Assert.Equal(Path.GetFullPath(account), result.DataRoot);
        Assert.Equal(3, result.DatabaseCount);
    }

    [Fact]
    public async Task MissingCandidateReturnsBoundedResult()
    {
        Directory.CreateDirectory(_root);
        var locator = new WeChatDataRootLocator([_root], []);

        var result = await locator.LocateAsync(default);

        Assert.False(result.Found);
        Assert.Null(result.DataRoot);
        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(0, result.DatabaseCount);
        Assert.Equal("data_root_missing", result.Code);
    }

    [Fact]
    public async Task NewerSessionOnlyDirectoryDoesNotReplaceCompleteAccount()
    {
        var container = Path.Combine(_root, "xwechat_files");
        var complete = CreateAccount(container, "complete", 3);
        var incomplete = CreateAccount(container, "incomplete", 1);
        SetDatabaseTimes(complete, DateTime.UtcNow.AddMinutes(-5));
        SetDatabaseTimes(incomplete, DateTime.UtcNow);
        var locator = new WeChatDataRootLocator([_root], []);

        var result = await locator.LocateAsync(default);

        Assert.Equal(Path.GetFullPath(complete), result.DataRoot);
        Assert.Equal(3, result.DatabaseCount);
    }

    [Fact]
    public async Task FiniteDriveSearchFindsNestedCloudAccount()
    {
        var account = CreateAccount(
            Path.Combine(_root, "cloud-user", "Documents", "Weixin Files"),
            "account",
            4);
        var locator = new WeChatDataRootLocator([], [_root]);

        var result = await locator.LocateAsync(default);

        Assert.Equal(Path.GetFullPath(account), result.DataRoot);
        Assert.Equal(4, result.DatabaseCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string CreateAccount(string container, string accountName, int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var account = Path.Combine(container, accountName);
        var session = Path.Combine(account, "db_storage", "session");
        var message = Path.Combine(account, "db_storage", "message");
        Directory.CreateDirectory(session);
        Directory.CreateDirectory(message);
        WriteEncryptedCandidate(Path.Combine(session, "session.db"));
        for (var index = 1; index < count; index++)
            WriteEncryptedCandidate(Path.Combine(message, $"message_{index - 1}.db"));
        return account;
    }

    private static void WriteEncryptedCandidate(string path)
    {
        var bytes = new byte[4096];
        bytes[0] = 0x7f;
        File.WriteAllBytes(path, bytes);
    }

    private static void SetDatabaseTimes(string account, DateTime value)
    {
        foreach (var path in Directory.EnumerateFiles(
                     account,
                     "*.db",
                     SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(path, value);
        }
    }
}
