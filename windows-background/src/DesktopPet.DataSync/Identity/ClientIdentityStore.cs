using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DesktopPet.Background.Infrastructure;

namespace DesktopPet.DataSync.Identity;

public sealed partial class ClientIdentityStore : IClientIdentityProvider
{
    private const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _path;
    private readonly string? _legacyPath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClientIdentityStore(
        string path,
        string? legacyPath,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _path = Path.GetFullPath(path);
        _legacyPath = string.IsNullOrWhiteSpace(legacyPath) ? null : Path.GetFullPath(legacyPath);
        _timeProvider = timeProvider;
    }

    public async Task<ClientIdentityDocument> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path)) return await ReadCurrentAsync(cancellationToken);

            var identity = await TryReadLegacyAsync(cancellationToken) ?? new ClientIdentityDocument(
                1,
                $"client-datasync-{Guid.NewGuid():N}",
                "client_datasync",
                _timeProvider.GetUtcNow());
            Validate(identity);
            var json = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
            await AtomicFile.ReplaceAsync(_path, json, cancellationToken);
            return identity;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ClientIdentityDocument> ReadCurrentAsync(
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(_path);
        if (info.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("Client identity file has an invalid size.");
        var json = await File.ReadAllBytesAsync(_path, cancellationToken);
        try
        {
            ClientIdentityDocument identity;
            try
            {
                identity = JsonSerializer.Deserialize<ClientIdentityDocument>(json, JsonOptions) ??
                    throw new InvalidDataException("Client identity file is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Client identity JSON is invalid.", exception);
            }
            Validate(identity);
            return identity;
        }
        finally
        {
            Array.Clear(json);
        }
    }

    private async Task<ClientIdentityDocument?> TryReadLegacyAsync(
        CancellationToken cancellationToken)
    {
        if (_legacyPath is null || !File.Exists(_legacyPath)) return null;
        var info = new FileInfo(_legacyPath);
        if (info.Length is <= 0 or > MaximumBytes) return null;
        try
        {
            var json = await File.ReadAllBytesAsync(_legacyPath, cancellationToken);
            try
            {
                var legacy = JsonSerializer.Deserialize<LegacyIdentity>(json, JsonOptions);
                if (legacy is null || string.IsNullOrWhiteSpace(legacy.SessionId)) return null;
                var createdAt = DateTimeOffset.TryParse(legacy.CreatedAt, out var parsed)
                    ? parsed
                    : _timeProvider.GetUtcNow();
                return new ClientIdentityDocument(
                    1,
                    NormalizeSessionId(legacy.SessionId),
                    "client_cs",
                    createdAt);
            }
            finally
            {
                Array.Clear(json);
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Validate(ClientIdentityDocument identity)
    {
        if (identity.SchemaVersion != 1)
            throw new InvalidDataException("Client identity schema is unsupported.");
        var normalizedSessionId = NormalizeSessionId(identity.SessionId);
        if (!string.Equals(normalizedSessionId, identity.SessionId, StringComparison.Ordinal))
            throw new InvalidDataException("Stored client session ID is not normalized.");
        if (!SourcePattern().IsMatch(identity.Source))
            throw new InvalidDataException("Client source is invalid.");
        if (identity.CreatedAtUtc == default)
            throw new InvalidDataException("Client identity creation time is invalid.");
    }

    private static string NormalizeSessionId(string value)
    {
        var normalized = WhitespacePattern().Replace(value.Trim(), "_");
        if (normalized.Length is < 1 or > 120 || normalized.Any(char.IsControl))
            throw new InvalidDataException("Client session ID is invalid.");
        return normalized;
    }

    [GeneratedRegex("^[a-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SourcePattern();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    private sealed record LegacyIdentity(
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("created_at")] string? CreatedAt);
}
