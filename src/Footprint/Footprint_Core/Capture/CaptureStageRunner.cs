using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Footprint.Core.Contracts;
using Footprint.Core.State;

namespace Footprint.Core.Capture;

public enum CaptureStageDirective
{
    Continue,
    CachedKeyAccepted,
    PassiveKeyAccepted,
    RestartRequired
}

public enum CaptureRunTerminalStatus
{
    WaitingForPhase03,
    WaitingForRemoteRestart,
    WaitingForRestartEnablement,
    UnsupportedProfile,
    DecompressionFailed,
    Failed,
    Cancelled
}

public sealed record CaptureStageArtifact(
    string TemporaryRelativePath,
    string PublishedRelativePath,
    long Length,
    string Sha256,
    CaptureSourceCategory SourceCategory,
    string SourceIdentityHash,
    int StabilityAttempts,
    IReadOnlyDictionary<string, string> AssociationEvidence);

public sealed record CaptureStageExecutionResult(
    CaptureStageStatus Status,
    string Code,
    string MessageZh,
    CaptureStageDirective Directive,
    IReadOnlyList<CaptureStageArtifact> Artifacts)
{
    public static CaptureStageExecutionResult Success(string code, string messageZh,
        CaptureStageDirective directive = CaptureStageDirective.Continue,
        IReadOnlyList<CaptureStageArtifact>? artifacts = null) =>
        new(CaptureStageStatus.Succeeded, code, messageZh, directive, artifacts ?? []);

    public static CaptureStageExecutionResult Failure(string code, string messageZh) =>
        new(CaptureStageStatus.Failed, code, messageZh, CaptureStageDirective.Continue, []);

    public static CaptureStageExecutionResult Skipped(string code, string messageZh) =>
        new(CaptureStageStatus.Skipped, code, messageZh, CaptureStageDirective.Continue, []);

    public static CaptureStageExecutionResult Waiting(string code, string messageZh) =>
        new(CaptureStageStatus.Waiting, code, messageZh, CaptureStageDirective.RestartRequired, []);
}

public sealed record WcdbDecompressionResult(
    bool IsSuccessful,
    string Code,
    string MessageZh,
    IReadOnlyList<CaptureStageArtifact> Artifacts,
    IReadOnlyDictionary<string, long> Metrics)
{
    public static WcdbDecompressionResult Success(string code, string messageZh,
        IReadOnlyList<CaptureStageArtifact> artifacts, IReadOnlyDictionary<string, long> metrics) =>
        new(true, code, messageZh, artifacts, metrics);

    public static WcdbDecompressionResult Failure(string code, string messageZh) =>
        new(false, code, messageZh, [], new Dictionary<string, long>());
}

public sealed record CaptureRunRequest(
    string RunId,
    CaptureGenerationId Generation,
    CaptureWorkspace Workspace,
    string StateRoot);

public sealed record CaptureStageContext(
    string RunId,
    CaptureGenerationId Generation,
    CaptureWorkspace Workspace,
    string StateRoot,
    FootprintStage Stage,
    long CheckpointVersion,
    RestartPolicy RestartPolicy,
    CaptureStageDirective Directive);

public sealed record CaptureStageRecord(
    FootprintStage Stage,
    CaptureStageStatus Status,
    string Code,
    string MessageZh);

public sealed record CaptureRunResult(
    CaptureRunTerminalStatus Status,
    string Code,
    string MessageZh,
    IReadOnlyList<CaptureStageRecord> Stages);

public interface ICaptureStageExecutor
{
    Task<CaptureStageExecutionResult> ExecuteAsync(CaptureStageContext context,
        string temporaryDirectory, CancellationToken cancellationToken);
}

public interface IWcdbDecompressionPort
{
    Task<WcdbDecompressionResult> DecompressAsync(CaptureStageContext context,
        string temporaryDirectory, CancellationToken cancellationToken);
}

public sealed class CaptureStageRunnerHooks
{
    private readonly Func<FootprintStage, CancellationToken, Task> _beforeExecute;
    private readonly Func<FootprintStage, CaptureStageExecutionResult, CancellationToken, Task> _afterExecute;
    private readonly Func<FootprintStage, CancellationToken, Task> _executeFailed;
    private readonly Func<FootprintStage, CancellationToken, Task> _beforePublish;
    private readonly Func<FootprintStage, CancellationToken, Task> _afterPublish;
    private readonly Func<FootprintStage, CancellationToken, Task> _afterDirectoryPromotion;

    public CaptureStageRunnerHooks(
        Func<FootprintStage, CancellationToken, Task>? beforePublish = null,
        Func<FootprintStage, CancellationToken, Task>? afterPublish = null,
        Func<FootprintStage, CancellationToken, Task>? afterDirectoryPromotion = null,
        Func<FootprintStage, CancellationToken, Task>? beforeExecute = null,
        Func<FootprintStage, CaptureStageExecutionResult, CancellationToken, Task>? afterExecute = null,
        Func<FootprintStage, CancellationToken, Task>? executeFailed = null)
    {
        _beforeExecute = beforeExecute ?? ((_, _) => Task.CompletedTask);
        _afterExecute = afterExecute ?? ((_, _, _) => Task.CompletedTask);
        _executeFailed = executeFailed ?? ((_, _) => Task.CompletedTask);
        _beforePublish = beforePublish ?? ((_, _) => Task.CompletedTask);
        _afterPublish = afterPublish ?? ((_, _) => Task.CompletedTask);
        _afterDirectoryPromotion = afterDirectoryPromotion ?? ((_, _) => Task.CompletedTask);
    }

    public static CaptureStageRunnerHooks None { get; } = new();

    internal Task BeforeExecuteAsync(FootprintStage stage, CancellationToken cancellationToken) =>
        _beforeExecute(stage, cancellationToken);

    internal Task AfterExecuteAsync(FootprintStage stage, CaptureStageExecutionResult result,
        CancellationToken cancellationToken) => _afterExecute(stage, result, cancellationToken);

    internal Task ExecuteFailedAsync(FootprintStage stage) => _executeFailed(stage, CancellationToken.None);

    internal Task BeforePublishAsync(FootprintStage stage, CancellationToken cancellationToken) =>
        _beforePublish(stage, cancellationToken);

    internal Task AfterPublishAsync(FootprintStage stage, CancellationToken cancellationToken) =>
        _afterPublish(stage, cancellationToken);

    internal Task AfterDirectoryPromotionAsync(FootprintStage stage, CancellationToken cancellationToken) =>
        _afterDirectoryPromotion(stage, cancellationToken);
}

public sealed class CaptureStageRunner
{
    public const string Phase03HandoffStep = "等待第三阶段交接";
    private const string PendingStep = "待执行";

    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IFootprintStateStore _stateStore;
    private readonly ICaptureStageExecutor _executor;
    private readonly IWcdbDecompressionPort _decompressor;
    private readonly CaptureStageRunnerHooks _hooks;
    private readonly Func<DateTimeOffset> _utcNow;

    public CaptureStageRunner(IFootprintStateStore stateStore, ICaptureStageExecutor executor,
        IWcdbDecompressionPort decompressor, CaptureStageRunnerHooks? hooks = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _decompressor = decompressor ?? throw new ArgumentNullException(nameof(decompressor));
        _hooks = hooks ?? CaptureStageRunnerHooks.None;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public static IReadOnlyList<FootprintStage> StageOrder { get; } =
    [
        FootprintStage.Footprint_Runtime,
        FootprintStage.Footprint_WeixinDetection,
        FootprintStage.Footprint_VersionVerification,
        FootprintStage.Footprint_KeyValidation,
        FootprintStage.Footprint_KeyCapture,
        FootprintStage.Footprint_WeixinRestart,
        FootprintStage.Footprint_ConnectionBinding,
        FootprintStage.Footprint_DatabaseSnapshot,
        FootprintStage.Footprint_ImageSnapshot,
        FootprintStage.Footprint_Decompression,
        FootprintStage.Footprint_VoiceSnapshot,
        FootprintStage.Footprint_FavoriteSnapshot
    ];

    public async Task<CaptureRunResult> RunAsync(CaptureRunRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await _stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = await _stateStore.LoadCheckpointAsync(request.RunId, cancellationToken)
            .ConfigureAwait(false);
        var version = checkpoint?.Version ?? 0;
        var restartPolicy = await _stateStore.GetRestartPolicyAsync(request.Generation.DeviceId,
            cancellationToken).ConfigureAwait(false);
        if (!Enum.IsDefined(restartPolicy))
            return Result(CaptureRunTerminalStatus.Failed, "restart_policy_invalid", "重启策略无效。", []);

        var records = new List<CaptureStageRecord>(StageOrder.Count);
        var manifestEntries = new List<CaptureManifestEntry>();
        var directive = CaptureStageDirective.Continue;
        var journalRoot = JournalRoot(request);
        Directory.CreateDirectory(journalRoot);

        foreach (var stage in StageOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var journalPath = JournalPath(journalRoot, stage);
            await RecoverPromotionAsync(request.Workspace, journalRoot, journalPath, stage,
                cancellationToken).ConfigureAwait(false);
            PublishedStageJournal journal;
            var alreadyPublished = File.Exists(journalPath);
            if (alreadyPublished)
            {
                journal = await LoadJournalAsync(journalPath, stage, cancellationToken).ConfigureAwait(false);
                await VerifyPublishedArtifactsAsync(request.Workspace, journal.Result.Artifacts,
                    cancellationToken).ConfigureAwait(false);
                if (ShouldRefreshPublishedStage(stage, journal.Result) &&
                    !IsConnectionBindingCommitted(checkpoint))
                {
                    try
                    {
                        journal = await RefreshPublishedStageAsync(journal, directive, restartPolicy, request,
                            version, journalRoot, journalPath, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return Result(CaptureRunTerminalStatus.Cancelled, "capture_cancelled", "采集已取消。", records);
                    }
                }
            }
            else
            {
                var temporaryDirectory = TemporaryDirectory(journalRoot, stage);
                ResetTemporaryDirectory(temporaryDirectory);
                CaptureStageExecutionResult execution;
                await _hooks.BeforeExecuteAsync(stage, cancellationToken).ConfigureAwait(false);
                try
                {
                    execution = await ExecuteStageAsync(stage, directive, restartPolicy, request,
                        version, temporaryDirectory, cancellationToken).ConfigureAwait(false);
                    ValidateExecution(execution, temporaryDirectory);
                    await _hooks.AfterExecuteAsync(stage, execution, cancellationToken).ConfigureAwait(false);
                    await _hooks.BeforePublishAsync(stage, cancellationToken).ConfigureAwait(false);
                    journal = new PublishedStageJournal(stage, execution, _utcNow().ToUniversalTime());
                    await PublishStageAsync(request.Workspace, temporaryDirectory, journalRoot,
                        journalPath, journal, cancellationToken).ConfigureAwait(false);
                    DeleteDirectory(temporaryDirectory);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await _hooks.ExecuteFailedAsync(stage).ConfigureAwait(false);
                    return Result(CaptureRunTerminalStatus.Cancelled, "capture_cancelled", "采集已取消。", records);
                }
                catch
                {
                    await _hooks.ExecuteFailedAsync(stage).ConfigureAwait(false);
                    throw;
                }
            }

            directive = ApplyDirective(directive, journal.Result.Directive);
            AddManifestEntries(manifestEntries, journal);
            records.Add(new CaptureStageRecord(stage, journal.Result.Status,
                journal.Result.Code, journal.Result.MessageZh));

            var terminal = TerminalFor(stage, journal.Result, restartPolicy);
            if (terminal is not null)
            {
                if (!IsCommitted(checkpoint, stage, journal.Result.Status))
                {
                    version++;
                    checkpoint = await SaveStateAsync(request, stage, journal.Result.MessageZh,
                        journal.Result.Status, version, cancellationToken).ConfigureAwait(false);
                }
                return terminal with { Stages = records.ToArray() };
            }

            if (!IsCommitted(checkpoint, stage, journal.Result.Status))
            {
                if (!alreadyPublished)
                    await _hooks.AfterPublishAsync(stage, cancellationToken).ConfigureAwait(false);
                version++;
                checkpoint = await AdvanceCheckpointAsync(request, stage, version, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await PublishManifestAsync(request, manifestEntries, _utcNow().ToUniversalTime(),
            cancellationToken).ConfigureAwait(false);
        return Result(CaptureRunTerminalStatus.WaitingForPhase03, "capture_wait_phase03",
            "采集已完成，正在等待第三阶段交接。", records);
    }

    private async Task<CaptureStageExecutionResult> ExecuteStageAsync(FootprintStage stage,
        CaptureStageDirective directive, RestartPolicy restartPolicy, CaptureRunRequest request,
        long version, string temporaryDirectory, CancellationToken cancellationToken)
    {
        if (stage == FootprintStage.Footprint_KeyCapture &&
            directive == CaptureStageDirective.CachedKeyAccepted)
            return CaptureStageExecutionResult.Skipped("key_capture_skipped", "缓存数据库密钥有效，已跳过被动捕获。");

        if (stage == FootprintStage.Footprint_WeixinRestart)
        {
            if (directive is CaptureStageDirective.CachedKeyAccepted or CaptureStageDirective.PassiveKeyAccepted or
                CaptureStageDirective.Continue)
                return CaptureStageExecutionResult.Skipped("restart_skipped", "数据库密钥已就绪，已跳过微信重启。");
            if (restartPolicy == RestartPolicy.RemoteOnly)
                return CaptureStageExecutionResult.Waiting("restart_wait_remote_command", "仅允许经验证的远程重启命令。");
            if (restartPolicy == RestartPolicy.Disabled)
                return CaptureStageExecutionResult.Waiting("restart_wait_disabled", "当前设备已禁用微信重启。");
        }

        var context = new CaptureStageContext(request.RunId, request.Generation, request.Workspace,
            request.StateRoot, stage, version, restartPolicy, directive);
        if (stage != FootprintStage.Footprint_Decompression)
            return await _executor.ExecuteAsync(context, temporaryDirectory, cancellationToken).ConfigureAwait(false);

        var decompression = await _decompressor.DecompressAsync(context, temporaryDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!decompression.IsSuccessful)
            return CaptureStageExecutionResult.Failure(decompression.Code, decompression.MessageZh);

        var metrics = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var metric in decompression.Metrics.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(metric.Key) || metric.Value < 0 || IsSecretName(metric.Key))
                throw new InvalidDataException("WCDB 解压指标无效。");
            metrics[metric.Key] = metric.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var artifacts = decompression.Artifacts.Select(artifact => artifact with
        {
            AssociationEvidence = DecompressionEvidence(artifact, metrics)
        }).ToArray();
        return CaptureStageExecutionResult.Success(decompression.Code, decompression.MessageZh,
            artifacts: artifacts);
    }

    private static CaptureRunResult? TerminalFor(FootprintStage stage,
        CaptureStageExecutionResult result, RestartPolicy restartPolicy)
    {
        if (result.Status == CaptureStageStatus.Waiting && stage == FootprintStage.Footprint_WeixinRestart)
        {
            return restartPolicy == RestartPolicy.RemoteOnly
                ? Result(CaptureRunTerminalStatus.WaitingForRemoteRestart, result.Code, result.MessageZh, [])
                : Result(CaptureRunTerminalStatus.WaitingForRestartEnablement, result.Code, result.MessageZh, []);
        }
        if (result.Status != CaptureStageStatus.Failed) return null;
        if (stage == FootprintStage.Footprint_VersionVerification &&
            result.Code.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
            return Result(CaptureRunTerminalStatus.UnsupportedProfile, result.Code, result.MessageZh, []);
        if (stage == FootprintStage.Footprint_Decompression)
            return Result(CaptureRunTerminalStatus.DecompressionFailed, result.Code, result.MessageZh, []);
        return Result(CaptureRunTerminalStatus.Failed, result.Code, result.MessageZh, []);
    }

    private async Task PublishManifestAsync(CaptureRunRequest request,
        IReadOnlyList<CaptureManifestEntry> entries, DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        if (File.Exists(request.Workspace.ManifestPath)) return;
        var manifest = new CaptureManifest
        {
            RunId = request.RunId,
            DeviceId = request.Generation.DeviceId,
            CaptureGeneration = request.Generation.CaptureGeneration,
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
            Entries = entries
        };
        await new CaptureManifestPublisher(request.Workspace).PublishAsync(manifest, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<FootprintCheckpoint> AdvanceCheckpointAsync(CaptureRunRequest request,
        FootprintStage completedStage, long version, CancellationToken cancellationToken)
    {
        var index = StageOrder.IndexOf(completedStage);
        if (index < StageOrder.Count - 1)
            return await SaveStateAsync(request, StageOrder[index + 1], PendingStep,
                CaptureStageStatus.Pending, version, cancellationToken).ConfigureAwait(false);
        return await SaveStateAsync(request, FootprintStage.Footprint_Decompression, Phase03HandoffStep,
            CaptureStageStatus.Waiting, version, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FootprintCheckpoint> SaveStateAsync(CaptureRunRequest request,
        FootprintStage stage, string step, CaptureStageStatus status, long version,
        CancellationToken cancellationToken)
    {
        var now = _utcNow().ToUniversalTime();
        var checkpoint = new FootprintCheckpoint(request.RunId, stage, step, version, now);
        await _stateStore.SaveCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        await _stateStore.SaveCaptureRunAsync(new CaptureRunState(request.RunId, request.Generation,
            stage, step, status, version, now), cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private static bool IsCommitted(FootprintCheckpoint? checkpoint, FootprintStage stage,
        CaptureStageStatus resultStatus)
    {
        if (checkpoint is null) return false;
        var checkpointIndex = StageOrder.IndexOf(checkpoint.Stage);
        var stageIndex = StageOrder.IndexOf(stage);
        if (checkpointIndex > stageIndex) return true;
        if (stage != checkpoint.Stage) return false;
        if (stage == FootprintStage.Footprint_Decompression &&
            string.Equals(checkpoint.Step, Phase03HandoffStep, StringComparison.Ordinal)) return true;
        return resultStatus is CaptureStageStatus.Failed or CaptureStageStatus.Waiting &&
               !string.Equals(checkpoint.Step, PendingStep, StringComparison.Ordinal);
    }

    private static bool IsConnectionBindingCommitted(FootprintCheckpoint? checkpoint)
    {
        if (checkpoint is null) return false;
        var checkpointIndex = StageOrder.IndexOf(checkpoint.Stage);
        var connectionBindingIndex = StageOrder.IndexOf(FootprintStage.Footprint_ConnectionBinding);
        if (checkpointIndex > connectionBindingIndex) return true;
        return checkpoint.Stage == FootprintStage.Footprint_ConnectionBinding &&
               !string.Equals(checkpoint.Step, PendingStep, StringComparison.Ordinal);
    }

    private static void AddManifestEntries(ICollection<CaptureManifestEntry> destination,
        PublishedStageJournal journal)
    {
        foreach (var artifact in journal.Result.Artifacts)
        {
            destination.Add(new CaptureManifestEntry(artifact.PublishedRelativePath, artifact.Length,
                artifact.Sha256, artifact.SourceCategory, artifact.SourceIdentityHash,
                journal.PublishedAtUtc, artifact.StabilityAttempts, artifact.AssociationEvidence));
        }
    }

    private async Task PublishStageAsync(CaptureWorkspace workspace, string temporaryDirectory,
        string journalRoot, string journalPath, PublishedStageJournal journal,
        CancellationToken cancellationToken)
    {
        if (journal.Result.Artifacts.Count == 0)
        {
            await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var artifact in journal.Result.Artifacts)
            await VerifyArtifactAsync(ResolveTemporary(temporaryDirectory, artifact.TemporaryRelativePath),
                artifact, cancellationToken).ConfigureAwait(false);

        var commonRelativeDirectory = CommonPublishedDirectory(journal.Result.Artifacts);
        var transaction = new StagePromotionJournal(journal.Stage, commonRelativeDirectory,
            journal.Result, journal.PublishedAtUtc);
        var paths = PromotionPaths(workspace, journal.Stage, commonRelativeDirectory);
        DeleteDirectory(paths.StagingDirectory);
        DeleteDirectory(paths.BackupDirectory);
        Directory.CreateDirectory(paths.StagingDirectory);
        if (Directory.Exists(paths.FinalDirectory))
            CopyDirectory(paths.FinalDirectory, paths.StagingDirectory);

        foreach (var artifact in journal.Result.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ResolveTemporary(temporaryDirectory, artifact.TemporaryRelativePath);
            var relativeUnderPublication = Path.GetRelativePath(paths.FinalDirectory,
                workspace.ResolveRelativePath(artifact.PublishedRelativePath));
            var staged = ResolveUnderDirectory(paths.StagingDirectory, relativeUnderPublication);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            if (File.Exists(staged)) File.Delete(staged);
            File.Move(source, staged);
            await VerifyArtifactAsync(staged, artifact, cancellationToken).ConfigureAwait(false);
        }

        await WritePromotionJournalAsync(PromotionJournalPath(journalRoot, journal.Stage), transaction,
            cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(paths.FinalDirectory))
            Directory.Move(paths.FinalDirectory, paths.BackupDirectory);
        Directory.Move(paths.StagingDirectory, paths.FinalDirectory);
        await _hooks.AfterDirectoryPromotionAsync(journal.Stage, cancellationToken).ConfigureAwait(false);
        await VerifyPublishedArtifactsAsync(workspace, journal.Result.Artifacts, cancellationToken)
            .ConfigureAwait(false);
        await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
        DeleteDirectory(paths.BackupDirectory);
        DeleteFile(PromotionJournalPath(journalRoot, journal.Stage));
    }

    private static async Task RecoverPromotionAsync(CaptureWorkspace workspace, string journalRoot,
        string journalPath, FootprintStage stage, CancellationToken cancellationToken)
    {
        var promotionPath = PromotionJournalPath(journalRoot, stage);
        if (!File.Exists(promotionPath)) return;
        var transaction = await LoadPromotionJournalAsync(promotionPath, stage, cancellationToken)
            .ConfigureAwait(false);
        var paths = PromotionPaths(workspace, stage, transaction.CommonRelativeDirectory);

        if (File.Exists(journalPath))
        {
            var published = await LoadJournalAsync(journalPath, stage, cancellationToken).ConfigureAwait(false);
            await VerifyPublishedArtifactsAsync(workspace, published.Result.Artifacts, cancellationToken)
                .ConfigureAwait(false);
            DeleteDirectory(paths.StagingDirectory);
            DeleteDirectory(paths.BackupDirectory);
            DeleteFile(promotionPath);
            return;
        }

        if (!Directory.Exists(paths.FinalDirectory) && Directory.Exists(paths.StagingDirectory))
            Directory.Move(paths.StagingDirectory, paths.FinalDirectory);
        else if (Directory.Exists(paths.FinalDirectory) && Directory.Exists(paths.StagingDirectory))
        {
            if (Directory.Exists(paths.BackupDirectory))
                throw new InvalidDataException("采集阶段目录发布恢复状态无效。");
            Directory.Move(paths.FinalDirectory, paths.BackupDirectory);
            Directory.Move(paths.StagingDirectory, paths.FinalDirectory);
        }
        else if (!Directory.Exists(paths.FinalDirectory) && Directory.Exists(paths.BackupDirectory))
        {
            Directory.Move(paths.BackupDirectory, paths.FinalDirectory);
            DeleteFile(promotionPath);
            return;
        }

        await VerifyPublishedArtifactsAsync(workspace, transaction.Result.Artifacts, cancellationToken)
            .ConfigureAwait(false);
        await WriteJournalAsync(journalPath, new PublishedStageJournal(stage, transaction.Result,
            transaction.PublishedAtUtc), cancellationToken).ConfigureAwait(false);
        DeleteDirectory(paths.BackupDirectory);
        DeleteFile(promotionPath);
    }

    private static void ValidateExecution(CaptureStageExecutionResult result, string temporaryDirectory)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(result.Code) || string.IsNullOrWhiteSpace(result.MessageZh) ||
            result.Artifacts is null)
            throw new InvalidDataException("采集阶段结果无效。");
        if (result.Status is not CaptureStageStatus.Succeeded and not CaptureStageStatus.Skipped and
            not CaptureStageStatus.Waiting and not CaptureStageStatus.Failed)
            throw new InvalidDataException("采集阶段状态无效。");
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in result.Artifacts)
        {
            if (artifact is null || artifact.Length < 0 || artifact.StabilityAttempts < 1 ||
                !IsSha256(artifact.Sha256) || !IsSha256(artifact.SourceIdentityHash) ||
                artifact.AssociationEvidence is null || !destinations.Add(artifact.PublishedRelativePath))
                throw new InvalidDataException("采集阶段工件无效。");
            _ = ResolveTemporary(temporaryDirectory, artifact.TemporaryRelativePath);
        }
    }

    private static async Task VerifyArtifactAsync(string path, CaptureStageArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || !await MatchesAsync(path, artifact, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("采集阶段工件校验失败。");
    }

    private static async Task VerifyPublishedArtifactsAsync(CaptureWorkspace workspace,
        IReadOnlyList<CaptureStageArtifact> artifacts, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = workspace.ResolveRelativePath(artifact.PublishedRelativePath);
                if (!File.Exists(path) || !await MatchesAsync(path, artifact, cancellationToken)
                        .ConfigureAwait(false))
                    throw new InvalidDataException("已发布的采集阶段工件缺失或校验失败。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException exception) when (string.Equals(exception.Message,
                   "已发布的采集阶段工件缺失或校验失败。", StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or CryptographicException)
        {
            throw new InvalidDataException("已发布的采集阶段工件缺失或校验失败。", exception);
        }
    }

    private static async Task<bool> MatchesAsync(string path, CaptureStageArtifact artifact,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != artifact.Length) return false;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        return string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTemporary(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("采集阶段临时路径无效。");
        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException("采集阶段临时路径无效。");
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(fullRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("采集阶段临时路径无效。");
        return path;
    }

    private static string ResolveUnderDirectory(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("采集阶段目录发布路径无效。");
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(fullRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("采集阶段目录发布路径无效。");
        return path;
    }

    private static string CommonPublishedDirectory(IReadOnlyList<CaptureStageArtifact> artifacts)
    {
        var parentSegments = artifacts.Select(artifact =>
        {
            var normalized = artifact.PublishedRelativePath.Replace('\\', '/');
            var separator = normalized.LastIndexOf('/');
            if (separator <= 0) throw new InvalidDataException("采集阶段发布目录无效。");
            return normalized[..separator].Split('/');
        }).ToArray();
        var commonLength = parentSegments.Min(segments => segments.Length);
        for (var index = 0; index < commonLength; index++)
        {
            var expected = parentSegments[0][index];
            if (parentSegments.Any(segments => !string.Equals(segments[index], expected,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                commonLength = index;
                break;
            }
        }
        if (commonLength == 0) throw new InvalidDataException("采集阶段工件必须位于同一发布目录树。");
        return string.Join('/', parentSegments[0].Take(commonLength));
    }

    private static PromotionDirectoryPaths PromotionPaths(CaptureWorkspace workspace,
        FootprintStage stage, string commonRelativeDirectory)
    {
        var finalDirectory = workspace.ResolveRelativePath(commonRelativeDirectory);
        if (IsSameOrUnder(finalDirectory, workspace.PackagePath))
            throw new InvalidDataException("采集阶段发布目录不得占用事务目录。");
        var transactionRoot = Path.Combine(workspace.PackagePath, ".stage-transactions");
        Directory.CreateDirectory(transactionRoot);
        return new PromotionDirectoryPaths(finalDirectory,
            Path.Combine(transactionRoot, $"{stage}.staging"),
            Path.Combine(transactionRoot, $"{stage}.backup"));
    }

    private static bool IsSameOrUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullPath, fullRoot, comparison) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static void CopyDirectory(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("采集阶段发布目录包含重解析点。");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("采集阶段发布目录包含重解析点。");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static async Task WriteJournalAsync(string path, PublishedStageJournal journal,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson);
        await AtomicFile.WriteAsync(path, (stream, token) => stream.WriteAsync(bytes, token).AsTask(),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WritePromotionJournalAsync(string path, StagePromotionJournal journal,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson);
        await AtomicFile.WriteAsync(path, (stream, token) => stream.WriteAsync(bytes, token).AsTask(),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StagePromotionJournal> LoadPromotionJournalAsync(string path,
        FootprintStage expectedStage, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var journal = JsonSerializer.Deserialize<StagePromotionJournal>(bytes, JournalJson);
            if (journal is null || journal.Stage != expectedStage ||
                journal.PublishedAtUtc.Offset != TimeSpan.Zero ||
                string.IsNullOrWhiteSpace(journal.CommonRelativeDirectory))
                throw new InvalidDataException();
            return journal;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            throw new InvalidDataException("采集阶段目录发布日志无效。", exception);
        }
    }

    private static async Task<PublishedStageJournal> LoadJournalAsync(string path, FootprintStage expectedStage,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var journal = JsonSerializer.Deserialize<PublishedStageJournal>(bytes, JournalJson);
            if (journal is null || journal.Stage != expectedStage || journal.PublishedAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException();
            ValidateExecution(journal.Result, TemporaryDirectory(Path.GetDirectoryName(path)!, expectedStage));
            return journal;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            throw new InvalidDataException("采集阶段发布日志无效。", exception);
        }
    }

    private static CaptureStageDirective ApplyDirective(CaptureStageDirective current,
        CaptureStageDirective next) => next == CaptureStageDirective.Continue ? current : next;

    private async Task<PublishedStageJournal> RefreshPublishedStageAsync(PublishedStageJournal published,
        CaptureStageDirective directive, RestartPolicy restartPolicy, CaptureRunRequest request,
        long version, string journalRoot, string journalPath, CancellationToken cancellationToken)
    {
        var temporaryDirectory = TemporaryDirectory(journalRoot, published.Stage);
        ResetTemporaryDirectory(temporaryDirectory);
        await _hooks.BeforeExecuteAsync(published.Stage, cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = await ExecuteStageAsync(published.Stage, directive, restartPolicy, request,
                version, temporaryDirectory, cancellationToken).ConfigureAwait(false);
            ValidateExecution(refreshed, temporaryDirectory);
            await _hooks.AfterExecuteAsync(published.Stage, refreshed, cancellationToken).ConfigureAwait(false);
            if (refreshed.Directive != CaptureStageDirective.CachedKeyAccepted) return published;

            var replacement = new PublishedStageJournal(published.Stage, refreshed, _utcNow().ToUniversalTime());
            await PublishStageAsync(request.Workspace, temporaryDirectory, journalRoot,
                journalPath, replacement, cancellationToken).ConfigureAwait(false);
            return replacement;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _hooks.ExecuteFailedAsync(published.Stage).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await _hooks.ExecuteFailedAsync(published.Stage).ConfigureAwait(false);
            throw;
        }
        finally
        {
            DeleteDirectory(temporaryDirectory);
        }
    }

    private static bool ShouldRefreshPublishedStage(FootprintStage stage,
        CaptureStageExecutionResult result) =>
        stage == FootprintStage.Footprint_KeyValidation &&
        result.Status == CaptureStageStatus.Succeeded &&
        result.Directive == CaptureStageDirective.Continue &&
        string.Equals(result.Code, "cached_key_unavailable", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, string> DecompressionEvidence(
        CaptureStageArtifact artifact, IReadOnlyDictionary<string, string> metrics)
    {
        if (artifact.SourceCategory != CaptureSourceCategory.Decompression ||
            !artifact.PublishedRelativePath.Replace('\\', '/').StartsWith(
                "Footprint_Decompression/", StringComparison.Ordinal) ||
            artifact.AssociationEvidence.Count != 0)
            throw new InvalidDataException("WCDB 解压工件无效。");
        var evidence = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var metric in metrics) evidence.Add(metric.Key, metric.Value);
        return evidence;
    }

    private static bool IsSecretName(string value) =>
        value.Contains("key", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void ValidateRequest(CaptureRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Generation);
        ArgumentNullException.ThrowIfNull(request.Workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StateRoot);
        if (!string.Equals(request.RunId, request.Workspace.RunId, StringComparison.Ordinal))
            throw new ArgumentException("运行标识与采集工作区不一致。", nameof(request));
    }

    private static string JournalRoot(CaptureRunRequest request) => Path.Combine(
        Path.GetFullPath(request.StateRoot), "Footprint_StageRuns", request.RunId);
    private static string JournalPath(string root, FootprintStage stage) =>
        Path.Combine(root, $"{stage}.published.json");
    private static string PromotionJournalPath(string root, FootprintStage stage) =>
        Path.Combine(root, $"{stage}.promotion.json");
    private static string TemporaryDirectory(string root, FootprintStage stage) =>
        Path.Combine(root, $"{stage}.tmp");

    private static void ResetTemporaryDirectory(string path)
    {
        DeleteDirectory(path);
        Directory.CreateDirectory(path);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static CaptureRunResult Result(CaptureRunTerminalStatus status, string code,
        string messageZh, IReadOnlyList<CaptureStageRecord> stages) => new(status, code, messageZh, stages);

    private sealed record PublishedStageJournal(FootprintStage Stage,
        CaptureStageExecutionResult Result, DateTimeOffset PublishedAtUtc);

    private sealed record StagePromotionJournal(FootprintStage Stage,
        string CommonRelativeDirectory, CaptureStageExecutionResult Result,
        DateTimeOffset PublishedAtUtc);

    private sealed record PromotionDirectoryPaths(string FinalDirectory,
        string StagingDirectory, string BackupDirectory);
}

internal static class CaptureStageListExtensions
{
    internal static int IndexOf(this IReadOnlyList<FootprintStage> stages, FootprintStage stage)
    {
        for (var index = 0; index < stages.Count; index++)
            if (stages[index] == stage) return index;
        return -1;
    }
}
