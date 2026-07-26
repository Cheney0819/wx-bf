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
    private const int MaximumParserPagesPerRun = 4096;
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
        var stageKey = "input_list";
        ParserProcessResult? process = null;
        try
        {
            var inputs = await _repository.ListParseJobInputsAsync(job.Id, cancellationToken);
            stageKey = "job_build";
            var jobRoot = Path.Combine(_jobsRoot, job.Id);
            var built = Directory.Exists(jobRoot)
                ? await _jobBuilder.LoadExistingAsync(job, inputs, 5000, cancellationToken)
                : await _jobBuilder.BuildAsync(job, inputs, 5000, cancellationToken);
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            if (built.Manifest.Cursor is not null)
                seenCursors.Add(built.Manifest.Cursor);
            long messageCount = 0;
            long contactCount = 0;
            long favoriteCount = 0;
            long noticeCount = 0;
            for (var page = 1; page <= MaximumParserPagesPerRun; page++)
            {
                process = null;
                stageKey = "process_start";
                try
                {
                    process = await _supervisor.RunAsync(built.JobManifestPath, cancellationToken);
                }
                catch (ParserSupervisorException exception) when (exception.Code == "parser_cleanup_timeout")
                {
                    stageKey = "process_cleanup";
                    throw;
                }
                stageKey = "process_exit";
                if (process.ExitCode != 0 || process.StdoutTruncated)
                    throw new InvalidDataException("Parser process did not return a bounded success object.");
                ParserCompletion completion;
                stageKey = "completion_parse";
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
                stageKey = "result_validate";
                var result = await _resultValidator.ValidateAsync(
                    expectedResultPath,
                    job.Id,
                    job.SourceSetId,
                    cancellationToken);
                if (result.NextCursor is not null && seenCursors.Contains(result.NextCursor))
                    throw new InvalidDataException("Parser continuation cursor repeated.");
                stageKey = "outbox_commit";
                await _outboxWriter.CommitAsync(job, result, cancellationToken);
                messageCount += result.Messages.Count;
                contactCount += result.Contacts.Count;
                favoriteCount += result.Favorites.Count;
                noticeCount += result.Notices.Count;
                if (result.NextCursor is null)
                {
                    TryDeleteJobDirectory(built.JobRoot);
                    stageKey = "completed";
                    await RecordEventAsync(
                        "datasync_parser_completed",
                        "success",
                        cancellationToken,
                        new
                        {
                            stageCode = stageKey,
                            pageCount = page,
                            messageCount,
                            contactCount,
                            favoriteCount,
                            noticeCount,
                        });
                    return true;
                }

                seenCursors.Add(result.NextCursor);
                stageKey = "job_cursor";
                built = await _jobBuilder.AdvanceCursorAsync(
                    built,
                    result.NextCursor,
                    cancellationToken);
            }

            stageKey = "page_limit";
            throw new InvalidDataException("Parser continuation page limit exceeded.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidDataException or IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or JsonException or
            InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or
            ParserSupervisorException)
        {
            await RecordEventAsync(
                "datasync_parser_failed",
                "bounded_failure",
                cancellationToken,
                BuildParserFailureMetrics(stageKey, process, exception));
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
            if (result.Disposition == UploadDisposition.CredentialMissing)
            {
                await RecordLocalDiagnosticAsync(
                    _repository,
                    "datasync_credential_missing",
                    "credential_missing",
                    cancellationToken);
            }
            else if (ShouldEmitUploadOutcome(result))
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
        CancellationToken cancellationToken,
        object? metrics = null) => RecordEventCoreAsync(eventType, code, metrics, cancellationToken);

    private async Task RecordEventCoreAsync(
        string eventType,
        string code,
        object? metrics,
        CancellationToken cancellationToken)
    {
        try
        {
            var metricsElement = metrics is null
                ? JsonSerializer.SerializeToElement(new { }, JsonOptions)
                : JsonSerializer.SerializeToElement(metrics, JsonOptions);
            await _repository.RecordRuntimeEventAsync(
                eventType,
                JsonSerializer.Serialize(new { code, metrics = metricsElement }, JsonOptions),
                cancellationToken);
            if (_eventWriter is null) return;
            var bytes = Encoding.UTF8.GetBytes($"{eventType}|{code}|{DateTimeOffset.UtcNow:O}|{Guid.NewGuid():N}");
            string eventId;
            try { eventId = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
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
                    metricsElement),
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

    private static object BuildParserFailureMetrics(
        string stageKey,
        ParserProcessResult? process,
        Exception exception)
    {
        var stderrCode = ExtractStderrCode(process?.Stderr);
        var metrics = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["stageCode"] = stageKey,
            ["failureCode"] = ParserFailureCode(stageKey, exception),
            ["stdoutTruncated"] = process?.StdoutTruncated ?? false,
            ["stderrTruncated"] = process?.StderrTruncated ?? false,
        };
        if (process is not null) metrics["exitCode"] = process.ExitCode;
        if (stderrCode is not null) metrics["stderrCode"] = stderrCode;
        return metrics;
    }

    private static string ParserFailureCode(string stageKey, Exception exception)
    {
        if (exception is ParserSupervisorException supervisorException)
            return supervisorException.Code;
        return (stageKey, exception) switch
        {
            ("job_build", FileNotFoundException) => "parser_input_missing",
            ("job_build", System.Security.Cryptography.CryptographicException) =>
                "parser_input_hash_mismatch",
            ("process_start", FileNotFoundException) => "parser_artifact_missing",
            ("process_start", System.Security.Cryptography.CryptographicException) =>
                "parser_hash_mismatch",
            ("process_start", InvalidDataException or JsonException) => "parser_install_invalid",
            ("process_start", System.ComponentModel.Win32Exception) =>
                "parser_process_start_failed",
            ("process_exit", InvalidDataException) => "parser_process_failed",
            ("completion_parse", InvalidDataException or JsonException) =>
                "parser_completion_invalid",
            ("result_validate", FileNotFoundException) => "parser_result_missing",
            ("result_validate", InvalidDataException or JsonException) => "parser_result_invalid",
            ("outbox_commit", _) => "parser_outbox_commit_failed",
            _ => "parser_bounded_failure",
        };
    }

    private static string? ExtractStderrCode(string? stderr)
    {
        var token = stderr?
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(token)) return null;
        if (token.Length > 80 || token.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            return "stderr_present";
        }
        return token.ToLowerInvariant();
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
