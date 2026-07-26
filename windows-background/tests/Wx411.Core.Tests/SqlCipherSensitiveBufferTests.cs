namespace Wx411.Core.Tests;

public sealed class SqlCipherSensitiveBufferTests
{
    [Fact]
    public void DecryptDatabaseProtectsSaltMacKeyAndOutputDuringAllocation()
    {
        var source = File.ReadAllText(FindSqlCipherSource());
        var start = RequiredIndex(source, "public static byte[] DecryptDatabase(");
        var end = RequiredIndex(source, "internal static bool VerifyPageWithMacKey(", start);
        var method = source[start..end];

        var saltAllocation = RequiredIndex(method, "encrypted[..16].ToArray()");
        var macKeyAllocation = RequiredIndex(method, "MakeMacKey(rawKey, salt, profile)");
        var outputAllocation = RequiredIndex(method, "new byte[encrypted.Length]");
        var cleanupScope = RequiredTokenIndex(method, "try");

        Assert.True(
            cleanupScope < saltAllocation &&
            cleanupScope < macKeyAllocation &&
            cleanupScope < outputAllocation,
            "Salt, MAC key, and plaintext output allocations must all occur inside the cleanup try/finally.");

        var finallyBlock = RequiredTokenIndex(method, "finally", cleanupScope);
        Assert.True(RequiredIndex(method, "ZeroMemory(output)", finallyBlock) > finallyBlock);
        Assert.True(RequiredIndex(method, "ZeroMemory(macKey)", finallyBlock) > finallyBlock);
        Assert.True(RequiredIndex(method, "ZeroMemory(salt)", finallyBlock) > finallyBlock);
    }

    private static string FindSqlCipherSource()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("WX411_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var configuredCandidate = Path.Combine(
                configuredRoot,
                "windows-easy",
                "src",
                "Wx411.Core",
                "SqlCipher4.cs");
            if (File.Exists(configuredCandidate)) return configuredCandidate;
        }

        var workingTreeCandidate = Path.Combine(
            Directory.GetCurrentDirectory(),
            "windows-easy",
            "src",
            "Wx411.Core",
            "SqlCipher4.cs");
        if (File.Exists(workingTreeCandidate)) return workingTreeCandidate;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Wx411.Core", "SqlCipher4.cs");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not locate src/Wx411.Core/SqlCipher4.cs from the test output.");
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected source marker was not found: {value}");
        return index;
    }

    private static int RequiredTokenIndex(string source, string token, int startIndex = 0)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            source[startIndex..],
            $@"\b{System.Text.RegularExpressions.Regex.Escape(token)}\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Expected source token was not found: {token}");
        return startIndex + match.Index;
    }
}
