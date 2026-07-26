using System.Security.Cryptography;
using System.Text.Json;
using DesktopPet.Background.Contracts;

namespace DesktopPet.Background.Infrastructure;

public sealed class AtomicTelemetryPublisher : IOperationalTelemetryPublisher
{
    private const int MaximumEnvelopeBytes = 64 * 1024;
    private const int MaximumMetricBytes = 8 * 1024;
    private const int MaximumMetricCount = 32;
    private const long MaximumMetricInteger = 1_000_000_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
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
    private static readonly HashSet<string> Severities =
        new(StringComparer.Ordinal) { "info", "warning", "error" };

    private readonly string _readyRoot;
    private readonly TimeProvider _timeProvider;

    public AtomicTelemetryPublisher(string readyRoot, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readyRoot);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _readyRoot = Path.GetFullPath(readyRoot);
        _timeProvider = timeProvider;
    }

    public async Task PublishAsync(
        OperationalTelemetryDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateIdentifier(draft.Component, nameof(draft.Component), 32);
        ValidateIdentifier(draft.EventName, nameof(draft.EventName), 64);
        ValidateIdentifier(draft.Code, nameof(draft.Code), 32);
        if (!Severities.Contains(draft.Severity))
            throw new ArgumentException("Telemetry severity is invalid.", nameof(draft));
        ValidateMetrics(draft.Metrics);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_readyRoot);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var eventId = CreateEventId();
            var envelope = new OperationalTelemetryEnvelope(
                1,
                eventId,
                draft.Component,
                draft.EventName,
                draft.Severity,
                draft.Code,
                _timeProvider.GetUtcNow().ToUniversalTime(),
                draft.Metrics.Clone());
            var content = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            if (content.Length > MaximumEnvelopeBytes)
                throw new ArgumentException("Telemetry envelope exceeds 64 KiB.", nameof(draft));

            var destination = Path.Combine(_readyRoot, eventId + ".json");
            var temporary = Path.Combine(_readyRoot, $".{eventId}.{Guid.NewGuid():N}.tmp");
            try
            {
                await WriteTemporaryAsync(temporary, content, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
                return;
            }
            catch (IOException) when (File.Exists(destination))
            {
                // A random identity collision gets a fresh identity; existing events are immutable.
            }
            finally
            {
                CryptographicOperations.ZeroMemory(content);
                TryDelete(temporary);
            }
        }

        throw new IOException("Unable to allocate a unique telemetry event identity.");
    }

    private static async Task WriteTemporaryAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateMetrics(JsonElement metrics)
    {
        if (metrics.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Telemetry metrics must be a JSON object.", nameof(metrics));

        var count = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in metrics.EnumerateObject())
        {
            count++;
            if (count > MaximumMetricCount)
                throw new ArgumentException("Telemetry metrics exceed 32 fields.", nameof(metrics));
            if (!names.Add(metric.Name))
                throw new ArgumentException("Telemetry metrics contain a duplicate field.", nameof(metrics));
            ValidateMetricName(metric.Name);
            ValidateMetricValue(metric.Name, metric.Value);
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(metrics);
        try
        {
            if (content.Length > MaximumMetricBytes)
                throw new ArgumentException("Telemetry metrics exceed 8 KiB.", nameof(metrics));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static void ValidateMetricName(string name)
    {
        if (!IsAsciiMetricName(name) || UnsafeMetricNameParts.Any(part =>
                name.Contains(part, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Telemetry metric name is unsafe.", nameof(name));
    }

    private static bool IsAsciiMetricName(string name) =>
        name.Length is >= 1 and <= 64 &&
        char.IsAsciiLetter(name[0]) &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static void ValidateMetricValue(string name, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                return;

            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer) && integer is >= 0 and <= MaximumMetricInteger)
                    return;
                break;

            case JsonValueKind.String:
                if (IsVersionMetric(name, value.GetString())) return;
                break;
        }

        throw new ArgumentException(
            "Telemetry metric value must be a bounded boolean, integer, or version.",
            nameof(value));
    }

    private static bool IsVersionMetric(string name, string? value)
    {
        return name.EndsWith("Version", StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(value) &&
            value.Length <= 32 &&
            char.IsAsciiDigit(value[0]) &&
            value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '.');
    }

    private static void ValidateIdentifier(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || !IsAsciiLower(value[0]) ||
            value.Any(character => !(IsAsciiLower(character) || char.IsAsciiDigit(character) || character == '_')))
            throw new ArgumentException("Telemetry identifier is invalid.", parameterName);
    }

    private static bool IsAsciiLower(char value) => value is >= 'a' and <= 'z';

    private static string CreateEventId()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
