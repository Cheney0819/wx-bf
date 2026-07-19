using System.Diagnostics;

namespace DesktopPet.Uninstaller.Core;

public sealed record ProcessSnapshot(int Pid, int? ParentPid, string ExecutablePath);

public interface IProcessCatalog
{
    IReadOnlyList<ProcessSnapshot> List();

    bool TryKill(int pid, bool entireTree);

    bool IsRunning(int pid);
}

public sealed class ProcessShutdownService(IProcessCatalog catalog)
{
    public IReadOnlyList<ProcessSnapshot> StopWithin(string installDirectory, TimeSpan timeout)
    {
        var targets = catalog.List()
            .Where(process => InstallPathPolicy.IsWithin(installDirectory, process.ExecutablePath))
            .ToArray();

        foreach (var process in targets)
        {
            catalog.TryKill(process.Pid, entireTree: true);
        }

        var stopwatch = Stopwatch.StartNew();
        while (targets.Any(process => catalog.IsRunning(process.Pid)) && stopwatch.Elapsed < timeout)
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(50, remaining.TotalMilliseconds)));
        }

        return targets.Where(process => catalog.IsRunning(process.Pid)).ToArray();
    }
}
