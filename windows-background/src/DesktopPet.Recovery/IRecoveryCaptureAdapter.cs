using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;

namespace DesktopPet.Recovery;

public interface IRecoveryCaptureAdapter
{
    Task<CaptureObservation> CaptureAsync(
        RecoveryEpoch epoch,
        CancellationToken cancellationToken);
}

public interface IRecoveryKeyReuseAdapter
{
    Task<CaptureObservation> TryDecryptAsync(
        RecoveryEpoch epoch,
        CancellationToken cancellationToken);
}
