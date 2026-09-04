using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Footprint.Core;

public sealed record WeixinInstallation(string ExecutablePath, string DllPath);

public static class WeixinLocator
{
    private static readonly string[] ExecutableNames = ["Weixin.exe", "WeChat.exe"];

    public static WeixinInstallation Locate()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Weixin discovery requires Windows.");
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && ExecutableNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                    {
                        var found = FromExecutable(path);
                        if (found is not null) return found;
                    }
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            }
        }

        foreach (var candidate in RegistryCandidates().Concat(FileSystemCandidates()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = FromExecutable(candidate);
            if (found is not null) return found;
        }
        throw new FileNotFoundException("未找到微信可执行文件和配套 Weixin.dll。");
    }

    public static WeixinInstallation? FromExecutable(string executablePath)
    {
        if (!File.Exists(executablePath)) return null;
        var root = Path.GetDirectoryName(executablePath)!;
        var dll = Directory.EnumerateFiles(root, "Weixin.dll", SearchOption.AllDirectories).FirstOrDefault();
        return dll is null ? null : new WeixinInstallation(Path.GetFullPath(executablePath), Path.GetFullPath(dll));
    }

    public static async Task<bool> RequestExitAsync(TimeSpan normalExitTimeout, TimeSpan forceExitTimeout,
        CancellationToken cancellationToken)
    {
        foreach (var process in GetRunningProcesses())
        {
            using (process)
            {
                try { _ = process.CloseMainWindow(); }
                catch (InvalidOperationException) { }
            }
        }

        if (await WaitUntilStoppedAsync(normalExitTimeout, cancellationToken)) return true;

        foreach (var process in GetRunningProcesses())
        {
            using (process)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }

        return await WaitUntilStoppedAsync(forceExitTimeout, cancellationToken);
    }

    private static async Task<bool> WaitUntilStoppedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning()) return true;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250),
                cancellationToken);
        }
    }

    public static async Task EnsureRunningAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (IsRunning()) return;
        using var process = Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true })
            ?? throw new InvalidOperationException("微信启动失败。");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRunning()) return;
            await Task.Delay(250, cancellationToken);
        }
        throw new InvalidOperationException("微信在 20 秒内没有进入运行状态。");
    }

    public static bool IsRunning()
    {
        var processes = GetRunningProcesses();
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    private static Process[] GetRunningProcesses()
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (ExecutableNames.Any(name => string.Equals(process.ProcessName + ".exe", name,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    matches.Add(process);
                    continue;
                }
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        return matches.ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryCandidates()
    {
        var roots = new[] { Registry.CurrentUser, Registry.LocalMachine };
        var paths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        foreach (var root in roots)
            foreach (var path in paths)
            {
                using var uninstall = root.OpenSubKey(path);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var item = uninstall.OpenSubKey(name);
                    var display = item?.GetValue("DisplayName") as string;
                    if (display is null || (!display.Contains("Weixin", StringComparison.OrdinalIgnoreCase) &&
                                             !display.Contains("WeChat", StringComparison.OrdinalIgnoreCase))) continue;
                    var location = item?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(location))
                        foreach (var exe in ExecutableNames) yield return Path.Combine(location, exe);
                    var icon = item?.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrWhiteSpace(icon)) yield return icon.Split(',')[0].Trim('"');
                }
            }
    }

    private static IEnumerable<string> FileSystemCandidates()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        }.Where(path => !string.IsNullOrWhiteSpace(path));
        var subdirectories = new[] { "Tencent", "Weixin", "WeChat", Path.Combine("Tencent", "Weixin"), Path.Combine("Tencent", "WeChat") };
        foreach (var root in roots)
            foreach (var subdirectory in subdirectories)
                foreach (var executable in ExecutableNames)
                {
                    var directory = Path.Combine(root, subdirectory);
                    if (!Directory.Exists(directory)) continue;
                    foreach (var path in Directory.EnumerateFiles(directory, executable, SearchOption.AllDirectories)) yield return path;
                }
    }
}
