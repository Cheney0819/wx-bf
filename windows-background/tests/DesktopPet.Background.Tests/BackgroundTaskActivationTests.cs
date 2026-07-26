using DesktopPet.Background.Launcher;

namespace DesktopPet.Background.Tests;

public sealed class BackgroundTaskActivationTests
{
    [Fact]
    public void ActivationLogWritesTaskNameExitCodeAndError()
    {
        using var directory = new TemporaryDirectory();
        var log = new BackgroundActivationLog(
            Path.Combine(directory.Path, "activation.ndjson"));

        log.Write(
        [
            new BackgroundTaskActivationResult(
                BackgroundTaskNames.Recovery,
                false,
                1,
                "missing"),
        ],
        new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));

        var content = File.ReadAllText(log.Path);
        Assert.Contains("JunjieeDesktopPet-Recovery", content);
        Assert.Contains("\"exitCode\":1", content);
        Assert.Contains("missing", content);
    }

    [Fact]
    public void ActivationLogRetainsRecentEntriesWithinBound()
    {
        using var directory = new TemporaryDirectory();
        var log = new BackgroundActivationLog(
            Path.Combine(directory.Path, "activation.ndjson"));

        for (var index = 0; index < 2000; index++)
        {
            log.Write(
            [
                new BackgroundTaskActivationResult(
                    BackgroundTaskNames.DataSync,
                    false,
                    index,
                    $"error-{index}"),
            ],
            DateTimeOffset.UtcNow);
        }

        var content = File.ReadAllText(log.Path);
        Assert.True(new FileInfo(log.Path).Length <= BackgroundActivationLog.MaxBytes);
        Assert.Contains("error-1999", content);
        Assert.DoesNotContain("error-0", content);
    }

    [Fact]
    public async Task ActivateAllRunsExactlyTheTwoFixedTasks()
    {
        var runner = new RecordingTaskRunner();
        var launcher = new ScheduledTaskLauncher(runner);

        var results = await launcher.ActivateAllAsync(CancellationToken.None);

        Assert.Equal(
            [BackgroundTaskNames.Recovery, BackgroundTaskNames.DataSync],
            runner.TaskNames.OrderBy(static name => name));
        Assert.All(results, result => Assert.True(result.Succeeded));
    }

    [Fact]
    public async Task OneTaskFailureIsReturnedWithoutBlockingTheOther()
    {
        var runner = new RecordingTaskRunner(
            BackgroundTaskNames.Recovery,
            new ScheduledTaskRunResult(1, "missing"));

        var results = await new ScheduledTaskLauncher(runner)
            .ActivateAllAsync(CancellationToken.None);

        var recovery = Assert.Single(
            results,
            result => result.TaskName == BackgroundTaskNames.Recovery);
        var dataSync = Assert.Single(
            results,
            result => result.TaskName == BackgroundTaskNames.DataSync);
        Assert.False(recovery.Succeeded);
        Assert.True(dataSync.Succeeded);
    }

    private sealed class RecordingTaskRunner(
        string? failingTask = null,
        ScheduledTaskRunResult? failure = null) : IScheduledTaskRunner
    {
        private readonly string? _failingTask = failingTask;
        private readonly ScheduledTaskRunResult _failure =
            failure ?? new ScheduledTaskRunResult(0, string.Empty);

        public List<string> TaskNames { get; } = [];

        public Task<ScheduledTaskRunResult> RunAsync(
            string taskName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (TaskNames)
            {
                TaskNames.Add(taskName);
            }

            var result = string.Equals(taskName, _failingTask, StringComparison.Ordinal)
                ? _failure
                : new ScheduledTaskRunResult(0, string.Empty);
            return Task.FromResult(result);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "desktop-pet-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
