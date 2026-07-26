using System.Net;
using System.Net.Sockets;
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
using DesktopPet.Recovery;

namespace DesktopPet.DataSync.Tests;

public sealed class EndToEndDataSyncTests : IDisposable
{
    private const string LegacySessionId = "client-cs-existing";
    private const string LegacySource = "client_cs";

    private static readonly IReadOnlyDictionary<string, string> FixtureHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contact/contact.db"] = "e0f16e5fd109b5b64fd3f47c9293783f1a324bf4a4ac5475c17e511b3a8ad1e9",
            ["favorite/favorite.db"] = "2b93b4f05185f2040f22ca01d75ce30001c775a77e321323d8327297305a385a",
            ["message/message_0.db"] = "3f39e7b3881d348943ea50b9953d4081738f15382f81d15ce9b4dc6ba6ba1e18",
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedRequestIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/api/contacts"] = "bd62f82a71e82b006980ca6ee8f2dd3718752da71de111b7a647c0f3ed5df175",
            ["/api/favorites"] = "f7fdd7dfa9a8618fd8facb8544ae97ec1c04136024a39857f897c48553f4c455",
            ["/api/messages"] = "2f48440fb87d32f2b38c9b6a91a0b78783b30aa610c4c85c8a33d553022ca0c7",
        };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-datasync-e2e-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProductionHandoffReachesLoopbackServerDurablyAcrossRestarts()
    {
        if (OperatingSystem.IsWindows()) return;
        var paths = TestPaths();
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        await WriteLegacyIdentityAsync(paths.LegacyIdentity);
        var identity = await new ClientIdentityStore(
                paths.Identity,
                paths.LegacyIdentity,
                time)
            .GetAsync(default);
        var reopenedIdentity = await new ClientIdentityStore(
                paths.Identity,
                paths.LegacyIdentity,
                time)
            .GetAsync(default);
        Assert.Equal(LegacySessionId, identity.SessionId);
        Assert.Equal(LegacySource, identity.Source);
        Assert.Equal(identity, reopenedIdentity);

        var staging = await CopyReadableFixtureToStagingAsync(paths.StagingRoot);
        var manifest = await PublishRecoveryHandoffAsync(paths, staging);
        Assert.Equal(
            FixtureHashes.OrderBy(item => item.Key).Select(item => item.Value),
            manifest.Databases.OrderBy(item => item.RelativePath).Select(item => item.Sha256));
        var sourceSetId = Hash(string.Join(
            "|",
            manifest.Databases
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .Select(item => $"{item.RelativePath}:{item.GenerationId}")));
        var parseJobId = Hash($"desktop-pet-datasync-parse-job-v1|{sourceSetId}");
        var parser = await CreateParserArtifactAsync(paths.ParserRoot);
        var recoveryEventId = await PublishRecoveryTelemetryAsync(paths, time.GetUtcNow());
        var secretProtector = new XorTestProtector();
        var outboxProtector = new EncryptedOutboxProtector(secretProtector);
        await using var server = new LoopbackServer(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.OK);

        await using (var repository = await OpenRepositoryAsync(paths.SyncDatabase, time, outboxProtector))
        {
            var runtime = CreateRuntime(
                paths,
                parser,
                repository,
                outboxProtector,
                new StaticSettingsProvider(new ServerSettings(server.BaseUri, "test-token")),
                new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }),
                time,
                identity);
            await runtime.ReconcileHandoffsAsync(default);
            Assert.True(await runtime.ProcessOneParserJobAsync(default));
            Assert.False(await runtime.ProcessOneParserJobAsync(default));
            await runtime.ReconcileTelemetryAsync(default);
            await runtime.EnqueueHeartbeatAsync(default);
            var firstHeartbeat = Assert.Single(
                await repository.GetPendingOutboxAsync(10, default),
                row => row.Endpoint == "status");
            time.Advance(TimeSpan.FromMinutes(1));
            await runtime.EnqueueHeartbeatAsync(default);
            var completedJob = await repository.GetParseJobAsync(parseJobId, default);
            Assert.NotNull(completedJob);
            Assert.Equal(sourceSetId, completedJob.SourceSetId);
            Assert.Equal(ParseJobState.Completed, completedJob.State);
            Assert.Equal(5, await repository.CountExportedItemsAsync(default));
            Assert.Equal(5, await repository.CountOutboxAsync(default));
            var pending = await repository.GetPendingOutboxAsync(10, default);
            Assert.Equal(
                new[] { "contacts", "events", "favorites", "messages", "status" },
                pending.Select(row => row.Endpoint).Order(StringComparer.Ordinal));
            Assert.All(pending, row =>
            {
                var plaintext = outboxProtector.Unprotect(row.Id, row.Endpoint, row.Ciphertext);
                using var document = JsonDocument.Parse(plaintext);
                Assert.Equal(LegacySessionId, document.RootElement.GetProperty("session_id").GetString());
                Assert.Equal(LegacySource, document.RootElement.GetProperty("source").GetString());
            });
            var eventRow = Assert.Single(pending, row => row.Endpoint == "events");
            var eventPlaintext = outboxProtector.Unprotect(
                eventRow.Id,
                eventRow.Endpoint,
                eventRow.Ciphertext);
            using var eventDocument = JsonDocument.Parse(eventPlaintext);
            Assert.Equal(
                recoveryEventId,
                eventDocument.RootElement.GetProperty("payload").GetProperty("event_id").GetString());
            var survivingHeartbeat = Assert.Single(pending, row => row.Endpoint == "status");
            Assert.NotEqual(firstHeartbeat.Id, survivingHeartbeat.Id);
            Assert.Null(await repository.GetOutboxAsync(firstHeartbeat.Id, default));
            Assert.Empty(Directory.EnumerateFiles(paths.TelemetryReadyRoot, "*.json"));
        }

        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await using var repository = await OpenRepositoryAsync(
                paths.SyncDatabase,
                time,
                outboxProtector);
            using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var uploader = new OutboxUploader(
                repository,
                outboxProtector,
                new StaticSettingsProvider(new ServerSettings(server.BaseUri, "test-token")),
                httpClient,
                time,
                new FixedBackoff(TimeSpan.FromMinutes(1)));
            var result = await uploader.UploadOneAsync($"restart-{attempt}", default);
            Assert.NotEqual(UploadDisposition.Idle, result.Disposition);
            if (result.OutboxId is not null) requestIds.Add(result.OutboxId);
            time.Advance(TimeSpan.FromMinutes(2));
        }

        await server.WaitForRequestsAsync(8, TimeSpan.FromSeconds(5));
        await using (var repository = await OpenRepositoryAsync(
                         paths.SyncDatabase,
                         time,
                         outboxProtector))
        {
            Assert.Equal(5, requestIds.Count);
            foreach (var requestId in requestIds)
                Assert.Equal(OutboxState.Acknowledged, (await repository.GetOutboxAsync(requestId, default))!.State);
            Assert.Equal(5, await repository.CountExportedItemsAsync(default));
            Assert.Equal(5, await repository.CountOutboxAsync(default));
        }

        Assert.Equal(
            new[] { 500, 429, 200 },
            server.StatusCodesServed.Take(3).ToArray());
        Assert.Equal(
            new[]
            {
                "/api/contacts",
                "/api/events",
                "/api/favorites",
                "/api/messages",
                "/api/status",
            },
            server.Requests.Select(request => request.Path).Distinct().Order(StringComparer.Ordinal));
        Assert.All(server.Requests, request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            Assert.Equal(LegacySessionId, document.RootElement.GetProperty("session_id").GetString());
            Assert.Equal(LegacySource, document.RootElement.GetProperty("source").GetString());
        });
        foreach (var expected in ExpectedRequestIds)
        {
            var endpointRequests = server.Requests
                .Where(request => request.Path == expected.Key)
                .ToArray();
            Assert.NotEmpty(endpointRequests);
            Assert.All(endpointRequests, request => Assert.Equal(expected.Value, request.RequestId));
        }
        Assert.Equal(
            ExpectedRequestIds.Values.Order(StringComparer.Ordinal),
            server.Requests
                .Where(request => ExpectedRequestIds.ContainsKey(request.Path))
                .Select(request => request.RequestId)
                .Distinct()
                .Order(StringComparer.Ordinal));
        Assert.All(
            server.Requests.GroupBy(request => request.RequestId),
            group => Assert.Single(group.Select(request => request.Body).Distinct()));
        var eventRequests = server.Requests.Where(request => request.Path == "/api/events").ToArray();
        var eventRequestId = Assert.Single(eventRequests.Select(request => request.RequestId).Distinct());
        Assert.Equal(5, server.LogicalEffects.Count);
        Assert.All(server.LogicalEffects, effect => Assert.Equal(1, effect.Value));
        Assert.Equal(1, server.LogicalEffects[eventRequestId]);
        Assert.Equal(eventRequestId, server.LostResponseRequestId);
        Assert.Equal(eventRequestId, Assert.Single(server.CachedRequestIds));
        Assert.Equal(1, server.LostResponseCount);
        Assert.Equal(1, server.CachedResponseCount);
        Assert.Single(Directory.EnumerateFiles(paths.AcceptedRoot, "*.json"));
        Assert.Equal(manifest.ManifestId, Path.GetFileNameWithoutExtension(
            Directory.EnumerateFiles(paths.AcceptedRoot, "*.json").Single()));
    }

    [Fact]
    public async Task ParserCleanupTimeoutIsContainedAndRuntimeContinues()
    {
        if (OperatingSystem.IsWindows()) return;
        var paths = TestPaths();
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var identity = new ClientIdentityDocument(1, LegacySessionId, LegacySource, time.GetUtcNow());
        var staging = await CopyReadableFixtureToStagingAsync(paths.StagingRoot);
        await PublishRecoveryHandoffAsync(paths, staging);
        var parser = await CreateUncooperativeParserArtifactAsync(paths.ParserRoot);
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        await using var repository = await OpenRepositoryAsync(paths.SyncDatabase, time, protector);
        var supervisor = new ParserProcessSupervisor(
            parser.InstallManifestPath,
            new ParserSupervisorOptions(
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(150),
                64 * 1024,
                "wx_parser.exe",
                TimeSpan.FromMilliseconds(100)),
            new NeverCompletingPostKillWaiter());
        var runtime = CreateRuntime(
            paths,
            parser,
            repository,
            protector,
            new MissingSettingsProvider(),
            new HttpClient(),
            time,
            identity,
            supervisor);
        await runtime.ReconcileHandoffsAsync(default);

        Assert.True(await runtime.ProcessOneParserJobAsync(default));
        Assert.False(await runtime.ProcessOneParserJobAsync(default));
    }

    private DataSyncRuntime CreateRuntime(
        E2ePaths paths,
        ParserArtifact parser,
        DataSyncRepository repository,
        IOutboxProtector outboxProtector,
        IServerSettingsProvider settings,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ClientIdentityDocument identity,
        ParserProcessSupervisor? supervisor = null)
    {
        var importer = new HandoffManifestImporter(
            repository,
            paths.GenerationRoot,
            new HandoffAcceptancePublisher(paths.AcceptedRoot, timeProvider),
            timeProvider);
        var jobsRoot = Path.Combine(paths.DataSyncRoot, "Jobs");
        return new DataSyncRuntime(
            paths.ReadyRoot,
            jobsRoot,
            "parser-e2e",
            repository,
            importer,
            new ParserJobBuilder(jobsRoot),
            supervisor ?? new ParserProcessSupervisor(parser.InstallManifestPath),
            new ParserResultValidator(),
            new IncrementalOutboxWriter(
                repository,
                outboxProtector,
                identity,
                timeProvider),
            new OutboxUploader(
                repository,
                outboxProtector,
                settings,
                httpClient,
                timeProvider,
                new FixedBackoff(TimeSpan.FromMinutes(1))),
            new StatusOutboxWriter(repository, outboxProtector, identity, timeProvider),
            new TelemetryHandoffImporter(
                new TelemetryEnvelopeValidator(),
                new TelemetryOutboxWriter(repository, outboxProtector, identity, timeProvider),
                paths.TelemetryRejectedRoot));
    }

    private static async Task WriteLegacyIdentityAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            "{\"session_id\":\"client-cs-existing\",\"created_at\":\"2026-07-01T00:00:00Z\"}");
    }

    private static async Task<string> PublishRecoveryTelemetryAsync(
        E2ePaths paths,
        DateTimeOffset occurredAtUtc)
    {
        await new AtomicTelemetryPublisher(paths.TelemetryReadyRoot, new FixedTimeProvider(occurredAtUtc))
            .PublishAsync(
                new OperationalTelemetryDraft(
                    "recovery",
                    "recovery_capture_succeeded",
                    "info",
                    "key_validated",
                    JsonSerializer.SerializeToElement(new
                    {
                        databaseCount = 3,
                        wechatLoggedIn = true,
                    })),
                default);
        var path = Assert.Single(Directory.EnumerateFiles(paths.TelemetryReadyRoot, "*.json"));
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path)))
        {
            Assert.Equal(
                document.RootElement.GetProperty("eventId").GetString() + ".json",
                Path.GetFileName(path));
            Assert.Equal(occurredAtUtc, document.RootElement.GetProperty("occurredAtUtc").GetDateTimeOffset());
            return document.RootElement.GetProperty("eventId").GetString()!;
        }
    }

    private async Task<DatabaseReadyManifest> PublishRecoveryHandoffAsync(
        E2ePaths paths,
        IReadOnlyList<(string RelativePath, string Path)> staging)
    {
        var recovered = new List<RecoveredDatabase>();
        foreach (var item in staging)
        {
            var sha256 = await Sha256Async(item.Path);
            recovered.Add(new RecoveredDatabase(sha256, item.RelativePath, item.Path, sha256));
        }
        return await new AtomicHandoffPublisher(
                paths.GenerationRoot,
                paths.ReadyRoot,
                paths.StagingRoot,
                TimeProvider.System)
            .PublishAsync("epoch-e2e", recovered, default);
    }

    private async Task<IReadOnlyList<(string RelativePath, string Path)>>
        CopyReadableFixtureToStagingAsync(string stagingRoot)
    {
        var fixtureRoot = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "real-recovery-handoff",
            "readable-set");
        var relativePaths = new[]
        {
            "contact/contact.db",
            "favorite/favorite.db",
            "message/message_0.db",
        };
        var results = new List<(string RelativePath, string Path)>();
        foreach (var relativePath in relativePaths)
        {
            var source = Path.Combine([fixtureRoot, .. relativePath.Split('/')]);
            var destination = Path.Combine([stagingRoot, .. relativePath.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(destination))
            {
                await input.CopyToAsync(output);
                await output.FlushAsync();
            }
            Assert.Equal(FixtureHashes[relativePath], await Sha256Async(destination));
            results.Add((relativePath, destination));
        }
        return results;
    }

    private async Task<ParserArtifact> CreateParserArtifactAsync(string parserRoot)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Local source-wrapper fixture is Unix-only.");
        Directory.CreateDirectory(parserRoot);
        var executable = Path.Combine(parserRoot, "wx_parser.exe");
        var python = FindExecutable("python3");
        var parserScript = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "windows-parser", "wx_parser.py"));
        await File.WriteAllTextAsync(
            executable,
            $"#!/bin/sh\nexec \"{python}\" \"{parserScript}\" \"$@\"\n");
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var sha256 = await Sha256Async(executable);
        var installManifest = Path.Combine(parserRoot, "parser-install.json");
        await File.WriteAllTextAsync(
            installManifest,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                executablePath = "wx_parser.exe",
                sha256,
            }));
        return new ParserArtifact(installManifest, sha256);
    }

    private static async Task<ParserArtifact> CreateUncooperativeParserArtifactAsync(
        string parserRoot)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix parser fixture requested on Windows.");
        Directory.CreateDirectory(parserRoot);
        var executable = Path.Combine(parserRoot, "wx_parser.exe");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nwhile true; do sleep 1; done\n");
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var sha256 = await Sha256Async(executable);
        var installManifest = Path.Combine(parserRoot, "parser-install.json");
        await File.WriteAllTextAsync(
            installManifest,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                executablePath = "wx_parser.exe",
                sha256,
            }));
        return new ParserArtifact(installManifest, sha256);
    }

    private static string FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("Python executable was not found for the local E2E fixture.");
    }

    private static async Task<DataSyncRepository> OpenRepositoryAsync(
        string path,
        TimeProvider timeProvider,
        IOutboxProtector protector)
    {
        var repository = new DataSyncRepository(path, timeProvider, protector);
        await repository.InitializeAsync(default);
        return repository;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string Hash(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();

    private E2ePaths TestPaths() => new(
        Path.Combine(_root, "staging"),
        Path.Combine(_root, "Recovery", "Generations"),
        Path.Combine(_root, "Handoff", "ready"),
        Path.Combine(_root, "Handoff", "accepted"),
        Path.Combine(_root, "DataSync"),
        Path.Combine(_root, "DataSync", "sync.db"),
        Path.Combine(_root, "Parser"),
        Path.Combine(_root, "DataSync", "client-identity.json"),
        Path.Combine(_root, "Legacy", "client_identity.json"),
        Path.Combine(_root, "Handoff", "Telemetry", "ready"),
        Path.Combine(_root, "Handoff", "Telemetry", "rejected"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record E2ePaths(
        string StagingRoot,
        string GenerationRoot,
        string ReadyRoot,
        string AcceptedRoot,
        string DataSyncRoot,
        string SyncDatabase,
        string ParserRoot,
        string Identity,
        string LegacyIdentity,
        string TelemetryReadyRoot,
        string TelemetryRejectedRoot);

    private sealed record ParserArtifact(string InstallManifestPath, string Sha256);

    private sealed class StaticSettingsProvider(ServerSettings settings) : IServerSettingsProvider
    {
        public Task<ServerSettings?> TryLoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ServerSettings?>(settings);
    }

    private sealed class MissingSettingsProvider : IServerSettingsProvider
    {
        public Task<ServerSettings?> TryLoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ServerSettings?>(null);
    }

    private sealed class NeverCompletingPostKillWaiter : IParserPostKillWaiter
    {
        public Task<bool> WaitAsync(Task completion, TimeSpan grace) => Task.FromResult(false);
    }

    private sealed class FixedBackoff(TimeSpan delay) : IUploadBackoff
    {
        public TimeSpan GetDelay(int attemptCount, TimeSpan? retryAfter) =>
            retryAfter is not null && retryAfter > delay ? retryAfter.Value : delay;
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan amount) => _utcNow += amount;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
        {
            var output = plaintext.ToArray();
            for (var index = 0; index < output.Length; index++)
                output[index] ^= entropy[index % entropy.Length];
            return output;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
            Protect(ciphertext, entropy);
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Queue<HttpStatusCode> _statuses;
        private readonly Dictionary<string, string> _receipts = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _run;
        private bool _eventResponseLost;

        internal LoopbackServer(params HttpStatusCode[] statuses)
        {
            _statuses = new Queue<HttpStatusCode>(statuses);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/");
            _run = RunAsync();
        }

        internal Uri BaseUri { get; }

        internal List<ReceivedRequest> Requests { get; } = [];

        internal List<int> StatusCodesServed { get; } = [];

        internal Dictionary<string, int> LogicalEffects { get; } = new(StringComparer.Ordinal);

        internal List<string> CachedRequestIds { get; } = [];

        internal int LostResponseCount { get; private set; }

        internal int CachedResponseCount { get; private set; }

        internal string? LostResponseRequestId { get; private set; }

        internal async Task WaitForRequestsAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Requests.Count < count && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.True(Requests.Count >= count, $"Expected {count} requests, observed {Requests.Count}.");
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    await HandleAsync(client, _cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                // Normal fake-server shutdown.
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var requestBytes = await ReadRequestAsync(stream, cancellationToken);
                var separator = FindHeaderEnd(requestBytes);
                var header = Encoding.ASCII.GetString(requestBytes, 0, separator);
                var requestLine = header.Split("\r\n", StringSplitOptions.None)[0];
                var path = requestLine.Split(' ')[1];
                var body = Encoding.UTF8.GetString(requestBytes, separator + 4, requestBytes.Length - separator - 4);
                using var json = JsonDocument.Parse(body);
                var requestId = json.RootElement.GetProperty("request_id").GetString()!;
                Requests.Add(new ReceivedRequest(path, requestId, body));
                var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;
                StatusCodesServed.Add((int)status);
                var responseBody = "temporary failure";
                if (status == HttpStatusCode.OK)
                {
                    if (_receipts.TryGetValue(requestId, out var cachedResponse))
                    {
                        responseBody = cachedResponse;
                        CachedResponseCount++;
                        CachedRequestIds.Add(requestId);
                    }
                    else
                    {
                        responseBody = path.EndsWith("messages", StringComparison.Ordinal)
                            ? "{\"ok\":true,\"added\":0}"
                            : path.EndsWith("contacts", StringComparison.Ordinal) ||
                              path.EndsWith("favorites", StringComparison.Ordinal)
                                ? "{\"ok\":true,\"changed\":0}"
                                : "{\"ok\":true}";
                        _receipts.Add(requestId, responseBody);
                        LogicalEffects[requestId] = LogicalEffects.GetValueOrDefault(requestId) + 1;
                        if (path == "/api/events" && !_eventResponseLost)
                        {
                            _eventResponseLost = true;
                            LostResponseCount++;
                            LostResponseRequestId = requestId;
                            return;
                        }
                    }
                }
                var retryAfter = status == HttpStatusCode.TooManyRequests
                    ? "Retry-After: 60\r\n"
                    : "";
                var response = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {(int)status} {status}\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {Encoding.UTF8.GetByteCount(responseBody)}\r\n" +
                    retryAfter +
                    "Connection: close\r\n\r\n" +
                    responseBody);
                await stream.WriteAsync(response, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        private static async Task<byte[]> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            var headerEnd = -1;
            var contentLength = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                output.Write(buffer, 0, read);
                var bytes = output.GetBuffer();
                if (headerEnd < 0)
                {
                    headerEnd = FindHeaderEnd(bytes.AsSpan(0, checked((int)output.Length)).ToArray());
                    if (headerEnd >= 0)
                    {
                        var header = Encoding.ASCII.GetString(bytes, 0, headerEnd);
                        var lengthLine = header.Split("\r\n", StringSplitOptions.None)
                            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                        contentLength = int.Parse(lengthLine.Split(':')[1].Trim());
                    }
                }
                if (headerEnd >= 0 && output.Length >= headerEnd + 4 + contentLength) break;
            }
            return output.ToArray();
        }

        private static int FindHeaderEnd(byte[] bytes)
        {
            for (var index = 0; index <= bytes.Length - 4; index++)
            {
                if (bytes[index] == '\r' && bytes[index + 1] == '\n' &&
                    bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
                    return index;
            }
            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try { await _run; }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
                // Normal listener shutdown on platforms that do not surface cancellation directly.
            }
            _cancellation.Dispose();
        }
    }

    private sealed record ReceivedRequest(string Path, string RequestId, string Body);
}
