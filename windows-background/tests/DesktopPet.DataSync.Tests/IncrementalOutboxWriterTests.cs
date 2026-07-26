using System.Text.Json;
using DesktopPet.DataSync.Identity;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;

namespace DesktopPet.DataSync.Tests;

public sealed class IncrementalOutboxWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-incremental-writer-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReplayingSameParserResultCreatesNoDuplicateOutboxRows()
    {
        var fixture = await CreateFixtureAsync();

        await fixture.Writer.CommitAsync(fixture.Job, fixture.Result, default);
        await fixture.Writer.CommitAsync(fixture.Job, fixture.Result, default);

        Assert.Equal(4, await fixture.Repository.CountExportedItemsAsync(default));
        Assert.Equal(3, await fixture.Repository.CountOutboxAsync(default));
        Assert.Equal(
            ParseJobState.Completed,
            (await fixture.Repository.GetParseJobAsync("job-1", default))!.State);
        await fixture.Repository.DisposeAsync();
    }

    [Fact]
    public async Task CrashAfterOutboxInsertRollsBackItemsOutboxAndCompletion()
    {
        var fixture = await CreateFixtureAsync(new ThrowingCommitObserver());

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Writer.CommitAsync(fixture.Job, fixture.Result, default));

        Assert.Equal(0, await fixture.Repository.CountExportedItemsAsync(default));
        Assert.Equal(0, await fixture.Repository.CountOutboxAsync(default));
        Assert.Equal(
            ParseJobState.Leased,
            (await fixture.Repository.GetParseJobAsync("job-1", default))!.State);
        await fixture.Repository.DisposeAsync();
    }

    [Fact]
    public async Task FiveHundredOneMessagesCreateTwoBoundedBatches()
    {
        var messages = Enumerable.Range(0, 501)
            .Select(index => ParserResultTestData.Message(index, $"message-{index}"))
            .ToArray();
        var valid = System.Text.Json.JsonSerializer.SerializeToElement(
            ParserResultTestData.Document());
        var document = valid.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Name switch
            {
                "messages" => (object?)messages,
                "contacts" or "favorites" => Array.Empty<object>(),
                _ => property.Value,
            });
        var fixture = await CreateFixtureAsync(document: document);

        await fixture.Writer.CommitAsync(fixture.Job, fixture.Result, default);

        Assert.Equal(501, await fixture.Repository.CountExportedItemsAsync(default));
        Assert.Equal(2, await fixture.Repository.CountOutboxAsync(default));
        await fixture.Repository.DisposeAsync();
    }

    [Fact]
    public async Task EveryBusinessPayloadCarriesStableClientIdentity()
    {
        var fixture = await CreateFixtureAsync();

        await fixture.Writer.CommitAsync(fixture.Job, fixture.Result, default);

        var rows = await fixture.Repository.GetPendingOutboxAsync(10, default);
        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            var plaintext = fixture.Protector.Unprotect(row.Id, row.Endpoint, row.Ciphertext);
            using var document = JsonDocument.Parse(plaintext);
            Assert.Equal("client-cs-existing", document.RootElement.GetProperty("session_id").GetString());
            Assert.Equal("client_cs", document.RootElement.GetProperty("source").GetString());
            Assert.Equal(row.Id, document.RootElement.GetProperty("request_id").GetString());
        }
        await fixture.Repository.DisposeAsync();
    }

    [Fact]
    public async Task StructurallyDistinctDelimiterContainingContactBatchesAreBothQueued()
    {
        var time = TimeProvider.System;
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        await using var repository = new DataSyncRepository(
            Path.Combine(_root, "contact-batches.db"),
            time,
            protector);
        await repository.InitializeAsync(default);
        var writer = new IncrementalOutboxWriter(
            repository,
            protector,
            new ClientIdentityDocument(
                1,
                "client-cs-existing",
                "client_cs",
                DateTimeOffset.Parse("2026-07-01T00:00:00Z")),
            time);

        await CommitContactsAsync(repository, writer, "job-a", "source-a", "a|b", "c");
        await CommitContactsAsync(repository, writer, "job-b", "source-b", "a", "b|c");

        Assert.Equal(4, await repository.CountExportedItemsAsync(default));
        Assert.Equal(2, await repository.CountOutboxAsync(default));
    }

    private async Task<WriterFixture> CreateFixtureAsync(
        IIncrementalCommitObserver? observer = null,
        object? document = null)
    {
        var protector = new EncryptedOutboxProtector(new XorTestProtector());
        var repository = new DataSyncRepository(
            Path.Combine(_root, $"{Guid.NewGuid():N}.db"),
            TimeProvider.System,
            protector);
        await repository.InitializeAsync(default);
        var job = new ParseJob(
            "job-1",
            "source-1",
            ParseJobState.Leased,
            "worker-a",
            DateTimeOffset.UtcNow.AddMinutes(3),
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await repository.EnqueueParseJobAsync(job, default);
        var resultPath = await ParserResultTestData.WriteAsync(_root, document);
        var result = await new ParserResultValidator().ValidateAsync(
            resultPath,
            job.Id,
            job.SourceSetId,
            default);
        var writer = new IncrementalOutboxWriter(
            repository,
            protector,
            new ClientIdentityDocument(
                1,
                "client-cs-existing",
                "client_cs",
                DateTimeOffset.Parse("2026-07-01T00:00:00Z")),
            TimeProvider.System,
            observer);
        return new WriterFixture(repository, protector, writer, job, result);
    }

    private async Task CommitContactsAsync(
        DataSyncRepository repository,
        IncrementalOutboxWriter writer,
        string jobId,
        string sourceSetId,
        params string[] wxids)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new ParseJob(
            jobId,
            sourceSetId,
            ParseJobState.Leased,
            "worker-a",
            now.AddMinutes(3),
            1,
            now,
            now);
        await repository.EnqueueParseJobAsync(job, default);
        var valid = JsonSerializer.SerializeToElement(ParserResultTestData.Document(jobId, sourceSetId));
        var document = valid.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Name switch
            {
                "messages" or "favorites" => (object?)Array.Empty<object>(),
                "contacts" => wxids.Select(Contact).ToArray(),
                _ => property.Value,
            });
        var resultPath = await ParserResultTestData.WriteAsync(_root, document);
        var result = await new ParserResultValidator().ValidateAsync(
            resultPath,
            jobId,
            sourceSetId,
            default);

        await writer.CommitAsync(job, result, default);
    }

    private static object Contact(string wxid) => new
    {
        wxid,
        alias = "",
        remark = "",
        nick_name = wxid,
        display_name = wxid,
        avatar = "",
        source_updated_at = 0L,
        extra_json = (object?)null,
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record WriterFixture(
        DataSyncRepository Repository,
        EncryptedOutboxProtector Protector,
        IncrementalOutboxWriter Writer,
        ParseJob Job,
        ParserResultDocument Result);

    private sealed class ThrowingCommitObserver : IIncrementalCommitObserver
    {
        public Task BeforeParseCompletionAsync(CancellationToken cancellationToken) =>
            throw new IOException("Simulated crash after Outbox insertion.");
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
}
