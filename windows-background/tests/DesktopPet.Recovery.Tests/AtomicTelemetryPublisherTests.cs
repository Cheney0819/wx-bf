using System.Text.Json;
using DesktopPet.Background.Contracts;
using DesktopPet.Background.Infrastructure;

namespace DesktopPet.Recovery.Tests;

public sealed class AtomicTelemetryPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-telemetry-publisher-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublishCreatesSchemaOneEnvelopeWhoseFilenameMatchesEventIdentity()
    {
        var now = new DateTimeOffset(2026, 7, 26, 8, 9, 10, TimeSpan.Zero);
        var publisher = new AtomicTelemetryPublisher(
            _root,
            new FixedTimeProvider(now));
        var metrics = JsonSerializer.SerializeToElement(new
        {
            databaseCount = 18,
            activeRestartSuppressed = true,
            executableVersion = "4.1.0",
        });

        await publisher.PublishAsync(
            new OperationalTelemetryDraft(
                "recovery",
                "recovery_capture_succeeded",
                "info",
                "key_validated",
                metrics),
            default);

        var path = Assert.Single(Directory.EnumerateFiles(_root));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root), candidate =>
            Path.GetFileName(candidate).StartsWith(".", StringComparison.Ordinal) ||
            candidate.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var root = document.RootElement;
        var eventId = root.GetProperty("eventId").GetString();
        Assert.Matches("^[0-9a-f]{64}$", eventId!);
        Assert.Equal(eventId + ".json", Path.GetFileName(path));
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("recovery", root.GetProperty("component").GetString());
        Assert.Equal("recovery_capture_succeeded", root.GetProperty("eventName").GetString());
        Assert.Equal("info", root.GetProperty("severity").GetString());
        Assert.Equal("key_validated", root.GetProperty("code").GetString());
        Assert.Equal(now, root.GetProperty("occurredAtUtc").GetDateTimeOffset());
        Assert.Equal(18, root.GetProperty("metrics").GetProperty("databaseCount").GetInt32());
        Assert.Equal(
            ["schemaVersion", "eventId", "component", "eventName", "severity", "code", "occurredAtUtc", "metrics"],
            root.EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData("outputPath", "stable")]
    [InlineData("accessToken", "stable")]
    [InlineData("apiKey", "stable")]
    [InlineData("encryptionKey", "stable")]
    [InlineData("accountName", "alice")]
    [InlineData("resultCode", "redacted_key_example_1234567890")]
    [InlineData("resultCode", "C:/Users/ACCOUNT/data.db")]
    [InlineData("resultCode", "https://HOST/path")]
    [InlineData("resultCode", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJBQ0NPVU5UIn0.signature")]
    public async Task PublishRejectsPathOrSecretShapedMetrics(string name, string value)
    {
        var publisher = new AtomicTelemetryPublisher(_root, TimeProvider.System);
        var metrics = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { [name] = value });

        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync(
            new OperationalTelemetryDraft(
                "recovery",
                "recovery_capture_failed",
                "warning",
                "capture_failed",
                metrics),
            default));

        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("null", "{\"databaseCount\":null}")]
    [InlineData("fraction", "{\"durationMilliseconds\":1.5}")]
    [InlineData("array", "{\"resultCode\":[\"stable\"]}")]
    [InlineData("nested", "{\"resultCode\":{\"value\":\"stable\"}}")]
    public async Task PublishRejectsMetricsOutsideBoundedScalarTypes(
        string _,
        string metricsJson)
    {
        var publisher = new AtomicTelemetryPublisher(_root, TimeProvider.System);
        using var document = JsonDocument.Parse(metricsJson);

        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync(
            new OperationalTelemetryDraft(
                "recovery",
                "recovery_capture_failed",
                "warning",
                "capture_failed",
                document.RootElement.Clone()),
            default));
    }

    [Fact]
    public async Task PublishRejectsSecretShapedTopLevelCode()
    {
        var publisher = new AtomicTelemetryPublisher(_root, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync(
            new OperationalTelemetryDraft(
                "recovery",
                "recovery_capture_failed",
                "warning",
                "redacted_key_example_1234567890",
                JsonSerializer.SerializeToElement(new { restartCount = 0 })),
            default));
    }

    [Fact]
    public async Task PublishRejectsDuplicateMetricNames()
    {
        var publisher = new AtomicTelemetryPublisher(_root, TimeProvider.System);
        using var document = JsonDocument.Parse(
            "{\"databaseCount\":1,\"databaseCount\":2}");

        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync(
            new OperationalTelemetryDraft(
                "recovery",
                "recovery_capture_succeeded",
                "info",
                "key_validated",
                document.RootElement.Clone()),
            default));
    }

    [Fact]
    public async Task ConcurrentPublicationsCreateUniqueCompleteFiles()
    {
        var publisher = new AtomicTelemetryPublisher(_root, TimeProvider.System);
        var draft = new OperationalTelemetryDraft(
            "recovery",
            "recovery_capture_started",
            "info",
            "capture_started",
            JsonSerializer.SerializeToElement(new { restartCount = 0 }));

        await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => publisher.PublishAsync(draft, default)));

        var paths = Directory.EnumerateFiles(_root).ToArray();
        Assert.Equal(32, paths.Length);
        Assert.Equal(32, paths.Select(Path.GetFileName).Distinct(StringComparer.Ordinal).Count());
        foreach (var path in paths)
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
            Assert.Equal(
                document.RootElement.GetProperty("eventId").GetString() + ".json",
                Path.GetFileName(path));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
