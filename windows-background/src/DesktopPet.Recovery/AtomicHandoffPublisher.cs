using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopPet.Background.Contracts;
using DesktopPet.Background.Infrastructure;

namespace DesktopPet.Recovery;

public sealed record HandoffPublicationResult(
    DatabaseReadyManifest Manifest,
    bool WasPublished);

public sealed class AtomicHandoffPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _generationRoot;
    private readonly string _readyRoot;
    private readonly string _allowedSourceRoot;
    private readonly TimeProvider _timeProvider;

    public AtomicHandoffPublisher(
        string generationRoot,
        string readyRoot,
        string allowedSourceRoot,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(readyRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedSourceRoot);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _generationRoot = Path.GetFullPath(generationRoot);
        _readyRoot = Path.GetFullPath(readyRoot);
        _allowedSourceRoot = Path.GetFullPath(allowedSourceRoot);
        _timeProvider = timeProvider;
    }

    public async Task<DatabaseReadyManifest> PublishAsync(
        string epochId,
        IReadOnlyList<RecoveredDatabase> databases,
        CancellationToken cancellationToken) =>
        (await PublishWithStatusAsync(
            epochId,
            databases,
            requiredDatabasesComplete: true,
            cancellationToken)).Manifest;

    public async Task<DatabaseReadyManifest> PublishAsync(
        string epochId,
        IReadOnlyList<RecoveredDatabase> databases,
        bool requiredDatabasesComplete,
        CancellationToken cancellationToken) =>
        (await PublishWithStatusAsync(
            epochId,
            databases,
            requiredDatabasesComplete,
            cancellationToken)).Manifest;

    public async Task<HandoffPublicationResult> PublishWithStatusAsync(
        string epochId,
        IReadOnlyList<RecoveredDatabase> databases,
        CancellationToken cancellationToken) =>
        await PublishWithStatusAsync(
            epochId,
            databases,
            requiredDatabasesComplete: true,
            cancellationToken);

    public async Task<HandoffPublicationResult> PublishWithStatusAsync(
        string epochId,
        IReadOnlyList<RecoveredDatabase> databases,
        bool requiredDatabasesComplete,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epochId);
        ArgumentNullException.ThrowIfNull(databases);
        if (databases.Count == 0)
            throw new ArgumentException("At least one recovered database is required.", nameof(databases));
        cancellationToken.ThrowIfCancellationRequested();

        var items = new List<DatabaseReadyItem>(databases.Count);
        foreach (var database in databases
                     .OrderBy(item => NormalizeRelativePath(item.RelativePath), StringComparer.Ordinal)
                     .ThenBy(item => item.Sha256, StringComparer.OrdinalIgnoreCase))
        {
            ValidateDatabase(database);
            var normalizedRelativePath = NormalizeRelativePath(database.RelativePath);
            var actualSourceHash = await FileSha256Async(
                database.PlaintextPath,
                cancellationToken);
            if (!string.Equals(actualSourceHash, database.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Recovered database hash does not match its manifest input.");
            var handoffGenerationId = HandoffGenerationId(
                epochId,
                normalizedRelativePath,
                actualSourceHash);

            var destinationDirectory = Path.Combine(
                _generationRoot,
                handoffGenerationId);
            var destination = Path.Combine(
                destinationDirectory,
                SafeOutputName(database.RelativePath));
            await CopyImmutableAsync(
                database.PlaintextPath,
                destination,
                database.Sha256,
                cancellationToken);
            items.Add(new DatabaseReadyItem(
                handoffGenerationId,
                normalizedRelativePath,
                destination,
                actualSourceHash));
        }

        var manifestId = ManifestId(
            epochId,
            items,
            requiredDatabasesComplete);
        var manifestPath = Path.Combine(_readyRoot, manifestId + ".json");
        if (File.Exists(manifestPath))
        {
            var existingBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            try
            {
                var existing = JsonSerializer.Deserialize<DatabaseReadyManifest>(
                    existingBytes,
                    JsonOptions) ?? throw new InvalidDataException("Existing handoff manifest is invalid.");
                if (existing.SchemaVersion != 2 ||
                    existing.ManifestId != manifestId ||
                    existing.EpochId != epochId ||
                    existing.RequiredDatabasesComplete != requiredDatabasesComplete)
                {
                    throw new InvalidDataException("Existing handoff manifest identity is invalid.");
                }
                return new HandoffPublicationResult(existing, WasPublished: false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(existingBytes);
            }
        }

        var manifest = new DatabaseReadyManifest(
            SchemaVersion: 2,
            manifestId,
            epochId,
            _timeProvider.GetUtcNow(),
            Array.AsReadOnly(items.ToArray()),
            requiredDatabasesComplete);
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        try
        {
            await AtomicFile.ReplaceAsync(manifestPath, json, cancellationToken);
            return new HandoffPublicationResult(manifest, WasPublished: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private void ValidateDatabase(RecoveredDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        ValidateSha256(database.GenerationId, nameof(database.GenerationId));
        ValidateSha256(database.Sha256, nameof(database.Sha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(database.RelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(database.PlaintextPath);
        var normalizedRelative = NormalizeRelativePath(database.RelativePath);
        if (IsPortableRooted(database.RelativePath) || IsParentTraversal(normalizedRelative))
            throw new ArgumentException("Database relative path must remain below its data root.", nameof(database));
        if (!IsBelowRoot(database.PlaintextPath, _allowedSourceRoot))
            throw new InvalidOperationException("Recovered database source is outside the allowed staging root.");
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 hex string.", parameterName);
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');

    private static bool IsParentTraversal(string relativePath) =>
        NormalizeRelativePath(relativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals("..", StringComparison.Ordinal));

    private static bool IsPortableRooted(string path) =>
        Path.IsPathRooted(path) ||
        path.StartsWith('/') ||
        path.StartsWith('\\') ||
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    private static bool IsBelowRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && !IsParentTraversal(relative);
    }

    private static string SafeOutputName(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        var name = Path.GetFileNameWithoutExtension(leaf);
        if (string.IsNullOrWhiteSpace(name)) name = "database";
        return name + ".readable.sqlite";
    }

    private static string ManifestId(
        string epochId,
        IReadOnlyList<DatabaseReadyItem> items,
        bool requiredDatabasesComplete)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{epochId}|requiredDatabasesComplete={requiredDatabasesComplete}|" + string.Join(
                "|",
                items.Select(item =>
                    $"{item.GenerationId}:{item.RelativePath}:{item.Sha256}")));
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

    private static string HandoffGenerationId(
        string epochId,
        string relativePath,
        string contentSha256)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{epochId}|{relativePath}|{contentSha256.ToLowerInvariant()}");
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

    private static async Task CopyImmutableAsync(
        string source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            var existingHash = await FileSha256Async(destination, cancellationToken);
            if (!string.Equals(existingHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Immutable generation file has conflicting content.");
            return;
        }

        var directory = Path.GetDirectoryName(destination) ??
            throw new InvalidOperationException("Generation destination has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = new FileStream(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var copiedHash = await FileSha256Async(temporary, cancellationToken);
            if (!string.Equals(copiedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Copied generation hash does not match its source.");
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of an unpublished generation temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; the publish operation still reports its primary failure.
        }
    }
}
