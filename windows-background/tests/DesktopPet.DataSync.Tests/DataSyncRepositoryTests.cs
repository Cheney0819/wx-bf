using DesktopPet.Background.Infrastructure;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;

namespace DesktopPet.DataSync.Tests;

public sealed class DataSyncRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-datasync-repository-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LeaseAndOutboxSurviveRepositoryReopen()
    {
        var path = Path.Combine(_root, "sync.db");
        await using (var first = await OpenAsync(path))
        {
            await first.EnqueueParseJobAsync(Job("job-1"), default);
            var claimed = await first.TryClaimParseJobAsync(
                "worker-a",
                TimeSpan.FromMinutes(3),
                default);
            Assert.Equal("job-1", claimed!.Id);

            await first.EnqueueOutboxAsync(
                new OutboxDraft(
                    "outbox-1",
                    "message:item-1",
                    "messages",
                    "{\"messages\":[]}"u8.ToArray()),
                default);
        }

        await using var reopened = await OpenAsync(path);
        var job = await reopened.GetParseJobAsync("job-1", default);
        var outbox = await reopened.GetPendingOutboxAsync(10, default);

        Assert.Equal("worker-a", job!.LeaseOwner);
        Assert.Single(outbox);
        Assert.Equal("message:item-1", outbox[0].IdempotencyKey);
    }

    [Fact]
    public async Task ExpiredLeaseCanBeClaimedByAnotherWorker()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        await using var repository = await OpenAsync(Path.Combine(_root, "sync.db"), time);
        await repository.EnqueueParseJobAsync(Job("job-1", time.GetUtcNow()), default);

        Assert.NotNull(await repository.TryClaimParseJobAsync(
            "worker-a", TimeSpan.FromMinutes(3), default));
        Assert.Null(await repository.TryClaimParseJobAsync(
            "worker-b", TimeSpan.FromMinutes(3), default));

        time.Advance(TimeSpan.FromMinutes(4));
        var reclaimed = await repository.TryClaimParseJobAsync(
            "worker-b", TimeSpan.FromMinutes(3), default);

        Assert.Equal("worker-b", reclaimed!.LeaseOwner);
        Assert.Equal(2, reclaimed.AttemptCount);
    }

    [Fact]
    public async Task PlaintextOutboxPayloadIsNeverStoredInSqlite()
    {
        var path = Path.Combine(_root, "sync.db");
        await using var repository = await OpenAsync(path);
        await repository.EnqueueOutboxAsync(
            new OutboxDraft(
                "outbox-1",
                "message:item-1",
                "messages",
                "unique-secret-message"u8.ToArray()),
            default);

        var databaseBytes = await File.ReadAllBytesAsync(path);

        Assert.Equal(-1, databaseBytes.AsSpan().IndexOf("unique-secret-message"u8));
    }

    [Fact]
    public async Task RequeueAuthenticationQuarantinesPreservesOtherClientErrors()
    {
        await using var repository = await OpenAsync(Path.Combine(_root, "sync.db"));
        await repository.EnqueueOutboxAsync(
            new OutboxDraft(
                "auth-row",
                "messages:auth-row",
                "messages",
                "{\"request_id\":\"auth-row\",\"messages\":[]}"u8.ToArray()),
            default);
        await repository.EnqueueOutboxAsync(
            new OutboxDraft(
                "bad-row",
                "messages:bad-row",
                "messages",
                "{\"request_id\":\"bad-row\",\"messages\":[]}"u8.ToArray()),
            default);

        var first = await repository.TryClaimOutboxAsync(
            "worker-a", TimeSpan.FromMinutes(3), default);
        Assert.NotNull(first);
        await repository.QuarantineOutboxAsync(
            first!.Id, "worker-a", 401, "unauthorized", default);
        var second = await repository.TryClaimOutboxAsync(
            "worker-a", TimeSpan.FromMinutes(3), default);
        Assert.NotNull(second);
        await repository.QuarantineOutboxAsync(
            second!.Id, "worker-a", 400, "payload_invalid", default);

        var requeued = await repository.RequeueQuarantinedOutboxAsync(
            [401, 403],
            default);

        Assert.Equal(1, requeued);
        Assert.Equal(OutboxState.Pending, (await repository.GetOutboxAsync(first.Id, default))!.State);
        Assert.Equal(OutboxState.Quarantined, (await repository.GetOutboxAsync(second.Id, default))!.State);
        Assert.Equal("messages:auth-row", (await repository.GetOutboxAsync("auth-row", default))!.IdempotencyKey);
    }

    private async Task<DataSyncRepository> OpenAsync(
        string path,
        TimeProvider? timeProvider = null)
    {
        var repository = new DataSyncRepository(
            path,
            timeProvider ?? TimeProvider.System,
            new EncryptedOutboxProtector(new XorTestProtector()));
        await repository.InitializeAsync(default);
        return repository;
    }

    private static ParseJob Job(string id, DateTimeOffset? now = null) => new(
        id,
        $"source-{id}",
        ParseJobState.Pending,
        LeaseOwner: null,
        LeaseUntilUtc: null,
        AttemptCount: 0,
        CreatedAtUtc: now ?? DateTimeOffset.UtcNow,
        UpdatedAtUtc: now ?? DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            Transform(plaintext, entropy);

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
            Transform(ciphertext, entropy);

        private static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy)
        {
            var output = input.ToArray();
            for (var index = 0; index < output.Length; index++)
                output[index] ^= entropy[index % entropy.Length];
            return output;
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
