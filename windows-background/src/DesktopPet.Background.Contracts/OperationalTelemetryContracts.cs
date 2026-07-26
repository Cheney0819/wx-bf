using System.Text.Json;

namespace DesktopPet.Background.Contracts;

public sealed record OperationalTelemetryDraft(
    string Component,
    string EventName,
    string Severity,
    string Code,
    JsonElement Metrics);

public sealed record OperationalTelemetryEnvelope(
    int SchemaVersion,
    string EventId,
    string Component,
    string EventName,
    string Severity,
    string Code,
    DateTimeOffset OccurredAtUtc,
    JsonElement Metrics);

public interface IOperationalTelemetryPublisher
{
    Task PublishAsync(
        OperationalTelemetryDraft draft,
        CancellationToken cancellationToken);
}
