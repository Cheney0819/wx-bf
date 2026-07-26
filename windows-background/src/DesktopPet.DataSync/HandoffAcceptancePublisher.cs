using System.Security.Cryptography;
using System.Text.Json;
using DesktopPet.Background.Infrastructure;

namespace DesktopPet.DataSync;

public sealed record HandoffAcceptedMarker(
    int SchemaVersion,
    string ManifestId,
    string SourceSetId,
    DateTimeOffset AcceptedAtUtc);

public interface IHandoffAcceptancePublisher
{
    Task PublishAsync(
        HandoffAcceptedMarker marker,
        CancellationToken cancellationToken);
}

public sealed class HandoffAcceptancePublisher : IHandoffAcceptancePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _acceptedRoot;
    private readonly TimeProvider _timeProvider;

    public HandoffAcceptancePublisher(string acceptedRoot, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptedRoot);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _acceptedRoot = Path.GetFullPath(acceptedRoot);
        _timeProvider = timeProvider;
    }

    public async Task PublishAsync(
        HandoffAcceptedMarker marker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(marker);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.Combine(_acceptedRoot, marker.ManifestId + ".json");
        if (File.Exists(destination))
        {
            var existingBytes = await File.ReadAllBytesAsync(destination, cancellationToken);
            try
            {
                var existing = JsonSerializer.Deserialize<HandoffAcceptedMarker>(
                    existingBytes,
                    JsonOptions) ?? throw new InvalidDataException("Accepted marker is invalid.");
                if (existing.SchemaVersion != 1 ||
                    existing.ManifestId != marker.ManifestId ||
                    existing.SourceSetId != marker.SourceSetId)
                {
                    throw new InvalidDataException("Accepted marker identity conflicts with committed state.");
                }
                return;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(existingBytes);
            }
        }

        var committedMarker = marker with { AcceptedAtUtc = _timeProvider.GetUtcNow() };
        var json = JsonSerializer.SerializeToUtf8Bytes(committedMarker, JsonOptions);
        try
        {
            await AtomicFile.ReplaceAsync(destination, json, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }
}
