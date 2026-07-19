using DesktopPet.Uninstaller.Core;
using System.IO;
using System.Windows;

namespace DesktopPet.Uninstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        var shortcuts = new WindowsShortcutStore();
        var coordinator = new UninstallCoordinator(
            new ProcessShutdownService(new WindowsProcessCatalog()),
            new ShortcutCleanupService(shortcuts),
            shortcuts,
            new WindowsUninstallOperations());
        var host = new UninstallerApplicationHost(
            new InstallationLocatorAdapter(new InstallLocator(new WindowsInstallationStore(), Directory.Exists)),
            new UninstallCoordinatorAdapter(coordinator),
            window.SelectInstallationAsync);

        _ = window.RunUninstallAsync(host, ReadInstallDirectory(e.Args));
    }

    private static string? ReadInstallDirectory(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--install-dir", StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private sealed class InstallationLocatorAdapter(InstallLocator locator) : IUninstallerInstallationLocator
    {
        public IReadOnlyList<InstallationCandidate> Locate(string? commandLineDirectory) =>
            locator.Locate(commandLineDirectory);
    }

    private sealed class UninstallCoordinatorAdapter(UninstallCoordinator coordinator) : IUninstallerCoordinator
    {
        public OperationResult Run(InstallationCandidate installation, TimeSpan processTimeout) =>
            coordinator.Run(installation, processTimeout);
    }
}
