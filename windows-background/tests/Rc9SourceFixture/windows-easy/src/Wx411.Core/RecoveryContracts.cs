namespace Wx411.Core;

public sealed record RecoveryProcessSelection(int? Pid, string Name, bool ScanAll = false);

public sealed record RecoveryProgress(int Percent, string Message, string? Log);

public sealed record CaptureRecoveryResult(
    IReadOnlyList<string> OutputPaths,
    IReadOnlyList<DatabaseCaptureMatch> Matches,
    IReadOnlyList<string> UnmatchedDatabasePaths,
    IReadOnlyList<string> FailedDatabasePaths,
    IReadOnlyList<string> LoadedPendingCaptureTicketIds);
