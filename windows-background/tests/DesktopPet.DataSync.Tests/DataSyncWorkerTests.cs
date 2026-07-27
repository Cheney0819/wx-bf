using System.Text.Json;
using System.Threading.Channels;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;
using DesktopPet.DataSync.Upload;
using DesktopPet.DataSync.Worker;
using Microsoft.Data.Sqlite;

namespace DesktopPet.DataSync.Tests;

public sealed class DataSyncWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-datasync-worker-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OnceInitializesReconcilesDrainsParserAndRunsTwoUploadSlots()
    {
        var runtime = new FakeRuntime(parseResults: [true, true, false]);
        var worker = CreateWorker(runtime);

        await worker.RunAsync(DataSyncRunMode.Once, default);

        Assert.Equal(1, runtime.InitializeCount);
        Assert.Equal(1, runtime.ReconcileCount);
        Assert.Equal(1, runtime.HeartbeatCount);
        Assert.Equal(1, runtime.TelemetryReconcileCount);
        Assert.Equal(3, runtime.ParserCalls);
        Assert.Equal(2, runtime.UploadCalls);
        Assert.True(runtime.Calls.IndexOf("heartbeat") < runtime.Calls.IndexOf("reconcile"));
        Assert.True(runtime.Calls.IndexOf("heartbeat") < runtime.Calls.IndexOf("upload"));
    }

    [Fact]
    public async Task ContinuousHeartbeatRunsOnInjectedCadence()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var runtime = new FakeRuntime(parseResults: [false]);
        runtime.AfterHeartbeat = count => { if (count == 2) cancellation.Cancel(); };
        var worker = CreateWorker(runtime, options: new DataSyncWorkerOptions(
            TimeSpan.Zero,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            2,
            TimeSpan.FromMilliseconds(20)));

        await worker.RunAsync(DataSyncRunMode.Continuous, cancellation.Token);

        Assert.Equal(2, runtime.HeartbeatCount);
    }

    [Fact]
    public async Task HeartbeatFailureDoesNotStopLaterCadenceOrShutdown()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var runtime = new FakeRuntime(parseResults: [false]) { ThrowHeartbeatAt = 2 };
        runtime.AfterHeartbeat = count => { if (count == 3) cancellation.Cancel(); };
        var worker = CreateWorker(runtime, options: new DataSyncWorkerOptions(
            TimeSpan.Zero, TimeSpan.FromHours(1), TimeSpan.FromHours(1), 2,
            TimeSpan.FromMilliseconds(20)));

        await worker.RunAsync(DataSyncRunMode.Continuous, cancellation.Token);

        Assert.Equal(3, runtime.HeartbeatCount);
    }

    [Fact]
    public async Task ContinuousModeDoesNotDependOnWpfAndRespondsToReadyHint()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new FakeRuntime(parseResults: [false, false]);
        runtime.AfterReconcile = count =>
        {
            if (count == 2) cancellation.Cancel();
        };
        var hints = new FakeHintSource();
        var worker = CreateWorker(runtime, hints);

        var running = worker.RunAsync(DataSyncRunMode.Continuous, cancellation.Token);
        await runtime.FirstReconcile;
        await hints.EmitAsync(new DataSyncHint(DataSyncHintKind.HandoffReady));
        await running;

        Assert.Equal(2, runtime.ReconcileCount);
    }

    [Fact]
    public async Task ReadyHintsWithinDebounceWindowCoalesceIntoOneReconciliation()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new FakeRuntime(parseResults: [false, false]);
        runtime.AfterReconcile = count =>
        {
            if (count == 2) cancellation.Cancel();
        };
        var hints = new FakeHintSource();
        var worker = CreateWorker(
            runtime,
            hints,
            new DataSyncWorkerOptions(
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromHours(1),
                TimeSpan.FromHours(1),
                2));

        var running = worker.RunAsync(DataSyncRunMode.Continuous, cancellation.Token);
        await runtime.FirstReconcile;
        await hints.EmitAsync(new DataSyncHint(DataSyncHintKind.HandoffReady));
        await hints.EmitAsync(new DataSyncHint(DataSyncHintKind.HandoffReady));
        await hints.EmitAsync(new DataSyncHint(DataSyncHintKind.HandoffReady));
        await running;

        Assert.Equal(2, runtime.ReconcileCount);
    }

    [Fact]
    public async Task ParserConcurrencyIsOneAndUploadConcurrencyIsExactlyTwo()
    {
        var runtime = new FakeRuntime(parseResults: [true, true, false])
        {
            DelayOperations = true,
        };

        await CreateWorker(runtime).RunAsync(DataSyncRunMode.Once, default);

        Assert.Equal(1, runtime.MaximumParserConcurrency);
        Assert.Equal(2, runtime.MaximumUploadConcurrency);
    }

    [Fact]
    public void DefaultIntervalsRemainFixed()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), DataSyncWorkerOptions.Default.DebounceInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), DataSyncWorkerOptions.Default.ReconciliationInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), DataSyncWorkerOptions.Default.UploadPollInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), DataSyncWorkerOptions.Default.HeartbeatInterval);
        Assert.Equal(2, DataSyncWorkerOptions.Default.UploadConcurrency);
    }

    [Theory]
    [InlineData("messages", true)]
    [InlineData("contacts", true)]
    [InlineData("favorites", true)]
    [InlineData("events", false)]
    [InlineData("status", false)]
    public void UploadOutcomesEmitOnlyForBusinessEndpoints(string endpoint, bool expected)
    {
        var result = new UploadResult(UploadDisposition.Acknowledged, "row-1", 200, endpoint);
        Assert.Equal(expected, DataSyncRuntime.ShouldEmitUploadOutcome(result));
    }

    [Theory]
    [InlineData("datasync_heartbeat_failed")]
    [InlineData("upload_failed")]
    [InlineData("datasync_credential_missing")]
    public async Task TelemetryFailureDiagnosticsStayLocalAndNeverCreateOutbox(string eventType)
    {
        var databasePath = Path.Combine(_root, $"{eventType}.db");
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        await using var repository = new DataSyncRepository(databasePath, TimeProvider.System, protector);
        await repository.InitializeAsync(default);

        await DataSyncRuntime.RecordLocalDiagnosticAsync(
            repository, eventType, "bounded_failure", default);

        Assert.Equal(0, await repository.CountOutboxAsync(default));
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM runtime_event WHERE event_type = $type;";
        command.Parameters.AddWithValue("$type", eventType);
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ReadyWatcherEmitsOnlyJsonCreatedBelowReadyRoot()
    {
        var ready = Path.Combine(_root, "ready");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(ready);
        Directory.CreateDirectory(outside);
        var watcher = new HandoffReadyWatcher(ready);
        var channel = Channel.CreateUnbounded<DataSyncHint>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var running = watcher.RunAsync(channel.Writer, cancellation.Token);
        await Task.Delay(50, cancellation.Token);

        await File.WriteAllTextAsync(Path.Combine(outside, "ignored.json"), "{}", cancellation.Token);
        await File.WriteAllTextAsync(Path.Combine(ready, "ignored.tmp"), "{}", cancellation.Token);
        await File.WriteAllTextAsync(Path.Combine(ready, "ready.json"), "{}", cancellation.Token);

        var hint = await channel.Reader.ReadAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(DataSyncHintKind.HandoffReady, hint.Kind);
    }

    [Fact]
    public void DuplicateSingleInstanceReturnsFixedExitCodeTen()
    {
        var name = $"desktop-pet-datasync-{Guid.NewGuid():N}";
        Assert.Equal(10, Program.DuplicateInstanceExitCode);
        Assert.True(SingleInstanceGuard.TryAcquire(name, out var first));
        using (first)
        {
            Assert.False(SingleInstanceGuard.TryAcquire(name, out var duplicate));
            Assert.Null(duplicate);
        }
    }

    [Fact]
    public async Task DiagnoseReportsCountsWithoutPayloadTokenOrAbsolutePaths()
    {
        var databasePath = Path.Combine(_root, "diagnose", "sync.db");
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        await using var repository = new DataSyncRepository(
            databasePath,
            TimeProvider.System,
            protector);
        await repository.InitializeAsync(default);
        await repository.EnqueueOutboxAsync(
            new OutboxDraft(
                "outbox-1",
                "messages:1",
                "messages",
                "{\"token\":\"secret-token\",\"messages\":[]}"u8.ToArray()),
            default);

        var json = await DataSyncDiagnosticReader.ReadJsonAsync(databasePath, default);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(1, document.RootElement.GetProperty("outboxPending").GetInt64());
        Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ciphertext", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptStateReturnsSanitizedDiagnosticAndIsNeverRecreated()
    {
        var databasePath = Path.Combine(_root, "corrupt", "sync.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await File.WriteAllTextAsync(databasePath, $"corrupt {_root}");
        var repository = new DataSyncRepository(
            databasePath,
            TimeProvider.System,
            new EncryptedOutboxProtector(new XorTestProtector()));

        await Assert.ThrowsAnyAsync<Exception>(() => repository.InitializeAsync(default));
        var json = await DataSyncDiagnosticReader.ReadJsonAsync(databasePath, default);

        Assert.Equal($"corrupt {_root}", await File.ReadAllTextAsync(databasePath));
        Assert.Contains("state_unreadable", json, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, DataSyncCommandMode.Continuous)]
    [InlineData("--once", DataSyncCommandMode.Once)]
    [InlineData("--diagnose", DataSyncCommandMode.Diagnose)]
    public void CommandLineAcceptsOnlyFixedModes(string? argument, DataSyncCommandMode expected)
    {
        var arguments = argument is null ? [] : new[] { argument };
        Assert.Equal(expected, DataSyncCommandLine.Parse(arguments));
        Assert.Throws<ArgumentException>(() => DataSyncCommandLine.Parse(["--url", "secret"]));
        Assert.Throws<ArgumentException>(() => DataSyncCommandLine.Parse(["C:/data"]));
    }

    private static DataSyncWorker CreateWorker(
        FakeRuntime runtime,
        IDataSyncHintSource? hints = null,
        DataSyncWorkerOptions? options = null) => new(
        runtime,
        hints is null ? [] : [hints],
        options ?? new DataSyncWorkerOptions(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(1),
            2),
        TimeProvider.System);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeRuntime(IEnumerable<bool> parseResults) : IDataSyncRuntime
    {
        private readonly Queue<bool> _parseResults = new(parseResults);
        private readonly TaskCompletionSource _firstReconcile = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _parserConcurrency;
        private int _uploadConcurrency;
        private readonly object _callsGate = new();

        internal int InitializeCount { get; private set; }
        internal int ReconcileCount { get; private set; }
        internal int ParserCalls { get; private set; }
        internal int UploadCalls { get; private set; }
        internal int HeartbeatCount { get; private set; }
        internal int TelemetryReconcileCount { get; private set; }
        internal int MaximumParserConcurrency { get; private set; }
        internal int MaximumUploadConcurrency { get; private set; }
        internal bool DelayOperations { get; init; }
        internal Action<int>? AfterReconcile { get; set; }
        internal Action<int>? AfterHeartbeat { get; set; }
        internal int ThrowHeartbeatAt { get; init; }
        internal List<string> Calls { get; } = [];
        internal Task FirstReconcile => _firstReconcile.Task;

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task ReconcileHandoffsAsync(CancellationToken cancellationToken)
        {
            AddCall("reconcile");
            ReconcileCount++;
            _firstReconcile.TrySetResult();
            AfterReconcile?.Invoke(ReconcileCount);
            return Task.CompletedTask;
        }

        public Task ReconcileTelemetryAsync(CancellationToken cancellationToken)
        {
            TelemetryReconcileCount++;
            return Task.CompletedTask;
        }

        public Task EnqueueHeartbeatAsync(CancellationToken cancellationToken)
        {
            AddCall("heartbeat");
            HeartbeatCount++;
            AfterHeartbeat?.Invoke(HeartbeatCount);
            if (HeartbeatCount == ThrowHeartbeatAt)
                throw new InvalidOperationException("simulated heartbeat failure");
            return Task.CompletedTask;
        }

        public async Task<bool> ProcessOneParserJobAsync(CancellationToken cancellationToken)
        {
            ParserCalls++;
            var concurrency = Interlocked.Increment(ref _parserConcurrency);
            MaximumParserConcurrency = Math.Max(MaximumParserConcurrency, concurrency);
            try
            {
                if (DelayOperations) await Task.Delay(20, cancellationToken);
                return _parseResults.Count > 0 && _parseResults.Dequeue();
            }
            finally
            {
                Interlocked.Decrement(ref _parserConcurrency);
            }
        }

        public async Task<UploadDisposition> UploadOneAsync(
            string workerId,
            CancellationToken cancellationToken)
        {
            AddCall("upload");
            UploadCalls++;
            var concurrency = Interlocked.Increment(ref _uploadConcurrency);
            MaximumUploadConcurrency = Math.Max(MaximumUploadConcurrency, concurrency);
            try
            {
                if (DelayOperations) await Task.Delay(40, cancellationToken);
                return UploadDisposition.Idle;
            }
            finally
            {
                Interlocked.Decrement(ref _uploadConcurrency);
            }
        }

        private void AddCall(string value)
        {
            lock (_callsGate) Calls.Add(value);
        }
    }

    private sealed class FakeHintSource : IDataSyncHintSource
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private ChannelWriter<DataSyncHint>? _writer;

        public async Task RunAsync(
            ChannelWriter<DataSyncHint> writer,
            CancellationToken cancellationToken)
        {
            _writer = writer;
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        internal async Task EmitAsync(DataSyncHint hint)
        {
            await _started.Task;
            await _writer!.WriteAsync(hint);
        }
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
        {
            var output = plaintext.ToArray();
            for (var index = 0; index < output.Length; index++)
                output[index] ^= entropy[index % entropy.Length];
            return output;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
            Protect(ciphertext, entropy);
    }
}
