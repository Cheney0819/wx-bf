using Footprint.Core.Contracts;

namespace Footprint.Core.Capture;

public interface IRestartExecutor
{
    Task<RestartExecutionResult> ExecuteAsync(RestartExecutionRequest request,
        IProgress<FootprintEvent> progress, CancellationToken cancellationToken);
}

public interface IFridaSpawnPort
{
    Task<IFridaCaptureSession> SpawnAsync(string executablePath, string profilePath,
        string outputDirectory, CancellationToken cancellationToken);
}

public interface IFridaCaptureSession : IAsyncDisposable
{
    string SessionId { get; }
    Task<bool> WaitForKeyCaptureAsync(CancellationToken cancellationToken);
}

public sealed record RestartExecutionRequest(
    string RunId,
    RestartDecision Decision,
    RestartRequestKind RequestKind,
    string? CommandId,
    CaptureGenerationId Generation,
    WeixinInstallation Installation,
    ProfileSelectionResult ProfileSelection,
    string ProfilePath,
    string OutputDirectory,
    long FirstDeviceSequence,
    long FirstRunSequence);

public enum RestartExecutionStatus
{
    Succeeded,
    BudgetUnavailable,
    Rejected,
    TimedOut,
    Cancelled,
    Failed
}

public sealed class RestartExecutionResult : IAsyncDisposable
{
    private IFridaCaptureSession? _session;

    public RestartExecutionResult(RestartExecutionStatus status, string code, string messageZh,
        IFridaCaptureSession? session = null)
    {
        Status = status;
        Code = code;
        MessageZh = messageZh;
        _session = session;
    }

    public RestartExecutionStatus Status { get; }
    public string Code { get; }
    public string MessageZh { get; }
    public bool HasSession => Volatile.Read(ref _session) is not null;

    public bool IsSuccessful => Status == RestartExecutionStatus.Succeeded;

    public IFridaCaptureSession TakeSession() =>
        Interlocked.Exchange(ref _session, null) ??
        throw new InvalidOperationException("捕获会话已转移或已释放。");

    public async ValueTask DisposeAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null) await session.DisposeAsync();
    }
}
