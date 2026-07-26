using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;

namespace DesktopPet.Recovery;

public sealed class RecoveryStateMachine
{
    private readonly IRecoveryRepository _repository;

    public RecoveryStateMachine(IRecoveryRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public RecoveryAction Begin(RecoveryEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        return epoch.IsActive
            ? RecoveryAction.CaptureCurrent()
            : RecoveryAction.Wait("epoch_inactive");
    }

    public async Task<RecoveryAction> ObserveAsync(
        string epochId,
        CaptureObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epochId);
        ArgumentNullException.ThrowIfNull(observation);

        var epoch = await _repository.GetEpochAsync(epochId, cancellationToken) ??
            throw new InvalidOperationException("Recovery epoch does not exist.");
        if (!epoch.IsActive) return RecoveryAction.Wait("epoch_inactive");

        if (string.Equals(
                observation.FailureCode,
                "breakpoint_restore_failed",
                StringComparison.Ordinal))
        {
            return RecoveryAction.Relaunch("breakpoint_restore_failed");
        }

        if (string.Equals(
                observation.FailureCode,
                "unsupported_module",
                StringComparison.Ordinal))
        {
            return RecoveryAction.Wait("unsupported_module");
        }

        // A readable output is already a validated handoff unit. Publish it now,
        // while the remaining databases continue through later reconciliation.
        if (observation.OutputPaths.Count > 0)
        {
            if (observation.RequiredDatabasesComplete && observation.HasValidatedKey)
                await _repository.MarkKeyAvailableAsync(epochId, cancellationToken);
            else if (observation.RequiredDatabasesComplete && observation.HasPendingCapture)
                await _repository.MarkPendingAvailableAsync(epochId, cancellationToken);
            return RecoveryAction.Publish(
                observation.OutputPaths,
                observation.Databases,
                observation.RequiredDatabasesComplete);
        }

        if (observation.HasValidatedKey)
        {
            await _repository.MarkKeyAvailableAsync(epochId, cancellationToken);
            return RecoveryAction.Wait("key_available_without_output");
        }

        if (observation.HasPendingCapture)
        {
            await _repository.MarkPendingAvailableAsync(epochId, cancellationToken);
            return RecoveryAction.Wait("pending_capture_available");
        }

        if (RecoveryPolicy.IsRestartSuppressed(epoch))
            return RecoveryAction.Wait("active_restart_suppressed");

        if (await _repository.TryConsumeRestartAsync(epochId, cancellationToken))
            return RecoveryAction.RestartAndCapture();

        epoch = await _repository.GetEpochAsync(epochId, cancellationToken) ??
            throw new InvalidOperationException("Recovery epoch disappeared.");
        if (RecoveryPolicy.IsRestartSuppressed(epoch))
            return RecoveryAction.Wait("active_restart_suppressed");

        if (epoch.RestartCount >= RecoveryPolicy.MaximumActiveRestarts)
        {
            await _repository.OpenCircuitAsync(
                epochId,
                string.IsNullOrWhiteSpace(observation.FailureCode)
                    ? "zero_key"
                    : observation.FailureCode,
                cancellationToken);
            return RecoveryAction.Wait("capture_circuit_open");
        }

        return RecoveryAction.Wait("restart_not_available");
    }
}
