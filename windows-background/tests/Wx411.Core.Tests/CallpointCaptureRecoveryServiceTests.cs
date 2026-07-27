using Wx411.Core.Windows;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Wx411.Core.Tests;

public sealed class CallpointCaptureRecoveryServiceTests
{
    [Fact]
    public async Task PendingVaultMatchCompletesWithoutAttachingAndDeletesTicketAfterOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wx411-capture-service-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(root, "output");
        var vaultRoot = Path.Combine(root, "vault");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "message_0.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "sqlcipher4_raw_key.db"), databasePath);
        var encrypted = File.ReadAllBytes(databasePath);
        var key = Convert.FromHexString(
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var saltFingerprintBytes = SHA256.HashData(encrypted.AsSpan(0, 16));
        var saltFingerprint = Convert.ToHexString(saltFingerprintBytes).ToLowerInvariant();
        var vault = new PendingCaptureVault(vaultRoot, new PassthroughProtector());
        var recordId = vault.Save(
            saltFingerprint,
            CallpointProfiles.Preferred.ModuleSha256,
            "sqlite3_key_equiv",
            key);
        var keySink = new RecordingKeySink();
        var service = new CallpointCaptureRecoveryService(
            () => throw new InvalidOperationException("backend must not be created"),
            vault,
            keySink);
        var selected = new DatabaseSource(databasePath, encrypted.LongLength);

        try
        {
            var result = await service.CaptureAndDecryptAsync(
                new RecoveryProcessSelection(null, "automatic", ScanAll: true),
                selected,
                [selected],
                outputDirectory,
                new Progress<RecoveryProgress>(),
                CancellationToken.None);

            var outputPath = Assert.Single(result.OutputPaths);
            var match = Assert.Single(result.Matches);
            Assert.Equal(databasePath, match.DatabaseId);
            Assert.Empty(result.UnmatchedDatabasePaths);
            Assert.Empty(result.FailedDatabasePaths);
            Assert.Equal(new[] { recordId }, result.LoadedPendingCaptureTicketIds);
            SqliteIntegrityChecker.VerifyFile(outputPath);
            Assert.Empty(vault.LoadMatching(
                saltFingerprint,
                CallpointProfiles.Preferred.ModuleSha256));
            Assert.Empty(Directory.EnumerateFiles(vaultRoot, "*.capture", SearchOption.AllDirectories));
            var stored = Assert.Single(keySink.Records);
            Assert.Equal(databasePath, stored.Metadata.DatabasePath);
            Assert.Equal("sqlite3_key_equiv", stored.Metadata.CallpointName);
            Assert.Equal(key, stored.Key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(saltFingerprintBytes);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedExportDoesNotPersistValidatedKey()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wx411-capture-service-{Guid.NewGuid():N}");
        var outputPathThatIsAFile = Path.Combine(root, "not-a-directory");
        var vaultRoot = Path.Combine(root, "vault");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(outputPathThatIsAFile, "occupied");
        var databasePath = Path.Combine(root, "message_0.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "sqlcipher4_raw_key.db"), databasePath);
        var encrypted = File.ReadAllBytes(databasePath);
        var key = Convert.FromHexString(
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var fingerprintBytes = SHA256.HashData(encrypted.AsSpan(0, 16));
        var fingerprint = Convert.ToHexString(fingerprintBytes).ToLowerInvariant();
        var vault = new PendingCaptureVault(vaultRoot, new PassthroughProtector());
        vault.Save(
            fingerprint,
            CallpointProfiles.Preferred.ModuleSha256,
            "sqlite3_key_equiv",
            key);
        var keySink = new RecordingKeySink();
        var service = new CallpointCaptureRecoveryService(
            () => throw new InvalidOperationException("backend must not be created"),
            vault,
            keySink);
        var selected = new DatabaseSource(databasePath, encrypted.LongLength);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CaptureAndDecryptAsync(
                    new RecoveryProcessSelection(null, "automatic", ScanAll: true),
                    selected,
                    [selected],
                    outputPathThatIsAFile,
                    new Progress<RecoveryProgress>(),
                    CancellationToken.None));

            Assert.Empty(keySink.Records);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(fingerprintBytes);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LiveCaptureDoesNotReportNewlySavedPendingTicket()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wx411-capture-service-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(root, "output");
        var vaultRoot = Path.Combine(root, "vault");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "message_0.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "sqlcipher4_raw_key.db"), databasePath);
        var encrypted = File.ReadAllBytes(databasePath);
        var key = Convert.FromHexString(
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var vault = new PendingCaptureVault(vaultRoot, new PassthroughProtector());
        using var candidate = new CapturedKeyMaterial(
            "sqlite3_key_equiv",
            HitRva: 0,
            RegisterValues: string.Empty,
            Pid: 1,
            CapturedAt: DateTime.UtcNow)
        {
            KeyData = key.ToArray(),
            KeyLength = key.Length,
        };
        var service = new CallpointCaptureRecoveryService(
            () => new FakeCaptureBackend(candidate),
            vault);
        var selected = new DatabaseSource(databasePath, encrypted.LongLength);

        try
        {
            var result = await service.CaptureAndDecryptAsync(
                new RecoveryProcessSelection(1, "fixture"),
                selected,
                [selected],
                outputDirectory,
                new Progress<RecoveryProgress>(),
                CancellationToken.None);

            SqliteIntegrityChecker.VerifyFile(Assert.Single(result.OutputPaths));
            Assert.Empty(result.LoadedPendingCaptureTicketIds);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureAndDecryptHonorsCancellationBeforeSnapshotOrAttach()
    {
        var vault = new PendingCaptureVault(
            Path.Combine(Path.GetTempPath(), $"wx411-capture-service-{Guid.NewGuid():N}"),
            new PassthroughProtector());
        var service = new CallpointCaptureRecoveryService(
            () => new FakeCaptureBackend(null),
            vault);
        var selected = new DatabaseSource("missing.db", 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.CaptureAndDecryptAsync(
                new RecoveryProcessSelection(null, "automatic", ScanAll: true),
                selected,
                [selected],
                Path.GetTempPath(),
                new Progress<RecoveryProgress>(),
                cancellation.Token));
    }

    [Fact]
    public async Task BackendFailureCodeIsPreservedWhenNoCandidateWasCaptured()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wx411-capture-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "message_0.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "sqlcipher4_raw_key.db"), databasePath);
        using var backendFailure = new CapturedKeyMaterial(
            "sqlite3_key_equiv",
            HitRva: 0,
            RegisterValues: string.Empty,
            Pid: 1,
            CapturedAt: DateTime.UtcNow)
        {
            Error = "early-attach:capture-timeout",
        };
        var service = new CallpointCaptureRecoveryService(
            () => new FakeCaptureBackend(backendFailure),
            new PendingCaptureVault(Path.Combine(root, "vault"), new PassthroughProtector()));
        var selected = new DatabaseSource(databasePath, new FileInfo(databasePath).Length);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CaptureAndDecryptAsync(
                    new RecoveryProcessSelection(1, "fixture"),
                    selected,
                    [selected],
                    Path.Combine(root, "output"),
                    new Progress<RecoveryProgress>(),
                    CancellationToken.None));

            Assert.Contains("early-attach:capture-timeout", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedLoadedModuleFailsBeforeCaptureBackendIsCreated()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wx411-capture-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "message_0.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "sqlcipher4_raw_key.db"), databasePath);
        var backendCreations = 0;
        var service = new CallpointCaptureRecoveryService(
            () =>
            {
                backendCreations++;
                return new FakeCaptureBackend(null);
            },
            new PendingCaptureVault(Path.Combine(root, "vault"), new PassthroughProtector()),
            validatedKeySink: null,
            (_, _) => PeCallpointLocator.ValidateIdentity(
                "9.9.9.9",
                new string('0', 64)));
        var selected = new DatabaseSource(databasePath, new FileInfo(databasePath).Length);

        try
        {
            var exception = await Assert.ThrowsAsync<UnsupportedModuleException>(() =>
                service.CaptureAndDecryptAsync(
                    new RecoveryProcessSelection(7, "fixture"),
                    selected,
                    [selected],
                    Path.Combine(root, "output"),
                    new Progress<RecoveryProgress>(),
                    CancellationToken.None));

            Assert.Equal("unsupported_module", exception.Code);
            Assert.Equal(0, backendCreations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnqueueNewCaptureTargetsAddsOnlyNewPidsInInputPriorityOrder()
    {
        var queue = new Queue<RecoveryProcessSelection>();
        var scheduledPids = new HashSet<int> { 10 };

        var addedPids = CallpointCaptureRecoveryService.EnqueueNewCaptureTargets(
            queue,
            scheduledPids,
            [
                new RecoveryProcessSelection(10, "already-tried"),
                new RecoveryProcessSelection(20, "database-owner"),
                new RecoveryProcessSelection(30, "new-worker"),
                new RecoveryProcessSelection(20, "duplicate-owner"),
                new RecoveryProcessSelection(null, "invalid"),
            ]);

        Assert.Equal(new[] { 20, 30 }, addedPids);
        Assert.Equal(new[] { 20, 30 }, queue.Select(target => target.Pid!.Value));
        Assert.Equal(new[] { 10, 20, 30 }, scheduledPids.Order());
    }

    [Fact]
    public void AutomaticDiscoveryRefreshesOnlyAfterScheduledQueueIsDrained()
    {
        Assert.False(CallpointCaptureRecoveryService.ShouldRefreshCaptureTargets(
            scanAll: false,
            queuedTargetCount: 0));
        Assert.False(CallpointCaptureRecoveryService.ShouldRefreshCaptureTargets(
            scanAll: true,
            queuedTargetCount: 4));
        Assert.True(CallpointCaptureRecoveryService.ShouldRefreshCaptureTargets(
            scanAll: true,
            queuedTargetCount: 0));
    }

    [Theory]
    [InlineData(false, 0, 10, 0, 5, false)]
    [InlineData(false, 1, 4, 0, 5, false)]
    [InlineData(false, 1, 5, 0, 5, true)]
    [InlineData(true, 0, 0, 0, 5, true)]
    public void MatchedDatabaseIdleClosesOnlyTheCurrentCaptureBatch(
        bool ready,
        int pendingMatches,
        long nowSeconds,
        long activitySeconds,
        long idleSeconds,
        bool expected)
    {
        var frequency = Stopwatch.Frequency;

        Assert.Equal(expected, CallpointCaptureRecoveryService.ShouldStopCurrentCapture(
            ready,
            pendingMatches,
            nowSeconds * frequency,
            activitySeconds * frequency,
            TimeSpan.FromSeconds(idleSeconds),
            frequency));
    }

    [Fact]
    public void LateFirstMatchStartsANewIdleWindow()
    {
        long now = 0;
        var signal = new CallpointCaptureRecoveryService.CaptureReadySignal(
            initial: false,
            pendingMatches: 0,
            getTimestamp: () => now,
            timestampFrequency: 1);
        var targetStartedAt = now;

        now = 10;
        signal.Update(ready: false, pendingMatches: 1);

        Assert.False(signal.ShouldStopCurrentCapture(
            targetStartedAt,
            TimeSpan.FromSeconds(5)));
        now = 15;
        Assert.True(signal.ShouldStopCurrentCapture(
            targetStartedAt,
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void CandidateProgressDistinguishesTotalPendingAndUnmatchedDatabases()
    {
        var detail = CallpointCaptureRecoveryService.FormatCandidateDatabaseProgress(
            "sqlite3_key_equiv",
            total: 18,
            pending: 12,
            unmatched: 6);

        Assert.Equal(
            "调用点=sqlite3_key_equiv; 数据库总数=18; 已暂存=12; 尚未命中=6",
            detail);
    }

    [Fact]
    public void EnqueueRefreshedCaptureTargetsSkipsNewPidsWithoutLoadedModule()
    {
        var queue = new Queue<RecoveryProcessSelection>();
        var scheduledPids = new HashSet<int> { 10 };

        var update = CallpointCaptureRecoveryService.EnqueueRefreshedCaptureTargets(
            queue,
            scheduledPids,
            [
                new RecoveryProcessSelection(10, "already-tried"),
                new RecoveryProcessSelection(20, "helper-without-module"),
                new RecoveryProcessSelection(30, "main-with-module"),
                new RecoveryProcessSelection(40, "another-helper"),
            ],
            (pid, moduleName) => pid == 30 && moduleName == "Weixin.dll");

        Assert.Equal(new[] { 30 }, update.AddedPids);
        Assert.Equal(new[] { 20, 40 }, update.SkippedPids);
        Assert.Equal(new[] { 30 }, queue.Select(target => target.Pid!.Value));
        Assert.Equal(new[] { 10, 30 }, scheduledPids.Order());
    }

    [Fact]
    public void RefreshedPidCanBeQueuedAfterItsModuleLoadsLater()
    {
        var queue = new Queue<RecoveryProcessSelection>();
        var scheduledPids = new HashSet<int>();
        var target = new RecoveryProcessSelection(20, "late-main");

        var beforeLoad = CallpointCaptureRecoveryService.EnqueueRefreshedCaptureTargets(
            queue,
            scheduledPids,
            [target],
            (_, _) => false);
        var afterLoad = CallpointCaptureRecoveryService.EnqueueRefreshedCaptureTargets(
            queue,
            scheduledPids,
            [target],
            (_, _) => true);

        Assert.Equal(new[] { 20 }, beforeLoad.SkippedPids);
        Assert.Empty(beforeLoad.AddedPids);
        Assert.Equal(new[] { 20 }, afterLoad.AddedPids);
        Assert.Empty(afterLoad.SkippedPids);
        Assert.Equal(new[] { 20 }, queue.Select(item => item.Pid!.Value));
        Assert.Equal(new[] { 20 }, scheduledPids);
    }

    [Fact]
    public async Task RefreshedDiscoveryRetriesSkippedPidWithinModuleLoadGracePeriod()
    {
        var queue = new Queue<RecoveryProcessSelection>();
        var scheduledPids = new HashSet<int>();
        var moduleChecks = 0;

        var update = await InvokeWaitForRefreshedCaptureTargetsAsync(
            queue,
            scheduledPids,
            () => [new RecoveryProcessSelection(20, "late-main")],
            (_, _) => ++moduleChecks >= 2,
            waitTimeout: TimeSpan.FromSeconds(1),
            retryDelay: TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.Equal(2, moduleChecks);
        Assert.Equal(new[] { 20 }, update.AddedPids);
        Assert.Empty(update.SkippedPids);
        Assert.Equal(new[] { 20 }, queue.Select(item => item.Pid!.Value));
        Assert.Equal(new[] { 20 }, scheduledPids);
    }

    [Fact]
    public async Task RefreshedDiscoveryStopsAtGraceDeadlineWithoutSchedulingAuxiliaryPid()
    {
        var queue = new Queue<RecoveryProcessSelection>();
        var scheduledPids = new HashSet<int>();

        var update = await InvokeWaitForRefreshedCaptureTargetsAsync(
            queue,
            scheduledPids,
            () => [new RecoveryProcessSelection(20, "helper")],
            (_, _) => false,
            waitTimeout: TimeSpan.Zero,
            retryDelay: TimeSpan.Zero,
            CancellationToken.None);

        Assert.Empty(update.AddedPids);
        Assert.Equal(new[] { 20 }, update.SkippedPids);
        Assert.Empty(queue);
        Assert.Empty(scheduledPids);
    }

    private static async Task<CallpointCaptureRecoveryService.CaptureTargetRefreshUpdate>
        InvokeWaitForRefreshedCaptureTargetsAsync(
            Queue<RecoveryProcessSelection> targetQueue,
            HashSet<int> scheduledPids,
            Func<IReadOnlyList<RecoveryProcessSelection>> discoverTargets,
            Func<int, string, bool> hasLoadedModule,
            TimeSpan waitTimeout,
            TimeSpan retryDelay,
            CancellationToken cancellationToken)
    {
        var method = typeof(CallpointCaptureRecoveryService).GetMethod(
            "WaitForRefreshedCaptureTargetsAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<CallpointCaptureRecoveryService.CaptureTargetRefreshUpdate>>(
            method!.Invoke(
                null,
                [
                    targetQueue,
                    scheduledPids,
                    discoverTargets,
                    hasLoadedModule,
                    waitTimeout,
                    retryDelay,
                    cancellationToken,
                ]));
        return await task;
    }

    private sealed class PassthroughProtector : ICapturePayloadProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
            plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy) =>
            ciphertext.ToArray();
    }

    private sealed class RecordingKeySink : IValidatedDatabaseKeySink
    {
        internal List<(ValidatedDatabaseKeyMetadata Metadata, byte[] Key)> Records { get; } = [];

        public void Store(ValidatedDatabaseKeyMetadata metadata, ReadOnlySpan<byte> key) =>
            Records.Add((metadata, key.ToArray()));
    }
}
