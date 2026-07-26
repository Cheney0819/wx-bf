using System.Runtime.Versioning;
using DesktopPet.Background.Infrastructure;
using DesktopPet.Recovery.Persistence;
using DesktopPet.Recovery.Security;
using Wx411.Core;

namespace DesktopPet.Recovery.Worker;

[SupportedOSPlatform("windows")]
public sealed class RecoveryCycle : IRecoveryCycle
{
    private readonly BackgroundPaths _paths;
    private readonly RecoveryRepository _repository;
    private readonly WeChatIdentityProvider _identityProvider;
    private readonly IReadOnlyList<string> _knownDataRoots;
    private readonly ValidatedKeyVault _validatedKeyVault;
    private readonly IProgress<RecoveryProgress> _progress;

    public RecoveryCycle(
        BackgroundPaths paths,
        RecoveryRepository repository,
        WeChatIdentityProvider identityProvider,
        IReadOnlyList<string> knownDataRoots,
        ValidatedKeyVault validatedKeyVault,
        IProgress<RecoveryProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(identityProvider);
        ArgumentNullException.ThrowIfNull(knownDataRoots);
        ArgumentNullException.ThrowIfNull(validatedKeyVault);
        _paths = paths;
        _repository = repository;
        _identityProvider = identityProvider;
        _knownDataRoots = knownDataRoots;
        _validatedKeyVault = validatedKeyVault;
        _progress = progress ?? NullProgress<RecoveryProgress>.Instance;
    }

    public async Task<RecoveryAction> RunAsync(
        RecoveryCycleTrigger trigger,
        CancellationToken cancellationToken)
    {
        WeChatRuntimeIdentity runtime;
        try
        {
            runtime = _identityProvider.ResolveActive(_knownDataRoots);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or DirectoryNotFoundException)
        {
            return RecoveryAction.Wait("target_not_running_or_data_root_unavailable");
        }

        var epoch = await _repository.BeginOrLoadEpochAsync(
            runtime.EpochIdentity,
            explicitRetry: false,
            cancellationToken);
        var stagingRoot = Path.Combine(_paths.RecoveryRoot, "Staging");
        var outputRoot = Path.Combine(stagingRoot, "Readable");
        var persistedKeyAdapter = new PersistedKeyRecoveryAdapter(
            new PersistedKeyDecryptor(_repository, _validatedKeyVault),
            runtime.DataRoot,
            outputRoot,
            _progress);
        var captureAdapter = new Rc9CaptureAdapter(
            runtime.DataRoot,
            outputRoot,
            Path.Combine(_paths.RecoveryVault, "PendingCaptures"),
            _validatedKeyVault,
            _progress);
        var publisher = new AtomicHandoffPublisher(
            _paths.RecoveryGenerations,
            _paths.HandoffReady,
            stagingRoot,
            TimeProvider.System);
        var telemetryPublisher = new AtomicTelemetryPublisher(
            Path.Combine(_paths.HandoffRoot, "Telemetry", "ready"),
            TimeProvider.System);
        var coordinator = new RecoveryCoordinator(
            _repository,
            captureAdapter,
            new WindowsAppProcessController(),
            publisher,
            telemetryPublisher,
            persistedKeyAdapter,
            telemetryPublishTimeout: TimeSpan.FromSeconds(2));
        var allowLiveCapture = trigger is
            RecoveryCycleTrigger.Startup or RecoveryCycleTrigger.ProcessStarted;
        return await coordinator.RunEpochAsync(
            epoch,
            allowLiveCapture,
            cancellationToken);
    }

    private sealed class NullProgress<T> : IProgress<T>
    {
        internal static NullProgress<T> Instance { get; } = new();

        public void Report(T value)
        {
        }
    }
}
