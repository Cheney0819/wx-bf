namespace DesktopPet.Recovery.Tests;

public sealed class WindowsAppProcessControllerTests
{
    [Fact]
    public async Task RestartUsesSnapshottedExecutableAndTerminatesOnlyItsGroup()
    {
        var firstExecutable = Path.GetFullPath(
            Path.Combine("opt", "one", "Weixin.exe"));
        var secondExecutable = Path.GetFullPath(
            Path.Combine("opt", "two", "Weixin.exe"));
        var operations = new FakeProcessOperations
        {
            Snapshots =
            [
                new AppProcessSnapshot(10, 1, firstExecutable, DateTimeOffset.UnixEpoch),
                new AppProcessSnapshot(11, 1, firstExecutable, DateTimeOffset.UnixEpoch),
                new AppProcessSnapshot(12, 1, secondExecutable, DateTimeOffset.UnixEpoch),
            ],
        };
        var controller = new WindowsAppProcessController(
            operations,
            TimeSpan.FromSeconds(5));

        var identity = await controller.RestartAsync(default);

        Assert.Equal([10, 11], operations.TerminatedProcessIds);
        Assert.Equal(firstExecutable, operations.StartedExecutable);
        Assert.Equal(99, identity.ProcessId);
    }

    [Fact]
    public async Task RestartInvokesCapturePreparationBeforeStartingTarget()
    {
        var executable = Path.GetFullPath(Path.Combine("opt", "one", "Weixin.exe"));
        var operations = new FakeProcessOperations
        {
            Snapshots =
            [new AppProcessSnapshot(10, 1, executable, DateTimeOffset.UnixEpoch)],
        };
        var controller = new WindowsAppProcessController(
            operations,
            TimeSpan.FromSeconds(5));

        await controller.RestartAsync(
            _ =>
            {
                operations.CapturePreparationStarted = true;
                return Task.CompletedTask;
            },
            default);

        Assert.True(operations.CapturePreparationStarted);
        Assert.True(operations.CapturePreparationStartedBeforeProcessStart);
    }

    [Fact]
    public async Task BoundRuntimeSelectsOnlyItsSessionAndExecutableGroup()
    {
        var boundExecutable = Path.GetFullPath(
            Path.Combine("opt", "bound", "Weixin.exe"));
        var otherExecutable = Path.GetFullPath(
            Path.Combine("opt", "other", "Weixin.exe"));
        var operations = new FakeProcessOperations
        {
            Snapshots =
            [
                new AppProcessSnapshot(10, 7, otherExecutable, DateTimeOffset.UnixEpoch),
                new AppProcessSnapshot(11, 7, otherExecutable, DateTimeOffset.UnixEpoch),
                new AppProcessSnapshot(12, 7, boundExecutable, DateTimeOffset.UnixEpoch),
                new AppProcessSnapshot(13, 7, boundExecutable, DateTimeOffset.UnixEpoch),
                new AppProcessSnapshot(14, 8, boundExecutable, DateTimeOffset.UnixEpoch),
            ],
        };
        var runtime = new WeChatRuntimeIdentity(
            12,
            7,
            boundExecutable,
            "fixture-executable");
        var controller = new WindowsAppProcessController(
            operations,
            TimeSpan.FromSeconds(5),
            runtime);

        await controller.RestartAsync(default);

        Assert.Equal([12, 13], operations.TerminatedProcessIds);
        Assert.Equal(boundExecutable, operations.StartedExecutable);
    }

    [Fact]
    public async Task BoundRuntimeContinuesWhenPidWasReplacedWithinSameIdentity()
    {
        var executable = Path.GetFullPath(
            Path.Combine("opt", "bound", "Weixin.exe"));
        var operations = new FakeProcessOperations
        {
            Snapshots =
            [
                new AppProcessSnapshot(13, 7, executable, DateTimeOffset.UnixEpoch),
                new AppProcessSnapshot(14, 8, executable, DateTimeOffset.UnixEpoch),
            ],
        };
        var runtime = new WeChatRuntimeIdentity(
            12,
            7,
            executable,
            "fixture-executable");
        var controller = new WindowsAppProcessController(
            operations,
            TimeSpan.FromSeconds(5),
            runtime);

        await controller.RestartAsync(default);

        Assert.Equal([13], operations.TerminatedProcessIds);
        Assert.Equal(executable, operations.StartedExecutable);
    }

    [Fact]
    public async Task RestartWithoutInteractiveTargetFailsBeforeStarting()
    {
        var operations = new FakeProcessOperations { Snapshots = [] };
        var controller = new WindowsAppProcessController(
            operations,
            TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.RestartAsync(default));

        Assert.Null(operations.StartedExecutable);
    }

    [Fact]
    public async Task AlreadyExitedSnapshotIsTreatedAsTerminated()
    {
        var operations = new WindowsAppProcessOperations();
        var missing = new AppProcessSnapshot(
            int.MaxValue,
            1,
            "/missing/Weixin.exe",
            DateTimeOffset.UnixEpoch);

        await operations.TerminateTreeAsync(
            missing,
            TimeSpan.FromMilliseconds(50),
            default);
    }

    private sealed class FakeProcessOperations : IWindowsAppProcessOperations
    {
        public IReadOnlyList<AppProcessSnapshot> Snapshots { get; init; } = [];

        public List<int> TerminatedProcessIds { get; } = [];

        public string? StartedExecutable { get; private set; }

        public bool CapturePreparationStarted { get; set; }

        public bool CapturePreparationStartedBeforeProcessStart { get; private set; }

        public IReadOnlyList<AppProcessSnapshot> SnapshotInteractiveTargets() => Snapshots;

        public Task TerminateTreeAsync(
            AppProcessSnapshot process,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminatedProcessIds.Add(process.ProcessId);
            return Task.CompletedTask;
        }

        public AppProcessIdentity Start(string executablePath)
        {
            CapturePreparationStartedBeforeProcessStart = CapturePreparationStarted;
            StartedExecutable = executablePath;
            return new AppProcessIdentity(99, executablePath);
        }
    }
}
