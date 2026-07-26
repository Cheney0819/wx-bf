using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopPet.Background.Contracts;
using DesktopPet.DataSync.Persistence;
using DesktopPet.DataSync.Security;

namespace DesktopPet.DataSync.Tests;

public sealed class HandoffManifestImporterTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-handoff-import-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CommitPrecedesAcceptedMarkerAndReimportIsIdempotent()
    {
        var fixture = await CreateManifestAsync(("message/message_0.db", "message-db"));
        await using var repository = await OpenRepositoryAsync();
        var importer = CreateImporter(repository);

        var first = await importer.ImportAsync(fixture.Path, default);
        var second = await importer.ImportAsync(fixture.Path, default);

        Assert.Equal(first.SourceSetId, second.SourceSetId);
        Assert.Single(await repository.ListManifestsAsync(default));
        Assert.Single(Directory.EnumerateFiles(AcceptedRoot(), "*.json"));
        Assert.NotNull(await repository.GetParseJobAsync(first.JobId, default));
    }

    [Fact]
    public async Task ImportAfterAcceptancePublishCrashRepairsMarkerWithoutDuplicateJob()
    {
        var fixture = await CreateManifestAsync(("message/message_0.db", "message-db"));
        await using var repository = await OpenRepositoryAsync();
        var interrupted = new HandoffManifestImporter(
            repository,
            GenerationRoot(),
            new ThrowingAcceptancePublisher(),
            TimeProvider.System);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            interrupted.ImportAsync(fixture.Path, default));
        Assert.Single(await repository.ListManifestsAsync(default));
        Assert.False(Directory.Exists(AcceptedRoot()));

        var repaired = await CreateImporter(repository).ImportAsync(fixture.Path, default);

        Assert.Single(await repository.ListManifestsAsync(default));
        Assert.Single(Directory.EnumerateFiles(AcceptedRoot(), "*.json"));
        Assert.NotNull(await repository.GetParseJobAsync(repaired.JobId, default));
    }

    [Fact]
    public async Task NewerGenerationReplacesOnlyMatchingRelativePathInSourceSet()
    {
        var first = await CreateManifestAsync(
            ("message/message_0.db", "message-v1"),
            ("contact/contact.db", "contact-v1"));
        await using var repository = await OpenRepositoryAsync();
        var importer = CreateImporter(repository);
        var firstImport = await importer.ImportAsync(first.Path, default);

        var second = await CreateManifestAsync(
            [("message/message_0.db", "message-v2")],
            createdAtUtc: first.Manifest.CreatedAtUtc.AddMinutes(1));
        var secondImport = await importer.ImportAsync(second.Path, default);
        var inputs = await repository.ListParseJobInputsAsync(secondImport.JobId, default);

        Assert.NotEqual(firstImport.SourceSetId, secondImport.SourceSetId);
        Assert.Equal(2, inputs.Count);
        Assert.Contains(inputs, input =>
            input.RelativePath == "contact/contact.db" &&
            input.GenerationId == first.Manifest.Databases[0].GenerationId);
        Assert.Contains(inputs, input =>
            input.RelativePath == "message/message_0.db" &&
            input.GenerationId == second.Manifest.Databases[0].GenerationId);
    }

    [Fact]
    public async Task ShaMismatchIsRejected()
    {
        var fixture = await CreateManifestAsync(("message/message_0.db", "message-db"));
        await File.AppendAllTextAsync(fixture.Manifest.Databases[0].PlaintextPath, "tampered");

        await AssertRejectedAsync<CryptographicException>(fixture.Path);
    }

    [Fact]
    public async Task ManifestIdMismatchIsRejected()
    {
        var fixture = await CreateManifestAsync(("message/message_0.db", "message-db"));
        var invalid = fixture.Manifest with { ManifestId = new string('a', 64) };
        await WriteManifestAsync(fixture.Path, invalid);

        await AssertRejectedAsync<InvalidDataException>(fixture.Path);
    }

    [Fact]
    public async Task FilenameMustMatchManifestId()
    {
        var fixture = await CreateManifestAsync(("message/message_0.db", "message-db"));
        var renamed = Path.Combine(ReadyRoot(), new string('b', 64) + ".json");
        File.Move(fixture.Path, renamed);

        await AssertRejectedAsync<InvalidDataException>(renamed);
    }

    [Fact]
    public async Task DuplicateRelativePathIsRejected()
    {
        var fixture = await CreateManifestAsync(("message/message_0.db", "message-db"));
        var duplicate = fixture.Manifest with
        {
            Databases = [fixture.Manifest.Databases[0], fixture.Manifest.Databases[0]],
        };
        duplicate = duplicate with { ManifestId = ComputeManifestId(duplicate.EpochId, duplicate.Databases) };
        var path = Path.Combine(ReadyRoot(), duplicate.ManifestId + ".json");
        await WriteManifestAsync(path, duplicate);

        await AssertRejectedAsync<InvalidDataException>(path);
    }

    [Fact]
    public async Task UnknownSchemaIsRejected()
    {
        var fixture = await CreateManifestAsync(("message/message_0.db", "message-db"));
        await WriteManifestAsync(fixture.Path, fixture.Manifest with { SchemaVersion = 2 });

        await AssertRejectedAsync<InvalidDataException>(fixture.Path);
    }

    [Fact]
    public async Task JsonAboveOneMebibyteIsRejectedBeforeParsing()
    {
        Directory.CreateDirectory(ReadyRoot());
        var path = Path.Combine(ReadyRoot(), new string('a', 64) + ".json");
        await File.WriteAllBytesAsync(path, new byte[1024 * 1024 + 1]);

        await AssertRejectedAsync<InvalidDataException>(path);
    }

    [Theory]
    [InlineData("../message_0.db")]
    [InlineData(@"..\message_0.db")]
    [InlineData("message/../message_0.db")]
    [InlineData(@"C:\absolute\message_0.db")]
    [InlineData(@"\\server\share\message_0.db")]
    public async Task PortableTraversalIsRejected(string relativePath)
    {
        var fixture = await CreateManifestAsync(("message/message_0.db", "message-db"));
        var item = fixture.Manifest.Databases[0] with { RelativePath = relativePath };
        var manifest = fixture.Manifest with { Databases = [item] };
        manifest = manifest with { ManifestId = ComputeManifestId(manifest.EpochId, manifest.Databases) };
        var path = Path.Combine(ReadyRoot(), manifest.ManifestId + ".json");
        await WriteManifestAsync(path, manifest);

        await AssertRejectedAsync<InvalidDataException>(path);
    }

    [Fact]
    public async Task PlaintextPathOutsideGenerationRootIsRejected()
    {
        var fixture = await CreateManifestAsync(("message/message_0.db", "message-db"));
        var outside = Path.Combine(_root, "outside.sqlite");
        await File.WriteAllTextAsync(outside, "message-db");
        var item = fixture.Manifest.Databases[0] with { PlaintextPath = outside };
        var manifest = fixture.Manifest with { Databases = [item] };
        manifest = manifest with { ManifestId = ComputeManifestId(manifest.EpochId, manifest.Databases) };
        var path = Path.Combine(ReadyRoot(), manifest.ManifestId + ".json");
        await WriteManifestAsync(path, manifest);

        await AssertRejectedAsync<InvalidDataException>(path);
    }

    [Fact]
    public async Task MissingGenerationIsRejected()
    {
        var fixture = await CreateManifestAsync(("message/message_0.db", "message-db"));
        File.Delete(fixture.Manifest.Databases[0].PlaintextPath);

        await AssertRejectedAsync<FileNotFoundException>(fixture.Path);
    }

    private HandoffManifestImporter CreateImporter(DataSyncRepository repository) => new(
        repository,
        GenerationRoot(),
        new HandoffAcceptancePublisher(AcceptedRoot(), TimeProvider.System),
        TimeProvider.System);

    private async Task<DataSyncRepository> OpenRepositoryAsync()
    {
        var repository = new DataSyncRepository(
            Path.Combine(_root, "sync.db"),
            TimeProvider.System,
            new EncryptedOutboxProtector(new XorTestProtector()));
        await repository.InitializeAsync(default);
        return repository;
    }

    private async Task AssertRejectedAsync<TException>(string manifestPath)
        where TException : Exception
    {
        await using var repository = await OpenRepositoryAsync();
        await Assert.ThrowsAsync<TException>(() =>
            CreateImporter(repository).ImportAsync(manifestPath, default));
        Assert.Empty(await repository.ListManifestsAsync(default));
    }

    private async Task<ManifestFixture> CreateManifestAsync(
        params (string RelativePath, string Content)[] databases) =>
        await CreateManifestAsync(databases, DateTimeOffset.UtcNow);

    private async Task<ManifestFixture> CreateManifestAsync(
        (string RelativePath, string Content)[] databases,
        DateTimeOffset createdAtUtc)
    {
        const string epochId = "epoch-1";
        var items = new List<DatabaseReadyItem>();
        foreach (var database in databases.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            var relativePath = database.RelativePath.Replace('\\', '/');
            var sha256 = Sha256(Encoding.UTF8.GetBytes(database.Content));
            var generationId = Sha256(Encoding.UTF8.GetBytes(
                $"{epochId}|{relativePath}|{sha256}"));
            var directory = Path.Combine(GenerationRoot(), generationId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Path.GetFileNameWithoutExtension(relativePath) + ".readable.sqlite");
            await File.WriteAllTextAsync(path, database.Content);
            items.Add(new DatabaseReadyItem(generationId, relativePath, path, sha256));
        }

        var manifestId = ComputeManifestId(epochId, items);
        var manifest = new DatabaseReadyManifest(1, manifestId, epochId, createdAtUtc, items);
        var manifestPath = Path.Combine(ReadyRoot(), manifestId + ".json");
        await WriteManifestAsync(manifestPath, manifest);
        return new ManifestFixture(manifestPath, manifest);
    }

    private static string ComputeManifestId(
        string epochId,
        IReadOnlyList<DatabaseReadyItem> items) =>
        Sha256(Encoding.UTF8.GetBytes(
            epochId + "|" + string.Join(
                "|",
                items.Select(item => $"{item.GenerationId}:{item.RelativePath}:{item.Sha256}"))));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task WriteManifestAsync(string path, DatabaseReadyManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
    }

    private string GenerationRoot() => Path.Combine(_root, "Recovery", "Generations");

    private string ReadyRoot() => Path.Combine(_root, "Handoff", "ready");

    private string AcceptedRoot() => Path.Combine(_root, "Handoff", "accepted");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record ManifestFixture(string Path, DatabaseReadyManifest Manifest);

    private sealed class ThrowingAcceptancePublisher : IHandoffAcceptancePublisher
    {
        public Task PublishAsync(
            HandoffAcceptedMarker marker,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException("Simulated crash before accepted rename.");
    }

    private sealed class XorTestProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
            ciphertext.ToArray();
    }
}
