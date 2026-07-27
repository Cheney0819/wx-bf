using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;
using Wx411.Core;

namespace DesktopPet.Recovery;

public sealed class PersistedKeyRecoveryAdapter : IRecoveryKeyReuseAdapter
{
    private readonly PersistedKeyDecryptor _decryptor;
    private readonly string _dataRoot;
    private readonly string _outputDirectory;
    private readonly IProgress<RecoveryProgress> _progress;

    public PersistedKeyRecoveryAdapter(
        PersistedKeyDecryptor decryptor,
        string dataRoot,
        string outputDirectory,
        IProgress<RecoveryProgress> progress)
    {
        ArgumentNullException.ThrowIfNull(decryptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(progress);
        _decryptor = decryptor;
        _dataRoot = Path.GetFullPath(dataRoot);
        _outputDirectory = Path.GetFullPath(outputDirectory);
        _progress = progress;
    }

    public Task<PersistedDecryptResult> TryDecryptAsync(
        RecoveryEpoch epoch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        cancellationToken.ThrowIfCancellationRequested();
        var databases = DatabaseSourceDiscovery.Discover([_dataRoot], cancellationToken);
        return _decryptor.TryDecryptUntilCancelledAsync(
            epoch,
            _dataRoot,
            databases,
            _outputDirectory,
            _progress,
            cancellationToken);
    }
}
