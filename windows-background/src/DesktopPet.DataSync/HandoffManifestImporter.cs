using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopPet.Background.Contracts;
using DesktopPet.DataSync.Persistence;

namespace DesktopPet.DataSync;

public sealed class IncompleteHandoffException : Exception
{
    public IncompleteHandoffException()
        : base("Recovery handoff is waiting for all required databases.")
    {
    }
}

public sealed class HandoffManifestImporter
{
    private const long MaximumManifestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly DataSyncRepository _repository;
    private readonly string _generationRoot;
    private readonly IHandoffAcceptancePublisher _acceptancePublisher;
    private readonly TimeProvider _timeProvider;

    public HandoffManifestImporter(
        DataSyncRepository repository,
        string generationRoot,
        IHandoffAcceptancePublisher acceptancePublisher,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationRoot);
        ArgumentNullException.ThrowIfNull(acceptancePublisher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _repository = repository;
        _generationRoot = Path.GetFullPath(generationRoot);
        _acceptancePublisher = acceptancePublisher;
        _timeProvider = timeProvider;
    }

    public async Task<HandoffImportResult> ImportAsync(
        string readyManifestPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readyManifestPath);
        cancellationToken.ThrowIfCancellationRequested();
        var fullManifestPath = Path.GetFullPath(readyManifestPath);
        var info = new FileInfo(fullManifestPath);
        if (!info.Exists) throw new FileNotFoundException("Ready manifest does not exist.", fullManifestPath);
        if (info.Length > MaximumManifestBytes)
            throw new InvalidDataException("Ready manifest exceeds 1 MiB.");

        var json = await File.ReadAllBytesAsync(fullManifestPath, cancellationToken);
        try
        {
            DatabaseReadyManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<DatabaseReadyManifest>(json, JsonOptions) ??
                    throw new InvalidDataException("Ready manifest is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Ready manifest JSON is invalid.", exception);
            }

            var validated = await ValidateAsync(
                fullManifestPath,
                manifest,
                cancellationToken);
            if (!validated.RequiredDatabasesComplete)
                throw new IncompleteHandoffException();
            var result = await _repository.ImportHandoffAsync(validated, cancellationToken);
            await _acceptancePublisher.PublishAsync(
                new HandoffAcceptedMarker(
                    1,
                    result.ManifestId,
                    result.SourceSetId,
                    _timeProvider.GetUtcNow()),
                cancellationToken);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private async Task<ValidatedHandoffManifest> ValidateAsync(
        string manifestPath,
        DatabaseReadyManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.SchemaVersion != 2)
            throw new InvalidDataException("Unsupported Recovery handoff schema.");
        ValidateSha256(manifest.ManifestId, "manifest ID");
        if (string.IsNullOrWhiteSpace(manifest.EpochId) || manifest.EpochId.Length > 256)
            throw new InvalidDataException("Recovery epoch ID is invalid.");
        if (!string.Equals(
                Path.GetFileNameWithoutExtension(manifestPath),
                manifest.ManifestId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Ready manifest filename does not match its identity.");
        }
        if (manifest.Databases is null || manifest.Databases.Count == 0)
            throw new InvalidDataException("Ready manifest contains no databases.");
        if (manifest.Databases.Count > 10_000)
            throw new InvalidDataException("Ready manifest contains too many databases.");

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<ValidatedHandoffDatabase>(manifest.Databases.Count);
        foreach (var item in manifest.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null) throw new InvalidDataException("Ready manifest contains a null database.");
            ValidateSha256(item.GenerationId, "generation ID");
            ValidateSha256(item.Sha256, "database SHA-256");
            var relativePath = NormalizeRelativePath(item.RelativePath);
            if (string.IsNullOrWhiteSpace(relativePath) ||
                IsPortableRooted(item.RelativePath) ||
                ContainsParentTraversal(relativePath))
            {
                throw new InvalidDataException("Database relative path is unsafe.");
            }
            if (!seenPaths.Add(relativePath))
                throw new InvalidDataException("Ready manifest contains a duplicate relative path.");

            if (string.IsNullOrWhiteSpace(item.PlaintextPath) || IsPortableUnc(item.PlaintextPath))
                throw new InvalidDataException("Database plaintext path is invalid.");
            var plaintextPath = Path.GetFullPath(item.PlaintextPath);
            var generationDirectory = Path.Combine(_generationRoot, item.GenerationId);
            if (!IsBelowRoot(plaintextPath, generationDirectory))
                throw new InvalidDataException("Database plaintext path is outside its immutable generation.");
            if (!File.Exists(plaintextPath))
                throw new FileNotFoundException("Immutable database generation does not exist.", plaintextPath);

            var actualSha256 = await FileSha256Async(plaintextPath, cancellationToken);
            if (!string.Equals(actualSha256, item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Immutable database generation hash mismatch.");
            var expectedGeneration = ComputeSha256(
                $"{manifest.EpochId}|{relativePath}|{actualSha256.ToLowerInvariant()}");
            if (!string.Equals(expectedGeneration, item.GenerationId, StringComparison.Ordinal))
                throw new InvalidDataException("Database generation ID is invalid.");

            validated.Add(new ValidatedHandoffDatabase(
                item.GenerationId,
                relativePath,
                plaintextPath,
                actualSha256));
        }

        var expectedManifestId = ComputeSha256(
            $"{manifest.EpochId}|requiredDatabasesComplete={manifest.RequiredDatabasesComplete}|" +
            string.Join(
                "|",
                validated.Select(item =>
                    $"{item.GenerationId}:{item.RelativePath}:{item.Sha256}")));
        if (!string.Equals(expectedManifestId, manifest.ManifestId, StringComparison.Ordinal))
            throw new InvalidDataException("Recovery manifest ID is invalid.");

        return new ValidatedHandoffManifest(
            manifest.ManifestId,
            manifest.EpochId,
            manifest.CreatedAtUtc,
            Array.AsReadOnly(validated.ToArray()),
            manifest.RequiredDatabasesComplete);
    }

    private static void ValidateSha256(string? value, string label)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"Recovery {label} is invalid.");
    }

    private static string NormalizeRelativePath(string? relativePath) =>
        (relativePath ?? string.Empty).Replace('\\', '/');

    private static bool ContainsParentTraversal(string relativePath) =>
        relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals("..", StringComparison.Ordinal));

    private static bool IsPortableRooted(string? path) =>
        !string.IsNullOrEmpty(path) &&
        (Path.IsPathRooted(path) ||
         path.StartsWith('/') ||
         path.StartsWith('\\') ||
         path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static bool IsPortableUnc(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    private static bool IsBelowRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) &&
            !ContainsParentTraversal(relative.Replace('\\', '/')) &&
            relative != ".";
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

    private static string ComputeSha256(string material)
    {
        var bytes = Encoding.UTF8.GetBytes(material);
        var digest = SHA256.HashData(bytes);
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(digest);
        }
    }
}
