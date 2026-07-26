using System.Security.Cryptography;
using DesktopPet.Background.Contracts;
using DesktopPet.Recovery.Persistence;
using Wx411.Core;

namespace DesktopPet.Recovery.Tests;

public sealed class Rc9CaptureAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-rc9-adapter-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SuccessfulCaptureReturnsStructuredRecoveredDatabase()
    {
        var encrypted = await WriteAsync("data/message/message_0.db", "encrypted"u8.ToArray());
        var plaintext = await WriteAsync("output/message_0.sqlite", "plaintext"u8.ToArray());
        var source = new DatabaseSource(encrypted, new FileInfo(encrypted).Length);
        var result = new CaptureRecoveryResult(
            [plaintext],
            [new DatabaseCaptureMatch(
                encrypted,
                new CipherProfileMatch(SqlCipher4.Profile, [1]),
                "raw",
                "sqlite3_key_equiv")],
            [],
            [],
            []);
        var adapter = CreateAdapter(
            [source],
            () => [],
            (_, _, _, _, _, _) => Task.FromResult(result));

        var observation = await adapter.CaptureAsync(Epoch(), default);

        Assert.True(observation.HasValidatedKey);
        Assert.False(observation.HasPendingCapture);
        var recovered = Assert.Single(observation.Databases);
        Assert.Equal("message/message_0.db", recovered.RelativePath);
        Assert.Equal(await Sha256Async(plaintext), recovered.Sha256);
        Assert.Equal(64, recovered.GenerationId.Length);
    }

    [Fact]
    public async Task ThrownCaptureWithPendingTicketSuppressesRestart()
    {
        var encrypted = await WriteAsync("data/message/message_0.db", "encrypted"u8.ToArray());
        var snapshots = new Queue<IReadOnlyList<string>>(
            [[], [new string('c', 64)]]);
        var adapter = CreateAdapter(
            [new DatabaseSource(encrypted, new FileInfo(encrypted).Length)],
            () => snapshots.Dequeue(),
            (_, _, _, _, _, _) => throw new InvalidOperationException("localized text"));

        var observation = await adapter.CaptureAsync(Epoch(), default);

        Assert.False(observation.HasValidatedKey);
        Assert.True(observation.HasPendingCapture);
        Assert.Equal("capture_no_result", observation.FailureCode);
    }

    [Fact]
    public async Task MissingDatabaseCandidateReturnsStableFailureCode()
    {
        var adapter = CreateAdapter(
            [],
            () => [],
            (_, _, _, _, _, _) => throw new Xunit.Sdk.XunitException("must not capture"));

        var observation = await adapter.CaptureAsync(Epoch(), default);

        Assert.False(observation.HasValidatedKey);
        Assert.False(observation.HasPendingCapture);
        Assert.Equal("capture_no_database_candidates", observation.FailureCode);
    }

    private Rc9CaptureAdapter CreateAdapter(
        IReadOnlyList<DatabaseSource> databases,
        Func<IReadOnlyList<string>> snapshotPendingIds,
        Rc9CaptureOperation capture) =>
        new(
            Path.Combine(_root, "data"),
            Path.Combine(_root, "output"),
            new Progress<RecoveryProgress>(),
            () => databases,
            snapshotPendingIds,
            capture);

    private static RecoveryEpoch Epoch() =>
        new(
            "epoch-1",
            new RecoveryEpochIdentity("4.1.0", "root-a"),
            0,
            false,
            RecoveryMode.CapturingCurrentProcess,
            null,
            true,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private async Task<string> WriteAsync(string relativePath, byte[] content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
