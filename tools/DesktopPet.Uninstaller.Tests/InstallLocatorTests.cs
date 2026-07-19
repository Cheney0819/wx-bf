using DesktopPet.Uninstaller.Core;
using Xunit;

namespace DesktopPet.Uninstaller.Tests;

public sealed class InstallLocatorTests
{
    [Fact]
    public void Locate_prefers_verified_command_line_directory()
    {
        var locator = new InstallLocator(new FakeInstallationStore([]), path => path == @"D:\Pet");

        Assert.Equal(@"D:\Pet", Assert.Single(locator.Locate(@"D:\Pet")).InstallDirectory);
    }

    [Fact]
    public void Locate_uses_verified_inno_candidate_before_legacy_directory()
    {
        var locator = new InstallLocator(
            new FakeInstallationStore(
                [new(@"C:\InnoPet", InstallKind.InnoSetup, "unins000.exe")],
                [@"C:\LegacyPet"]),
            path => path is @"C:\InnoPet" or @"C:\LegacyPet");

        var result = Assert.Single(locator.Locate(null));

        Assert.Equal(@"C:\InnoPet", result.InstallDirectory);
        Assert.Equal(InstallKind.InnoSetup, result.Kind);
    }

    [Fact]
    public void Locate_preserves_inno_metadata_for_command_line_directory()
    {
        var locator = new InstallLocator(
            new FakeInstallationStore([new(@"D:\Pet", InstallKind.InnoSetup, "unins000.exe")]),
            path => path == @"D:\Pet");

        var result = Assert.Single(locator.Locate(@"D:\Pet"));

        Assert.Equal(InstallKind.InnoSetup, result.Kind);
        Assert.Equal("unins000.exe", result.UninstallCommand);
    }

    [Fact]
    public void Locate_skips_unverified_candidates()
    {
        var locator = new InstallLocator(
            new FakeInstallationStore(
                [new(@"C:\Missing", InstallKind.InnoSetup, "unins000.exe")],
                [@"C:\LegacyPet"]),
            path => path == @"C:\LegacyPet");

        var result = Assert.Single(locator.Locate(null));

        Assert.Equal(new InstallationCandidate(@"C:\LegacyPet", InstallKind.Direct, null), result);
    }

    private sealed class FakeInstallationStore(
        IEnumerable<InstallationCandidate> innoCandidates,
        IEnumerable<string>? legacyDirectories = null) : IInstallationStore
    {
        public IEnumerable<InstallationCandidate> ReadInnoCandidates() => innoCandidates;

        public IEnumerable<string> ReadLegacyDirectories() => legacyDirectories ?? [];
    }
}
