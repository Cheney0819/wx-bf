using System.ComponentModel;
using Footprint.Core;
using Footprint.Core.Capture;

namespace Footprint.Capture.Windows;

public sealed record WindowsProcessEntry(int ProcessId, string ExecutableName);

public sealed record WindowsFileIdentity(ulong VolumeSerialNumber, ulong FileIdLow, ulong FileIdHigh);

public interface IStableFileCapture : IDisposable
{
    string CanonicalPath { get; }
    WindowsFileIdentity Identity { get; }
    Task<string> HashStableCopyAsync(CancellationToken cancellationToken);
}

public sealed class StableLoadedModuleCapture : IDisposable
{
    private readonly IStableFileCapture _file;
    private bool _disposed;

    public StableLoadedModuleCapture(IStableFileCapture file, nint moduleBase)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        ModuleBase = moduleBase;
    }

    public string CanonicalPath => _file.CanonicalPath;
    public WindowsFileIdentity Identity => _file.Identity;
    public nint ModuleBase { get; }

    public Task<string> HashStableCopyAsync(CancellationToken cancellationToken) =>
        _file.HashStableCopyAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file.Dispose();
    }
}

public interface IWindowsProcessApi
{
    IReadOnlyList<WindowsProcessEntry> EnumerateProcesses();
    string QueryExecutablePath(int processId);
    IStableFileCapture OpenStableFile(string path);
    StableLoadedModuleCapture? CaptureLoadedModule(int processId, string moduleName);
    int? GetForegroundProcessId();
}

public class WindowsObservationException : Exception
{
    public WindowsObservationException(string code, int processId = 0)
        : base(PublicMessage(code, processId))
    {
        Code = code;
        ProcessId = processId;
    }

    public string Code { get; }
    public int ProcessId { get; }

    private static string PublicMessage(string code, int processId) => code switch
    {
        "module_snapshot_changed" => $"微信进程 {processId} 的已加载模块在捕获期间发生变化。",
        "process_inaccessible" => $"无法访问微信进程 {processId}。",
        _ => "无法完成微信进程观察。"
    };
}

public sealed class WindowsProcessAccessException : WindowsObservationException
{
    public WindowsProcessAccessException(int processId) : base("process_inaccessible", processId) { }
}

public sealed class WindowsWeixinProbe : IWeixinProcessProbe, IWeixinRestartObserver
{
    private static readonly string[] ExecutableNames = ["Weixin.exe", "WeChat.exe"];
    private readonly WeixinInstallation _installation;
    private readonly ProfileSelectionResult _profileSelection;
    private readonly IWindowsProcessApi _api;
    private readonly Func<bool> _isWindows;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _observationGate = new(1, 1);
    private IReadOnlyList<WeixinProcessDiagnostic> _diagnostics = Array.Empty<WeixinProcessDiagnostic>();
    private static readonly TimeSpan ConcurrentObservationWaitTimeout = TimeSpan.FromMilliseconds(250);

    public WindowsWeixinProbe(WeixinInstallation installation, ProfileSelectionResult profileSelection)
        : this(installation, profileSelection, new ToolhelpWindowsProcessApi(), OperatingSystem.IsWindows,
            static () => DateTimeOffset.UtcNow)
    {
    }

    public WindowsWeixinProbe(WeixinInstallation installation, ProfileSelectionResult profileSelection,
        IWindowsProcessApi api, Func<bool> isWindows, Func<DateTimeOffset>? utcNow = null)
    {
        _installation = installation ?? throw new ArgumentNullException(nameof(installation));
        _profileSelection = profileSelection ?? throw new ArgumentNullException(nameof(profileSelection));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _isWindows = isWindows ?? throw new ArgumentNullException(nameof(isWindows));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<WeixinProcessDiagnostic> Diagnostics => Volatile.Read(ref _diagnostics);

    public async Task<WeixinObservationResult> ObserveAsync(CancellationToken cancellationToken)
    {
        if (!await _observationGate.WaitAsync(ConcurrentObservationWaitTimeout, cancellationToken))
        {
            var diagnostic = new WeixinProcessDiagnostic(0, "weixin_observation_in_progress",
                "上一次微信进程观察尚未结束，本次已按失败关闭。");
            var blocked = CreateObservation([], [diagnostic], 0, false);
            Volatile.Write(ref _diagnostics, blocked.Diagnostics);
            return blocked;
        }

        try
        {
            var observation = await CaptureObservationAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _diagnostics, observation.Diagnostics);
            return observation;
        }
        finally
        {
            _observationGate.Release();
        }
    }

    public async Task<IReadOnlyList<WeixinProcessSnapshot>> CaptureAsync(CancellationToken cancellationToken)
        => (await ObserveAsync(cancellationToken)).Snapshots;

    private async Task<WeixinObservationResult> CaptureObservationAsync(CancellationToken cancellationToken)
    {
        if (!_isWindows()) throw new PlatformNotSupportedException("微信进程快照仅支持 Windows。");
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSelection();

        var diagnostics = new List<WeixinProcessDiagnostic>();
        IStableFileCapture installationCapture;
        string installationHash;
        try
        {
            installationCapture = _api.OpenStableFile(_installation.DllPath);
            try
            {
                installationHash = await installationCapture.HashStableCopyAsync(cancellationToken);
            }
            catch
            {
                installationCapture.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            diagnostics.Add(new WeixinProcessDiagnostic(0, "weixin_installation_snapshot_unavailable",
                "无法稳定读取当前微信安装模块，本次未生成进程快照。"));
            return CreateObservation([], diagnostics, 0, false);
        }

        using (installationCapture)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(installationHash, _profileSelection.DllSha256, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new WeixinProcessDiagnostic(0, "weixin_installation_hash_changed",
                    "微信安装模块已在配置校验后发生变化，本次未生成进程快照。"));
                return CreateObservation([], diagnostics, 0, false);
            }

            return await CaptureProcessesAsync(installationCapture, installationHash, diagnostics,
                cancellationToken);
        }
    }

    private async Task<WeixinObservationResult> CaptureProcessesAsync(
        IStableFileCapture installationCapture, string installationHash,
        List<WeixinProcessDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var snapshots = new List<WeixinProcessSnapshot>();
        var foregroundProcessId = ReadForegroundProcessId(diagnostics);
        var capturedAtUtc = _utcNow().ToUniversalTime();
        var expectedModuleName = _profileSelection.Profile?.ModuleName ?? Path.GetFileName(_installation.DllPath);

        IReadOnlyList<WindowsProcessEntry> processes;
        try
        {
            processes = _api.EnumerateProcesses();
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            diagnostics.Add(new WeixinProcessDiagnostic(0, "weixin_process_enumeration_failed",
                "无法枚举微信进程，本次未生成进程快照。"));
            return CreateObservation([], diagnostics, 0, false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var candidates = processes
            .Where(process => IsWeixinExecutableName(process.ExecutableName))
            .OrderBy(process => process.ProcessId)
            .ToArray();
        foreach (var process in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var executablePath = _api.QueryExecutablePath(process.ProcessId);
                if (!CanonicalPathsEqual(executablePath, _installation.ExecutablePath)) continue;

                using var module = _api.CaptureLoadedModule(process.ProcessId, expectedModuleName);
                if (module is null)
                {
                    diagnostics.Add(new WeixinProcessDiagnostic(process.ProcessId,
                        "weixin_auxiliary_process_skipped",
                        $"微信辅助进程 {process.ProcessId} 未加载 {expectedModuleName}，已跳过。"));
                    continue;
                }

                if (CanonicalPathsEqual(module.CanonicalPath, installationCapture.CanonicalPath) &&
                    module.Identity != installationCapture.Identity)
                {
                    diagnostics.Add(new WeixinProcessDiagnostic(process.ProcessId,
                        "weixin_module_identity_mismatch",
                        $"微信进程 {process.ProcessId} 的已加载模块文件身份与当前安装文件不一致，已跳过该进程。"));
                    continue;
                }

                string loadedHash;
                try
                {
                    loadedHash = await module.HashStableCopyAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception exception) when (IsObservationFailure(exception))
                {
                    diagnostics.Add(new WeixinProcessDiagnostic(process.ProcessId,
                        "weixin_module_backing_unavailable",
                        $"无法稳定读取微信进程 {process.ProcessId} 的已加载模块，已跳过该进程。"));
                    continue;
                }

                if (!string.Equals(loadedHash, installationHash, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new WeixinProcessDiagnostic(process.ProcessId, "weixin_module_hash_mismatch",
                        $"微信进程 {process.ProcessId} 已加载模块与当前安装文件不一致，已跳过该进程。"));
                    continue;
                }

                snapshots.Add(new WeixinProcessSnapshot(process.ProcessId, executablePath, module.CanonicalPath,
                    installationHash, loadedHash, module.ModuleBase, foregroundProcessId == process.ProcessId,
                    capturedAtUtc));
            }
            catch (WindowsObservationException exception) when (exception.Code == "module_snapshot_changed")
            {
                diagnostics.Add(new WeixinProcessDiagnostic(process.ProcessId, "weixin_module_snapshot_changed",
                    $"微信进程 {process.ProcessId} 的已加载模块在捕获期间发生变化，已跳过该进程。"));
            }
            catch (Exception exception) when (IsObservationFailure(exception))
            {
                diagnostics.Add(new WeixinProcessDiagnostic(process.ProcessId, "weixin_process_inaccessible",
                    $"无法访问微信进程 {process.ProcessId}，已跳过该进程并继续检测。"));
            }
        }

        return CreateObservation(snapshots, diagnostics, candidates.Length, true);
    }

    private static WeixinObservationResult CreateObservation(
        IReadOnlyList<WeixinProcessSnapshot> snapshots, IReadOnlyList<WeixinProcessDiagnostic> diagnostics,
        int candidateProcessCount, bool enumerationSucceeded)
    {
        var observationFailure = diagnostics.FirstOrDefault(diagnostic => diagnostic.Code is
            "weixin_observation_in_progress" or
            "weixin_installation_snapshot_unavailable" or
            "weixin_process_enumeration_failed" or
            "weixin_process_inaccessible" or
            "weixin_module_backing_unavailable");
        if (observationFailure is not null)
            return new WeixinObservationResult(WeixinObservationStatus.ObservationFailed, snapshots,
                observationFailure.Code, observationFailure.MessageZh, diagnostics, candidateProcessCount,
                enumerationSucceeded);

        var identityMismatch = diagnostics.FirstOrDefault(diagnostic => diagnostic.Code is
            "weixin_installation_hash_changed" or
            "weixin_module_identity_mismatch" or
            "weixin_module_hash_mismatch" or
            "weixin_module_snapshot_changed");
        if (identityMismatch is not null)
            return new WeixinObservationResult(WeixinObservationStatus.IdentityMismatch, snapshots,
                identityMismatch.Code, identityMismatch.MessageZh, diagnostics, candidateProcessCount,
                enumerationSucceeded);

        return snapshots.Count == 0
            ? new WeixinObservationResult(WeixinObservationStatus.NoProcess, snapshots,
                "weixin_no_process", "未发现已验证安装路径的微信进程。", diagnostics,
                candidateProcessCount, enumerationSucceeded)
            : new WeixinObservationResult(WeixinObservationStatus.Verified, snapshots,
                "weixin_process_verified", "微信进程身份已验证。", diagnostics,
                candidateProcessCount, enumerationSucceeded);
    }

    private int? ReadForegroundProcessId(List<WeixinProcessDiagnostic> diagnostics)
    {
        try
        {
            return _api.GetForegroundProcessId();
        }
        catch (Exception exception) when (IsObservationFailure(exception))
        {
            diagnostics.Add(new WeixinProcessDiagnostic(0, "weixin_foreground_unavailable",
                "无法识别当前前台进程，微信进程快照仍继续。"));
            return null;
        }
    }

    private void ValidateSelection()
    {
        if (!_profileSelection.Accepted || !IsSha256(_profileSelection.DllSha256))
            throw new InvalidOperationException("微信版本尚未通过配置校验，不能生成进程快照。");
        if (string.IsNullOrWhiteSpace(_installation.ExecutablePath) || string.IsNullOrWhiteSpace(_installation.DllPath))
            throw new InvalidOperationException("微信安装路径无效，不能生成进程快照。");
    }

    internal static bool CanonicalPathsEqual(string left, string right) =>
        string.Equals(NormalizeCanonicalPath(left), NormalizeCanonicalPath(right),
            StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeCanonicalPath(string path)
    {
        var normalized = path.Trim();
        if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            normalized = @"\\" + normalized[8..];
        else if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];
        return normalized.Replace('/', '\\').TrimEnd('\\');
    }

    private static bool IsWeixinExecutableName(string name) =>
        ExecutableNames.Contains(Path.GetFileName(name), StringComparer.OrdinalIgnoreCase);

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsObservationFailure(Exception exception) =>
        exception is not OperationCanceledException &&
        (exception is WindowsObservationException or Win32Exception or UnauthorizedAccessException or IOException or
            InvalidOperationException or ArgumentException or NotSupportedException);
}
