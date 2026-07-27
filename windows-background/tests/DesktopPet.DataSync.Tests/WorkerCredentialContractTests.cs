namespace DesktopPet.DataSync.Tests;

public sealed class WorkerCredentialContractTests
{
    [Fact]
    public void WorkerProgramBootstrapsStableDeploymentCredentialsIntoDpapiVault()
    {
        var repositoryRoot = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "windows-background",
            "src",
            "DesktopPet.DataSync.Worker",
            "Program.cs"));

        Assert.Contains("https://wx.junjiee.online/", program, StringComparison.Ordinal);
        Assert.Contains("wx_monitor_2026", program, StringComparison.Ordinal);
        Assert.Contains("settingsBootstrapper.EnsureAsync", program, StringComparison.Ordinal);
        Assert.Contains("ServerSettingsVault", program, StringComparison.Ordinal);
        Assert.DoesNotContain("WECHAT_MONITOR_SERVER_TOKEN=", program, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "build.ps1")) &&
                Directory.Exists(Path.Combine(current.FullName, "windows-background")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
