using System.Runtime.Versioning;
using DesktopPet.Background.Contracts;
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
    private readonly IWeChatIdentityProvider _identityProvider;
    private readonly IWeChatDataRootLocator _dataRootLocator;
    private readonly ValidatedKeyVault _validatedKeyVault;
    private readonly IOperationalTelemetryPublisher _telemetryPublisher;
    private readonly RecoveryPreflightTelemetry _preflightTelemetry;
    private readonly IProgress<RecoveryProgress> _progress;
    private readonly SemaphoreSlim _keyReuseFence = new(1, 1);

    public RecoveryCycle(
        BackgroundPaths paths,
        RecoveryRepository repository,
        IWeChatIdentityProvider identityProvider,
        IWeChatDataRootLocator dataRootLocator,
        ValidatedKeyVault validatedKeyVault,
        IOperationalTelemetryPublisher telemetryPublisher,
        IProgress<RecoveryProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(identityProvider);
        ArgumentNullException.ThrowIfNull(dataRootLocator);
        ArgumentNullException.ThrowIfNull(validatedKeyVault);
        ArgumentNullException.ThrowIfNull(telemetryPublisher);
        _paths = paths;
        _repository = repository;
        _identityProvider = identityProvider;
        _dataRootLocator = dataRootLocator;
        _validatedKeyVault = validatedKeyVault;
        _telemetryPublisher = telemetryPublisher;
        _preflightTelemetry = new RecoveryPreflightTelemetry(telemetryPublisher);
        _progress = progress ?? NullProgress<RecoveryProgress>.Instance;
    }

    public async Task<RecoveryAction> RunAsync(
        RecoveryCycleTrigger trigger,
        CancellationToken cancellationToken)
    {
        var resolution = await _dataRootLocator.LocateAsync(cancellationToken);
        if (!resolution.Found && resolution.Code == "data_root_missing")
        {
            await _preflightTelemetry.PublishDataRootResultAsync(
                resolution,
                wechatLoggedIn: false,
                cancellationToken);
            return RecoveryAction.Wait(resolution.Code);
        }

        WeChatRuntimeIdentity processRuntime;
        try
        {
            processRuntime = _identityProvider.ResolveActiveProcess();
        }
        catch (AmbiguousWeChatProcessException exception)
        {
            var ambiguous = new WeChatDataRootResolution(
                DataRoot: null,
                exception.CandidateCount,
                DatabaseCount: 0,
                exception.Code);
            await _preflightTelemetry.PublishDataRootResultAsync(
                ambiguous,
                wechatLoggedIn: true,
                cancellationToken);
            return RecoveryAction.Wait(exception.Code);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or DirectoryNotFoundException)
        {
            await _preflightTelemetry.PublishDataRootResultAsync(
                resolution,
                wechatLoggedIn: false,
                cancellationToken);
            return RecoveryAction.Wait("target_not_running");
        }

        resolution = await _dataRootLocator.LocateAsync(
            processRuntime,
            cancellationToken);
        if (!resolution.Found)
        {
            await _preflightTelemetry.PublishDataRootResultAsync(
                resolution,
                wechatLoggedIn: true,
                cancellationToken);
            return RecoveryAction.Wait(resolution.Code);
        }

        WeChatRuntimeIdentity runtime;
        try
        {
            runtime = _identityProvider.BindDataRoot(
                processRuntime,
                resolution.DataRoot!);
        }
        catch (DirectoryNotFoundException)
        {
            await _preflightTelemetry.PublishDataRootResultAsync(
                resolution,
                wechatLoggedIn: true,
                cancellationToken);
            return RecoveryAction.Wait("data_root_missing");
        }
        await _preflightTelemetry.PublishDataRootResultAsync(
            resolution,
            wechatLoggedIn: true,
            cancellationToken);

        var epochIdentity = runtime.EpochIdentity ??
            throw new InvalidOperationException("Bound runtime identity is missing its epoch identity.");
        var dataRoot = runtime.DataRoot ??
            throw new InvalidOperationException("Bound runtime identity is missing its data root.");
        var epoch = await _repository.BeginOrLoadEpochAsync(
            epochIdentity,
            explicitRetry: false,
            cancellationToken);
        var stagingRoot = Path.Combine(_paths.RecoveryRoot, "Staging");
        var outputRoot = Path.Combine(stagingRoot, "Readable");
        var persistedKeyAdapter = new PersistedKeyRecoveryAdapter(
            new PersistedKeyDecryptor(_repository, _validatedKeyVault),
            dataRoot,
            outputRoot,
            _progress);
        var captureAdapter = new Rc9CaptureAdapter(
            dataRoot,
            outputRoot,
            Path.Combine(_paths.RecoveryVault, "PendingCaptures"),
            _validatedKeyVault,
            runtime,
            _progress);
        var publisher = new AtomicHandoffPublisher(
            _paths.RecoveryGenerations,
            _paths.HandoffReady,
            stagingRoot,
            TimeProvider.System);
        var coordinator = new RecoveryCoordinator(
            _repository,
            captureAdapter,
            new WindowsAppProcessController(runtime),
            publisher,
            _telemetryPublisher,
            persistedKeyAdapter,
            telemetryPublishTimeout: TimeSpan.FromSeconds(2),
            keyReuseFence: _keyReuseFence);
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
