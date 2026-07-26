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

        var result = coordinator.Run(new(@"C:\Pet", InstallKind.InnoSetup, @"""C:\Pet\unins000.exe"""), TimeSpan.Zero);

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
            UninstallerExitCode = 1,
            DirectoryExistsAfterDelete = true
        };

        var result = CreateCoordinator(operations)
            .Run(new(@"C:\Pet", InstallKind.InnoSetup, @"C:\Pet\unins000.exe"), TimeSpan.Zero);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Messages, message => message.Contains(@"C:\Pet", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_rejects_uninstall_command_outside_target_even_when_matching_file_exists()
    {
        var operations = new FakeOperations
        {
            Uninstallers = [@"C:\Pet\unins000.exe"],
            UninstallerExitCode = 0
        };

        var result = CreateCoordinator(operations)
            .Run(new(@"C:\Pet", InstallKind.InnoSetup, @"C:\Other\unins000.exe"), TimeSpan.Zero);

        Assert.False(result.Succeeded);
        Assert.Empty(operations.Runs);
    }

    [Fact]
    public void Run_rejects_noncanonical_target_before_any_delete_or_launch()
    {
        var operations = new FakeOperations { DirectoryExistsAfterDelete = true };

        var result = CreateCoordinator(operations)
            .Run(new(@"C:\Pet.", InstallKind.Direct, null), TimeSpan.Zero);

        Assert.False(result.Succeeded);
        Assert.Equal(0, operations.DeleteCalls);
        Assert.Empty(operations.Runs);
    }

    [Fact]
    public void Run_fails_when_appid_registration_remains()
    {
        var result = CreateCoordinator(new FakeOperations { AppIdRegistrationExists = true })
            .Run(new(@"C:\Pet", InstallKind.Direct, null), TimeSpan.Zero);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Run_ignores_same_appid_registration_for_another_install_directory()
    {
        var operations = new FakeOperations
        {
            AppIdRegistrations = [@"HKCU\...\{APPID}_is1"],
            RegistrationInstallDirectory = @"C:\OtherPet"
        };

        var result = CreateCoordinator(operations)
            .Run(new(@"C:\Pet", InstallKind.Direct, null), TimeSpan.Zero);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Run_lists_each_exact_remaining_artifact()
    {
        var installDirectory = @"C:\Pet";
        var shortcut = new ShortcutEntry(
            @"C:\Users\alice\Desktop\桌宠.lnk",
            @"C:\Pet\DesktopPet.Wpf.exe");
        var registration = @"HKCU\Registry64\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8D5C4C3A-9F3E-4BA3-A8F1-35D3C86A7C11}_is1";
        var shortcuts = new FakeShortcutStore([shortcut]) { IgnoreDeletes = true };
        var operations = new FakeOperations
        {
            DirectoryExistsAfterDelete = true,
            AppIdRegistrations = [registration]
        };

        var result = CreateCoordinator(operations, shortcuts)
            .Run(new(installDirectory, InstallKind.Direct, null), TimeSpan.Zero);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Messages, message => message.Contains(installDirectory, StringComparison.Ordinal));
        Assert.Contains(result.Messages, message => message.Contains(shortcut.ShortcutPath, StringComparison.Ordinal));
        Assert.Contains(result.Messages, message => message.Contains(shortcut.TargetPath, StringComparison.Ordinal));
        Assert.Contains(result.Messages, message => message.Contains(registration, StringComparison.Ordinal));
    }

    [Fact]
    public void Run_fails_when_shortcut_enumeration_is_unreadable()
    {
        var result = CreateCoordinator(new FakeOperations(), new ThrowingShortcutStore())
            .Run(new(@"C:\Pet", InstallKind.Direct, null), TimeSpan.Zero);

        Assert.False(result.Succeeded);
    }

    private static UninstallCoordinator CreateCoordinator(FakeOperations operations, IShortcutStore? shortcuts = null)
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

    private sealed class ThrowingShortcutStore : IShortcutStore
    {
        public IEnumerable<ShortcutEntry> List() => throw new UnauthorizedAccessException("Programs directory is inaccessible.");
        public void Delete(string shortcutPath) { }
    }

    private sealed class FakeOperations : IUninstallOperations
    {
        public IEnumerable<string> Uninstallers { get; init; } = [];
        public List<(string Executable, string Arguments, string WorkingDirectory)> Runs { get; } = [];
        public int UninstallerExitCode { get; init; }
        public bool AppIdRegistrationExists { get; init; }
        public bool DirectoryExistsAfterDelete { get; init; }
        public IReadOnlyList<string> AppIdRegistrations { get; init; } = [];
        public string? RegistrationInstallDirectory { get; init; }
        public int DeleteCalls { get; private set; }
        public bool DirectoryExists(string path) => DirectoryExistsAfterDelete;
        public void DeleteDirectory(string path) => DeleteCalls++;
        public IEnumerable<string> FindUninstallers(string installDirectory) => Uninstallers;
        public int RunUninstaller(string executablePath, string arguments, string workingDirectory)
        {
            Runs.Add((executablePath, arguments, workingDirectory));
            return UninstallerExitCode;
        }

        public bool HasAppIdRegistration() => AppIdRegistrationExists;
        public IEnumerable<string> FindAppIdRegistrations() => AppIdRegistrations;
        public bool HasAppIdRegistration(string installDirectory) =>
            AppIdRegistrationExists || FindAppIdRegistrations(installDirectory).Any();
        public IEnumerable<string> FindAppIdRegistrations(string installDirectory) =>
            RegistrationInstallDirectory is null ||
            RegistrationInstallDirectory.Equals(installDirectory, StringComparison.OrdinalIgnoreCase)
                ? AppIdRegistrations
                : [];
    }
}
