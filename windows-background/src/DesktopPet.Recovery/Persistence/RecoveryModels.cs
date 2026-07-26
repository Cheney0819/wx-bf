using DesktopPet.Background.Contracts;

namespace DesktopPet.Recovery.Persistence;

public sealed record RecoveryEpoch(
    string Id,
    RecoveryEpochIdentity Identity,
    int RestartCount,
    bool ActiveRestartSuppressed,
    RecoveryMode Mode,
    string? FailureCode,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record DatabaseGenerationState(
    string Id,
    string EpochId,
    string RelativePath,
    string ContentFingerprint,
    string Status,
    string? OutputPath,
    DateTimeOffset UpdatedAtUtc);

public sealed record RecoveryRuntimeEvent(
    long Sequence,
    string EventType,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc);

public interface IRecoveryRepository : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<RecoveryEpoch> BeginOrLoadEpochAsync(
        RecoveryEpochIdentity identity,
        bool explicitRetry,
        CancellationToken cancellationToken);

    Task<RecoveryEpoch?> GetEpochAsync(
        string epochId,
        CancellationToken cancellationToken);

    Task<bool> TryConsumeRestartAsync(
        string epochId,
        CancellationToken cancellationToken);

    Task MarkKeyAvailableAsync(
        string epochId,
        CancellationToken cancellationToken);

    Task MarkPendingAvailableAsync(
        string epochId,
        CancellationToken cancellationToken);

    Task OpenCircuitAsync(
        string epochId,
        string failureCode,
        CancellationToken cancellationToken);

    Task RecordGenerationAsync(
        DatabaseGenerationState generation,
        CancellationToken cancellationToken);

    Task<DatabaseGenerationState?> GetGenerationAsync(
        string generationId,
        CancellationToken cancellationToken);

    Task RecordRuntimeEventAsync(
        string eventType,
        string payloadJson,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RecoveryRuntimeEvent>> GetRecentRuntimeEventsAsync(
        int limit,
        CancellationToken cancellationToken);
}
