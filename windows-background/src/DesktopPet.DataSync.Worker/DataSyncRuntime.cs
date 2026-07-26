using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using DesktopPet.Background.Contracts;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Upload;
using DesktopPet.DataSync.Telemetry;

namespace DesktopPet.DataSync.Worker;

public sealed class DataSyncRuntime : IDataSyncRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _readyRoot;
    private readonly string _jobsRoot;
    private readonly string _parserWorkerId;
    private readonly DataSyncRepository _repository;
    private readonly HandoffManifestImporter _importer;
    private readonly ParserJobBuilder _jobBuilder;
    private readonly ParserProcessSupervisor _supervisor;
    private readonly ParserResultValidator _resultValidator;
    private readonly IncrementalOutboxWriter _outboxWriter;
    private readonly OutboxUploader _uploader;
    private readonly StatusOutboxWriter? _statusWriter;
    private readonly TelemetryHandoffImporter? _telemetryImporter;
    private readonly TelemetryOutboxWriter? _eventWriter;

    public DataSyncRuntime(
        string readyRoot,
        string jobsRoot,
        string parserWorkerId,
        DataSyncRepository repository,
        HandoffManifestImporter importer,
        ParserJobBuilder jobBuilder,
        ParserProcessSupervisor supervisor,
        ParserResultValidator resultValidator,
        IncrementalOutboxWriter outboxWriter,
        OutboxUploader uploader,
        StatusOutboxWriter? statusWriter = null,
        TelemetryHandoffImporter? telemetryImporter = null,
        TelemetryOutboxWriter? eventWriter = null)
    {
        _readyRoot = Path.GetFullPath(readyRoot);
        _jobsRoot = Path.GetFullPath(jobsRoot);
        _parserWorkerId = parserWorkerId;
        _repository = repository;
        _importer = importer;
        _jobBuilder = jobBuilder;
        _supervisor = supervisor;
        _resultValidator = resultValidator;
        _outboxWriter = outboxWriter;
        _uploader = uploader;
        _statusWriter = statusWriter;
        _telemetryImporter = telemetryImporter;
        _eventWriter = eventWriter;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken);
        await RecordEventAsync("datasync_worker_started", "success", cancellationToken);
    }

    public async Task ReconcileHandoffsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_readyRoot)) return;
        foreach (var manifestPath in Directory.EnumerateFiles(
                     _readyRoot,
                     "*.json",
                     SearchOption.TopDirectoryOnly)
                 .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var imported = await _importer.ImportAsync(manifestPath, cancellationToken);
                if (!imported.WasAlreadyImported)
                    await RecordEventAsync("datasync_handoff_imported", "success", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                InvalidDataException or IOException or UnauthorizedAccessException or
                System.Security.Cryptography.CryptographicException or JsonException)
            {
                await RecordEventAsync("datasync_handoff_rejected", "validation_failed", cancellationToken);
            }
        }
    }

    public async Task ReconcileTelemetryAsync(CancellationToken cancellationToken)
    {
        if (_telemetryImporter is null) return;
        var readyRoot = Path.Combine(
            Path.GetDirectoryName(_readyRoot) ?? _readyRoot,
            "Telemetry",
            "ready");
        if (!Directory.Exists(readyRoot)) return;
        var invalid = new List<string>();
        var valid = new List<(string Path, DateTimeOffset OccurredAtUtc, string EventId)>();
        foreach (var path in Directory.EnumerateFiles(readyRoot, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(item => Path.GetFileName(item), StringComparer.Ordinal))
        {
            try
            {
                var order = await _telemetryImporter.ReadOccurrenceOrderAsync(path, cancellationToken);
                valid.Add((path, order.OccurredAtUtc, order.EventId));
            }
            catch (InvalidDataException)
            {
                invalid.Add(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                System.Security.Cryptography.CryptographicException or JsonException)
            {
                await RecordEventAsync("datasync_handoff_rejected", "bounded_failure", cancellationToken);
            }
        }

        var orderedPaths = invalid.Concat(valid
            .OrderBy(item => item.OccurredAtUtc)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .Select(item => item.Path));
        foreach (var path in orderedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await _telemetryImporter.ImportAsync(path, cancellationToken);
                if (result.WasRejected || !result.WasAlreadyImported)
                    await RecordEventAsync(
                        result.WasRejected ? "datasync_handoff_rejected" : "datasync_handoff_imported",
                        result.WasRejected ? "validation_failed" : "success",
                        cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or
                UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException)
            {
                await RecordEventAsync("datasync_handoff_rejected", "bounded_failure", cancellationToken);
            }
        }
    }

    public async Task EnqueueHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_statusWriter is null) return;
        try
        {
            await _statusWriter.EnqueueHeartbeatAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await RecordLocalDiagnosticAsync(
                _repository, "datasync_heartbeat_failed", "bounded_failure", cancellationToken);
        }
    }

    public async Task<bool> ProcessOneParserJobAsync(CancellationToken cancellationToken)
    {
        var job = await _repository.TryClaimParseJobAsync(
            _parserWorkerId,
            TimeSpan.FromMinutes(3),
            cancellationToken);
        if (job is null) return false;
        try
        {
            var inputs = await _repository.ListParseJobInputsAsync(job.Id, cancellationToken);
            var jobRoot = Path.Combine(_jobsRoot, job.Id);
            var built = Directory.Exists(jobRoot)
                ? await _jobBuilder.LoadExistingAsync(job, inputs, 5000, cancellationToken)
                : await _jobBuilder.BuildAsync(job, inputs, 5000, cancellationToken);
            var process = await _supervisor.RunAsync(built.JobManifestPath, cancellationToken);
            if (process.ExitCode != 0 || process.StdoutTruncated)
                throw new InvalidDataException("Parser process did not return a bounded success object.");
            ParserCompletion completion;
            try
            {
                completion = JsonSerializer.Deserialize<ParserCompletion>(process.Stdout, JsonOptions) ??
                    throw new InvalidDataException("Parser completion object is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Parser completion object is invalid.", exception);
            }
            var expectedResultPath = Path.Combine(built.OutputRoot, "result.json");
            if (completion.SchemaVersion != 1 ||
                completion.JobId != job.Id ||
                completion.SourceSetId != job.SourceSetId ||
                string.IsNullOrWhiteSpace(completion.ResultPath) ||
                Path.GetFullPath(completion.ResultPath) != expectedResultPath)
            {
                throw new InvalidDataException("Parser completion identity is invalid.");
            }
            var result = await _resultValidator.ValidateAsync(
                expectedResultPath,
                job.Id,
                job.SourceSetId,
                cancellationToken);
            await _outboxWriter.CommitAsync(job, result, cancellationToken);
            TryDeleteJobDirectory(built.JobRoot);
            await RecordEventAsync("datasync_parser_completed", "success", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidDataException or IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or JsonException or
            InvalidOperationException or ArgumentException)
        {
            await RecordEventAsync("datasync_parser_failed", "bounded_failure", cancellationToken);
        }
        return true;
    }

    public async Task<UploadDisposition> UploadOneAsync(
        string workerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _uploader.UploadOneAsync(workerId, cancellationToken);
            if (ShouldEmitUploadOutcome(result))
            {
                var eventType = result.Disposition switch
                {
                    UploadDisposition.Acknowledged => "datasync_upload_acknowledged",
                    UploadDisposition.RetryScheduled => "datasync_upload_retry_scheduled",
                    _ => "datasync_upload_quarantined",
                };
                await RecordEventAsync(eventType, result.Endpoint!, cancellationToken);
            }
            return result.Disposition;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidDataException or IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or HttpRequestException or
            InvalidOperationException or ArgumentException)
        {
            await RecordLocalDiagnosticAsync(
                _repository, "upload_failed", "bounded_failure", cancellationToken);
            return UploadDisposition.RetryScheduled;
        }
    }

    internal static bool ShouldEmitUploadOutcome(UploadResult result) =>
        result.Endpoint is "messages" or "contacts" or "favorites" &&
        result.Disposition is UploadDisposition.Acknowledged or
        UploadDisposition.RetryScheduled or UploadDisposition.Quarantined;

    internal static async Task RecordLocalDiagnosticAsync(
        DataSyncRepository repository,
        string eventType,
        string code,
        CancellationToken cancellationToken)
    {
        try
        {
            await repository.RecordRuntimeEventAsync(
                eventType,
                JsonSerializer.Serialize(new { code }),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A local diagnostic cannot reverse or reclassify completed core work.
        }
    }

    private Task RecordEventAsync(
        string eventType,
        string code,
        CancellationToken cancellationToken) => RecordEventCoreAsync(eventType, code, cancellationToken);

    private async Task RecordEventCoreAsync(
        string eventType,
        string code,
        CancellationToken cancellationToken)
    {
        try
        {
            await _repository.RecordRuntimeEventAsync(
                eventType,
                JsonSerializer.Serialize(new { code }),
                cancellationToken);
            if (_eventWriter is null) return;
            var bytes = Encoding.UTF8.GetBytes($"{eventType}|{code}|{DateTimeOffset.UtcNow:O}|{Guid.NewGuid():N}");
            string eventId;
            try { eventId = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            using var metrics = JsonDocument.Parse("{}");
            await _eventWriter.CommitAsync(
                new OperationalTelemetryEnvelope(
                    1,
                    eventId,
                    "datasync",
                    eventType,
                    eventType.Contains("failed", StringComparison.Ordinal) ||
                    eventType.Contains("quarantined", StringComparison.Ordinal)
                        ? "error" : "info",
                    code,
                    DateTimeOffset.UtcNow,
                    metrics.RootElement.Clone()),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            try
            {
                await _repository.RecordRuntimeEventAsync(
                    "datasync_telemetry_failed",
                    "{\"code\":\"bounded_failure\"}",
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Core state is already durable; telemetry diagnostics remain best effort.
            }
        }
    }

    private static void TryDeleteJobDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException)
        {
            // Completed work is durable; stale controlled input is safe to reconcile later.
        }
        catch (UnauthorizedAccessException)
        {
            // Completed work is durable; stale controlled input is safe to reconcile later.
        }
    }

    private sealed record ParserCompletion(
        int SchemaVersion,
        string ResultPath,
        string JobId,
        string SourceSetId);
}
