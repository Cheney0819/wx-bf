using System.Reflection;
using System.Text;
using Footprint.Capture.Windows;
using Footprint.Core;
using Footprint.Core.Capture;
using Footprint.Core.Runtime;
using Footprint.Core.State;

namespace Footprint.Worker;

public sealed class CaptureRuntimeAssemblyWorker : ICaptureWorkerRuntime
{
    private readonly Func<CaptureWorkerOptions, CancellationToken, Task<CaptureRuntimeComposition>> _compose;

    public CaptureRuntimeAssemblyWorker() : this(CaptureRuntimeComposition.CreateProductionAsync)
    {
    }

    internal CaptureRuntimeAssemblyWorker(
        Func<CaptureWorkerOptions, CancellationToken, Task<CaptureRuntimeComposition>> compose) =>
        _compose = compose ?? throw new ArgumentNullException(nameof(compose));

    public async Task<CaptureRunTerminalStatus> RunAsync(CaptureWorkerOptions options,
        CancellationToken cancellationToken)
    {
        var audit = CreateAudit(options);
        audit?.Write("采集", options.RunId, "正在组装采集运行时", "准备执行密钥提取阶段");
        await using var composition = await _compose(options, cancellationToken).ConfigureAwait(false);
        audit?.Write("采集", options.RunId, "采集运行时组装完成", "开始执行密钥提取阶段");
        var hooks = audit is null ? null : new CaptureStageRunnerHooks(
            beforeExecute: (stage, _) =>
            {
                audit.Write("采集", options.RunId, "密钥提取阶段",
                    $"阶段={KeyExtractionAuditLog.StageNameZh(stage)}；状态=开始执行");
                return Task.CompletedTask;
            },
            afterExecute: (stage, result, _) =>
            {
                audit.Write("采集", options.RunId, "密钥提取阶段",
                    $"阶段={KeyExtractionAuditLog.StageNameZh(stage)}；状态={KeyExtractionAuditLog.StatusNameZh(result.Status)}；结果={result.MessageZh}");
                return Task.CompletedTask;
            },
            executeFailed: (stage, _) =>
            {
                audit.Write("采集", options.RunId, "密钥提取阶段",
                    $"阶段={KeyExtractionAuditLog.StageNameZh(stage)}；状态=异常停止");
                return Task.CompletedTask;
            });
        var runner = new CaptureStageRunner(composition.StateStore, composition.StageExecutor,
            composition.DecompressionPort, hooks);
        var result = await runner.RunAsync(composition.Request, cancellationToken).ConfigureAwait(false);
        foreach (var stage in result.Stages)
        {
            audit?.Write("采集", options.RunId, "密钥提取阶段",
                $"阶段={KeyExtractionAuditLog.StageNameZh(stage.Stage)}；状态={KeyExtractionAuditLog.StatusNameZh(stage.Status)}；结果={stage.MessageZh}");
        }
        audit?.Write("采集", options.RunId, "密钥提取阶段已结束", result.MessageZh);
        return result.Status;
    }

    internal static KeyExtractionAuditLog? CreateAudit(CaptureWorkerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.LogFilePath)) return null;
        var outbox = string.IsNullOrWhiteSpace(options.EventOutboxDirectory) ||
                     string.IsNullOrWhiteSpace(options.DeviceId)
            ? null
            : new SourceEventOutbox(options.EventOutboxDirectory, options.DeviceId);
        return new KeyExtractionAuditLog(options.LogFilePath, outbox);
    }
}

internal sealed class CaptureRuntimeComposition(
    IFootprintStateStore stateStore,
    ICaptureStageExecutor stageExecutor,
    IWcdbDecompressionPort decompressionPort,
    CaptureRunRequest request,
    IAsyncDisposable? lifetime = null) : IAsyncDisposable
{
    internal IFootprintStateStore StateStore { get; } = stateStore;
    internal ICaptureStageExecutor StageExecutor { get; } = stageExecutor;
    internal IWcdbDecompressionPort DecompressionPort { get; } = decompressionPort;
    internal CaptureRunRequest Request { get; } = request;

    public ValueTask DisposeAsync() => lifetime?.DisposeAsync() ?? ValueTask.CompletedTask;

    internal static async Task<CaptureRuntimeComposition> CreateProductionAsync(CaptureWorkerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var audit = CaptureRuntimeAssemblyWorker.CreateAudit(options);
        audit?.Write("采集", options.RunId, "正在释放采集运行时", "准备 Python、Frida 和数据库工具");
        var assembly = Assembly.LoadFrom(options.RuntimeAssemblyPath);
        var runtime = await new CaptureRuntimeBootstrapper(assembly, options.StateRoot)
            .EnsureAsync(cancellationToken).ConfigureAwait(false);
        audit?.Write("采集", options.RunId, "采集运行时已就绪", "Python、Frida 和数据库工具已准备");
        audit?.Write("采集", options.RunId, "正在定位微信", "查找微信程序和 Weixin.dll");
        var installation = WeixinLocator.Locate();
        audit?.Write("采集", options.RunId, "微信定位完成", "已找到微信程序和 Weixin.dll");
        var catalogSelection = new ProfileCatalog().Select(installation.DllPath, runtime.ProfilePaths);
        var selection = catalogSelection.Selection;
        if (catalogSelection.ProfilePath is { } profilePath)
            runtime = runtime with { ProfilePath = profilePath };
        audit?.Write("采集", options.RunId, "微信版本校验完成",
            selection.Accepted
                ? $"配置匹配；Weixin.dll 摘要={selection.DllSha256}"
                : $"配置不匹配；Weixin.dll 摘要={selection.DllSha256}");
        var generationSha = IsSha256(selection.DllSha256)
            ? selection.DllSha256.ToLowerInvariant()
            : await Hashing.Sha256FileAsync(installation.DllPath, cancellationToken).ConfigureAwait(false);
        var deviceId = string.IsNullOrWhiteSpace(options.DeviceId)
            ? StableIdentity("device", Environment.MachineName)
            : options.DeviceId;
        var accountHash = StableIdentity("account", $"{Environment.UserDomainName}\\{Environment.UserName}");
        var store = new SqliteFootprintStateStore(Path.Combine(options.StateRoot, "Footprint_State.db"));
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var active = await store.LoadActiveCaptureRunAsync(cancellationToken).ConfigureAwait(false);
        var generation = active is not null && string.Equals(active.RunId, options.RunId, StringComparison.Ordinal)
            ? active.Generation
            : new CaptureGenerationId(deviceId, accountHash, generationSha,
                checked((active?.Generation.CaptureGeneration ?? 0) + 1));
        var workspace = CaptureWorkspace.Create(options.StateRoot, options.RunId);
        var pipelineStatePath = Path.Combine(options.StateRoot, "Footprint_Sessions", options.RunId,
            "pipeline-state.json");
        var pipeline = await CapturePipelineState.OpenAsync(pipelineStatePath, options.StateRoot, options.RunId,
            runtime, installation, selection, cancellationToken).ConfigureAwait(false);
        var frida = new FridaCaptureClient(runtime.PythonExecutable, runtime.FridaHostScript,
            runtime.AgentScript);
        audit?.Write("采集", options.RunId, "Frida 密钥捕获链路已创建", "等待微信进程和数据库连接");
        var processProbe = new WindowsWeixinProbe(installation, selection);
        var sessionFactory = new FridaSessionFactory(frida);
        var restartExecutor = new WindowsRestartExecutor(store, processProbe, frida);
        var cachedKeys = new WindowsCachedKeyValidationPort(new WindowsCredentialKeyStore(workspace.SecretsPath),
            new SqlCipherVerifier(), runtime.SqlCipherExecutable);
        var executor = new ProductionCaptureStageExecutor(pipeline, processProbe, restartExecutor,
            sessionFactory, cachedKeys, new WindowsUserActivityProbe(), store,
            options.RemoteRestartCommandId);
        var decompressor = new FridaWcdbDecompressionAdapter(pipeline);
        var request = new CaptureRunRequest(options.RunId, generation, workspace, options.StateRoot);
        return new CaptureRuntimeComposition(store, executor, decompressor, request, pipeline);
    }

    private static string StableIdentity(string category, string value) =>
        Hashing.Sha256Hex(Encoding.UTF8.GetBytes($"Footprint:{category}:{value}"));

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
