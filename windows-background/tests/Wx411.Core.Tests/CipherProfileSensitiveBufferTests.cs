namespace Wx411.Core.Tests;

public sealed class CipherProfileSensitiveBufferTests
{
    [Fact]
    public void ValidationCacheSnapshotsProfilesBeforeCopyingSalt()
    {
        var source = File.ReadAllText(FindCipherProfileProbeSource());
        var start = RequiredIndex(source, "public CipherProfileValidationCache(");
        var end = RequiredIndex(source, "public int Count", start);
        var constructor = source[start..end];

        var profileSnapshot = RequiredIndex(constructor, "_profiles = Array.AsReadOnly");
        var saltCopy = RequiredIndex(constructor, "_salt = salt.ToArray()");

        Assert.True(
            profileSnapshot < saltCopy,
            "The non-sensitive profile snapshot must complete before the constructor creates its salt copy.");
    }

    private static string FindCipherProfileProbeSource()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("WX411_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var configuredCandidate = Path.Combine(
                configuredRoot,
                "windows-easy",
                "src",
                "Wx411.Core",
                "CipherProfileProbe.cs");
            if (File.Exists(configuredCandidate)) return configuredCandidate;
        }

        var workingTreeCandidate = Path.Combine(
            Directory.GetCurrentDirectory(),
            "windows-easy",
            "src",
            "Wx411.Core",
            "CipherProfileProbe.cs");
        if (File.Exists(workingTreeCandidate)) return workingTreeCandidate;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Wx411.Core", "CipherProfileProbe.cs");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Could not locate src/Wx411.Core/CipherProfileProbe.cs from the test output.");
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Expected source marker was not found: {value}");
        return index;
    }
}
