using System.Text.Json;
using DesktopPet.DataSync.Identity;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;
using DesktopPet.DataSync.Telemetry;
using DesktopPet.DataSync.Upload;

namespace DesktopPet.DataSync.Tests;

public sealed class StatusOutboxWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "desktop-pet-status-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HeartbeatCarriesIdentityAndReplacesOlderPendingStatus()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        await using var repository = new DataSyncRepository(Path.Combine(_root, "sync.db"), time, protector);
        await repository.InitializeAsync(default);
        var identity = new ClientIdentityDocument(1, "session-a", "client_datasync", time.GetUtcNow());
        var writer = new StatusOutboxWriter(repository, protector, identity, time);

        await writer.EnqueueHeartbeatAsync(default);
        time.Advance(TimeSpan.FromSeconds(60));
        await writer.EnqueueHeartbeatAsync(default);

        var rows = await repository.GetPendingOutboxAsync(10, default);
        Assert.Single(rows);
        var payload = protector.Unprotect(rows[0].Id, rows[0].Endpoint, rows[0].Ciphertext);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("session-a", document.RootElement.GetProperty("session_id").GetString());
        Assert.Equal("client_datasync", document.RootElement.GetProperty("source").GetString());
        Assert.True(document.RootElement.TryGetProperty("request_id", out _));
    }

    [Fact]
    public async Task HeartbeatDoesNotDeleteLeasedStatus()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        await using var repository = new DataSyncRepository(Path.Combine(_root, "sync.db"), time, protector);
        await repository.InitializeAsync(default);
        var identity = new ClientIdentityDocument(1, "session-a", "client_datasync", time.GetUtcNow());
        var writer = new StatusOutboxWriter(repository, protector, identity, time);

        await writer.EnqueueHeartbeatAsync(default);
        var leased = await repository.TryClaimOutboxAsync("worker-a", TimeSpan.FromMinutes(3), default);
        await writer.EnqueueHeartbeatAsync(default);

        Assert.Equal(OutboxState.Leased, (await repository.GetOutboxAsync(leased!.Id, default))!.State);
        Assert.Equal(2, await repository.CountOutboxAsync(default));
    }

    [Fact]
    public async Task HeartbeatSequenceIncreasesAcrossRepositoryReopenWithoutMutatingLease()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        var databasePath = Path.Combine(_root, "reopen-sequence.db");
        var identity = new ClientIdentityDocument(1, "session-a", "client_datasync", time.GetUtcNow());
        string firstId;
        long firstSequence;

        await using (var repository = new DataSyncRepository(databasePath, time, protector))
        {
            await repository.InitializeAsync(default);
            await new StatusOutboxWriter(repository, protector, identity, time)
                .EnqueueHeartbeatAsync(default);
            var leased = await repository.TryClaimOutboxAsync(
                "worker-a",
                TimeSpan.FromMinutes(3),
                default);
            Assert.NotNull(leased);
            firstId = leased.Id;
            firstSequence = ReadSequence(protector, leased);
        }

        time.Advance(TimeSpan.FromMinutes(1));
        await using (var reopened = new DataSyncRepository(databasePath, time, protector))
        {
            await reopened.InitializeAsync(default);
            await new StatusOutboxWriter(reopened, protector, identity, time)
                .EnqueueHeartbeatAsync(default);

            var pending = Assert.Single(await reopened.GetPendingOutboxAsync(10, default));
            Assert.True(ReadSequence(protector, pending) > firstSequence);
            var stillLeased = await reopened.GetOutboxAsync(firstId, default);
            Assert.NotNull(stillLeased);
            Assert.Equal(OutboxState.Leased, stillLeased.State);
        }
    }

    [Fact]
    public async Task CoalescingTreatsSessionIdAsDataNotSqlPattern()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        await using var repository = new DataSyncRepository(Path.Combine(_root, "sync.db"), time, protector);
        await repository.InitializeAsync(default);
        var wildcard = new StatusOutboxWriter(repository, protector,
            new ClientIdentityDocument(1, "session%", "client_datasync", time.GetUtcNow()), time);
        var neighbor = new StatusOutboxWriter(repository, protector,
            new ClientIdentityDocument(1, "session-other", "client_datasync", time.GetUtcNow()), time);

        await wildcard.EnqueueHeartbeatAsync(default);
        await neighbor.EnqueueHeartbeatAsync(default);
        await wildcard.EnqueueHeartbeatAsync(default);

        Assert.Equal(2, await repository.CountOutboxAsync(default));
    }

    [Fact]
    public async Task MissingServerSettingsLeaveEncryptedHeartbeatPending()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        var path = Path.Combine(_root, "sync.db");
        await using var repository = new DataSyncRepository(path, time, protector);
        await repository.InitializeAsync(default);
        var writer = new StatusOutboxWriter(repository, protector,
            new ClientIdentityDocument(1, "session-a", "client_datasync", time.GetUtcNow()), time);
        await writer.EnqueueHeartbeatAsync(default);
        var uploader = new OutboxUploader(
            repository,
            protector,
            new MissingSettingsProvider(),
            new HttpClient(new NoOpHandler()),
            time,
            new FixedBackoff());

        var result = await uploader.UploadOneAsync("worker-a", default);
        var pending = await repository.GetPendingOutboxAsync(10, default);

        Assert.Equal(UploadDisposition.Offline, result.Disposition);
        Assert.Single(pending);
        Assert.Equal("status", pending[0].Endpoint);
        Assert.NotEmpty(pending[0].Ciphertext);

        await using var reopened = new DataSyncRepository(path, time, protector);
        await reopened.InitializeAsync(default);
        var afterRestart = await reopened.GetPendingOutboxAsync(10, default);
        Assert.Single(afterRestart);
        Assert.Equal(pending[0].Id, afterRestart[0].Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static long ReadSequence(
        EncryptedOutboxProtector protector,
        OutboxRecord row)
    {
        var payload = protector.Unprotect(row.Id, row.Endpoint, row.Ciphertext);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("heartbeat_sequence").GetInt64();
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan value) => _utcNow += value;
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
        {
            var output = plaintext.ToArray();
            for (var i = 0; i < output.Length; i++) output[i] ^= entropy[i % entropy.Length];
            return output;
        }
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) => Protect(ciphertext, entropy);
    }

    private sealed class MissingSettingsProvider : IServerSettingsProvider
    {
        public Task<ServerSettings?> TryLoadAsync(CancellationToken cancellationToken) => Task.FromResult<ServerSettings?>(null);
    }

    private sealed class FixedBackoff : IUploadBackoff
    {
        public TimeSpan GetDelay(int attemptCount, TimeSpan? retryAfter) => TimeSpan.FromSeconds(1);
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Missing settings must prevent network use.");
    }
}
