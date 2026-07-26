namespace Wx411.Core.Tests;

public sealed class ReleaseContractTests
{
    [Fact]
    public void ProductAndManifestUseV15DevIdentity()
    {
        var project = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Easy", "Wx411.Easy.csproj"));
        var manifest = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Easy", "app.manifest"));
        var mainForm = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Easy", "MainForm.cs"));

        Assert.Contains("<Version>1.5.0-dev</Version>", project, StringComparison.Ordinal);
        Assert.Contains("version=\"1.5.0.0\"", manifest, StringComparison.Ordinal);
        Assert.Contains("private const string DisplayVersion = \"1.5-dev\";", mainForm, StringComparison.Ordinal);
        Assert.Contains("精准定位版 {DisplayVersion}", mainForm, StringComparison.Ordinal);
        Assert.Contains("本地数据一键读取 {DisplayVersion}", mainForm, StringComparison.Ordinal);
        Assert.DoesNotContain("1.4-dev", mainForm, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScriptProducesVersionedDevArtifactWithoutDeletingDist()
    {
        var script = TestSourceTree.ReadWindowsEasy("build-win-x64.ps1");

        Assert.Contains("Wx411Easy-v1.5-dev.exe", script, StringComparison.Ordinal);
        Assert.Contains("Wx411Easy-v1.5-dev.zip", script, StringComparison.Ordinal);
        Assert.Contains("Wx411Easy-v1.4-dev.exe", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item $obsoletePath -Force", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item .\\dist -Recurse -Force", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$expectedDist", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Rc9WindowsInstructionsDescribeOnlyThePreciseCaptureWorkflow()
    {
        var guide = TestSourceTree.ReadWindowsEasy("使用说明.txt");
        var diagnostic = TestSourceTree.ReadWindowsEasy("诊断测试步骤.txt");

        Assert.Contains("1.5 RC9", guide, StringComparison.Ordinal);
        Assert.Contains("定位 key 并解密", guide, StringComparison.Ordinal);
        Assert.Contains("刷新列表", guide, StringComparison.Ordinal);
        Assert.Contains("Gate A", guide, StringComparison.Ordinal);
        Assert.Contains("N/A", guide, StringComparison.Ordinal);
        Assert.Contains("1.5 RC9", diagnostic, StringComparison.Ordinal);
        Assert.Contains("定位 key 并解密", diagnostic, StringComparison.Ordinal);
        Assert.Contains("自动捕获全部", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("一键恢复并生成副本", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("兼容检查 30 秒", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("一键恢复并生成副本", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("兼容检查 30 秒", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("同时设置 8 个观察点", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("8 个观察点同时监听", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("同时设置 8 个观察点", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("8 个观察点会监听", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanSourcePackageUsesCurrentVersion()
    {
        var packageScript = TestSourceTree.ReadRecoveryRoot("package_source.py");
        Assert.Contains("wx411_recover 1.5-dev clean source package", packageScript, StringComparison.Ordinal);
        Assert.Contains("single precise-capture", packageScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("codec-holder", packageScript, StringComparison.OrdinalIgnoreCase);
    }
}
