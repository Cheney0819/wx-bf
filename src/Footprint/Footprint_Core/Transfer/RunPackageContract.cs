using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Footprint.Core.Capture;

namespace Footprint.Core.Transfer;

public static partial class RunPackageContract
{
    private const int MaximumCaptureManifestBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions CaptureManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public const string PackageSha256Header = "X-Footprint-Package-Sha256";
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string DeviceIdHeader = "X-Footprint-Device-Id";
    public const string ReceiptSignatureHeader = "X-Footprint-Receipt-Signature";
    public const string VolumeCountHeader = "X-Footprint-Volume-Count";
    public const int MaximumVolumeCount = 100_000;
    public const string CompletionMessageZh = "本次运行已上传并完成源数据清理。";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RunIdPattern();

    public static string ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!RunIdPattern().IsMatch(runId))
            throw new ArgumentException("Run ID must be one safe globally unique route segment.", nameof(runId));
        return runId;
    }

    public static string CanonicalRunId(string runId)
    {
        var validated = ValidateRunId(runId);
        const string prefix = "Footprint_Run_";
        if (!validated.StartsWith(prefix, StringComparison.Ordinal)) return validated;
        if (!IsCaptureRunId(validated))
            throw new ArgumentException(
                "Run IDs beginning with Footprint_Run_ must match the exact capture workspace format.",
                nameof(runId));
        return validated[prefix.Length..];
    }

    public static string PackageFileName(string runId) => $"Footprint_Run_{CanonicalRunId(runId)}.zip";

    public static string UploadPath(string runId) => $"/api/footprint/runs/{CanonicalRunId(runId)}/package";

    public static string VolumeFileName(string runId, int volumeNumber, int totalVolumes)
    {
        ValidateVolumeIdentity(volumeNumber, totalVolumes);
        return $"Footprint_Run_{CanonicalRunId(runId)}.part-{volumeNumber:D6}-of-{totalVolumes:D6}.zip";
    }

    public static string VolumeUploadPath(string runId, int volumeNumber)
    {
        if (volumeNumber is < 1 or > MaximumVolumeCount)
            throw new ArgumentOutOfRangeException(nameof(volumeNumber));
        return $"/api/footprint/runs/{CanonicalRunId(runId)}/volumes/{volumeNumber}";
    }

    public static string CreateIdempotencyKey(string runId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalRunId(runId)))).ToLowerInvariant();

    public static string CreateVolumeIdempotencyKey(string runId, int volumeNumber, int totalVolumes)
    {
        ValidateVolumeIdentity(volumeNumber, totalVolumes);
        var identity = $"{CanonicalRunId(runId)}\n{volumeNumber}\n{totalVolumes}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public static void ValidateVolumeIdentity(int volumeNumber, int totalVolumes)
    {
        if (totalVolumes is < 1 or > MaximumVolumeCount)
            throw new ArgumentOutOfRangeException(nameof(totalVolumes));
        if (volumeNumber is < 1 || volumeNumber > totalVolumes)
            throw new ArgumentOutOfRangeException(nameof(volumeNumber));
    }

    public static Task<IReadOnlyList<RunPackageSource>> CreateSourcesFromManifestAsync(string expectedRunId,
        string captureRunRoot, CancellationToken cancellationToken) =>
        CreateSourcesFromManifestCoreAsync(expectedRunId, captureRunRoot, null, cancellationToken);

    public static async Task<IReadOnlyList<RunPackageSource>> CreateSourcesFromManifestAsync(string expectedRunId,
        string captureRunRoot, string expectedDeviceId, CancellationToken cancellationToken)
    {
        if (!CaptureDeviceIdContract.IsValid(expectedDeviceId))
            throw new InvalidDataException("Expected Device ID contract is invalid.");
        return await CreateSourcesFromManifestCoreAsync(expectedRunId, captureRunRoot, expectedDeviceId,
            cancellationToken);
    }

    private static async Task<IReadOnlyList<RunPackageSource>> CreateSourcesFromManifestCoreAsync(
        string expectedRunId, string captureRunRoot, string? expectedDeviceId, CancellationToken cancellationToken)
    {
        ValidateRunId(expectedRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(captureRunRoot);
        var root = Path.GetFullPath(captureRunRoot);
        if (!Directory.Exists(root)) throw new InvalidDataException("Capture Run root is missing.");
        var manifestPath = ResolveCapturePath(root, "Footprint_CaptureManifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("Published capture manifest is missing.");
        var manifestBytes = await ReadBoundedManifestAsync(manifestPath, cancellationToken);
        CaptureManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            var required = new HashSet<string>(
                ["schema", "run_id", "device_id", "capture_generation", "created_at_utc", "entries"], StringComparer.Ordinal);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(required) is false)
                throw new InvalidDataException("Published capture manifest fields are invalid.");
            manifest = JsonSerializer.Deserialize<CaptureManifest>(manifestBytes, CaptureManifestJsonOptions) ??
                       throw new InvalidDataException("Published capture manifest is empty.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Published capture manifest JSON is invalid.", exception);
        }
        if (!string.Equals(manifest.Schema, "footprint.capture-manifest.v1", StringComparison.Ordinal) ||
            !IsCaptureRunId(manifest.RunId) ||
            !string.Equals(manifest.RunId, expectedRunId, StringComparison.Ordinal) ||
            !CaptureDeviceIdContract.IsValid(manifest.DeviceId) ||
            expectedDeviceId is not null &&
            !string.Equals(manifest.DeviceId, expectedDeviceId, StringComparison.Ordinal) ||
            manifest.CaptureGeneration < 1 ||
            manifest.CreatedAtUtc.Offset != TimeSpan.Zero || manifest.Entries is null)
            throw new InvalidDataException("Published capture manifest contract is invalid.");

        var sources = new List<RunPackageSource>(manifest.Entries.Count + 1);
        sources.Add(new RunPackageSource("Footprint_CaptureManifest.json", manifestPath, manifestBytes.LongLength,
            Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(), root));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Footprint_CaptureManifest.json" };
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null) throw new InvalidDataException("Capture manifest entries cannot be null.");
            var normalized = entry.RelativePath?.Replace('\\', '/') ?? string.Empty;
            var expectedPrefix = entry.SourceCategory switch
            {
                CaptureSourceCategory.Database => "Footprint_Databases/",
                CaptureSourceCategory.Decompression => "Footprint_Decompression/",
                CaptureSourceCategory.Image => "Footprint_MediaSnapshot/image/",
                CaptureSourceCategory.Voice => "Footprint_MediaSnapshot/voice/",
                CaptureSourceCategory.Favorite => "Footprint_MediaSnapshot/favorite/",
                CaptureSourceCategory.Attachment => "Footprint_MediaSnapshot/attachment/",
                _ => throw new InvalidDataException("Capture manifest source category is invalid.")
            };
            if (!CaptureWorkspace.IsSafeRelativePath(normalized) ||
                !normalized.StartsWith(expectedPrefix, StringComparison.Ordinal) || entry.Length < 0 ||
                entry.StabilityAttempts < 1 || entry.SnapshotTimestampUtc.Offset != TimeSpan.Zero ||
                entry.AssociationEvidence is null)
                throw new InvalidDataException("Capture manifest contains a non-package source path.");
            if (!paths.Add(normalized)) throw new InvalidDataException("Capture manifest contains a duplicate package path.");
            var sourcePath = ResolveCapturePath(root, normalized);
            _ = ValidateSha256(entry.SourceIdentityHash, nameof(entry.SourceIdentityHash));
            sources.Add(new RunPackageSource(normalized, sourcePath, entry.Length,
                ValidateSha256(entry.Sha256, nameof(entry.Sha256)), root));
        }
        return sources;
    }

    public static string ValidateSha256(string sha256, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64 || sha256.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
            throw new ArgumentException("SHA-256 must be 64 lowercase hexadecimal characters.", parameterName);
        return sha256;
    }

    private static async Task<byte[]> ReadBoundedManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length is <= 0 or > MaximumCaptureManifestBytes)
            throw new InvalidDataException("Published capture manifest size is invalid.");
        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.Length != length) throw new InvalidDataException("Published capture manifest changed while reading.");
        return bytes;
    }

    private static string ResolveCapturePath(string root, string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new InvalidDataException("Capture manifest path is unsafe.");
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Capture manifest path escapes its Run root.");
        RejectReparsePoints(root, path);
        return path;
    }

    private static void RejectReparsePoints(string root, string path)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Capture package source path cannot traverse a link or reparse point.");
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Capture package source path cannot traverse a link or reparse point.");
        }
    }

    private static bool IsCaptureRunId(string runId) =>
        runId is not null && runId.Length == "Footprint_Run_".Length + 32 &&
        runId.StartsWith("Footprint_Run_", StringComparison.Ordinal) &&
        runId["Footprint_Run_".Length..].All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

public sealed record RunPackageSource
{
    public RunPackageSource(string relativePath, string sourcePath, long length, string sha256,
        string? containmentRoot = null)
    {
        RelativePath = relativePath ?? throw new ArgumentNullException(nameof(relativePath));
        SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
        Sha256 = RunPackageContract.ValidateSha256(sha256, nameof(sha256));
        ContainmentRoot = containmentRoot is null ? null : Path.GetFullPath(containmentRoot);
    }

    public string RelativePath { get; init; }
    public string SourcePath { get; init; }
    public long Length { get; init; }
    public string Sha256 { get; init; }
    public string? ContainmentRoot { get; init; }
}

public sealed record RunPackageArtifact
{
    public RunPackageArtifact(string runId, string packagePath, long packageLength, string packageSha256,
        string idempotencyKey)
    {
        RunId = RunPackageContract.CanonicalRunId(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        PackagePath = Path.GetFullPath(packagePath);
        if (packageLength < 0) throw new ArgumentOutOfRangeException(nameof(packageLength));
        PackageLength = packageLength;
        PackageSha256 = RunPackageContract.ValidateSha256(packageSha256, nameof(packageSha256));
        IdempotencyKey = RunPackageContract.ValidateSha256(idempotencyKey, nameof(idempotencyKey));
        var expected = RunPackageContract.CreateIdempotencyKey(RunId);
        if (!string.Equals(IdempotencyKey, expected, StringComparison.Ordinal))
            throw new ArgumentException("Idempotency key is not bound to the Run ID.", nameof(idempotencyKey));
    }

    public string RunId { get; }
    public string PackagePath { get; }
    public long PackageLength { get; }
    public string PackageSha256 { get; }
    public string IdempotencyKey { get; }
    public string UploadPath => RunPackageContract.UploadPath(RunId);
}

public sealed record RunPackageVolumeArtifact
{
    public RunPackageVolumeArtifact(string runId, int volumeNumber, int totalVolumes, string packagePath,
        long packageLength, string packageSha256, string idempotencyKey)
    {
        RunId = RunPackageContract.CanonicalRunId(runId);
        RunPackageContract.ValidateVolumeIdentity(volumeNumber, totalVolumes);
        VolumeNumber = volumeNumber;
        TotalVolumes = totalVolumes;
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        PackagePath = Path.GetFullPath(packagePath);
        if (packageLength < 0) throw new ArgumentOutOfRangeException(nameof(packageLength));
        PackageLength = packageLength;
        PackageSha256 = RunPackageContract.ValidateSha256(packageSha256, nameof(packageSha256));
        IdempotencyKey = RunPackageContract.ValidateSha256(idempotencyKey, nameof(idempotencyKey));
        var expected = RunPackageContract.CreateVolumeIdempotencyKey(RunId, volumeNumber, totalVolumes);
        if (!string.Equals(IdempotencyKey, expected, StringComparison.Ordinal))
            throw new ArgumentException("Idempotency key is not bound to the Run volume identity.",
                nameof(idempotencyKey));
    }

    public string RunId { get; }
    public int VolumeNumber { get; }
    public int TotalVolumes { get; }
    public string PackagePath { get; }
    public long PackageLength { get; }
    public string PackageSha256 { get; }
    public string IdempotencyKey { get; }
    public string UploadPath => RunPackageContract.VolumeUploadPath(RunId, VolumeNumber);
}
