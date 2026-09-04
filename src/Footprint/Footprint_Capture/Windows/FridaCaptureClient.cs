using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Footprint.Core;
using Footprint.Core.Capture;

namespace Footprint.Capture.Windows;

public interface IFridaCaptureClient : IFridaSpawnPort
{
    Task<FridaCaptureSession> AttachAsync(int processId, string profilePath,
        string outputDirectory, CancellationToken cancellationToken);
    Task<DecompressionResult> DecompressAsync(FridaCaptureSession session,
        IReadOnlyList<DatabaseBinding> bindings, string outputDirectory,
        CancellationToken cancellationToken);
}

public sealed record DecompressionResult(bool IsSuccessful, string Code, string MessageZh, string OutputDirectory);

public sealed class FridaCaptureException : Exception
{
    public FridaCaptureException(string code, string messageZh, Exception? innerException = null)
        : base(messageZh, innerException)
    {
        Code = code;
        MessageZh = messageZh;
    }

    public string Code { get; }
    public string MessageZh { get; }
}

internal interface IFridaHostProcessFactory
{
    IFridaHostProcess Start(ProcessStartInfo startInfo);
}

internal interface IFridaHostProcess : IAsyncDisposable
{
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void KillTree();
    void CloseOutputStreams();
}

public sealed class FridaCaptureClient : IFridaCaptureClient
{
    private static readonly string[] CaptureFiles =
    [
        "capture-events.jsonl", "connections.jsonl", "capture-summary.json", "capture-terminal.json",
        "agent-diagnostics.jsonl", "runtime-probes.jsonl", "runtime-file-opens.jsonl",
        "image-protocol-diagnostics.json", "image-protocol-evidence.json", "image-protocol-artifacts.jsonl"
    ];
    private static readonly string[] CaptureDirectories = ["keys", "runtime-export", "image-protocol-artifacts"];
    private readonly string _pythonExecutable;
    private readonly string _hostScript;
    private readonly string _agentScript;
    private readonly IFridaHostProcessFactory _processFactory;
    private readonly TimeSpan _startupTimeout;
    private readonly int _outputLimit;

    public FridaCaptureClient(string pythonExecutable, string hostScript, string agentScript)
        : this(pythonExecutable, hostScript, agentScript, new SystemFridaHostProcessFactory(),
            TimeSpan.FromSeconds(60), 1024 * 1024)
    {
    }

    internal FridaCaptureClient(string pythonExecutable, string hostScript, string agentScript,
        IFridaHostProcessFactory processFactory, TimeSpan startupTimeout, int outputLimit)
    {
        _pythonExecutable = RequireFile(pythonExecutable, "frida_python_missing", "Frida Python 运行时不存在。");
        _hostScript = RequireFile(hostScript, "frida_host_missing", "Frida Host 脚本不存在。");
        _agentScript = RequireFile(agentScript, "frida_agent_missing", "Frida Agent 脚本不存在。");
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _startupTimeout = startupTimeout > TimeSpan.Zero ? startupTimeout : throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        _outputLimit = outputLimit > 0 ? outputLimit : throw new ArgumentOutOfRangeException(nameof(outputLimit));
    }

    public Task<FridaCaptureSession> AttachAsync(int processId, string profilePath,
        string outputDirectory, CancellationToken cancellationToken)
    {
        if (processId <= 0) throw Failure("frida_pid_invalid", "微信进程标识无效。");
        return StartAsync("attach", "--pid", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profilePath, outputDirectory, cancellationToken);
    }

    public async Task<IFridaCaptureSession> SpawnAsync(string executablePath, string profilePath,
        string outputDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw Failure("frida_executable_invalid", "微信可执行文件路径无效。");
        return await StartAsync("spawn", "--executable", executablePath, profilePath, outputDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DecompressionResult> DecompressAsync(FridaCaptureSession session,
        IReadOnlyList<DatabaseBinding> bindings, string outputDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(bindings);
        cancellationToken.ThrowIfCancellationRequested();
        if (bindings.Count == 0)
            return new DecompressionResult(false, "frida_decompression_binding_missing",
                "缺少已验证的数据库绑定，已停止解压。", outputDirectory);
        if (bindings.Any(binding => !string.Equals(binding.ProfileSha256, session.ProfileSha256,
                StringComparison.OrdinalIgnoreCase)))
            return new DecompressionResult(false, "frida_decompression_profile_mismatch",
                "数据库绑定与 Frida 采集配置不一致，已停止解压。", outputDirectory);
        if (!PathsEqual(outputDirectory, session.OutputDirectory))
            return new DecompressionResult(false, "frida_decompression_output_mismatch",
                "Frida 解压输出目录与采集会话不一致，已停止发布。", outputDirectory);
        if (!await session.WaitForKeyCaptureAsync(cancellationToken).ConfigureAwait(false))
            return new DecompressionResult(false, "frida_decompression_failed",
                "Frida 解压未完成，已停止发布。", outputDirectory);

        var runtimeExport = Path.Combine(session.OutputDirectory, "runtime-export");
        var summaryPath = Path.Combine(runtimeExport, "decompression-summary.json");
        if (!File.Exists(summaryPath) || new FileInfo(summaryPath).Length is <= 0 or >
            DecompressionSummaryValidator.MaximumBytes)
            return new DecompressionResult(false, "frida_decompression_missing",
                "Frida 解压结果缺失，已停止发布。", outputDirectory);
        var summaryBytes = await File.ReadAllBytesAsync(summaryPath, cancellationToken).ConfigureAwait(false);
        var expected = bindings.Select(binding => new DecompressionSummaryBinding(binding.Path, binding.Tag,
            binding.KeySha256, binding.KeyLength)).ToArray();
        var validation = DecompressionSummaryValidator.Validate(summaryBytes, expected);
        if (!validation.IsValid)
            return new DecompressionResult(false, DecompressionSummaryValidator.StageFailureCode(validation.Code),
                validation.MessageZh, outputDirectory);
        return new DecompressionResult(true,
            validation.Code == DecompressionSummaryValidator.SummaryValidWithOptionalFailuresCode
                ? validation.Code
                : "frida_decompression_ready",
            validation.Code == DecompressionSummaryValidator.SummaryValidWithOptionalFailuresCode
                ? validation.MessageZh
                : "Frida 解压结果已就绪。", runtimeExport);
    }

    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task<FridaCaptureSession> StartAsync(string mode, string selector, string selectorValue,
        string profilePath, string outputDirectory, CancellationToken cancellationToken)
    {
        profilePath = RequireFile(profilePath, "frida_profile_missing", "微信版本配置不存在。");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw Failure("frida_output_invalid", "Frida 采集输出目录无效。");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            ResetCaptureArtifacts(outputDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure("frida_output_reset_failed", "Frida 采集输出无法重置。", exception);
        }
        var profileSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(profilePath,
            cancellationToken).ConfigureAwait(false))).ToLowerInvariant();
        var info = CreateStartInfo(mode, selector, selectorValue, profilePath, outputDirectory);
        IFridaHostProcess process;
        try
        {
            process = _processFactory.Start(info);
        }
        catch (Exception exception)
        {
            throw Failure("frida_start_failed", "Frida 采集进程启动失败。", exception);
        }

        var session = new FridaCaptureSession(process, profileSha, outputDirectory, _startupTimeout, _outputLimit);
        try
        {
            await session.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ResetCaptureArtifacts(string outputDirectory)
    {
        foreach (var relative in CaptureFiles)
            File.Delete(Path.Combine(outputDirectory, relative));
        foreach (var relative in CaptureDirectories)
        {
            var path = Path.Combine(outputDirectory, relative);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private ProcessStartInfo CreateStartInfo(string mode, string selector, string selectorValue,
        string profilePath, string outputDirectory)
    {
        var info = new ProcessStartInfo(_pythonExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_hostScript) ?? Environment.CurrentDirectory
        };
        foreach (var argument in new[]
                 {
                     _hostScript, "--mode", mode, selector, selectorValue, "--profile", profilePath,
                     "--agent", _agentScript, "--output", outputDirectory
                 })
            info.ArgumentList.Add(argument);
        return info;
    }

    private static string RequireFile(string path, string code, string messageZh)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw Failure(code, messageZh);
        return Path.GetFullPath(path);
    }

    private static FridaCaptureException Failure(string code, string messageZh, Exception? innerException = null) =>
        new(code, messageZh, innerException);
}

public sealed class FridaCaptureSession : IFridaCaptureSession
{
    private const string HostSchema = "Footprint_FridaHost_v1";
    private readonly IFridaHostProcess _process;
    private readonly TimeSpan _startupTimeout;
    private readonly int _outputLimit;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _capture = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private string? _sessionId;
    private Exception? _failure;
    private int _captureCompleteReported;
    private int _disposed;

    internal FridaCaptureSession(IFridaHostProcess process, string profileSha256, string outputDirectory,
        TimeSpan startupTimeout, int outputLimit)
    {
        _process = process;
        ProfileSha256 = profileSha256;
        OutputDirectory = outputDirectory;
        _startupTimeout = startupTimeout;
        _outputLimit = outputLimit;
        _stdoutPump = RunPumpAsync(() => PumpStdoutAsync(_lifetime.Token));
        _stderrPump = RunPumpAsync(() => DrainBoundedAsync(_process.StandardError, _lifetime.Token));
        _ = ObserveExitAsync();
    }

    public string SessionId => Volatile.Read(ref _sessionId) ?? string.Empty;
    public string ProfileSha256 { get; }
    public string OutputDirectory { get; }

    internal async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_startupTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            _ = await _ready.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            KillTreeQuietly();
            throw new FridaCaptureException("frida_startup_timeout", "Frida 采集进程启动超时。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillTreeQuietly();
            throw;
        }
    }

    public async Task<bool> WaitForKeyCaptureAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _capture.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillTreeQuietly();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        KillTreeQuietly();
        StopPumpsQuietly();
        try { await Task.WhenAll(_stdoutPump, _stderrPump).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) when (Volatile.Read(ref _failure) is not null) { }
        await _process.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task RunPumpAsync(Func<Task> pump)
    {
        try
        {
            await pump().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (IOException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SignalFailure(exception);
            throw;
        }
    }

    private async Task PumpStdoutAsync(CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var total = 0;
        var lineBytes = 0;
        while (true)
        {
            var read = await _process.StandardOutput.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (line.Length > 0) HandleHostLine(TrimCarriageReturn(line.ToString()));
                return;
            }

            var segmentStart = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != '\n') continue;
                AppendSegment(buffer.AsSpan(segmentStart, index - segmentStart), line, ref lineBytes, ref total);
                total = checked(total + 1);
                ThrowIfOutputLimitExceeded(total, lineBytes);
                HandleHostLine(TrimCarriageReturn(line.ToString()));
                line.Clear();
                lineBytes = 0;
                segmentStart = index + 1;
            }
            AppendSegment(buffer.AsSpan(segmentStart, read - segmentStart), line, ref lineBytes, ref total);
        }
    }

    private void AppendSegment(ReadOnlySpan<char> segment, StringBuilder line, ref int lineBytes, ref int total)
    {
        if (segment.Length == 0) return;
        var byteCount = Encoding.UTF8.GetByteCount(segment);
        lineBytes = checked(lineBytes + byteCount);
        total = checked(total + byteCount);
        ThrowIfOutputLimitExceeded(total, lineBytes);
        line.Append(segment);
    }

    private void ThrowIfOutputLimitExceeded(int total, int lineBytes)
    {
        if (total > _outputLimit || lineBytes > _outputLimit)
            throw ProtocolFailure("frida_output_limit", "Frida 采集输出超过限制。");
    }

    private void HandleHostLine(string line)
    {
        var envelope = ParseHostLine(line);
        if (string.Equals(envelope.Type, "agent_ready", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(envelope.SessionId)) throw ProtocolFailure();
            var sessionId = BindSessionId(envelope.SessionId);
            _ready.TrySetResult(sessionId);
        }
        else if (string.Equals(envelope.Type, "capture_complete", StringComparison.Ordinal))
        {
            if (envelope.ConnectionCount is null or <= 0)
                throw new FridaCaptureException("frida_capture_binding_missing",
                    "Frida 采集未形成有效数据库连接。");
            Volatile.Write(ref _captureCompleteReported, 1);
        }
    }

    private static string TrimCarriageReturn(string line) => line.EndsWith('\r') ? line[..^1] : line;

    private async Task DrainBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var total = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            total = checked(total + Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read)));
            if (total > _outputLimit)
                throw ProtocolFailure("frida_output_limit", "Frida 采集输出超过限制。");
        }
    }

    private HostEnvelope ParseHostLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw ProtocolFailure();
            var allowed = new HashSet<string>(StringComparer.Ordinal)
                { "schema", "type", "profile_sha256", "session_id", "message", "connection_count", "boundary_counts" };
            if (document.RootElement.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
                throw ProtocolFailure();
            var envelope = JsonSerializer.Deserialize<HostEnvelope>(line, TargetProfile.JsonOptions) ?? throw ProtocolFailure();
            if (!string.Equals(envelope.Schema, HostSchema, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(envelope.Type)) throw ProtocolFailure();
            if (!string.Equals(envelope.ProfileSha256, ProfileSha256, StringComparison.OrdinalIgnoreCase))
                throw new FridaCaptureException("frida_profile_sha_mismatch", "Frida 采集配置 SHA-256 不匹配。");
            return envelope;
        }
        catch (FridaCaptureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw ProtocolFailure();
        }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
            var exitCode = _process.ExitCode;
            StopPumpsQuietly();
            try { await Task.WhenAll(_stdoutPump, _stderrPump).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
            catch (IOException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception) when (Volatile.Read(ref _failure) is not null) { }
            if (Volatile.Read(ref _failure) is not null) return;

            var captured = exitCode == 0 && TryValidateCaptureTerminal();
            if (!_ready.Task.IsCompleted)
                _ready.TrySetException(ProtocolFailure("frida_startup_failed", "Frida 采集进程未完成启动。"));
            _capture.TrySetResult(captured);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SignalFailure(exception);
        }
    }

    private bool TryValidateCaptureTerminal()
    {
        try
        {
            var path = Path.Combine(OutputDirectory, "capture-terminal.json");
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 4096) return false;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var allowed = new HashSet<string>(StringComparer.Ordinal)
                { "schema", "profile_sha256", "session_id", "connection_count" };
            if (document.RootElement.EnumerateObject().Any(property => !allowed.Contains(property.Name))) return false;
            var terminal = document.RootElement.Deserialize<CaptureTerminal>(TargetProfile.JsonOptions);
            if (terminal is null ||
                !string.Equals(terminal.Schema, "Footprint_FridaCaptureTerminal_v1", StringComparison.Ordinal) ||
                !string.Equals(terminal.ProfileSha256, ProfileSha256, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(terminal.SessionId) || terminal.ConnectionCount <= 0) return false;
            var sessionId = BindSessionId(terminal.SessionId);
            _ready.TrySetResult(sessionId);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private string BindSessionId(string value)
    {
        var existing = Interlocked.CompareExchange(ref _sessionId, value, null);
        if (existing is null || string.Equals(existing, value, StringComparison.Ordinal)) return value;
        throw ProtocolFailure();
    }

    private void SignalFailure(Exception exception)
    {
        var safe = exception as FridaCaptureException ?? ProtocolFailure();
        if (Interlocked.CompareExchange(ref _failure, safe, null) is not null) return;
        _ready.TrySetException(safe);
        _capture.TrySetException(safe);
        KillTreeQuietly();
        StopPumpsQuietly();
    }

    private void KillTreeQuietly()
    {
        try { _process.KillTree(); } catch { }
    }

    private void StopPumpsQuietly()
    {
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        try { _process.CloseOutputStreams(); } catch { }
    }

    private static FridaCaptureException ProtocolFailure(string code = "frida_protocol_invalid",
        string messageZh = "Frida 采集协议无效。") => new(code, messageZh);

    private sealed class HostEnvelope
    {
        [System.Text.Json.Serialization.JsonPropertyName("schema")] public string? Schema { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("type")] public string? Type { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("profile_sha256")] public string? ProfileSha256 { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("session_id")] public string? SessionId { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("connection_count")] public int? ConnectionCount { get; init; }
    }

    private sealed class CaptureTerminal
    {
        [System.Text.Json.Serialization.JsonPropertyName("schema")] public string? Schema { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("profile_sha256")] public string? ProfileSha256 { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("session_id")] public string? SessionId { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("connection_count")] public int ConnectionCount { get; init; }
    }
}

internal sealed class SystemFridaHostProcessFactory : IFridaHostProcessFactory
{
    public IFridaHostProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("process_start_failed");
        }
        return new SystemFridaHostProcess(process);
    }
}

internal sealed class SystemFridaHostProcess(Process process) : IFridaHostProcess
{
    public StreamReader StandardOutput => process.StandardOutput;
    public StreamReader StandardError => process.StandardError;
    public int ExitCode => process.ExitCode;
    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
    public void KillTree()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
    public void CloseOutputStreams()
    {
        process.StandardOutput.Dispose();
        process.StandardError.Dispose();
    }
    public ValueTask DisposeAsync()
    {
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
