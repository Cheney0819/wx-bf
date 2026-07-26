using System.Security.Cryptography;
using System.Text;
using DesktopPet.Background.Contracts;

namespace DesktopPet.Recovery.Tests;

public sealed class AtomicHandoffPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-handoff-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SameGenerationPublishesOneImmutableFileAndManifest()
    {
        var source = await WriteSourceAsync("one.sqlite", "database-one"u8.ToArray());
        var recovered = Recovered('a', "message/message_0.db", source);
        var publisher = CreatePublisher();

        var first = await publisher.PublishAsync("epoch-1", [recovered], default);
        var second = await publisher.PublishAsync("epoch-1", [recovered], default);

        Assert.Equal(first.ManifestId, second.ManifestId);
        Assert.Single(Directory.EnumerateFiles(GenerationRoot(), "*.sqlite", SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateFiles(ReadyRoot(), "*.json"));
        var item = Assert.Single(first.Databases);
        Assert.Equal(recovered.Sha256, item.Sha256);
        Assert.Equal(recovered.Sha256, await Sha256Async(item.PlaintextPath));
    }

    [Fact]
    public async Task DifferentGenerationCreatesDifferentManifest()
    {
        var firstSource = await WriteSourceAsync("one.sqlite", "database-one"u8.ToArray());
        var secondSource = await WriteSourceAsync("two.sqlite", "database-two"u8.ToArray());
        var publisher = CreatePublisher();

        var first = await publisher.PublishAsync(
            "epoch-1", [Recovered('a', "message/message_0.db", firstSource)], default);
        var second = await publisher.PublishAsync(
            "epoch-1", [Recovered('b', "message/message_0.db", secondSource)], default);

        Assert.NotEqual(first.ManifestId, second.ManifestId);
        Assert.Equal(2, Directory.EnumerateFiles(GenerationRoot(), "*.sqlite", SearchOption.AllDirectories).Count());
        Assert.Equal(2, Directory.EnumerateFiles(ReadyRoot(), "*.json").Count());
    }

    [Fact]
    public async Task CompletenessIsAuthenticatedByDistinctManifestIdentity()
    {
        var source = await WriteSourceAsync("one.sqlite", "database-one"u8.ToArray());
        var recovered = Recovered('a', "message/message_0.db", source);
        var publisher = CreatePublisher();

        var partial = await publisher.PublishAsync(
            "epoch-1",
            [recovered],
            requiredDatabasesComplete: false,
            default);
        var complete = await publisher.PublishAsync(
            "epoch-1",
            [recovered],
            requiredDatabasesComplete: true,
            default);

        Assert.Equal(2, partial.SchemaVersion);
        Assert.False(partial.RequiredDatabasesComplete);
        Assert.True(complete.RequiredDatabasesComplete);
        Assert.NotEqual(partial.ManifestId, complete.ManifestId);
        Assert.Equal(2, Directory.EnumerateFiles(ReadyRoot(), "*.json").Count());
    }

    [Fact]
    public async Task HandoffGenerationIdIsDerivedFromEpochPathAndContent()
    {
        var source = await WriteSourceAsync("one.sqlite", "database-one"u8.ToArray());
        var recovered = Recovered('a', @"message\message_0.db", source);

        var manifest = await CreatePublisher().PublishAsync("epoch-1", [recovered], default);

        var item = Assert.Single(manifest.Databases);
        var material = Encoding.UTF8.GetBytes(
            $"epoch-1|message/message_0.db|{recovered.Sha256}");
        var expected = Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        Assert.Equal(expected, item.GenerationId);
    }

    [Fact]
    public async Task CancelledPublishLeavesNoReadyManifest()
    {
        var source = await WriteSourceAsync("one.sqlite", "database-one"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreatePublisher().PublishAsync(
                "epoch-1",
                [Recovered('a', "message/message_0.db", source)],
                cancellation.Token));

        Assert.False(Directory.Exists(ReadyRoot()));
    }

    [Fact]
    public async Task SourceOutsideAllowedRootIsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.sqlite");
        await File.WriteAllTextAsync(outside, "outside");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreatePublisher().PublishAsync(
                    "epoch-1",
                    [Recovered('a', "message/message_0.db", outside)],
                    default));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("../message_0.db")]
    [InlineData(@"..\message_0.db")]
    [InlineData("message/../message_0.db")]
    [InlineData(@"message\..\message_0.db")]
    [InlineData("/absolute/message_0.db")]
    [InlineData(@"C:\absolute\message_0.db")]
    [InlineData(@"\\server\share\message_0.db")]
    public async Task UnsafeRelativePathIsRejected(string relativePath)
    {
        var source = await WriteSourceAsync("one.sqlite", "database-one"u8.ToArray());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreatePublisher().PublishAsync(
                "epoch-1",
                [Recovered('a', relativePath, source)],
                default));
    }

    private AtomicHandoffPublisher CreatePublisher() =>
        new(GenerationRoot(), ReadyRoot(), StagingRoot(), TimeProvider.System);

    private RecoveredDatabase Recovered(char idCharacter, string relativePath, string source) =>
        new(
            new string(idCharacter, 64),
            relativePath,
            source,
            Sha256Async(source).GetAwaiter().GetResult());

    private async Task<string> WriteSourceAsync(string name, byte[] content)
    {
        Directory.CreateDirectory(StagingRoot());
        var path = Path.Combine(StagingRoot(), name);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private string StagingRoot() => Path.Combine(_root, "staging");

    private string GenerationRoot() => Path.Combine(_root, "generations");

    private string ReadyRoot() => Path.Combine(_root, "handoff", "ready");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
