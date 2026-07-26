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

        if (observation.HasValidatedKey)
        {
            await _repository.MarkKeyAvailableAsync(epochId, cancellationToken);
            return observation.OutputPaths.Count > 0
                ? RecoveryAction.Publish(observation.OutputPaths, observation.Databases)
                : RecoveryAction.Wait("key_available_without_output");
        }

        if (observation.HasPendingCapture)
        {
            await _repository.MarkPendingAvailableAsync(epochId, cancellationToken);
            return observation.OutputPaths.Count > 0
                ? RecoveryAction.Publish(observation.OutputPaths, observation.Databases)
                : RecoveryAction.Wait("pending_capture_available");
        }

        if (observation.OutputPaths.Count > 0)
            return RecoveryAction.Wait("outputs_without_validated_key");

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
