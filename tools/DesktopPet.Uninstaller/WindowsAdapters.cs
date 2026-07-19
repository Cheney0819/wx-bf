using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using DesktopPet.Uninstaller.Core;

namespace DesktopPet.Uninstaller;

[SupportedOSPlatform("windows")]
public sealed class WindowsInstallationStore : IInstallationStore
{
    public const string AppId = "{8D5C4C3A-9F3E-4BA3-A8F1-35D3C86A7C11}";
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IEnumerable<InstallationCandidate> ReadInnoCandidates()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var candidate in ReadInnoCandidates(hive, view))
                {
                    yield return candidate;
                }
            }
        }
    }

    public IEnumerable<string> ReadLegacyDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "JunjieeDesktopPet");
        }
    }

    private static IEnumerable<InstallationCandidate> ReadInnoCandidates(RegistryHive hive, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var uninstallKey = baseKey.OpenSubKey(UninstallKeyPath);
        if (uninstallKey is null)
        {
            yield break;
        }

        foreach (var name in uninstallKey.GetSubKeyNames())
        {
            if (!IsMatchingAppId(name))
            {
                continue;
            }

            using var appKey = uninstallKey.OpenSubKey(name);
            var directory = appKey?.GetValue("Inno Setup: App Path") as string;
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            yield return new InstallationCandidate(
                directory,
                InstallKind.InnoSetup,
                appKey?.GetValue("UninstallString") as string);
        }
    }

    private static bool IsMatchingAppId(string keyName) =>
        keyName.Equals(AppId, StringComparison.OrdinalIgnoreCase) ||
        keyName.Equals(AppId + "_is1", StringComparison.OrdinalIgnoreCase);
}

public sealed class WindowsProcessCatalog : IProcessCatalog
{
    private readonly List<string> diagnostics = [];

    public IReadOnlyList<string> Diagnostics => diagnostics;

    public IReadOnlyList<ProcessSnapshot> List()
    {
        var processes = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath))
                    {
                        diagnostics.Add($"Unable to read executable path for PID {process.Id}.");
                        continue;
                    }

                    processes.Add(new ProcessSnapshot(process.Id, null, executablePath));
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                                                   System.ComponentModel.Win32Exception or
                                                   UnauthorizedAccessException or
                                                   NotSupportedException)
                {
                    diagnostics.Add($"Unable to read executable path for PID {process.Id}: {exception.Message}");
                }
            }
        }

        return processes;
    }

    public bool TryKill(int pid, bool entireTree)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireTree);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidOperationException or
                                           System.ComponentModel.Win32Exception or
                                           UnauthorizedAccessException or
                                           NotSupportedException)
        {
            diagnostics.Add($"Unable to stop PID {pid}: {exception.Message}");
            return false;
        }
    }

    public bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed record ShortcutSearchDirectory(string Path, bool SearchSubdirectories);

[SupportedOSPlatform("windows")]
public sealed class WindowsShortcutStore : IShortcutStore
{
    private readonly Func<IEnumerable<ShortcutSearchDirectory>> directories;
    private readonly Func<string, string?> readTarget;

    public WindowsShortcutStore()
        : this(ShortcutDirectories, ReadTarget)
    {
    }

    internal WindowsShortcutStore(
        Func<IEnumerable<ShortcutSearchDirectory>> directories,
        Func<string, string?> readTarget)
    {
        this.directories = directories;
        this.readTarget = readTarget;
    }

    public IEnumerable<ShortcutEntry> List()
    {
        foreach (var directory in directories())
        {
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(
                    directory.Path,
                    "*.lnk",
                    directory.SearchSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Unable to enumerate shortcuts in '{directory.Path}'.", exception);
            }

            foreach (var shortcutPath in shortcuts)
            {
                var targetPath = readTarget(shortcutPath);
                if (!string.IsNullOrWhiteSpace(targetPath))
                {
                    yield return new ShortcutEntry(shortcutPath, targetPath);
                }
            }
        }
    }

    public void Delete(string shortcutPath) => File.Delete(shortcutPath);

    private static IEnumerable<ShortcutSearchDirectory> ShortcutDirectories() =>
    [
        new(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), SearchSubdirectories: false),
        new(Environment.GetFolderPath(Environment.SpecialFolder.Programs), SearchSubdirectories: true),
        new(Environment.GetFolderPath(Environment.SpecialFolder.Startup), SearchSubdirectories: false)
    ];

    private static string? ReadTarget(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                throw new InvalidOperationException("WScript.Shell is unavailable for shortcut target resolution.");
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                throw new InvalidOperationException("Unable to create WScript.Shell for shortcut target resolution.");
            }

            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, [shortcutPath]);
            return shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty,
                null, shortcut, null) as string;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Unable to read target for shortcut '{shortcutPath}'.", exception);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsUninstallOperations : IUninstallOperations
{
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    public IEnumerable<string> FindUninstallers(string installDirectory) =>
        Directory.Exists(installDirectory)
            ? Directory.EnumerateFiles(installDirectory, "unins*.exe", SearchOption.TopDirectoryOnly)
            : [];

    public int RunUninstaller(string executablePath, string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo(executablePath, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        }) ?? throw new InvalidOperationException("Unable to start the Inno Setup uninstaller.");
        process.WaitForExit();
        return process.ExitCode;
    }

    public bool HasAppIdRegistration()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(UninstallKeyPath);
                if (uninstallKey?.GetSubKeyNames().Any(IsMatchingAppId) == true)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsMatchingAppId(string keyName) =>
        keyName.Equals(WindowsInstallationStore.AppId, StringComparison.OrdinalIgnoreCase) ||
        keyName.Equals(WindowsInstallationStore.AppId + "_is1", StringComparison.OrdinalIgnoreCase);
}
