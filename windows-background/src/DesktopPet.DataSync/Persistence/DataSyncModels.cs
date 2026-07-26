using DesktopPet.Background.Contracts;

namespace DesktopPet.DataSync.Persistence;

public enum ParseJobState
{
    Pending,
    Leased,
    Completed,
    Failed,
}

public enum OutboxState
{
    Pending,
    Leased,
    Acknowledged,
    Quarantined,
}

public sealed record ImportedManifest(
    string ManifestId,
    string EpochId,
    string SourceSetId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ImportedAtUtc);

public sealed record DatabaseCatalogEntry(
    string GenerationId,
    string ManifestId,
    string RelativePath,
    string PlaintextPath,
    string Sha256,
    DateTimeOffset CreatedAtUtc);

public sealed record ParseJobInput(
    string JobId,
    string GenerationId,
    string RelativePath,
    string PlaintextPath,
    string Sha256,
    int Ordinal);

public sealed record ValidatedHandoffDatabase(
    string GenerationId,
    string RelativePath,
    string PlaintextPath,
    string Sha256);

public sealed record ValidatedHandoffManifest(
    string ManifestId,
    string EpochId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<ValidatedHandoffDatabase> Databases);

public sealed record HandoffImportResult(
    string ManifestId,
    string SourceSetId,
    string JobId,
    bool WasAlreadyImported);

public sealed record ParseJob(
    string Id,
    string SourceSetId,
    ParseJobState State,
    string? LeaseOwner,
    DateTimeOffset? LeaseUntilUtc,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record OutboxDraft(
    string Id,
    string IdempotencyKey,
    string Endpoint,
    byte[] Payload);

public sealed record OutboxRecord(
    string Id,
    string IdempotencyKey,
    string Endpoint,
    byte[] Ciphertext,
    OutboxState State,
    string? LeaseOwner,
    DateTimeOffset? LeaseUntilUtc,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    int? LastStatusCode,
    string? LastErrorSummary,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record DataSyncRuntimeEvent(
    long Sequence,
    string EventType,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc);

public sealed record OperationalStateEntry(
    string Key,
    string ValueJson,
    DateTimeOffset UpdatedAtUtc);

public sealed record TelemetryCommitDraft(
    OperationalTelemetryEnvelope Envelope,
    string RequestId,
    byte[] Ciphertext,
    IReadOnlyDictionary<string, string> OperationalState);

public sealed record TelemetryImportResult(
    string? EventId,
    string? RequestId,
    bool WasAlreadyImported,
    bool WasRejected);

public interface IDataSyncRepository : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task EnqueueParseJobAsync(ParseJob job, CancellationToken cancellationToken);

    Task<ParseJob?> TryClaimParseJobAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<ParseJob?> GetParseJobAsync(string jobId, CancellationToken cancellationToken);

    Task EnqueueOutboxAsync(OutboxDraft draft, CancellationToken cancellationToken);

    Task EnqueueStatusOutboxAsync(
        OutboxDraft draft,
        string sessionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OutboxRecord>> GetPendingOutboxAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<OutboxRecord?> TryClaimOutboxAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<HandoffImportResult> ImportHandoffAsync(
        ValidatedHandoffManifest manifest,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ImportedManifest>> ListManifestsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ParseJobInput>> ListParseJobInputsAsync(
        string jobId,
        CancellationToken cancellationToken);

    Task<long> CountExportedItemsAsync(CancellationToken cancellationToken);

    Task<long> CountOutboxAsync(CancellationToken cancellationToken);

    Task<OutboxRecord?> GetOutboxAsync(
        string outboxId,
        CancellationToken cancellationToken);

    Task AcknowledgeOutboxAsync(
        string outboxId,
        string workerId,
        int statusCode,
        CancellationToken cancellationToken);

    Task ScheduleOutboxRetryAsync(
        string outboxId,
        string workerId,
        DateTimeOffset nextAttemptAtUtc,
        int statusCode,
        string summary,
        CancellationToken cancellationToken);

    Task QuarantineOutboxAsync(
        string outboxId,
        string workerId,
        int statusCode,
        string summary,
        CancellationToken cancellationToken);

    Task<int> RequeueQuarantinedOutboxAsync(
        IReadOnlyCollection<int> statusCodes,
        CancellationToken cancellationToken);

    Task RecordRuntimeEventAsync(
        string eventType,
        string payloadJson,
        CancellationToken cancellationToken);

    Task<OperationalStateEntry?> GetOperationalStateAsync(
        string key,
        CancellationToken cancellationToken);
}
