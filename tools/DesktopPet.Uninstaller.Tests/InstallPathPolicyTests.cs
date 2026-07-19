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

    [Fact]
    public void TryCreate_rejects_consecutive_traversal_resolving_to_injected_profile_directory()
    {
        Assert.False(InstallPathPolicy.TryCreate(@"C:\Users\alice\foo\..\..", @"C:\Users", out _));
    }

    [Fact]
    public void TryCreate_rejects_consecutive_traversal_resolving_to_drive_root()
    {
        Assert.False(InstallPathPolicy.TryCreate(@"C:\foo\..\..", @"C:\Users\alice", out _));
    }

    [Theory]
    [InlineData(@"C:\Users\alice.")]
    [InlineData(@"C:\Users\ALICE~1")]
    [InlineData(@"\\?\C:\Users\alice")]
    [InlineData(@"\\.\C:\")]
    [InlineData(@"\\server\share\Users\alice")]
    public void TryCreate_rejects_windows_namespace_and_aliases(string input)
    {
        Assert.False(InstallPathPolicy.TryCreate(input, @"C:\Users\alice", out _));
    }
}
