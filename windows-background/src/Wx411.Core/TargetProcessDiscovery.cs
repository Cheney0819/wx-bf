using System.Diagnostics;

namespace Wx411.Core;

public sealed record TargetProcessSource(int Pid, string Name);

public static class TargetProcessDiscovery
{
    public static IReadOnlyList<TargetProcessSource> Discover(string processName = "Weixin")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        var processes = new List<TargetProcessSource>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                processes.Add(new TargetProcessSource(process.Id, process.ProcessName + ".exe"));
            }
            catch (InvalidOperationException)
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
}
