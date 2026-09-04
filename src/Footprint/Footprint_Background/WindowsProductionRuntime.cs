using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Footprint.Core.Capture;
using Footprint.Core.Runtime;
using Footprint.Core.State;
using Footprint.Core.Transfer;

namespace Footprint.Background;

public static class ProductionRunIdFactory
{
    public static string Create() => $"Footprint_Run_{Guid.NewGuid():N}";
}

public sealed class WindowsDeviceIdentity(string path)
{
    private readonly string _path = Path.GetFullPath(path);

    public string GetOrCreate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path)) return Validate(File.ReadAllText(_path).Trim());
        var value = "windows-" + Guid.NewGuid().ToString("N");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".partial";
        File.WriteAllText(temporary, value, new UTF8Encoding(false));
        try { File.Move(temporary, _path); }
        catch (IOException) when (File.Exists(_path)) { File.Delete(temporary); }
        return Validate(File.ReadAllText(_path).Trim());
    }

    private static string Validate(string value)
    {
        if (value.Length != 40 || !value.StartsWith("windows-", StringComparison.Ordinal) ||
            value[8..].Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Windows DeviceId state is invalid.");
        return value;
    }
}

public sealed record WindowsProductionConfiguration(
    string ServerBaseUri,
    string UploadBearerToken,
    string CommandBearerToken,
    string ReceiptPublicKeyPath,
    string CommandPublicKeyPath,
    string CaptureRuntimeAssemblyPath,
    string CaptureExecutablePath,
    string TransferExecutablePath,
    TimeSpan CommandPollInterval)
{
    public static WindowsProductionConfiguration FromEnvironment()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return new WindowsProductionConfiguration(
            Required("FOOTPRINT_SERVER_BASE_URI"),
            Required("FOOTPRINT_UPLOAD_TOKEN"),
            Required("FOOTPRINT_SOURCE_COMMAND_TOKEN"),
            Path.GetFullPath(Required("FOOTPRINT_RECEIPT_PUBLIC_KEY_PATH")),
            Path.GetFullPath(Required("FOOTPRINT_COMMAND_PUBLIC_KEY_PATH")),
            Path.Combine(baseDirectory, "Footprint_CaptureRuntime.dll"),
            Path.Combine(baseDirectory, "Footprint_Capture.exe"),
            Path.Combine(baseDirectory, "Footprint_Transfer.exe"),
            TimeSpan.FromSeconds(15));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required.");
}

public interface IWindowsWorkerProcessRunner
{
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, bool keepParentPipeOpen,
        CancellationToken cancellationToken);
}

public sealed class WindowsWorkerProcessRunner : IWindowsWorkerProcessRunner
{
    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, bool keepParentPipeOpen,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        NamedPipeServerStream? parentPipe = null;
        if (keepParentPipeOpen)
        {
            var pipeName = ValueAfter(arguments, "--pipe");
            parentPipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("工作进程启动失败。");
            if (parentPipe is not null)
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await parentPipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            if (parentPipe is not null) await parentPipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string ValueAfter(IReadOnlyList<string> values, string option)
    {
        for (var index = 0; index + 1 < values.Count; index++)
            if (string.Equals(values[index], option, StringComparison.Ordinal)) return values[index + 1];
        throw new ArgumentException($"缺少 {option} 参数。", nameof(values));
    }
}

public interface IWindowsRunCoordinator
{
    Task<string> StartNewRunAsync(string? remoteRestartCommandId, CancellationToken cancellationToken);
    Task RetryUploadAsync(string runId, CancellationToken cancellationToken);
}

public sealed class WindowsRunCoordinator : IWindowsRunCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly FootprintPaths _paths;
    private readonly WindowsProductionConfiguration _configuration;
    private readonly string _deviceId;
    private readonly IWindowsWorkerProcessRunner _runner;
    private readonly Func<string> _createRunId;
    private readonly KeyExtractionAuditLog _audit;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsRunCoordinator(FootprintPaths paths, WindowsProductionConfiguration configuration,
        string deviceId, IWindowsWorkerProcessRunner? runner = null, Func<string>? createRunId = null,
        KeyExtractionAuditLog? audit = null)
    {
        _paths = paths;
        _configuration = configuration;
        _deviceId = deviceId;
        _runner = runner ?? new WindowsWorkerProcessRunner();
        _createRunId = createRunId ?? ProductionRunIdFactory.Create;
        _audit = audit ?? new KeyExtractionAuditLog(Path.Combine(paths.LogsDirectory, "key-extraction.log"));
    }

    public async Task<string> StartNewRunAsync(string? remoteRestartCommandId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            var runId = _createRunId();
            _audit.Write("后台", runId, "密钥提取链路已启动", "正在创建采集运行");
            _ = CaptureWorkspace.Create(_paths.StateDirectory, runId);
            var captureArguments = new List<string>
            {
                "--pipe", "Footprint_Capture_" + Guid.NewGuid().ToString("N"),
                "--run-id", runId,
                "--state-root", _paths.StateDirectory,
                "--runtime-assembly", _configuration.CaptureRuntimeAssemblyPath,
                "--device-id", _deviceId,
                "--log-file", _audit.Path,
                "--event-outbox", Path.Combine(_paths.StateDirectory, "Footprint_EventOutbox")
            };
            if (!string.IsNullOrWhiteSpace(remoteRestartCommandId))
            {
                captureArguments.Add("--remote-restart-command-id");
                captureArguments.Add(remoteRestartCommandId);
            }
            _audit.Write("后台", runId, "正在启动采集工作进程", "等待密钥提取阶段结果");
            var captureExit = await _runner.RunAsync(_configuration.CaptureExecutablePath, captureArguments,
                keepParentPipeOpen: false, cancellationToken).ConfigureAwait(false);
            _audit.Write("后台", runId, "采集工作进程已退出", CaptureExitResult(captureExit));
            if (captureExit != 0)
            {
                _audit.Write("后台", runId, "密钥提取链路已停止", CaptureExitResult(captureExit));
                throw new InvalidOperationException($"采集工作进程失败：{captureExit}。");
            }
            _audit.Write("后台", runId, "密钥提取链路已完成", "正在启动上传");
            await RunTransferAsync(runId, cancellationToken).ConfigureAwait(false);
            return runId;
        }
        finally { _gate.Release(); }
    }

    public async Task RetryUploadAsync(string runId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { EnsureAvailable(); await RunTransferAsync(runId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task RunTransferAsync(string runId, CancellationToken cancellationToken)
    {
        var canonical = RunPackageContract.CanonicalRunId(runId);
        var workspaceRunId = "Footprint_Run_" + canonical;
        var workspace = CaptureWorkspace.Create(_paths.StateDirectory, workspaceRunId);
        var credentialRoot = Path.Combine(_paths.StateDirectory, "Footprint_CredentialCache");
        var confirmedOutboxRoot = Path.Combine(_paths.StateDirectory, "Footprint_ConfirmedOutbox");
        var temporaryRoot = Path.Combine(_paths.StateDirectory, "Footprint_Temporary");
        var uploadState = Path.Combine(_paths.StateDirectory, "Footprint_UploadState");
        var credentialDirectory = Path.Combine(credentialRoot, workspaceRunId);
        Directory.CreateDirectory(credentialDirectory);
        var tokenPath = Path.Combine(credentialDirectory, "upload.token");
        await File.WriteAllTextAsync(tokenPath, _configuration.UploadBearerToken, new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_paths.QueueDirectory);
        var configPath = Path.Combine(_paths.QueueDirectory, workspaceRunId + ".json");
        var document = new
        {
            schema = "footprint.transfer-worker.v1",
            deviceId = _deviceId,
            captureRunRoot = workspace.RootPath,
            serverBaseUri = _configuration.ServerBaseUri,
            bearerTokenPath = tokenPath,
            receiptPublicKeyPath = _configuration.ReceiptPublicKeyPath,
            stateDirectory = uploadState,
            temporaryRoot,
            queueRoot = _paths.QueueDirectory,
            credentialCacheRoot = credentialRoot,
            confirmedOutboxRoot,
            stallTimeoutSeconds = 300
        };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(document, JsonOptions),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var pipeName = "Footprint_Transfer_" + Guid.NewGuid().ToString("N");
        var exit = await _runner.RunAsync(_configuration.TransferExecutablePath,
            ["--pipe", pipeName, "--config", configPath], keepParentPipeOpen: true, cancellationToken)
            .ConfigureAwait(false);
        if (exit != 0) throw new InvalidOperationException($"传输工作进程失败：{exit}。");
    }

    private void EnsureAvailable()
    {
        if (File.Exists(_paths.MaintenanceLockPath)) throw new InvalidOperationException("维护期间不启动新运行。");
    }

    private static string CaptureExitResult(int exitCode) => exitCode switch
    {
        0 => "成功；退出码=0",
        65 => "数据库解压失败；退出码=65",
        70 => "采集运行失败；退出码=70",
        75 => "正在等待微信重启条件；退出码=75",
        78 => "微信版本配置不受支持；退出码=78",
        130 => "采集已取消；退出码=130",
        _ => $"采集进程异常结束；退出码={exitCode}"
    };
}

public sealed record RemoteBackgroundCommand(string CommandId, string DeviceId, string CommandType,
    string ParametersJson, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc, string SignatureBase64);

public sealed record BackgroundCommandResult(string ResultCode, string MessageZh);

public sealed class BackgroundCommandDispatcher(string deviceId, IWindowsRunCoordinator runs,
    IFootprintStateStore store)
{
    private bool _paused;

    public async Task<BackgroundCommandResult> ExecuteAsync(RemoteBackgroundCommand command,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(command.DeviceId, deviceId, StringComparison.Ordinal))
            return new("rejected", "命令设备不匹配。");
        using var parameters = JsonDocument.Parse(command.ParametersJson);
        if (parameters.RootElement.ValueKind != JsonValueKind.Object) return new("rejected", "命令参数无效。");
        switch (command.CommandType)
        {
            case "Footprint_SetRestartPolicy":
                if (!TrySingleString(parameters.RootElement, "restartPolicy", out var policyText) ||
                    !RestartPolicyParser.TryParse(policyText, out var policy)) return new("rejected", "重启策略无效。");
                await store.SetRestartPolicyAsync(deviceId, policy, cancellationToken).ConfigureAwait(false);
                return Completed();
            case "Footprint_RestartWeixinOnce":
                if (parameters.RootElement.EnumerateObject().Any()) return Rejected();
                await runs.StartNewRunAsync(command.CommandId, cancellationToken).ConfigureAwait(false);
                return Completed();
            case "Footprint_StartCapture":
                if (parameters.RootElement.EnumerateObject().Any()) return Rejected();
                if (_paused) return new("paused", "当前已暂停新运行。");
                await runs.StartNewRunAsync(null, cancellationToken).ConfigureAwait(false);
                return Completed();
            case "Footprint_PauseNewRuns":
                if (parameters.RootElement.EnumerateObject().Any()) return Rejected();
                _paused = true;
                return Completed();
            case "Footprint_ResumeNewRuns":
                if (parameters.RootElement.EnumerateObject().Any()) return Rejected();
                _paused = false;
                return Completed();
            case "Footprint_RetryUpload":
                if (!TrySingleString(parameters.RootElement, "runId", out var retryRunId)) return Rejected();
                await runs.RetryUploadAsync(RunPackageContract.CanonicalRunId(retryRunId), cancellationToken)
                    .ConfigureAwait(false);
                return Completed();
            default:
                return new("rejected", "命令类型不受支持。");
        }
    }

    private static bool TrySingleString(JsonElement value, string name, out string text)
    {
        text = string.Empty;
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != 1 || !string.Equals(properties[0].Name, name, StringComparison.Ordinal) ||
            properties[0].Value.ValueKind != JsonValueKind.String) return false;
        text = properties[0].Value.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(text);
    }

    private static BackgroundCommandResult Completed() => new("completed", "命令已执行。");
    private static BackgroundCommandResult Rejected() => new("rejected", "命令参数无效。");
}

public sealed class RemoteCommandPoller : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly HttpClient _http;
    private readonly Uri _server;
    private readonly string _deviceId;
    private readonly string _token;
    private readonly BackgroundCommandDispatcher _dispatcher;
    private readonly ECDsa _key;
    private readonly TimeSpan _interval;
    private readonly RemoteCommandExecutionStore _results;
    private readonly KeyExtractionAuditLog? _audit;

    public RemoteCommandPoller(HttpClient http, WindowsProductionConfiguration configuration, string deviceId,
        BackgroundCommandDispatcher dispatcher, RemoteCommandExecutionStore? results = null,
        KeyExtractionAuditLog? audit = null)
    {
        _http = http;
        _server = new Uri(configuration.ServerBaseUri, UriKind.Absolute);
        if (!_server.IsAbsoluteUri || _server.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("服务器地址必须为 HTTPS。");
        _deviceId = deviceId;
        _token = configuration.CommandBearerToken;
        _dispatcher = dispatcher;
        _interval = configuration.CommandPollInterval;
        _results = results ?? new RemoteCommandExecutionStore(Path.Combine(
            Path.GetDirectoryName(configuration.CommandPublicKeyPath)!, "Footprint_CommandResults"));
        _audit = audit;
        _key = ECDsa.Create();
        _key.ImportFromPem(File.ReadAllText(configuration.CommandPublicKeyPath));
        if (_key.KeySize != 256) throw new InvalidDataException("命令公钥必须为 ECDSA P-256。");
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await PollOnceAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                _audit?.Write("后台", "background-command-poller", "远程命令轮询异常", "错误已记录，将继续轮询");
            }
            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        using var request = Create(HttpMethod.Get,
            $"api/footprint/commands/next?deviceId={Uri.EscapeDataString(_deviceId)}");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return;
        response.EnsureSuccessStatusCode();
        var command = await response.Content.ReadFromJsonAsync<RemoteBackgroundCommand>(JsonOptions,
                          cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("远程命令为空。");
        _audit?.Write("后台", command.CommandId, "远程命令已领取", $"命令={command.CommandType}");
        var verified = Verify(command);
        var claim = verified ? _results.Claim(command.CommandId) : null;
        var result = verified == false
            ? new BackgroundCommandResult("rejected", "命令签名或有效期无效。")
            : claim!.ShouldExecute
                ? await ExecuteSafelyAsync(command, cancellationToken).ConfigureAwait(false)
                : claim.Result!;
        if (verified && claim!.ShouldExecute) _results.Complete(command.CommandId, result);
        await AcknowledgeAsync(command, result, cancellationToken).ConfigureAwait(false);
        _audit?.Write("后台", command.CommandId, "远程命令已确认",
            $"状态={ResultStatusZh(result.ResultCode)}；结果={result.MessageZh}");
    }

    private async Task<BackgroundCommandResult> ExecuteSafelyAsync(RemoteBackgroundCommand command,
        CancellationToken cancellationToken)
    {
        try { return await _dispatcher.ExecuteAsync(command, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _audit?.Write("后台", command.CommandId, "远程命令执行失败", "错误已记录。");
            return new BackgroundCommandResult("failed", "命令执行失败，错误已记录。");
        }
    }

    private async Task AcknowledgeAsync(RemoteBackgroundCommand command, BackgroundCommandResult result,
        CancellationToken cancellationToken)
    {
        var ack = new
        {
            commandId = command.CommandId,
            deviceId = _deviceId,
            resultCode = result.ResultCode,
            messageZh = result.MessageZh,
            acknowledgedAtUtc = DateTimeOffset.UtcNow
        };
        using var request = Create(HttpMethod.Post,
            $"api/footprint/commands/{Uri.EscapeDataString(command.CommandId)}/ack");
        request.Content = JsonContent.Create(ack);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static string ResultStatusZh(string resultCode) => resultCode switch
    {
        "completed" => "已完成",
        "paused" => "已暂停",
        "rejected" => "已拒绝",
        "failed" => "失败",
        _ => "未知"
    };

    private bool Verify(RemoteBackgroundCommand command)
    {
        if (!string.Equals(command.DeviceId, _deviceId, StringComparison.Ordinal) ||
            !IsIdentifier(command.CommandId) || !IsIdentifier(command.DeviceId) ||
            !FixedCommandTypes.Contains(command.CommandType, StringComparer.Ordinal) ||
            command.IssuedAtUtc.Offset != TimeSpan.Zero || command.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            command.ExpiresAtUtc - command.IssuedAtUtc != TimeSpan.FromMinutes(10) ||
            command.IssuedAtUtc > DateTimeOffset.UtcNow || command.ExpiresAtUtc <= DateTimeOffset.UtcNow) return false;
        try
        {
            var signature = Convert.FromBase64String(command.SignatureBase64);
            return _key.VerifyData(Canonical(command), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or CryptographicException)
        { return false; }
    }

    private HttpRequestMessage Create(HttpMethod method, string relative)
    {
        var request = new HttpRequestMessage(method, new Uri(_server, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private static byte[] Canonical(RemoteBackgroundCommand command)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });
        writer.WriteStartObject();
        writer.WriteString("commandId", command.CommandId);
        writer.WriteString("deviceId", command.DeviceId);
        writer.WriteString("commandType", command.CommandType);
        writer.WritePropertyName("parameters");
        using (var parameters = JsonDocument.Parse(command.ParametersJson)) parameters.RootElement.WriteTo(writer);
        writer.WriteString("issuedAtUtc", Utc(command.IssuedAtUtc));
        writer.WriteString("expiresAtUtc", Utc(command.ExpiresAtUtc));
        writer.WriteEndObject();
        writer.Flush();
        return stream.ToArray();
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly string[] FixedCommandTypes =
    [
        "Footprint_SetRestartPolicy", "Footprint_RestartWeixinOnce", "Footprint_StartCapture",
        "Footprint_PauseNewRuns", "Footprint_ResumeNewRuns", "Footprint_RetryUpload"
    ];

    private static bool IsIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    public void Dispose() => _key.Dispose();
}

public sealed class WindowsBackgroundProductionRuntime(
    IWindowsRunCoordinator runs,
    RemoteCommandPoller commands,
    SourceEventForwarder? events = null,
    KeyExtractionAuditLog? audit = null)
{
    private readonly KeyExtractionAuditLog? _audit = audit;
    private readonly SourceEventForwarder? _events = events;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var initialRun = StartInitialRunAsync(cancellationToken);
        var polling = commands.RunAsync(cancellationToken);
        var forwarding = _events?.RunAsync(cancellationToken) ?? Task.CompletedTask;
        await Task.WhenAll(initialRun, polling, forwarding).ConfigureAwait(false);
    }

    private async Task StartInitialRunAsync(CancellationToken cancellationToken)
    {
        try { await runs.StartNewRunAsync(null, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _audit?.Write("后台", "background-initial-run", "首次密钥提取未完成", "错误已记录，可继续接收远程命令");
        }
    }
}
