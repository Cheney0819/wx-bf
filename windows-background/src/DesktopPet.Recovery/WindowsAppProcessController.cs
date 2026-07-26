using System.ComponentModel;
using System.Diagnostics;

namespace DesktopPet.Recovery;

internal sealed record AppProcessSnapshot(
    int ProcessId,
    int SessionId,
    string ExecutablePath,
    DateTimeOffset StartedAtUtc);

internal interface IWindowsAppProcessOperations
{
    IReadOnlyList<AppProcessSnapshot> SnapshotInteractiveTargets();

    Task TerminateTreeAsync(
        AppProcessSnapshot process,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    AppProcessIdentity Start(string executablePath);
}

public sealed class WindowsAppProcessController : IAppProcessController
{
    private static readonly TimeSpan DefaultExitTimeout = TimeSpan.FromSeconds(15);
    private readonly IWindowsAppProcessOperations _operations;
    private readonly TimeSpan _exitTimeout;

    public WindowsAppProcessController()
        : this(new WindowsAppProcessOperations(), DefaultExitTimeout)
    {
    }

    internal WindowsAppProcessController(
        IWindowsAppProcessOperations operations,
        TimeSpan exitTimeout)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (exitTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(exitTimeout));
        _operations = operations;
        _exitTimeout = exitTimeout;
    }

    public async Task<AppProcessIdentity> RestartAsync(
        CancellationToken cancellationToken) =>
        await RestartAsync(static _ => Task.CompletedTask, cancellationToken);

    public async Task<AppProcessIdentity> RestartAsync(
        Func<CancellationToken, Task> beforeStart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beforeStart);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = _operations.SnapshotInteractiveTargets()
            .Where(item => IsExpectedExecutable(item.ExecutablePath))
            .ToArray();
        if (snapshots.Length == 0)
            throw new InvalidOperationException(
                "No restartable target process is active in the interactive session.");

        var selectedGroup = snapshots
            .GroupBy(
                item => Path.GetFullPath(item.ExecutablePath),
                StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .First()
            .OrderBy(item => item.ProcessId)
            .ToArray();
        var executablePath = Path.GetFullPath(selectedGroup[0].ExecutablePath);
        foreach (var process in selectedGroup)
        {
            await _operations.TerminateTreeAsync(
                process,
                _exitTimeout,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await beforeStart(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return _operations.Start(executablePath);
    }

    private static bool IsExpectedExecutable(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(
            Path.GetFileName(path),
            "Weixin.exe",
            StringComparison.OrdinalIgnoreCase);
}

internal sealed class WindowsAppProcessOperations : IWindowsAppProcessOperations
{
    public IReadOnlyList<AppProcessSnapshot> SnapshotInteractiveTargets()
    {
        using var current = Process.GetCurrentProcess();
        var currentSession = current.SessionId;
        var snapshots = new List<AppProcessSnapshot>();
        foreach (var process in Process.GetProcessesByName("Weixin"))
        {
            try
            {
                if (process.SessionId != currentSession) continue;
                var executablePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executablePath)) continue;
                snapshots.Add(new AppProcessSnapshot(
                    process.Id,
                    process.SessionId,
                    Path.GetFullPath(executablePath),
                    process.StartTime.ToUniversalTime()));
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // The process may exit or deny module inspection while it is enumerated.
            }
            finally
            {
                process.Dispose();
            }
        }
        return Array.AsReadOnly(snapshots
            .OrderBy(item => item.ProcessId)
            .ToArray());
    }

    public async Task TerminateTreeAsync(
        AppProcessSnapshot snapshot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(snapshot.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }
        using var ownedProcess = process;
        if (process.HasExited) return;
        var actualPath = process.MainModule?.FileName;
        var actualStart = process.StartTime.ToUniversalTime();
        if (string.IsNullOrWhiteSpace(actualPath) ||
            !string.Equals(
                Path.GetFullPath(actualPath),
                Path.GetFullPath(snapshot.ExecutablePath),
                StringComparison.OrdinalIgnoreCase) ||
            actualStart != snapshot.StartedAtUtc.UtcDateTime)
        {
            throw new InvalidOperationException(
                "Target process identity changed before restart.");
        }

        process.Kill(entireProcessTree: true);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            timeoutCancellation.IsCancellationRequested)
        {
            throw new TimeoutException("Target process tree did not exit before the deadline.");
        }
    }

    public AppProcessIdentity Start(string executablePath)
    {
        var normalized = Path.GetFullPath(executablePath);
        if (!File.Exists(normalized) ||
            !string.Equals(
                Path.GetFileName(normalized),
                "Weixin.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Snapshotted target executable is no longer available.");
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = normalized,
            WorkingDirectory = Path.GetDirectoryName(normalized),
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Target process did not start.");
        using (process)
        {
            return new AppProcessIdentity(process.Id, normalized);
        }
    }
}
