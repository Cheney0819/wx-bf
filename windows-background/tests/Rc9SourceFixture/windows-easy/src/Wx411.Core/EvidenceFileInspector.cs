using System.Security.Cryptography;

namespace Wx411.Core;

public interface IEvidenceFileInspector
{
    Task<EvidenceFileRecord> InspectAsync(
        string path,
        bool verifySqlite,
        CancellationToken cancellationToken);

    EvidenceDirectorySnapshot CaptureDirectory(string path);
}

public sealed class EvidenceFileInspector : IEvidenceFileInspector
{
    private const int BufferSize = 1024 * 1024;
    private readonly EvidenceTemporaryDirectoryCleaner _temporaryDirectoryCleaner;
    private readonly Action<string, string>? _privateCopyReady;

    public EvidenceFileInspector()
        : this(new EvidenceTemporaryDirectoryCleaner())
    {
    }

    internal EvidenceFileInspector(
        EvidenceTemporaryDirectoryCleaner temporaryDirectoryCleaner,
        Action<string, string>? privateCopyReady = null)
    {
        _temporaryDirectoryCleaner = temporaryDirectoryCleaner ??
                                     throw new ArgumentNullException(nameof(temporaryDirectoryCleaner));
        _privateCopyReady = privateCopyReady;
    }

    public async Task<EvidenceFileRecord> InspectAsync(
        string path,
        bool verifySqlite,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return MissingFile(fullPath);

        try
        {
            var before = new FileInfo(fullPath);
            var length = before.Length;
            var lastWriteUtc = before.LastWriteTimeUtc;
            var sqliteEvidence = verifySqlite
                ? await InspectPrivateSqliteCopyAsync(fullPath, cancellationToken)
                : null;
            var sha256 = sqliteEvidence?.Sha256 ?? await ComputeFileSha256Async(fullPath, cancellationToken);
            var evidenceLength = sqliteEvidence?.Length ?? length;

            var after = new FileInfo(fullPath);
            after.Refresh();
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != lastWriteUtc)
            {
                return new EvidenceFileRecord(
                    fullPath,
                    after.Exists,
                    evidenceLength,
                    sha256,
                    sqliteEvidence?.Status,
                    Error: JoinInspectionErrors(
                        "file_changed_during_inspection",
                        sqliteEvidence?.Error));
            }

            if (!verifySqlite)
                return new EvidenceFileRecord(fullPath, true, length, sha256, null, null);

            return new EvidenceFileRecord(
                fullPath,
                true,
                evidenceLength,
                sha256,
                sqliteEvidence!.Status,
                sqliteEvidence.Error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsInspectableFileError(ex))
        {
            return new EvidenceFileRecord(
                fullPath,
                File.Exists(fullPath),
                TryGetLength(fullPath),
                Sha256: null,
                IntegrityCheck: verifySqlite ? "failed" : null,
                Error: FormatError(ex));
        }
    }

    public EvidenceDirectorySnapshot CaptureDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var capturedUtc = DateTimeOffset.UtcNow;
        if (!Directory.Exists(fullPath))
        {
            return new EvidenceDirectorySnapshot(
                fullPath,
                capturedUtc,
                Array.Empty<EvidenceDirectoryEntry>(),
                "directory_not_found");
        }

        try
        {
            var entries = Directory
                .EnumerateFileSystemEntries(fullPath, "*", SearchOption.TopDirectoryOnly)
                .Select(CreateDirectoryEntry)
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            return new EvidenceDirectorySnapshot(
                fullPath,
                capturedUtc,
                Array.AsReadOnly(entries),
                Error: null);
        }
        catch (Exception ex) when (IsInspectableFileError(ex))
        {
            return new EvidenceDirectorySnapshot(
                fullPath,
                capturedUtc,
                Array.Empty<EvidenceDirectoryEntry>(),
                FormatError(ex));
        }
    }

    internal static bool IsTemporaryArtifact(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".tmp-wal", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".tmp-shm", StringComparison.OrdinalIgnoreCase);
    }

    private static EvidenceDirectoryEntry CreateDirectoryEntry(string path)
    {
        var isDirectory = Directory.Exists(path);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        var name = info.Name;
        return new EvidenceDirectoryEntry(
            name,
            isDirectory,
            isDirectory ? null : ((FileInfo)info).Length,
            new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)),
            IsTemporaryArtifact(name));
    }

    private async Task<PrivateSqliteEvidence> InspectPrivateSqliteCopyAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "Wx411Easy-evidence-check-" + Guid.NewGuid().ToString("N"));
        var temporaryDatabase = Path.Combine(temporaryDirectory, "database.sqlite");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryDatabase,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, BufferSize, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            _privateCopyReady?.Invoke(sourcePath, temporaryDatabase);
            var snapshotLength = new FileInfo(temporaryDatabase).Length;
            var snapshotSha256 = await ComputeFileSha256Async(temporaryDatabase, cancellationToken);
            try
            {
                await Task.Run(
                    () => SqliteIntegrityChecker.VerifyFile(temporaryDatabase, cancellationToken),
                    cancellationToken);
                return new PrivateSqliteEvidence(snapshotLength, snapshotSha256, "ok", Error: null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IntegrityException or IOException or UnauthorizedAccessException)
            {
                return new PrivateSqliteEvidence(snapshotLength, snapshotSha256, "failed", FormatError(ex));
            }
        }
        finally
        {
            SqliteSidecarCleaner.DeleteForTemporaryDatabase(temporaryDatabase);
            _temporaryDirectoryCleaner.Delete(temporaryDirectory);
        }
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] digest;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete,
                         BufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            digest = await SHA256.HashDataAsync(stream, cancellationToken);
        }

        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static string JoinInspectionErrors(string primary, string? secondary) =>
        string.IsNullOrWhiteSpace(secondary) ? primary : primary + "; " + secondary;

    private static EvidenceFileRecord MissingFile(string path) => new(
        path,
        Exists: false,
        Length: null,
        Sha256: null,
        IntegrityCheck: null,
        Error: "file_not_found");

    private static long? TryGetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (Exception ex) when (IsInspectableFileError(ex))
        {
            return null;
        }
    }

    private static bool IsInspectableFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static string FormatError(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private sealed record PrivateSqliteEvidence(
        long Length,
        string Sha256,
        string Status,
        string? Error);
}

internal sealed class EvidenceTemporaryDirectoryCleaner
{
    private const int MaximumAttempts = 3;
    private readonly Func<string, bool> _directoryExists;
    private readonly Action<string, bool> _deleteDirectory;
    private readonly Action<int> _delay;

    public EvidenceTemporaryDirectoryCleaner()
        : this(
            Directory.Exists,
            Directory.Delete,
            milliseconds => Thread.Sleep(milliseconds))
    {
    }

    internal EvidenceTemporaryDirectoryCleaner(
        Func<string, bool> directoryExists,
        Action<string, bool> deleteDirectory,
        Action<int> delay)
    {
        _directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        _deleteDirectory = deleteDirectory ?? throw new ArgumentNullException(nameof(deleteDirectory));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                if (!_directoryExists(path)) return;
                _deleteDirectory(path, true);
                if (!_directoryExists(path)) return;
                lastError = new IOException("Private evidence directory still exists after deletion.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
            }

            if (attempt < MaximumAttempts) _delay(50 * attempt);
        }

        throw new IOException(
            $"Private SQLite evidence-copy cleanup failed after {MaximumAttempts} attempts: {path}",
            lastError);
    }
}
