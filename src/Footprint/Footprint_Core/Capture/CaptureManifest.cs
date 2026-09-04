using System.Text.Json;
using System.Text.Json.Serialization;

namespace Footprint.Core.Capture;

[JsonConverter(typeof(JsonStringEnumConverter<CaptureSourceCategory>))]
public enum CaptureSourceCategory
{
    Database,
    Image,
    Voice,
    Favorite,
    Attachment,
    Decompression
}

public sealed record CaptureManifestEntry(
    [property: JsonPropertyName("relative_path"), JsonPropertyOrder(0)] string RelativePath,
    [property: JsonPropertyName("length"), JsonPropertyOrder(1)] long Length,
    [property: JsonPropertyName("sha256"), JsonPropertyOrder(2)] string Sha256,
    [property: JsonPropertyName("source_category"), JsonPropertyOrder(3)] CaptureSourceCategory SourceCategory,
    [property: JsonPropertyName("source_identity_hash"), JsonPropertyOrder(4)] string SourceIdentityHash,
    [property: JsonPropertyName("snapshot_timestamp_utc"), JsonPropertyOrder(5)] DateTimeOffset SnapshotTimestampUtc,
    [property: JsonPropertyName("stability_attempts"), JsonPropertyOrder(6)] int StabilityAttempts,
    [property: JsonPropertyName("association_evidence"), JsonPropertyOrder(7)]
    IReadOnlyDictionary<string, string> AssociationEvidence);

public sealed record CaptureManifest
{
    [JsonPropertyName("schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = "footprint.capture-manifest.v1";

    [JsonPropertyName("run_id"), JsonPropertyOrder(1)]
    public string RunId { get; init; } = string.Empty;

    [JsonPropertyName("device_id"), JsonPropertyOrder(2)]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("capture_generation"), JsonPropertyOrder(3)]
    public long CaptureGeneration { get; init; }

    [JsonPropertyName("created_at_utc"), JsonPropertyOrder(4)]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("entries"), JsonPropertyOrder(5)]
    public IReadOnlyList<CaptureManifestEntry> Entries { get; init; } = Array.Empty<CaptureManifestEntry>();
}

public static class CaptureDeviceIdContract
{
    public static bool IsValid(string? value) => value is { Length: >= 1 and <= 128 } &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}

public sealed class CaptureManifestPublisher
{
    private readonly CaptureWorkspace _workspace;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CaptureManifestPublisher(CaptureWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public async Task PublishAsync(CaptureManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = NormalizeAndValidate(manifest);
        await VerifySnapshotFilesAsync(normalized.Entries, cancellationToken);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        if (File.Exists(_workspace.ManifestPath))
        {
            var existing = await File.ReadAllBytesAsync(_workspace.ManifestPath, cancellationToken);
            if (existing.AsSpan().SequenceEqual(bytes)) return;
            throw new InvalidOperationException("Published capture manifest bytes are immutable.");
        }

        await AtomicFile.WriteAsync(_workspace.ManifestPath, async (stream, token) =>
        {
            await stream.WriteAsync(bytes, token);
        }, cancellationToken, (temporaryPath, finalPath) => PublishImmutable(temporaryPath, finalPath, bytes));
    }

    private CaptureManifest NormalizeAndValidate(CaptureManifest manifest)
    {
        if (manifest.RunId is null || manifest.RunId.Length != "Footprint_Run_".Length + 32 ||
            !manifest.RunId.StartsWith("Footprint_Run_", StringComparison.Ordinal) ||
            manifest.RunId["Footprint_Run_".Length..].Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Capture manifest Run ID is invalid.");
        if (!string.Equals(manifest.RunId, _workspace.RunId, StringComparison.Ordinal))
            throw new InvalidDataException("Capture manifest Run ID does not match its workspace.");
        if (!CaptureDeviceIdContract.IsValid(manifest.DeviceId))
            throw new InvalidDataException("Capture manifest DeviceId is invalid.");
        if (manifest.CaptureGeneration < 1)
            throw new InvalidDataException("Capture generation must be positive.");
        if (manifest.CreatedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Capture manifest timestamp must be UTC.");
        if (!string.Equals(manifest.Schema, "footprint.capture-manifest.v1", StringComparison.Ordinal))
            throw new InvalidDataException("Capture manifest schema is invalid.");
        if (manifest.Entries is null)
            throw new InvalidDataException("Capture manifest entries are required.");

        var normalizedEntries = new List<CaptureManifestEntry>(manifest.Entries.Count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            if (entry is null) throw new InvalidDataException("Capture manifest entries cannot be null.");
            ValidateEntry(entry);
            var normalizedPath = entry.RelativePath.Replace('\\', '/');
            if (!paths.Add(normalizedPath))
                throw new InvalidDataException("Capture manifest contains a duplicate relative path.");
            normalizedEntries.Add(entry with
            {
                RelativePath = normalizedPath,
                Sha256 = entry.Sha256.ToLowerInvariant(),
                SourceIdentityHash = entry.SourceIdentityHash.ToLowerInvariant(),
                SnapshotTimestampUtc = entry.SnapshotTimestampUtc.ToUniversalTime(),
                AssociationEvidence = new SortedDictionary<string, string>(
                    entry.AssociationEvidence.ToDictionary(pair => pair.Key, pair => pair.Value,
                        StringComparer.Ordinal), StringComparer.Ordinal)
            });
        }

        return manifest with
        {
            CreatedAtUtc = manifest.CreatedAtUtc.ToUniversalTime(),
            Entries = normalizedEntries
                .OrderBy(entry => CategoryOrder(entry.SourceCategory))
                .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private async Task VerifySnapshotFilesAsync(IEnumerable<CaptureManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path;
            try
            {
                path = _workspace.ResolveRelativePath(entry.RelativePath);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Capture manifest entry path is outside its workspace.", exception);
            }

            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length != entry.Length)
                    throw new InvalidDataException("Capture manifest entry length does not match its snapshot file.");
                var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream,
                    cancellationToken)).ToLowerInvariant();
                if (stream.Length != entry.Length ||
                    !string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Capture manifest entry hash does not match its snapshot file.");
            }
            catch (FileNotFoundException exception)
            {
                throw new InvalidDataException("Capture manifest entry snapshot file is missing.", exception);
            }
            catch (DirectoryNotFoundException exception)
            {
                throw new InvalidDataException("Capture manifest entry snapshot file is missing.", exception);
            }
        }
    }

    private static void ValidateEntry(CaptureManifestEntry entry)
    {
        if (!CaptureWorkspace.IsSafeRelativePath(entry.RelativePath))
            throw new InvalidDataException("Capture manifest entry path is unsafe.");
        var normalized = entry.RelativePath.Replace('\\', '/');
        var expectedPrefix = entry.SourceCategory switch
        {
            CaptureSourceCategory.Database => "Footprint_Databases/",
            CaptureSourceCategory.Decompression => "Footprint_Decompression/",
            _ => $"Footprint_MediaSnapshot/{SnapshotPlan.CategoryName(entry.SourceCategory)}/"
        };
        if (!normalized.StartsWith(expectedPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("Capture manifest entry path does not match its category.");
        if (entry.Length < 0 || entry.StabilityAttempts < 1 || !IsSha256(entry.Sha256) ||
            !IsSha256(entry.SourceIdentityHash) || entry.SnapshotTimestampUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Capture manifest entry fingerprint is invalid.");
        if (entry.AssociationEvidence is null || entry.AssociationEvidence.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new InvalidDataException("Capture manifest association evidence is invalid.");
    }

    private static bool IsSha256(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static int CategoryOrder(CaptureSourceCategory category) => category switch
    {
        CaptureSourceCategory.Database => 0,
        CaptureSourceCategory.Image => 1,
        CaptureSourceCategory.Voice => 2,
        CaptureSourceCategory.Favorite => 3,
        CaptureSourceCategory.Attachment => 4,
        CaptureSourceCategory.Decompression => 5,
        _ => throw new InvalidDataException("Capture source category is invalid.")
    };

    private static void PublishImmutable(string temporaryPath, string finalPath, ReadOnlySpan<byte> expected)
    {
        try
        {
            File.Move(temporaryPath, finalPath);
            return;
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            var existing = File.ReadAllBytes(finalPath);
            if (existing.AsSpan().SequenceEqual(expected))
            {
                File.Delete(temporaryPath);
                return;
            }
        }

        throw new InvalidOperationException("Published capture manifest bytes are immutable.");
    }
}
