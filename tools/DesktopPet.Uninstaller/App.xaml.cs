using DesktopPet.Uninstaller.Core;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DesktopPet.Uninstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        bool relocatedWorker;
        try
        {
            relocatedWorker = WaitForBootstrapProcess(e.Args);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"准备卸载工具失败：{exception.Message}",
                "桌宠一键卸载",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var installationLocator = new InstallLocator(
            new WindowsInstallationStore(),
            directory => File.Exists(Path.Combine(directory, "DesktopPet.Wpf.exe")));
        var installDirectory = ReadInstallDirectory(e.Args);
        try
        {
            var relocation = UninstallerSelfRelocator.CreatePlan(
                e.Args,
                Environment.ProcessPath ?? string.Empty,
                Path.GetTempPath(),
                installationLocator.Locate(installDirectory).Select(candidate => candidate.InstallDirectory),
                Environment.ProcessId);
            if (relocation is not null)
            {
                RelaunchFromTemporaryDirectory(relocation);
                // This process is only a bootstrap. Its handoff code is
                // intentionally nonzero; the worker owns the final result.
                Shutdown(UninstallerSelfRelocator.BootstrapHandoffExitCode);
                return;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"准备卸载工具失败：{exception.Message}",
                "桌宠一键卸载",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (relocatedWorker)
        {
            window.ShowRelocatedWorkerNotice();
        }

        var shortcuts = new WindowsShortcutStore();
        var coordinator = new UninstallCoordinator(
            new ProcessShutdownService(new WindowsProcessCatalog()),
            new ShortcutCleanupService(shortcuts),
            shortcuts,
            new WindowsUninstallOperations());
        var host = new UninstallerApplicationHost(
            new InstallationLocatorAdapter(installationLocator),
            new UninstallCoordinatorAdapter(coordinator),
            window.SelectInstallationAsync);

        _ = RunAndShutdownAsync(window, host, installDirectory);
    }

    private async Task RunAndShutdownAsync(
        MainWindow window,
        UninstallerApplicationHost host,
        string? installDirectory)
    {
        var windowClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => windowClosed.TrySetResult();
        var exitCode = await window.RunUninstallAsync(host, installDirectory);
        if (window.IsVisible)
        {
            await windowClosed.Task;
        }

        Shutdown(exitCode);
    }

    private static void RelaunchFromTemporaryDirectory(UninstallerSelfRelocationPlan relocation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(relocation.DestinationPath)!);
        File.Copy(relocation.SourcePath, relocation.DestinationPath, overwrite: true);
        var startInfo = new ProcessStartInfo(relocation.DestinationPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(relocation.DestinationPath)!
        };
        foreach (var argument in relocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("未能从临时目录启动卸载工具。");
    }

    private static bool WaitForBootstrapProcess(IReadOnlyList<string> arguments)
    {
        var processId = UninstallerSelfRelocator.ReadBootstrapProcessId(arguments);
        if (processId is null)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException($"等待原始卸载工具退出超时（PID {processId}）。");
            }
        }
        catch (ArgumentException)
        {
            // The bootstrap process already exited before the relocated process started waiting.
        }

        return true;
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

    private sealed class UninstallCoordinatorAdapter(UninstallCoordinator coordinator) : IProgressReportingUninstallerCoordinator
    {
        public OperationResult Run(InstallationCandidate installation, TimeSpan processTimeout) =>
            coordinator.Run(installation, processTimeout);

        public OperationResult Run(
            InstallationCandidate installation,
            TimeSpan processTimeout,
            Action<UninstallCoordinatorStage, string> reportProgress) =>
            coordinator.Run(installation, processTimeout, reportProgress);
    }
}
