using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;

namespace DesktopPet.Recovery;

public enum RecoveryCaptureTarget
{
    BoundProcess,
    RestartedProcess,
}

public interface IRecoveryCaptureAdapter
{
    Task<CaptureObservation> CaptureAsync(
        RecoveryEpoch epoch,
        CancellationToken cancellationToken);

    Task<CaptureObservation> CaptureAsync(
        RecoveryEpoch epoch,
        RecoveryCaptureTarget target,
        CancellationToken cancellationToken) =>
        CaptureAsync(epoch, cancellationToken);

    Task<CaptureObservation> CaptureAsync(
        RecoveryEpoch epoch,
        RecoveryCaptureTarget target,
        IReadOnlySet<string> completedRelativePaths,
        CancellationToken cancellationToken) =>
        CaptureAsync(epoch, target, cancellationToken);
}

public interface IRecoveryKeyReuseAdapter
{
    Task<PersistedDecryptResult> TryDecryptAsync(
        RecoveryEpoch epoch,
        CancellationToken cancellationToken);
}
