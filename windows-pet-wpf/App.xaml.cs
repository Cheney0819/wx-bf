using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DesktopPet.Wpf;

public partial class App : System.Windows.Application
{
    private Mutex? _monitorMutex;
    private Mutex? _watchdogMutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!EnsureElevatedOrExit(e.Args))
        {
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--watchdog", StringComparison.OrdinalIgnoreCase)))
        {
            StartWatchdogProcess();
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--monitor-only", StringComparison.OrdinalIgnoreCase)))
        {
            StartHiddenMonitorProcess();
            return;
        }

        EnsureWatchdogBackgroundProcess();

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _monitorMutex?.Dispose();
        _watchdogMutex?.Dispose();
        base.OnExit(e);
    }

    private bool EnsureElevatedOrExit(string[] args)
    {
        if (IsProcessElevated())
        {
            return true;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            System.Windows.MessageBox.Show(
                "未完成授权，桌宠未启动。",
                "桌宠",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information
            );
            Shutdown();
            return false;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = BuildArgumentString(args),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });

            if (process is null)
            {
                System.Windows.MessageBox.Show(
                    "未完成授权，桌宠未启动。",
                    "桌宠",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information
                );
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            System.Windows.MessageBox.Show(
                "未完成授权，桌宠未启动。",
                "桌宠",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information
            );
        }
        catch
        {
            System.Windows.MessageBox.Show(
                "未完成授权，桌宠未启动。",
                "桌宠",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information
            );
        }

        Shutdown();
        return false;
    }

    private void StartWatchdogProcess()
    {
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        _watchdogMutex = new Mutex(initiallyOwned: true, "Local\\DesktopPetWeChatWatchdog", out var isFirstWatchdog);
        if (!isFirstWatchdog)
        {
            Shutdown();
            return;
        }

        _ = Task.Run(WatchMonitorLoopAsync);
    }

    private void StartHiddenMonitorProcess()
    {
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        _monitorMutex = new Mutex(initiallyOwned: true, "Local\\DesktopPetWeChatMonitor", out var isFirstMonitor);
        if (!isFirstMonitor)
        {
            Shutdown();
            return;
        }

        global::WeChatMonitor.Start();
    }

    private static void EnsureWatchdogBackgroundProcess()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--watchdog",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch
        {
        }
    }

    private static async Task WatchMonitorLoopAsync()
    {
        while (true)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                continue;
            }

            if (IsMonitorRunning())
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                continue;
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--monitor-only",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                });

                if (process != null)
                    await process.WaitForExitAsync();
            }
            catch
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private static bool IsMonitorRunning()
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, "Local\\DesktopPetWeChatMonitor");
            if (!mutex.WaitOne(0))
                return true;

            mutex.ReleaseMutex();
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string BuildArgumentString(string[] args)
    {
        return string.Join(
            " ",
            args.Select(static arg =>
            {
                if (string.IsNullOrWhiteSpace(arg))
                {
                    return "\"\"";
                }

                return arg.Contains(' ') || arg.Contains('"')
                    ? $"\"{arg.Replace("\"", "\\\"")}\""
                    : arg;
            })
        );
    }
}
