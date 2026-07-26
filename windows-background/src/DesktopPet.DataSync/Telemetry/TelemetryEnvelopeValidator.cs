using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopPet.Background.Contracts;

namespace DesktopPet.DataSync.Telemetry;

public sealed class TelemetryEnvelopeValidator
{
    private const int MaximumEnvelopeBytes = 64 * 1024;
    private const int MaximumMetricBytes = 8 * 1024;
    private const int MaximumMetricCount = 32;
    private const long MaximumMetricInteger = 1_000_000_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };
    private static readonly HashSet<string> ExpectedMembers = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "eventId",
        "component",
        "eventName",
        "severity",
        "code",
        "occurredAtUtc",
        "metrics",
    };
    private static readonly HashSet<string> Severities =
        new(StringComparer.Ordinal) { "info", "warning", "error" };
    private static readonly string[] UnsafeMetricNameParts =
    [
        "account",
        "address",
        "authorization",
        "credential",
        "cookie",
        "directory",
        "host",
        "http",
        "key",
        "memory",
        "password",
        "path",
        "process",
        "secret",
        "token",
        "url",
        "uri",
    ];

    public async Task<OperationalTelemetryEnvelope> ValidateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            MaximumEnvelopeBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumEnvelopeBytes)
            throw new InvalidDataException("Telemetry envelope has an invalid size.");

        var content = new byte[checked((int)stream.Length)];
        try
        {
            await stream.ReadExactlyAsync(content, cancellationToken);

            try
            {
                using var document = JsonDocument.Parse(content, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
                ValidateExactMembers(document.RootElement);
                var envelope = JsonSerializer.Deserialize<OperationalTelemetryEnvelope>(content, JsonOptions) ??
                    throw new InvalidDataException("Telemetry envelope is empty.");
                if (!string.Equals(
                        Path.GetFileName(fullPath),
                        envelope.EventId + ".json",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Telemetry filename does not match its event identity.");
                }
                Validate(envelope);
                return envelope with { Metrics = envelope.Metrics.Clone() };
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Telemetry envelope JSON is invalid.", exception);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static void ValidateExactMembers(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Telemetry envelope must be a JSON object.");
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!ExpectedMembers.Contains(property.Name) || !members.Add(property.Name))
                throw new InvalidDataException("Telemetry envelope members are invalid.");
        }
        if (members.Count != ExpectedMembers.Count)
            throw new InvalidDataException("Telemetry envelope is missing a required member.");
    }

    public static void Validate(OperationalTelemetryEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SchemaVersion != 1)
            throw new InvalidDataException("Telemetry envelope schema is unsupported.");
        if (!IsLowerSha256(envelope.EventId))
            throw new InvalidDataException("Telemetry event identity is invalid.");
        ValidateIdentifier(envelope.Component, 32, "component");
        ValidateIdentifier(envelope.EventName, 64, "event name");
        ValidateIdentifier(envelope.Code, 32, "code");
        if (!Severities.Contains(envelope.Severity))
            throw new InvalidDataException("Telemetry severity is invalid.");
        if (envelope.OccurredAtUtc == default || envelope.OccurredAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Telemetry occurrence time is invalid.");
        ValidateMetrics(envelope.Metrics);
    }

    private static void ValidateMetrics(JsonElement metrics)
    {
        if (metrics.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Telemetry metrics must be a JSON object.");
        var count = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in metrics.EnumerateObject())
        {
            count++;
            if (count > MaximumMetricCount || !names.Add(metric.Name))
                throw new InvalidDataException("Telemetry metrics are invalid.");
            if (!IsAsciiMetricName(metric.Name) || UnsafeMetricNameParts.Any(part =>
                    metric.Name.Contains(part, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Telemetry metric name is unsafe.");
            }
            ValidateMetricValue(metric.Name, metric.Value);
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(metrics);
        try
        {
            if (content.Length > MaximumMetricBytes)
                throw new InvalidDataException("Telemetry metrics exceed 8 KiB.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static void ValidateMetricValue(string name, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var integer) &&
            integer is >= 0 and <= MaximumMetricInteger)
        {
            return;
        }
        if (value.ValueKind == JsonValueKind.String && IsVersionMetric(name, value.GetString())) return;
        throw new InvalidDataException("Telemetry metric value is invalid.");
    }

    private static bool IsVersionMetric(string name, string? value) =>
        name.EndsWith("Version", StringComparison.Ordinal) &&
        !string.IsNullOrEmpty(value) &&
        value.Length <= 32 &&
        char.IsAsciiDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.');

    private static bool IsAsciiMetricName(string name) =>
        name.Length is >= 1 and <= 64 &&
        char.IsAsciiLetter(name[0]) &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static void ValidateIdentifier(string? value, int maximumLength, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            !IsAsciiLower(value[0]) ||
            value.Any(character =>
                !(IsAsciiLower(character) || char.IsAsciiDigit(character) || character == '_')))
        {
            throw new InvalidDataException($"Telemetry {label} is invalid.");
        }
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static bool IsAsciiLower(char value) => value is >= 'a' and <= 'z';
}
