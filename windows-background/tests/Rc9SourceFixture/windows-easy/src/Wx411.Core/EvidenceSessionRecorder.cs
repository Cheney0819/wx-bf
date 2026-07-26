using System.Runtime.InteropServices;

namespace Wx411.Core;

public sealed record EvidenceOperationHandle(Guid OperationId);

public sealed class EvidenceSessionRecorder
{
    private readonly object _sync = new();
    private readonly IEvidenceFileInspector _inspector;
    private readonly string _toolVersion;
    private readonly DateTimeOffset _startedUtc;
    private readonly List<OperationState> _operations = new();

    public EvidenceSessionRecorder(
        string toolVersion,
        IEvidenceFileInspector? inspector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        _toolVersion = toolVersion;
        _inspector = inspector ?? new EvidenceFileInspector();
        _startedUtc = DateTimeOffset.UtcNow;
    }

    public async Task<EvidenceOperationHandle> BeginAsync(
        EvidenceOperationKind kind,
        RecoveryProcessSelection process,
        string sourcePath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var state = new OperationState(
            Guid.NewGuid(),
            kind,
            DateTimeOffset.UtcNow,
            new EvidenceProcessSelection(process.Pid, process.Name, process.ScanAll),
            PendingFile(sourcePath),
            outputDirectory,
            PendingDirectory(outputDirectory));
        lock (_sync)
        {
            _operations.Add(state);
        }

        try
        {
            var sourceBefore = await _inspector.InspectAsync(
                sourcePath,
                verifySqlite: false,
                cancellationToken);
            var directoryBefore = _inspector.CaptureDirectory(outputDirectory);
            lock (_sync)
            {
                state.SourceBefore = sourceBefore;
                state.DirectoryBefore = directoryBefore;
            }
            return new EvidenceOperationHandle(state.OperationId);
        }
        catch (OperationCanceledException ex)
        {
            lock (_sync)
            {
                state.Outcome = EvidenceOperationOutcome.Cancelled;
                state.CompletedUtc = DateTimeOffset.UtcNow;
                state.SourceBefore = FailedFile(
                    sourcePath,
                    verifySqlite: false,
                    ex,
                    "begin_inspection_cancelled");
                state.DirectoryBefore = FailedDirectory(
                    outputDirectory,
                    ex,
                    "begin_inspection_cancelled");
                state.ErrorType = ex.GetType().Name;
                state.ErrorMessage = ex.Message;
            }
            throw;
        }
        catch
        {
            lock (_sync)
            {
                _operations.Remove(state);
            }
            throw;
        }
    }

    public async Task CompleteAsync(
        EvidenceOperationHandle handle,
        EvidenceOperationOutcome outcome,
        IEnumerable<string> outputPaths,
        Exception? error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(outputPaths);
        if (outcome == EvidenceOperationOutcome.Running)
            throw new ArgumentException("A completed operation cannot remain Running.", nameof(outcome));

        OperationState state;
        lock (_sync)
        {
            state = _operations.SingleOrDefault(item => item.OperationId == handle.OperationId) ??
                    throw new InvalidOperationException("Unknown evidence operation handle.");
            if (state.Outcome != EvidenceOperationOutcome.Running || state.Completing)
                throw new InvalidOperationException("Evidence operation is already complete.");
            state.Completing = true;
        }

        EvidenceFileRecord? sourceAfter = null;
        IReadOnlyList<string> normalizedOutputPaths = Array.Empty<string>();
        IReadOnlyList<EvidenceFileRecord>? outputs = null;
        EvidenceDirectorySnapshot? directoryAfter = null;
        try
        {
            normalizedOutputPaths = NormalizePaths(outputPaths);
            sourceAfter = await _inspector.InspectAsync(
                state.SourceBefore.Path,
                verifySqlite: false,
                cancellationToken);
            outputs = await InspectManyAsync(
                normalizedOutputPaths,
                verifySqlite: true,
                cancellationToken);
            directoryAfter = _inspector.CaptureDirectory(state.OutputDirectory);

            lock (_sync)
            {
                if (state.Outcome != EvidenceOperationOutcome.Running)
                    throw new InvalidOperationException("Evidence operation is already complete.");
                state.Outcome = outcome;
                state.CompletedUtc = DateTimeOffset.UtcNow;
                state.SourceAfter = sourceAfter;
                state.DirectoryAfter = directoryAfter;
                state.Outputs = outputs;
                state.ErrorType = error?.GetType().Name;
                state.ErrorMessage = error?.Message;
                state.Completing = false;
            }
        }
        catch (Exception inspectionError)
        {
            lock (_sync)
            {
                if (state.Outcome == EvidenceOperationOutcome.Running)
                {
                    state.Outcome = outcome;
                    state.CompletedUtc = DateTimeOffset.UtcNow;
                    state.SourceAfter = sourceAfter ?? FailedFile(
                        state.SourceBefore.Path,
                        verifySqlite: false,
                        inspectionError,
                        "completion_inspection_failed");
                    state.DirectoryAfter = directoryAfter ?? FailedDirectory(
                        state.OutputDirectory,
                        inspectionError,
                        "completion_inspection_failed");
                    state.Outputs = outputs ?? FailedFiles(
                        normalizedOutputPaths,
                        verifySqlite: true,
                        inspectionError);
                    state.ErrorType = error?.GetType().Name ?? inspectionError.GetType().Name;
                    state.ErrorMessage = JoinErrors(error?.Message, inspectionError.Message);
                }
                state.Completing = false;
            }
            throw;
        }
    }

    public void RecordCancelledPendingTickets(
        EvidenceOperationHandle handle,
        IReadOnlyCollection<string>? createdTicketIds)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var createdIds = createdTicketIds is null
            ? null
            : new HashSet<string>(createdTicketIds, StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            var state = _operations.SingleOrDefault(item => item.OperationId == handle.OperationId) ??
                        throw new InvalidOperationException("Unknown evidence operation handle.");
            if (state.Kind != EvidenceOperationKind.PreciseCapture ||
                state.Outcome != EvidenceOperationOutcome.Cancelled ||
                state.CompletedUtc is null)
            {
                throw new InvalidOperationException(
                    "Pending ticket evidence requires a completed cancelled PreciseCapture operation.");
            }
            if (state.PendingCaptureTicketNotReused is not null)
            {
                throw new InvalidOperationException(
                    "Pending ticket evidence cannot replace a completed correlation result.");
            }

            state.CreatedPendingCaptureTicketIds = createdIds;
        }
    }

    public void RecordPendingCaptureFollowUp(
        EvidenceOperationHandle successfulHandle,
        IEnumerable<string> loadedTicketIds)
    {
        ArgumentNullException.ThrowIfNull(successfulHandle);
        ArgumentNullException.ThrowIfNull(loadedTicketIds);
        var loadedIds = new HashSet<string>(loadedTicketIds, StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            var successful = _operations.SingleOrDefault(item => item.OperationId == successfulHandle.OperationId) ??
                             throw new InvalidOperationException("Unknown evidence operation handle.");
            if (successful.Kind != EvidenceOperationKind.PreciseCapture ||
                successful.Outcome != EvidenceOperationOutcome.Success ||
                successful.CompletedUtc is null)
            {
                throw new InvalidOperationException(
                    "Pending ticket follow-up requires a completed successful PreciseCapture operation.");
            }

            foreach (var cancelled in _operations)
            {
                var createdIds = cancelled.CreatedPendingCaptureTicketIds;
                if (cancelled.Kind != EvidenceOperationKind.PreciseCapture ||
                    cancelled.Outcome != EvidenceOperationOutcome.Cancelled ||
                    cancelled.CompletedUtc is not { } completedUtc ||
                    completedUtc > successful.StartedUtc ||
                    createdIds is null ||
                    cancelled.PendingCaptureTicketNotReused is not null)
                {
                    continue;
                }

                cancelled.PendingCaptureTicketNotReused = !createdIds.Overlaps(loadedIds);
            }
        }
    }

    public EvidenceSessionSnapshot Snapshot()
    {
        lock (_sync)
        {
            var operations = _operations.Select(ToRecord).ToArray();
            return new EvidenceSessionSnapshot(
                _toolVersion,
                _startedUtc,
                RuntimeInformation.OSDescription,
                Environment.Is64BitProcess,
                Environment.Is64BitOperatingSystem,
                Array.AsReadOnly(operations));
        }
    }

    private async Task<IReadOnlyList<EvidenceFileRecord>> InspectManyAsync(
        IEnumerable<string> paths,
        bool verifySqlite,
        CancellationToken cancellationToken)
    {
        var records = new List<EvidenceFileRecord>();
        foreach (var path in paths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            records.Add(await _inspector.InspectAsync(path, verifySqlite, cancellationToken));
        }
        return Array.AsReadOnly(records.ToArray());
    }

    private static IReadOnlyList<string> NormalizePaths(IEnumerable<string> paths) => Array.AsReadOnly(paths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray());

    private static IReadOnlyList<EvidenceFileRecord> FailedFiles(
        IEnumerable<string> paths,
        bool verifySqlite,
        Exception error) => Array.AsReadOnly(paths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => FailedFile(path, verifySqlite, error, "completion_inspection_failed"))
        .ToArray());

    private static EvidenceFileRecord PendingFile(string path) => new(
        path,
        Exists: false,
        Length: null,
        Sha256: null,
        IntegrityCheck: null,
        Error: "inspection_pending");

    private static EvidenceFileRecord FailedFile(
        string path,
        bool verifySqlite,
        Exception error,
        string code) => new(
        path,
        Exists: File.Exists(path),
        Length: null,
        Sha256: null,
        IntegrityCheck: verifySqlite ? "failed" : null,
        Error: $"{code}: {error.GetType().Name}: {error.Message}");

    private static EvidenceDirectorySnapshot PendingDirectory(string path) => new(
        path,
        DateTimeOffset.UtcNow,
        Array.Empty<EvidenceDirectoryEntry>(),
        Error: "inspection_pending");

    private static EvidenceDirectorySnapshot FailedDirectory(
        string path,
        Exception error,
        string code) => new(
        path,
        DateTimeOffset.UtcNow,
        Array.Empty<EvidenceDirectoryEntry>(),
        Error: $"{code}: {error.GetType().Name}: {error.Message}");

    private static string JoinErrors(string? operationError, string inspectionError) =>
        string.IsNullOrWhiteSpace(operationError)
            ? inspectionError
            : operationError + "; evidence inspection: " + inspectionError;

    private static EvidenceOperationRecord ToRecord(OperationState state) => new(
        state.OperationId,
        state.Kind,
        state.Outcome,
        state.StartedUtc,
        state.CompletedUtc,
        state.Process,
        state.SourceBefore,
        state.SourceAfter,
        state.OutputDirectory,
        CopyDirectorySnapshot(state.DirectoryBefore),
        state.DirectoryAfter is null ? null : CopyDirectorySnapshot(state.DirectoryAfter),
        Array.AsReadOnly(state.Outputs.ToArray()),
        state.ErrorType,
        state.ErrorMessage,
        state.PendingCaptureTicketNotReused);

    private static EvidenceDirectorySnapshot CopyDirectorySnapshot(EvidenceDirectorySnapshot snapshot) => new(
        snapshot.Path,
        snapshot.CapturedUtc,
        Array.AsReadOnly(snapshot.Entries.ToArray()),
        snapshot.Error);

    private sealed class OperationState
    {
        public OperationState(
            Guid operationId,
            EvidenceOperationKind kind,
            DateTimeOffset startedUtc,
            EvidenceProcessSelection process,
            EvidenceFileRecord sourceBefore,
            string outputDirectory,
            EvidenceDirectorySnapshot directoryBefore)
        {
            OperationId = operationId;
            Kind = kind;
            StartedUtc = startedUtc;
            Process = process;
            SourceBefore = sourceBefore;
            OutputDirectory = outputDirectory;
            DirectoryBefore = directoryBefore;
        }

        public Guid OperationId { get; }
        public EvidenceOperationKind Kind { get; }
        public DateTimeOffset StartedUtc { get; }
        public EvidenceProcessSelection Process { get; }
        public EvidenceFileRecord SourceBefore { get; set; }
        public string OutputDirectory { get; }
        public EvidenceDirectorySnapshot DirectoryBefore { get; set; }
        public EvidenceOperationOutcome Outcome { get; set; } = EvidenceOperationOutcome.Running;
        public DateTimeOffset? CompletedUtc { get; set; }
        public EvidenceFileRecord? SourceAfter { get; set; }
        public EvidenceDirectorySnapshot? DirectoryAfter { get; set; }
        public IReadOnlyList<EvidenceFileRecord> Outputs { get; set; } = Array.Empty<EvidenceFileRecord>();
        public string? ErrorType { get; set; }
        public string? ErrorMessage { get; set; }
        public HashSet<string>? CreatedPendingCaptureTicketIds { get; set; }
        public bool? PendingCaptureTicketNotReused { get; set; }
        public bool Completing { get; set; }
    }
}
