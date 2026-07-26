using System.Diagnostics;
using DesktopPet.Background.Launcher;

namespace DesktopPet.Wpf;

internal static class BackgroundTaskBootstrap
{
    public static void Start()
    {
        _ = ActivateAsync();
    }

    private static async Task ActivateAsync()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JunjieeDesktopPet",
            "Background",
            "Launcher",
            "activation.ndjson");
        var log = new BackgroundActivationLog(logPath);

        try
        {
            var results = await new ScheduledTaskLauncher(new SchtasksRunner())
                .ActivateAllAsync(CancellationToken.None);
            log.Write(results.ToArray(), DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Background task activation failed: {exception.Message}");
        }
    }
}
