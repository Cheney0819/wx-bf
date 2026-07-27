using System.Net;
using System.Text;
using System.Text.Json;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;
using DesktopPet.DataSync.Upload;

namespace DesktopPet.DataSync.Tests;

public sealed class OutboxUploaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-outbox-uploader-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingSettingsLeaveOutboxPendingAndOffline()
    {
        var fixture = await CreateFixtureAsync(handler: new QueueHandler());
        fixture.Settings.Value = null;

        var result = await fixture.Uploader.UploadOneAsync("worker-a", default);
        var row = await fixture.Repository.GetOutboxAsync("outbox-1", default);

        Assert.Equal(UploadDisposition.CredentialMissing, result.Disposition);
        Assert.Equal(OutboxState.Pending, row!.State);
        Assert.Equal(0, row.AttemptCount);
        await fixture.Repository.DisposeAsync();
    }

    [Fact]
    public async Task AuthenticationQuarantineSurvivesRestartUntilCredentialChanges()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("bad token"),
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"added\":0}"),
            });
        var fixture = await CreateFixtureAsync(handler);

        var rejected = await fixture.Uploader.UploadOneAsync("worker-a", default);
        var restartedUploader = new OutboxUploader(
            fixture.Repository,
            new EncryptedOutboxProtector(new XorTestProtector()),
            fixture.Settings,
            new HttpClient(handler),
            fixture.Time,
            new FixedBackoff(TimeSpan.FromSeconds(30)));

        var unchanged = await restartedUploader.UploadOneAsync("worker-a", default);
        fixture.Settings.Value = new ServerSettings(
            new Uri("https://example.invalid/"),
            "replacement-token");
        var recovered = await restartedUploader.UploadOneAsync("worker-a", default);
        var row = await fixture.Repository.GetOutboxAsync("outbox-1", default);

        Assert.Equal(UploadDisposition.Quarantined, rejected.Disposition);
        Assert.Equal(UploadDisposition.Idle, unchanged.Disposition);
        Assert.Equal(UploadDisposition.Acknowledged, recovered.Disposition);
        Assert.Equal(OutboxState.Acknowledged, row!.State);
        Assert.Equal(2, handler.RequestBodies.Count);
        using var retryBody = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal(
            "replacement-token",
            retryBody.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public async Task ValidSuccessAcknowledgesAndInjectsTokenOnlyInRequestBody()
    {
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"added\":0}"),
        });
        var fixture = await CreateFixtureAsync(handler);

        var result = await fixture.Uploader.UploadOneAsync("worker-a", default);
        var row = await fixture.Repository.GetOutboxAsync("outbox-1", default);
        using var request = JsonDocument.Parse(handler.RequestBodies.Single());

        Assert.Equal(UploadDisposition.Acknowledged, result.Disposition);
        Assert.Equal("messages", result.Endpoint);
        Assert.Equal(OutboxState.Acknowledged, row!.State);
        Assert.Equal("secret-token", request.RootElement.GetProperty("token").GetString());
        Assert.Equal("outbox-1", request.RootElement.GetProperty("request_id").GetString());
        Assert.Equal("https://example.invalid/api/messages", handler.RequestUris.Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task TransientHttpStatusSchedulesRetry(HttpStatusCode statusCode)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("temporary failure"),
        };
        if (statusCode == HttpStatusCode.TooManyRequests)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromMinutes(2));
        var fixture = await CreateFixtureAsync(new QueueHandler(response));

        var result = await fixture.Uploader.UploadOneAsync("worker-a", default);
        var row = await fixture.Repository.GetOutboxAsync("outbox-1", default);

        Assert.Equal(UploadDisposition.RetryScheduled, result.Disposition);
        Assert.Equal(OutboxState.Pending, row!.State);
        Assert.Equal((int)statusCode, row.LastStatusCode);
        Assert.True(row.NextAttemptAtUtc > fixture.Time.GetUtcNow());
        if (statusCode == HttpStatusCode.TooManyRequests)
            Assert.True(row.NextAttemptAtUtc >= fixture.Time.GetUtcNow().AddMinutes(2));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task PermanentClientStatusQuarantinesWithoutTokenLeak(HttpStatusCode statusCode)
    {
        var fixture = await CreateFixtureAsync(new QueueHandler(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("echo secret-token\r\ninvalid"),
        }));

        var result = await fixture.Uploader.UploadOneAsync("worker-a", default);
        var row = await fixture.Repository.GetOutboxAsync("outbox-1", default);

        Assert.Equal(UploadDisposition.Quarantined, result.Disposition);
        Assert.Equal(OutboxState.Quarantined, row!.State);
        Assert.DoesNotContain("secret-token", row.LastErrorSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", row.LastErrorSummary, StringComparison.Ordinal);
        Assert.True(row.LastErrorSummary!.Length <= 256);
    }

    [Fact]
    public async Task NetworkFailureSchedulesRetry()
    {
        var fixture = await CreateFixtureAsync(new QueueHandler(new HttpRequestException("network down")));

        var result = await fixture.Uploader.UploadOneAsync("worker-a", default);
        var row = await fixture.Repository.GetOutboxAsync("outbox-1", default);

        Assert.Equal(UploadDisposition.RetryScheduled, result.Disposition);
        Assert.Equal(OutboxState.Pending, row!.State);
        Assert.Equal(0, row.LastStatusCode);
    }

    [Fact]
    public async Task InvalidTwoHundredResponseIsRetriedNotAcknowledged()
    {
        var fixture = await CreateFixtureAsync(new QueueHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":false}"),
        }));

        var result = await fixture.Uploader.UploadOneAsync("worker-a", default);

        Assert.Equal(UploadDisposition.RetryScheduled, result.Disposition);
    }

    [Fact]
    public async Task CancellationPreservesCommittedLeaseForRestartRecovery()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new QueueHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var fixture = await CreateFixtureAsync(handler);
        using var cancellation = new CancellationTokenSource();

        var upload = fixture.Uploader.UploadOneAsync("worker-a", cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        var row = await fixture.Repository.GetOutboxAsync("outbox-1", default);

        Assert.Equal(OutboxState.Leased, row!.State);
        Assert.Equal("worker-a", row.LeaseOwner);
    }

    [Fact]
    public async Task ExpiredInflightLeaseIsRecoveredAfterProcessRestart()
    {
        var fixture = await CreateFixtureAsync(new QueueHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"added\":0}"),
        }));
        Assert.NotNull(await fixture.Repository.TryClaimOutboxAsync(
            "crashed-worker", TimeSpan.FromMinutes(3), default));
        fixture.Time.Advance(TimeSpan.FromMinutes(4));

        var result = await fixture.Uploader.UploadOneAsync("replacement-worker", default);

        Assert.Equal(UploadDisposition.Acknowledged, result.Disposition);
        Assert.Equal(
            OutboxState.Acknowledged,
            (await fixture.Repository.GetOutboxAsync("outbox-1", default))!.State);
    }

    private async Task<UploaderFixture> CreateFixtureAsync(QueueHandler handler)
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-07-26T00:00:00Z"));
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        var repository = new DataSyncRepository(
            Path.Combine(_root, $"{Guid.NewGuid():N}.db"),
            time,
            protector);
        await repository.InitializeAsync(default);
        await repository.EnqueueOutboxAsync(
            new OutboxDraft(
                "outbox-1",
                "messages:outbox-1",
                "messages",
                "{\"request_id\":\"outbox-1\",\"messages\":[]}"u8.ToArray()),
            default);
        var settings = new MutableSettingsProvider
        {
            Value = new ServerSettings(new Uri("https://example.invalid/"), "secret-token"),
        };
        var uploader = new OutboxUploader(
            repository,
            protector,
            settings,
            new HttpClient(handler),
            time,
            new FixedBackoff(TimeSpan.FromSeconds(30)));
        return new UploaderFixture(repository, uploader, settings, time);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record UploaderFixture(
        DataSyncRepository Repository,
        OutboxUploader Uploader,
        MutableSettingsProvider Settings,
        AdjustableTimeProvider Time);

    private sealed class MutableSettingsProvider : IServerSettingsProvider
    {
        internal ServerSettings? Value { get; set; }

        public Task<ServerSettings?> TryLoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Value);
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

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<object> _responses = [];
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _callback;

        internal QueueHandler(params object[] responses)
        {
            foreach (var response in responses) _responses.Enqueue(response);
        }

        internal QueueHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) =>
            _callback = callback;

        internal List<string> RequestBodies { get; } = [];

        internal List<string> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.ToString());
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (_callback is not null) return await _callback(request, cancellationToken);
            var response = _responses.Dequeue();
            if (response is Exception exception) throw exception;
            return (HttpResponseMessage)response;
        }
    }
}
