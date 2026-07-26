using System.ComponentModel;
using System.Diagnostics;

namespace Wx411.Core;

public sealed record TargetProcessSource(int Pid, string Name);

public static class TargetProcessDiscovery
{
    public static IReadOnlyList<TargetProcessSource> Discover(
        string processName = "Weixin",
        int? sessionId = null,
        string? executablePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        var normalizedExecutable = string.IsNullOrWhiteSpace(executablePath)
            ? null
            : Path.GetFullPath(executablePath);

        var processes = new List<TargetProcessSource>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (sessionId is int expectedSession && process.SessionId != expectedSession)
                    continue;
                if (normalizedExecutable is not null)
                {
                    var actualPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(actualPath) ||
                        !string.Equals(
                            Path.GetFullPath(actualPath),
                            normalizedExecutable,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                processes.Add(new TargetProcessSource(process.Id, process.ProcessName + ".exe"));
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Process exited between enumeration and reading its name.
            }
            finally
            {
                process.Dispose();
            }
        }

        processes.Sort((left, right) => left.Pid.CompareTo(right.Pid));
        return Array.AsReadOnly(processes.ToArray());
    }

    public static bool Matches(
        int pid,
        int? sessionId,
        string? executablePath)
    {
        if (pid <= 0) return false;
        if (sessionId is null && string.IsNullOrWhiteSpace(executablePath)) return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (sessionId is int expectedSession && process.SessionId != expectedSession)
                return false;
            if (string.IsNullOrWhiteSpace(executablePath)) return true;
            var actualPath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actualPath) &&
                string.Equals(
                    Path.GetFullPath(actualPath),
                    Path.GetFullPath(executablePath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or
            Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}
