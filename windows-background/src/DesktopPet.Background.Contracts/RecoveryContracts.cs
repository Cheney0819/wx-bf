namespace DesktopPet.Background.Contracts;

public enum RecoveryMode
{
    PassiveWaiting,
    CapturingCurrentProcess,
    RestartingForCapture,
    KeyMaterialAvailable,
    CaptureCircuitOpen,
    Degraded,
}

public sealed record RecoveryEpochIdentity(
    string ExecutableVersion,
    string DataRootIdentity);

public sealed record RecoveredDatabase(
    string GenerationId,
    string RelativePath,
    string PlaintextPath,
    string Sha256);

public sealed record CaptureObservation(
    bool HasValidatedKey,
    bool HasPendingCapture,
    IReadOnlyList<string> OutputPaths,
    string? FailureCode,
    IReadOnlyList<RecoveredDatabase>? RecoveredDatabases = null,
    int CandidateDatabaseCount = 0,
    IReadOnlyList<string>? UnmatchedDatabasePaths = null,
    IReadOnlyList<string>? FailedDatabasePaths = null,
    bool RequiredDatabasesComplete = true)
{
    public IReadOnlyList<RecoveredDatabase> Databases => RecoveredDatabases ?? [];

    public IReadOnlyList<string> UnmatchedDatabases => UnmatchedDatabasePaths ?? [];

    public IReadOnlyList<string> FailedDatabases => FailedDatabasePaths ?? [];
}

public sealed record DatabaseReadyItem(
    string GenerationId,
    string RelativePath,
    string PlaintextPath,
    string Sha256);

public sealed record DatabaseReadyManifest(
    int SchemaVersion,
    string ManifestId,
    string EpochId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<DatabaseReadyItem> Databases,
    bool RequiredDatabasesComplete = false);
