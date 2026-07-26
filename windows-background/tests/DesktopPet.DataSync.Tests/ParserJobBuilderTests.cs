using System.Security.Cryptography;
using DesktopPet.DataSync.Persistence;

namespace DesktopPet.DataSync.Tests;

public sealed class ParserJobBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-parser-job-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReconstructsRelativePathsAndPreservesHashes()
    {
        var source = await WriteSourceAsync("generation.sqlite", "readable-database"u8.ToArray());
        var input = Input("message/message_0.db", source);
        var builder = new ParserJobBuilder(Path.Combine(_root, "jobs"));

        var built = await builder.BuildAsync(Job(), [input], 5000, default);

        var copied = Path.Combine(built.InputRoot, "message", "message_0.db");
        Assert.True(File.Exists(copied));
        Assert.Equal(input.Sha256, await Sha256Async(copied));
        Assert.Equal(copied, built.Manifest.Databases[0].Path);
        Assert.Equal("message/message_0.db", built.Manifest.Databases[0].RelativePath);
        Assert.True(File.Exists(built.JobManifestPath));
    }

    [Theory]
    [InlineData("../message.db")]
    [InlineData(@"..\message.db")]
    [InlineData(@"C:\message.db")]
    [InlineData(@"\\server\share\message.db")]
    public async Task RejectsPortableUnsafeRelativePath(string relativePath)
    {
        var source = await WriteSourceAsync("generation.sqlite", "readable-database"u8.ToArray());
        var builder = new ParserJobBuilder(Path.Combine(_root, "jobs"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            builder.BuildAsync(Job(), [Input(relativePath, source)], 5000, default));
    }

    [Fact]
    public async Task ExistingJobDirectoryIsNeverOverwritten()
    {
        var source = await WriteSourceAsync("generation.sqlite", "readable-database"u8.ToArray());
        var builder = new ParserJobBuilder(Path.Combine(_root, "jobs"));
        await builder.BuildAsync(Job(), [Input("message/message_0.db", source)], 5000, default);

        await Assert.ThrowsAsync<IOException>(() =>
            builder.BuildAsync(Job(), [Input("message/message_0.db", source)], 5000, default));
    }

    [Fact]
    public async Task SourceHashDriftIsRejectedBeforeJobPublish()
    {
        var source = await WriteSourceAsync("generation.sqlite", "readable-database"u8.ToArray());
        var input = Input("message/message_0.db", source) with { Sha256 = new string('0', 64) };
        var builder = new ParserJobBuilder(Path.Combine(_root, "jobs"));

        await Assert.ThrowsAsync<CryptographicException>(() =>
            builder.BuildAsync(Job(), [input], 5000, default));
        Assert.False(Directory.Exists(Path.Combine(_root, "jobs", "job-1")));
    }

    private ParseJob Job() => new(
        "job-1",
        "source-1",
        ParseJobState.Leased,
        "worker-a",
        DateTimeOffset.UtcNow.AddMinutes(3),
        1,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private ParseJobInput Input(string relativePath, string source) => new(
        "job-1",
        new string('a', 64),
        relativePath,
        source,
        Sha256Async(source).GetAwaiter().GetResult(),
        0);

    private async Task<string> WriteSourceAsync(string name, byte[] content)
    {
        var directory = Path.Combine(_root, "generations");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
