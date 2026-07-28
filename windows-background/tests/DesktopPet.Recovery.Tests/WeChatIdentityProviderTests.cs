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

    [Fact]
    public void BindingDataRootPreservesProcessAndExecutableIdentity()
    {
        Directory.CreateDirectory(_root);
        var executable = typeof(WeChatIdentityProviderTests).Assembly.Location;
        var provider = new WeChatIdentityProvider();
        var process = new WeChatRuntimeIdentity(
            42,
            7,
            executable,
            "fixture-executable");

        var bound = provider.BindDataRoot(process, _root);

        Assert.Equal(42, bound.ProcessId);
        Assert.Equal(7, bound.SessionId);
        Assert.Equal(executable, bound.ExecutablePath);
        Assert.Equal("fixture-executable", bound.ExecutableIdentity);
        Assert.Equal("fixture-executable", bound.EpochIdentity!.ExecutableVersion);
        Assert.Equal(
            Path.GetFullPath(_root),
            bound.DataRoot,
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
    }

    [Fact]
    public void MultipleInteractiveProcessesAreAmbiguousInsteadOfChoosingLowestPid()
    {
        var executable = typeof(WeChatIdentityProviderTests).Assembly.Location;
        var processes = new[]
        {
            new WeChatProcessCandidate(41, 7, executable),
            new WeChatProcessCandidate(42, 7, executable),
        };

        var exception = Assert.Throws<AmbiguousWeChatProcessException>(() =>
            WeChatIdentityProvider.SelectInteractiveProcess(processes));

        Assert.Equal("ambiguous_wechat_process", exception.Code);
    }

    [Fact]
    public void UniqueWindowedProcessIsSelectedAmongHelperProcesses()
    {
        var executable = typeof(WeChatIdentityProviderTests).Assembly.Location;
        var expected = new WeChatProcessCandidate(42, 7, executable, HasMainWindow: true);
        var processes = new[]
        {
            new WeChatProcessCandidate(41, 7, executable, HasMainWindow: false),
            expected,
            new WeChatProcessCandidate(43, 7, executable, HasMainWindow: false),
        };

        var selected = WeChatIdentityProvider.SelectInteractiveProcess(processes);

        Assert.Equal(expected, selected);
    }

    [Fact]
    public void UniqueProcessTreeRootIsSelectedWhenNoProcessHasAMainWindow()
    {
        var executable = typeof(WeChatIdentityProviderTests).Assembly.Location;
        const int mainPid = 7301;
        var expected = new WeChatProcessCandidate(
            mainPid,
            7,
            executable,
            ParentProcessId: 611);
        var processes = new[]
        {
            new WeChatProcessCandidate(7302, 7, executable, ParentProcessId: mainPid),
            expected,
            new WeChatProcessCandidate(7303, 7, executable, ParentProcessId: mainPid),
            new WeChatProcessCandidate(7304, 7, executable, ParentProcessId: mainPid),
            new WeChatProcessCandidate(7305, 7, executable, ParentProcessId: mainPid),
        };

        var selected = WeChatIdentityProvider.SelectInteractiveProcess(processes);

        Assert.Equal(expected, selected);
    }

    [Fact]
    public void OneInteractiveProcessRemainsSelectable()
    {
        var executable = typeof(WeChatIdentityProviderTests).Assembly.Location;
        var expected = new WeChatProcessCandidate(42, 7, executable);

        var selected = WeChatIdentityProvider.SelectInteractiveProcess([expected]);

        Assert.Equal(expected, selected);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

