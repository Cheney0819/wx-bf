using DesktopPet.Uninstaller.Core;
using Xunit;

namespace DesktopPet.Uninstaller.Tests;

public sealed class InstallPathPolicyTests
{
    [Fact]
    public void TryCreate_rejects_root_and_injected_profile_directory()
    {
        Assert.False(InstallPathPolicy.TryCreate(@"C:\", @"C:\Users\alice", out _));
        Assert.False(InstallPathPolicy.TryCreate(@"C:\Users\alice", @"C:\Users\alice", out _));
    }

    [Fact]
    public void IsWithin_rejects_prefix_match()
    {
        Assert.True(InstallPathPolicy.IsWithin(@"C:\Apps\Pet", @"C:\Apps\Pet\ffmpeg.exe"));
        Assert.False(InstallPathPolicy.IsWithin(@"C:\Apps\Pet", @"C:\Apps\PetBackup\ffmpeg.exe"));
    }
}
