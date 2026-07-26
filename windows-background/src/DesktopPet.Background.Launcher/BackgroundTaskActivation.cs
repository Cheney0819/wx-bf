using System.Diagnostics;

namespace DesktopPet.Background.Launcher;

public static class BackgroundTaskNames
{
    public const string Recovery = "JunjieeDesktopPet-Recovery";
    public const string DataSync = "JunjieeDesktopPet-DataSync";

    public static bool IsKnown(string taskName)
        => string.Equals(taskName, Recovery, StringComparison.Ordinal) ||
           string.Equals(taskName, DataSync, StringComparison.Ordinal);
}

public sealed record ScheduledTaskRunResult(int ExitCode, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IScheduledTaskRunner
{
    Task<ScheduledTaskRunResult> RunAsync(
        string taskName,
        CancellationToken cancellationToken);
}

public sealed record BackgroundTaskActivationResult(
    string TaskName,
    bool Succeeded,
    int ExitCode,
    string Error);

public sealed class ScheduledTaskLauncher(IScheduledTaskRunner runner)
{
    public async Task<IReadOnlyList<BackgroundTaskActivationResult>> ActivateAllAsync(
        CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(
            ActivateAsync(BackgroundTaskNames.Recovery, cancellationToken),
            ActivateAsync(BackgroundTaskNames.DataSync, cancellationToken));
        return results;
    }

    private async Task<BackgroundTaskActivationResult> ActivateAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(taskName, cancellationToken);
            return new(taskName, result.Succeeded, result.ExitCode, Bound(result.StandardError));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(taskName, false, -1, Bound(exception.Message));
        }
    }

    private static string Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return normalized.Length <= 2048 ? normalized : normalized[..2048];
    }
}

public sealed class SchtasksRunner : IScheduledTaskRunner
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    public async Task<ScheduledTaskRunResult> RunAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(-1, "schtasks.exe is only available on Windows.");
        }

        if (!BackgroundTaskNames.IsKnown(taskName))
        {
            throw new ArgumentException("Unknown background task.", nameof(taskName));
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("/Run");
        process.StartInfo.ArgumentList.Add("/TN");
        process.StartInfo.ArgumentList.Add(taskName);

        try
        {
            if (!process.Start())
            {
                return new(-1, "schtasks.exe did not start.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CommandTimeout);
            await process.WaitForExitAsync(timeoutCts.Token);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            return new(process.ExitCode, Bound(error));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new(-1, "schtasks.exe timed out.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception exception)
        {
            TryKill(process);
            return new(-1, Bound(exception.Message));
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The command is already best-effort; the caller records the failure.
        }
    }

    private static string Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return normalized.Length <= 2048 ? normalized : normalized[..2048];
    }
}
