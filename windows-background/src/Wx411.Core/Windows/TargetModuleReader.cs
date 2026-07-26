using System.Runtime.InteropServices;
using static Wx411.Core.Windows.NativeMethods;

namespace Wx411.Core.Windows;

public sealed record ModuleInfo(
    string Name,
    string FullPath,
    IntPtr BaseAddress,
    uint Size,
    uint Pid);

public static class TargetModuleReader
{
    public static ModuleInfo? FindModuleInProcess(uint pid, string moduleName)
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, pid);
        if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1))
            return null;

        try
        {
            ModuleEntry32 entry = default;
            entry.dwSize = (uint)Marshal.SizeOf<ModuleEntry32>();

            if (!Module32First(snapshot, ref entry))
                return null;

            do
            {
                if (entry.szModule.Equals(moduleName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new ModuleInfo(
                        entry.szModule,
                        entry.szExePath,
                        entry.modBaseAddr,
                        entry.modBaseSize,
                        entry.th32ProcessID);
                }
            } while (Module32Next(snapshot, ref entry));

            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public static string? ResolveDllPath(uint pid, string moduleName)
    {
        var info = FindModuleInProcess(pid, moduleName);
        return info?.FullPath;
    }

    public static IntPtr? ResolveBaseAddress(uint pid, string moduleName)
    {
        var info = FindModuleInProcess(pid, moduleName);
        return info?.BaseAddress;
    }
}
