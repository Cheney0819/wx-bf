namespace Footprint.Core.Capture;

public sealed record WeixinProcessSnapshot(int ProcessId, string ExecutablePath, string ModulePath,
    string InstallationDllSha256, string LoadedModuleFileSha256, nint ModuleBase, bool IsForeground,
    DateTimeOffset CapturedAtUtc);

public interface IWeixinProcessProbe
{
    Task<IReadOnlyList<WeixinProcessSnapshot>> CaptureAsync(CancellationToken cancellationToken);
}

public enum WeixinObservationStatus
{
    NoProcess,
    Verified,
    IdentityMismatch,
    ObservationFailed
}

public sealed record WeixinProcessDiagnostic(int ProcessId, string Code, string MessageZh);

public sealed class WeixinObservationResult
{
    public WeixinObservationResult(WeixinObservationStatus status,
        IReadOnlyList<WeixinProcessSnapshot> snapshots, string code, string messageZh,
        IReadOnlyList<WeixinProcessDiagnostic>? diagnostics = null, int candidateProcessCount = 0,
        bool enumerationSucceeded = true)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageZh);
        if (candidateProcessCount < 0) throw new ArgumentOutOfRangeException(nameof(candidateProcessCount));
        Status = status;
        Snapshots = Array.AsReadOnly(snapshots.ToArray());
        Code = code;
        MessageZh = messageZh;
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
        CandidateProcessCount = candidateProcessCount;
        EnumerationSucceeded = enumerationSucceeded;
    }

    public WeixinObservationStatus Status { get; }
    public IReadOnlyList<WeixinProcessSnapshot> Snapshots { get; }
    public string Code { get; }
    public string MessageZh { get; }
    public IReadOnlyList<WeixinProcessDiagnostic> Diagnostics { get; }
    public int CandidateProcessCount { get; }
    public bool EnumerationSucceeded { get; }
}

public interface IWeixinRestartObserver
{
    Task<WeixinObservationResult> ObserveAsync(CancellationToken cancellationToken);
}
