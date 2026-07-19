using DesktopPet.Uninstaller.Core;
using Xunit;

namespace DesktopPet.Uninstaller.Tests;

public sealed class ProcessShutdownServiceTests
{
    [Fact]
    public void StopWithin_keeps_same_named_process_outside_target()
    {
        var catalog = new FakeProcessCatalog(
        [
            new(10, null, @"C:\Pet\DesktopPet.Wpf.exe"),
            new(11, 10, @"C:\Pet\ffmpeg.exe"),
            new(12, null, @"C:\Other\ffmpeg.exe")
        ]);

        new ProcessShutdownService(catalog).StopWithin(@"C:\Pet", TimeSpan.FromSeconds(1));

        Assert.Equal([10, 11], catalog.KilledPids.Order());
    }

    [Fact]
    public void StopWithin_returns_running_target_processes_after_timeout()
    {
        var catalog = new FakeProcessCatalog([new(10, null, @"C:\Pet\DesktopPet.Wpf.exe")])
        {
            KeepRunning = true
        };

        var remaining = new ProcessShutdownService(catalog).StopWithin(@"C:\Pet", TimeSpan.Zero);

        Assert.Equal([10], remaining.Select(process => process.Pid));
    }

    private sealed class FakeProcessCatalog(IReadOnlyList<ProcessSnapshot> processes) : IProcessCatalog
    {
        private readonly HashSet<int> running = processes.Select(process => process.Pid).ToHashSet();

        public List<int> KilledPids { get; } = [];
        public bool KeepRunning { get; init; }

        public IReadOnlyList<ProcessSnapshot> List() => processes;

        public bool TryKill(int pid, bool entireTree)
        {
            KilledPids.Add(pid);
            if (!KeepRunning)
            {
                running.Remove(pid);
            }

            return true;
        }

        public bool IsRunning(int pid) => running.Contains(pid);
    }
}
