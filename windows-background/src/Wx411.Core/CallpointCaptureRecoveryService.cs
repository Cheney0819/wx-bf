using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Wx411.Core.Windows;

namespace Wx411.Core;

public sealed class CallpointCaptureRecoveryService
{
    private static readonly TimeSpan CallpointCaptureTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan CaptureProcessWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CaptureRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DatabaseRefreshInterval = TimeSpan.FromMilliseconds(500);

    private readonly Func<ICallpointCaptureBackend> _captureBackendFactory;
    private readonly PendingCaptureVault _pendingCaptureVault;
    private readonly IValidatedDatabaseKeySink? _validatedKeySink;
    private readonly Func<int, string, ModuleIdentityValidation?> _inspectLoadedModule;
    private readonly ConsistentDatabaseExporter _exporter;

    public CallpointCaptureRecoveryService(
        Func<ICallpointCaptureBackend> captureBackendFactory,
        PendingCaptureVault pendingCaptureVault,
        IValidatedDatabaseKeySink? validatedKeySink = null)
        : this(
            captureBackendFactory,
            pendingCaptureVault,
            validatedKeySink,
            InspectLoadedModule)
    {
    }

    internal CallpointCaptureRecoveryService(
        Func<ICallpointCaptureBackend> captureBackendFactory,
        PendingCaptureVault pendingCaptureVault,
        IValidatedDatabaseKeySink? validatedKeySink,
        Func<int, string, ModuleIdentityValidation?> inspectLoadedModule)
    {
        ArgumentNullException.ThrowIfNull(captureBackendFactory);
        ArgumentNullException.ThrowIfNull(pendingCaptureVault);
        ArgumentNullException.ThrowIfNull(inspectLoadedModule);
        _captureBackendFactory = captureBackendFactory;
        _pendingCaptureVault = pendingCaptureVault;
        _validatedKeySink = validatedKeySink;
        _inspectLoadedModule = inspectLoadedModule;
        _exporter = new ConsistentDatabaseExporter();
    }

    public async Task<CaptureRecoveryResult> CaptureAndDecryptAsync(
        RecoveryProcessSelection process,
        DatabaseSource selectedDatabase,
        IReadOnlyList<DatabaseSource> databases,
        string outputDirectory,
        IProgress<RecoveryProgress> progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(selectedDatabase);
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(progress);
        token.ThrowIfCancellationRequested();

        progress.Report(new RecoveryProgress(
            4,
            "[1/7] 正在读取数据库轻量描述符…",
            "只读取 salt 和抽样页；登录期间新建或换代的数据库会在监听期自动加入。"));
        using var catalog = DatabaseProbeCatalog.Create(
            selectedDatabase.Path,
            databases.Select(database => database.Path),
            token);
        var counters = new CipherProfileProbeCounters();
        using var collector = new MultiDatabaseCaptureCollector(catalog.Descriptors);
        var vaultRecords = new Dictionary<string, HashSet<PendingVaultReference>>(StringComparer.OrdinalIgnoreCase);
        var loadedPendingCaptureTicketIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LoadPendingCapturesFromVault(
            _pendingCaptureVault,
            catalog,
            collector,
            counters,
            vaultRecords,
            loadedPendingCaptureTicketIds,
            progress,
            token);

        var ready = new CaptureReadySignal(collector.IsReadyForValidation);
        string? lastCaptureFailure = null;
        if (!ready.Value)
        {
            var resolution = await WaitForCaptureProcessSelectionsAsync(
                process,
                selectedDatabase.Path,
                progress,
                token);
            if (resolution.Targets.Count == 0)
                throw new InvalidOperationException("没有可用于精准定位的目标进程。请启动目标程序后重试。");
            if (resolution.DatabaseOwnerPids.Count > 0)
            {
                progress.Report(new RecoveryProgress(
                    6,
                    "[1/7] 已锁定数据库句柄进程。",
                    $"文件句柄 PID 优先: {string.Join(", ", resolution.DatabaseOwnerPids)}"));
            }

            var targetQueue = new Queue<RecoveryProcessSelection>();
            var scheduledPids = new HashSet<int>();
            EnqueueNewCaptureTargets(targetQueue, scheduledPids, resolution.Targets);
            while (!ready.Value && targetQueue.TryDequeue(out var targetProcess))
            {
                token.ThrowIfCancellationRequested();
                if (targetProcess.Pid is not int targetPid) continue;
                var moduleIdentity = _inspectLoadedModule(targetPid, "Weixin.dll");
                if (moduleIdentity?.IsUnsupported == true)
                    throw new UnsupportedModuleException(moduleIdentity.Error);
                progress.Report(new RecoveryProgress(
                    8,
                    $"[2/7] 正在附加 PID {targetPid}…",
                    "观察点将在模块加载后设置；候选只复制到有界通道。"));
                using var channel = new CapturedCandidateChannel();
                using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var gate = new SemaphoreSlim(1, 1);
                var consumer = ConsumeCandidatesAsync(
                    channel,
                    catalog,
                    collector,
                    counters,
                    _pendingCaptureVault,
                    vaultRecords,
                    progress,
                    ready,
                    gate,
                    token);
                var periodic = RefreshCatalogPeriodicallyAsync(
                    catalog,
                    collector,
                    counters,
                    progress,
                    ready,
                    gate,
                    refreshCancellation.Token);
                try
                {
                    var backend = _captureBackendFactory();
                    try
                    {
                        var captureResult = await backend.CaptureToChannelWhenModuleLoadsAsync(
                            targetPid,
                            "Weixin.dll",
                            CallpointProfiles.Preferred.Callpoints,
                            CaptureProcessWaitTimeout,
                            CallpointCaptureTimeout,
                            channel,
                            () => ready.Value,
                            new Progress<CallpointCaptureStatus>(status =>
                                progress.Report(new RecoveryProgress(22, status.Message, status.Detail))),
                            token);
                        if (!string.IsNullOrWhiteSpace(captureResult?.Error))
                            lastCaptureFailure = captureResult.Error;
                        captureResult?.Dispose();
                    }
                    finally
                    {
                        if (backend is IDisposable disposable) disposable.Dispose();
                    }
                }
                finally
                {
                    channel.Complete();
                    refreshCancellation.Cancel();
                    await consumer;
                    try { await periodic; } catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested) { }
                    gate.Dispose();
                }

                if (channel.Error is not null)
                    throw new InvalidOperationException(channel.Error.Message);
                if (ready.Value) break;
                if (process.ScanAll)
                {
                    var update = await WaitForRefreshedCaptureTargetsAsync(
                        targetQueue,
                        scheduledPids,
                        () => ResolveCaptureProcessSelections(process, selectedDatabase.Path).Targets,
                        static (pid, moduleName) => TargetModuleReader.FindModuleInProcess((uint)pid, moduleName) is not null,
                        TimeSpan.FromSeconds(5),
                        CaptureRetryDelay,
                        token);
                    if (update.AddedPids.Count == 0) break;
                }
            }
        }

        if (!ready.Value && collector.PendingMatches.Count == 0)
        {
            throw new InvalidOperationException(
                lastCaptureFailure ??
                "所有候选 PID 的调用点均未命中数据库 key。请确认先点捕获，再启动并登录目标程序。");
        }

        var outputPaths = new List<string>();
        var failedDatabasePaths = new List<string>();
        foreach (var match in collector.PendingMatches.ToArray())
        {
            token.ThrowIfCancellationRequested();
            var descriptor = catalog.Descriptors.SingleOrDefault(item =>
                string.Equals(item.Path, match.DatabaseId, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null)
            {
                failedDatabasePaths.Add(match.DatabaseId);
                continue;
            }

            var exportKey = collector.CopyPendingKey(match);
            var persistentKey = exportKey.ToArray();
            try
            {
                var export = await _exporter.ExportAsync(
                    new DatabaseExportRequest(
                        descriptor.Path,
                        descriptor.Generation,
                        exportKey,
                        match.ProfileMatch.Profile,
                        outputDirectory),
                    progress,
                    token);
                token.ThrowIfCancellationRequested();
                if (export.Status == DatabaseExportStatus.Completed && export.OutputPath is not null)
                {
                    _validatedKeySink?.Store(
                        new ValidatedDatabaseKeyMetadata(
                            descriptor.Path,
                            descriptor.Generation,
                            match.ProfileMatch.Profile.Name,
                            match.CallpointName),
                        persistentKey);
                    collector.ConfirmExport(match);
                    catalog.MarkConfirmed(match.DatabaseId);
                    DeleteVaultRecords(_pendingCaptureVault, match.DatabaseId, vaultRecords);
                    outputPaths.Add(export.OutputPath);
                    progress.Report(new RecoveryProgress(
                        92,
                        $"[7/7] 已生成 {Path.GetFileName(export.OutputPath)}",
                        $"逐页认证和 integrity_check 完成：{match.DatabaseId}"));
                }
                else
                {
                    failedDatabasePaths.Add(match.DatabaseId);
                    progress.Report(new RecoveryProgress(
                        88,
                        $"[7/7] {Path.GetFileName(match.DatabaseId)} 导出失败，继续处理其它数据库…",
                        $"数据库={match.DatabaseId}; 状态={export.Status}; {export.Error}"));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(persistentKey);
            }
        }

        var unmatched = collector.UnmatchedIds
            .Concat(catalog.SkippedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (outputPaths.Count == 0)
            throw new InvalidOperationException(
                collector.PendingMatches.Count > 0
                    ? "已发现候选 key，但没有数据库通过逐页认证；pending 票据和当前会话 key 已保留。"
                    : "没有数据库完成导出，请确认目标程序已登录并重新捕获。");

        progress.Report(new RecoveryProgress(
            100,
            $"[7/7] 完成：生成 {outputPaths.Count} 个副本",
            $"输出 {outputPaths.Count}，未命中/未读取 {unmatched.Length}，输出失败 {failedDatabasePaths.Count}"));
        return new CaptureRecoveryResult(
            Array.AsReadOnly(outputPaths.ToArray()),
            Array.AsReadOnly(collector.Matches.ToArray()),
            Array.AsReadOnly(unmatched),
            Array.AsReadOnly(failedDatabasePaths.ToArray()),
            Array.AsReadOnly(loadedPendingCaptureTicketIds.OrderBy(id => id, StringComparer.Ordinal).ToArray()));
    }

    private static async Task ConsumeCandidatesAsync(
        CapturedCandidateChannel channel,
        DatabaseProbeCatalog catalog,
        MultiDatabaseCaptureCollector collector,
        CipherProfileProbeCounters counters,
        PendingCaptureVault vault,
        Dictionary<string, HashSet<PendingVaultReference>> vaultRecords,
        IProgress<RecoveryProgress> progress,
        CaptureReadySignal ready,
        SemaphoreSlim gate,
        CancellationToken token)
    {
        await foreach (var candidate in channel.ReadAllAsync(token))
        {
            using (candidate)
            {
                await gate.WaitAsync(token);
                try
                {
                    await catalog.RefreshAsync(token);
                    collector.Synchronize(catalog.Descriptors, counters, token);
                    var update = collector.TryCollect(candidate, counters, token);
                    SavePendingCaptureToVault(
                        candidate,
                        update.NewMatches,
                        catalog,
                        vault,
                        vaultRecords,
                        progress);
                    ready.Value = collector.IsReadyForValidation;
                    progress.Report(new RecoveryProgress(
                        update.NewMatches.Count > 0 ? 48 : 34,
                        update.NewMatches.Count > 0
                            ? $"已暂存 {update.NewMatches.Count} 个数据库候选 key。"
                            : "候选未命中当前抽样页，继续后台监听…",
                        FormatCandidateDatabaseProgress(
                            candidate.CallpointName,
                            collector.Matches.Count + collector.PendingDatabaseIds.Count + collector.UnmatchedIds.Count,
                            collector.PendingDatabaseIds.Count,
                            collector.UnmatchedIds.Count)));
                }
                finally
                {
                    gate.Release();
                }
            }
        }
    }

    internal static string FormatCandidateDatabaseProgress(
        string callpointName,
        int total,
        int pending,
        int unmatched) =>
        $"调用点={callpointName}; 数据库总数={total}; 已暂存={pending}; 尚未命中={unmatched}";

    private static async Task RefreshCatalogPeriodicallyAsync(
        DatabaseProbeCatalog catalog,
        MultiDatabaseCaptureCollector collector,
        CipherProfileProbeCounters counters,
        IProgress<RecoveryProgress> progress,
        CaptureReadySignal ready,
        SemaphoreSlim gate,
        CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(DatabaseRefreshInterval, token);
            await gate.WaitAsync(token);
            try
            {
                var update = await catalog.RefreshAsync(token);
                collector.Synchronize(catalog.Descriptors, counters, token);
                ready.Value = collector.IsReadyForValidation;
                if (update.AddedPaths.Count > 0 || update.ReplacedPaths.Count > 0)
                {
                    progress.Report(new RecoveryProgress(
                        30,
                        "监听期数据库目录已刷新。",
                        $"新增 {update.AddedPaths.Count}，换代 {update.ReplacedPaths.Count}，删除/暂不可用 {update.UnavailablePaths.Count}"));
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static IReadOnlyList<TargetProcessSource> ResolveTargets(RecoveryProcessSelection process)
    {
        if (process.ScanAll)
        {
            var processName = Path.GetFileNameWithoutExtension(process.Name);
            if (string.IsNullOrWhiteSpace(processName) ||
                string.Equals(processName, "Automatic", StringComparison.OrdinalIgnoreCase))
            {
                processName = "Weixin";
            }
            return TargetProcessDiscovery.Discover(
                processName,
                process.SessionId,
                process.ExecutablePath);
        }
        if (process.Pid is not int pid) return Array.Empty<TargetProcessSource>();
        if (!TargetProcessDiscovery.Matches(
                pid,
                process.SessionId,
                process.ExecutablePath))
        {
            return Array.Empty<TargetProcessSource>();
        }
        return [new TargetProcessSource(pid, process.Name)];
    }

    private static ModuleIdentityValidation? InspectLoadedModule(int pid, string moduleName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var path = TargetModuleReader.ResolveDllPath((uint)pid, moduleName);
        return path is null ? null : PeCallpointLocator.ValidateModuleIdentity(path);
    }

    private static CaptureProcessResolution ResolveCaptureProcessSelections(
        RecoveryProcessSelection process,
        string databasePath)
    {
        if (!process.ScanAll)
        {
            var target = ResolveTargets(process);
            return new CaptureProcessResolution(
                target.Select(item => process with
                {
                    Pid = item.Pid,
                    Name = item.Name,
                }).ToArray(),
                []);
        }
        var targets = ResolveTargets(process).Where(item => item.Pid > 0).OrderBy(item => item.Pid).ToArray();
        var ownerPids = ProcessFileHandleFinder.FindProcessIdsHoldingFile(
            databasePath,
            targets.Select(item => item.Pid).ToArray()).ToArray();
        if (ownerPids.Length == 0)
            return new CaptureProcessResolution(
                targets.Select(item => process with
                {
                    Pid = item.Pid,
                    Name = item.Name,
                    ScanAll = false,
                }).ToArray(), ownerPids);
        var byPid = targets.ToDictionary(item => item.Pid);
        var ordered = ownerPids.Where(byPid.ContainsKey).Select(pid => byPid[pid]).ToList();
        ordered.AddRange(targets.Where(item => !ownerPids.Contains(item.Pid)));
        return new CaptureProcessResolution(
            ordered.Select(item => process with
            {
                Pid = item.Pid,
                Name = item.Name,
                ScanAll = false,
            }).ToArray(), ownerPids);
    }

    private static async Task<CaptureProcessResolution> WaitForCaptureProcessSelectionsAsync(
        RecoveryProcessSelection process,
        string databasePath,
        IProgress<RecoveryProgress> progress,
        CancellationToken token)
    {
        var resolution = ResolveCaptureProcessSelections(process, databasePath);
        if (!process.ScanAll || resolution.Targets.Count > 0) return resolution;
        progress.Report(new RecoveryProgress(4, "早鸟等待 Weixin.exe 启动…", "请现在启动目标程序，等待自动附加。"));
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < CaptureProcessWaitTimeout)
        {
            await Task.Delay(250, token);
            resolution = ResolveCaptureProcessSelections(process, databasePath);
            if (resolution.Targets.Count > 0) return resolution;
        }
        return resolution;
    }

    internal static IReadOnlyList<int> EnqueueNewCaptureTargets(
        Queue<RecoveryProcessSelection> targetQueue,
        HashSet<int> scheduledPids,
        IEnumerable<RecoveryProcessSelection> targets)
    {
        var added = new List<int>();
        foreach (var target in targets)
        {
            if (target.Pid is not int pid || !scheduledPids.Add(pid)) continue;
            targetQueue.Enqueue(target);
            added.Add(pid);
        }
        return added;
    }

    internal static CaptureTargetRefreshUpdate EnqueueRefreshedCaptureTargets(
        Queue<RecoveryProcessSelection> targetQueue,
        HashSet<int> scheduledPids,
        IEnumerable<RecoveryProcessSelection> targets,
        Func<int, string, bool> hasLoadedModule)
    {
        var added = new List<int>();
        var skipped = new List<int>();
        foreach (var target in targets)
        {
            if (target.Pid is not int pid || scheduledPids.Contains(pid)) continue;
            if (!hasLoadedModule(pid, "Weixin.dll")) { skipped.Add(pid); continue; }
            scheduledPids.Add(pid);
            targetQueue.Enqueue(target);
            added.Add(pid);
        }
        return new CaptureTargetRefreshUpdate(added, skipped);
    }

    internal static async Task<CaptureTargetRefreshUpdate> WaitForRefreshedCaptureTargetsAsync(
        Queue<RecoveryProcessSelection> targetQueue,
        HashSet<int> scheduledPids,
        Func<IReadOnlyList<RecoveryProcessSelection>> discoverTargets,
        Func<int, string, bool> hasLoadedModule,
        TimeSpan waitTimeout,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var update = EnqueueRefreshedCaptureTargets(
                targetQueue, scheduledPids, discoverTargets(), hasLoadedModule);
            if (update.AddedPids.Count > 0 || update.SkippedPids.Count == 0 || clock.Elapsed >= waitTimeout)
                return update;
            await Task.Delay(retryDelay, cancellationToken);
        }
    }

    private static void LoadPendingCapturesFromVault(
        PendingCaptureVault vault,
        DatabaseProbeCatalog catalog,
        MultiDatabaseCaptureCollector collector,
        CipherProfileProbeCounters counters,
        Dictionary<string, HashSet<PendingVaultReference>> vaultRecords,
        HashSet<string> loadedTicketIds,
        IProgress<RecoveryProgress> progress,
        CancellationToken token)
    {
        foreach (var descriptor in catalog.Descriptors)
        {
            token.ThrowIfCancellationRequested();
            var fingerprint = ComputeSaltFingerprint(descriptor.Salt);
            foreach (var moduleSha in CallpointProfiles.Supported.Select(profile => profile.ModuleSha256).Distinct())
            {
                IReadOnlyList<PendingCaptureRecord> records;
                try { records = vault.LoadMatching(fingerprint, moduleSha); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or Win32Exception or ArgumentException)
                {
                    progress.Report(new RecoveryProgress(5, "DPAPI pending 票据读取失败，继续实时捕获…", FormatException(ex)));
                    continue;
                }
                foreach (var record in records)
                {
                    using (record)
                    {
                        loadedTicketIds.Add(record.RecordId);
                        TrackVaultRecord(descriptor.Path, new PendingVaultReference(fingerprint, record.RecordId), vaultRecords);
                        using var candidate = new CapturedKeyMaterial(
                            record.CallpointName, 0, "DPAPI pending ticket", 0, record.CapturedAtUtc)
                        { KeyData = record.CapturedPayload.ToArray(), KeyLength = record.CapturedPayload.Length };
                        collector.TryCollect(candidate, counters, token);
                    }
                }
            }
        }
    }

    private static void SavePendingCaptureToVault(
        CapturedKeyMaterial candidate,
        IReadOnlyList<DatabaseCaptureMatch> matches,
        DatabaseProbeCatalog catalog,
        PendingCaptureVault vault,
        Dictionary<string, HashSet<PendingVaultReference>> vaultRecords,
        IProgress<RecoveryProgress> progress)
    {
        if (candidate.KeyData is not { Length: > 0 } payload) return;
        foreach (var match in matches)
        {
            var descriptor = catalog.Descriptors.SingleOrDefault(item => item.Path.Equals(match.DatabaseId, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null) continue;
            var fingerprint = ComputeSaltFingerprint(descriptor.Salt);
            try
            {
                var id = vault.Save(
                    fingerprint,
                    ResolveModuleSha256(candidate),
                    candidate.CallpointName,
                    payload);
                TrackVaultRecord(match.DatabaseId, new PendingVaultReference(fingerprint, id), vaultRecords);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or Win32Exception or ArgumentException)
            {
                progress.Report(new RecoveryProgress(48, "pending 票据保存失败。", FormatException(ex)));
            }
        }
    }

    private static string ResolveModuleSha256(CapturedKeyMaterial candidate) =>
        CallpointProfiles.Supported.FirstOrDefault(profile => profile.Callpoints.Any(callpoint =>
            callpoint.Name == candidate.CallpointName && callpoint.BreakpointRva == candidate.HitRva))?.ModuleSha256
        ?? CallpointProfiles.Preferred.ModuleSha256;

    private static string ComputeSaltFingerprint(ReadOnlySpan<byte> salt) =>
        Convert.ToHexString(SHA256.HashData(salt)).ToLowerInvariant();

    private static void DeleteVaultRecords(PendingCaptureVault vault, string databaseId, Dictionary<string, HashSet<PendingVaultReference>> records)
    {
        if (!records.Remove(databaseId, out var items)) return;
        foreach (var item in items) vault.Delete(item.DatabaseSaltFingerprint, item.RecordId);
    }

    private static void TrackVaultRecord(string databaseId, PendingVaultReference reference, Dictionary<string, HashSet<PendingVaultReference>> records)
    {
        if (!records.TryGetValue(databaseId, out var items)) records.Add(databaseId, items = []);
        items.Add(reference);
    }

    private static string FormatException(Exception ex)
    {
        if (ex is AggregateException aggregate) ex = aggregate.GetBaseException();
        if (ex is Win32Exception win32) return $"{win32.Message} (错误码 {win32.NativeErrorCode})";
        return ex.Message;
    }

    internal sealed record CaptureTargetRefreshUpdate(IReadOnlyList<int> AddedPids, IReadOnlyList<int> SkippedPids);
    private sealed record CaptureProcessResolution(IReadOnlyList<RecoveryProcessSelection> Targets, IReadOnlyList<int> DatabaseOwnerPids);
    private sealed record PendingVaultReference(string DatabaseSaltFingerprint, string RecordId);

    private sealed class CaptureReadySignal(bool initial)
    {
        private int _value = initial ? 1 : 0;
        internal bool Value { get => Volatile.Read(ref _value) == 1; set => Volatile.Write(ref _value, value ? 1 : 0); }
    }
}
