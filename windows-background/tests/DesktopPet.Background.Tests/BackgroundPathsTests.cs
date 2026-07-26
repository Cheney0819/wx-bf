using DesktopPet.Background.Infrastructure;

namespace DesktopPet.Background.Tests;

public sealed class BackgroundPathsTests
{
    [Fact]
    public void ForRootKeepsWorkerOwnershipSeparate()
    {
        var paths = BackgroundPaths.ForRoot(Path.Combine(Path.GetTempPath(), "state"));

        Assert.EndsWith(Path.Combine("Recovery", "recovery.db"), paths.RecoveryDatabase);
        Assert.EndsWith(Path.Combine("DataSync", "sync.db"), paths.SyncDatabase);
        Assert.NotEqual(paths.RecoveryDatabase, paths.SyncDatabase);
        Assert.EndsWith(Path.Combine("Handoff", "ready"), paths.HandoffReady);
    }

    [Fact]
    public void ForCurrentUserUsesExpectedApplicationRoot()
    {
        var paths = BackgroundPaths.ForCurrentUser();

        Assert.Contains("JunjieeDesktopPet", paths.Root, StringComparison.Ordinal);
        Assert.Contains("Background", paths.Root, StringComparison.Ordinal);
    }
}
