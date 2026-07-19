using DesktopPet.Uninstaller.Core;
using Xunit;

namespace DesktopPet.Uninstaller.Tests;

public sealed class UninstallCoordinatorTests
{
    [Fact]
    public void Run_fails_when_install_directory_remains()
    {
        var result = UninstallCoordinator.CreateForTests(directoryExistsAfterDelete: true)
            .Run(new(@"C:\Pet", InstallKind.Direct, null), TimeSpan.FromSeconds(1));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Run_invokes_only_in_directory_inno_uninstaller_with_silent_arguments()
    {
        var operations = new FakeOperations
        {
            Uninstallers = [@"C:\Pet\unins000.exe", @"C:\Other\unins001.exe"]
        };
        var coordinator = CreateCoordinator(operations);

        var result = coordinator.Run(new(@"C:\Pet", InstallKind.InnoSetup, null), TimeSpan.Zero);

        Assert.True(result.Succeeded);
        Assert.Equal((@"C:\Pet\unins000.exe", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", @"C:\Pet"),
            Assert.Single(operations.Runs));
    }

    [Fact]
    public void Run_fails_when_target_shortcut_remains_after_cleanup()
    {
        var shortcuts = new FakeShortcutStore([new(@"C:\Users\alice\Desktop\桌宠.lnk", @"C:\Pet\DesktopPet.Wpf.exe")])
        {
            IgnoreDeletes = true
        };
        var result = CreateCoordinator(new FakeOperations(), shortcuts)
            .Run(new(@"C:\Pet", InstallKind.Direct, null), TimeSpan.Zero);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Run_fails_when_inno_uninstaller_returns_nonzero_exit_code()
    {
        var operations = new FakeOperations
        {
            Uninstallers = [@"C:\Pet\unins000.exe"],
            UninstallerExitCode = 1
        };

        var result = CreateCoordinator(operations)
            .Run(new(@"C:\Pet", InstallKind.InnoSetup, null), TimeSpan.Zero);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Run_fails_when_appid_registration_remains()
    {
        var result = CreateCoordinator(new FakeOperations { AppIdRegistrationExists = true })
            .Run(new(@"C:\Pet", InstallKind.Direct, null), TimeSpan.Zero);

        Assert.False(result.Succeeded);
    }

    private static UninstallCoordinator CreateCoordinator(FakeOperations operations, FakeShortcutStore? shortcuts = null)
    {
        shortcuts ??= new FakeShortcutStore([]);
        return new UninstallCoordinator(
            new ProcessShutdownService(new FakeProcessCatalog()),
            new ShortcutCleanupService(shortcuts),
            shortcuts,
            operations);
    }

    private sealed class FakeProcessCatalog : IProcessCatalog
    {
        public IReadOnlyList<ProcessSnapshot> List() => [];
        public bool TryKill(int pid, bool entireTree) => true;
        public bool IsRunning(int pid) => false;
    }

    private sealed class FakeShortcutStore(IEnumerable<ShortcutEntry> entries) : IShortcutStore
    {
        private readonly List<ShortcutEntry> entries = [.. entries];
        public bool IgnoreDeletes { get; init; }
        public IEnumerable<ShortcutEntry> List() => entries;
        public void Delete(string shortcutPath)
        {
            if (!IgnoreDeletes)
            {
                entries.RemoveAll(entry => entry.ShortcutPath == shortcutPath);
            }
        }
    }

    private sealed class FakeOperations : IUninstallOperations
    {
        public IEnumerable<string> Uninstallers { get; init; } = [];
        public List<(string Executable, string Arguments, string WorkingDirectory)> Runs { get; } = [];
        public int UninstallerExitCode { get; init; }
        public bool AppIdRegistrationExists { get; init; }
        public bool DirectoryExists(string path) => false;
        public void DeleteDirectory(string path) { }
        public IEnumerable<string> FindUninstallers(string installDirectory) => Uninstallers;
        public int RunUninstaller(string executablePath, string arguments, string workingDirectory)
        {
            Runs.Add((executablePath, arguments, workingDirectory));
            return UninstallerExitCode;
        }

        public bool HasAppIdRegistration() => AppIdRegistrationExists;
    }
}
