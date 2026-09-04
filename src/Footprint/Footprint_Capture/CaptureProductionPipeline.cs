using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Footprint.Capture.Windows;
using Footprint.Core;
using Footprint.Core.Capture;
using Footprint.Core.Contracts;
using Footprint.Core.Runtime;
using Footprint.Core.State;

namespace Footprint.Worker;

internal interface IFridaDecompressionSession : IAsyncDisposable
{
    string OutputDirectory { get; }
    Task<bool> WaitForKeyCaptureAsync(CancellationToken cancellationToken);
    Task<DecompressionResult> DecompressAsync(IReadOnlyList<DatabaseBinding> bindings,
        CancellationToken cancellationToken);
}

internal interface IFridaSessionFactory
{
    Task<IFridaDecompressionSession> AttachAsync(int processId, string profilePath,
        string outputDirectory, CancellationToken cancellationToken);
    IFridaDecompressionSession FromRestartSession(IFridaCaptureSession captureSession, string outputDirectory);
}

internal sealed class FridaSessionFactory(IFridaCaptureClient client) : IFridaSessionFactory
{
    public async Task<IFridaDecompressionSession> AttachAsync(int processId, string profilePath,
        string outputDirectory, CancellationToken cancellationToken) =>
        new FridaDecompressionSession(client,
            await client.AttachAsync(processId, profilePath, outputDirectory, cancellationToken)
                .ConfigureAwait(false), outputDirectory);

    public IFridaDecompressionSession FromRestartSession(IFridaCaptureSession captureSession,
        string outputDirectory) => new FridaDecompressionSession(client, captureSession, outputDirectory);
}

internal sealed class FridaDecompressionSession(
    IFridaCaptureClient client,
    IFridaCaptureSession session,
    string outputDirectory) : IFridaDecompressionSession
{
    public string OutputDirectory { get; } = Path.GetFullPath(outputDirectory);

    public Task<bool> WaitForKeyCaptureAsync(CancellationToken cancellationToken) =>
        session.WaitForKeyCaptureAsync(cancellationToken);

    public Task<DecompressionResult> DecompressAsync(IReadOnlyList<DatabaseBinding> bindings,
        CancellationToken cancellationToken) => session is FridaCaptureSession concrete
        ? client.DecompressAsync(concrete, bindings, OutputDirectory, cancellationToken)
        : Task.FromResult(new DecompressionResult(false, "frida_session_invalid",
            "Frida 会话类型无效。", OutputDirectory));

    public ValueTask DisposeAsync() => session.DisposeAsync();
}

internal sealed class PersistedFridaDecompressionSession(string outputDirectory) : IFridaDecompressionSession
{
    public string OutputDirectory { get; } = Path.GetFullPath(outputDirectory);

    public Task<bool> WaitForKeyCaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(Path.Combine(OutputDirectory, "runtime-export",
            "decompression-summary.json")));
    }

    public async Task<DecompressionResult> DecompressAsync(IReadOnlyList<DatabaseBinding> bindings,
        CancellationToken cancellationToken)
    {
        var export = Path.Combine(OutputDirectory, "runtime-export");
        var summaryPath = Path.Combine(export, "decompression-summary.json");
        if (!File.Exists(summaryPath))
            return new DecompressionResult(false, "frida_decompression_missing",
                "Frida 解压结果缺失，已停止发布。", export);
        var bytes = await File.ReadAllBytesAsync(summaryPath, cancellationToken).ConfigureAwait(false);
        var expected = bindings.Select(binding => new DecompressionSummaryBinding(binding.Path, binding.Tag,
            binding.KeySha256, binding.KeyLength)).ToArray();
        var validation = DecompressionSummaryValidator.Validate(bytes, expected);
        return validation.IsValid
            ? new DecompressionResult(true,
                validation.Code == DecompressionSummaryValidator.SummaryValidWithOptionalFailuresCode
                    ? validation.Code
                    : "frida_decompression_ready",
                validation.Code == DecompressionSummaryValidator.SummaryValidWithOptionalFailuresCode
                    ? validation.MessageZh
                    : "Frida 解压结果已就绪。", export)
            : new DecompressionResult(false, validation.Code, validation.MessageZh, export);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal interface ICachedKeyValidationPort
{
    Task CacheAsync(CaptureStageContext context, IReadOnlyList<DatabaseBinding> bindings,
        CancellationToken cancellationToken);

    Task<CachedKeyValidationResult> ValidateAsync(CaptureStageContext context,
        IReadOnlyList<DatabaseBinding> bindings, CancellationToken cancellationToken);
}

internal sealed class WindowsCachedKeyValidationPort(
    ICachedKeyStore keyStore,
    SqlCipherVerifier verifier,
    string sqlCipherExecutable) : ICachedKeyValidationPort
{
    private readonly Func<string, byte[]> _unprotect = ProtectedKeyStore.UnprotectFromFile;

    internal WindowsCachedKeyValidationPort(ICachedKeyStore keyStore, SqlCipherVerifier verifier,
        string sqlCipherExecutable, Func<string, byte[]> unprotect)
        : this(keyStore, verifier, sqlCipherExecutable) =>
        _unprotect = unprotect ?? throw new ArgumentNullException(nameof(unprotect));

    public async Task CacheAsync(CaptureStageContext context, IReadOnlyList<DatabaseBinding> bindings,
        CancellationToken cancellationToken)
    {
        foreach (var binding in bindings.OrderBy(item => item.Path, PathComparer()))
        {
            var protectedPaths = binding.Evidence.Select(item => item.ProtectedKeyPath)
                .OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(PathComparer()).ToArray();
            if (protectedPaths.Length != 1)
                throw new InvalidDataException("数据库绑定缺少唯一的受保护密钥路径。");

            byte[]? plaintext = null;
            try
            {
                plaintext = _unprotect(protectedPaths[0]);
                if (plaintext.Length != binding.KeyLength ||
                    !string.Equals(Hashing.Sha256Hex(plaintext), binding.KeySha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("受保护数据库密钥与绑定证据不一致。");
                var tag = binding.Tag.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await keyStore.SaveAsync(context.RunId, context.Generation, tag, plaintext, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public async Task<CachedKeyValidationResult> ValidateAsync(CaptureStageContext context,
        IReadOnlyList<DatabaseBinding> bindings, CancellationToken cancellationToken)
    {
        foreach (var binding in bindings.OrderBy(item => item.Path, PathComparer()))
        {
            var tag = binding.Tag.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var candidate = await keyStore.LoadAsync(context.RunId, context.Generation, tag, cancellationToken)
                .ConfigureAwait(false);
            if (candidate is null) continue;
            var result = await CachedKeyValidator.ValidateAsync(context.Generation, binding.Path, tag, candidate,
                verifier, sqlCipherExecutable, binding.Compatibility, binding.PageSize, "4.1.0",
                Path.Combine(context.Workspace.SecretsPath, ".verification"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Accepted) return result;
            await keyStore.DeleteAsync(context.RunId, context.Generation, tag, cancellationToken)
                .ConfigureAwait(false);
        }
        return CachedKeyValidationResult.Failure("cached_key_unavailable", "未找到可复用的缓存密钥。");
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed class CapturePipelineState(
    CaptureRuntimeEnvironment? runtime = null,
    WeixinInstallation? installation = null,
    ProfileSelectionResult? profileSelection = null,
    string? statePath = null,
    string? stateRoot = null,
    string? runId = null) : IAsyncDisposable
{
    private const string StateSchema = "Footprint_CapturePipelineState_v1";
    private const int MaximumStateBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions StateJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly object _gate = new();
    private readonly string? _statePath = statePath is null ? null : Path.GetFullPath(statePath);
    private readonly string? _stateRoot = stateRoot is null ? null : Path.GetFullPath(stateRoot);
    private readonly string? _runId = runId;
    private IFridaDecompressionSession? _session;
    private IReadOnlyList<DatabaseBinding> _bindings = [];
    private IReadOnlyList<WeixinProcessSnapshot> _processes = [];

    internal CaptureRuntimeEnvironment? Runtime { get; } = runtime;
    internal WeixinInstallation? Installation { get; } = installation;
    internal ProfileSelectionResult? ProfileSelection { get; } = profileSelection;

    internal static async Task<CapturePipelineState> OpenAsync(string statePath, string stateRoot, string runId,
        CaptureRuntimeEnvironment? runtime = null, WeixinInstallation? installation = null,
        ProfileSelectionResult? profileSelection = null, CancellationToken cancellationToken = default)
    {
        var state = new CapturePipelineState(runtime, installation, profileSelection, statePath, stateRoot, runId);
        if (!File.Exists(state._statePath)) return state;
        try
        {
            if (new FileInfo(state._statePath!).Length is <= 0 or > MaximumStateBytes)
                throw new InvalidDataException("采集恢复状态无效。");
            var bytes = await File.ReadAllBytesAsync(state._statePath!, cancellationToken).ConfigureAwait(false);
            var persisted = JsonSerializer.Deserialize<PersistedPipelineState>(bytes, StateJson)
                ?? throw new InvalidDataException("采集恢复状态无效。");
            if (!string.Equals(persisted.Schema, StateSchema, StringComparison.Ordinal) ||
                !string.Equals(persisted.RunId, runId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(persisted.OutputDirectory) || persisted.Bindings is null)
                throw new InvalidDataException("采集恢复状态无效。");
            var output = Path.GetFullPath(persisted.OutputDirectory);
            EnsureUnderRoot(output, state._stateRoot!);
            var bindings = persisted.Bindings.Select(ToBinding).ToArray();
            ValidateBindings(bindings);
            state._session = new PersistedFridaDecompressionSession(output);
            state._bindings = bindings;
            return state;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or
                                           UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidDataException("采集恢复状态无效。", exception);
        }
    }

    internal void SetProcesses(IReadOnlyList<WeixinProcessSnapshot> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        lock (_gate) _processes = processes.ToArray();
    }

    internal IReadOnlyList<WeixinProcessSnapshot> GetProcesses()
    {
        lock (_gate) return _processes.ToArray();
    }

    internal void SetDecompressionSession(IFridaDecompressionSession session,
        IReadOnlyList<DatabaseBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(bindings);
        lock (_gate)
        {
            if (_session is not null && !ReferenceEquals(_session, session))
                throw new InvalidOperationException("Frida 采集会话已存在。");
            _session = session;
            _bindings = bindings.ToArray();
        }
    }

    internal async Task SetDecompressionSessionAsync(IFridaDecompressionSession session,
        IReadOnlyList<DatabaseBinding> bindings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(bindings);
        await PersistAsync(session, bindings, cancellationToken).ConfigureAwait(false);
        SetDecompressionSession(session, bindings);
    }

    internal void SetBindings(IReadOnlyList<DatabaseBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        lock (_gate) _bindings = bindings.ToArray();
    }

    internal async Task SetBindingsAsync(IReadOnlyList<DatabaseBinding> bindings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        IFridaDecompressionSession session;
        lock (_gate) session = _session ?? throw new InvalidOperationException("缺少已验证的 Frida 会话。");
        await PersistAsync(session, bindings, cancellationToken).ConfigureAwait(false);
        SetBindings(bindings);
    }

    internal (IFridaDecompressionSession Session, IReadOnlyList<DatabaseBinding> Bindings) GetDecompressionInput()
    {
        lock (_gate)
        {
            if (_session is null || _bindings.Count == 0)
                throw new InvalidOperationException("缺少已验证的 Frida 会话或数据库绑定。");
            return (_session, _bindings.ToArray());
        }
    }

    internal IFridaDecompressionSession GetSession()
    {
        lock (_gate) return _session ?? throw new InvalidOperationException("缺少已验证的 Frida 会话。");
    }

    internal IFridaDecompressionSession? TryGetSession()
    {
        lock (_gate) return _session;
    }

    internal async Task ClearSessionAsync(CancellationToken cancellationToken)
    {
        IFridaDecompressionSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _bindings = [];
        }
        if (_statePath is not null && File.Exists(_statePath))
            File.Delete(_statePath);
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal bool HasCompleteDecompressionInput()
    {
        lock (_gate) return _session is not null && _bindings.Count > 0;
    }

    internal async Task<bool> ValidatePersistedDecompressionInputAsync(CancellationToken cancellationToken)
    {
        IFridaDecompressionSession? session;
        IReadOnlyList<DatabaseBinding> bindings;
        lock (_gate)
        {
            session = _session;
            bindings = _bindings.ToArray();
        }

        if (session is null || bindings.Count == 0) return false;
        if (session is not PersistedFridaDecompressionSession) return true;
        if (await HasValidDecompressionSummaryAsync(session, bindings, cancellationToken).ConfigureAwait(false))
            return true;

        await ClearSessionAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    internal IReadOnlyList<DatabaseBinding> GetBindings()
    {
        lock (_gate) return _bindings.ToArray();
    }

    private static async Task<bool> HasValidDecompressionSummaryAsync(IFridaDecompressionSession session,
        IReadOnlyList<DatabaseBinding> bindings, CancellationToken cancellationToken)
    {
        try
        {
            var summaryPath = Path.Combine(session.OutputDirectory, "runtime-export", "decompression-summary.json");
            if (!File.Exists(summaryPath)) return false;
            var bytes = await File.ReadAllBytesAsync(summaryPath, cancellationToken).ConfigureAwait(false);
            var expected = bindings.Select(binding => new DecompressionSummaryBinding(binding.Path, binding.Tag,
                binding.KeySha256, binding.KeyLength)).ToArray();
            return DecompressionSummaryValidator.TryValidate(bytes, expected, out _);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private async Task PersistAsync(IFridaDecompressionSession session,
        IReadOnlyList<DatabaseBinding> bindings, CancellationToken cancellationToken)
    {
        if (_statePath is null) return;
        EnsureUnderRoot(session.OutputDirectory, _stateRoot!);
        ValidateBindings(bindings);
        var persisted = new PersistedPipelineState(StateSchema, _runId!, session.OutputDirectory,
            bindings.Select(FromBinding).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(persisted, StateJson);
        if (bytes.Length > MaximumStateBytes) throw new InvalidDataException("采集恢复状态过大。");
        await AtomicFile.WriteAsync(_statePath, (stream, token) => stream.WriteAsync(bytes, token).AsTask(),
            cancellationToken).ConfigureAwait(false);
    }

    private static PersistedBinding FromBinding(DatabaseBinding binding) => new(binding.Path, binding.Tag,
        binding.Wrapper, binding.Core, binding.DbPointer, binding.KeySha256, binding.KeyLength, binding.PageSize,
        binding.Compatibility, binding.PathFromDb, binding.ProfileSha256);

    private static DatabaseBinding ToBinding(PersistedBinding binding) => new(binding.Path, binding.Tag,
        binding.Wrapper, binding.Core, binding.DbPointer, binding.KeySha256, binding.KeyLength, binding.PageSize,
        binding.Compatibility, [], binding.PathFromDb, binding.ProfileSha256);

    private static void ValidateBindings(IReadOnlyList<DatabaseBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.Path) || string.IsNullOrWhiteSpace(binding.DbPointer) ||
                binding.Tag < 0 || binding.KeyLength <= 0 || binding.PageSize <= 0 ||
                binding.KeySha256 is not { Length: 64 } || binding.KeySha256.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("采集恢复绑定状态无效。");
        }
    }

    private static void EnsureUnderRoot(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("采集恢复路径越界。");
    }

    public async ValueTask DisposeAsync()
    {
        IFridaDecompressionSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _bindings = [];
            _processes = [];
        }
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record PersistedPipelineState(string Schema, string RunId, string OutputDirectory,
        IReadOnlyList<PersistedBinding> Bindings);
    private sealed record PersistedBinding(string Path, int Tag, string Wrapper, string Core, string DbPointer,
        string KeySha256, int KeyLength, int PageSize, int Compatibility, string? PathFromDb,
        string? ProfileSha256);
}

internal interface IRestartSafetyState
{
    bool IsMaintenanceLocked(string stateRoot);
    bool TryAcquire(string stateRoot, out IDisposable? lease);
}

internal sealed class FileSemaphoreRestartSafetyState : IRestartSafetyState
{
    private static readonly object FallbackGate = new();
    private static readonly HashSet<string> FallbackOwners = new(StringComparer.Ordinal);

    public bool IsMaintenanceLocked(string stateRoot) => File.Exists(Path.Combine(
        Directory.GetParent(Path.GetFullPath(stateRoot))?.FullName ?? stateRoot, "Footprint_Maintenance.lock"));

    public bool TryAcquire(string stateRoot, out IDisposable? lease)
    {
        var name = "Footprint_WeixinRestart_" + Hashing.Sha256Hex(Encoding.UTF8.GetBytes(Path.GetFullPath(stateRoot)))[..24];
        if (OperatingSystem.IsWindows())
        {
            Semaphore? semaphore = null;
            try
            {
                semaphore = new Semaphore(1, 1, name);
                if (!semaphore.WaitOne(0)) { lease = null; return false; }
                lease = new SemaphoreLease(semaphore);
                semaphore = null;
                return true;
            }
            finally { semaphore?.Dispose(); }
        }

        lock (FallbackGate)
        {
            if (!FallbackOwners.Add(name)) { lease = null; return false; }
            lease = new FallbackLease(name);
            return true;
        }
    }

    private sealed class SemaphoreLease(Semaphore semaphore) : IDisposable
    {
        private Semaphore? _semaphore = semaphore;
        public void Dispose()
        {
            var semaphore = Interlocked.Exchange(ref _semaphore, null);
            if (semaphore is null) return;
            try { semaphore.Release(); }
            finally { semaphore.Dispose(); }
        }
    }

    private sealed class FallbackLease(string name) : IDisposable
    {
        private string? _name = name;
        public void Dispose()
        {
            var name = Interlocked.Exchange(ref _name, null);
            if (name is null) return;
            lock (FallbackGate) FallbackOwners.Remove(name);
        }
    }
}

internal sealed class ProductionCaptureStageExecutor(
    CapturePipelineState state,
    IWeixinProcessProbe processProbe,
    IRestartExecutor restartExecutor,
    IFridaSessionFactory sessionFactory,
    ICachedKeyValidationPort cachedKeyValidation,
    IUserActivityProbe userActivity,
    IFootprintStateStore stateStore,
    string? verifiedRemoteRestartCommandId = null,
    IRestartSafetyState? restartSafety = null) : ICaptureStageExecutor
{
    private readonly WindowsMediaInventory _mediaInventory = new();

    public async Task<CaptureStageExecutionResult> ExecuteAsync(CaptureStageContext context,
        string temporaryDirectory, CancellationToken cancellationToken)
    {
        return context.Stage switch
        {
            FootprintStage.Footprint_Runtime => RequireRuntime(),
            FootprintStage.Footprint_WeixinDetection => DetectInstallation(),
            FootprintStage.Footprint_VersionVerification => VerifyProfile(),
            FootprintStage.Footprint_KeyValidation => await ValidateCachedKeyAsync(context, cancellationToken)
                .ConfigureAwait(false),
            FootprintStage.Footprint_KeyCapture => await CaptureKeyAsync(context, cancellationToken)
                .ConfigureAwait(false),
            FootprintStage.Footprint_WeixinRestart => await RestartForCaptureAsync(context, cancellationToken)
                .ConfigureAwait(false),
            FootprintStage.Footprint_ConnectionBinding => await BindConnectionsAsync(context, cancellationToken)
                .ConfigureAwait(false),
            FootprintStage.Footprint_DatabaseSnapshot => await SnapshotDatabasesAsync(context, temporaryDirectory,
                cancellationToken).ConfigureAwait(false),
            FootprintStage.Footprint_ImageSnapshot => await SnapshotMediaAsync(CaptureSourceCategory.Image,
                temporaryDirectory, cancellationToken).ConfigureAwait(false),
            FootprintStage.Footprint_VoiceSnapshot => await SnapshotMediaAsync(CaptureSourceCategory.Voice,
                temporaryDirectory, cancellationToken).ConfigureAwait(false),
            FootprintStage.Footprint_FavoriteSnapshot => await SnapshotMediaAsync(CaptureSourceCategory.Favorite,
                temporaryDirectory, cancellationToken).ConfigureAwait(false),
            _ => CaptureStageExecutionResult.Failure("capture_stage_unsupported", "采集阶段不受支持。")
        };
    }

    private async Task<CaptureStageExecutionResult> ValidateCachedKeyAsync(CaptureStageContext context,
        CancellationToken cancellationToken)
    {
        var bindings = state.GetBindings();
        if (bindings.Count == 0 || !state.HasCompleteDecompressionInput())
            return CaptureStageExecutionResult.Success("cached_key_unavailable",
                "未找到可复用的缓存密钥，继续被动采集。");
        if (!await state.ValidatePersistedDecompressionInputAsync(cancellationToken).ConfigureAwait(false))
        {
            DeleteJournalsAfterKeyValidation(context);
            return CaptureStageExecutionResult.Success("cached_key_unavailable",
                "未找到可复用的缓存密钥，继续被动采集。");
        }
        var result = await cachedKeyValidation.ValidateAsync(context, bindings, cancellationToken)
            .ConfigureAwait(false);
        return result.Accepted
            ? CaptureStageExecutionResult.Success(result.Code, result.MessageZh,
                CaptureStageDirective.CachedKeyAccepted)
            : CaptureStageExecutionResult.Success(result.Code, result.MessageZh);
    }

    private static void DeleteJournalsAfterKeyValidation(CaptureStageContext context)
    {
        var journalRoot = Path.Combine(Path.GetFullPath(context.StateRoot), "Footprint_StageRuns", context.RunId);
        if (!Directory.Exists(journalRoot)) return;
        foreach (var stage in CaptureStageRunner.StageOrder
                     .SkipWhile(stage => stage != FootprintStage.Footprint_KeyCapture))
        {
            DeleteFileIfExists(Path.Combine(journalRoot, $"{stage}.published.json"));
            DeleteFileIfExists(Path.Combine(journalRoot, $"{stage}.promotion.json"));
            DeleteDirectoryIfExists(Path.Combine(journalRoot, $"{stage}.tmp"));
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private CaptureStageExecutionResult RequireRuntime() => state.Runtime is null
        ? CaptureStageExecutionResult.Failure("capture_runtime_missing", "采集运行时未完成验证。")
        : CaptureStageExecutionResult.Success("capture_runtime_ready", "采集运行时已验证。");

    private CaptureStageExecutionResult DetectInstallation() => state.Installation is null
        ? CaptureStageExecutionResult.Failure("weixin_installation_missing", "未找到微信安装。")
        : CaptureStageExecutionResult.Success("weixin_installation_ready", "微信安装已定位。");

    private CaptureStageExecutionResult VerifyProfile()
    {
        var selection = state.ProfileSelection;
        return selection is { Accepted: true }
            ? CaptureStageExecutionResult.Success("weixin_profile_verified", "微信版本配置已验证。")
            : CaptureStageExecutionResult.Failure(selection?.ErrorCode ?? "weixin_profile_unsupported",
                selection?.MessageZh ?? "当前微信版本不受支持，已停止采集。");
    }

    private async Task<CaptureStageExecutionResult> CaptureKeyAsync(CaptureStageContext context,
        CancellationToken cancellationToken)
    {
        if (await TryResumeCapturedSessionAsync(cancellationToken).ConfigureAwait(false))
            return CaptureStageExecutionResult.Success("passive_key_capture_resumed",
                "已恢复完成的被动密钥采集。", CaptureStageDirective.PassiveKeyAccepted);

        var processes = await processProbe.CaptureAsync(cancellationToken).ConfigureAwait(false);
        state.SetProcesses(processes);
        var process = processes.OrderByDescending(item => item.IsForeground).ThenBy(item => item.ProcessId)
            .FirstOrDefault();
        if (process is null)
            return CaptureStageExecutionResult.Success("weixin_restart_required",
                "未发现可被动附加的微信进程，需要受控启动。", CaptureStageDirective.RestartRequired);

        var runtime = state.Runtime ?? throw new InvalidOperationException("采集运行时未初始化。");
        var output = SessionOutput(context);
        IFridaDecompressionSession? session = null;
        try
        {
            session = await sessionFactory.AttachAsync(process.ProcessId, runtime.ProfilePath, output,
                cancellationToken).ConfigureAwait(false);
            if (!await session.WaitForKeyCaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return CaptureStageExecutionResult.Success("passive_key_capture_failed",
                    "被动密钥采集未完成，需要受控重启。", CaptureStageDirective.RestartRequired);
            }
            if (!await HasCompletePassiveCaptureAsync(session, cancellationToken).ConfigureAwait(false))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return CaptureStageExecutionResult.Success("passive_key_capture_incomplete",
                    "被动采集缺少完整数据库配置证据，需要受控重启。", CaptureStageDirective.RestartRequired);
            }
            await state.SetDecompressionSessionAsync(session, [], cancellationToken).ConfigureAwait(false);
            return CaptureStageExecutionResult.Success("passive_key_captured", "被动密钥采集已完成。",
                CaptureStageDirective.PassiveKeyAccepted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            return CaptureStageExecutionResult.Success("passive_key_capture_failed",
                "被动密钥采集未完成，需要受控重启。", CaptureStageDirective.RestartRequired);
        }
    }

    private static async Task<bool> HasCompletePassiveCaptureAsync(IFridaDecompressionSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var eventsPath = Path.Combine(session.OutputDirectory, "capture-events.jsonl");
            if (!File.Exists(eventsPath)) return false;
            var binder = new CaptureBinder(TimeSpan.FromSeconds(5), File.Exists);
            var configurationBoundaries = 0;
            await foreach (var line in File.ReadLinesAsync(eventsPath, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var captureEvent = CaptureJson.Parse(line).Event;
                if (string.Equals(captureEvent.Kind, "profile", StringComparison.Ordinal) &&
                    string.Equals(captureEvent.Boundary, "config_cipher", StringComparison.Ordinal))
                    configurationBoundaries++;
                binder.Add(captureEvent);
            }

            if (configurationBoundaries == 0) return false;
            var result = binder.Build();
            return result.Bindings.Count > 0 && result.Ambiguities.Count == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private async Task<CaptureStageExecutionResult> RestartForCaptureAsync(CaptureStageContext context,
        CancellationToken cancellationToken)
    {
        if (await TryResumeCapturedSessionAsync(cancellationToken).ConfigureAwait(false))
            return CaptureStageExecutionResult.Success("restart_capture_resumed",
                "已恢复完成的受控重启采集。", CaptureStageDirective.PassiveKeyAccepted);

        var runtime = state.Runtime ?? throw new InvalidOperationException("采集运行时未初始化。");
        var installation = state.Installation ?? throw new InvalidOperationException("微信安装未初始化。");
        var selection = state.ProfileSelection ?? throw new InvalidOperationException("微信配置未初始化。");
        var budget = await stateStore.LoadRestartBudgetAsync(context.Generation.BudgetKey, cancellationToken)
            .ConfigureAwait(false);
        var processes = state.GetProcesses();
        var now = DateTimeOffset.UtcNow;
        var remoteRestart = !string.IsNullOrWhiteSpace(verifiedRemoteRestartCommandId);
        var requestKind = remoteRestart ? RestartRequestKind.Manual : RestartRequestKind.Automatic;
        var safety = restartSafety ?? new FileSemaphoreRestartSafetyState();
        var maintenanceLocked = safety.IsMaintenanceLocked(context.StateRoot);
        var acquired = safety.TryAcquire(context.StateRoot, out var lease);
        using (lease)
        {
        var decision = RestartDecisionEngine.Decide(new RestartDecisionContext(context.RestartPolicy,
            requestKind, selection.Accepted && selection.MayControlProcess,
            IsMaintenanceLocked: maintenanceLocked, IsRestartAlreadyRunning: acquired == false,
            IsForeground: processes.Any(item => item.IsForeground), LastInputAge: userActivity.GetLastInputAge(),
            IsGenerationBudgetConsumed: budget is not null, HasVerifiedRemoteCommand: remoteRestart,
            CooldownUntilUtc: budget?.CooldownUntilUtc, NowUtc: now));
        if (decision.IsAllowed == false)
            return CaptureStageExecutionResult.Failure(decision.Code, decision.MessageZh);
        if (safety.IsMaintenanceLocked(context.StateRoot))
            return CaptureStageExecutionResult.Failure("restart_deny_maintenance", "维护锁已生效，暂不重启微信。");

        var output = SessionOutput(context);
        await using var result = await restartExecutor.ExecuteAsync(new RestartExecutionRequest(context.RunId,
            decision, requestKind, verifiedRemoteRestartCommandId, context.Generation, installation, selection,
            runtime.ProfilePath, output, 1, 1), new NoOpEventProgress(), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccessful)
            return CaptureStageExecutionResult.Failure(result.Code, result.MessageZh);
        var session = sessionFactory.FromRestartSession(result.TakeSession(), output);
        await state.SetDecompressionSessionAsync(session, [], cancellationToken).ConfigureAwait(false);
        return CaptureStageExecutionResult.Success(result.Code, result.MessageZh,
            CaptureStageDirective.PassiveKeyAccepted);
        }
    }

    private async Task<bool> TryResumeCapturedSessionAsync(CancellationToken cancellationToken)
    {
        var session = state.TryGetSession();
        if (session is null) return false;
        try
        {
            if (await session.WaitForKeyCaptureAsync(cancellationToken).ConfigureAwait(false)) return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
        await state.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task<CaptureStageExecutionResult> BindConnectionsAsync(CaptureStageContext context,
        CancellationToken cancellationToken)
    {
        var session = state.GetSession();
        if (!await session.WaitForKeyCaptureAsync(cancellationToken).ConfigureAwait(false))
            return CaptureStageExecutionResult.Failure("frida_key_capture_failed", "Frida 密钥采集未完成。");

        var eventsPath = Path.Combine(session.OutputDirectory, "capture-events.jsonl");
        if (!File.Exists(eventsPath))
            return CaptureStageExecutionResult.Failure("capture_events_missing", "采集事件缺失，已停止绑定。");
        var binder = new CaptureBinder(TimeSpan.FromSeconds(5), File.Exists);
        await foreach (var line in File.ReadLinesAsync(eventsPath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            binder.Add(CaptureJson.Parse(line).Event);
        }
        var result = binder.Build();
        if (result.Bindings.Count == 0 || result.Ambiguities.Count > 0)
            return CaptureStageExecutionResult.Failure("capture_binding_invalid", "数据库连接绑定不完整，已停止采集。");
        var summaryValidation = await ValidateDecompressionSummaryAsync(session, result.Bindings, cancellationToken)
            .ConfigureAwait(false);
        if (!summaryValidation.IsValid)
            return CaptureStageExecutionResult.Failure(
                DecompressionSummaryValidator.StageFailureCode(summaryValidation.Code),
                summaryValidation.MessageZh);
        await state.SetBindingsAsync(result.Bindings, cancellationToken).ConfigureAwait(false);
        try
        {
            await cachedKeyValidation.CacheAsync(context, result.Bindings, cancellationToken).ConfigureAwait(false);
            return CaptureStageExecutionResult.Success("capture_binding_verified", "数据库连接绑定已验证。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Cached-key persistence is an optimization.  A machine-specific DPAPI,
            // credential-provider, or atomic-sidecar failure must not invalidate the
            // bindings captured in this run or block the remaining snapshots.
            return CaptureStageExecutionResult.Success("capture_binding_verified_cache_unavailable",
                "数据库连接绑定已验证；缓存密钥保存失败，已继续后续采集。");
        }
    }

    private static async Task<DecompressionSummaryValidationResult> ValidateDecompressionSummaryAsync(
        IFridaDecompressionSession session,
        IReadOnlyList<DatabaseBinding> bindings, CancellationToken cancellationToken)
    {
        try
        {
            var summaryPath = Path.Combine(session.OutputDirectory, "runtime-export", "decompression-summary.json");
            if (!File.Exists(summaryPath))
                return new DecompressionSummaryValidationResult(false, "frida_decompression_summary_missing",
                    "Frida 解压摘要缺失：decompression-summary.json。", default);
            var bytes = await File.ReadAllBytesAsync(summaryPath, cancellationToken).ConfigureAwait(false);
            var expected = bindings.Select(binding => new DecompressionSummaryBinding(binding.Path, binding.Tag,
                binding.KeySha256, binding.KeyLength)).ToArray();
            return DecompressionSummaryValidator.Validate(bytes, expected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException)
        {
            return new DecompressionSummaryValidationResult(false,
                DecompressionSummaryValidator.SummaryInvalidCode,
                "Frida 解压摘要读取或验证失败。", default);
        }
    }

    private async Task<CaptureStageExecutionResult> SnapshotDatabasesAsync(CaptureStageContext context,
        string temporaryDirectory, CancellationToken cancellationToken)
    {
        var bindings = state.GetDecompressionInput().Bindings;
        var artifacts = new List<CaptureStageArtifact>();
        foreach (var binding in bindings.OrderBy(item => item.Path, PathComparer()))
        {
            var identity = Hashing.Sha256Hex(Encoding.UTF8.GetBytes(NormalizePath(binding.Path)));
            var relativeDirectory = identity[..16];
            var destination = Path.Combine(temporaryDirectory, relativeDirectory);
            var snapshot = await StableSnapshotter.CreateCoherentAsync(binding.Path, destination, 4,
                cancellationToken).ConfigureAwait(false);
            if (!snapshot.Stable)
                return CaptureStageExecutionResult.Failure("database_snapshot_unstable", "数据库快照不稳定，已停止发布。");
            foreach (var file in snapshot.Files)
            {
                var evidence = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["account_identity"] = context.Generation.AccountHash,
                    ["database_tag"] = binding.Tag.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["database_pointer_identity"] = Hashing.Sha256Hex(Encoding.UTF8.GetBytes(binding.DbPointer))
                };
                if (string.Equals(file.Name, Path.GetFileName(binding.Path), StringComparison.OrdinalIgnoreCase))
                {
                    var consistency = DatabaseSnapshotConsistencyValidator.Validate(
                        Path.Combine(snapshot.Directory!, file.Name), binding.PageSize);
                    evidence["snapshot_consistency_code"] = consistency.ErrorCode;
                    evidence["snapshot_consistency_message"] = consistency.MessageZh;
                    evidence["database_size"] = consistency.DatabaseSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    evidence["wal_size"] = consistency.WalSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    evidence["shm_size"] = consistency.ShmSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    evidence["page_size"] = consistency.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    evidence["wal_header_valid"] = consistency.WalHeaderValid.ToString();
                    evidence["wal_page_size_matches"] = consistency.WalPageSizeMatches.ToString();
                    evidence["wal_frame_aligned"] = consistency.WalFrameAligned.ToString();
                    evidence["wal_salt_consistent"] = consistency.WalSaltConsistent.ToString();
                }
                artifacts.Add(new CaptureStageArtifact(
                    $"{relativeDirectory}/{file.Name}",
                    $"Footprint_Databases/{relativeDirectory}/{file.Name}",
                    file.Size, file.Sha256, CaptureSourceCategory.Database, identity,
                    snapshot.StabilityAttempts, evidence));
            }
        }
        return CaptureStageExecutionResult.Success("database_snapshot_ready", "数据库快照已完成。",
            artifacts: artifacts);
    }

    private async Task<CaptureStageExecutionResult> SnapshotMediaAsync(CaptureSourceCategory category,
        string temporaryDirectory, CancellationToken cancellationToken)
    {
        var accountRoots = FindAccountRoots(state.GetDecompressionInput().Bindings, cancellationToken);
        var discovered = accountRoots
            .SelectMany(accountRoot => _mediaInventory.DiscoverFiles(accountRoot, cancellationToken))
            .GroupBy(item => item.SourceIdentityHash, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var sources = discovered.Where(item => item.Category == category).ToArray();
        var voiceExpected = 0;
        var imageProtocolExpected = 0;

        if (category == CaptureSourceCategory.Image)
        {
            var session = state.TryGetSession();
            if (session is not null)
            {
                var imageInventory = new ImageProtocolMediaInventory().Discover(session.OutputDirectory,
                    cancellationToken);
                imageProtocolExpected = imageInventory.ExpectedArtifactCount;
                if (imageInventory.ProtocolVerified && imageInventory.DecryptedArtifactCount == 0)
                    return CaptureStageExecutionResult.Failure("image_media_missing",
                        "图片协议验证成功，但未生成可发布的解密图片文件。" );
                var protocolAssociations = imageInventory.Sources
                    .Select(source => AssociationKey(source.AssociationEvidence))
                    .OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var decryptedMainAssociations = protocolAssociations
                    .Where(key => !IsImageVariantAssociation(key))
                    .Select(CanonicalImageAssociation)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (protocolAssociations.Count > 0)
                    discovered = discovered.Where(source =>
                            source.Category != CaptureSourceCategory.Image ||
                            AssociationKey(source.AssociationEvidence) is not { } association ||
                            (!protocolAssociations.Contains(association) &&
                             !(IsImageVariantAssociation(association) &&
                               decryptedMainAssociations.Contains(CanonicalImageAssociation(association)))))
                        .ToArray();
                sources = discovered.Where(item => item.Category == category).Concat(imageInventory.Sources)
                    .GroupBy(item => item.SourceIdentityHash, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();
            }
        }

        if (category == CaptureSourceCategory.Voice)
        {
            var session = state.TryGetSession();
            var runtimeExport = session is null
                ? null
                : Path.Combine(session.OutputDirectory, "runtime-export");
            var voiceInventory = runtimeExport is null
                ? new VoiceInfoMediaInventoryResult([], 0)
                : new VoiceInfoMediaInventory().Discover(runtimeExport, cancellationToken);
            voiceExpected = voiceInventory.ExpectedRecordCount;
            sources = sources.Concat(voiceInventory.Sources)
                .GroupBy(item => item.SourceIdentityHash, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            if (voiceInventory.ExpectedRecordCount > 0 && sources.Length == 0)
                return CaptureStageExecutionResult.Failure("voice_media_missing",
                    $"VoiceInfo 检测到 {voiceInventory.ExpectedRecordCount} 条语音记录，但未生成可发布的语音文件。");
        }

        if (sources.Length == 0)
            return CaptureStageExecutionResult.Success("media_snapshot_empty",
                $"未发现待快照媒体文件。账户根目录={accountRoots.Count}；已扫描媒体文件={discovered.Length}；" +
                $"目标分类={CategoryName(category)}；VoiceInfo 预期记录={voiceExpected}；协议图片预期工件={imageProtocolExpected}。");
        var artifacts = new List<CaptureStageArtifact>(sources.Length);
        foreach (var source in sources)
        {
            var extension = SafeExtension(Path.GetExtension(source.SourcePath));
            var relative = $"{source.SourceIdentityHash}{extension}";
            var snapshot = await StableSnapshotter.CreateFileAsync(source.SourcePath,
                Path.Combine(temporaryDirectory, relative), 3, cancellationToken).ConfigureAwait(false);
            if (!snapshot.Stable)
                return CaptureStageExecutionResult.Failure("media_snapshot_unstable", "媒体快照不稳定，已停止发布。");
            artifacts.Add(new CaptureStageArtifact(relative,
                $"Footprint_MediaSnapshot/{CategoryName(category)}/{relative}", snapshot.Size, snapshot.Sha256,
                category, source.SourceIdentityHash, snapshot.StabilityAttempts, source.AssociationEvidence));
        }
        return CaptureStageExecutionResult.Success("media_snapshot_ready", "媒体快照已完成。",
            artifacts: artifacts);
    }

    private IReadOnlyList<string> FindAccountRoots(IEnumerable<DatabaseBinding> bindings,
        CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(PathComparer());
        foreach (var binding in bindings.OrderBy(item => item.Path, PathComparer()))
        {
            foreach (var path in new[] { binding.Path, binding.PathFromDb }
                         .Where(item => !string.IsNullOrWhiteSpace(item))
                         .Distinct(PathComparer()))
            {
                var current = Directory.GetParent(Path.GetFullPath(path!));
                for (var depth = 0; current is not null && depth < 16; depth++, current = current.Parent)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (_mediaInventory.DiscoverRoots(current.FullName).Count == 0) continue;
                        roots.Add(current.FullName);
                        // Keep walking: multiple sibling account roots can share a parent, and
                        // an active binding path may sit below a storage subdirectory.
                        continue;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                       InvalidDataException)
                    {
                    }
                }
            }
        }
        return roots.OrderBy(item => item, PathComparer()).ToArray();
    }

    private static string CategoryName(CaptureSourceCategory category) => category switch
    {
        CaptureSourceCategory.Image => "image",
        CaptureSourceCategory.Voice => "voice",
        CaptureSourceCategory.Favorite => "favorite",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private static string? AssociationKey(IReadOnlyDictionary<string, string> evidence)
    {
        if (!evidence.TryGetValue("candidate_root", out var root) ||
            !evidence.TryGetValue("source_relative_path", out var relative) ||
            string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative)) return null;
        return root.TrimEnd('/', '\\') + "/" + relative.TrimStart('/', '\\');
    }

    private static bool IsImageVariantAssociation(string association) =>
        association.EndsWith("_t.dat", StringComparison.OrdinalIgnoreCase) ||
        association.EndsWith("_h.dat", StringComparison.OrdinalIgnoreCase);

    private static string CanonicalImageAssociation(string association) =>
        System.Text.RegularExpressions.Regex.Replace(association, "_(?:t|h)\\.dat$", ".dat",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string SafeExtension(string extension) => string.IsNullOrWhiteSpace(extension) ||
        extension.Length > 16 || extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character))
            ? ".bin"
            : extension.ToLowerInvariant();

    private static string SessionOutput(CaptureStageContext context) =>
        Path.Combine(context.StateRoot, "Footprint_Sessions", context.RunId);

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class NoOpEventProgress : IProgress<FootprintEvent>
    {
        public void Report(FootprintEvent value) { }
    }
}
