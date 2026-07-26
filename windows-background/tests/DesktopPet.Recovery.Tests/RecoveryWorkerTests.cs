using System.Threading.Channels;
using System.Text.Json;
using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;
using DesktopPet.Recovery.Security;
using DesktopPet.Recovery.Worker;

namespace DesktopPet.Recovery.Tests;

public sealed class RecoveryWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-worker-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OnceInitializesAndRunsExactlyOneBoundedCycle()
    {
        var startup = new FakeStartup();
        var cycle = new FakeCycle();
        var worker = CreateWorker(startup, cycle, new FakeHintSource());

        await worker.RunAsync(WorkerRunMode.Once, default);

        Assert.Equal(1, startup.CallCount);
        Assert.Equal(1, cycle.CallCount);
        Assert.Equal([RecoveryCycleTrigger.Startup], cycle.Triggers);
    }

    [Fact]
    public async Task ContinuousModeRemainsAliveAfterCircuitAndRespondsToHint()
    {
        using var cancellation = new CancellationTokenSource();
        var startup = new FakeStartup();
        var cycleCallCount = 0;
        var cycle = new FakeCycle(() =>
        {
            cycleCallCount++;
            if (cycleCallCount == 2) cancellation.Cancel();
            return RecoveryAction.Wait("capture_circuit_open");
        });
        var hints = new FakeHintSource();
        var worker = CreateWorker(startup, cycle, hints);

        var running = worker.RunAsync(WorkerRunMode.Continuous, cancellation.Token);
        await cycle.FirstCall;
        await hints.EmitAsync(new RecoveryHint(RecoveryHintKind.ProcessStarted));
        await running;

        Assert.Equal(2, cycle.CallCount);
        Assert.Equal(1, startup.CallCount);
        Assert.Equal(
            [RecoveryCycleTrigger.Startup, RecoveryCycleTrigger.ProcessStarted],
            cycle.Triggers);
    }

    [Fact]
    public async Task DatabaseHintRequestsKeyReuseOnlyCycle()
    {
        using var cancellation = new CancellationTokenSource();
        var cycle = new FakeCycle(() => RecoveryAction.Wait());
        cycle.AfterCall = count =>
        {
            if (count == 2) cancellation.Cancel();
        };
        var hints = new FakeHintSource();
        var worker = CreateWorker(new FakeStartup(), cycle, hints);

        var running = worker.RunAsync(WorkerRunMode.Continuous, cancellation.Token);
        await cycle.FirstCall;
        await hints.EmitAsync(new RecoveryHint(RecoveryHintKind.DatabaseChanged));
        await running;

        Assert.Equal(
            [RecoveryCycleTrigger.Startup, RecoveryCycleTrigger.DatabaseChanged],
            cycle.Triggers);
    }

    [Fact]
    public async Task CycleFailureCancelsHintSourcesAndPropagatesPromptly()
    {
        var cycle = new FakeCycle
        {
            Exception = new InvalidOperationException("cycle failed"),
        };
        var worker = CreateWorker(new FakeStartup(), cycle, new FakeHintSource());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.RunAsync(WorkerRunMode.Continuous, default)
                .WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void DuplicateSingleInstanceIsRejectedUntilOwnerDisposes()
    {
        var name = $"desktop-pet-worker-{Guid.NewGuid():N}";
        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first));
        using (first)
        {
            Assert.False(SingleInstanceGuard.TryAcquire(name, out var duplicate));
            Assert.Null(duplicate);
        }

        Assert.True(SingleInstanceGuard.TryAcquire(name, out var reacquired));
        reacquired!.Dispose();
    }

    [Fact]
    public async Task StartupRestoresCriticalSnapshotWhenStateDatabaseIsMissing()
    {
        var databasePath = Path.Combine(_root, "state", "recovery.db");
        var snapshot = new CriticalRecoverySnapshotStore(
            Path.Combine(_root, "state", "critical.bin"),
            new XorProtector());
        var critical = new CriticalRecoveryState(
            "epoch-restored",
            new("4.1.0", "root-a"),
            2,
            true,
            RecoveryMode.CaptureCircuitOpen,
            "zero_key",
            DateTimeOffset.UtcNow);
        await snapshot.SaveAsync(critical, default);
        var repository = new RecoveryRepository(
            databasePath,
            TimeProvider.System,
            snapshot);
        var startup = new RecoveryBootstrapper(repository, snapshot);

        await startup.InitializeAsync(default);

        var restored = await repository.GetEpochAsync("epoch-restored", default);
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.RestartCount);
        Assert.True(restored.ActiveRestartSuppressed);
        Assert.Equal(RecoveryMode.CaptureCircuitOpen, restored.Mode);
    }

    [Fact]
    public async Task CorruptStateWithoutCriticalSnapshotNeverResetsRestartBudget()
    {
        var databasePath = Path.Combine(_root, "corrupt", "recovery.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await File.WriteAllTextAsync(databasePath, "not a sqlite database");
        var repository = new RecoveryRepository(databasePath, TimeProvider.System);
        var startup = new RecoveryBootstrapper(repository, new EmptySnapshotStore());

        await Assert.ThrowsAnyAsync<Exception>(() => startup.InitializeAsync(default));

        Assert.True(File.Exists(databasePath));
        Assert.Equal("not a sqlite database", await File.ReadAllTextAsync(databasePath));
    }

    [Fact]
    public async Task ProcessWatcherEmitsOnlyNewlyObservedProcessIds()
    {
        var snapshots = new Queue<IReadOnlyList<int>>(
            [[10], [10], [10, 20], [20]]);
        var watcher = new ProcessStartWatcher(
            () => snapshots.Count > 0 ? snapshots.Dequeue() : [20],
            TimeSpan.FromMilliseconds(5),
            TimeProvider.System);
        var channel = Channel.CreateUnbounded<RecoveryHint>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var running = watcher.RunAsync(channel.Writer, cancellation.Token);

        var first = await channel.Reader.ReadAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Equal(20, first.ProcessId);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task KnownRootWatcherEmitsDatabaseChangeWithoutDriveScan()
    {
        var knownRoot = Path.Combine(_root, "known");
        var outsideRoot = Path.Combine(_root, "outside");
        Directory.CreateDirectory(knownRoot);
        Directory.CreateDirectory(outsideRoot);
        var watcher = new KnownRootDatabaseWatcher([knownRoot]);
        var channel = Channel.CreateUnbounded<RecoveryHint>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var running = watcher.RunAsync(channel.Writer, cancellation.Token);
        await Task.Delay(50, cancellation.Token);

        await File.WriteAllTextAsync(
            Path.Combine(outsideRoot, "ignored.db"),
            "outside",
            cancellation.Token);
        await File.WriteAllTextAsync(
            Path.Combine(knownRoot, "message_0.db"),
            "inside",
            cancellation.Token);

        var hint = await channel.Reader.ReadAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Equal(RecoveryHintKind.DatabaseChanged, hint.Kind);
    }

    [Fact]
    public async Task DiagnoseReadsStateWithoutSecretsOrAbsolutePaths()
    {
        var databasePath = Path.Combine(_root, "diagnose", "recovery.db");
        await using var repository = new RecoveryRepository(
            databasePath,
            TimeProvider.System);
        await repository.InitializeAsync(default);
        var epoch = await repository.BeginOrLoadEpochAsync(
            new("4.1.0", "root-hash"),
            false,
            default);
        Assert.True(await repository.TryConsumeRestartAsync(epoch.Id, default));
        await repository.RecordRuntimeEventAsync(
            "worker_test",
            "{\"stage\":\"test\"}",
            default);

        var json = await RecoveryDiagnosticReader.ReadJsonAsync(
            databasePath,
            default);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("restartCount").GetInt32());
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnoseReturnsOneSanitizedObjectForCorruptState()
    {
        var databasePath = Path.Combine(_root, "diagnose-corrupt", "recovery.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await File.WriteAllTextAsync(databasePath, $"corrupt {_root}");

        var json = await RecoveryDiagnosticReader.ReadJsonAsync(databasePath, default);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("state_unreadable", document.RootElement.GetProperty("mode").GetString());
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, WorkerCommandMode.Continuous)]
    [InlineData("--once", WorkerCommandMode.Once)]
    [InlineData("--diagnose", WorkerCommandMode.Diagnose)]
    public void CommandLineAcceptsOnlyFixedModes(
        string? argument,
        WorkerCommandMode expected)
    {
        var arguments = argument is null ? [] : new[] { argument };

        Assert.Equal(expected, WorkerCommandLine.Parse(arguments));
        Assert.Throws<ArgumentException>(() => WorkerCommandLine.Parse(["--pid", "42"]));
        Assert.Throws<ArgumentException>(() => WorkerCommandLine.Parse(["C:/data"]));
    }

    private static RecoveryWorker CreateWorker(
        IRecoveryStartup startup,
        IRecoveryCycle cycle,
        IRecoveryHintSource hints) =>
        new(
            startup,
            cycle,
            [hints],
            new RecoveryWorkerOptions(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromHours(1)),
            TimeProvider.System);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeStartup : IRecoveryStartup
    {
        public int CallCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCycle : IRecoveryCycle
    {
        private readonly Func<RecoveryAction> _run;
        private readonly TaskCompletionSource _firstCall = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal FakeCycle(Func<RecoveryAction>? run = null) =>
            _run = run ?? (() => RecoveryAction.Wait());

        internal int CallCount { get; private set; }

        internal Exception? Exception { get; init; }

        internal Action<int>? AfterCall { get; set; }

        internal List<RecoveryCycleTrigger> Triggers { get; } = [];

        internal Task FirstCall => _firstCall.Task;

        public Task<RecoveryAction> RunAsync(
            RecoveryCycleTrigger trigger,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Triggers.Add(trigger);
            _firstCall.TrySetResult();
            AfterCall?.Invoke(CallCount);
            if (Exception is not null) throw Exception;
            return Task.FromResult(_run());
        }
    }

    private sealed class FakeHintSource : IRecoveryHintSource
    {
        private ChannelWriter<RecoveryHint>? _writer;
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(
            ChannelWriter<RecoveryHint> writer,
            CancellationToken cancellationToken)
        {
            _writer = writer;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        internal async Task EmitAsync(RecoveryHint hint)
        {
            await _started.Task;
            await _writer!.WriteAsync(hint);
        }
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            Transform(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
            Transform(ciphertext);

        private static byte[] Transform(ReadOnlySpan<byte> value)
        {
            var output = value.ToArray();
            for (var index = 0; index < output.Length; index++) output[index] ^= 0x5A;
            return output;
        }
    }

    private sealed class EmptySnapshotStore : ICriticalRecoverySnapshotStore
    {
        public Task SaveAsync(
            CriticalRecoveryState state,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<CriticalRecoveryState?> LoadAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<CriticalRecoveryState?>(null);
    }
}
