using DesktopPet.Background.Contracts;
using DesktopPet.Background.Infrastructure;
using DesktopPet.Recovery;
using DesktopPet.Recovery.Persistence;
using DesktopPet.Recovery.Security;
using DesktopPet.Recovery.Worker;
using System.Threading.Channels;
using System.Runtime.Versioning;

namespace DesktopPet.Recovery.Tests;

[SupportedOSPlatform("windows")]
public sealed class RecoveryCycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "desktop-pet-recovery-cycle-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingDataRootPublishesWebCompatiblePreflightWithoutStartingEpoch()
    {
        var paths = BackgroundPaths.ForRoot(_root);
        await using var repository = new RecoveryRepository(
            paths.RecoveryDatabase,
            TimeProvider.System);
        var telemetry = new RecordingTelemetryPublisher();
        var cycle = new RecoveryCycle(
            paths,
            repository,
            new WeChatIdentityProvider(),
            new FakeLocator(new(null, 0, 0, "data_root_missing")),
            new ValidatedKeyVault(
                Path.Combine(paths.RecoveryVault, "ValidatedKeys"),
                new XorProtector()),
            telemetry);

        var action = await cycle.RunAsync(
            RecoveryCycleTrigger.Startup,
            default);

        Assert.Equal(RecoveryActionKind.WaitPassively, action.Kind);
        Assert.Equal("data_root_missing", action.Reason);
        var draft = Assert.Single(telemetry.Events);
        Assert.Equal("client_v4_data_dir_result", draft.EventName);
        Assert.Equal("data_root_missing", draft.Code);
        Assert.Equal(0, draft.Metrics.GetProperty("candidateCount").GetInt32());
        Assert.Equal(0, draft.Metrics.GetProperty("databaseCount").GetInt32());
        Assert.False(File.Exists(paths.RecoveryDatabase));
    }

    [Fact]
    public async Task SelectedRootWatcherFollowsAccountChanges()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var locator = new FakeLocator(new(first, 1, 1, "data_root_discovered"));
        var watcher = new SelectedRootDatabaseWatcher(
            locator,
            TimeSpan.FromMilliseconds(10),
            TimeProvider.System);
        var channel = Channel.CreateUnbounded<RecoveryHint>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var running = watcher.RunAsync(channel.Writer, cancellation.Token);
        await Task.Delay(40, cancellation.Token);

        locator.Result = new(second, 1, 1, "data_root_discovered");
        await File.WriteAllBytesAsync(
            Path.Combine(second, "message_0.db"),
            new byte[4096],
            cancellation.Token);

        var hint = await channel.Reader.ReadAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Equal(RecoveryHintKind.DatabaseChanged, hint.Kind);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeLocator(WeChatDataRootResolution result)
        : IWeChatDataRootLocator
    {
        public WeChatDataRootResolution Result { get; set; } = result;

        public string? CurrentDataRoot => Result.DataRoot;

        public Task<WeChatDataRootResolution> LocateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingTelemetryPublisher
        : IOperationalTelemetryPublisher
    {
        public List<OperationalTelemetryDraft> Events { get; } = [];

        public Task PublishAsync(
            OperationalTelemetryDraft draft,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(draft);
            return Task.CompletedTask;
        }
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(
            ReadOnlySpan<byte> plaintext,
            ReadOnlySpan<byte> entropy) =>
            plaintext.ToArray();

        public byte[] Unprotect(
            ReadOnlySpan<byte> ciphertext,
            ReadOnlySpan<byte> entropy) =>
            ciphertext.ToArray();
    }
}
