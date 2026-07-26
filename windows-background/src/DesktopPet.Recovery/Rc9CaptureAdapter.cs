using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;
using DesktopPet.Recovery.Security;
using Wx411.Core;
using Wx411.Core.Windows;

namespace DesktopPet.Recovery;

internal delegate Task<CaptureRecoveryResult> Rc9CaptureOperation(
    RecoveryProcessSelection process,
    DatabaseSource selectedDatabase,
    IReadOnlyList<DatabaseSource> databases,
    string outputDirectory,
    IProgress<RecoveryProgress> progress,
    CancellationToken cancellationToken);

public sealed class Rc9CaptureAdapter : IRecoveryCaptureAdapter
{
    private string _dataRoot = null!;
    private string _outputDirectory = null!;
    private IProgress<RecoveryProgress> _progress = null!;
    private Func<IReadOnlyList<DatabaseSource>> _discoverDatabases = null!;
    private Func<IReadOnlyList<string>> _snapshotPendingIds = null!;
    private Rc9CaptureOperation _capture = null!;

    [SupportedOSPlatform("windows")]
    public Rc9CaptureAdapter(
        string dataRoot,
        string outputDirectory,
        string pendingCaptureRoot,
        ValidatedKeyVault validatedKeyVault,
        IProgress<RecoveryProgress> progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingCaptureRoot);
        ArgumentNullException.ThrowIfNull(validatedKeyVault);
        var pendingVault = new PendingCaptureVault(
            pendingCaptureRoot,
            new WindowsDpapiProtector());
        var service = new CallpointCaptureRecoveryService(
            static () => new DebugCaptureBackend(),
            pendingVault,
            validatedKeyVault);
        Initialize(
            dataRoot,
            outputDirectory,
            progress,
            () => DatabaseSourceDiscovery.Discover([Path.GetFullPath(dataRoot)]),
            pendingVault.SnapshotRecordIds,
            service.CaptureAndDecryptAsync);
    }

    internal Rc9CaptureAdapter(
        string dataRoot,
        string outputDirectory,
        IProgress<RecoveryProgress> progress,
        Func<IReadOnlyList<DatabaseSource>> discoverDatabases,
        Func<IReadOnlyList<string>> snapshotPendingIds,
        Rc9CaptureOperation capture) =>
        Initialize(
            dataRoot,
            outputDirectory,
            progress,
            discoverDatabases,
            snapshotPendingIds,
            capture);

    public async Task<CaptureObservation> CaptureAsync(
        RecoveryEpoch epoch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        cancellationToken.ThrowIfCancellationRequested();
        var databases = _discoverDatabases();
        if (databases.Count == 0)
            return Failure(
                "capture_no_database_candidates",
                hasPending: SnapshotHasPending(),
                candidateDatabaseCount: 0);

        IReadOnlyList<string> pendingBefore;
        try
        {
            pendingBefore = _snapshotPendingIds();
        }
        catch (Exception exception) when (IsExpectedCaptureException(exception))
        {
            return Failure("capture_pending_vault_error", hasPending: false);
        }

        try
        {
            var result = await _capture(
                new RecoveryProcessSelection(null, "Automatic", ScanAll: true),
                databases[0],
                databases,
                _outputDirectory,
                _progress,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var recovered = await BuildRecoveredAsync(
                epoch.Id,
                result,
                cancellationToken);
            if (recovered.Count != result.OutputPaths.Count)
            {
                return new CaptureObservation(
                    HasValidatedKey: result.OutputPaths.Count > 0,
                    HasPendingCapture: HasPendingAfter(pendingBefore) ||
                        result.LoadedPendingCaptureTicketIds.Count > 0,
                    OutputPaths: [],
                    FailureCode: "capture_result_mapping_failed",
                    CandidateDatabaseCount: databases.Count);
            }

            return new CaptureObservation(
                HasValidatedKey: result.OutputPaths.Count > 0,
                HasPendingCapture: HasPendingAfter(pendingBefore) ||
                    result.LoadedPendingCaptureTicketIds.Count > 0,
                result.OutputPaths,
                FailureCode: null,
                recovered,
                databases.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedCaptureException(exception))
        {
            return Failure(
                FailureCode(exception),
                HasPendingAfter(pendingBefore),
                databases.Count);
        }
    }

    private void Initialize(
        string dataRoot,
        string outputDirectory,
        IProgress<RecoveryProgress> progress,
        Func<IReadOnlyList<DatabaseSource>> discoverDatabases,
        Func<IReadOnlyList<string>> snapshotPendingIds,
        Rc9CaptureOperation capture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(discoverDatabases);
        ArgumentNullException.ThrowIfNull(snapshotPendingIds);
        ArgumentNullException.ThrowIfNull(capture);
        _dataRoot = Path.GetFullPath(dataRoot);
        _outputDirectory = Path.GetFullPath(outputDirectory);
        _progress = progress;
        _discoverDatabases = discoverDatabases;
        _snapshotPendingIds = snapshotPendingIds;
        _capture = capture;
    }

    private async Task<IReadOnlyList<RecoveredDatabase>> BuildRecoveredAsync(
        string epochId,
        CaptureRecoveryResult result,
        CancellationToken cancellationToken)
    {
        if (result.OutputPaths.Count != result.Matches.Count) return [];
        var recovered = new List<RecoveredDatabase>(result.OutputPaths.Count);
        for (var index = 0; index < result.OutputPaths.Count; index++)
        {
            var outputPath = Path.GetFullPath(result.OutputPaths[index]);
            var databasePath = Path.GetFullPath(result.Matches[index].DatabaseId);
            var relativePath = Path.GetRelativePath(_dataRoot, databasePath);
            if (Path.IsPathRooted(relativePath) || IsParentTraversal(relativePath)) return [];
            var normalizedRelative = relativePath.Replace('\\', '/');
            var sha256 = await FileSha256Async(outputPath, cancellationToken);
            recovered.Add(new RecoveredDatabase(
                GenerationId(epochId, normalizedRelative, sha256),
                normalizedRelative,
                outputPath,
                sha256));
        }
        return Array.AsReadOnly(recovered.ToArray());
    }

    private bool SnapshotHasPending()
    {
        try
        {
            return _snapshotPendingIds().Count > 0;
        }
        catch (Exception exception) when (IsExpectedCaptureException(exception))
        {
            return false;
        }
    }

    private bool HasPendingAfter(IReadOnlyList<string> pendingBefore)
    {
        try
        {
            var after = _snapshotPendingIds();
            return after.Count > 0 || pendingBefore.Count > 0;
        }
        catch (Exception exception) when (IsExpectedCaptureException(exception))
        {
            return pendingBefore.Count > 0;
        }
    }

    private static CaptureObservation Failure(
        string code,
        bool hasPending,
        int candidateDatabaseCount = 0) =>
        new(
            false,
            hasPending,
            [],
            code,
            CandidateDatabaseCount: candidateDatabaseCount);

    private static bool IsParentTraversal(string relativePath) =>
        relativePath.Equals("..", StringComparison.Ordinal) ||
        relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsExpectedCaptureException(Exception exception) =>
        exception is InvalidOperationException or IOException or UnauthorizedAccessException or
            CryptographicException or Win32Exception or ArgumentException or
            PlatformNotSupportedException or NotSupportedException;

    private static string FailureCode(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "capture_access_denied",
        Win32Exception => "capture_windows_error",
        CryptographicException => "capture_crypto_error",
        IOException => "capture_io_error",
        PlatformNotSupportedException or NotSupportedException => "capture_platform_unsupported",
        ArgumentException => "capture_invalid_input",
        InvalidOperationException => "capture_no_result",
        _ => "capture_failed",
    };

    private static string GenerationId(
        string epochId,
        string relativePath,
        string contentSha256)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{epochId}|{relativePath}|{contentSha256}");
        var digest = SHA256.HashData(material);
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static async Task<string> FileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
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
