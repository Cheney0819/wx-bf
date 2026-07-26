using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;
using System.Text.Json;

namespace DesktopPet.Recovery;

public sealed class RecoveryCoordinator
{
    private readonly IRecoveryRepository _repository;
    private readonly RecoveryStateMachine _stateMachine;
    private readonly IRecoveryCaptureAdapter _captureAdapter;
    private readonly IAppProcessController _processController;
    private readonly AtomicHandoffPublisher _handoffPublisher;
    private readonly IOperationalTelemetryPublisher _telemetryPublisher;
    private readonly IRecoveryKeyReuseAdapter? _keyReuseAdapter;
    private readonly TimeSpan _telemetryPublishTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RecoveryCoordinator(
        IRecoveryRepository repository,
        IRecoveryCaptureAdapter captureAdapter,
        IAppProcessController processController,
        AtomicHandoffPublisher handoffPublisher,
        IOperationalTelemetryPublisher telemetryPublisher,
        IRecoveryKeyReuseAdapter? keyReuseAdapter = null,
        TimeSpan? telemetryPublishTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(captureAdapter);
        ArgumentNullException.ThrowIfNull(processController);
        ArgumentNullException.ThrowIfNull(handoffPublisher);
        ArgumentNullException.ThrowIfNull(telemetryPublisher);
        var publishTimeout = telemetryPublishTimeout ?? TimeSpan.FromSeconds(2);
        if (publishTimeout <= TimeSpan.Zero || publishTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(telemetryPublishTimeout));
        _repository = repository;
        _stateMachine = new RecoveryStateMachine(repository);
        _captureAdapter = captureAdapter;
        _processController = processController;
        _handoffPublisher = handoffPublisher;
        _telemetryPublisher = telemetryPublisher;
        _keyReuseAdapter = keyReuseAdapter;
        _telemetryPublishTimeout = publishTimeout;
    }

    public async Task<RecoveryAction> RunEpochAsync(
        RecoveryEpoch epoch,
        CancellationToken cancellationToken) =>
        await RunEpochAsync(epoch, allowLiveCapture: true, cancellationToken);

    public async Task<RecoveryAction> RunEpochAsync(
        RecoveryEpoch epoch,
        bool allowLiveCapture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        await _gate.WaitAsync(cancellationToken);
        Task<CaptureObservation>? preparedCaptureTask = null;
        CancellationTokenSource? preparedCaptureCancellation = null;
        try
        {
            var current = await RequireActiveEpochAsync(epoch.Id, cancellationToken);
            if (_keyReuseAdapter is not null)
            {
                var reuse = await _keyReuseAdapter.TryDecryptAsync(current, cancellationToken);
                if (reuse.HasValidatedKey || reuse.HasPendingCapture || reuse.OutputPaths.Count > 0)
                {
                    var reuseAction = await _stateMachine.ObserveAsync(
                        epoch.Id,
                        reuse,
                        cancellationToken);
                    if (reuseAction.Kind == RecoveryActionKind.PublishOutputs)
                    {
                        await PublishDecryptResultBestEffortAsync(
                            reuse,
                            cancellationToken);
                        var publication = await _handoffPublisher.PublishWithStatusAsync(
                            epoch.Id,
                            reuseAction.Databases,
                            cancellationToken);
                        if (publication.WasPublished)
                        {
                            await PublishTelemetryBestEffortAsync(
                                "recovery_handoff_published",
                                "info",
                                "handoff_ready",
                                new { databaseCount = reuseAction.Databases.Count },
                                cancellationToken);
                        }
                    }
                    return reuseAction;
                }
            }
            if (!allowLiveCapture) return RecoveryAction.Wait("key_reuse_only");
            var action = _stateMachine.Begin(current);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (action.Kind)
                {
                    case RecoveryActionKind.CaptureCurrent:
                        current = await RequireActiveEpochAsync(epoch.Id, cancellationToken);
                        await PublishTelemetryBestEffortAsync(
                            "recovery_capture_started",
                            "info",
                            "capture_started",
                            new
                            {
                                restartCount = current.RestartCount,
                                executableVersion = current.Identity.ExecutableVersion,
                            },
                            cancellationToken);
                        CaptureObservation observation;
                        var captureTask = preparedCaptureTask;
                        var captureCancellation = preparedCaptureCancellation;
                        preparedCaptureTask = null;
                        preparedCaptureCancellation = null;
                        try
                        {
                            captureTask ??= _captureAdapter.CaptureAsync(
                                current,
                                cancellationToken);
                            observation = await captureTask;
                        }
                        catch (Exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            await PublishTelemetryBestEffortAsync(
                                "recovery_capture_failed",
                                "error",
                                "capture_failed",
                                new { restartCount = current.RestartCount },
                                CancellationToken.None);
                            throw;
                        }
                        finally
                        {
                            captureCancellation?.Dispose();
                        }
                        action = await _stateMachine.ObserveAsync(
                            epoch.Id,
                            observation,
                            cancellationToken);
                        var observationCode = StableCodeOrDefault(
                            observation.FailureCode,
                            "capture_failed");
                        if (observation.HasValidatedKey || observation.HasPendingCapture || observation.OutputPaths.Count > 0)
                        {
                            await PublishTelemetryBestEffortAsync(
                                "recovery_capture_succeeded",
                                "info",
                                observation.HasValidatedKey
                                    ? "key_validated"
                                    : observation.HasPendingCapture
                                        ? "pending_capture_available"
                                        : "partial_outputs_available",
                                new
                                {
                                    databaseCount = observation.Databases.Count,
                                    outputCount = observation.OutputPaths.Count,
                                },
                                cancellationToken);
                            if (observation.OutputPaths.Count > 0)
                            {
                                await PublishDecryptResultBestEffortAsync(
                                    observation,
                                    cancellationToken);
                            }
                        }
                        else
                        {
                            await PublishTelemetryBestEffortAsync(
                                "recovery_capture_failed",
                                "warning",
                                observationCode,
                                new { restartCount = current.RestartCount },
                                cancellationToken);
                            if (action.Kind == RecoveryActionKind.WaitPassively &&
                                action.Reason == "capture_circuit_open")
                            {
                                await PublishTelemetryBestEffortAsync(
                                    "recovery_circuit_opened",
                                    "error",
                                    observationCode,
                                    new { restartCount = RecoveryPolicy.MaximumActiveRestarts },
                                    cancellationToken);
                            }
                        }
                        break;

                    case RecoveryActionKind.RestartAndCapture:
                        current = await RequireActiveEpochAsync(epoch.Id, cancellationToken);
                        await PublishTelemetryBestEffortAsync(
                            "recovery_restart_started",
                            "info",
                            "restart_started",
                            new { restartCount = current.RestartCount },
                            cancellationToken);
                        CancellationTokenSource? preparationCancellation = null;
                        Task<CaptureObservation>? preparationTask = null;
                        try
                        {
                            preparationCancellation =
                                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            await _processController.RestartAsync(
                                token =>
                                {
                                    token.ThrowIfCancellationRequested();
                                    preparationTask = _captureAdapter.CaptureAsync(
                                        current,
                                        preparationCancellation.Token);
                                    return Task.CompletedTask;
                                },
                                cancellationToken);
                            preparedCaptureTask = preparationTask ??
                                throw new InvalidOperationException(
                                    "Capture preparation did not start before the target process.");
                            preparedCaptureCancellation = preparationCancellation;
                            preparationTask = null;
                            preparationCancellation = null;
                        }
                        catch (Exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            await PublishTelemetryBestEffortAsync(
                                "recovery_restart_failed",
                                "error",
                                "restart_failed",
                                new { restartCount = current.RestartCount },
                                CancellationToken.None);
                            throw;
                        }
                        finally
                        {
                            CancelAndObservePreparedCapture(
                                preparationTask,
                                preparationCancellation);
                        }
                        await PublishTelemetryBestEffortAsync(
                            "recovery_restart_completed",
                            "info",
                            "restart_completed",
                            new { restartCount = current.RestartCount },
                            cancellationToken);
                        action = RecoveryAction.CaptureCurrent();
                        break;

                    case RecoveryActionKind.PublishOutputs:
                        var publication = await _handoffPublisher.PublishWithStatusAsync(
                            epoch.Id,
                            action.Databases,
                            cancellationToken);
                        if (publication.WasPublished)
                        {
                            await PublishTelemetryBestEffortAsync(
                                "recovery_handoff_published",
                                "info",
                                "handoff_ready",
                                new { databaseCount = action.Databases.Count },
                                cancellationToken);
                        }
                        return action;

                    case RecoveryActionKind.WaitPassively:
                        return action;

                    default:
                        throw new InvalidOperationException(
                            $"Unsupported recovery action: {action.Kind}.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PublishTelemetryBestEffortAsync(
                "recovery_coordinator_failed",
                "error",
                "unexpected_error",
                new { },
                CancellationToken.None);
            var payload = JsonSerializer.Serialize(new
            {
                epochId = epoch.Id,
                stage = "run_epoch",
                exceptionType = exception.GetType().Name,
            });
            await _repository.RecordRuntimeEventAsync(
                "recovery_coordinator_error",
                payload,
                CancellationToken.None);
            throw;
        }
        finally
        {
            CancelAndObservePreparedCapture(
                preparedCaptureTask,
                preparedCaptureCancellation);
            _gate.Release();
        }
    }

    private static void CancelAndObservePreparedCapture(
        Task<CaptureObservation>? captureTask,
        CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        cancellation.Cancel();
        if (captureTask is null)
        {
            cancellation.Dispose();
            return;
        }

        _ = captureTask.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task PublishTelemetryBestEffortAsync(
        string eventName,
        string severity,
        string code,
        object metrics,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_telemetryPublishTimeout);
        try
        {
            var publication = _telemetryPublisher.PublishAsync(
                new OperationalTelemetryDraft(
                    "recovery",
                    eventName,
                    severity,
                    code,
                    JsonSerializer.SerializeToElement(metrics)),
                timeout.Token);
            await publication.WaitAsync(_telemetryPublishTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            timeout.Cancel();
            var payload = JsonSerializer.Serialize(new
            {
                code = "telemetry_publish_failed",
                eventName,
            });
            using var diagnosticTimeout = new CancellationTokenSource(_telemetryPublishTimeout);
            try
            {
                var diagnostic = _repository.RecordRuntimeEventAsync(
                    "telemetry_publish_failed",
                    payload,
                    diagnosticTimeout.Token);
                await diagnostic.WaitAsync(_telemetryPublishTimeout);
            }
            catch (Exception)
            {
                // Telemetry diagnostics remain best effort relative to Recovery work.
            }
        }
    }

    private Task PublishDecryptResultBestEffortAsync(
        CaptureObservation observation,
        CancellationToken cancellationToken)
    {
        var databaseCount = Math.Max(
            observation.CandidateDatabaseCount,
            Math.Max(observation.Databases.Count, observation.OutputPaths.Count));
        var outputCount = Math.Min(observation.OutputPaths.Count, databaseCount);
        var pendingCount = Math.Max(0, databaseCount - outputCount);
        return PublishTelemetryBestEffortAsync(
            "client_wechat_decrypt_export_result",
            "info",
            pendingCount == 0 ? "success" : "partial_success",
            new
            {
                databaseCount,
                outputCount,
                pendingCount,
            },
            cancellationToken);
    }

    private static string StableCodeOrDefault(string? value, string fallback) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 32 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character =>
            character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '_')
            ? value
            : fallback;

    private async Task<RecoveryEpoch> RequireActiveEpochAsync(
        string epochId,
        CancellationToken cancellationToken)
    {
        var epoch = await _repository.GetEpochAsync(epochId, cancellationToken) ??
            throw new InvalidOperationException("Recovery epoch does not exist.");
        if (!epoch.IsActive)
            throw new InvalidOperationException("Recovery epoch is no longer active.");
        return epoch;
    }
}
