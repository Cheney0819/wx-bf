namespace Wx411.Core.Tests;

public sealed class Rc9SpecificationContractTests
{
    [Fact]
    public void CaptureServiceUsesRc9ProbeChannelAndExporterPipeline()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "CallpointCaptureRecoveryService.cs"));

        Assert.DoesNotContain("StableDatabaseSnapshot.Read", source, StringComparison.Ordinal);
        Assert.Contains("DatabaseProbeCatalog", source, StringComparison.Ordinal);
        Assert.Contains("CapturedCandidateChannel", source, StringComparison.Ordinal);
        Assert.Contains("ConsistentDatabaseExporter", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseCaptureTargetDoesNotOwnAFullSnapshot()
    {
        var source = TestSourceTree.ReadWindowsEasy(
            Path.Combine("src", "Wx411.Core", "MultiDatabaseCaptureCollector.cs"));

        Assert.DoesNotContain("byte[]? _snapshot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("DatabaseProbeDescriptor", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyScanAndFixedCompatibilityWaitRemainRemoved()
    {
        var root = TestSourceTree.FindWindowsEasyRoot();
        var core = string.Join('\n', Directory.EnumerateFiles(
            Path.Combine(root, "src", "Wx411.Core"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        var easy = string.Join('\n', Directory.EnumerateFiles(
            Path.Combine(root, "src", "Wx411.Easy"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        Assert.DoesNotContain("MemoryKeyScanner", core, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromSeconds(30)", core, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromSeconds(30)", easy, StringComparison.Ordinal);
    }
}
