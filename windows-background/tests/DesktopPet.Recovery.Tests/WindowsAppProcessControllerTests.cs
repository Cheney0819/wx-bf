namespace DesktopPet.Recovery.Tests;

public sealed class WindowsAppProcessControllerTests
{
    [Fact]
    public async Task RestartUsesSnapshottedExecutableAndTerminatesOnlyItsGroup()
    {
        var operations = new FakeProcessOperations
        {
            Snapshots =
            [
                new AppProcessSnapshot(10, 1, "/opt/one/Weixin.exe", DateTimeOffset.UnixEpoch),
                new AppProcessSnapshot(11, 1, "/opt/one/Weixin.exe", DateTimeOffset.UnixEpoch),
                new AppProcessSnapshot(12, 1, "/opt/two/Weixin.exe", DateTimeOffset.UnixEpoch),
            ],
        };
        var controller = new WindowsAppProcessController(
            operations,
            TimeSpan.FromSeconds(5));

        var identity = await controller.RestartAsync(default);

        Assert.Equal([10, 11], operations.TerminatedProcessIds);
        Assert.Equal("/opt/one/Weixin.exe", operations.StartedExecutable);
        Assert.Equal(99, identity.ProcessId);
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
            StartedExecutable = executablePath;
            return new AppProcessIdentity(99, executablePath);
        }
    }
}
