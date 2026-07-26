namespace Wx411.Core.Tests;

internal static class TestSourceTree
{
    internal static string FindWindowsEasyRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("WX411_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var configuredCandidate = Path.Combine(configuredRoot, "windows-easy");
            if (File.Exists(Path.Combine(configuredCandidate, "Wx411Easy.sln")))
                return configuredCandidate;
        }

        var workingTreeCandidate = Path.Combine(
            Directory.GetCurrentDirectory(),
            "windows-easy");
        if (File.Exists(Path.Combine(workingTreeCandidate, "Wx411Easy.sln")))
            return workingTreeCandidate;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wx411Easy.sln")))
                return directory.FullName;

            var nested = Path.Combine(directory.FullName, "windows-easy");
            if (File.Exists(Path.Combine(nested, "Wx411Easy.sln")))
                return nested;
        }

        throw new DirectoryNotFoundException("Could not locate windows-easy source root.");
    }

    internal static string ReadWindowsEasy(string relativePath) =>
        File.ReadAllText(Path.Combine(FindWindowsEasyRoot(), relativePath));

    internal static string ReadRecoveryRoot(string relativePath) =>
        File.ReadAllText(Path.Combine(Directory.GetParent(FindWindowsEasyRoot())!.FullName, relativePath));
}
