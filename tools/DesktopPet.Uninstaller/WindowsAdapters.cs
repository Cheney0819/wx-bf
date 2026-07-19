using System.Diagnostics;
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
