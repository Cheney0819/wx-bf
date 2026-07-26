using DesktopPet.Uninstaller;
using DesktopPet.Uninstaller.Core;
using Xunit;

namespace DesktopPet.Uninstaller.Tests;

public sealed class UninstallerSelfRelocatorTests
{
    [Fact]
    public void Bootstrap_handoff_exit_code_is_distinct_and_nonzero()
    {
        Assert.Equal(2, UninstallerSelfRelocator.BootstrapHandoffExitCode);
    }

    [Fact]
    public void CreatePlan_moves_the_executable_to_a_unique_temporary_directory_and_preserves_arguments()
    {
        var plan = UninstallerSelfRelocator.CreatePlan(
            ["--install-dir", @"C:\Pet"],
            @"C:\Pet\一键卸载.exe",
            @"C:\Temp",
            [@"C:\Pet"],
            bootstrapProcessId: 123);

        Assert.NotNull(plan);
        Assert.Equal(@"C:\Pet\一键卸载.exe", plan.SourcePath);
        Assert.True(InstallPathPolicy.IsWithin(@"C:\Temp", plan.DestinationPath));
        Assert.False(InstallPathPolicy.IsWithin(@"C:\Pet", plan.DestinationPath));
        Assert.Equal(
        [
            "--install-dir",
            @"C:\Pet",
            UninstallerSelfRelocator.RelocatedMarker,
            UninstallerSelfRelocator.BootstrapProcessOption,
            "123"
        ], plan.Arguments);
    }

    [Fact]
    public void CreatePlan_does_not_relaunch_an_already_relocated_process()
    {
        var plan = UninstallerSelfRelocator.CreatePlan(
            [UninstallerSelfRelocator.RelocatedMarker],
            @"C:\Temp\DesktopPet.Uninstaller\一键卸载.exe",
            @"C:\Temp",
            [@"C:\Pet"],
            bootstrapProcessId: 123);

        Assert.Null(plan);
    }

    [Fact]
    public void CreatePlan_does_not_trust_the_marker_when_executable_is_still_in_target()
    {
        var plan = UninstallerSelfRelocator.CreatePlan(
            [UninstallerSelfRelocator.RelocatedMarker],
            @"C:\Pet\一键卸载.exe",
            @"C:\Temp",
            [@"C:\Pet"],
            bootstrapProcessId: 123);

        Assert.NotNull(plan);
    }

    [Fact]
    public void CreatePlan_keeps_an_executable_that_is_outside_every_target_directory()
    {
        var plan = UninstallerSelfRelocator.CreatePlan(
            ["--install-dir", @"C:\Pet"],
            @"D:\Downloads\一键卸载.exe",
            @"C:\Temp",
            [@"C:\Pet"],
            bootstrapProcessId: 123);

        Assert.Null(plan);
    }

    [Fact]
    public void ReadBootstrapProcessId_returns_the_relocation_parent_pid()
    {
        Assert.Equal(123, UninstallerSelfRelocator.ReadBootstrapProcessId(
            [UninstallerSelfRelocator.BootstrapProcessOption, "123"]));
        Assert.Null(UninstallerSelfRelocator.ReadBootstrapProcessId([]));
    }
}
