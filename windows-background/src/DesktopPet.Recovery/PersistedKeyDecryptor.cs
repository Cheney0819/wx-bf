using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;
using DesktopPet.Recovery.Security;
using Wx411.Core;

namespace DesktopPet.Recovery;

public sealed record PersistedDecryptResult(
    CaptureObservation Observation,
    IReadOnlyList<DatabaseSource> UnresolvedRequiredDatabases)
{
    public bool HasValidatedKey => Observation.HasValidatedKey;
    public bool HasPendingCapture => Observation.HasPendingCapture;
    public IReadOnlyList<string> OutputPaths => Observation.OutputPaths;
    public string? FailureCode => Observation.FailureCode;
    public IReadOnlyList<RecoveredDatabase> Databases => Observation.Databases;
    public int CandidateDatabaseCount => Observation.CandidateDatabaseCount;
}

public sealed class PersistedKeyDecryptor
{
    private readonly IRecoveryRepository _repository;
    private readonly ValidatedKeyVault _vault;
    private readonly ConsistentDatabaseExporter _exporter = new();

    public PersistedKeyDecryptor(
        IRecoveryRepository repository,
        ValidatedKeyVault vault)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(vault);
        _repository = repository;
        _vault = vault;
    }

    public async Task<PersistedDecryptResult> TryDecryptAsync(
        RecoveryEpoch epoch,
        string dataRoot,
        IReadOnlyList<DatabaseSource> databases,
        string outputDirectory,
        IProgress<RecoveryProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(progress);

        var normalizedRoot = Path.GetFullPath(dataRoot);
        var keyIds = _vault.ListIds();
        var outputs = new List<string>();
        var recovered = new List<RecoveredDatabase>();
        var unresolved = new List<DatabaseSource>();
        var hasValidatedKey = false;
        var failureCount = 0;

        foreach (var source in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolvedThisDatabase = false;
            try
            {
                var fullPath = Path.GetFullPath(source.Path);
                var relativePath = Path.GetRelativePath(normalizedRoot, fullPath);
                if (Path.IsPathRooted(relativePath) || IsParentTraversal(relativePath))
                {
                    failureCount++;
                    continue;
                }

                using var descriptor = DatabaseProbeDescriptor.Read(fullPath, cancellationToken);
                if (descriptor.Profiles.Count == 0)
                {
                    failureCount++;
                    continue;
                }
                var generationId = GenerationId(epoch.Id, relativePath, descriptor.Generation);
                var existing = await _repository.GetGenerationAsync(
                    generationId,
                    cancellationToken);
                if (existing is { Status: "completed", OutputPath: not null } &&
                    File.Exists(existing.OutputPath))
                {
                    hasValidatedKey = true;
                    outputs.Add(existing.OutputPath);
                    recovered.Add(new RecoveredDatabase(
                        generationId,
                        relativePath,
                        existing.OutputPath,
                        await FileSha256Async(existing.OutputPath, cancellationToken)));
                    resolvedThisDatabase = true;
                    continue;
                }

                var matchedThisDatabase = false;
                foreach (var keyId in keyIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidatedKeyRecord? stored = null;
                    try
                    {
                        stored = _vault.Load(keyId);
                    }
                    catch (Exception exception) when (exception is
                        IOException or UnauthorizedAccessException or
                        ArgumentException or CryptographicException or
                        InvalidDataException or JsonException)
                    {
                        _vault.Quarantine(keyId);
                        continue;
                    }

                    using (stored)
                    {
                        var match = CipherProfileProbe.FindMatch(
                            descriptor,
                            stored.Key,
                            cancellationToken);
                        if (match is null) continue;

                        hasValidatedKey = true;
                        matchedThisDatabase = true;
                        var exportKey = stored.Key.ToArray();
                        var result = await _exporter.ExportAsync(
                            new DatabaseExportRequest(
                                fullPath,
                                descriptor.Generation,
                                exportKey,
                                match.Profile,
                                outputDirectory),
                            progress,
                            cancellationToken);
                        if (result.Status == DatabaseExportStatus.Completed && result.OutputPath is not null)
                        {
                            outputs.Add(result.OutputPath);
                            var outputSha256 = await FileSha256Async(
                                result.OutputPath,
                                cancellationToken);
                            recovered.Add(new RecoveredDatabase(
                                generationId,
                                relativePath,
                                result.OutputPath,
                                outputSha256));
                            await _repository.RecordGenerationAsync(
                                new DatabaseGenerationState(
                                    generationId,
                                    epoch.Id,
                                    relativePath,
                                    ContentFingerprint(descriptor.Generation),
                                    "completed",
                                    result.OutputPath,
                                    DateTimeOffset.UtcNow),
                                cancellationToken);
                            resolvedThisDatabase = true;
                        }
                        else
                        {
                            failureCount++;
                        }
                        break;
                    }
                }

                if (!matchedThisDatabase && keyIds.Count > 0)
                {
                    progress.Report(new RecoveryProgress(
                        5,
                        $"持久 key 未匹配 {Path.GetFileName(fullPath)}",
                        null));
                }
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or
                ArgumentException or CryptographicException or IntegrityException)
            {
                failureCount++;
            }
            finally
            {
                if (!resolvedThisDatabase) unresolved.Add(source);
            }
        }

        var distinctOutputs = outputs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failureCode = failureCount > 0 && distinctOutputs.Length > 0
            ? "persisted_key_partial_failure"
            : hasValidatedKey && distinctOutputs.Length == 0
                ? "persisted_key_export_failed"
                : !hasValidatedKey
                    ? "persisted_key_no_match"
                    : null;
        return new PersistedDecryptResult(
            new CaptureObservation(
                hasValidatedKey,
                HasPendingCapture: false,
                Array.AsReadOnly(distinctOutputs),
                failureCode,
                Array.AsReadOnly(recovered
                    .DistinctBy(item => item.GenerationId, StringComparer.Ordinal)
                    .ToArray()),
                databases.Count),
            Array.AsReadOnly(unresolved.ToArray()));
    }

    private static bool IsParentTraversal(string relativePath) =>
        relativePath.Equals("..", StringComparison.Ordinal) ||
        relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static string GenerationId(
        string epochId,
        string relativePath,
        DatabaseFileGeneration generation)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{epochId}|{relativePath}|{ContentFingerprint(generation)}");
        try
        {
            return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static string ContentFingerprint(DatabaseFileGeneration generation) =>
        $"{generation.FileIdentity}|{generation.Length}|{generation.LastWriteTimeUtc.Ticks}|{generation.WalFingerprint}|{generation.SharedMemoryFingerprint}";

    private static async Task<string> FileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }
}
