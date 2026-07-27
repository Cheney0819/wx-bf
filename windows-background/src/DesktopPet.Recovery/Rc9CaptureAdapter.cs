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
    private Func<RecoveryEpoch, IReadOnlyList<DatabaseSource>, CaptureContext> _createCaptureContext = null!;
    private RecoveryProcessSelection _boundProcess = null!;
    private RecoveryProcessSelection _restartedProcess = null!;

    [SupportedOSPlatform("windows")]
    public Rc9CaptureAdapter(
        string dataRoot,
        string outputDirectory,
        string pendingCaptureRoot,
        ValidatedKeyVault validatedKeyVault,
        IProgress<RecoveryProgress> progress)
        : this(
            dataRoot,
            outputDirectory,
            pendingCaptureRoot,
            validatedKeyVault,
            boundProcess: null,
            progress)
    {
    }

    [SupportedOSPlatform("windows")]
    public Rc9CaptureAdapter(
        string dataRoot,
        string outputDirectory,
        string pendingCaptureRoot,
        ValidatedKeyVault validatedKeyVault,
        WeChatRuntimeIdentity runtime,
        IProgress<RecoveryProgress> progress)
        : this(
            dataRoot,
            outputDirectory,
            pendingCaptureRoot,
            validatedKeyVault,
            BoundProcessSelection(runtime, dataRoot),
            progress)
    {
    }

    [SupportedOSPlatform("windows")]
    private Rc9CaptureAdapter(
        string dataRoot,
        string outputDirectory,
        string pendingCaptureRoot,
        ValidatedKeyVault validatedKeyVault,
        RecoveryProcessSelection? boundProcess,
        IProgress<RecoveryProgress> progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingCaptureRoot);
        ArgumentNullException.ThrowIfNull(validatedKeyVault);
        boundProcess ??= new RecoveryProcessSelection(null, "Automatic", ScanAll: true);
        Initialize(
            dataRoot,
            outputDirectory,
            progress,
            () => DatabaseSourceDiscovery.Discover([Path.GetFullPath(dataRoot)]),
            (epoch, databases) =>
            {
                var scopeRoot = Path.Combine(
                    Path.GetFullPath(pendingCaptureRoot),
                    PendingScopeId(epoch.Identity.DataRootIdentity, epoch.Id));
                var pendingVault = new PendingCaptureVault(
                    scopeRoot,
                    new WindowsDpapiProtector());
                var service = new CallpointCaptureRecoveryService(
                    static () => new DebugCaptureBackend(),
                    pendingVault,
                    validatedKeyVault);
                var fingerprints = CurrentDatabaseSaltFingerprints(databases);
                return new CaptureContext(
                    () => pendingVault.SnapshotRecordIds(fingerprints),
                    service.CaptureAndDecryptAsync);
            },
            boundProcess);
    }

    internal Rc9CaptureAdapter(
        string dataRoot,
        string outputDirectory,
        IProgress<RecoveryProgress> progress,
        Func<IReadOnlyList<DatabaseSource>> discoverDatabases,
        Func<IReadOnlyList<string>> snapshotPendingIds,
        Rc9CaptureOperation capture,
        RecoveryProcessSelection? boundProcess = null) =>
        Initialize(
            dataRoot,
            outputDirectory,
            progress,
            discoverDatabases,
            (_, _) => new CaptureContext(snapshotPendingIds, capture),
            boundProcess ?? new RecoveryProcessSelection(null, "Automatic", ScanAll: true));

    public Task<CaptureObservation> CaptureAsync(
        RecoveryEpoch epoch,
        CancellationToken cancellationToken) =>
        CaptureAsync(epoch, RecoveryCaptureTarget.BoundProcess, cancellationToken);

    public async Task<CaptureObservation> CaptureAsync(
        RecoveryEpoch epoch,
        RecoveryCaptureTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        cancellationToken.ThrowIfCancellationRequested();
        var databases = _discoverDatabases();
        if (databases.Count == 0)
            return Failure(
                "capture_no_database_candidates",
                hasPending: false,
                candidateDatabaseCount: 0);

        var scopeMatches = string.Equals(
            epoch.Identity.DataRootIdentity,
            DataRootIdentity(_dataRoot),
            StringComparison.OrdinalIgnoreCase);
        var context = _createCaptureContext(epoch, databases);

        IReadOnlyList<string> pendingBefore;
        try
        {
            pendingBefore = scopeMatches
                ? context.SnapshotPendingIds()
                : [];
        }
        catch (Exception exception) when (IsExpectedCaptureException(exception))
        {
            return Failure("capture_pending_vault_error", hasPending: false);
        }

        try
        {
            var result = await context.Capture(
                target == RecoveryCaptureTarget.RestartedProcess
                    ? _restartedProcess
                    : _boundProcess,
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
                    HasPendingCapture: HasPendingAfter(context, pendingBefore, scopeMatches) ||
                        result.LoadedPendingCaptureTicketIds.Count > 0,
                    OutputPaths: [],
                    FailureCode: "capture_result_mapping_failed",
                    CandidateDatabaseCount: databases.Count,
                    UnmatchedDatabasePaths: result.UnmatchedDatabasePaths,
                    FailedDatabasePaths: result.FailedDatabasePaths,
                    RequiredDatabasesComplete: RequiredDatabasesComplete(
                        databases,
                        result));
            }

            return new CaptureObservation(
                HasValidatedKey: result.OutputPaths.Count > 0,
                HasPendingCapture: HasPendingAfter(context, pendingBefore, scopeMatches) ||
                    result.LoadedPendingCaptureTicketIds.Count > 0,
                result.OutputPaths,
                FailureCode: null,
                recovered,
                databases.Count,
                result.UnmatchedDatabasePaths,
                result.FailedDatabasePaths,
                RequiredDatabasesComplete(databases, result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedCaptureException(exception))
        {
            return Failure(
                FailureCode(exception),
                HasPendingAfter(context, pendingBefore, scopeMatches),
                databases.Count);
        }
    }

    private static bool RequiredDatabasesComplete(
        IReadOnlyList<DatabaseSource> databases,
        CaptureRecoveryResult result)
    {
        var requiredPaths = databases
            .Where(database => database.IsRequired)
            .Select(database => Path.GetFullPath(database.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return result.UnmatchedDatabasePaths
                .Concat(result.FailedDatabasePaths)
                .All(path => !requiredPaths.Contains(Path.GetFullPath(path)));
    }

    private void Initialize(
        string dataRoot,
        string outputDirectory,
        IProgress<RecoveryProgress> progress,
        Func<IReadOnlyList<DatabaseSource>> discoverDatabases,
        Func<RecoveryEpoch, IReadOnlyList<DatabaseSource>, CaptureContext> createCaptureContext,
        RecoveryProcessSelection boundProcess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(discoverDatabases);
        ArgumentNullException.ThrowIfNull(createCaptureContext);
        ArgumentNullException.ThrowIfNull(boundProcess);
        _dataRoot = Path.GetFullPath(dataRoot);
        _outputDirectory = Path.GetFullPath(outputDirectory);
        _progress = progress;
        _discoverDatabases = discoverDatabases;
        _createCaptureContext = createCaptureContext;
        _boundProcess = boundProcess;
        _restartedProcess = boundProcess with
        {
            Pid = null,
            ScanAll = true,
        };
    }

    private static RecoveryProcessSelection BoundProcessSelection(
        WeChatRuntimeIdentity runtime,
        string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.ProcessId <= 0 || runtime.SessionId < 0)
            throw new ArgumentOutOfRangeException(nameof(runtime));
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime.ExecutablePath);
        var expectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var runtimeRoot = string.IsNullOrWhiteSpace(runtime.DataRoot)
            ? expectedRoot
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtime.DataRoot));
        if (!string.Equals(
                expectedRoot,
                runtimeRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Runtime identity is bound to a different data root.",
                nameof(runtime));
        }

        var executablePath = Path.GetFullPath(runtime.ExecutablePath);
        return new RecoveryProcessSelection(
            runtime.ProcessId,
            Path.GetFileName(executablePath),
            ScanAll: false,
            runtime.SessionId,
            executablePath);
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

    private static bool HasPendingAfter(
        CaptureContext context,
        IReadOnlyList<string> pendingBefore,
        bool scopeMatches)
    {
        if (!scopeMatches) return false;
        try
        {
            var after = context.SnapshotPendingIds();
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

    private static string FailureCode(Exception exception)
    {
        if (exception is UnsupportedModuleException)
            return UnsupportedModuleException.StableCode;
        if (exception is InvalidOperationException invalid)
        {
            if (invalid.Message.Contains(
                    UnsupportedModuleException.StableCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                return UnsupportedModuleException.StableCode;
            }

            if (invalid.Message.Contains(
                    "breakpoint_restore_failed",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "breakpoint_restore_failed";
            }
            if (invalid.Message.Contains(
                    "early-attach:module-timeout",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "capture_module_timeout";
            }

            if (invalid.Message.Contains(
                    "early-attach:capture-timeout",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "capture_callpoint_timeout";
            }
        }

        return exception switch
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
    }

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

    private static string PendingScopeId(string dataRootIdentity, string epochId) =>
        TextSha256($"{dataRootIdentity}|{epochId}");

    private static string DataRootIdentity(string dataRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return TextSha256(normalized);
    }

    private static string TextSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static IReadOnlyList<string> CurrentDatabaseSaltFingerprints(
        IReadOnlyList<DatabaseSource> databases)
    {
        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var database in databases)
        {
            var salt = new byte[16];
            try
            {
                using var stream = new FileStream(
                    database.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var offset = 0;
                while (offset < salt.Length)
                {
                    var read = stream.Read(salt, offset, salt.Length - offset);
                    if (read == 0) break;
                    offset += read;
                }
                if (offset != salt.Length) continue;
                fingerprints.Add(Convert.ToHexString(SHA256.HashData(salt)).ToLowerInvariant());
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
            }
        }
        return fingerprints.OrderBy(value => value, StringComparer.Ordinal).ToArray();
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

    private sealed record CaptureContext(
        Func<IReadOnlyList<string>> SnapshotPendingIds,
        Rc9CaptureOperation Capture);
}
