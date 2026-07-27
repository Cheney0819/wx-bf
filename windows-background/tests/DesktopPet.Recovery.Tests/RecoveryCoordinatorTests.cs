using System.Security.Cryptography;
using System.Text.Json;
using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;
using Wx411.Core;

namespace DesktopPet.Recovery.Tests;

public sealed class RecoveryCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-coordinator-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ZeroKeyConsumesTwoRestartsBeforeOpeningCircuit()
    {
        await using var fixture = await CreateFixtureAsync(
            Zero(), Zero(), Zero());

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.Equal("capture_circuit_open", action.Reason);
        Assert.Equal(3, fixture.Capture.CallCount);
        Assert.Equal(2, fixture.Process.RestartCount);
        Assert.Equal(
            ["capture", "restart", "capture", "process_start", "restart", "capture", "process_start"],
            fixture.Events);
        Assert.Equal(
            [
                "recovery_capture_started:capture_started",
                "recovery_capture_failed:zero_key",
                "recovery_restart_started:restart_started",
                "recovery_restart_completed:restart_completed",
                "recovery_capture_started:capture_started",
                "recovery_capture_failed:zero_key",
                "recovery_restart_started:restart_started",
                "recovery_restart_completed:restart_completed",
                "recovery_capture_started:capture_started",
                "recovery_capture_failed:zero_key",
                "recovery_circuit_opened:zero_key",
            ],
            fixture.Telemetry.Events.Select(EventIdentity));
        var persisted = await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default);
        Assert.Equal(2, persisted!.RestartCount);
        Assert.Equal(RecoveryMode.CaptureCircuitOpen, persisted.Mode);
    }

    [Fact]
    public async Task RestartPreparesCaptureBeforeStartingProcessAndReusesItOnce()
    {
        await using var fixture = await CreateFixtureAsync(
            Zero(),
            new CaptureObservation(false, true, [], null));

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.Equal("pending_capture_available", action.Reason);
        Assert.Equal(2, fixture.Capture.CallCount);
        Assert.Equal(1, fixture.Process.RestartCount);
        Assert.Equal(
            ["capture", "restart", "capture", "process_start"],
            fixture.Events);
        Assert.Equal(
            [RecoveryCaptureTarget.BoundProcess, RecoveryCaptureTarget.RestartedProcess],
            fixture.Capture.Targets);
    }

    [Fact]
    public async Task RestartStartFailureCancelsPreparedCapture()
    {
        await using var fixture = await CreateFixtureAsync(Zero(), Zero());
        fixture.Capture.BlockCallNumber = 2;
        fixture.Process.ExceptionAfterPreparation = new IOException("start failed");

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Coordinator.RunEpochAsync(fixture.Epoch, default));

        await fixture.Capture.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Capture.CallCount);
        Assert.Equal(1, fixture.Process.RestartCount);
        Assert.Equal(["capture", "restart", "capture"], fixture.Events);
    }

    [Fact]
    public async Task OpenCircuitStillAllowsLaterPassiveCaptureWithoutRestart()
    {
        await using var fixture = await CreateFixtureAsync(
            Zero(), Zero(), Zero(), Zero());
        await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        var later = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.WaitPassively, later.Kind);
        Assert.Equal("active_restart_suppressed", later.Reason);
        Assert.Equal(4, fixture.Capture.CallCount);
        Assert.Equal(2, fixture.Process.RestartCount);
    }

    [Fact]
    public async Task ReconciliationRunsKeyReuseWithoutLiveCaptureOrRestart()
    {
        await using var fixture = await CreateFixtureAsync(Zero());

        var action = await fixture.Coordinator.RunEpochAsync(
            fixture.Epoch,
            allowLiveCapture: false,
            default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.Equal("key_reuse_only", action.Reason);
        Assert.Equal(0, fixture.Capture.CallCount);
        Assert.Equal(0, fixture.Process.RestartCount);
    }

    [Fact]
    public async Task ValidKeyPublishesWithoutRestart()
    {
        var source = await WriteStagingAsync("message_0.sqlite", "plaintext"u8.ToArray());
        var sha256 = await Sha256Async(source);
        var recovered = new RecoveredDatabase(
            new string('a', 64),
            "message/message_0.db",
            source,
            sha256);
        await using var fixture = await CreateFixtureAsync(
            new CaptureObservation(
                true,
                false,
                [source],
                null,
                [recovered],
                CandidateDatabaseCount: 3));

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.PublishOutputs, action.Kind);
        Assert.Equal(0, fixture.Process.RestartCount);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(_root, "handoff", "ready"),
            "*.json"));
        var persisted = await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default);
        Assert.True(persisted!.ActiveRestartSuppressed);
        Assert.Equal(RecoveryMode.KeyMaterialAvailable, persisted.Mode);
        Assert.Equal(
            [
                "recovery_capture_started:capture_started",
                "recovery_capture_succeeded:key_validated",
                "client_wechat_decrypt_export_result:partial_success",
                "recovery_handoff_published:handoff_ready",
            ],
            fixture.Telemetry.Events.Select(EventIdentity));
        var decryptEvent = fixture.Telemetry.Events[^2];
        Assert.Equal(3, decryptEvent.Metrics.GetProperty("databaseCount").GetInt32());
        Assert.Equal(1, decryptEvent.Metrics.GetProperty("outputCount").GetInt32());
        Assert.Equal(2, decryptEvent.Metrics.GetProperty("pendingCount").GetInt32());
    }

    [Fact]
    public async Task PendingCaptureStopsWithoutRestart()
    {
        await using var fixture = await CreateFixtureAsync(
            new CaptureObservation(false, true, [], null));

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.Equal("pending_capture_available", action.Reason);
        Assert.Equal(0, fixture.Process.RestartCount);
        var persisted = await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default);
        Assert.True(persisted!.ActiveRestartSuppressed);
    }

    [Fact]
    public async Task UnsupportedModuleStopsWithoutConsumingRestartBudget()
    {
        await using var fixture = await CreateFixtureAsync(
            new CaptureObservation(false, false, [], "unsupported_module"));

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.Equal("unsupported_module", action.Reason);
        Assert.Equal(1, fixture.Capture.CallCount);
        Assert.Equal(0, fixture.Process.RestartCount);
        Assert.Equal(0, (await fixture.Repository.GetEpochAsync(
            fixture.Epoch.Id,
            default))!.RestartCount);
    }

    [Fact]
    public async Task BreakpointRestoreFailureRelaunchesWithoutStartingAnotherCapture()
    {
        await using var fixture = await CreateFixtureAsync(
            new CaptureObservation(false, false, [], "breakpoint_restore_failed"));

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.Equal("breakpoint_restore_relaunch_completed", action.Reason);
        Assert.Equal(1, fixture.Process.RestartCount);
        Assert.Equal(1, fixture.Capture.CallCount);
    }

    [Fact]
    public async Task ReadablePartialOutputsPublishImmediatelyWhileRemainingDatabasesStayPending()
    {
        var source = await WriteStagingAsync("partial.sqlite", "partial"u8.ToArray());
        var recovered = new RecoveredDatabase(
            new string('d', 64),
            "message/message_partial.db",
            source,
            await Sha256Async(source));
        await using var fixture = await CreateFixtureAsync(
            new CaptureObservation(
                HasValidatedKey: false,
                HasPendingCapture: false,
                OutputPaths: [source],
                FailureCode: "partial_success",
                RecoveredDatabases: [recovered],
                CandidateDatabaseCount: 18,
                RequiredDatabasesComplete: false));

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.PublishOutputs, action.Kind);
        Assert.Single(action.Databases);
        var manifestPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(_root, "handoff", "ready"),
            "*.json"));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Assert.Equal(2, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(manifest.RootElement
            .GetProperty("requiredDatabasesComplete")
            .GetBoolean());
        Assert.Contains(
            fixture.Telemetry.Events,
            draft => draft.EventName == "client_wechat_decrypt_export_result" &&
                      draft.Code == "partial_success");
        Assert.Equal(0, fixture.Process.RestartCount);
    }

    [Fact]
    public async Task PartialBatchPublishesAndContinuesWithCompletedDatabaseExcluded()
    {
        var auxiliarySource = await WriteStagingAsync("hardlink.sqlite", "auxiliary"u8.ToArray());
        var messageSource = await WriteStagingAsync("message.sqlite", "message"u8.ToArray());
        var auxiliary = new RecoveredDatabase(
            new string('6', 64),
            "db_storage/hardlink/hardlink.db",
            auxiliarySource,
            await Sha256Async(auxiliarySource));
        var message = new RecoveredDatabase(
            new string('7', 64),
            "db_storage/message/message_0.db",
            messageSource,
            await Sha256Async(messageSource));
        await using var fixture = await CreateFixtureAsync(
            new CaptureObservation(
                HasValidatedKey: true,
                HasPendingCapture: false,
                OutputPaths: [auxiliarySource],
                FailureCode: null,
                RecoveredDatabases: [auxiliary],
                CandidateDatabaseCount: 2,
                UnmatchedDatabasePaths: ["C:/fixture/db_storage/message/message_0.db"],
                RequiredDatabasesComplete: false),
            new CaptureObservation(
                HasValidatedKey: true,
                HasPendingCapture: false,
                OutputPaths: [messageSource],
                FailureCode: null,
                RecoveredDatabases: [message],
                CandidateDatabaseCount: 2,
                RequiredDatabasesComplete: true));

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.PublishOutputs, action.Kind);
        Assert.Equal(2, fixture.Capture.CallCount);
        Assert.Equal(2, Directory.EnumerateFiles(
            Path.Combine(_root, "handoff", "ready"),
            "*.json").Count());
        Assert.Empty(fixture.Capture.CompletedPathSnapshots[0]);
        Assert.Contains(
            "db_storage/hardlink/hardlink.db",
            fixture.Capture.CompletedPathSnapshots[1]);
        Assert.Equal("db_storage/message/message_0.db", Assert.Single(action.Databases).RelativePath);
        var decryptEvents = fixture.Telemetry.Events
            .Where(draft => draft.EventName == "client_wechat_decrypt_export_result")
            .ToArray();
        Assert.Equal(2, decryptEvents.Length);
        Assert.Equal(1, decryptEvents[0].Metrics.GetProperty("outputCount").GetInt32());
        Assert.Equal(1, decryptEvents[0].Metrics.GetProperty("pendingCount").GetInt32());
        Assert.Equal("success", decryptEvents[1].Code);
        Assert.Equal(2, decryptEvents[1].Metrics.GetProperty("outputCount").GetInt32());
        Assert.Equal(0, decryptEvents[1].Metrics.GetProperty("pendingCount").GetInt32());
    }

    [Fact]
    public async Task RestartedCaptureKeepsCompletedDatabaseExclusions()
    {
        var auxiliarySource = await WriteStagingAsync("restart-hardlink.sqlite", "auxiliary"u8.ToArray());
        var messageSource = await WriteStagingAsync("restart-message.sqlite", "message"u8.ToArray());
        var auxiliary = new RecoveredDatabase(
            new string('8', 64),
            "db_storage/hardlink/hardlink.db",
            auxiliarySource,
            await Sha256Async(auxiliarySource));
        var message = new RecoveredDatabase(
            new string('9', 64),
            "db_storage/message/message_0.db",
            messageSource,
            await Sha256Async(messageSource));
        await using var fixture = await CreateFixtureAsync(
            new CaptureObservation(
                HasValidatedKey: true,
                HasPendingCapture: false,
                OutputPaths: [auxiliarySource],
                FailureCode: null,
                RecoveredDatabases: [auxiliary],
                CandidateDatabaseCount: 2,
                UnmatchedDatabasePaths: ["C:/fixture/db_storage/message/message_0.db"],
                RequiredDatabasesComplete: false),
            Zero(),
            new CaptureObservation(
                HasValidatedKey: true,
                HasPendingCapture: false,
                OutputPaths: [messageSource],
                FailureCode: null,
                RecoveredDatabases: [message],
                CandidateDatabaseCount: 2,
                RequiredDatabasesComplete: true));

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.PublishOutputs, action.Kind);
        Assert.Equal(3, fixture.Capture.CallCount);
        Assert.Equal(1, fixture.Process.RestartCount);
        Assert.Equal(RecoveryCaptureTarget.RestartedProcess, fixture.Capture.Targets[2]);
        Assert.Contains(
            "db_storage/hardlink/hardlink.db",
            fixture.Capture.CompletedPathSnapshots[2]);
    }

    [Fact]
    public async Task OversizedFailureCodeFallsBackBeforeTelemetryPublication()
    {
        var oversized = new string('a', 33);
        await using var fixture = await CreateFixtureAsync(
            new CaptureObservation(false, false, [], oversized),
            new CaptureObservation(false, false, [], oversized),
            new CaptureObservation(false, false, [], oversized));

        await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.All(
            fixture.Telemetry.Events.Where(draft =>
                draft.EventName is "recovery_capture_failed" or "recovery_circuit_opened"),
            draft => Assert.Equal("capture_failed", draft.Code));
    }

    [Fact]
    public async Task PersistedKeyOutputPublishesOnceBeforeLiveCapture()
    {
        var source = await WriteStagingAsync("reused.sqlite", "reused"u8.ToArray());
        var sha256 = await Sha256Async(source);
        var reuse = new FakeReuseAdapter(new CaptureObservation(
            true,
            false,
            [source],
            null,
            [new RecoveredDatabase(
                new string('b', 64),
                "message/message_1.db",
                source,
                sha256)]));
        await using var fixture = await CreateFixtureAsync(reuse, Zero());

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.PublishOutputs, action.Kind);
        Assert.Equal(1, reuse.CallCount);
        Assert.Equal(0, fixture.Capture.CallCount);
        Assert.Equal(0, fixture.Process.RestartCount);

        var reconciled = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.PublishOutputs, reconciled.Kind);
        Assert.Equal(2, reuse.CallCount);
        Assert.Equal(
            1,
            fixture.Telemetry.Events.Count(draft =>
                draft.EventName == "recovery_handoff_published"));
    }

    [Fact]
    public async Task PartialPersistedOutputPublishesThenCapturesUnresolvedDatabases()
    {
        var source = await WriteStagingAsync("reused-partial.sqlite", "reused-partial"u8.ToArray());
        var sha256 = await Sha256Async(source);
        var reuse = new FakeReuseAdapter(new PersistedDecryptResult(
            new CaptureObservation(
                true,
                false,
                [source],
                "persisted_key_partial_failure",
                [new RecoveredDatabase(
                    new string('e', 64),
                    "message/message_partial.db",
                    source,
                    sha256)],
                CandidateDatabaseCount: 2),
            [new DatabaseSource("message/message_unresolved.db", 1)]));
        await using var fixture = await CreateFixtureAsync(
            reuse,
            Zero(),
            new CaptureObservation(false, true, [], null));

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.Equal("pending_capture_available", action.Reason);
        Assert.Equal(2, fixture.Capture.CallCount);
        Assert.Equal(1, fixture.Process.RestartCount);
        Assert.Equal(
            [RecoveryCaptureTarget.BoundProcess, RecoveryCaptureTarget.RestartedProcess],
            fixture.Capture.Targets);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(_root, "handoff", "ready"),
            "*.json"));
    }

    [Fact]
    public async Task IncompletePersistedKeyWithoutOutputKeepsRestartEligible()
    {
        var reuse = new FakeReuseAdapter(new PersistedDecryptResult(
            new CaptureObservation(
                HasValidatedKey: true,
                HasPendingCapture: false,
                OutputPaths: [],
                FailureCode: "persisted_key_export_failed",
                CandidateDatabaseCount: 1),
            [new DatabaseSource("message/message_unresolved.db", 1)]));
        await using var fixture = await CreateFixtureAsync(
            reuse,
            Zero(),
            new CaptureObservation(false, true, [], null));

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.Equal("pending_capture_available", action.Reason);
        Assert.Equal(2, fixture.Capture.CallCount);
        Assert.Equal(1, fixture.Process.RestartCount);
        Assert.Equal(
            [RecoveryCaptureTarget.BoundProcess, RecoveryCaptureTarget.RestartedProcess],
            fixture.Capture.Targets);
        Assert.False(Directory.Exists(Path.Combine(_root, "handoff", "ready")));
    }

    [Fact]
    public async Task RestartFailureDoesNotRefundPersistedBudget()
    {
        await using var fixture = await CreateFixtureAsync(Zero(), Zero(), Zero());
        fixture.Process.Exception = new IOException("restart failed");

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Coordinator.RunEpochAsync(fixture.Epoch, default));

        var persisted = await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default);
        Assert.Equal(1, persisted!.RestartCount);
        Assert.Equal(1, fixture.Process.RestartCount);
        Assert.Equal(
            [
                "recovery_capture_started:capture_started",
                "recovery_capture_failed:zero_key",
                "recovery_restart_started:restart_started",
                "recovery_restart_failed:restart_failed",
                "recovery_coordinator_failed:unexpected_error",
            ],
            fixture.Telemetry.Events.Select(EventIdentity));

        fixture.Process.Exception = null;
        var resumed = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal("capture_circuit_open", resumed.Reason);
        persisted = await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default);
        Assert.Equal(2, persisted!.RestartCount);
        Assert.Equal(RecoveryMode.CaptureCircuitOpen, persisted.Mode);
        Assert.Equal(2, fixture.Process.RestartCount);
    }

    [Fact]
    public async Task UnexpectedCaptureFailurePersistsBoundedSanitizedDiagnostic()
    {
        await using var fixture = await CreateFixtureAsync(Zero());
        fixture.Capture.Exception = new FormatException(
            $"sensitive path: {_root}");

        await Assert.ThrowsAsync<FormatException>(() =>
            fixture.Coordinator.RunEpochAsync(fixture.Epoch, default));

        var diagnostic = Assert.Single(
            await fixture.Repository.GetRecentRuntimeEventsAsync(10, default));
        Assert.Equal("recovery_coordinator_error", diagnostic.EventType);
        Assert.Contains("FormatException", diagnostic.PayloadJson);
        Assert.DoesNotContain(_root, diagnostic.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.True(diagnostic.PayloadJson.Length <= 4096);
        Assert.Equal(
            [
                "recovery_capture_started:capture_started",
                "recovery_capture_failed:capture_failed",
                "recovery_coordinator_failed:unexpected_error",
            ],
            fixture.Telemetry.Events.Select(EventIdentity));
    }

    [Fact]
    public async Task TelemetryFailureDoesNotChangeRecoveryActionOrRestartAccounting()
    {
        var telemetry = new RecordingTelemetryPublisher
        {
            Exception = new IOException("telemetry unavailable"),
        };
        await using var fixture = await CreateFixtureAsync(
            telemetry,
            reuse: null,
            observations: [Zero(), Zero(), Zero()]);

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.Equal("capture_circuit_open", action.Reason);
        Assert.Equal(3, fixture.Capture.CallCount);
        Assert.Equal(2, fixture.Process.RestartCount);
        var persisted = await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default);
        Assert.Equal(2, persisted!.RestartCount);
        Assert.Equal(RecoveryMode.CaptureCircuitOpen, persisted.Mode);
        var diagnostics = await fixture.Repository.GetRecentRuntimeEventsAsync(20, default);
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal("telemetry_publish_failed", diagnostic.EventType);
            Assert.DoesNotContain("telemetry unavailable", diagnostic.PayloadJson);
        });
    }

    [Fact]
    public async Task HungTelemetryDoesNotBlockHandoffPublication()
    {
        var source = await WriteStagingAsync("bounded.sqlite", "bounded"u8.ToArray());
        var recovered = new RecoveredDatabase(
            new string('c', 64),
            "message/message_2.db",
            source,
            await Sha256Async(source));
        var telemetry = new RecordingTelemetryPublisher { NeverCompletes = true };
        await using var fixture = await CreateFixtureAsync(
            telemetry,
            reuse: null,
            telemetryTimeout: TimeSpan.FromMilliseconds(20),
            observations:
            [
                new CaptureObservation(true, false, [source], null, [recovered]),
            ]);

        var action = await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecoveryActionKind.PublishOutputs, action.Kind);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(_root, "handoff", "ready"),
            "*.json"));
        var persisted = await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default);
        Assert.Equal(RecoveryMode.KeyMaterialAvailable, persisted!.Mode);
    }

    [Fact]
    public async Task HungTelemetryDoesNotMaskRestartFailure()
    {
        var telemetry = new RecordingTelemetryPublisher { NeverCompletes = true };
        await using var fixture = await CreateFixtureAsync(
            telemetry,
            reuse: null,
            telemetryTimeout: TimeSpan.FromMilliseconds(20),
            observations: [Zero()]);
        fixture.Process.Exception = new IOException("restart failed");

        await Assert.ThrowsAsync<IOException>(async () =>
            await fixture.Coordinator.RunEpochAsync(fixture.Epoch, default)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        var persisted = await fixture.Repository.GetEpochAsync(fixture.Epoch.Id, default);
        Assert.Equal(1, persisted!.RestartCount);
        Assert.Equal(1, fixture.Process.RestartCount);
    }

    private async Task<CoordinatorFixture> CreateFixtureAsync(
        params CaptureObservation[] observations)
        => await CreateFixtureAsync(reuse: null, observations);

    private async Task<CoordinatorFixture> CreateFixtureAsync(
        IRecoveryKeyReuseAdapter? reuse,
        params CaptureObservation[] observations)
        => await CreateFixtureAsync(
            new RecordingTelemetryPublisher(),
            reuse,
            telemetryTimeout: null,
            observations);

    private async Task<CoordinatorFixture> CreateFixtureAsync(
        RecordingTelemetryPublisher telemetry,
        IRecoveryKeyReuseAdapter? reuse,
        TimeSpan? telemetryTimeout = null,
        params CaptureObservation[] observations)
    {
        var repository = new RecoveryRepository(
            Path.Combine(_root, "state", "recovery.db"),
            TimeProvider.System);
        await repository.InitializeAsync(default);
        var epoch = await repository.BeginOrLoadEpochAsync(
            new RecoveryEpochIdentity("4.1.0", "root-a"),
            explicitRetry: false,
            default);
        var events = new List<string>();
        var capture = new FakeCaptureAdapter(events, observations);
        var process = new FakeProcessController(events);
        var publisher = new AtomicHandoffPublisher(
            Path.Combine(_root, "generations"),
            Path.Combine(_root, "handoff", "ready"),
            Path.Combine(_root, "staging"),
            TimeProvider.System);
        return new CoordinatorFixture(
            repository,
            epoch,
            capture,
            process,
            events,
            telemetry,
            new RecoveryCoordinator(
                repository,
                capture,
                process,
                publisher,
                telemetry,
                reuse,
                telemetryTimeout));
    }

    private static CaptureObservation Zero() =>
        new(false, false, [], "zero_key");

    private async Task<string> WriteStagingAsync(string name, byte[] content)
    {
        var directory = Path.Combine(_root, "staging");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record CoordinatorFixture(
        RecoveryRepository Repository,
        RecoveryEpoch Epoch,
        FakeCaptureAdapter Capture,
        FakeProcessController Process,
        List<string> Events,
        RecordingTelemetryPublisher Telemetry,
        RecoveryCoordinator Coordinator) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Repository.DisposeAsync();
    }

    private sealed class FakeCaptureAdapter(
        List<string> events,
        IEnumerable<CaptureObservation> observations) : IRecoveryCaptureAdapter
    {
        private readonly Queue<CaptureObservation> _observations = new(observations);

        public int CallCount { get; private set; }

        public List<RecoveryCaptureTarget> Targets { get; } = [];

        public List<IReadOnlySet<string>> CompletedPathSnapshots { get; } = [];

        public Exception? Exception { get; set; }

        public int? BlockCallNumber { get; set; }

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CaptureObservation> CaptureAsync(
            RecoveryEpoch epoch,
            CancellationToken cancellationToken) =>
            CaptureAsync(epoch, RecoveryCaptureTarget.BoundProcess, cancellationToken);

        public Task<CaptureObservation> CaptureAsync(
            RecoveryEpoch epoch,
            RecoveryCaptureTarget target,
            CancellationToken cancellationToken) =>
            CaptureAsync(
                epoch,
                target,
                new HashSet<string>(StringComparer.Ordinal),
                cancellationToken);

        public Task<CaptureObservation> CaptureAsync(
            RecoveryEpoch epoch,
            RecoveryCaptureTarget target,
            IReadOnlySet<string> completedRelativePaths,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Targets.Add(target);
            CompletedPathSnapshots.Add(completedRelativePaths.ToHashSet(StringComparer.Ordinal));
            CallCount++;
            events.Add("capture");
            if (Exception is not null) throw Exception;
            if (CallCount == BlockCallNumber)
                return WaitForCancellationAsync(cancellationToken);
            return Task.FromResult(_observations.Dequeue());
        }

        private async Task<CaptureObservation> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The capture wait was not canceled.");
            }
            finally
            {
                CancellationObserved.TrySetResult();
            }
        }
    }

    private sealed class FakeProcessController(List<string> events) : IAppProcessController
    {
        public Exception? Exception { get; set; }

        public Exception? ExceptionAfterPreparation { get; set; }

        public int RestartCount { get; private set; }

        public Task<AppProcessIdentity> RestartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestartCount++;
            events.Add("restart");
            if (Exception is not null) throw Exception;
            return Task.FromResult(new AppProcessIdentity(42, "C:/Program Files/TARGET/TARGET.exe"));
        }

        public async Task<AppProcessIdentity> RestartAsync(
            Func<CancellationToken, Task> beforeStart,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestartCount++;
            events.Add("restart");
            if (Exception is not null) throw Exception;
            await beforeStart(cancellationToken);
            if (ExceptionAfterPreparation is not null) throw ExceptionAfterPreparation;
            events.Add("process_start");
            return new AppProcessIdentity(42, "C:/Program Files/TARGET/TARGET.exe");
        }
    }

    private sealed class FakeReuseAdapter : IRecoveryKeyReuseAdapter
    {
        private readonly PersistedDecryptResult _result;

        public FakeReuseAdapter(PersistedDecryptResult result) => _result = result;

        public FakeReuseAdapter(CaptureObservation observation)
            : this(new PersistedDecryptResult(observation, []))
        {
        }

        public int CallCount { get; private set; }

        public Task<PersistedDecryptResult> TryDecryptAsync(
            RecoveryEpoch epoch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingTelemetryPublisher : IOperationalTelemetryPublisher
    {
        public List<OperationalTelemetryDraft> Events { get; } = [];

        public Exception? Exception { get; set; }

        public bool NeverCompletes { get; set; }

        public Task PublishAsync(
            OperationalTelemetryDraft draft,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(draft);
            if (NeverCompletes)
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            return Exception is null
                ? Task.CompletedTask
                : Task.FromException(Exception);
        }
    }

    private static string EventIdentity(OperationalTelemetryDraft draft) =>
        $"{draft.EventName}:{draft.Code}";
}
