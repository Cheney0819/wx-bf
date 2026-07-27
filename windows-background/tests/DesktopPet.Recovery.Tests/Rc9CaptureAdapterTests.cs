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
    public async Task PartialCapturePreservesUnmatchedAndFailedDatabasePaths()
    {
        var encrypted = await WriteAsync("data/message/message_0.db", "encrypted"u8.ToArray());
        var requiredFailed = await WriteAsync("data/session/session.db", "required-failed"u8.ToArray());
        var unmatched = await WriteAsync("data/contact/contact.db", "unmatched"u8.ToArray());
        var failed = await WriteAsync("data/favorite/favorite.db", "failed"u8.ToArray());
        var plaintext = await WriteAsync("output/message_0.sqlite", "plaintext"u8.ToArray());
        var sources = new[]
        {
            new DatabaseSource(encrypted, new FileInfo(encrypted).Length),
            new DatabaseSource(requiredFailed, new FileInfo(requiredFailed).Length),
            new DatabaseSource(unmatched, new FileInfo(unmatched).Length),
            new DatabaseSource(failed, new FileInfo(failed).Length),
        };
        var result = new CaptureRecoveryResult(
            [plaintext],
            [new DatabaseCaptureMatch(
                encrypted,
                new CipherProfileMatch(SqlCipher4.Profile, [1]),
                "raw",
                "sqlite3_key_equiv")],
            [unmatched],
            [failed, requiredFailed],
            []);
        var adapter = CreateAdapter(
            sources,
            () => [],
            (_, _, _, _, _, _) => Task.FromResult(result));

        var observation = await adapter.CaptureAsync(Epoch(), default);

        Assert.Equal([unmatched], observation.UnmatchedDatabasePaths);
        Assert.Equal([failed, requiredFailed], observation.FailedDatabasePaths);
        Assert.False(observation.RequiredDatabasesComplete);
        Assert.Single(observation.Databases);
    }

    [Fact]
    public async Task BoundRuntimeScansAllProcessesWithinItsSessionAndExecutablePath()
    {
        var encrypted = await WriteAsync("data/message/message_0.db", "encrypted"u8.ToArray());
        var source = new DatabaseSource(encrypted, new FileInfo(encrypted).Length);
        var selections = new List<RecoveryProcessSelection>();
        var executable = Path.GetFullPath(Path.Combine(_root, "bin", "Weixin.exe"));
        var bound = new RecoveryProcessSelection(
            42,
            "Weixin.exe",
            ScanAll: false,
            SessionId: 7,
            ExecutablePath: executable);
        var adapter = CreateAdapter(
            [source],
            () => [],
            (selection, _, _, _, _, _) =>
            {
                selections.Add(selection);
                throw new InvalidOperationException("fixture");
            },
            bound);

        _ = await adapter.CaptureAsync(
            Epoch(),
            RecoveryCaptureTarget.BoundProcess,
            default);
        _ = await adapter.CaptureAsync(
            Epoch(),
            RecoveryCaptureTarget.RestartedProcess,
            default);

        Assert.Null(selections[0].Pid);
        Assert.True(selections[0].ScanAll);
        Assert.Equal(7, selections[0].SessionId);
        Assert.Equal(executable, selections[0].ExecutablePath);
        Assert.Null(selections[1].Pid);
        Assert.True(selections[1].ScanAll);
        Assert.Equal(7, selections[1].SessionId);
        Assert.Equal(executable, selections[1].ExecutablePath);
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

    [Fact]
    public async Task PendingTicketFromAnotherEpochDoesNotSuppressCurrentCapture()
    {
        var encrypted = await WriteAsync("data/message/message_0.db", "encrypted"u8.ToArray());
        var snapshots = new Queue<IReadOnlyList<string>>(
            [[], [new string('c', 64)]]);
        var adapter = CreateAdapter(
            [new DatabaseSource(encrypted, new FileInfo(encrypted).Length)],
            () => snapshots.Dequeue(),
            (_, _, _, _, _, _) => throw new InvalidOperationException("localized text"));

        var observation = await adapter.CaptureAsync(
            Epoch("different-root-and-epoch"),
            default);

        Assert.False(observation.HasPendingCapture);
        Assert.Equal("capture_no_result", observation.FailureCode);
    }

    [Theory]
    [InlineData("early-attach:module-timeout", "capture_module_timeout")]
    [InlineData("early-attach:capture-timeout", "capture_callpoint_timeout")]
    [InlineData("unsupported_module: fixture", "unsupported_module")]
    [InlineData("breakpoint_restore_failed: fixture", "breakpoint_restore_failed")]
    public async Task EarlyAttachFailurePreservesStageCode(
        string failureMessage,
        string expectedCode)
    {
        var encrypted = await WriteAsync("data/message/message_0.db", "encrypted"u8.ToArray());
        var adapter = CreateAdapter(
            [new DatabaseSource(encrypted, new FileInfo(encrypted).Length)],
            () => [],
            (_, _, _, _, _, _) => throw new InvalidOperationException(failureMessage));

        var observation = await adapter.CaptureAsync(Epoch(), default);

        Assert.Equal(expectedCode, observation.FailureCode);
    }

    private Rc9CaptureAdapter CreateAdapter(
        IReadOnlyList<DatabaseSource> databases,
        Func<IReadOnlyList<string>> snapshotPendingIds,
        Rc9CaptureOperation capture,
        RecoveryProcessSelection? boundProcess = null) =>
        new(
            Path.Combine(_root, "data"),
            Path.Combine(_root, "output"),
            new Progress<RecoveryProgress>(),
            () => databases,
            snapshotPendingIds,
            capture,
            boundProcess);

    private RecoveryEpoch Epoch(string? rootIdentity = null) =>
        new(
            "epoch-1",
            new RecoveryEpochIdentity(
                "4.1.0",
                rootIdentity ?? RootIdentity(Path.Combine(_root, "data"))),
            0,
            false,
            RecoveryMode.CapturingCurrentProcess,
            null,
            true,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static string RootIdentity(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

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
