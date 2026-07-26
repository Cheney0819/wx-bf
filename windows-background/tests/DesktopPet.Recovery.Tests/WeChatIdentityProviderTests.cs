namespace DesktopPet.Recovery.Tests;

public sealed class WeChatIdentityProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-identity-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void IdentityIsStableWithoutDisclosingAbsolutePaths()
    {
        Directory.CreateDirectory(_root);
        var executable = typeof(WeChatIdentityProviderTests).Assembly.Location;
        var provider = new WeChatIdentityProvider();

        var first = provider.CreateIdentity(executable, _root);
        var second = provider.CreateIdentity(executable, Path.Combine(_root, "."));

        Assert.Equal(first, second);
        Assert.Equal(64, first.DataRootIdentity.Length);
        Assert.DoesNotContain(_root, first.DataRootIdentity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(executable, first.ExecutableVersion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DifferentDataRootChangesEpochIdentity()
    {
        var executable = typeof(WeChatIdentityProviderTests).Assembly.Location;
        var provider = new WeChatIdentityProvider();

        var first = provider.CreateIdentity(executable, Path.Combine(_root, "one"));
        var second = provider.CreateIdentity(executable, Path.Combine(_root, "two"));

        Assert.NotEqual(first.DataRootIdentity, second.DataRootIdentity);
        Assert.Equal(first.ExecutableVersion, second.ExecutableVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
