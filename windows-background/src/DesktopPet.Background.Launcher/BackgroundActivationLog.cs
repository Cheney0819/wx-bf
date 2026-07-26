using System.Text;
using System.Text.Json;

namespace DesktopPet.Background.Launcher;

public sealed class BackgroundActivationLog
{
    public const int MaxBytes = 128 * 1024;
    private const int RetainedBytes = 64 * 1024;
    private readonly object _gate = new();

    public BackgroundActivationLog(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A log path is required.", nameof(path));
        }

        Path = path;
    }

    public string Path { get; }

    public void Write(
        IReadOnlyCollection<BackgroundTaskActivationResult> activations,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(activations);
        var line = JsonSerializer.Serialize(new
        {
            timestamp = timestamp,
            tasks = activations.Select(static activation => new
            {
                taskName = activation.TaskName,
                succeeded = activation.Succeeded,
                exitCode = activation.ExitCode,
                error = activation.Error,
            }),
        }) + Environment.NewLine;

        lock (_gate)
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(Path, line, Encoding.UTF8);
            TrimIfNeeded();
        }
    }

    private void TrimIfNeeded()
    {
        var bytes = File.ReadAllBytes(Path);
        if (bytes.Length <= MaxBytes)
        {
            return;
        }

        var start = Math.Max(0, bytes.Length - RetainedBytes);
        while (start < bytes.Length && bytes[start] != (byte)'\n')
        {
            start++;
        }

        if (start < bytes.Length)
        {
            start++;
        }

        File.WriteAllBytes(Path, bytes[start..]);
    }
}
