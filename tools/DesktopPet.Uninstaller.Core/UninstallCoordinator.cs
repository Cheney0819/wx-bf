namespace DesktopPet.Uninstaller.Core;

public interface IUninstallOperations
{
    bool DirectoryExists(string path);

    void DeleteDirectory(string path);

    IEnumerable<string> FindUninstallers(string installDirectory);

    int RunUninstaller(string executablePath, string arguments, string workingDirectory);

    bool HasAppIdRegistration();
}

public sealed class UninstallCoordinator(
    ProcessShutdownService processShutdown,
    ShortcutCleanupService shortcutCleanup,
    IShortcutStore shortcutStore,
    IUninstallOperations operations)
{
    private const string InnoArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

    public OperationResult Run(InstallationCandidate installation, TimeSpan processTimeout)
    {
        try
        {
            var remainingProcesses = processShutdown.StopWithin(installation.InstallDirectory, processTimeout);
            if (remainingProcesses.Count != 0)
            {
                return OperationResult.Failure("Target processes are still running.");
            }

            var beforeUninstall = shortcutCleanup.RemoveTargetShortcuts(installation.InstallDirectory);
            if (!beforeUninstall.Succeeded)
            {
                return beforeUninstall;
            }

            var uninstallResult = installation.Kind switch
            {
                InstallKind.InnoSetup => RunInnoUninstaller(installation.InstallDirectory),
                InstallKind.Direct => DeleteInstallationDirectory(installation.InstallDirectory),
                _ => OperationResult.Failure("Unknown installation kind.")
            };
            if (!uninstallResult.Succeeded)
            {
                return uninstallResult;
            }

            var afterUninstall = shortcutCleanup.RemoveTargetShortcuts(installation.InstallDirectory);
            if (!afterUninstall.Succeeded)
            {
                return afterUninstall;
            }

            return HasNoResidue(installation.InstallDirectory)
                ? OperationResult.Success("Installation artifacts removed.")
                : OperationResult.Failure("Installation artifacts remain after uninstall.");
        }
        catch (Exception exception)
        {
            return OperationResult.Failure($"Uninstall failed: {exception.Message}");
        }
    }

    public static UninstallCoordinator CreateForTests(bool directoryExistsAfterDelete)
    {
        var shortcuts = new EmptyShortcutStore();
        var processes = new EmptyProcessCatalog();
        return new UninstallCoordinator(
            new ProcessShutdownService(processes),
            new ShortcutCleanupService(shortcuts),
            shortcuts,
            new TestOperations(directoryExistsAfterDelete));
    }

    private OperationResult RunInnoUninstaller(string installDirectory)
    {
        var uninstaller = operations.FindUninstallers(installDirectory)
            .Where(path => InstallPathPolicy.IsWithin(installDirectory, path))
            .Where(path => Path.GetFileName(path).StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
                           Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (uninstaller is null)
        {
            return OperationResult.Failure("No Inno Setup uninstaller was found in the installation directory.");
        }

        return operations.RunUninstaller(uninstaller, InnoArguments, installDirectory) == 0
            ? OperationResult.Success("Inno Setup uninstaller completed.")
            : OperationResult.Failure("Inno Setup uninstaller returned a non-zero exit code.");
    }

    private OperationResult DeleteInstallationDirectory(string installDirectory)
    {
        if (operations.DirectoryExists(installDirectory))
        {
            operations.DeleteDirectory(installDirectory);
        }

        return OperationResult.Success("Installation directory deletion completed.");
    }

    private bool HasNoResidue(string installDirectory) =>
        !operations.DirectoryExists(installDirectory) &&
        !shortcutStore.List().Any(shortcut => InstallPathPolicy.IsWithin(installDirectory, shortcut.TargetPath)) &&
        !operations.HasAppIdRegistration();

    private sealed class EmptyShortcutStore : IShortcutStore
    {
        public IEnumerable<ShortcutEntry> List() => [];
        public void Delete(string shortcutPath) { }
    }

    private sealed class EmptyProcessCatalog : IProcessCatalog
    {
        public IReadOnlyList<ProcessSnapshot> List() => [];
        public bool TryKill(int pid, bool entireTree) => true;
        public bool IsRunning(int pid) => false;
    }

    private sealed class TestOperations(bool directoryExistsAfterDelete) : IUninstallOperations
    {
        public bool DirectoryExists(string path) => directoryExistsAfterDelete;
        public void DeleteDirectory(string path) { }
        public IEnumerable<string> FindUninstallers(string installDirectory) => [];
        public int RunUninstaller(string executablePath, string arguments, string workingDirectory) => 0;
        public bool HasAppIdRegistration() => false;
    }
}
