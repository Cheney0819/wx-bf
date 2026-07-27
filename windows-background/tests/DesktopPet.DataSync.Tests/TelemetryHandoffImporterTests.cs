using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopPet.Background.Contracts;
using DesktopPet.Background.Infrastructure;
using DesktopPet.DataSync.Identity;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;
using DesktopPet.DataSync.Telemetry;
using DesktopPet.DataSync.Upload;
using DesktopPet.DataSync.Worker;
using Microsoft.Data.Sqlite;

namespace DesktopPet.DataSync.Tests;

public sealed class TelemetryHandoffImporterTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-telemetry-import-tests",
        Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-07-26T08:09:10Z");

    [Fact]
    public async Task ValidEnvelopeCommitsIdentityDiagnosticStateAndEncryptedOutboxThenDeletesSource()
    {
        var fixture = await CreateFixtureAsync();
        var path = await WriteEnvelopeAsync(EventId('a'), new { databaseCount = 1 });

        var result = await fixture.Importer.ImportAsync(path, default);

        Assert.False(result.WasAlreadyImported);
        Assert.False(result.WasRejected);
        Assert.False(File.Exists(path));
        Assert.Equal(1, await CountAsync(fixture.Repository.DatabasePath, "imported_telemetry"));
        Assert.Equal(1, await CountAsync(fixture.Repository.DatabasePath, "runtime_event"));
        Assert.Equal("true", await ReadStateAsync(fixture.Repository.DatabasePath, "decrypt_ok"));

        var row = Assert.Single(await fixture.Repository.GetPendingOutboxAsync(10, default));
        var expectedRequestId = Sha256(
            $"desktop-pet-datasync-event-v1|client-cs-existing|{EventId('a')}");
        Assert.Equal(expectedRequestId, row.Id);
        Assert.Equal($"events:{expectedRequestId}", row.IdempotencyKey);
        Assert.Equal("events", row.Endpoint);
        var plaintext = fixture.Protector.Unprotect(row.Id, row.Endpoint, row.Ciphertext);
        using var document = JsonDocument.Parse(plaintext);
        Assert.Equal(expectedRequestId, document.RootElement.GetProperty("request_id").GetString());
        Assert.Equal("client-cs-existing", document.RootElement.GetProperty("session_id").GetString());
        Assert.Equal("client_cs", document.RootElement.GetProperty("source").GetString());
        Assert.Equal("recovery_capture_succeeded", document.RootElement.GetProperty("event_name").GetString());
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal("recovery", payload.GetProperty("component").GetString());
        Assert.Equal("info", payload.GetProperty("severity").GetString());
        Assert.Equal("key_validated", payload.GetProperty("code").GetString());
        Assert.Equal(1, payload.GetProperty("metrics").GetProperty("databaseCount").GetInt32());
        Assert.Equal(-1, (await File.ReadAllBytesAsync(fixture.Repository.DatabasePath)).AsSpan()
            .IndexOf("client-cs-existing"u8));
    }

    [Fact]
    public async Task RecoveryPreflightFailureFlowsIntoNextStatusHeartbeat()
    {
        var fixture = await CreateFixtureAsync();
        await fixture.Writer.CommitAsync(
            new OperationalTelemetryEnvelope(
                1,
                EventId('9'),
                "recovery",
                "client_v4_data_dir_result",
                "warning",
                "data_root_missing",
                _now,
                JsonSerializer.SerializeToElement(new
                {
                    candidateCount = 0,
                    databaseCount = 0,
                    wechatLoggedIn = false,
                })),
            default);
        var identity = new ClientIdentityDocument(
            1,
            "client-cs-existing",
            "client_cs",
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"));
        var status = new StatusOutboxWriter(
            fixture.Repository,
            fixture.Protector,
            identity,
            new FixedTimeProvider(_now));

        await status.EnqueueHeartbeatAsync(default);

        Assert.Equal(
            "data_root_missing",
            await ReadStateAsync(fixture.Repository.DatabasePath, "error"));
        var row = Assert.Single(
            await fixture.Repository.GetPendingOutboxAsync(10, default),
            item => item.Endpoint == "status");
        var plaintext = fixture.Protector.Unprotect(
            row.Id,
            row.Endpoint,
            row.Ciphertext);
        using var document = JsonDocument.Parse(plaintext);
        Assert.False(document.RootElement.GetProperty("wechat_logged_in").GetBoolean());
        Assert.Equal(
            "data_root_missing",
            document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ReplayAfterCommitCreatesNoDuplicateRows()
    {
        var fixture = await CreateFixtureAsync();
        var eventId = EventId('b');
        var path = await WriteEnvelopeAsync(eventId, new { databaseCount = 2 });
        var content = await File.ReadAllBytesAsync(path);
        await fixture.Importer.ImportAsync(path, default);
        await File.WriteAllBytesAsync(path, content);

        var replay = await fixture.Importer.ImportAsync(path, default);

        Assert.True(replay.WasAlreadyImported);
        Assert.False(File.Exists(path));
        Assert.Equal(1, await CountAsync(fixture.Repository.DatabasePath, "imported_telemetry"));
        Assert.Equal(1, await CountAsync(fixture.Repository.DatabasePath, "runtime_event"));
        Assert.Equal(1, await CountAsync(fixture.Repository.DatabasePath, "outbox"));
    }

    [Fact]
    public async Task CommitFailureRollsBackEveryTelemetryEffectAndKeepsSource()
    {
        var fixture = await CreateFixtureAsync(new ThrowingTelemetryCommitObserver());
        var path = await WriteEnvelopeAsync(EventId('c'), new { wechatLoggedIn = false });

        await Assert.ThrowsAsync<IOException>(() => fixture.Importer.ImportAsync(path, default));

        Assert.True(File.Exists(path));
        Assert.Equal(0, await CountAsync(fixture.Repository.DatabasePath, "imported_telemetry"));
        Assert.Equal(0, await CountAsync(fixture.Repository.DatabasePath, "runtime_event"));
        Assert.Equal(0, await CountAsync(fixture.Repository.DatabasePath, "operational_state"));
        Assert.Equal(0, await CountAsync(fixture.Repository.DatabasePath, "outbox"));
    }

    [Fact]
    public async Task InvalidPersistedStateFailureDoesNotRejectAValidSourceEnvelope()
    {
        var fixture = await CreateFixtureAsync(new InvalidDataTelemetryCommitObserver());
        var path = await WriteEnvelopeAsync(EventId('3'), new { databaseCount = 1 });

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Importer.ImportAsync(path, default));

        Assert.True(File.Exists(path));
        Assert.False(Directory.Exists(RejectedRoot()));
        Assert.Equal(0, await CountAsync(fixture.Repository.DatabasePath, "imported_telemetry"));
    }

    [Theory]
    [InlineData("filename")]
    [InlineData("unknown_member")]
    [InlineData("oversized")]
    public async Task InvalidEnvelopeMovesAtomicallyToRejectedWithoutPayloadLeak(string defect)
    {
        var fixture = await CreateFixtureAsync();
        const string untrusted = "UNTRUSTED-SECRET-PAYLOAD";
        var eventId = EventId('d');
        var path = defect switch
        {
            "filename" => await WriteEnvelopeAsync(eventId, new { databaseCount = 1 }, EventId('e')),
            "unknown_member" => await WriteRawAsync(
                eventId,
                JsonSerializer.Serialize(Envelope(eventId, new { databaseCount = 1 }), JsonOptions)
                    .TrimEnd('}') + $",\"unknown\":\"{untrusted}\"}}"),
            "oversized" => await WriteRawAsync(eventId, new string('x', 64 * 1024 + 1)),
            _ => throw new InvalidOperationException(),
        };

        var result = await fixture.Importer.ImportAsync(path, default);

        Assert.True(result.WasRejected);
        Assert.False(File.Exists(path));
        var rejected = Assert.Single(Directory.EnumerateFiles(RejectedRoot()));
        Assert.Equal(Path.GetFileName(path), Path.GetFileName(rejected));
        Assert.Equal(0, await CountAsync(fixture.Repository.DatabasePath, "imported_telemetry"));
        Assert.Equal(0, await CountAsync(fixture.Repository.DatabasePath, "outbox"));
        Assert.Contains(
            "datasync_telemetry_rejected",
            await ReadRuntimeEventTypesAsync(fixture.Repository.DatabasePath));
        Assert.DoesNotContain(
            "datasync_handoff_rejected",
            await ReadRuntimeEventTypesAsync(fixture.Repository.DatabasePath));
        var diagnostic = await ReadLatestRuntimePayloadAsync(fixture.Repository.DatabasePath);
        Assert.Contains(Path.GetFileName(path), diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(untrusted, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedEnvelopeDoesNotBlockLaterValidEnvelope()
    {
        var fixture = await CreateFixtureAsync();
        var invalid = await WriteRawAsync(EventId('f'), "{not-json");
        var valid = await WriteEnvelopeAsync(EventId('1'), new { databaseCount = 3 });

        var rejected = await fixture.Importer.ImportAsync(invalid, default);
        var imported = await fixture.Importer.ImportAsync(valid, default);

        Assert.True(rejected.WasRejected);
        Assert.False(imported.WasRejected);
        Assert.Equal(1, await CountAsync(fixture.Repository.DatabasePath, "imported_telemetry"));
        Assert.Equal(1, await CountAsync(fixture.Repository.DatabasePath, "outbox"));
    }

    [Fact]
    public async Task OlderRecoveryFailureCannotOverwriteNewerSuccessOnReverseArrival()
    {
        var fixture = await CreateFixtureAsync();
        var newer = Envelope(EventId('a'), new { databaseCount = 1 });
        var older = Envelope(EventId('f'), new { databaseCount = 0 }) with
        {
            EventName = "recovery_capture_failed",
            Code = "capture_failed",
            Severity = "error",
            OccurredAtUtc = _now.AddHours(-1),
        };

        await fixture.Writer.CommitAsync(newer, default);
        await fixture.Writer.CommitAsync(older, default);

        var state = await fixture.Repository.GetOperationalStateAsync("decrypt_ok", default);
        Assert.NotNull(state);
        Assert.Equal("true", state.ValueJson);
        Assert.Equal(newer.OccurredAtUtc, state.UpdatedAtUtc);
    }

    [Theory]
    [InlineData("success", "true")]
    [InlineData("partial_success", "false")]
    public async Task DecryptExportResultUpdatesDecryptOperationalState(
        string code,
        string expected)
    {
        var fixture = await CreateFixtureAsync();
        var result = Envelope(
            EventId('b'),
            new { databaseCount = 18, outputCount = code == "success" ? 18 : 1, pendingCount = code == "success" ? 0 : 17 }) with
        {
            EventName = "client_wechat_decrypt_export_result",
            Code = code,
        };

        await fixture.Writer.CommitAsync(result, default);

        Assert.Equal(expected, await ReadStateAsync(fixture.Repository.DatabasePath, "decrypt_ok"));
    }

    [Fact]
    public async Task PublishedHandoffDoesNotOverwritePartialDecryptResult()
    {
        var fixture = await CreateFixtureAsync();
        var partial = Envelope(
            EventId('b'),
            new { databaseCount = 18, outputCount = 1, pendingCount = 17 }) with
        {
            EventName = "client_wechat_decrypt_export_result",
            Code = "partial_success",
        };
        var handoff = Envelope(
            EventId('c'),
            new { databaseCount = 1, requiredDatabasesComplete = false }) with
        {
            EventName = "recovery_handoff_published",
            Code = "handoff_ready",
            OccurredAtUtc = _now.AddMilliseconds(1),
        };

        await fixture.Writer.CommitAsync(partial, default);
        await fixture.Writer.CommitAsync(handoff, default);

        Assert.Equal("false", await ReadStateAsync(fixture.Repository.DatabasePath, "decrypt_ok"));
    }

    [Fact]
    public async Task ReconciliationUsesOccurrenceOrderDespiteReverseFilenamesAndInvalidFiles()
    {
        var fixture = await CreateFixtureAsync();
        await WriteRawAsync(EventId('0'), "{not-json");
        await WriteRawAsync(
            EventId('a'),
            JsonSerializer.Serialize(
                Envelope(EventId('a'), new { databaseCount = 1 }),
                JsonOptions));
        await WriteRawAsync(
            EventId('f'),
            JsonSerializer.Serialize(
                Envelope(EventId('f'), new { databaseCount = 0 }) with
                {
                    EventName = "recovery_capture_failed",
                    Code = "capture_failed",
                    Severity = "error",
                    OccurredAtUtc = _now.AddHours(-1),
                },
                JsonOptions));
        var runtime = CreateRuntime(fixture);

        await runtime.ReconcileTelemetryAsync(default);

        var state = await fixture.Repository.GetOperationalStateAsync("decrypt_ok", default);
        Assert.NotNull(state);
        Assert.Equal("true", state.ValueJson);
        Assert.Equal(_now, state.UpdatedAtUtc);
        Assert.Single(Directory.EnumerateFiles(RejectedRoot()));
        Assert.Empty(Directory.EnumerateFiles(ReadyRoot()));
    }

    [Fact]
    public async Task ReconciliationNamesTelemetryImportWithoutClaimingDatabaseHandoff()
    {
        var fixture = await CreateFixtureAsync();
        await WriteEnvelopeAsync(EventId('e'), new { databaseCount = 18 });
        var runtime = CreateRuntime(fixture);

        await runtime.ReconcileTelemetryAsync(default);

        var eventTypes = await ReadRuntimeEventTypesAsync(fixture.Repository.DatabasePath);
        Assert.Contains("datasync_telemetry_imported", eventTypes);
        Assert.DoesNotContain("datasync_handoff_imported", eventTypes);
    }

    [Fact]
    public async Task EnvelopeAtExactlySixtyFourKibIsAccepted()
    {
        var fixture = await CreateFixtureAsync();
        var eventId = EventId('2');
        var json = JsonSerializer.Serialize(Envelope(eventId, new { databaseCount = 1 }), JsonOptions);
        var content = json + new string(' ', 64 * 1024 - Encoding.UTF8.GetByteCount(json));
        var path = await WriteRawAsync(eventId, content);

        var result = await fixture.Importer.ImportAsync(path, default);

        Assert.False(result.WasRejected);
        Assert.Equal(1, await CountAsync(fixture.Repository.DatabasePath, "imported_telemetry"));
    }

    [Fact]
    public async Task SourceOutsideTelemetryReadyDirectoryIsNotImportedOrMoved()
    {
        var fixture = await CreateFixtureAsync();
        var eventId = EventId('4');
        var path = Path.Combine(_root, eventId + ".json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(Envelope(eventId, new { databaseCount = 1 }), JsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Importer.ImportAsync(path, default));

        Assert.True(File.Exists(path));
        Assert.False(Directory.Exists(RejectedRoot()));
        Assert.Equal(0, await CountAsync(fixture.Repository.DatabasePath, "imported_telemetry"));
    }

    [Fact]
    public async Task DirectWriterRejectsEnvelopeThatBypassesFileValidation()
    {
        var fixture = await CreateFixtureAsync();
        var invalid = Envelope(EventId('5'), new { databaseCount = 1 }) with
        {
            EventName = "Recovery_Invalid",
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Writer.CommitAsync(invalid, default));

        Assert.Equal(0, await CountAsync(fixture.Repository.DatabasePath, "imported_telemetry"));
        Assert.Equal(0, await CountAsync(fixture.Repository.DatabasePath, "outbox"));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("event_id")]
    [InlineData("identifier")]
    [InlineData("severity")]
    [InlineData("time")]
    [InlineData("metric_count")]
    [InlineData("metric_name")]
    [InlineData("metric_type")]
    public async Task DirectWriterEnforcesCompleteEnvelopeValidation(string defect)
    {
        var fixture = await CreateFixtureAsync();
        var valid = Envelope(EventId('0'), new { databaseCount = 1 });
        var invalid = defect switch
        {
            "schema" => valid with { SchemaVersion = 2 },
            "event_id" => valid with { EventId = new string('A', 64) },
            "identifier" => valid with { Component = "Recovery" },
            "severity" => valid with { Severity = "fatal" },
            "time" => valid with
            {
                OccurredAtUtc = new DateTimeOffset(2026, 7, 26, 16, 9, 10, TimeSpan.FromHours(8)),
            },
            "metric_count" => valid with
            {
                Metrics = JsonSerializer.SerializeToElement(
                    Enumerable.Range(0, 33).ToDictionary(index => $"count{index}", index => index)),
            },
            "metric_name" => valid with
            {
                Metrics = JsonSerializer.SerializeToElement(new { accessToken = true }),
            },
            "metric_type" => valid with
            {
                Metrics = JsonSerializer.SerializeToElement(new { result = new { nested = true } }),
            },
            _ => throw new InvalidOperationException(),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Writer.CommitAsync(invalid, default));

        Assert.Equal(0, await CountAsync(fixture.Repository.DatabasePath, "imported_telemetry"));
    }

    [Fact]
    public async Task NonRecoveryMetricsCannotOverwriteRecoveryOperationalState()
    {
        var fixture = await CreateFixtureAsync();
        var eventEnvelope = Envelope(EventId('6'), new { decryptOk = true }) with
        {
            Component = "datasync",
            EventName = "datasync_worker_started",
            Code = "worker_started",
        };

        await fixture.Writer.CommitAsync(eventEnvelope, default);

        Assert.Null(await ReadStateAsync(fixture.Repository.DatabasePath, "decrypt_ok"));
    }

    [Fact]
    public async Task UnmappedRecoveryMetricCannotInventOperationalState()
    {
        var fixture = await CreateFixtureAsync();
        var eventEnvelope = Envelope(EventId('9'), new { decryptOk = true }) with
        {
            EventName = "recovery_restart_completed",
            Code = "restart_completed",
        };

        await fixture.Writer.CommitAsync(eventEnvelope, default);

        Assert.Null(await ReadStateAsync(fixture.Repository.DatabasePath, "decrypt_ok"));
    }

    [Fact]
    public async Task RejectionDiagnosticFailureKeepsInvalidSourceInReady()
    {
        var fixture = await CreateFixtureAsync();
        var eventId = EventId('7');
        var path = await WriteRawAsync(eventId, "{not-json");
        File.Delete(fixture.Repository.DatabasePath);

        await Assert.ThrowsAsync<SqliteException>(() => fixture.Importer.ImportAsync(path, default));

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.Exists(RejectedRoot())
            ? Directory.EnumerateFiles(RejectedRoot())
            : []);
    }

    [Fact]
    public async Task RepeatedInvalidFilenameAtomicallyReplacesRejectedCopy()
    {
        var fixture = await CreateFixtureAsync();
        var eventId = EventId('8');
        var first = await WriteRawAsync(eventId, "{first-invalid");
        await fixture.Importer.ImportAsync(first, default);
        var second = await WriteRawAsync(eventId, "{second-invalid");

        var result = await fixture.Importer.ImportAsync(second, default);

        Assert.True(result.WasRejected);
        Assert.False(File.Exists(second));
        var rejected = Assert.Single(Directory.EnumerateFiles(RejectedRoot()));
        Assert.Equal("{second-invalid", await File.ReadAllTextAsync(rejected));
    }

    private async Task<ImportFixture> CreateFixtureAsync(ITelemetryCommitObserver? observer = null)
    {
        Directory.CreateDirectory(ReadyRoot());
        var protector = new XorOutboxProtector();
        var repository = new DataSyncRepository(
            Path.Combine(_root, "sync.db"),
            new FixedTimeProvider(_now),
            protector);
        await repository.InitializeAsync(default);
        var identity = new ClientIdentityDocument(
            1,
            "client-cs-existing",
            "client_cs",
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"));
        var writer = new TelemetryOutboxWriter(
            repository,
            protector,
            identity,
            new FixedTimeProvider(_now),
            observer);
        var importer = new TelemetryHandoffImporter(
            new TelemetryEnvelopeValidator(),
            writer,
            RejectedRoot());
        return new ImportFixture(repository, protector, writer, importer);
    }

    private DataSyncRuntime CreateRuntime(ImportFixture fixture)
    {
        var jobsRoot = Path.Combine(_root, "DataSync", "Jobs");
        var identity = new ClientIdentityDocument(
            1,
            "client-cs-existing",
            "client_cs",
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"));
        var time = new FixedTimeProvider(_now);
        return new DataSyncRuntime(
            Path.Combine(_root, "Handoff", "ready"),
            jobsRoot,
            "parser-test",
            fixture.Repository,
            new HandoffManifestImporter(
                fixture.Repository,
                Path.Combine(_root, "Recovery", "Generations"),
                new HandoffAcceptancePublisher(Path.Combine(_root, "Handoff", "accepted"), time),
                time),
            new ParserJobBuilder(jobsRoot),
            new ParserProcessSupervisor(Path.Combine(_root, "Parser", "parser-install.json")),
            new ParserResultValidator(),
            new IncrementalOutboxWriter(fixture.Repository, fixture.Protector, identity, time),
            new OutboxUploader(
                fixture.Repository,
                fixture.Protector,
                new MissingSettingsProvider(),
                new HttpClient(new UnexpectedHttpHandler()),
                time,
                new FixedBackoff()),
            telemetryImporter: fixture.Importer);
    }

    private Task<string> WriteEnvelopeAsync(string eventId, object metrics, string? fileId = null) =>
        WriteRawAsync(fileId ?? eventId, JsonSerializer.Serialize(Envelope(eventId, metrics), JsonOptions));

    private async Task<string> WriteRawAsync(string fileId, string content)
    {
        Directory.CreateDirectory(ReadyRoot());
        var path = Path.Combine(ReadyRoot(), fileId + ".json");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private OperationalTelemetryEnvelope Envelope(string eventId, object metrics) => new(
        1,
        eventId,
        "recovery",
        "recovery_capture_succeeded",
        "info",
        "key_validated",
        _now,
        JsonSerializer.SerializeToElement(metrics));

    private static string EventId(char value) => new(value, 64);

    private static string Sha256(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();

    private string ReadyRoot() => Path.Combine(_root, "Handoff", "Telemetry", "ready");

    private string RejectedRoot() => Path.Combine(_root, "Handoff", "Telemetry", "rejected");

    private static async Task<long> CountAsync(string path, string table)
    {
        await using var connection = await SqliteConnectionFactory.OpenAsync(path, false, default);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadStateAsync(string path, string key)
    {
        await using var connection = await SqliteConnectionFactory.OpenAsync(path, false, default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM operational_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<string> ReadLatestRuntimePayloadAsync(string path)
    {
        await using var connection = await SqliteConnectionFactory.OpenAsync(path, false, default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM runtime_event ORDER BY sequence DESC LIMIT 1;";
        return (string)(await command.ExecuteScalarAsync() ?? string.Empty);
    }

    private static async Task<IReadOnlyList<string>> ReadRuntimeEventTypesAsync(string path)
    {
        await using var connection = await SqliteConnectionFactory.OpenAsync(path, false, default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_type FROM runtime_event ORDER BY sequence;";
        var eventTypes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) eventTypes.Add(reader.GetString(0));
        return eventTypes;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record ImportFixture(
        DataSyncRepository Repository,
        XorOutboxProtector Protector,
        TelemetryOutboxWriter Writer,
        TelemetryHandoffImporter Importer);

    private sealed class ThrowingTelemetryCommitObserver : ITelemetryCommitObserver
    {
        public Task BeforeCommitAsync(CancellationToken cancellationToken) =>
            throw new IOException("Simulated crash before telemetry commit.");
    }

    private sealed class InvalidDataTelemetryCommitObserver : ITelemetryCommitObserver
    {
        public Task BeforeCommitAsync(CancellationToken cancellationToken) =>
            throw new InvalidDataException("Simulated invalid persisted state.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MissingSettingsProvider : IServerSettingsProvider
    {
        public Task<ServerSettings?> TryLoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ServerSettings?>(null);
    }

    private sealed class FixedBackoff : IUploadBackoff
    {
        public TimeSpan GetDelay(int attemptCount, TimeSpan? retryAfter) => TimeSpan.FromSeconds(1);
    }

    private sealed class UnexpectedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Telemetry reconciliation must not use HTTP.");
    }

    private sealed class XorOutboxProtector : IOutboxProtector
    {
        public byte[] Protect(string outboxId, string endpoint, ReadOnlySpan<byte> plaintext) =>
            Transform(outboxId, endpoint, plaintext);

        public byte[] Unprotect(string outboxId, string endpoint, ReadOnlySpan<byte> ciphertext) =>
            Transform(outboxId, endpoint, ciphertext);

        private static byte[] Transform(string outboxId, string endpoint, ReadOnlySpan<byte> input)
        {
            var entropy = Encoding.UTF8.GetBytes(outboxId + "|" + endpoint);
            var output = input.ToArray();
            for (var index = 0; index < output.Length; index++)
                output[index] ^= entropy[index % entropy.Length];
            return output;
        }
    }
}
