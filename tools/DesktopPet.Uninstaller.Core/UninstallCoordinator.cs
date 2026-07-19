namespace DesktopPet.Uninstaller.Core;

public interface IUninstallOperations
{
    const string DesktopPetAppId = "{8D5C4C3A-9F3E-4BA3-A8F1-35D3C86A7C11}";

    bool DirectoryExists(string path);

    void DeleteDirectory(string path);

    IEnumerable<string> FindUninstallers(string installDirectory);

    int RunUninstaller(string executablePath, string arguments, string workingDirectory);

    bool HasAppIdRegistration();

    IEnumerable<string> FindAppIdRegistrations() =>
        HasAppIdRegistration() ? [DesktopPetAppId] : [];
}

public enum UninstallCoordinatorStage
{
    StopProcesses,
    CleanupShortcuts,
    RemoveFiles
}

public sealed class UninstallCoordinator(
    ProcessShutdownService processShutdown,
    ShortcutCleanupService shortcutCleanup,
    IShortcutStore shortcutStore,
    IUninstallOperations operations)
{
    private const string InnoArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

    public OperationResult Run(InstallationCandidate installation, TimeSpan processTimeout)
        => Run(installation, processTimeout, reportProgress: null);

    public OperationResult Run(
        InstallationCandidate installation,
        TimeSpan processTimeout,
        Action<UninstallCoordinatorStage, string>? reportProgress)
    {
        try
        {
            reportProgress?.Invoke(
                UninstallCoordinatorStage.StopProcesses,
                $"正在退出安装目录中的后台进程：{installation.InstallDirectory}");
            var remainingProcesses = processShutdown.StopWithin(installation.InstallDirectory, processTimeout);
            if (remainingProcesses.Count != 0)
            {
                return FailureWithRemainingArtifacts(
                    installation.InstallDirectory,
                    remainingProcesses.Select(process =>
                        $"Target process remains: PID {process.Pid}, {process.ExecutablePath}"));
            }

            reportProgress?.Invoke(
                UninstallCoordinatorStage.CleanupShortcuts,
                $"正在清理指向安装目录的快捷方式：{installation.InstallDirectory}");
            var beforeUninstall = shortcutCleanup.RemoveTargetShortcuts(installation.InstallDirectory);
            if (!beforeUninstall.Succeeded)
            {
                return FailureWithRemainingArtifacts(installation.InstallDirectory, beforeUninstall.Messages);
            }

            reportProgress?.Invoke(
                UninstallCoordinatorStage.RemoveFiles,
                $"正在删除安装文件：{installation.InstallDirectory}");
            var uninstallResult = installation.Kind switch
            {
                InstallKind.InnoSetup => RunInnoUninstaller(installation.InstallDirectory),
                InstallKind.Direct => DeleteInstallationDirectory(installation.InstallDirectory),
                _ => OperationResult.Failure("Unknown installation kind.")
            };
            if (!uninstallResult.Succeeded)
            {
                return FailureWithRemainingArtifacts(installation.InstallDirectory, uninstallResult.Messages);
            }

            var afterUninstall = shortcutCleanup.RemoveTargetShortcuts(installation.InstallDirectory);
            if (!afterUninstall.Succeeded)
            {
                return FailureWithRemainingArtifacts(installation.InstallDirectory, afterUninstall.Messages);
            }

            var remainingArtifacts = FindRemainingArtifacts(installation.InstallDirectory);
            return remainingArtifacts.Count == 0
                ? OperationResult.Success("Installation artifacts removed.")
                : OperationResult.Failure(remainingArtifacts.ToArray());
        }
        catch (Exception exception)
        {
            return FailureWithRemainingArtifacts(
                installation.InstallDirectory,
                [$"Uninstall failed: {exception.Message}"]);
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
            .Where(IsInnoUninstaller)
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

    private static bool IsInnoUninstaller(string path)
    {
        var fileName = path.Replace('\\', '/').Split('/').Last();
        return fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
               fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<string> FindRemainingArtifacts(string installDirectory)
    {
        var messages = new List<string>();
        if (operations.DirectoryExists(installDirectory))
        {
            messages.Add($"Installation directory remains: {installDirectory}");
        }

        foreach (var shortcut in shortcutStore.List()
                     .Where(shortcut => InstallPathPolicy.IsWithin(installDirectory, shortcut.TargetPath)))
        {
            messages.Add($"Target shortcut remains: {shortcut.ShortcutPath} -> {shortcut.TargetPath}");
        }

        var registrations = operations.FindAppIdRegistrations().ToArray();
        if (registrations.Length == 0 && operations.HasAppIdRegistration())
        {
            registrations = [IUninstallOperations.DesktopPetAppId];
        }

        messages.AddRange(registrations.Select(registration =>
            $"AppId registry entry remains: {registration}"));
        return messages;
    }

    private OperationResult FailureWithRemainingArtifacts(
        string installDirectory,
        IEnumerable<string> failureMessages)
    {
        var messages = failureMessages.ToList();
        try
        {
            messages.AddRange(FindRemainingArtifacts(installDirectory));
        }
        catch (Exception exception)
        {
            messages.Add($"Residue verification failed: {exception.Message}");
        }

        return OperationResult.Failure(messages.Distinct(StringComparer.Ordinal).ToArray());
    }

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
