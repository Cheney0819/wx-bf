using System.Globalization;
using System.Text;

namespace Footprint.Core.Capture;

public sealed record MediaSnapshotSource(
    string SourcePath,
    string RelativePath,
    CaptureSourceCategory Category,
    string SourceIdentityHash,
    IReadOnlyDictionary<string, string> AssociationEvidence);

public sealed record SnapshotPlanRequest(
    string SourcePath,
    string RelativePath,
    CaptureSourceCategory Category,
    string SourceIdentityHash,
    IReadOnlyDictionary<string, string>? AssociationEvidence);

public delegate Task<IReadOnlyList<CaptureManifestEntry>> SnapshotPlanExecutor(
    SnapshotPlanRequest request,
    CaptureWorkspace workspace,
    int maxAttempts,
    CancellationToken cancellationToken);

public sealed class SnapshotPlan
{
    private readonly IReadOnlyList<SnapshotPlanRequest> _requests;
    private readonly SnapshotPlanExecutor _executor;
    private readonly Func<DateTimeOffset> _utcNow;

    public SnapshotPlan(IEnumerable<SnapshotPlanRequest> requests, SnapshotPlanExecutor? executor = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        _requests = requests.ToArray();
        if (_requests.Any(request => request.Category is not CaptureSourceCategory.Database and
                not CaptureSourceCategory.Image and not CaptureSourceCategory.Voice and
                not CaptureSourceCategory.Favorite and not CaptureSourceCategory.Attachment))
            throw new ArgumentException("Snapshot plan contains an invalid source category.", nameof(requests));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _executor = executor ?? ExecuteRequestAsync;
    }

    public int DatabaseConcurrencyLimit => 1;
    public int MediaConcurrencyLimit => 1;
    public IReadOnlyList<SnapshotPlanRequest> Requests => _requests;

    public static SnapshotPlan Create(IEnumerable<DatabaseBinding> bindings,
        IEnumerable<MediaSnapshotSource> mediaSources, Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(mediaSources);
        var requests = new List<SnapshotPlanRequest>();
        foreach (var binding in bindings.OrderBy(binding => binding.Path, PathComparer()))
        {
            var identity = SourceIdentity(binding.Path);
            requests.Add(new SnapshotPlanRequest(binding.Path,
                $"Footprint_Databases/{identity[..16]}", CaptureSourceCategory.Database, identity,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["database_tag"] = binding.Tag.ToString(CultureInfo.InvariantCulture),
                    ["database_pointer_identity"] = Hashing.Sha256Hex(Encoding.UTF8.GetBytes(binding.DbPointer))
                }));
        }

        foreach (var source in mediaSources.OrderBy(source => CategoryOrder(source.Category))
                     .ThenBy(source => source.SourceIdentityHash, StringComparer.Ordinal))
        {
            if (source.Category == CaptureSourceCategory.Database)
                throw new ArgumentException("Media inventory cannot contain database sources.", nameof(mediaSources));
            var extension = SafeExtension(Path.GetExtension(source.SourcePath));
            requests.Add(new SnapshotPlanRequest(source.SourcePath,
                $"Footprint_MediaSnapshot/{CategoryName(source.Category)}/{source.SourceIdentityHash}{extension}",
                source.Category, source.SourceIdentityHash, source.AssociationEvidence));
        }
        return new SnapshotPlan(requests, utcNow: utcNow);
    }

    public async Task<IReadOnlyList<CaptureManifestEntry>> ExecuteAsync(CaptureWorkspace workspace, int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        using var databaseGate = new SemaphoreSlim(DatabaseConcurrencyLimit, DatabaseConcurrencyLimit);
        using var mediaGate = new SemaphoreSlim(MediaConcurrencyLimit, MediaConcurrencyLimit);
        var tasks = _requests.Select(request => ExecuteBoundedAsync(request,
            request.Category == CaptureSourceCategory.Database ? databaseGate : mediaGate,
            workspace, maxAttempts, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(result => result)
            .OrderBy(entry => CategoryOrder(entry.SourceCategory))
            .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<CaptureManifestEntry>> ExecuteBoundedAsync(SnapshotPlanRequest request,
        SemaphoreSlim gate, CaptureWorkspace workspace, int maxAttempts, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await _executor(request, workspace, maxAttempts, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<CaptureManifestEntry>> ExecuteRequestAsync(SnapshotPlanRequest request,
        CaptureWorkspace workspace, int maxAttempts, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var evidence = request.AssociationEvidence ?? new Dictionary<string, string>();
        if (request.Category == CaptureSourceCategory.Database)
        {
            var directory = workspace.ResolveRelativePath(request.RelativePath);
            var snapshot = await StableSnapshotter.CreateAsync(request.SourcePath, directory, maxAttempts,
                cancellationToken);
            if (!snapshot.Stable)
                throw new IOException("Database source did not remain stable during snapshot.");
            return snapshot.Files.Select(file => new CaptureManifestEntry(
                CombinePortable(request.RelativePath, file.Name), file.Size, file.Sha256,
                request.Category, request.SourceIdentityHash, _utcNow().ToUniversalTime(),
                snapshot.StabilityAttempts, evidence)).ToArray();
        }

        var destination = workspace.ResolveRelativePath(request.RelativePath);
        var fileSnapshot = await StableSnapshotter.CreateFileAsync(request.SourcePath, destination, maxAttempts,
            cancellationToken);
        if (!fileSnapshot.Stable)
            throw new IOException("Media source did not remain stable during snapshot.");
        return
        [
            new CaptureManifestEntry(request.RelativePath.Replace('\\', '/'), fileSnapshot.Size,
                fileSnapshot.Sha256, request.Category, request.SourceIdentityHash,
                _utcNow().ToUniversalTime(), fileSnapshot.StabilityAttempts, evidence)
        ];
    }

    private static void ValidateRequest(SnapshotPlanRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        if (!CaptureWorkspace.IsSafeRelativePath(request.RelativePath))
            throw new InvalidDataException("Snapshot destination path is unsafe.");
        var normalizedPath = request.RelativePath.Replace('\\', '/');
        var expectedPrefix = request.Category == CaptureSourceCategory.Database
            ? "Footprint_Databases/"
            : $"Footprint_MediaSnapshot/{CategoryName(request.Category)}/";
        if (!normalizedPath.StartsWith(expectedPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("Snapshot destination does not match its source category.");
        if (!IsSha256(request.SourceIdentityHash))
            throw new InvalidDataException("Snapshot source identity hash is invalid.");
    }

    private static string SourceIdentity(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        return Hashing.Sha256Hex(Encoding.UTF8.GetBytes(full));
    }

    private static string SafeExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension) || extension.Length > 16 ||
            extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character))) return ".bin";
        return extension.ToLowerInvariant();
    }

    private static string CombinePortable(string left, string right) =>
        left.TrimEnd('/', '\\') + "/" + right.Replace('\\', '/');

    internal static string CategoryName(CaptureSourceCategory category) => category switch
    {
        CaptureSourceCategory.Image => "image",
        CaptureSourceCategory.Voice => "voice",
        CaptureSourceCategory.Favorite => "favorite",
        CaptureSourceCategory.Attachment => "attachment",
        CaptureSourceCategory.Database => "database",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private static int CategoryOrder(CaptureSourceCategory category) => category switch
    {
        CaptureSourceCategory.Database => 0,
        CaptureSourceCategory.Image => 1,
        CaptureSourceCategory.Voice => 2,
        CaptureSourceCategory.Favorite => 3,
        CaptureSourceCategory.Attachment => 4,
        _ => 99
    };

    private static bool IsSha256(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
