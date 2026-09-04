using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Footprint.Core;

public sealed class PlainDbExporter(
    IPlainProcessRunner? processRunner = null,
    IPlainKeyLoader? keyLoader = null)
{
    private readonly IPlainProcessRunner _processRunner = processRunner ?? SystemPlainProcessRunner.Instance;
    private readonly IPlainKeyLoader _keyLoader = keyLoader ?? new ProtectedPlainKeyLoader();

    public async Task<PlainExportSummary> ExportAsync(string sessionDirectory, SessionManifest sessionManifest,
        string sqlCipherExecutable, CancellationToken cancellationToken,
        Action<string, string, double>? progress = null)
    {
        var plan = PlainDbPlanner.Create(sessionDirectory, sessionManifest);
        Directory.CreateDirectory(plan.RootDirectory);
        await using var exportLock = AcquireLock(plan.RootDirectory);
        var previous = await ReadPreviousSummaryAsync(Path.Combine(plan.RootDirectory, "manifest.json"), cancellationToken);
        var summary = new PlainExportSummary
        {
            SessionId = sessionManifest.SessionId,
            Expected = PlainExportSummary.RequiredBusinessDatabaseCount,
            Excluded = plan.Excluded.Count
        };
        foreach (var exclusion in plan.Excluded)
        {
            var result = new PlainExportDatabaseResult
            {
                SourceIndex = exclusion.SourceIndex,
                SourceName = exclusion.SourceName,
                Status = "excluded",
                Stage = ToProtocolValue(PlainExportStage.Filter),
                Reason = exclusion.Reason
            };
            summary.Databases.Add(result);
            ApplyToSession(exclusion.Database, result);
        }
        foreach (var item in plan.Ineligible)
        {
            var result = new PlainExportDatabaseResult
            {
                Slot = item.Slot,
                SourceIndex = item.SourceIndex,
                SourceName = item.SourceName,
                Status = "failed",
                Stage = ToProtocolValue(PlainExportStage.SourceCheck),
                Reason = item.Reason,
                ErrorCode = "SourceNotEligible",
                ErrorSummary = item.Reason,
                SourceSnapshotFiles = item.Database.Snapshot.Files.ToList()
            };
            summary.Databases.Add(result);
            ApplyToSession(item.Database, result);
        }

        for (var index = 0; index < plan.Exportable.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = plan.Exportable[index];
            progress?.Invoke(candidate.SourceName, ToProtocolValue(PlainExportStage.SourceCheck),
                plan.Exportable.Count == 0 ? 1 : (double)index / plan.Exportable.Count);
            var priorResult = previous?.Databases.FirstOrDefault(item => item.SourceIndex == candidate.SourceIndex &&
                item.Status == "passed" && string.Equals(item.Slot, candidate.Slot, StringComparison.Ordinal));
            var result = await ExportOneAsync(sessionDirectory, candidate, priorResult, sqlCipherExecutable, cancellationToken,
                stage => progress?.Invoke(candidate.SourceName, ToProtocolValue(stage),
                    (index + 0.5) / plan.Exportable.Count));
            summary.Databases.Add(result);
            ApplyToSession(candidate.Database, result);
        }

        summary.Databases.Sort((left, right) => left.SourceIndex.CompareTo(right.SourceIndex));
        summary.Passed = summary.Databases.Count(item => item.Status == "passed");
        summary.Failed = summary.Databases.Count(item => item.Status == "failed");
        summary.Status = PlainExportSummary.CalculateStatus(summary.Passed, summary.Failed, summary.Expected);
        await PlainDbManifestWriter.WriteAsync(summary, Path.Combine(plan.RootDirectory, "manifest.json"), cancellationToken);
        progress?.Invoke(string.Empty, ToProtocolValue(PlainExportStage.Manifest), 1);
        return summary;
    }

    private async Task<PlainExportDatabaseResult> ExportOneAsync(string sessionDirectory, PlainExportCandidate candidate,
        PlainExportDatabaseResult? previous, string executable, CancellationToken cancellationToken,
        Action<PlainExportStage> progress)
    {
        var stopwatch = Stopwatch.StartNew();
        var stage = PlainExportStage.SourceCheck;
        byte[]? key = null;
        PlainDatabaseBaseline? source = null;
        PlainDatabaseBaseline? target = null;
        PlainValidationComparison? comparison = null;
        var countScriptPath = candidate.TemporaryPath + ".counts.sql";
        PlainExportDatabaseResult Result(PlainExportStage resultStage, PlainExportOutcome outcome) =>
            BuildResult(candidate, resultStage, stopwatch.ElapsedMilliseconds, source, target, comparison, outcome);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(candidate.FinalPath)!);
            PlainDbPublisher.DeleteTemporary(candidate.TemporaryPath);
            await PlainExportWorkspace.PrepareAsync(candidate, cancellationToken);
            if (string.IsNullOrWhiteSpace(candidate.Database.ProtectedKeyPath))
                throw new InvalidDataException("Protected database credential path is missing.");
            var protectedPath = Path.Combine(sessionDirectory,
                candidate.Database.ProtectedKeyPath.Replace('\\', Path.DirectorySeparatorChar));
            key = _keyLoader.Load(protectedPath);

            stage = PlainExportStage.SourceBaseline;
            progress(stage);
            source = await PlainDbValidator.ReadAsync(_processRunner, executable, candidate.SourcePath, key,
                candidate.Database.Compatibility, candidate.Database.PageSize, cancellationToken);

            if (await PlainExportWorkspace.CanReuseAsync(candidate, previous, cancellationToken))
            {
                stage = PlainExportStage.OpenPlaintext;
                progress(stage);
                target = await PlainDbValidator.ReadAsync(_processRunner, executable, candidate.FinalPath, null, 0, 0,
                    cancellationToken);
                comparison = PlainDbValidator.Compare(source, target);
                if (comparison.Passed)
                {
                    await PlainExportWorkspace.VerifyAsync(candidate, cancellationToken);
                    PlainExportWorkspace.EnsureNoSidecars(candidate.FinalPath);
                    var existing = new FileInfo(candidate.FinalPath);
                    return Result(PlainExportStage.Publish,
                        new PassedPlainExportOutcome(existing.Length, previous!.OutputSha256!));
                }
            }
            target = null;
            comparison = null;

            stage = PlainExportStage.Export;
            progress(stage);
            var execution = await PlainExportExecutor.ExecuteAsync(_processRunner, executable, candidate, key, source,
                countScriptPath, cancellationToken);
            if (!string.Equals(execution.PlainJournalMode, "delete", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plaintext journal mode was not normalized to DELETE.");
            source = execution.SourceBaseline;
            await PlainExportWorkspace.VerifyAsync(candidate, cancellationToken);

            stage = PlainExportStage.OpenPlaintext;
            progress(stage);
            await PlainDbValidator.EnsureHeaderAsync(candidate.TemporaryPath, cancellationToken);
            target = await execution.ReadTargetAsync(_processRunner, executable, candidate.TemporaryPath,
                cancellationToken);

            stage = PlainExportStage.Schema;
            comparison = PlainDbValidator.Compare(source, target);
            if (!comparison.Passed)
                throw new InvalidDataException(string.Join("; ", comparison.Errors));
            PlainExportWorkspace.EnsureNoSidecars(candidate.TemporaryPath);

            stage = PlainExportStage.Publish;
            progress(stage);
            await PlainDbPublisher.PublishAsync(candidate.TemporaryPath, candidate.FinalPath, cancellationToken);
            var hash = await Hashing.Sha256FileAsync(candidate.FinalPath, cancellationToken);
            var info = new FileInfo(candidate.FinalPath);
            return Result(stage, new PassedPlainExportOutcome(info.Length, hash));
        }
        catch (OperationCanceledException)
        {
            PlainDbPublisher.DeleteTemporary(candidate.TemporaryPath);
            throw;
        }
        catch (Exception error)
        {
            PlainDbPublisher.DeleteTemporary(candidate.TemporaryPath);
            return Result(stage,
                new FailedPlainExportOutcome(error.GetType().Name, Redactor.Redact(error.Message, key ?? [])));
        }
        finally
        {
            PlainExportWorkspace.DeleteFile(countScriptPath);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            PlainExportWorkspace.DeleteDirectory(candidate.WorkingDirectory);
        }
    }

    private static PlainExportDatabaseResult BuildResult(PlainExportCandidate candidate, PlainExportStage stage,
        long duration, PlainDatabaseBaseline? source, PlainDatabaseBaseline? target,
        PlainValidationComparison? comparison, PlainExportOutcome outcome)
    {
        (string status, long outputSize, string? outputHash, string? errorCode, string? errorSummary) = outcome switch
        {
            PassedPlainExportOutcome passed =>
                ("passed", passed.OutputSize, passed.OutputHash, (string?)null, (string?)null),
            FailedPlainExportOutcome failed =>
                ("failed", 0L, (string?)null, failed.ErrorCode, failed.ErrorSummary),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        return new PlainExportDatabaseResult
        {
            Slot = candidate.Slot,
            SourceIndex = candidate.SourceIndex,
            SourceName = candidate.SourceName,
            SourceSnapshotFiles = candidate.Database.Snapshot.Files.ToList(),
            OutputPath = status == "passed" ? $"{candidate.Slot}/{candidate.SourceName}" : null,
            OutputSize = outputSize,
            OutputSha256 = outputHash,
            Status = status,
            Stage = ToProtocolValue(stage),
            Compatibility = candidate.Database.Compatibility,
            PageSize = candidate.Database.PageSize,
            SourceUserVersion = source?.UserVersion ?? 0,
            TargetUserVersion = target?.UserVersion ?? 0,
            SourceSchemaObjects = source?.Schema.Count ?? 0,
            TargetSchemaObjects = target?.Schema.Count ?? 0,
            TablesChecked = source?.RowCounts.Count ?? 0,
            RowCountMismatches = comparison?.RowCountMismatches.ToList() ?? [],
            SchemaMismatches = comparison?.SchemaMismatches.ToList() ?? [],
            IntegrityCheck = target?.IntegrityCheck,
            DurationMs = duration,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary
        };
    }

    private abstract record PlainExportOutcome;

    private sealed record PassedPlainExportOutcome(long OutputSize, string OutputHash) : PlainExportOutcome;

    private sealed record FailedPlainExportOutcome(string ErrorCode, string ErrorSummary) : PlainExportOutcome;

    private static void ApplyToSession(DatabaseManifest database, PlainExportDatabaseResult result)
    {
        database.PlaintextExport.Status = result.Status;
        database.PlaintextExport.Stage = result.Stage;
        database.PlaintextExport.OutputPath = result.OutputPath is null ? null : "plain-db/" + result.OutputPath;
        database.PlaintextExport.ManifestPath = "plain-db/manifest.json";
        database.PlaintextExport.OutputSha256 = result.OutputSha256;
        database.PlaintextExport.ErrorCode = result.ErrorCode;
        database.PlaintextExport.ErrorSummary = result.ErrorSummary;
    }

    private static async Task<PlainExportSummary?> ReadPreviousSummaryAsync(string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<PlainExportSummary>(stream, TargetProfile.JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FileStream AcquireLock(string rootDirectory)
    {
        try
        {
            return new FileStream(Path.Combine(rootDirectory, ".export.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException("Another plaintext export is already running for this session.", error);
        }
    }

    private static string ToProtocolValue(PlainExportStage stage) => stage switch
    {
        PlainExportStage.Filter => "filter",
        PlainExportStage.SourceCheck => "source-check",
        PlainExportStage.SourceBaseline => "source-baseline",
        PlainExportStage.Export => "export",
        PlainExportStage.OpenPlaintext => "open-plaintext",
        PlainExportStage.Schema => "schema",
        PlainExportStage.Publish => "publish",
        PlainExportStage.Manifest => "manifest",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown plaintext export stage.")
    };
}
